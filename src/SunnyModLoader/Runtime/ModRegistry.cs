using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using MakeineGalGameQM.Core;
using UnityEngine;

namespace SunnyModLoader;

internal enum ModPackageSourceKind
{
    BuiltIn,
    Local,
    AdditionalRoot,
    SteamWorkshop
}

internal sealed class ModScanIssue
{
    internal string RootPath;
    internal string SourceDescription;
    internal string Message;
    internal bool IsRuntime;
    internal bool IsWarning;
}

internal sealed class ModUserState
{
    internal bool Enabled;
    internal int Priority;
    internal readonly Dictionary<string, bool> BoolSettings =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    internal ModUserState Clone()
    {
        ModUserState clone = new ModUserState
        {
            Enabled = Enabled,
            Priority = Priority
        };
        foreach (KeyValuePair<string, bool> setting in BoolSettings)
        {
            clone.BoolSettings[setting.Key] = setting.Value;
        }

        return clone;
    }
}

internal sealed class ModPackage
{
    internal string RootPath;
    internal ModManifest Manifest;
    internal ConfigEntry<bool> Enabled;
    internal ConfigEntry<int> Priority;
    internal readonly Dictionary<string, ConfigEntry<bool>> BoolSettings =
        new Dictionary<string, ConfigEntry<bool>>(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, bool> RuntimeBoolSettings =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    internal ModPackageSourceKind SourceKind;
    internal string SourceDescription;
    internal string SourceIdentity;
    internal bool IsManaged;
    internal bool RuntimeEnabled;
    internal int RuntimePriority;
    internal bool RuntimeBlocked;
    internal string RuntimeBlockReason;
    internal readonly List<FlowDefinition> Flows = new List<FlowDefinition>();
    internal DialoguePatchDefinition[] DialoguePatches = Array.Empty<DialoguePatchDefinition>();
    internal BranchOptionDefinition[] Branches = Array.Empty<BranchOptionDefinition>();
    internal GalleryDefinition[] Gallery = Array.Empty<GalleryDefinition>();
    internal OverlayDefinition[] Overlays = Array.Empty<OverlayDefinition>();
    internal SpriteDefinition[] Sprites = Array.Empty<SpriteDefinition>();
    internal SpineDefinition[] Spines = Array.Empty<SpineDefinition>();
    internal AudioControlDefinition AudioControl;

    internal string Id => Manifest.id;
    internal string DisplayName => string.IsNullOrWhiteSpace(Manifest.name) ? Manifest.id : Manifest.name;
    internal bool IsBuiltIn => SourceKind == ModPackageSourceKind.BuiltIn;
    internal bool IsExternal => SourceKind == ModPackageSourceKind.AdditionalRoot ||
                                SourceKind == ModPackageSourceKind.SteamWorkshop;
}

internal sealed class ModRegistry
{
    internal const int LoaderApiVersion = 2;
    internal const string BuiltInVoiceControlId = "qm.sunny.voice-control";
    private const string BuiltInVoiceControlSource = "builtin:voice-control";

    private readonly ConfigFile _config;
    private readonly ManualLogSource _log;
    private readonly string _gameBuild;
    private readonly ConfigEntry<string> _additionalRoots;
    private readonly WorkshopDiscovery _workshopDiscovery;
    private readonly Dictionary<string, ModPackage> _byId =
        new Dictionary<string, ModPackage>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> _voiceVolumes =
        new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    private List<ModPackage> _all = new List<ModPackage>();
    private List<ModPackage> _activeLowToHigh = new List<ModPackage>();
    private List<ModScanIssue> _issues = new List<ModScanIssue>();

    internal ModRegistry(ConfigFile config, ManualLogSource log, string gameBuild)
    {
        _config = config;
        _log = log;
        _gameBuild = gameBuild ?? string.Empty;
        _additionalRoots = config.Bind(
            "Workshop",
            "AdditionalRoots",
            string.Empty,
            "Semicolon-separated package directories or directories containing Mod packages.");
        _workshopDiscovery = new WorkshopDiscovery(config, log, Paths.GameRootPath);
    }

    internal IReadOnlyList<ModPackage> All => _all;
    internal IReadOnlyList<ModPackage> ActiveLowToHigh => _activeLowToHigh;
    internal IReadOnlyList<ModScanIssue> Issues => _issues;
    internal IEnumerable<ModPackage> ActiveHighToLow => _activeLowToHigh.AsEnumerable().Reverse();

    internal void Scan()
    {
        _byId.Clear();
        _voiceVolumes.Clear();
        List<ModScanIssue> issues = new List<ModScanIssue>();
        List<PackageCandidate> validCandidates = new List<PackageCandidate>();

        foreach (PackageLocation location in EnumeratePackageDirectories(issues))
        {
            string manifestPath = Path.Combine(location.RootPath, "manifest.json");
            try
            {
                if (IsReparsePoint(location.RootPath))
                {
                    AddIssue(issues, location, "Package root is a symbolic link or reparse point.");
                    continue;
                }

                FileInfo manifestFile = new FileInfo(manifestPath);
                if (!manifestFile.Exists)
                {
                    AddIssue(issues, location, "manifest.json is missing from the package root.");
                    continue;
                }

                if (IsReparsePoint(manifestPath))
                {
                    AddIssue(issues, location, "manifest.json is a symbolic link or reparse point.");
                    continue;
                }

                if (manifestFile.Length > 256 * 1024)
                {
                    AddIssue(issues, location, "manifest.json exceeds the 256 KiB limit.");
                    continue;
                }

                string json = File.ReadAllText(manifestPath);
                if (!ManifestValidator.TryValidateRawJson(json, out string rawManifestError))
                {
                    _log.LogError("Invalid mod manifest " + manifestPath + ": " + rawManifestError);
                    AddIssue(issues, location, rawManifestError);
                    continue;
                }

                ModManifest manifest = JsonCodec.Deserialize<ModManifest>(json);
                if (string.Equals(manifest?.id, BuiltInVoiceControlId, StringComparison.OrdinalIgnoreCase))
                {
                    const string message =
                        "This Mod id is reserved by the built-in VoiceControl component; remove the external copy.";
                    _log.LogWarning(message + " Path: " + location.RootPath);
                    AddIssue(issues, location, message);
                    continue;
                }
                if (!ManifestValidator.Validate(
                        manifest,
                        location.RootPath,
                        manifestPath,
                        _gameBuild,
                        LoaderApiVersion,
                        _log,
                        out FlowPackageContent flowContent))
                {
                    AddIssue(issues, location, "Manifest validation failed; see BepInEx/LogOutput.log.");
                    continue;
                }

                validCandidates.Add(new PackageCandidate
                {
                    Location = location,
                    Manifest = manifest,
                    FlowContent = flowContent
                });
            }
            catch (Exception ex)
            {
                _log.LogError("Failed to load mod manifest " + manifestPath + ": " + ex);
                AddIssue(issues, location, "Manifest could not be loaded: " + ex.Message);
            }
        }

        List<ModPackage> found = new List<ModPackage>();
        foreach (IGrouping<string, PackageCandidate> group in validCandidates.GroupBy(
                     candidate => candidate.Manifest.id,
                     StringComparer.OrdinalIgnoreCase))
        {
            PackageCandidate[] candidates = group.ToArray();
            if (candidates.Length > 1)
            {
                string paths = string.Join(", ", candidates.Select(candidate => candidate.Location.RootPath));
                _log.LogError("Duplicate mod id disabled: " + group.Key + " at " + paths);
                foreach (PackageCandidate duplicateCandidate in candidates)
                {
                    AddIssue(
                        issues,
                        duplicateCandidate.Location,
                        "Duplicate Mod id '" + group.Key + "'. Remove or disable the conflicting copy.");
                }

                continue;
            }

            PackageCandidate candidate = candidates[0];
            try
            {
                ModManifest manifest = candidate.Manifest;
                DefaultsManifest defaults = manifest.defaults ?? new DefaultsManifest();
                bool isManaged = candidate.Location.SourceKind == ModPackageSourceKind.Local &&
                                 File.Exists(Path.Combine(candidate.Location.RootPath, ".sunny-installed.json"));
                bool defaultEnabled = isManaged || candidate.Location.SourceKind == ModPackageSourceKind.SteamWorkshop
                    ? false
                    : defaults.enabled;
                string sourceIdentity = isManaged
                    ? "managed:" + manifest.id
                    : candidate.Location.SourceIdentity;
                string section = "Mod:" + manifest.id;
                ConfigEntry<string> sourceFingerprint = _config.Bind(
                    section,
                    "SourceFingerprint",
                    string.Empty,
                    "Loader-owned package source identity; changing source disables the Mod until reviewed.");
                string previousSourceIdentity = sourceFingerprint.Value;
                bool hadSourceFingerprint = !string.IsNullOrEmpty(previousSourceIdentity);
                bool sourceChanged = hadSourceFingerprint &&
                                     !string.Equals(
                                         previousSourceIdentity,
                                         sourceIdentity,
                                         StringComparison.OrdinalIgnoreCase);
                ConfigEntry<bool> enabledEntry = _config.Bind(
                    section,
                    "Enabled",
                    defaultEnabled,
                    "Enable this mod.");
                if (sourceChanged ||
                    (!hadSourceFingerprint &&
                     (isManaged || candidate.Location.SourceKind == ModPackageSourceKind.SteamWorkshop)))
                {
                    enabledEntry.Value = false;
                }
                sourceFingerprint.Value = sourceIdentity;

                ModPackage package = new ModPackage
                {
                    RootPath = candidate.Location.RootPath,
                    Manifest = manifest,
                    SourceKind = candidate.Location.SourceKind,
                    SourceDescription = candidate.Location.SourceDescription,
                    SourceIdentity = sourceIdentity,
                    IsManaged = isManaged,
                    Enabled = enabledEntry,
                    Priority = _config.Bind(
                        section,
                        "Priority",
                        defaults.priority,
                        "Higher values win conflicts.")
                };
                package.Flows.AddRange(candidate.FlowContent.Flows);
                package.DialoguePatches = candidate.FlowContent.DialoguePatches.ToArray();
                package.Branches = candidate.FlowContent.Branches.ToArray();
                package.Gallery = candidate.FlowContent.Gallery.ToArray();
                package.Overlays = candidate.FlowContent.Overlays.ToArray();
                package.Sprites = candidate.FlowContent.Sprites.ToArray();
                package.Spines = candidate.FlowContent.Spines.ToArray();
                if (manifest.settings != null)
                {
                    foreach (BoolSettingManifest setting in manifest.settings)
                    {
                        if (setting == null || string.IsNullOrWhiteSpace(setting.id) ||
                            package.BoolSettings.ContainsKey(setting.id))
                        {
                            continue;
                        }

                        package.BoolSettings[setting.id] = _config.Bind(
                            "Setting:" + manifest.id,
                            setting.id,
                            setting.defaultValue,
                            setting.label ?? setting.id);
                    }
                }

                LoadRuntimeStateFromConfiguration(package);
                _byId.Add(manifest.id, package);
                found.Add(package);
            }
            catch (Exception ex)
            {
                _log.LogError("Failed to initialize Mod configuration for " + candidate.Manifest.id + ": " + ex);
                AddIssue(issues, candidate.Location, "Mod configuration could not be initialized: " + ex.Message);
            }
        }

        ModPackage builtInVoiceControl = CreateBuiltInVoiceControlPackage();
        _byId.Add(builtInVoiceControl.Id, builtInVoiceControl);
        found.Add(builtInVoiceControl);
        _all = found.OrderBy(package => package.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        _issues = issues;
        RebuildActiveOrder();
        _log.LogInfo(
            "Discovered " + _all.Count + " mod package(s); " + _activeLowToHigh.Count +
            " enabled; " + _issues.Count + " scan issue(s).");
    }

    internal void RebuildActiveOrder()
    {
        _activeLowToHigh = ModRelationshipService.ResolveActivePackages(_all, out List<ModScanIssue> relationshipIssues);
        relationshipIssues.AddRange(ModRelationshipService.AnalyzeOverrides(_activeLowToHigh));
        IReadOnlyList<ModScanIssue> runtimeIssues = RuntimeContentCoordinator.Rebuild(this);
        _issues.RemoveAll(issue => issue != null && issue.IsRuntime);
        _issues.AddRange(relationshipIssues);
        _issues.AddRange(runtimeIssues);
    }

    internal bool IsRuntimeActive(ModPackage package)
    {
        return package != null && _activeLowToHigh.Contains(package);
    }

    internal Dictionary<string, ModUserState> CaptureConfiguredStates()
    {
        Dictionary<string, ModUserState> result =
            new Dictionary<string, ModUserState>(StringComparer.OrdinalIgnoreCase);
        foreach (ModPackage package in _all)
        {
            ModUserState state = new ModUserState
            {
                Enabled = package.Enabled.Value,
                Priority = package.Priority.Value
            };
            foreach (KeyValuePair<string, ConfigEntry<bool>> setting in package.BoolSettings)
            {
                state.BoolSettings[setting.Key] = setting.Value.Value;
            }

            result[package.Id] = state;
        }

        return result;
    }

    internal Dictionary<string, ModUserState> CaptureRuntimeStates()
    {
        Dictionary<string, ModUserState> result =
            new Dictionary<string, ModUserState>(StringComparer.OrdinalIgnoreCase);
        foreach (ModPackage package in _all)
        {
            ModUserState state = new ModUserState
            {
                Enabled = package.RuntimeEnabled,
                Priority = package.RuntimePriority
            };
            foreach (KeyValuePair<string, bool> setting in package.RuntimeBoolSettings)
            {
                state.BoolSettings[setting.Key] = setting.Value;
            }

            result[package.Id] = state;
        }

        return result;
    }

    internal void ApplyUserStates(IReadOnlyDictionary<string, ModUserState> states, bool writeConfiguration)
    {
        if (states == null)
        {
            return;
        }

        foreach (ModPackage package in _all)
        {
            if (!states.TryGetValue(package.Id, out ModUserState state) || state == null)
            {
                continue;
            }

            package.RuntimeEnabled = state.Enabled;
            package.RuntimePriority = state.Priority;
            if (writeConfiguration)
            {
                package.Enabled.Value = state.Enabled;
                package.Priority.Value = state.Priority;
            }

            foreach (KeyValuePair<string, ConfigEntry<bool>> setting in package.BoolSettings)
            {
                if (!state.BoolSettings.TryGetValue(setting.Key, out bool value))
                {
                    value = setting.Value.Value;
                }

                package.RuntimeBoolSettings[setting.Key] = value;
                if (writeConfiguration)
                {
                    setting.Value.Value = value;
                }
            }
        }

        if (writeConfiguration)
        {
            _config.Save();
        }

        RebuildActiveOrder();
    }

    internal bool TryGetPackage(string modId, out ModPackage package)
    {
        return _byId.TryGetValue(modId ?? string.Empty, out package);
    }

    internal bool TryResolveAsset(string logicalPath, AssetKind kind, out string fullPath)
    {
        fullPath = null;
        if (TryParseModUri(logicalPath, out string modId, out string relativePath))
        {
            if (!_byId.TryGetValue(modId, out ModPackage package) || !IsRuntimeActive(package))
            {
                _log.LogWarning("Asset requested from missing or disabled mod: " + logicalPath);
                return false;
            }

            return TryResolveWithExtensions(package, relativePath, kind, out fullPath);
        }

        string target = LoaderUtil.NormalizeResourceKey(logicalPath);
        foreach (ModPackage package in ActiveHighToLow)
        {
            OverlayDefinition[] overlays = package.Overlays;
            if (overlays == null)
            {
                continue;
            }

            foreach (OverlayDefinition overlay in overlays)
            {
                if (overlay == null || !KindMatches(overlay.kind, kind) ||
                    !string.Equals(LoaderUtil.NormalizeResourceKey(overlay.target), target, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryResolveWithExtensions(package, overlay.source, kind, out fullPath))
                {
                    return true;
                }

                _log.LogError("Overlay source is missing: " + package.Id + ":" + overlay.source);
            }
        }

        return false;
    }

    internal bool TryGetFlow(string logicalPath, out FlowDefinition flow)
    {
        flow = null;
        if (!LoaderUtil.TryParseModUri(logicalPath, out string modId, out string relativePath) ||
            !_byId.TryGetValue(modId, out ModPackage package) || !IsRuntimeActive(package))
        {
            return false;
        }

        string normalized = relativePath.Replace('\\', '/').TrimStart('/');
        flow = package.Flows.FirstOrDefault(candidate =>
            string.Equals(candidate.RelativePath, normalized, StringComparison.OrdinalIgnoreCase));
        return flow != null;
    }

    internal bool TryGetVoiceVolume(string logicalPath, out float volume)
    {
        if (_voiceVolumes.TryGetValue(logicalPath ?? string.Empty, out volume))
        {
            return true;
        }

        volume = 1f;
        return false;
    }

    internal void RegisterVoiceVolume(string logicalPath, float volume)
    {
        if (!string.IsNullOrWhiteSpace(logicalPath))
        {
            _voiceVolumes[logicalPath] = Mathf.Clamp01(volume);
        }
    }

    internal bool GetBoolSetting(ModPackage package, string settingId, bool defaultValue = true)
    {
        if (package == null || string.IsNullOrWhiteSpace(settingId))
        {
            return defaultValue;
        }

        return package.RuntimeBoolSettings.TryGetValue(settingId, out bool value) ? value : defaultValue;
    }

    internal void SyncSettings(CorePlayer player)
    {
        if (player == null)
        {
            return;
        }

        foreach (ModPackage package in _all)
        {
            player.SetVariable(
                "mod_" + LoaderUtil.SafeId(package.Id) + "_enabled",
                IsRuntimeActive(package) ? "1" : "0");
            foreach (KeyValuePair<string, bool> setting in package.RuntimeBoolSettings)
            {
                string key = "mod_" + LoaderUtil.SafeId(package.Id) + "_" + LoaderUtil.SafeId(setting.Key);
                player.SetVariable(key, setting.Value ? "1" : "0");
            }
        }
    }

    private IEnumerable<PackageLocation> EnumeratePackageDirectories(List<ModScanIssue> issues)
    {
        List<PackageLocation> result = new List<PackageLocation>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddContainer(Path.Combine(Paths.GameRootPath, "Mods"), ModPackageSourceKind.Local, "Local Mods", false);

        string configuredRoots = _additionalRoots.Value;
        if (!string.IsNullOrWhiteSpace(configuredRoots))
        {
            foreach (string configuredRoot in configuredRoots.Split(
                         new[] { ';' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                AddContainer(
                    Environment.ExpandEnvironmentVariables(configuredRoot.Trim()),
                    ModPackageSourceKind.AdditionalRoot,
                    "Additional root",
                    true);
            }
        }

        foreach (WorkshopPackageLocation workshopPackage in _workshopDiscovery.Discover())
        {
            AddPackage(
                workshopPackage.RootPath,
                ModPackageSourceKind.SteamWorkshop,
                workshopPackage.SourceDescription,
                "steam:" + workshopPackage.AppId.ToString(CultureInfo.InvariantCulture) + ":" +
                workshopPackage.PublishedFileId.ToString(CultureInfo.InvariantCulture));
        }

        return result;

        void AddContainer(string value, ModPackageSourceKind sourceKind, string sourceDescription, bool acceptRootPackage)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            string root;
            try
            {
                root = Path.GetFullPath(value);
            }
            catch (Exception ex)
            {
                issues.Add(new ModScanIssue
                {
                    RootPath = value,
                    SourceDescription = sourceDescription,
                    Message = "Root path is invalid: " + ex.Message
                });
                return;
            }

            try
            {
                if (!Directory.Exists(root))
                {
                    return;
                }

                if (IsReparsePoint(root))
                {
                    issues.Add(new ModScanIssue
                    {
                        RootPath = root,
                        SourceDescription = sourceDescription,
                        Message = "Scan root is a symbolic link or reparse point."
                    });
                    return;
                }

                if (acceptRootPackage && File.Exists(Path.Combine(root, "manifest.json")))
                {
                    AddPackage(root, sourceKind, sourceDescription, null);
                    return;
                }

                if (!acceptRootPackage && File.Exists(Path.Combine(root, "manifest.json")))
                {
                    issues.Add(new ModScanIssue
                    {
                        RootPath = root,
                        SourceDescription = sourceDescription,
                        Message = "manifest.json at the Mods container root is ignored; place each Mod in its own directory."
                    });
                }

                string[] directories = Directory.GetDirectories(root);
                Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
                foreach (string directory in directories)
                {
                    string name = Path.GetFileName(directory);
                    if (sourceKind == ModPackageSourceKind.Local &&
                        (string.Equals(name, "Inbox", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(name, ".sunny", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    AddPackage(directory, sourceKind, sourceDescription, null);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("Mod root could not be scanned " + root + ": " + ex.Message);
                issues.Add(new ModScanIssue
                {
                    RootPath = root,
                    SourceDescription = sourceDescription,
                    Message = "Root could not be scanned: " + ex.Message
                });
            }
        }

        void AddPackage(
            string value,
            ModPackageSourceKind sourceKind,
            string sourceDescription,
            string sourceIdentity)
        {
            try
            {
                string fullPath = NormalizeDirectoryPath(value);
                if (seen.Add(fullPath))
                {
                    result.Add(new PackageLocation
                    {
                        RootPath = fullPath,
                        SourceKind = sourceKind,
                        SourceDescription = sourceDescription,
                        SourceIdentity = sourceIdentity ??
                                         sourceKind.ToString().ToLowerInvariant() + ":" + fullPath
                    });
                }
            }
            catch (Exception ex)
            {
                issues.Add(new ModScanIssue
                {
                    RootPath = value,
                    SourceDescription = sourceDescription,
                    Message = "Package path is invalid: " + ex.Message
                });
            }
        }
    }

    private bool TryResolveWithExtensions(ModPackage package, string relativePath, AssetKind kind, out string fullPath)
    {
        if (package?.IsBuiltIn == true)
        {
            fullPath = null;
            return false;
        }
        if (LoaderUtil.TryResolvePackageFile(package.RootPath, relativePath, out fullPath))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(Path.GetExtension(relativePath ?? string.Empty)))
        {
            return false;
        }

        foreach (string extension in AssetPolicy.GetExtensions(kind))
        {
            if (LoaderUtil.TryResolvePackageFile(package.RootPath, relativePath + extension, out fullPath))
            {
                return true;
            }
        }

        return false;
    }

    private ModPackage CreateBuiltInVoiceControlPackage()
    {
        const string section = "Mod:" + BuiltInVoiceControlId;
        ConfigEntry<string> sourceFingerprint = _config.Bind(
            section,
            "SourceFingerprint",
            BuiltInVoiceControlSource,
            "Loader-owned package source identity.");
        sourceFingerprint.Value = BuiltInVoiceControlSource;

        ModPackage package = new ModPackage
        {
            RootPath = string.Empty,
            Manifest = new ModManifest
            {
                schemaVersion = 2,
                id = BuiltInVoiceControlId,
                name = "VoiceControl",
                version = SunnyModLoaderPlugin.PluginVersion,
                authors = new[] { "Sunny Mod Loader" },
                compatibility = new CompatibilityManifest
                {
                    loaderApi = LoaderApiVersion,
                    gameBuilds = new[] { _gameBuild }
                },
                defaults = new DefaultsManifest { enabled = true, priority = 1000 }
            },
            SourceKind = ModPackageSourceKind.BuiltIn,
            SourceDescription = "随 Sunny Mod Loader 提供",
            SourceIdentity = BuiltInVoiceControlSource,
            IsManaged = false,
            Enabled = _config.Bind(section, "Enabled", true, "Enable the built-in VoiceControl component."),
            Priority = _config.Bind(section, "Priority", 1000, "Internal VoiceControl priority."),
            AudioControl = AudioControlService.CreateBuiltInDefinition(BuiltInVoiceControlId)
        };
        package.Priority.Value = 1000;
        LoadRuntimeStateFromConfiguration(package);
        return package;
    }

    private static void LoadRuntimeStateFromConfiguration(ModPackage package)
    {
        package.RuntimeEnabled = package.Enabled.Value;
        package.RuntimePriority = package.Priority.Value;
        package.RuntimeBoolSettings.Clear();
        foreach (KeyValuePair<string, ConfigEntry<bool>> setting in package.BoolSettings)
        {
            package.RuntimeBoolSettings[setting.Key] = setting.Value.Value;
        }
    }

    private static void AddIssue(List<ModScanIssue> issues, PackageLocation location, string message)
    {
        issues.Add(new ModScanIssue
        {
            RootPath = location.RootPath,
            SourceDescription = location.SourceDescription,
            Message = message
        });
    }

    private static bool IsReparsePoint(string path)
    {
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static string NormalizeDirectoryPath(string value)
    {
        string fullPath = Path.GetFullPath(value);
        string root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool TryParseModUri(string value, out string modId, out string relativePath)
    {
        return LoaderUtil.TryParseModUri(value, out modId, out relativePath);
    }

    private static bool KindMatches(string manifestKind, AssetKind requested)
    {
        if (string.IsNullOrWhiteSpace(manifestKind) || requested == AssetKind.Any)
        {
            return true;
        }

        return Enum.TryParse(manifestKind, true, out AssetKind parsed) &&
               (parsed == AssetKind.Any || parsed == requested);
    }

    private sealed class PackageLocation
    {
        internal string RootPath;
        internal ModPackageSourceKind SourceKind;
        internal string SourceDescription;
        internal string SourceIdentity;
    }

    private sealed class PackageCandidate
    {
        internal PackageLocation Location;
        internal ModManifest Manifest;
        internal FlowPackageContent FlowContent;
    }
}
