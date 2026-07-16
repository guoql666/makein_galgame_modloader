using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace SunnyModLoader;

internal sealed class WorkshopPackageLocation
{
    internal WorkshopPackageLocation(
        string rootPath,
        uint appId,
        ulong publishedFileId,
        string sourceDescription)
    {
        RootPath = rootPath;
        AppId = appId;
        PublishedFileId = publishedFileId;
        SourceDescription = sourceDescription;
    }

    internal string RootPath { get; }
    internal uint AppId { get; }
    internal ulong PublishedFileId { get; }
    internal string SourceDescription { get; }
}

internal sealed class WorkshopDiscovery
{
    private const long MaxVdfBytes = 16L * 1024L * 1024L;
    private const int MaxVdfEntries = 100000;

    private readonly ManualLogSource _log;
    private readonly string _gameRoot;
    private readonly ConfigEntry<string> _steamAppIds;
    private readonly ConfigEntry<string> _steamRoots;

    internal WorkshopDiscovery(ConfigFile config, ManualLogSource log, string gameRoot)
    {
        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        _log = log ?? throw new ArgumentNullException(nameof(log));
        _gameRoot = gameRoot ?? string.Empty;
        _steamAppIds = config.Bind(
            "Workshop",
            "SteamAppIds",
            string.Empty,
            "Semicolon- or comma-separated Steam AppIDs whose workshop items should be scanned.");
        _steamRoots = config.Bind(
            "Workshop",
            "SteamRoots",
            string.Empty,
            "Optional semicolon-separated Steam installation or library roots. When set, these override registry and common-path discovery.");
    }

    internal IReadOnlyList<WorkshopPackageLocation> Discover()
    {
        try
        {
            List<string> libraries = DiscoverLibraries();
            List<TrustedAppId> appIds = DiscoverAppIds(libraries);
            if (appIds.Count == 0)
            {
                _log.LogInfo(
                    "Steam Workshop auto-discovery was skipped because no trusted AppID was found. " +
                    "Set Workshop.SteamAppIds or place mods under a configured workshop root.");
                return Array.Empty<WorkshopPackageLocation>();
            }

            List<WorkshopPackageLocation> locations = new List<WorkshopPackageLocation>();
            HashSet<string> seenLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TrustedAppId appId in appIds)
            {
                foreach (string library in libraries)
                {
                    try
                    {
                        DiscoverPackages(library, appId, locations, seenLocations);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(
                            "Steam Workshop directory was skipped for AppID " + appId.Value +
                            " in " + library + ": " + ex.Message);
                    }
                }
            }

            locations.Sort((left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(left.RootPath, right.RootPath));
            return locations.AsReadOnly();
        }
        catch (Exception ex)
        {
            _log.LogWarning("Steam Workshop discovery failed without stopping the mod loader: " + ex.Message);
            return Array.Empty<WorkshopPackageLocation>();
        }
    }

    private List<string> DiscoverLibraries()
    {
        List<string> seeds = new List<string>();
        HashSet<string> seenSeeds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasConfiguredRoots = !string.IsNullOrWhiteSpace(_steamRoots.Value);

        if (hasConfiguredRoots)
        {
            foreach (string configuredRoot in SplitRoots(_steamRoots.Value))
            {
                AddExistingDirectory(configuredRoot, seeds, seenSeeds, "configured Steam root", true);
            }
        }
        else
        {
            AddRegistrySteamRoots(seeds, seenSeeds);
            AddCommonSteamRoots(seeds, seenSeeds);
            AddNearbySteamLibrary(seeds, seenSeeds);
        }

        List<string> libraries = new List<string>();
        HashSet<string> seenLibraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string seed in seeds)
        {
            AddSteamLibrary(seed, libraries, seenLibraries, "Steam root " + seed);

            string libraryFoldersPath = Path.Combine(seed, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFoldersPath))
            {
                continue;
            }

            try
            {
                if (HasReparsePointBelowRoot(seed, libraryFoldersPath))
                {
                    _log.LogWarning("Steam library list is behind a reparse point and was skipped: " + libraryFoldersPath);
                    continue;
                }

                foreach (string libraryPath in ReadLibraryFolders(libraryFoldersPath))
                {
                    AddSteamLibrary(
                        libraryPath,
                        libraries,
                        seenLibraries,
                        "libraryfolders.vdf " + libraryFoldersPath);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("Steam library list could not be read " + libraryFoldersPath + ": " + ex.Message);
            }
        }

        libraries.Sort(StringComparer.OrdinalIgnoreCase);
        return libraries;
    }

    private List<TrustedAppId> DiscoverAppIds(IReadOnlyList<string> libraries)
    {
        List<TrustedAppId> configured = ParseConfiguredAppIds(_steamAppIds.Value);
        if (configured.Count > 0)
        {
            return configured;
        }

        string environmentAppId = Environment.GetEnvironmentVariable("SteamAppId");
        if (!string.IsNullOrWhiteSpace(environmentAppId))
        {
            if (TryParseAppId(environmentAppId, out uint appId))
            {
                return new List<TrustedAppId>
                {
                    new TrustedAppId(appId, "SteamAppId environment variable")
                };
            }

            _log.LogWarning("SteamAppId environment variable is not a valid positive numeric AppID.");
        }

        if (TryNormalizePath(_gameRoot, out string gameRoot))
        {
            string appIdPath = Path.Combine(gameRoot, "steam_appid.txt");
            if (File.Exists(appIdPath))
            {
                try
                {
                    string value = File.ReadAllText(appIdPath).Trim().TrimStart('\ufeff');
                    if (TryParseAppId(value, out uint appId))
                    {
                        return new List<TrustedAppId>
                        {
                            new TrustedAppId(appId, "steam_appid.txt " + appIdPath)
                        };
                    }

                    _log.LogWarning("steam_appid.txt does not contain a valid positive numeric AppID: " + appIdPath);
                }
                catch (Exception ex)
                {
                    _log.LogWarning("steam_appid.txt could not be read " + appIdPath + ": " + ex.Message);
                }
            }

            List<TrustedAppId> manifestMatches = FindExactManifestMatches(gameRoot, libraries);
            if (manifestMatches.Count > 0)
            {
                return manifestMatches;
            }
        }

        return new List<TrustedAppId>();
    }

    private List<TrustedAppId> ParseConfiguredAppIds(string value)
    {
        List<TrustedAppId> result = new List<TrustedAppId>();
        HashSet<uint> seen = new HashSet<uint>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        string[] entries = value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string entry in entries)
        {
            if (!TryParseAppId(entry, out uint appId))
            {
                _log.LogWarning("Workshop.SteamAppIds entry is not a valid positive numeric AppID: " + entry.Trim());
                continue;
            }

            if (seen.Add(appId))
            {
                result.Add(new TrustedAppId(appId, "Workshop.SteamAppIds"));
            }
        }

        result.Sort((left, right) => left.Value.CompareTo(right.Value));
        return result;
    }

    private List<TrustedAppId> FindExactManifestMatches(string gameRoot, IReadOnlyList<string> libraries)
    {
        List<TrustedAppId> result = new List<TrustedAppId>();
        HashSet<uint> seen = new HashSet<uint>();
        foreach (string library in libraries)
        {
            string steamApps = Path.Combine(library, "steamapps");
            string[] manifests;
            try
            {
                if (HasReparsePointBelowRoot(library, steamApps))
                {
                    _log.LogWarning("Steam app manifest directory is behind a reparse point: " + steamApps);
                    continue;
                }

                manifests = Directory.GetFiles(steamApps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly);
                Array.Sort(manifests, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Steam app manifests could not be enumerated under " + steamApps + ": " + ex.Message);
                continue;
            }

            foreach (string manifestPath in manifests)
            {
                try
                {
                    if (IsReparsePoint(manifestPath))
                    {
                        _log.LogWarning("Steam app manifest is a reparse point and was skipped: " + manifestPath);
                        continue;
                    }

                    List<VdfEntry> document = ReadVdf(manifestPath);
                    VdfEntry appState = FindEntry(document, "AppState");
                    if (appState == null || appState.Children == null)
                    {
                        continue;
                    }

                    string appIdValue = FindValue(appState.Children, "appid");
                    string installDirectory = FindValue(appState.Children, "installdir");
                    if (!TryParseAppId(appIdValue, out uint appId) || string.IsNullOrWhiteSpace(installDirectory))
                    {
                        continue;
                    }

                    string expectedManifestName = "appmanifest_" +
                                                  appId.ToString(CultureInfo.InvariantCulture) + ".acf";
                    if (!string.Equals(
                            Path.GetFileName(manifestPath),
                            expectedManifestName,
                            StringComparison.OrdinalIgnoreCase) ||
                        !IsSimpleInstallDirectory(installDirectory))
                    {
                        _log.LogWarning("Steam app manifest identity is inconsistent and was skipped: " + manifestPath);
                        continue;
                    }

                    string expectedRoot = Path.Combine(library, "steamapps", "common", installDirectory);
                    if (!TryNormalizePath(expectedRoot, out expectedRoot) ||
                        !string.Equals(gameRoot, expectedRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (seen.Add(appId))
                    {
                        result.Add(new TrustedAppId(appId, "exact install match in " + manifestPath));
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug("Steam app manifest was skipped " + manifestPath + ": " + ex.Message);
                }
            }
        }

        result.Sort((left, right) => left.Value.CompareTo(right.Value));
        return result;
    }

    private void DiscoverPackages(
        string library,
        TrustedAppId appId,
        List<WorkshopPackageLocation> locations,
        HashSet<string> seenLocations)
    {
        string appIdText = appId.Value.ToString(CultureInfo.InvariantCulture);
        string workshopRoot = Path.Combine(library, "steamapps", "workshop", "content", appIdText);
        if (!Directory.Exists(workshopRoot))
        {
            return;
        }

        if (!TryNormalizePath(workshopRoot, out workshopRoot))
        {
            _log.LogWarning("Steam Workshop root has an invalid path and was skipped: " + workshopRoot);
            return;
        }

        if (HasReparsePointBelowRoot(library, workshopRoot))
        {
            _log.LogWarning("Steam Workshop root is behind a reparse point and was skipped: " + workshopRoot);
            return;
        }

        string[] directories;
        bool hasInstalledItemIndex = TryReadInstalledWorkshopItems(library, appId.Value, out HashSet<ulong> installedItems);
        try
        {
            directories = Directory.GetDirectories(workshopRoot, "*", SearchOption.TopDirectoryOnly);
            Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Steam Workshop root could not be enumerated " + workshopRoot + ": " + ex.Message);
            return;
        }

        foreach (string directory in directories)
        {
            string name = Path.GetFileName(directory);
            if (!ulong.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out ulong publishedFileId) ||
                publishedFileId == 0)
            {
                continue;
            }

            if (hasInstalledItemIndex && !installedItems.Contains(publishedFileId))
            {
                continue;
            }

            try
            {
                if (!TryNormalizePath(directory, out string itemRoot))
                {
                    continue;
                }

                if (HasReparsePointBelowRoot(library, itemRoot))
                {
                    _log.LogWarning("Steam Workshop item is behind a reparse point and was skipped: " + itemRoot);
                    continue;
                }

                if (!seenLocations.Add(itemRoot))
                {
                    continue;
                }

                locations.Add(new WorkshopPackageLocation(
                    itemRoot,
                    appId.Value,
                    publishedFileId,
                    appId.Source + "; Steam Workshop " + appIdText + "/" + name +
                    "; library " + library));
            }
            catch (Exception ex)
            {
                _log.LogWarning("Steam Workshop item was skipped " + directory + ": " + ex.Message);
            }
        }
    }

    private void AddRegistrySteamRoots(List<string> roots, HashSet<string> seen)
    {
        TryReadRegistryRoot("CurrentUser", @"Software\Valve\Steam", "SteamPath", roots, seen);
        TryReadRegistryRoot("CurrentUser", @"Software\Valve\Steam", "InstallPath", roots, seen);
        TryReadRegistryExeRoot("CurrentUser", @"Software\Valve\Steam", "SteamExe", roots, seen);
        TryReadRegistryRoot("LocalMachine", @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", roots, seen);
        TryReadRegistryRoot("LocalMachine", @"SOFTWARE\WOW6432Node\Valve\Steam", "SteamPath", roots, seen);
        TryReadRegistryRoot("LocalMachine", @"SOFTWARE\Valve\Steam", "InstallPath", roots, seen);
        TryReadRegistryRoot("LocalMachine", @"SOFTWARE\Valve\Steam", "SteamPath", roots, seen);
    }

    private bool TryReadInstalledWorkshopItems(
        string library,
        uint appId,
        out HashSet<ulong> installedItems)
    {
        installedItems = new HashSet<ulong>();
        string appIdText = appId.ToString(CultureInfo.InvariantCulture);
        string path = Path.Combine(
            library,
            "steamapps",
            "workshop",
            "appworkshop_" + appIdText + ".acf");
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            if (HasReparsePointBelowRoot(library, path))
            {
                _log.LogWarning("Steam Workshop index is behind a reparse point and was ignored: " + path);
                return false;
            }

            List<VdfEntry> document = ReadVdf(path);
            VdfEntry appWorkshop = FindEntry(document, "AppWorkshop");
            VdfEntry installed = FindEntry(appWorkshop?.Children, "WorkshopItemsInstalled");
            if (installed?.Children == null)
            {
                _log.LogDebug("Steam Workshop index has no installed-item section: " + path);
                return false;
            }

            foreach (VdfEntry entry in installed.Children)
            {
                if (ulong.TryParse(
                        entry.Key,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out ulong publishedFileId) &&
                    publishedFileId > 0)
                {
                    installedItems.Add(publishedFileId);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Steam Workshop installed-item index could not be read " + path + ": " + ex.Message);
            installedItems.Clear();
            return false;
        }
    }

    private void TryReadRegistryRoot(
        string hiveName,
        string keyPath,
        string valueName,
        List<string> roots,
        HashSet<string> seen)
    {
        try
        {
            string value = ReadRegistryString(hiveName, keyPath, valueName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                AddExistingDirectory(value, roots, seen, "Steam registry", false);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug("Steam registry value could not be read " + keyPath + "\\" + valueName + ": " + ex.Message);
        }
    }

    private void TryReadRegistryExeRoot(
        string hiveName,
        string keyPath,
        string valueName,
        List<string> roots,
        HashSet<string> seen)
    {
        try
        {
            string value = ReadRegistryString(hiveName, keyPath, valueName);
            string directory = string.IsNullOrWhiteSpace(value) ? null : Path.GetDirectoryName(value);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                AddExistingDirectory(directory, roots, seen, "Steam registry executable", false);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug("Steam registry executable could not be read " + keyPath + "\\" + valueName + ": " + ex.Message);
        }
    }

    private static string ReadRegistryString(string hiveName, string keyPath, string valueName)
    {
        Type registryType = FindRegistryType();
        if (registryType == null)
        {
            throw new PlatformNotSupportedException("The Microsoft.Win32 Registry API is unavailable.");
        }

        System.Reflection.PropertyInfo hiveProperty = registryType.GetProperty(
            hiveName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        System.Reflection.FieldInfo hiveField = registryType.GetField(
            hiveName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        object hive = hiveProperty?.GetValue(null, null) ?? hiveField?.GetValue(null);
        if (hive == null)
        {
            return null;
        }

        object key = null;
        try
        {
            System.Reflection.MethodInfo openSubKey = hive.GetType().GetMethod(
                "OpenSubKey",
                new[] { typeof(string) });
            key = openSubKey?.Invoke(hive, new object[] { keyPath });
            if (key == null)
            {
                return null;
            }

            System.Reflection.MethodInfo getValue = key.GetType().GetMethod(
                "GetValue",
                new[] { typeof(string) });
            return getValue?.Invoke(key, new object[] { valueName }) as string;
        }
        finally
        {
            (key as IDisposable)?.Dispose();
        }
    }

    private static Type FindRegistryType()
    {
        Type registryType = Type.GetType("Microsoft.Win32.Registry, mscorlib", false) ??
                            Type.GetType("Microsoft.Win32.Registry, Microsoft.Win32.Registry", false);
        if (registryType != null)
        {
            return registryType;
        }

        foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            registryType = assembly.GetType("Microsoft.Win32.Registry", false);
            if (registryType != null)
            {
                return registryType;
            }
        }

        return null;
    }

    private void AddCommonSteamRoots(List<string> roots, HashSet<string> seen)
    {
        AddCommonRoot(Environment.GetEnvironmentVariable("ProgramFiles(x86)"), roots, seen);
        AddCommonRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), roots, seen);

        try
        {
            string systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
            if (!string.IsNullOrWhiteSpace(systemRoot))
            {
                AddExistingDirectory(Path.Combine(systemRoot, "Steam"), roots, seen, "common Steam path", false);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug("Common Steam path could not be evaluated: " + ex.Message);
        }
    }

    private void AddCommonRoot(string programFiles, List<string> roots, HashSet<string> seen)
    {
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            AddExistingDirectory(Path.Combine(programFiles, "Steam"), roots, seen, "common Steam path", false);
        }
    }

    private void AddNearbySteamLibrary(List<string> roots, HashSet<string> seen)
    {
        try
        {
            if (!TryNormalizePath(_gameRoot, out string gameRoot))
            {
                return;
            }

            DirectoryInfo gameDirectory = new DirectoryInfo(gameRoot);
            DirectoryInfo commonDirectory = gameDirectory.Parent;
            DirectoryInfo steamAppsDirectory = commonDirectory?.Parent;
            DirectoryInfo libraryDirectory = steamAppsDirectory?.Parent;
            if (commonDirectory == null || steamAppsDirectory == null || libraryDirectory == null ||
                !string.Equals(commonDirectory.Name, "common", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(steamAppsDirectory.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            AddExistingDirectory(libraryDirectory.FullName, roots, seen, "game install Steam library", false);
        }
        catch (Exception ex)
        {
            _log.LogDebug("The game path could not be checked for a nearby Steam library: " + ex.Message);
        }
    }

    private void AddSteamLibrary(
        string value,
        List<string> libraries,
        HashSet<string> seen,
        string source)
    {
        if (!TryNormalizePath(value, out string library) || !Directory.Exists(library))
        {
            return;
        }

        try
        {
            if (IsReparsePoint(library))
            {
                _log.LogWarning("Steam library root is a reparse point and was skipped: " + library);
                return;
            }

            if (!Directory.Exists(Path.Combine(library, "steamapps")))
            {
                return;
            }

            if (HasReparsePointBelowRoot(library, Path.Combine(library, "steamapps")))
            {
                _log.LogWarning("Steam library steamapps directory is a reparse point and was skipped: " + library);
                return;
            }

            if (seen.Add(library))
            {
                libraries.Add(library);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Steam library was skipped from " + source + ": " + ex.Message);
        }
    }

    private void AddExistingDirectory(
        string value,
        List<string> roots,
        HashSet<string> seen,
        string source,
        bool reportMissing)
    {
        string expanded = Environment.ExpandEnvironmentVariables((value ?? string.Empty).Trim().Trim('"'));
        if (!TryNormalizePath(expanded, out string root))
        {
            if (reportMissing)
            {
                _log.LogWarning("Invalid " + source + ": " + value);
            }

            return;
        }

        if (!Directory.Exists(root))
        {
            if (reportMissing)
            {
                _log.LogWarning("Missing " + source + ": " + root);
            }

            return;
        }

        try
        {
            if (IsReparsePoint(root))
            {
                _log.LogWarning(source + " is a reparse point and was skipped: " + root);
                return;
            }

            if (seen.Add(root))
            {
                roots.Add(root);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(source + " was skipped " + root + ": " + ex.Message);
        }
    }

    private IEnumerable<string> ReadLibraryFolders(string path)
    {
        List<VdfEntry> document = ReadVdf(path);
        VdfEntry libraryFolders = FindEntry(document, "libraryfolders");
        if (libraryFolders == null || libraryFolders.Children == null)
        {
            yield break;
        }

        foreach (VdfEntry entry in libraryFolders.Children)
        {
            if (!uint.TryParse(entry.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                continue;
            }

            string value = entry.Children == null ? entry.Value : FindValue(entry.Children, "path");
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }

    private static List<VdfEntry> ReadVdf(string path)
    {
        FileInfo file = new FileInfo(path);
        if (file.Length > MaxVdfBytes)
        {
            throw new InvalidDataException("VDF file exceeds the 16 MiB safety limit.");
        }

        string text = File.ReadAllText(path);
        return new VdfParser(text).Parse();
    }

    private static VdfEntry FindEntry(IReadOnlyList<VdfEntry> entries, string key)
    {
        if (entries == null)
        {
            return null;
        }

        for (int i = 0; i < entries.Count; i++)
        {
            if (string.Equals(entries[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return entries[i];
            }
        }

        return null;
    }

    private static string FindValue(IReadOnlyList<VdfEntry> entries, string key)
    {
        VdfEntry entry = FindEntry(entries, key);
        return entry?.Value;
    }

    private static IEnumerable<string> SplitRoots(string value)
    {
        return (value ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool TryParseAppId(string value, out uint appId)
    {
        return uint.TryParse(
                   (value ?? string.Empty).Trim(),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out appId) && appId > 0;
    }

    private static bool TryNormalizePath(string value, out string normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(value.Trim());
            string pathRoot = Path.GetPathRoot(fullPath);
            normalized = string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static bool HasReparsePointBelowRoot(string root, string target)
    {
        string normalizedRoot = NormalizeComparablePath(root);
        string normalizedTarget = NormalizeComparablePath(target);
        string prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                        normalizedRoot.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        if (!string.Equals(normalizedRoot, normalizedTarget, StringComparison.OrdinalIgnoreCase) &&
            !normalizedTarget.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsReparsePoint(normalizedRoot))
        {
            return true;
        }

        if (string.Equals(normalizedRoot, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string relative = normalizedTarget.Substring(prefix.Length);
        string current = normalizedRoot;
        foreach (string component in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if ((File.Exists(current) || Directory.Exists(current)) && IsReparsePoint(current))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeComparablePath(string value)
    {
        string fullPath = Path.GetFullPath(value);
        string pathRoot = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsSimpleInstallDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "." || value == ".." ||
            Path.IsPathRooted(value) || value.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0 ||
            value.IndexOf(':') >= 0 || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal);
    }

    private sealed class TrustedAppId
    {
        internal TrustedAppId(uint value, string source)
        {
            Value = value;
            Source = source;
        }

        internal uint Value { get; }
        internal string Source { get; }
    }

    private sealed class VdfEntry
    {
        internal VdfEntry(string key, string value, List<VdfEntry> children)
        {
            Key = key;
            Value = value;
            Children = children;
        }

        internal string Key { get; }
        internal string Value { get; }
        internal List<VdfEntry> Children { get; }
    }

    private sealed class VdfParser
    {
        private readonly string _text;
        private int _position;
        private int _entryCount;

        internal VdfParser(string text)
        {
            _text = text ?? string.Empty;
        }

        internal List<VdfEntry> Parse()
        {
            return ParseEntries(false, 0);
        }

        private List<VdfEntry> ParseEntries(bool expectCloseBrace, int depth)
        {
            if (depth > 64)
            {
                throw new InvalidDataException("VDF nesting exceeds the safety limit.");
            }

            List<VdfEntry> entries = new List<VdfEntry>();
            while (true)
            {
                SkipTrivia();
                if (_position >= _text.Length)
                {
                    if (expectCloseBrace)
                    {
                        throw new InvalidDataException("VDF object is missing a closing brace.");
                    }

                    return entries;
                }

                if (_text[_position] == '}')
                {
                    if (!expectCloseBrace)
                    {
                        throw new InvalidDataException("VDF contains an unexpected closing brace.");
                    }

                    _position++;
                    return entries;
                }

                string key = ReadToken();
                SkipTrivia();
                if (_position >= _text.Length)
                {
                    throw new InvalidDataException("VDF key has no value: " + key);
                }

                if (_text[_position] == '{')
                {
                    _position++;
                    AddEntry(entries, new VdfEntry(key, null, ParseEntries(true, depth + 1)));
                }
                else
                {
                    AddEntry(entries, new VdfEntry(key, ReadToken(), null));
                }
            }
        }

        private void AddEntry(List<VdfEntry> entries, VdfEntry entry)
        {
            _entryCount++;
            if (_entryCount > MaxVdfEntries)
            {
                throw new InvalidDataException("VDF entry count exceeds the safety limit.");
            }

            entries.Add(entry);
        }

        private void SkipTrivia()
        {
            while (_position < _text.Length)
            {
                char current = _text[_position];
                if (char.IsWhiteSpace(current) || current == '\ufeff')
                {
                    _position++;
                    continue;
                }

                if (current == '/' && _position + 1 < _text.Length && _text[_position + 1] == '/')
                {
                    _position += 2;
                    while (_position < _text.Length && _text[_position] != '\r' && _text[_position] != '\n')
                    {
                        _position++;
                    }

                    continue;
                }

                break;
            }
        }

        private string ReadToken()
        {
            SkipTrivia();
            if (_position >= _text.Length || _text[_position] == '{' || _text[_position] == '}')
            {
                throw new InvalidDataException("VDF token was expected.");
            }

            if (_text[_position] != '"')
            {
                int start = _position;
                while (_position < _text.Length && !char.IsWhiteSpace(_text[_position]) &&
                       _text[_position] != '{' && _text[_position] != '}')
                {
                    _position++;
                }

                return _text.Substring(start, _position - start);
            }

            _position++;
            System.Text.StringBuilder value = new System.Text.StringBuilder();
            while (_position < _text.Length)
            {
                char current = _text[_position++];
                if (current == '"')
                {
                    return value.ToString();
                }

                if (current != '\\' || _position >= _text.Length)
                {
                    value.Append(current);
                    continue;
                }

                char escaped = _text[_position++];
                switch (escaped)
                {
                    case '\\':
                    case '"':
                        value.Append(escaped);
                        break;
                    case 'n':
                        value.Append('\n');
                        break;
                    case 'r':
                        value.Append('\r');
                        break;
                    case 't':
                        value.Append('\t');
                        break;
                    default:
                        value.Append('\\');
                        value.Append(escaped);
                        break;
                }
            }

            throw new InvalidDataException("VDF quoted string is not terminated.");
        }
    }
}
