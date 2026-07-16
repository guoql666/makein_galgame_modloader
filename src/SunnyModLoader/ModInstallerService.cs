using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Logging;

namespace SunnyModLoader;

internal enum ModInstallerOperation
{
    Install,
    Uninstall,
    RestoreTrash,
    RestoreBackup,
    DeleteBackup
}

internal enum ModInstallerFaultPoint
{
    AfterExistingMovedToBackup,
    AfterNewMovedToTarget,
    AfterTargetMovedToTrash,
    AfterTrashMovedToTarget,
    AfterCurrentMovedToBackup,
    AfterBackupMovedToTarget
}

internal interface IModInstallerFaultInjector
{
    void Hit(ModInstallerFaultPoint point);
}

internal sealed class ModInstallerResult
{
    internal ModInstallerOperation Operation;
    internal bool Success;
    internal bool IsUpdate;
    internal string ModId;
    internal string Version;
    internal string SourcePath;
    internal string TargetPath;
    internal string BackupPath;
    internal string Error;

    internal static ModInstallerResult Failed(ModInstallerOperation operation, string error, string sourcePath = null)
    {
        return new ModInstallerResult
        {
            Operation = operation,
            Success = false,
            SourcePath = sourcePath,
            Error = error ?? "Unknown installer error."
        };
    }
}

internal sealed class ModInboxArchiveInfo
{
    internal string FileName;
    internal string FullPath;
    internal long Length;
    internal DateTime LastWriteTimeUtc;
}

internal sealed class ModManagedPackageInfo
{
    internal string EntryName;
    internal string FullPath;
    internal string ModId;
    internal string Version;
    internal string ArchiveSha256;
    internal DateTime InstalledAtUtc;
}

[DataContract]
internal sealed class ModInstalledMetadata
{
    [DataMember] public int schemaVersion = 1;
    [DataMember] public string id;
    [DataMember] public string version;
    [DataMember] public string archiveFileName;
    [DataMember] public string archiveSha256;
    [DataMember] public string installedAtUtc;
}

internal sealed class ModInstallerService
{
    internal const long MaxArchiveBytes = 2L * 1024L * 1024L * 1024L;
    internal const long MaxManifestBytes = 256L * 1024L;
    internal const int MaxEntries = 10000;
    internal const long MaxEntryBytes = 2L * 1024L * 1024L * 1024L;
    internal const long MaxTotalBytes = 8L * 1024L * 1024L * 1024L;
    internal const int MaxDepth = 32;
    internal const long RatioGraceBytes = 1024L * 1024L;
    internal const long MaxCompressionRatio = 200L;

    private const long MaxCentralDirectoryBytes = 64L * 1024L * 1024L;
    private const int MetadataMaxBytes = 64 * 1024;
    private const string MetadataFileName = ".sunny-installed.json";
    private const string StateDirectoryName = ".sunny";
    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly PropertyInfo ZipCrc32Property = typeof(ZipArchiveEntry).GetProperty(
        "Crc32",
        BindingFlags.Instance | BindingFlags.Public);
    private static readonly HashSet<string> ReservedDeviceNames = BuildReservedDeviceNames();
    private static readonly char[] InvalidWindowsNameChars =
        "<>:\"/\\|?*".ToCharArray();

    private readonly string _modsRoot;
    private readonly string _inboxRoot;
    private readonly string _stateRoot;
    private readonly string _stagingRoot;
    private readonly string _backupRoot;
    private readonly string _trashRoot;
    private readonly string _gameBuild;
    private readonly int _loaderApiVersion;
    private readonly ManualLogSource _log;
    private readonly IModInstallerFaultInjector _faultInjector;

    internal ModInstallerService(ManualLogSource log, string gameBuild, int loaderApiVersion)
        : this(Path.Combine(Paths.GameRootPath, "Mods"), log, gameBuild, loaderApiVersion, null)
    {
    }

    internal ModInstallerService(
        string modsRoot,
        ManualLogSource log,
        string gameBuild,
        int loaderApiVersion,
        IModInstallerFaultInjector faultInjector = null)
    {
        if (string.IsNullOrWhiteSpace(modsRoot))
        {
            throw new ArgumentException("A Mods root is required.", nameof(modsRoot));
        }

        _log = log ?? throw new ArgumentNullException(nameof(log));
        _modsRoot = Path.GetFullPath(modsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _inboxRoot = Path.Combine(_modsRoot, "Inbox");
        _stateRoot = Path.Combine(_modsRoot, StateDirectoryName);
        _stagingRoot = Path.Combine(_stateRoot, "staging");
        _backupRoot = Path.Combine(_stateRoot, "backup");
        _trashRoot = Path.Combine(_stateRoot, "trash");
        _gameBuild = gameBuild ?? string.Empty;
        _loaderApiVersion = loaderApiVersion;
        _faultInjector = faultInjector;
    }

    internal IReadOnlyList<ModInboxArchiveInfo> EnumerateInbox()
    {
        try
        {
            EnsureInfrastructure();
            List<ModInboxArchiveInfo> result = new List<ModInboxArchiveInfo>();
            foreach (string path in Directory.GetFiles(_inboxRoot))
            {
                string extension = Path.GetExtension(path);
                if (!string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".sunmod", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                FileInfo file = new FileInfo(path);
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                result.Add(new ModInboxArchiveInfo
                {
                    FileName = file.Name,
                    FullPath = file.FullName,
                    Length = file.Length,
                    LastWriteTimeUtc = file.LastWriteTimeUtc
                });
            }

            return result.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            _log.LogError("Could not enumerate the Mod inbox: " + ex);
            return Array.Empty<ModInboxArchiveInfo>();
        }
    }

    internal IReadOnlyList<ModManagedPackageInfo> EnumerateManagedInstalls()
    {
        try
        {
            EnsureInfrastructure();
            List<ModManagedPackageInfo> result = new List<ModManagedPackageInfo>();
            foreach (string directory in Directory.GetDirectories(_modsRoot))
            {
                string name = Path.GetFileName(directory);
                if (string.Equals(name, "Inbox", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, StateDirectoryName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryReadManagedMetadata(directory, null, out ModInstalledMetadata metadata, out _))
                {
                    result.Add(ToPackageInfo(directory, metadata));
                }
            }

            return result.OrderBy(item => item.ModId, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            _log.LogError("Could not enumerate managed Mods: " + ex);
            return Array.Empty<ModManagedPackageInfo>();
        }
    }

    internal IReadOnlyList<ModManagedPackageInfo> EnumerateTrash()
    {
        return EnumerateManagedStorage(_trashRoot, "trash");
    }

    internal IReadOnlyList<ModManagedPackageInfo> EnumerateBackups()
    {
        return EnumerateManagedStorage(_backupRoot, "backup");
    }

    internal ModInstallerResult InstallFromInbox(string inboxFileName)
    {
        const ModInstallerOperation Operation = ModInstallerOperation.Install;
        try
        {
            EnsureInfrastructure();
            return InstallResolvedArchive(ResolveInboxArchive(inboxFileName));
        }
        catch (Exception ex)
        {
            _log.LogError("Mod inbox archive could not be opened for installation: " + ex);
            return ModInstallerResult.Failed(Operation, ex.Message, inboxFileName);
        }
    }

    internal ModInstallerResult InstallArchive(string archivePath)
    {
        const ModInstallerOperation Operation = ModInstallerOperation.Install;
        try
        {
            EnsureInfrastructure();
            return InstallResolvedArchive(ResolveArchivePath(archivePath));
        }
        catch (Exception ex)
        {
            _log.LogError("Mod archive path could not be opened for installation: " + ex);
            return ModInstallerResult.Failed(Operation, ex.Message, archivePath);
        }
    }

    private ModInstallerResult InstallResolvedArchive(string archivePath)
    {
        const ModInstallerOperation Operation = ModInstallerOperation.Install;
        string transactionRoot = null;
        try
        {
            using FileStream installLock = AcquireInstallLock();
            using FileStream archiveStream = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.SequentialScan);

            if (archiveStream.Length <= 0 || archiveStream.Length > MaxArchiveBytes)
            {
                throw new InvalidDataException(
                    "Archive size must be between 1 byte and " + MaxArchiveBytes + " bytes.");
            }

            ValidateCentralDirectoryEnvelope(archiveStream);
            string archiveSha256 = ComputeSha256(archiveStream);
            archiveStream.Position = 0;

            transactionRoot = CreateTransactionDirectory();
            string payloadRoot = Path.Combine(transactionRoot, "payload");
            CreateSafeDirectory(payloadRoot);

            ModManifest manifest;
            string manifestPath;
            using (ZipArchive archive = new ZipArchive(
                       archiveStream,
                       ZipArchiveMode.Read,
                       true,
                       StrictUtf8))
            {
                ArchivePlan plan = BuildArchivePlan(archive);
                ExtractArchive(plan, payloadRoot);
                manifestPath = Path.Combine(payloadRoot, "manifest.json");
                manifest = ReadAndValidateManifest(manifestPath, payloadRoot);
            }

            ValidateInstallId(manifest.id);
            string targetPath = GetDirectChildPath(_modsRoot, manifest.id);
            WriteInstalledMetadata(
                payloadRoot,
                manifest,
                Path.GetFileName(archivePath),
                archiveSha256);

            bool isUpdate = Directory.Exists(targetPath);
            string backupPath = null;
            if (isUpdate)
            {
                if (!TryReadManagedMetadata(targetPath, manifest.id, out _, out string managedError))
                {
                    throw new InvalidOperationException(
                        "The target Mod directory exists but is not managed by Sunny Mod Loader: " + managedError);
                }

                backupPath = CreateStorageDestination(_backupRoot, manifest.id);
            }
            else if (File.Exists(targetPath))
            {
                throw new InvalidOperationException("A file already occupies the Mod target path: " + targetPath);
            }

            CommitInstall(payloadRoot, targetPath, backupPath, isUpdate);
            ModInstallerResult result = new ModInstallerResult
            {
                Operation = Operation,
                Success = true,
                IsUpdate = isUpdate,
                ModId = manifest.id,
                Version = manifest.version,
                SourcePath = archivePath,
                TargetPath = targetPath,
                BackupPath = backupPath
            };
            _log.LogInfo(
                (isUpdate ? "Updated" : "Installed") + " Mod " + manifest.id + " from " + archivePath + ".");
            return result;
        }
        catch (Exception ex)
        {
            _log.LogError("Mod archive installation failed" +
                          (archivePath == null ? string.Empty : " for " + archivePath) + ": " + ex);
            return ModInstallerResult.Failed(Operation, ex.Message, archivePath);
        }
        finally
        {
            if (transactionRoot != null)
            {
                TryDeleteInternalTree(transactionRoot);
            }
        }
    }

    internal ModInstallerResult UninstallManaged(string modId)
    {
        const ModInstallerOperation Operation = ModInstallerOperation.Uninstall;
        try
        {
            EnsureInfrastructure();
            ValidateInstallId(modId);
            using FileStream installLock = AcquireInstallLock();
            string targetPath = GetDirectChildPath(_modsRoot, modId);
            if (!Directory.Exists(targetPath))
            {
                throw new DirectoryNotFoundException("The installed Mod directory does not exist: " + targetPath);
            }

            if (!TryReadManagedMetadata(targetPath, modId, out ModInstalledMetadata metadata, out string managedError))
            {
                throw new InvalidOperationException(
                    "Refusing to uninstall a non-managed directory: " + managedError);
            }

            string trashPath = CreateStorageDestination(_trashRoot, modId);
            Directory.Move(targetPath, trashPath);
            try
            {
                HitFault(ModInstallerFaultPoint.AfterTargetMovedToTrash);
            }
            catch
            {
                Directory.Move(trashPath, targetPath);
                throw;
            }

            return new ModInstallerResult
            {
                Operation = Operation,
                Success = true,
                ModId = metadata.id,
                Version = metadata.version,
                SourcePath = targetPath,
                TargetPath = trashPath
            };
        }
        catch (Exception ex)
        {
            _log.LogError("Managed Mod uninstall failed for " + modId + ": " + ex);
            return ModInstallerResult.Failed(Operation, ex.Message);
        }
    }

    internal ModInstallerResult RestoreTrash(string trashEntryName)
    {
        return RestoreStoredPackage(
            ModInstallerOperation.RestoreTrash,
            _trashRoot,
            trashEntryName,
            false);
    }

    internal ModInstallerResult RestoreBackup(string backupEntryName)
    {
        return RestoreStoredPackage(
            ModInstallerOperation.RestoreBackup,
            _backupRoot,
            backupEntryName,
            true);
    }

    internal ModInstallerResult DeleteBackup(string backupEntryName)
    {
        const ModInstallerOperation Operation = ModInstallerOperation.DeleteBackup;
        try
        {
            EnsureInfrastructure();
            using FileStream installLock = AcquireInstallLock();
            string backupPath = ResolveStorageEntry(_backupRoot, backupEntryName);
            if (!TryReadManagedMetadata(backupPath, null, out ModInstalledMetadata metadata, out string managedError))
            {
                throw new InvalidOperationException(
                    "Refusing to delete a non-managed backup directory: " + managedError);
            }

            ValidateTreeHasNoReparsePoints(backupPath);
            Directory.Delete(backupPath, true);
            return new ModInstallerResult
            {
                Operation = Operation,
                Success = true,
                ModId = metadata.id,
                Version = metadata.version,
                SourcePath = backupPath
            };
        }
        catch (Exception ex)
        {
            _log.LogError("Managed Mod backup cleanup failed for " + backupEntryName + ": " + ex);
            return ModInstallerResult.Failed(Operation, ex.Message);
        }
    }

    private IReadOnlyList<ModManagedPackageInfo> EnumerateManagedStorage(string root, string label)
    {
        try
        {
            EnsureInfrastructure();
            List<ModManagedPackageInfo> result = new List<ModManagedPackageInfo>();
            foreach (string directory in Directory.GetDirectories(root))
            {
                if (TryReadManagedMetadata(directory, null, out ModInstalledMetadata metadata, out _))
                {
                    result.Add(ToPackageInfo(directory, metadata));
                }
            }

            return result.OrderByDescending(item => item.InstalledAtUtc).ToList();
        }
        catch (Exception ex)
        {
            _log.LogError("Could not enumerate the managed Mod " + label + ": " + ex);
            return Array.Empty<ModManagedPackageInfo>();
        }
    }

    private ModInstallerResult RestoreStoredPackage(
        ModInstallerOperation operation,
        string storageRoot,
        string entryName,
        bool replaceCurrent)
    {
        try
        {
            EnsureInfrastructure();
            using FileStream installLock = AcquireInstallLock();
            string storedPath = ResolveStorageEntry(storageRoot, entryName);
            if (!TryReadManagedMetadata(storedPath, null, out ModInstalledMetadata metadata, out string managedError))
            {
                throw new InvalidOperationException(
                    "Refusing to restore a non-managed directory: " + managedError);
            }

            ValidateTreeHasNoReparsePoints(storedPath);
            ValidateManagedPackageForRestore(storedPath, metadata);
            ValidateInstallId(metadata.id);
            string targetPath = GetDirectChildPath(_modsRoot, metadata.id);
            string currentBackupPath = null;
            bool currentMoved = false;
            if (Directory.Exists(targetPath))
            {
                if (!replaceCurrent)
                {
                    throw new InvalidOperationException("The Mod is already installed: " + metadata.id);
                }

                if (!TryReadManagedMetadata(targetPath, metadata.id, out _, out managedError))
                {
                    throw new InvalidOperationException(
                        "Refusing to replace a non-managed target directory: " + managedError);
                }

                currentBackupPath = CreateStorageDestination(_backupRoot, metadata.id);
                Directory.Move(targetPath, currentBackupPath);
                currentMoved = true;
                try
                {
                    HitFault(ModInstallerFaultPoint.AfterCurrentMovedToBackup);
                }
                catch
                {
                    Directory.Move(currentBackupPath, targetPath);
                    throw;
                }
            }
            else if (File.Exists(targetPath))
            {
                throw new InvalidOperationException("A file occupies the restore target: " + targetPath);
            }

            try
            {
                Directory.Move(storedPath, targetPath);
                HitFault(
                    operation == ModInstallerOperation.RestoreTrash
                        ? ModInstallerFaultPoint.AfterTrashMovedToTarget
                        : ModInstallerFaultPoint.AfterBackupMovedToTarget);
            }
            catch
            {
                if (Directory.Exists(targetPath) && !Directory.Exists(storedPath))
                {
                    Directory.Move(targetPath, storedPath);
                }

                if (currentMoved && !Directory.Exists(targetPath) && Directory.Exists(currentBackupPath))
                {
                    Directory.Move(currentBackupPath, targetPath);
                }

                throw;
            }

            return new ModInstallerResult
            {
                Operation = operation,
                Success = true,
                IsUpdate = currentMoved,
                ModId = metadata.id,
                Version = metadata.version,
                SourcePath = storedPath,
                TargetPath = targetPath,
                BackupPath = currentBackupPath
            };
        }
        catch (Exception ex)
        {
            _log.LogError("Managed Mod restore failed for " + entryName + ": " + ex);
            return ModInstallerResult.Failed(operation, ex.Message);
        }
    }

    private void CommitInstall(string payloadRoot, string targetPath, string backupPath, bool isUpdate)
    {
        bool oldMoved = false;
        bool newMoved = false;
        try
        {
            if (isUpdate)
            {
                Directory.Move(targetPath, backupPath);
                oldMoved = true;
                HitFault(ModInstallerFaultPoint.AfterExistingMovedToBackup);
            }

            Directory.Move(payloadRoot, targetPath);
            newMoved = true;
            HitFault(ModInstallerFaultPoint.AfterNewMovedToTarget);
        }
        catch (Exception original)
        {
            Exception rollbackError = null;
            try
            {
                if (newMoved && Directory.Exists(targetPath) && !Directory.Exists(payloadRoot))
                {
                    Directory.Move(targetPath, payloadRoot);
                }

                if (oldMoved && Directory.Exists(backupPath) && !Directory.Exists(targetPath))
                {
                    Directory.Move(backupPath, targetPath);
                }
            }
            catch (Exception ex)
            {
                rollbackError = ex;
            }

            if (rollbackError != null)
            {
                throw new IOException(
                    "Install failed and rollback also failed. Previous package remains at " + backupPath +
                    ". Install error: " + original.Message + "; rollback error: " + rollbackError.Message,
                    original);
            }

            throw;
        }
    }

    private ArchivePlan BuildArchivePlan(ZipArchive archive)
    {
        if (archive.Entries.Count == 0)
        {
            throw new InvalidDataException("The archive is empty.");
        }

        if (archive.Entries.Count > MaxEntries)
        {
            throw new InvalidDataException("The archive contains more than " + MaxEntries + " entries.");
        }

        List<ArchiveEntryPlan> entries = new List<ArchiveEntryPlan>(archive.Entries.Count);
        HashSet<string> explicitKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> fileKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> directoryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalLength = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            ArchiveEntryPlan planned = ParseArchiveEntry(entry);
            string key = BuildCollisionKey(planned.Components, planned.Components.Count);
            if (!explicitKeys.Add(key))
            {
                throw new InvalidDataException("Archive path collision: " + entry.FullName);
            }

            ValidatePathTreeCollision(planned, fileKeys, directoryKeys);
            if (planned.IsDirectory)
            {
                if (entry.Length != 0 || entry.CompressedLength != 0)
                {
                    throw new InvalidDataException("Directory entries must not contain data: " + entry.FullName);
                }
            }
            else
            {
                if (entry.Length < 0 || entry.Length > MaxEntryBytes)
                {
                    throw new InvalidDataException("Archive entry exceeds the per-file limit: " + entry.FullName);
                }

                if (entry.CompressedLength < 0 || ExceedsCompressionRatio(entry.Length, entry.CompressedLength))
                {
                    throw new InvalidDataException("Archive entry exceeds the compression-ratio limit: " + entry.FullName);
                }

                try
                {
                    totalLength = checked(totalLength + entry.Length);
                }
                catch (OverflowException)
                {
                    throw new InvalidDataException("Archive uncompressed size overflowed the supported range.");
                }

                if (totalLength > MaxTotalBytes)
                {
                    throw new InvalidDataException("Archive exceeds the total uncompressed-size limit.");
                }
            }

            entries.Add(planned);
        }

        List<ArchiveEntryPlan> manifests = entries.Where(IsManifestEntry).ToList();
        if (manifests.Count != 1)
        {
            throw new InvalidDataException("The archive must contain exactly one manifest.json file.");
        }

        ArchiveEntryPlan manifest = manifests[0];
        if (manifest.Entry.Length > MaxManifestBytes)
        {
            throw new InvalidDataException("manifest.json exceeds " + MaxManifestBytes + " bytes.");
        }

        if (manifest.Components.Count != 1 && manifest.Components.Count != 2)
        {
            throw new InvalidDataException(
                "manifest.json must be at the archive root or inside one wrapper directory.");
        }

        string wrapperKey = null;
        if (manifest.Components.Count == 2)
        {
            wrapperKey = NormalizeCollisionComponent(manifest.Components[0]);
            foreach (ArchiveEntryPlan entry in entries)
            {
                if (!string.Equals(
                        NormalizeCollisionComponent(entry.Components[0]),
                        wrapperKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "All archive entries must be inside the same wrapper directory as manifest.json.");
                }
            }
        }

        foreach (ArchiveEntryPlan entry in entries)
        {
            int skip = wrapperKey == null ? 0 : 1;
            if (entry.Components.Count == skip)
            {
                entry.Skip = true;
                continue;
            }

            entry.RelativeComponents = entry.Components.Skip(skip).ToArray();
            if (!entry.IsDirectory && entry.RelativeComponents.Count == 1 &&
                string.Equals(
                    NormalizeCollisionComponent(entry.RelativeComponents[0]),
                    NormalizeCollisionComponent(MetadataFileName),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The archive contains the reserved loader metadata file.");
            }
        }

        return new ArchivePlan(entries, totalLength);
    }

    private static ArchiveEntryPlan ParseArchiveEntry(ZipArchiveEntry entry)
    {
        string raw = entry.FullName;
        if (string.IsNullOrEmpty(raw) || raw.IndexOf('\0') >= 0)
        {
            throw new InvalidDataException("Archive entry has an empty or NUL-containing path.");
        }

        if (raw[0] == '/' || raw[0] == '\\' || Path.IsPathRooted(raw))
        {
            throw new InvalidDataException("Absolute or UNC archive paths are not allowed: " + raw);
        }

        string slashPath = raw.Replace('\\', '/');
        bool directory = slashPath.EndsWith("/", StringComparison.Ordinal);
        string componentText = directory ? slashPath.Substring(0, slashPath.Length - 1) : slashPath;
        if (componentText.Length == 0 || componentText.IndexOf("//", StringComparison.Ordinal) >= 0)
        {
            throw new InvalidDataException("Archive paths must not contain empty components: " + raw);
        }

        string[] components = componentText.Split('/');
        if (components.Length > MaxDepth)
        {
            throw new InvalidDataException("Archive path exceeds the maximum depth of " + MaxDepth + ": " + raw);
        }

        foreach (string component in components)
        {
            ValidateWindowsPathComponent(component, "archive path " + raw);
        }

        ValidateArchiveEntryType(entry, directory);
        return new ArchiveEntryPlan(entry, components, directory);
    }

    private static void ValidateArchiveEntryType(ZipArchiveEntry entry, bool pathIsDirectory)
    {
        uint attributes = unchecked((uint)entry.ExternalAttributes);
        uint unixType = (attributes >> 16) & 0xF000u;
        const uint UnixDirectory = 0x4000u;
        const uint UnixRegularFile = 0x8000u;
        const uint DosDirectory = 0x10u;
        const uint DosReparsePoint = 0x400u;

        if ((attributes & DosReparsePoint) != 0)
        {
            throw new InvalidDataException("Reparse-point ZIP entries are not allowed: " + entry.FullName);
        }

        if (unixType != 0 && unixType != UnixDirectory && unixType != UnixRegularFile)
        {
            throw new InvalidDataException("Non-regular Unix ZIP entries are not allowed: " + entry.FullName);
        }

        if (unixType == UnixDirectory && !pathIsDirectory)
        {
            throw new InvalidDataException("ZIP directory metadata conflicts with its path: " + entry.FullName);
        }

        if (unixType == UnixRegularFile && pathIsDirectory)
        {
            throw new InvalidDataException("ZIP file metadata conflicts with its path: " + entry.FullName);
        }

        if ((attributes & DosDirectory) != 0 && !pathIsDirectory)
        {
            throw new InvalidDataException("DOS directory metadata conflicts with its path: " + entry.FullName);
        }
    }

    private static void ValidatePathTreeCollision(
        ArchiveEntryPlan entry,
        HashSet<string> fileKeys,
        HashSet<string> directoryKeys)
    {
        string fullKey = BuildCollisionKey(entry.Components, entry.Components.Count);
        for (int count = 1; count < entry.Components.Count; count++)
        {
            string parentKey = BuildCollisionKey(entry.Components, count);
            if (fileKeys.Contains(parentKey))
            {
                throw new InvalidDataException(
                    "An archive file is also used as a parent directory: " + entry.Entry.FullName);
            }

            directoryKeys.Add(parentKey);
        }

        if (entry.IsDirectory)
        {
            if (fileKeys.Contains(fullKey))
            {
                throw new InvalidDataException("Archive file/directory path collision: " + entry.Entry.FullName);
            }

            directoryKeys.Add(fullKey);
        }
        else
        {
            if (fileKeys.Contains(fullKey) || directoryKeys.Contains(fullKey))
            {
                throw new InvalidDataException("Archive file/directory path collision: " + entry.Entry.FullName);
            }

            fileKeys.Add(fullKey);
        }
    }

    private void ExtractArchive(ArchivePlan plan, string payloadRoot)
    {
        long actualTotal = 0;
        byte[] buffer = new byte[128 * 1024];
        foreach (ArchiveEntryPlan entry in plan.Entries)
        {
            if (entry.Skip)
            {
                continue;
            }

            string destination = GetExtractionPath(payloadRoot, entry.RelativeComponents);
            if (entry.IsDirectory)
            {
                CreateSafeDirectory(destination);
                continue;
            }

            string parent = Path.GetDirectoryName(destination);
            CreateSafeDirectoryTree(payloadRoot, parent);
            uint crc = Crc32.Start;
            long actualEntryLength = 0;
            using (Stream input = entry.Entry.Open())
            using (FileStream output = new FileStream(
                       destination,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       buffer.Length,
                       FileOptions.SequentialScan))
            {
                while (true)
                {
                    int read = input.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    actualEntryLength = checked(actualEntryLength + read);
                    actualTotal = checked(actualTotal + read);
                    if (actualEntryLength > MaxEntryBytes || actualEntryLength > entry.Entry.Length)
                    {
                        throw new InvalidDataException(
                            "Archive entry produced more data than declared or allowed: " + entry.Entry.FullName);
                    }

                    if (actualTotal > MaxTotalBytes)
                    {
                        throw new InvalidDataException("Archive exceeded the total extraction limit.");
                    }

                    Crc32.Update(ref crc, buffer, 0, read);
                    output.Write(buffer, 0, read);
                }

                output.Flush(true);
            }

            if (actualEntryLength != entry.Entry.Length)
            {
                throw new InvalidDataException("Archive entry length does not match its header: " + entry.Entry.FullName);
            }

            if (TryGetExpectedCrc32(entry.Entry, out uint expectedCrc) && Crc32.Finish(crc) != expectedCrc)
            {
                throw new InvalidDataException("Archive entry CRC32 check failed: " + entry.Entry.FullName);
            }

            EnsureNotReparsePoint(destination);
        }

        if (actualTotal != plan.DeclaredTotalLength)
        {
            throw new InvalidDataException("Archive extracted size does not match its declared total.");
        }
    }

    private ModManifest ReadAndValidateManifest(string manifestPath, string payloadRoot)
    {
        EnsureNotReparsePoint(manifestPath);
        FileInfo file = new FileInfo(manifestPath);
        if (!file.Exists || file.Length <= 0 || file.Length > MaxManifestBytes)
        {
            throw new InvalidDataException("manifest.json is missing, empty, or too large.");
        }

        byte[] bytes = File.ReadAllBytes(manifestPath);
        string json = StrictUtf8.GetString(bytes);
        if (!ManifestValidator.TryValidateRawJson(json, out string rawManifestError))
        {
            throw new InvalidDataException(rawManifestError);
        }

        ModManifest manifest = JsonCodec.Deserialize<ModManifest>(json);
        if (!ManifestValidator.Validate(
                manifest,
                payloadRoot,
                manifestPath,
                _gameBuild,
                _loaderApiVersion,
                _log))
        {
            throw new InvalidDataException("manifest.json failed Mod manifest validation.");
        }

        return manifest;
    }

    private void WriteInstalledMetadata(
        string payloadRoot,
        ModManifest manifest,
        string archiveFileName,
        string archiveSha256)
    {
        ModInstalledMetadata metadata = new ModInstalledMetadata
        {
            schemaVersion = 1,
            id = manifest.id,
            version = manifest.version,
            archiveFileName = archiveFileName,
            archiveSha256 = archiveSha256,
            installedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };
        byte[] bytes = new UTF8Encoding(false).GetBytes(JsonCodec.Serialize(metadata));
        string metadataPath = Path.Combine(payloadRoot, MetadataFileName);
        using (FileStream stream = new FileStream(
                   metadataPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   4096,
                   FileOptions.WriteThrough))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }

        File.SetAttributes(metadataPath, FileAttributes.Hidden);
        EnsureNotReparsePoint(metadataPath);
    }

    private bool TryReadManagedMetadata(
        string packageRoot,
        string expectedId,
        out ModInstalledMetadata metadata,
        out string error)
    {
        metadata = null;
        error = null;
        try
        {
            if (!Directory.Exists(packageRoot))
            {
                error = "directory does not exist";
                return false;
            }

            EnsureNotReparsePoint(packageRoot);
            string metadataPath = Path.Combine(packageRoot, MetadataFileName);
            if (!File.Exists(metadataPath))
            {
                error = MetadataFileName + " is missing";
                return false;
            }

            EnsureNotReparsePoint(metadataPath);
            FileInfo file = new FileInfo(metadataPath);
            if (file.Length <= 0 || file.Length > MetadataMaxBytes)
            {
                error = "loader metadata has an invalid size";
                return false;
            }

            metadata = JsonCodec.Deserialize<ModInstalledMetadata>(StrictUtf8.GetString(File.ReadAllBytes(metadataPath)));
            if (metadata == null || metadata.schemaVersion != 1 ||
                !ManifestValidator.TryValidatePackageId(metadata.id, out _) ||
                !ManifestValidator.TryValidateSemVer(metadata.version, out _) ||
                !IsSha256(metadata.archiveSha256))
            {
                error = "loader metadata is incomplete";
                metadata = null;
                return false;
            }

            if (!string.IsNullOrEmpty(expectedId) &&
                !string.Equals(metadata.id, expectedId, StringComparison.OrdinalIgnoreCase))
            {
                error = "loader metadata belongs to a different Mod id";
                metadata = null;
                return false;
            }

            string manifestPath = Path.Combine(packageRoot, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                error = "manifest.json is missing";
                metadata = null;
                return false;
            }

            EnsureNotReparsePoint(manifestPath);
            FileInfo manifestFile = new FileInfo(manifestPath);
            if (manifestFile.Length <= 0 || manifestFile.Length > MaxManifestBytes)
            {
                error = "manifest.json has an invalid size";
                metadata = null;
                return false;
            }

            ModManifest manifest = JsonCodec.Deserialize<ModManifest>(
                StrictUtf8.GetString(File.ReadAllBytes(manifestPath)));
            if (manifest == null || !string.Equals(manifest.id, metadata.id, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(manifest.version, metadata.version, StringComparison.Ordinal))
            {
                error = "manifest identity does not match loader metadata";
                metadata = null;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            metadata = null;
            error = ex.Message;
            return false;
        }
    }

    private void EnsureInfrastructure()
    {
        CreateSafeDirectory(_modsRoot);
        CreateSafeDirectory(_inboxRoot);
        CreateSafeDirectory(_stateRoot);
        CreateSafeDirectory(_stagingRoot);
        CreateSafeDirectory(_backupRoot);
        CreateSafeDirectory(_trashRoot);
    }

    private FileStream AcquireInstallLock()
    {
        string lockPath = Path.Combine(_stateRoot, "install.lock");
        if (File.Exists(lockPath))
        {
            EnsureNotReparsePoint(lockPath);
        }

        FileStream stream = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            1,
            FileOptions.WriteThrough);
        try
        {
            EnsureNotReparsePoint(lockPath);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private string ResolveInboxArchive(string inboxFileName)
    {
        ValidateSimpleEntryName(inboxFileName, "inbox archive");
        string extension = Path.GetExtension(inboxFileName);
        if (!string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".sunmod", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Only .zip and .sunmod archives can be installed.");
        }

        string fullPath = GetDirectChildPath(_inboxRoot, inboxFileName);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The inbox archive does not exist.", fullPath);
        }

        EnsureNotReparsePoint(fullPath);
        return fullPath;
    }

    private static string ResolveArchivePath(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            throw new InvalidDataException("An archive path is required.");
        }

        string value = Environment.ExpandEnvironmentVariables(archivePath.Trim().Trim('"'));
        string fullPath = Path.GetFullPath(value);
        string extension = Path.GetExtension(fullPath);
        if (!string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".sunmod", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Only .zip and .sunmod archives can be installed.");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The Mod archive does not exist.", fullPath);
        }

        EnsureNotReparsePoint(fullPath);
        return fullPath;
    }

    private string CreateTransactionDirectory()
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            string path = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N"));
            if (Directory.Exists(path) || File.Exists(path))
            {
                continue;
            }

            Directory.CreateDirectory(path);
            EnsureNotReparsePoint(path);
            return path;
        }

        throw new IOException("Could not allocate a unique Mod staging directory.");
    }

    private string CreateStorageDestination(string storageRoot, string modId)
    {
        string token = HashToken(modId);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            string name = token + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) +
                          "." + Guid.NewGuid().ToString("N");
            string path = GetDirectChildPath(storageRoot, name);
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                return path;
            }
        }

        throw new IOException("Could not allocate a unique managed Mod storage path.");
    }

    private string ResolveStorageEntry(string storageRoot, string entryName)
    {
        ValidateSimpleEntryName(entryName, "managed storage entry");
        string path = GetDirectChildPath(storageRoot, entryName);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException("Managed storage entry does not exist: " + entryName);
        }

        EnsureNotReparsePoint(path);
        return path;
    }

    private static void ValidateSimpleEntryName(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Invalid " + label + " name.");
        }

        ValidateWindowsPathComponent(value, label);
    }

    private static void ValidateInstallId(string modId)
    {
        ValidateSimpleEntryName(modId, "Mod id");
        if (!ManifestValidator.TryValidatePackageId(modId, out string error))
        {
            throw new InvalidDataException("Invalid Mod id: " + error);
        }

        if (string.Equals(modId, "Inbox", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modId, StateDirectoryName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modId, ModRegistry.BuiltInVoiceControlId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Mod id is reserved by Sunny Mod Loader: " + modId);
        }
    }

    private static void ValidateWindowsPathComponent(string component, string context)
    {
        if (string.IsNullOrEmpty(component) || component == "." || component == "..")
        {
            throw new InvalidDataException("Invalid path component in " + context + ".");
        }

        if (component.Length > 255 || component.EndsWith(".", StringComparison.Ordinal) ||
            component.EndsWith(" ", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Overlong or trailing-dot/space path component in " + context + ".");
        }

        foreach (char character in component)
        {
            if (character < 32 || Array.IndexOf(InvalidWindowsNameChars, character) >= 0)
            {
                throw new InvalidDataException("Invalid Windows filename character in " + context + ".");
            }
        }

        int extension = component.IndexOf('.');
        string deviceStem = (extension < 0 ? component : component.Substring(0, extension)).TrimEnd(' ', '.');
        if (ReservedDeviceNames.Contains(deviceStem))
        {
            throw new InvalidDataException("Reserved Windows device name in " + context + ": " + component);
        }
    }

    private static HashSet<string> BuildReservedDeviceNames()
    {
        HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"
        };
        for (int number = 1; number <= 9; number++)
        {
            result.Add("COM" + number);
            result.Add("LPT" + number);
        }

        return result;
    }

    private void ValidateManagedPackageForRestore(string packageRoot, ModInstalledMetadata metadata)
    {
        string manifestPath = Path.Combine(packageRoot, "manifest.json");
        string manifestJson = StrictUtf8.GetString(File.ReadAllBytes(manifestPath));
        if (!ManifestValidator.TryValidateRawJson(manifestJson, out string rawManifestError))
        {
            throw new InvalidDataException(rawManifestError);
        }

        ModManifest manifest = JsonCodec.Deserialize<ModManifest>(manifestJson);
        if (!ManifestValidator.Validate(
                manifest,
                packageRoot,
                manifestPath,
                _gameBuild,
                _loaderApiVersion,
                _log) ||
            !string.Equals(manifest.id, metadata.id, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.version, metadata.version, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Stored Mod package no longer matches its validated metadata.");
        }
    }

    private static bool IsSha256(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 64)
        {
            return false;
        }

        foreach (char character in value)
        {
            bool digit = character >= '0' && character <= '9';
            bool lower = character >= 'a' && character <= 'f';
            bool upper = character >= 'A' && character <= 'F';
            if (!digit && !lower && !upper)
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildCollisionKey(IReadOnlyList<string> components, int count)
    {
        StringBuilder builder = new StringBuilder();
        for (int index = 0; index < count; index++)
        {
            if (index > 0)
            {
                builder.Append('/');
            }

            builder.Append(NormalizeCollisionComponent(components[index]));
        }

        return builder.ToString();
    }

    private static string NormalizeCollisionComponent(string value)
    {
        return value.Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }

    private static bool IsManifestEntry(ArchiveEntryPlan entry)
    {
        return !entry.IsDirectory &&
               string.Equals(
                   NormalizeCollisionComponent(entry.Components[entry.Components.Count - 1]),
                   "MANIFEST.JSON",
                   StringComparison.Ordinal);
    }

    private static bool ExceedsCompressionRatio(long length, long compressedLength)
    {
        if (length <= RatioGraceBytes)
        {
            return false;
        }

        if (compressedLength <= 0)
        {
            return true;
        }

        long beyondGrace = length - RatioGraceBytes;
        return beyondGrace / compressedLength > MaxCompressionRatio ||
               (beyondGrace / compressedLength == MaxCompressionRatio &&
                beyondGrace % compressedLength != 0);
    }

    private static string GetExtractionPath(string payloadRoot, IReadOnlyList<string> components)
    {
        string relative = string.Join(Path.DirectorySeparatorChar.ToString(), components);
        string candidate = Path.GetFullPath(Path.Combine(payloadRoot, relative));
        string prefix = payloadRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                        Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Archive entry escaped the staging directory.");
        }

        return candidate;
    }

    private static string GetDirectChildPath(string root, string name)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string candidate = Path.GetFullPath(Path.Combine(normalizedRoot, name));
        if (!string.Equals(Path.GetDirectoryName(candidate), normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Path is not a direct child of its managed root: " + name);
        }

        return candidate;
    }

    private static void CreateSafeDirectory(string path)
    {
        if (File.Exists(path))
        {
            throw new IOException("A file occupies a required directory path: " + path);
        }

        Directory.CreateDirectory(path);
        EnsureNotReparsePoint(path);
    }

    private static void CreateSafeDirectoryTree(string root, string targetDirectory)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string normalizedTarget = Path.GetFullPath(targetDirectory).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!string.Equals(normalizedRoot, normalizedTarget, StringComparison.OrdinalIgnoreCase) &&
            !normalizedTarget.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Directory creation escaped its managed root.");
        }

        EnsureNotReparsePoint(normalizedRoot);
        if (string.Equals(normalizedRoot, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string relative = normalizedTarget.Substring(prefix.Length);
        string current = normalizedRoot;
        foreach (string component in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, component);
            CreateSafeDirectory(current);
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Reparse points are not allowed in managed Mod paths: " + path);
        }
    }

    private static void ValidateTreeHasNoReparsePoints(string root)
    {
        EnsureNotReparsePoint(root);
        Stack<string> pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string entry in Directory.GetFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Reparse points are not allowed in managed Mod paths: " + entry);
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private void TryDeleteInternalTree(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            string normalized = Path.GetFullPath(path);
            string prefix = _stagingRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                            Path.DirectorySeparatorChar;
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Refusing to clean a path outside the staging root: " + normalized);
            }

            ValidateTreeHasNoReparsePoints(normalized);
            Directory.Delete(normalized, true);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not clean Mod staging directory " + path + ": " + ex.Message);
        }
    }

    private static bool TryGetExpectedCrc32(ZipArchiveEntry entry, out uint crc32)
    {
        crc32 = 0;
        if (ZipCrc32Property == null)
        {
            return false;
        }

        object value = ZipCrc32Property.GetValue(entry, null);
        if (value == null)
        {
            return false;
        }

        crc32 = Convert.ToUInt32(value, CultureInfo.InvariantCulture);
        return true;
    }

    private static string ComputeSha256(Stream stream)
    {
        stream.Position = 0;
        using SHA256 sha256 = SHA256.Create();
        return ToHex(sha256.ComputeHash(stream));
    }

    private static string HashToken(string value)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] bytes = StrictUtf8.GetBytes(value.Normalize(NormalizationForm.FormC).ToUpperInvariant());
        return ToHex(sha256.ComputeHash(bytes)).Substring(0, 24);
    }

    private static string ToHex(byte[] bytes)
    {
        StringBuilder builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes)
        {
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static ModManagedPackageInfo ToPackageInfo(string directory, ModInstalledMetadata metadata)
    {
        DateTime installedAt = DateTime.MinValue;
        DateTime.TryParse(
            metadata.installedAtUtc,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out installedAt);
        return new ModManagedPackageInfo
        {
            EntryName = Path.GetFileName(directory),
            FullPath = directory,
            ModId = metadata.id,
            Version = metadata.version,
            ArchiveSha256 = metadata.archiveSha256,
            InstalledAtUtc = installedAt
        };
    }

    private void HitFault(ModInstallerFaultPoint point)
    {
        _faultInjector?.Hit(point);
    }

    private static void ValidateCentralDirectoryEnvelope(FileStream stream)
    {
        const uint EndSignature = 0x06054b50u;
        const int EndRecordMinimum = 22;
        int tailLength = (int)Math.Min(stream.Length, 65535L + EndRecordMinimum);
        byte[] tail = new byte[tailLength];
        stream.Position = stream.Length - tailLength;
        ReadExactly(stream, tail, 0, tail.Length);

        int endOffset = -1;
        for (int index = tail.Length - EndRecordMinimum; index >= 0; index--)
        {
            if (ReadUInt32(tail, index) != EndSignature)
            {
                continue;
            }

            ushort commentLength = ReadUInt16(tail, index + 20);
            if (index + EndRecordMinimum + commentLength == tail.Length)
            {
                endOffset = index;
                break;
            }
        }

        if (endOffset < 0)
        {
            throw new InvalidDataException("ZIP end-of-central-directory record was not found.");
        }

        ushort diskNumber = ReadUInt16(tail, endOffset + 4);
        ushort centralDisk = ReadUInt16(tail, endOffset + 6);
        ushort diskEntries = ReadUInt16(tail, endOffset + 8);
        ushort totalEntries = ReadUInt16(tail, endOffset + 10);
        uint centralSize = ReadUInt32(tail, endOffset + 12);
        uint centralOffset = ReadUInt32(tail, endOffset + 16);
        if (diskNumber != 0 || centralDisk != 0 || diskEntries != totalEntries)
        {
            throw new InvalidDataException("Multi-disk ZIP archives are not supported.");
        }

        if (totalEntries == ushort.MaxValue || centralSize == uint.MaxValue || centralOffset == uint.MaxValue)
        {
            throw new InvalidDataException("ZIP64 archives are not accepted by the bounded Mod installer.");
        }

        if (totalEntries > MaxEntries)
        {
            throw new InvalidDataException("The archive declares more than " + MaxEntries + " entries.");
        }

        if (centralSize > MaxCentralDirectoryBytes)
        {
            throw new InvalidDataException("The ZIP central directory exceeds the supported limit.");
        }

        long absoluteEndOffset = stream.Length - tailLength + endOffset;
        if ((long)centralOffset + centralSize > absoluteEndOffset)
        {
            throw new InvalidDataException("The ZIP central-directory bounds are invalid.");
        }

        stream.Position = 0;
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            int read = stream.Read(buffer, offset, count);
            if (read <= 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
            count -= read;
        }
    }

    private static ushort ReadUInt16(byte[] buffer, int offset)
    {
        return (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
    }

    private static uint ReadUInt32(byte[] buffer, int offset)
    {
        return (uint)(buffer[offset] |
                      (buffer[offset + 1] << 8) |
                      (buffer[offset + 2] << 16) |
                      (buffer[offset + 3] << 24));
    }

    private sealed class ArchivePlan
    {
        internal ArchivePlan(List<ArchiveEntryPlan> entries, long declaredTotalLength)
        {
            Entries = entries;
            DeclaredTotalLength = declaredTotalLength;
        }

        internal List<ArchiveEntryPlan> Entries { get; }
        internal long DeclaredTotalLength { get; }
    }

    private sealed class ArchiveEntryPlan
    {
        internal ArchiveEntryPlan(ZipArchiveEntry entry, IReadOnlyList<string> components, bool isDirectory)
        {
            Entry = entry;
            Components = components;
            IsDirectory = isDirectory;
        }

        internal ZipArchiveEntry Entry { get; }
        internal IReadOnlyList<string> Components { get; }
        internal bool IsDirectory { get; }
        internal IReadOnlyList<string> RelativeComponents { get; set; }
        internal bool Skip { get; set; }
    }

    private static class Crc32
    {
        internal const uint Start = 0xffffffffu;
        private static readonly uint[] Table = BuildTable();

        internal static void Update(ref uint crc, byte[] buffer, int offset, int count)
        {
            int end = offset + count;
            for (int index = offset; index < end; index++)
            {
                crc = Table[(crc ^ buffer[index]) & 0xffu] ^ (crc >> 8);
            }
        }

        internal static uint Finish(uint crc)
        {
            return ~crc;
        }

        private static uint[] BuildTable()
        {
            uint[] table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                uint value = index;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1u) != 0 ? 0xedb88320u ^ (value >> 1) : value >> 1;
                }

                table[index] = value;
            }

            return table;
        }
    }
}
