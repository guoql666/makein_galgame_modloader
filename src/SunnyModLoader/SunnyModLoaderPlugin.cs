using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using MakeineGalGameQM.Core;
using MakeineGalGameQM.Core.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SunnyModLoader;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class SunnyModLoaderPlugin : BaseUnityPlugin
{
    internal const string PluginGuid = "qm.sunny.modloader";
    internal const string PluginName = "Sunny Mod Loader";
    internal const string PluginVersion = "1.1.1";

    internal static ManualLogSource Log { get; private set; }
    internal static ModRegistry Registry { get; private set; }
    internal static VoiceService Voice { get; private set; }
    internal static ScreenEffectService ScreenEffects { get; private set; }
    internal static bool ManagerVisible { get; private set; }
    internal static bool ShouldBlockGameAdvance => ManagerVisible && IsManagerInputActive();

    private static readonly string[] ManagerTabs = { "Mod", "安装", "问题", "恢复" };
    private static readonly KeyCode[] ManagerKeyboardKeys = Enum.GetValues(typeof(KeyCode))
        .Cast<KeyCode>()
        .Where(IsManagerKeyboardKey)
        .ToArray();
    private const string ArchivePathControlName = "SunnyModLoader.ArchivePath";

    private Harmony _harmony;
    private GameObject _managerRaycastBlocker;
    private RectTransform _managerRaycastRect;
    private ConfigEntry<KeyCode> _managerKey;
    private ModInstallerService _installer;
    private Dictionary<string, ModUserState> _editingStates;
    private IReadOnlyList<ModInboxArchiveInfo> _inboxArchives = Array.Empty<ModInboxArchiveInfo>();
    private IReadOnlyList<ModManagedPackageInfo> _trashEntries = Array.Empty<ModManagedPackageInfo>();
    private IReadOnlyList<ModManagedPackageInfo> _backupEntries = Array.Empty<ModManagedPackageInfo>();
    private Task<ModInstallerResult> _installerTask;
    private bool _showManager;
    private bool _stateChangesPending;
    private bool _contentReloadPending;
    private int _managerTab;
    private Rect _windowRect = new Rect(24f, 24f, 680f, 700f);
    private Vector2 _scroll;
    private string _archivePath = string.Empty;
    private string _status = string.Empty;
    private string _confirmAction = string.Empty;
    private static Rect _managerInputRect;
    private static bool _managerKeyboardFocus;
    private static bool _managerPointerCaptured;
    private static int _managerInputFrame = -1;
    private static bool _managerInputBlockedThisFrame;
    private bool _clearManagerGuiFocus;

    private bool HasPendingChanges => _stateChangesPending || _contentReloadPending;
    private bool InstallerBusy => _installerTask != null;

    private void Awake()
    {
        Log = Logger;
        Directory.CreateDirectory(Path.Combine(Paths.GameRootPath, "Mods"));
        Directory.CreateDirectory(Paths.PluginPath);

        _managerKey = Config.Bind("UI", "ManagerKey", KeyCode.F8, "Open the mod manager.");
        string buildGuid = ReadBuildGuid();
        _installer = new ModInstallerService(Logger, buildGuid, ModRegistry.LoaderApiVersion);
        Registry = new ModRegistry(Config, Logger, buildGuid);
        Registry.Scan();
        RefreshEditingStatesFromConfiguration();
        Voice = new VoiceService(this);
        ScreenEffects = new ScreenEffectService(this);

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll(typeof(SunnyModLoaderPlugin).Assembly);
        StartCoroutine(RunSaveCompatibilityMigration());
        Logger.LogInfo(
            PluginName + " " + PluginVersion + " loaded for build " + buildGuid +
            ". Press F8 for the mod manager.");

        if (StartupDiagnostics.IsRequested || StartupDiagnostics.IsInstallerValidationRequested)
        {
            StartCoroutine(RunStartupValidation());
        }

    }

    private void Update()
    {
        CompleteInstallerOperation();
        if (Input.GetKeyDown(_managerKey.Value))
        {
            SetManagerVisible(!_showManager);
        }
    }

    private void OnGUI()
    {
        if (_clearManagerGuiFocus)
        {
            GUI.FocusControl(null);
            _clearManagerGuiFocus = false;
        }
        if (!_showManager)
        {
            return;
        }

        NormalizeManagerWindowRect();
        if (Event.current.type == EventType.MouseDown && !_windowRect.Contains(Event.current.mousePosition))
        {
            GUI.FocusControl(null);
        }

        _windowRect = GUI.Window(7162401, _windowRect, DrawManagerWindow, "Sunny Mod Loader");
        NormalizeManagerWindowRect();
        _managerInputRect = _windowRect;
        _managerKeyboardFocus = string.Equals(
            GUI.GetNameOfFocusedControl(),
            ArchivePathControlName,
            StringComparison.Ordinal);
        UpdateManagerRaycastBlocker();
    }

    private void OnDestroy()
    {
        AudioControlService.RestoreDisplayPreferencesForDiagnostics();
        DestroyManagerRaycastBlocker();
        ManagerVisible = false;
        _managerKeyboardFocus = false;
        _managerPointerCaptured = false;
        _managerInputFrame = -1;
        Voice?.Stop();
        ScreenEffects?.Shutdown();
        SpriteService.Shutdown();
        _harmony?.UnpatchSelf();
    }

    private void DrawManagerWindow(int windowId)
    {
        GUILayout.BeginVertical();
        GUILayout.Label(
            "已发现: " + Registry.All.Count +
            "    当前启用: " + Registry.All.Count(package => package.RuntimeEnabled) +
            "    扫描问题: " + Registry.Issues.Count);
        int selectedTab = GUILayout.Toolbar(_managerTab, ManagerTabs);
        if (selectedTab != _managerTab)
        {
            GUI.FocusControl(null);
            _managerTab = selectedTab;
            _scroll = Vector2.zero;
            _confirmAction = string.Empty;
        }

        _scroll = GUILayout.BeginScrollView(_scroll, false, true);
        switch (_managerTab)
        {
            case 0:
                DrawPackages();
                break;
            case 1:
                DrawInstaller();
                break;
            case 2:
                DrawScanIssues();
                break;
            case 3:
                DrawRecovery();
                break;
        }

        GUILayout.EndScrollView();
        DrawManagerFooter();
        if (!string.IsNullOrEmpty(_status))
        {
            GUIStyle statusStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
            GUILayout.Label(_status, statusStyle);
        }

        GUILayout.EndVertical();
        GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
    }

    private void DrawPackages()
    {
        if (Registry.All.Count == 0)
        {
            GUILayout.Label("没有可用的 Mod。可在安装页导入包，或查看问题页。 ");
            return;
        }

        bool changed = false;
        foreach (ModPackage package in Registry.All)
        {
            ModUserState state = GetEditingState(package);
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            bool enabled = GUILayout.Toggle(state.Enabled, package.DisplayName, GUILayout.ExpandWidth(true));
            if (enabled != state.Enabled)
            {
                state.Enabled = enabled;
                changed = true;
            }

            GUILayout.Label("v" + (package.Manifest.version ?? "?"), GUILayout.Width(96f));
            GUILayout.EndHorizontal();

            GUIStyle metadataStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
            GUILayout.Label(GetSourceLabel(package), metadataStyle);

            if (!package.IsBuiltIn)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("优先级", GUILayout.Width(58f));
                if (GUILayout.Button("-", GUILayout.Width(32f)))
                {
                    state.Priority--;
                    changed = true;
                }
                GUILayout.Label(state.Priority.ToString(), GUILayout.Width(48f));
                if (GUILayout.Button("+", GUILayout.Width(32f)))
                {
                    state.Priority++;
                    changed = true;
                }
                GUILayout.FlexibleSpace();

                if (package.IsManaged)
                {
                    bool canUninstall = !InstallerBusy && !package.RuntimeEnabled;
                    GUI.enabled = canUninstall;
                    string actionKey = "uninstall:" + package.Id;
                    string buttonText = _confirmAction == actionKey ? "确认卸载" : "卸载";
                    if (GUILayout.Button(buttonText, GUILayout.Width(82f)))
                    {
                        if (_confirmAction == actionKey)
                        {
                            _confirmAction = string.Empty;
                            StartManagedUninstall(package);
                        }
                        else
                        {
                            _confirmAction = actionKey;
                        }
                    }
                    GUI.enabled = true;
                }

                GUILayout.EndHorizontal();
            }

            if (package.IsManaged && package.RuntimeEnabled)
            {
                GUILayout.Label("卸载前需先禁用并应用。", metadataStyle);
            }

            if (state.Enabled && package.Manifest.settings != null)
            {
                foreach (BoolSettingManifest setting in package.Manifest.settings)
                {
                    if (setting == null || !state.BoolSettings.TryGetValue(setting.id, out bool value))
                    {
                        continue;
                    }

                    bool nextValue = GUILayout.Toggle(value, setting.label ?? setting.id);
                    if (nextValue != value)
                    {
                        state.BoolSettings[setting.id] = nextValue;
                        changed = true;
                    }
                }
            }

            GUILayout.EndVertical();
        }

        if (changed)
        {
            UpdatePendingStateFlag();
        }
    }

    private void DrawInstaller()
    {
        GUILayout.Label("本地包路径");
        GUI.SetNextControlName(ArchivePathControlName);
        _archivePath = GUILayout.TextField(_archivePath ?? string.Empty);
        GUI.enabled = !InstallerBusy && !string.IsNullOrWhiteSpace(_archivePath);
        if (GUILayout.Button("从路径安装/更新"))
        {
            string archivePath = _archivePath;
            StartInstallerOperation(
                () => _installer.InstallArchive(archivePath),
                "正在校验并安装 " + Path.GetFileName(archivePath) + "...");
        }
        GUI.enabled = true;
        GUILayout.Space(10f);

        GUILayout.BeginHorizontal();
        GUILayout.Label("Mods/Inbox", GUILayout.ExpandWidth(true));
        GUI.enabled = !InstallerBusy;
        if (GUILayout.Button("刷新", GUILayout.Width(72f)))
        {
            RefreshInstallerLists();
            _status = "安装列表已刷新";
        }
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        if (_inboxArchives.Count == 0)
        {
            GUILayout.Label("Inbox 中没有 .sunmod 或 .zip 包。 ");
            return;
        }

        foreach (ModInboxArchiveInfo archive in _inboxArchives)
        {
            GUILayout.BeginHorizontal(GUI.skin.box);
            GUIStyle nameStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
            GUILayout.Label(archive.FileName + "  (" + FormatBytes(archive.Length) + ")", nameStyle, GUILayout.ExpandWidth(true));
            GUI.enabled = !InstallerBusy;
            if (GUILayout.Button("安装/更新", GUILayout.Width(92f)))
            {
                StartInstallerOperation(
                    () => _installer.InstallFromInbox(archive.FileName),
                    "正在校验并安装 " + archive.FileName + "...");
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }
    }

    private void DrawScanIssues()
    {
        if (Registry.Issues.Count == 0)
        {
            GUILayout.Label("当前没有扫描问题。 ");
            return;
        }

        GUIStyle wrapStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
        foreach (ModScanIssue issue in Registry.Issues)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label(issue.SourceDescription ?? "未知来源");
            GUILayout.Label(issue.Message ?? "未知错误", wrapStyle);
            GUILayout.Label(issue.RootPath ?? string.Empty, wrapStyle);
            GUILayout.EndVertical();
        }
    }

    private void DrawRecovery()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("已卸载，可恢复", GUILayout.ExpandWidth(true));
        GUI.enabled = !InstallerBusy;
        if (GUILayout.Button("刷新", GUILayout.Width(72f)))
        {
            RefreshInstallerLists();
            _status = "恢复列表已刷新";
        }
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        if (_trashEntries.Count == 0)
        {
            GUILayout.Label("无");
        }
        else
        {
            foreach (ModManagedPackageInfo entry in _trashEntries)
            {
                DrawStoredPackage(entry, false);
            }
        }

        GUILayout.Space(10f);
        GUILayout.Label("更新备份");
        if (_backupEntries.Count == 0)
        {
            GUILayout.Label("无");
            return;
        }

        foreach (ModManagedPackageInfo entry in _backupEntries)
        {
            DrawStoredPackage(entry, true);
        }
    }

    private void DrawStoredPackage(ModManagedPackageInfo entry, bool backup)
    {
        GUILayout.BeginHorizontal(GUI.skin.box);
        GUIStyle labelStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
        GUILayout.Label(entry.ModId + "  v" + entry.Version, labelStyle, GUILayout.ExpandWidth(true));

        bool currentInstalled = Registry.TryGetPackage(entry.ModId, out ModPackage current);
        bool currentEnabled = currentInstalled && current.RuntimeEnabled;
        GUI.enabled = !InstallerBusy && !currentEnabled && (backup || !currentInstalled);
        if (GUILayout.Button("恢复", GUILayout.Width(64f)))
        {
            StartInstallerOperation(
                () => backup
                    ? _installer.RestoreBackup(entry.EntryName)
                    : _installer.RestoreTrash(entry.EntryName),
                "正在恢复 " + entry.ModId + "...");
        }

        if (backup)
        {
            GUI.enabled = !InstallerBusy;
            string actionKey = "delete-backup:" + entry.EntryName;
            string buttonText = _confirmAction == actionKey ? "确认删除" : "删除";
            if (GUILayout.Button(buttonText, GUILayout.Width(82f)))
            {
                if (_confirmAction == actionKey)
                {
                    _confirmAction = string.Empty;
                    StartInstallerOperation(
                        () => _installer.DeleteBackup(entry.EntryName),
                        "正在删除备份 " + entry.ModId + "...");
                }
                else
                {
                    _confirmAction = actionKey;
                }
            }
        }

        GUI.enabled = true;
        GUILayout.EndHorizontal();
        if (currentEnabled)
        {
            GUILayout.Label("恢复旧版本前需先禁用并应用。", labelStyle);
        }
        else if (!backup && currentInstalled)
        {
            GUILayout.Label("同 ID 的 Mod 已安装，不能从回收区覆盖。", labelStyle);
        }
    }

    private void DrawManagerFooter()
    {
        GUILayout.BeginHorizontal();
        GUI.enabled = !InstallerBusy;
        if (GUILayout.Button("重新扫描"))
        {
            RescanPackages();
        }

        GUI.enabled = !InstallerBusy && _stateChangesPending;
        if (GUILayout.Button("放弃编辑"))
        {
            _editingStates = CloneStates(Registry.CaptureRuntimeStates());
            _stateChangesPending = false;
            _status = "未应用的启停与设置改动已放弃";
        }

        GUI.enabled = !InstallerBusy && HasPendingChanges;
        if (GUILayout.Button("应用到当前剧情"))
        {
            ApplyToCurrentScene();
        }

        GUI.enabled = true;
        if (GUILayout.Button("关闭"))
        {
            SetManagerVisible(false);
        }
        GUILayout.EndHorizontal();
    }

    private void ApplyToCurrentScene()
    {
        CorePlayer player = UnityEngine.Object.FindFirstObjectByType<CorePlayer>();
        Dictionary<string, ModUserState> oldConfigured = Registry.CaptureConfiguredStates();
        Dictionary<string, ModUserState> oldRuntime = Registry.CaptureRuntimeStates();
        bool requiresSceneReload = RequiresSceneReloadForStateChanges(
            Registry.All,
            oldRuntime,
            _editingStates,
            _contentReloadPending);
        if (!requiresSceneReload)
        {
            try
            {
                Registry.ApplyUserStates(_editingStates, true);
                RefreshEditingStatesFromConfiguration();
                _contentReloadPending = false;
                _status = "内置功能设置已应用";
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
                try
                {
                    Registry.ApplyUserStates(oldConfigured, true);
                    Registry.ApplyUserStates(oldRuntime, false);
                }
                catch (Exception rollbackException)
                {
                    Logger.LogError("Failed to restore the previous built-in Mod state: " + rollbackException);
                }
                UpdatePendingStateFlag();
                _status = "应用失败，已尝试恢复原状态；详情见 BepInEx 日志";
            }
            return;
        }

        if (player != null && IsModScene(player.currentScene, out string currentModId))
        {
            _status = "正在 Mod 剧情 " + currentModId + " 中，返回原剧情后才能应用数据 Mod 改动";
            return;
        }

        if (player == null || string.IsNullOrWhiteSpace(player.currentScene))
        {
            try
            {
                Registry.ApplyUserStates(_editingStates, true);
                RefreshEditingStatesFromConfiguration();
                _contentReloadPending = false;
                _status = "设置已保存，将用于下一段剧情";
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
                try
                {
                    Registry.ApplyUserStates(oldConfigured, true);
                }
                catch (Exception configurationRollbackException)
                {
                    Logger.LogError("Failed to restore the previous Mod configuration: " +
                                    configurationRollbackException);
                }

                try
                {
                    Registry.ApplyUserStates(oldRuntime, false);
                }
                catch (Exception runtimeRollbackException)
                {
                    Logger.LogError("Failed to restore the previous Mod runtime state: " + runtimeRollbackException);
                }

                _status = "保存失败，已尝试恢复原状态；详情见 BepInEx 日志";
            }

            return;
        }

        bool wasPlaying = player.isPlaying;
        var snapshot = player.CreateSnapshot();
        try
        {
            Registry.ApplyUserStates(_editingStates, false);
            player.ReloadSceneScriptsFromDisk(player.currentScene);
            player.RestoreSnapshot(snapshot, wasPlaying);
            Registry.ApplyUserStates(_editingStates, true);
            Registry.SyncSettings(player);
            RefreshEditingStatesFromConfiguration();
            _contentReloadPending = false;
            _status = "已应用";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
            try
            {
                Registry.ApplyUserStates(oldConfigured, true);
                Registry.ApplyUserStates(oldRuntime, false);
                player.ReloadSceneScriptsFromDisk(player.currentScene);
                player.RestoreSnapshot(snapshot, wasPlaying);
                Registry.SyncSettings(player);
            }
            catch (Exception rollbackException)
            {
                Logger.LogError("Failed to restore the previous Mod runtime state: " + rollbackException);
            }

            UpdatePendingStateFlag();
            _status = "应用失败，已尝试恢复原状态；详情见 BepInEx 日志";
        }
    }

    private void RescanPackages()
    {
        if (_stateChangesPending)
        {
            _status = "请先应用或放弃当前编辑，再重新扫描";
            return;
        }

        CorePlayer player = UnityEngine.Object.FindFirstObjectByType<CorePlayer>();
        if (player != null && IsModScene(player.currentScene, out string currentModId))
        {
            _status = "正在 Mod 剧情 " + currentModId + " 中，返回原剧情后才能重新扫描";
            return;
        }

        Registry.Scan();
        RefreshEditingStatesFromConfiguration();
        RefreshInstallerLists();
        _contentReloadPending = player != null && !string.IsNullOrWhiteSpace(player.currentScene);
        _status = "扫描完成";
    }

    private void StartManagedUninstall(ModPackage package)
    {
        if (package == null || !package.IsManaged)
        {
            return;
        }

        if (package.RuntimeEnabled)
        {
            _status = "请先禁用 " + package.DisplayName + " 并应用";
            return;
        }

        StartInstallerOperation(
            () => _installer.UninstallManaged(package.Id),
            "正在卸载 " + package.DisplayName + "...");
    }

    private void StartInstallerOperation(Func<ModInstallerResult> operation, string status)
    {
        if (InstallerBusy)
        {
            return;
        }

        if (_stateChangesPending)
        {
            _status = "请先应用或放弃当前编辑，再执行文件操作";
            return;
        }

        CorePlayer player = UnityEngine.Object.FindFirstObjectByType<CorePlayer>();
        if (player != null && IsModScene(player.currentScene, out string currentModId))
        {
            _status = "正在 Mod 剧情 " + currentModId + " 中，返回原剧情后才能改动文件";
            return;
        }

        _confirmAction = string.Empty;
        _status = status;
        _installerTask = Task.Run(operation);
    }

    private void CompleteInstallerOperation()
    {
        if (_installerTask == null || !_installerTask.IsCompleted)
        {
            return;
        }

        Task<ModInstallerResult> completed = _installerTask;
        _installerTask = null;
        ModInstallerResult result;
        try
        {
            result = completed.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.LogError("Installer operation failed unexpectedly: " + ex);
            _status = "安装器发生异常，详情见 BepInEx 日志";
            RefreshInstallerLists();
            return;
        }

        if (result == null || !result.Success)
        {
            _status = "操作失败: " + (result?.Error ?? "未知错误");
            RefreshInstallerLists();
            return;
        }

        bool wasRuntimeEnabled = Registry.TryGetPackage(result.ModId, out ModPackage previousPackage) &&
                                 previousPackage.RuntimeEnabled;
        if (result.Operation == ModInstallerOperation.Install && !result.IsUpdate &&
            !string.IsNullOrWhiteSpace(result.ModId))
        {
            ConfigEntry<bool> enabled = Config.Bind(
                "Mod:" + result.ModId,
                "Enabled",
                false,
                "Enable this mod.");
            enabled.Value = false;
            Config.Save();
        }

        if (result.Operation != ModInstallerOperation.DeleteBackup)
        {
            Registry.Scan();
        }

        bool isRuntimeEnabledAfter = Registry.TryGetPackage(result.ModId, out ModPackage currentPackage) &&
                                     currentPackage.RuntimeEnabled;

        RefreshEditingStatesFromConfiguration();
        RefreshInstallerLists();
        CorePlayer player = UnityEngine.Object.FindFirstObjectByType<CorePlayer>();
        if ((wasRuntimeEnabled || isRuntimeEnabledAfter) &&
            player != null && !string.IsNullOrWhiteSpace(player.currentScene) &&
            (result.Operation == ModInstallerOperation.Install ||
             result.Operation == ModInstallerOperation.RestoreBackup ||
             result.Operation == ModInstallerOperation.RestoreTrash))
        {
            _contentReloadPending = true;
        }

        string verb = result.Operation switch
        {
            ModInstallerOperation.Install => result.IsUpdate ? "已更新" : "已安装（默认禁用）",
            ModInstallerOperation.Uninstall => "已卸载，可在恢复页找回",
            ModInstallerOperation.RestoreTrash => "已恢复",
            ModInstallerOperation.RestoreBackup => "已恢复旧版本",
            ModInstallerOperation.DeleteBackup => "备份已删除",
            _ => "操作完成"
        };
        _status = verb + (string.IsNullOrWhiteSpace(result.ModId) ? string.Empty : ": " + result.ModId);
    }

    private void RefreshInstallerLists()
    {
        _inboxArchives = _installer.EnumerateInbox();
        _trashEntries = _installer.EnumerateTrash();
        _backupEntries = _installer.EnumerateBackups();
    }

    private void RefreshEditingStatesFromConfiguration()
    {
        _editingStates = CloneStates(Registry.CaptureConfiguredStates());
        UpdatePendingStateFlag();
    }

    private void UpdatePendingStateFlag()
    {
        _stateChangesPending = !StatesEqual(_editingStates, Registry.CaptureRuntimeStates());
    }

    private ModUserState GetEditingState(ModPackage package)
    {
        if (_editingStates.TryGetValue(package.Id, out ModUserState state))
        {
            return state;
        }

        state = new ModUserState
        {
            Enabled = package.Enabled.Value,
            Priority = package.Priority.Value
        };
        foreach (KeyValuePair<string, ConfigEntry<bool>> setting in package.BoolSettings)
        {
            state.BoolSettings[setting.Key] = setting.Value.Value;
        }

        _editingStates[package.Id] = state;
        return state;
    }

    private static Dictionary<string, ModUserState> CloneStates(
        IReadOnlyDictionary<string, ModUserState> states)
    {
        Dictionary<string, ModUserState> clone =
            new Dictionary<string, ModUserState>(StringComparer.OrdinalIgnoreCase);
        if (states == null)
        {
            return clone;
        }

        foreach (KeyValuePair<string, ModUserState> state in states)
        {
            clone[state.Key] = state.Value?.Clone() ?? new ModUserState();
        }

        return clone;
    }

    private static bool StatesEqual(
        IReadOnlyDictionary<string, ModUserState> left,
        IReadOnlyDictionary<string, ModUserState> right)
    {
        if (left == null || right == null || left.Count != right.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, ModUserState> item in left)
        {
            if (!right.TryGetValue(item.Key, out ModUserState other) || item.Value == null || other == null ||
                item.Value.Enabled != other.Enabled || item.Value.Priority != other.Priority ||
                item.Value.BoolSettings.Count != other.BoolSettings.Count)
            {
                return false;
            }

            foreach (KeyValuePair<string, bool> setting in item.Value.BoolSettings)
            {
                if (!other.BoolSettings.TryGetValue(setting.Key, out bool otherValue) || otherValue != setting.Value)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool StatesEqualForPackage(
        IReadOnlyDictionary<string, ModUserState> left,
        IReadOnlyDictionary<string, ModUserState> right,
        string packageId)
    {
        if (left == null || right == null || string.IsNullOrWhiteSpace(packageId) ||
            !left.TryGetValue(packageId, out ModUserState leftState) ||
            !right.TryGetValue(packageId, out ModUserState rightState) ||
            leftState == null || rightState == null ||
            leftState.Enabled != rightState.Enabled || leftState.Priority != rightState.Priority ||
            leftState.BoolSettings.Count != rightState.BoolSettings.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, bool> setting in leftState.BoolSettings)
        {
            if (!rightState.BoolSettings.TryGetValue(setting.Key, out bool otherValue) ||
                otherValue != setting.Value)
            {
                return false;
            }
        }
        return true;
    }

    internal static bool RequiresSceneReloadForStateChanges(
        IEnumerable<ModPackage> packages,
        IReadOnlyDictionary<string, ModUserState> current,
        IReadOnlyDictionary<string, ModUserState> edited,
        bool contentReloadPending)
    {
        return contentReloadPending || (packages ?? Enumerable.Empty<ModPackage>())
            .Where(package => package != null && !package.IsBuiltIn)
            .Any(package => !StatesEqualForPackage(current, edited, package.Id));
    }

    private static string GetSourceLabel(ModPackage package)
    {
        if (package.IsBuiltIn)
        {
            return "Loader 内置功能  |  随 Sunny Mod Loader 一同更新";
        }
        string kind = package.SourceKind switch
        {
            ModPackageSourceKind.SteamWorkshop => "Steam 工坊（外部托管）",
            ModPackageSourceKind.AdditionalRoot => "额外目录（外部托管）",
            _ => package.IsManaged ? "Sunny 安装器管理" : "本地手动目录"
        };
        string source = string.IsNullOrWhiteSpace(package.SourceDescription)
            ? string.Empty
            : "  |  " + package.SourceDescription;
        return kind + "  |  " + package.Id + source + "\n" + package.RootPath;
    }

    private static bool IsModScene(string scene, out string modId)
    {
        modId = null;
        const string Prefix = "mod://";
        if (string.IsNullOrWhiteSpace(scene) || !scene.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string remainder = scene.Substring(Prefix.Length);
        int separator = remainder.IndexOf('/');
        modId = separator > 0 ? remainder.Substring(0, separator) : remainder;
        return true;
    }

    private static string FormatBytes(long value)
    {
        if (value >= 1024L * 1024L * 1024L)
        {
            return (value / (1024d * 1024d * 1024d)).ToString("0.0") + " GiB";
        }

        if (value >= 1024L * 1024L)
        {
            return (value / (1024d * 1024d)).ToString("0.0") + " MiB";
        }

        if (value >= 1024L)
        {
            return (value / 1024d).ToString("0.0") + " KiB";
        }

        return value + " B";
    }

    private void SetManagerVisible(bool visible)
    {
        if (!visible && InstallerBusy)
        {
            _status = "文件操作尚未完成，完成后才能关闭管理器";
            return;
        }

        if (!visible && _contentReloadPending)
        {
            _status = "Mod 内容已经变化，请先应用到当前剧情";
            return;
        }

        if (_showManager == visible)
        {
            return;
        }

        _showManager = visible;
        _managerPointerCaptured = false;
        _managerInputFrame = -1;
        if (visible)
        {
            NormalizeManagerWindowRect();
            ManagerVisible = true;
            CreateManagerRaycastBlocker();
            _managerInputRect = _windowRect;
            UpdateManagerRaycastBlocker();

            if (_editingStates == null)
            {
                RefreshEditingStatesFromConfiguration();
            }
            RefreshInstallerLists();
        }
        if (!visible)
        {
            DestroyManagerRaycastBlocker();
            ManagerVisible = false;
            _managerKeyboardFocus = false;
            _clearManagerGuiFocus = true;
        }
    }

    private void NormalizeManagerWindowRect()
    {
        _windowRect.width = Mathf.Min(680f, Mathf.Max(440f, Screen.width - 48f));
        _windowRect.height = Mathf.Min(700f, Mathf.Max(360f, Screen.height - 48f));
        _windowRect.x = Mathf.Clamp(_windowRect.x, 0f, Mathf.Max(0f, Screen.width - _windowRect.width));
        _windowRect.y = Mathf.Clamp(_windowRect.y, 0f, Mathf.Max(0f, Screen.height - _windowRect.height));
    }

    private void CreateManagerRaycastBlocker()
    {
        if (_managerRaycastBlocker != null)
        {
            return;
        }

        _managerRaycastBlocker = new GameObject(
            "SunnyModLoader Window Input Blocker",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(GraphicRaycaster));
        DontDestroyOnLoad(_managerRaycastBlocker);

        Canvas canvas = _managerRaycastBlocker.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue;

        GameObject blockerImage = new GameObject(
            "Window Rect",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image));
        blockerImage.transform.SetParent(_managerRaycastBlocker.transform, false);
        _managerRaycastRect = blockerImage.GetComponent<RectTransform>();
        _managerRaycastRect.anchorMin = new Vector2(0f, 1f);
        _managerRaycastRect.anchorMax = new Vector2(0f, 1f);
        _managerRaycastRect.pivot = new Vector2(0f, 1f);

        Image image = blockerImage.GetComponent<Image>();
        image.color = Color.clear;
        image.raycastTarget = true;
        UpdateManagerRaycastBlocker();
    }

    private void UpdateManagerRaycastBlocker()
    {
        if (_managerRaycastRect == null)
        {
            return;
        }

        _managerRaycastRect.anchoredPosition = new Vector2(_windowRect.x, -_windowRect.y);
        _managerRaycastRect.sizeDelta = new Vector2(_windowRect.width, _windowRect.height);
    }

    private void DestroyManagerRaycastBlocker()
    {
        if (_managerRaycastBlocker == null)
        {
            return;
        }

        Destroy(_managerRaycastBlocker);
        _managerRaycastBlocker = null;
        _managerRaycastRect = null;
    }

    private static bool IsManagerInputActive()
    {
        if (_managerInputFrame == Time.frameCount)
        {
            return _managerInputBlockedThisFrame;
        }

        _managerInputFrame = Time.frameCount;
        Vector3 mousePosition = Input.mousePosition;
        Vector2 guiPosition = new Vector2(mousePosition.x, Screen.height - mousePosition.y);
        bool pointerPressed = Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2);
        bool pointerHeld = Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);
        bool pointerReleased = Input.GetMouseButtonUp(0) || Input.GetMouseButtonUp(1) || Input.GetMouseButtonUp(2);
        bool keyboardKeyDown = _managerKeyboardFocus && IsManagerKeyboardInputDown();
        _managerInputBlockedThisFrame = ShouldBlockManagerInput(
            _managerInputRect,
            guiPosition,
            pointerPressed,
            pointerHeld,
            pointerReleased,
            Input.mouseScrollDelta.sqrMagnitude > 0f,
            _managerKeyboardFocus,
            keyboardKeyDown,
            ref _managerPointerCaptured);
        return _managerInputBlockedThisFrame;
    }

    private static bool IsManagerKeyboardInputDown()
    {
        foreach (KeyCode key in ManagerKeyboardKeys)
        {
            if (Input.GetKeyDown(key))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsManagerKeyboardKey(KeyCode key)
    {
        if (key == KeyCode.None || key == KeyCode.Mouse0 || key == KeyCode.Mouse1 ||
            key == KeyCode.Mouse2 || key == KeyCode.Mouse3 || key == KeyCode.Mouse4 ||
            key == KeyCode.Mouse5 || key == KeyCode.Mouse6 || key == KeyCode.WheelUp ||
            key == KeyCode.WheelDown)
        {
            return false;
        }

        return !key.ToString().StartsWith("Joystick", StringComparison.Ordinal);
    }

    internal static bool ShouldBlockManagerInput(
        Rect windowRect,
        Vector2 pointerPosition,
        bool pointerPressed,
        bool pointerHeld,
        bool pointerReleased,
        bool scrollInput,
        bool keyboardFocus,
        bool keyDown,
        ref bool pointerCaptured)
    {
        bool pointerInside = windowRect.Contains(pointerPosition);
        if (pointerPressed && pointerInside)
        {
            pointerCaptured = true;
        }

        bool pointerEvent = pointerPressed || pointerHeld || pointerReleased;
        bool block = pointerCaptured && pointerEvent || pointerInside && scrollInput || keyboardFocus && keyDown;

        if (pointerCaptured &&
            ((pointerReleased && !pointerHeld) ||
             (!pointerPressed && !pointerHeld && !pointerReleased)))
        {
            pointerCaptured = false;
        }

        return block;
    }

    private static string ReadBuildGuid()
    {
        try
        {
            IEnumerable<string> dataDirectories = new[] { Application.dataPath }
                .Concat(Directory.GetDirectories(Paths.GameRootPath, "*_Data"))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (string dataDirectory in dataDirectories)
            {
                string bootConfig = Path.Combine(dataDirectory, "boot.config");
                if (!File.Exists(bootConfig))
                {
                    continue;
                }

                foreach (string line in File.ReadAllLines(bootConfig))
                {
                    if (line.StartsWith("build-guid=", StringComparison.OrdinalIgnoreCase))
                    {
                        return line.Substring("build-guid=".Length).Trim();
                    }
                }
            }
        }
        catch
        {
        }

        return "unknown";
    }

    private static IEnumerator RunStartupValidation()
    {
        const int MaxWaitFrames = 300;
        int waitedFrames = 0;
        while (DebugHelper.Instance == null && waitedFrames++ < MaxWaitFrames)
        {
            yield return null;
        }

        if (DebugHelper.Instance == null)
        {
            Log.LogError("Startup validation timed out waiting for the game's DebugHelper.");
        }
        else
        {
            if (StartupDiagnostics.ResolutionIndexToApply >= 0 &&
                AudioControlService.TryApplyResolutionForDiagnostics(
                    StartupDiagnostics.ResolutionIndexToApply))
            {
                yield return new WaitForSecondsRealtime(1f);
                AudioControlService.RefreshSettingsUiLayout(SettingManager.Instance);
                Canvas.ForceUpdateCanvases();
                Log.LogInfo(
                    "Startup validation applied temporary windowed resolution index " +
                    StartupDiagnostics.ResolutionIndexToApply + " (" + Screen.width + "x" + Screen.height + ").");
            }

            try
            {
                StartupDiagnostics.Run(Registry);
            }
            catch (Exception ex)
            {
                Log.LogError("Startup validation failed unexpectedly: " + ex);
            }

            float voiceVolumeBeforeSettingsOpen = SunnyModAudioControl.VoiceVolume;
            bool stopVoiceBeforeSettingsOpen = SunnyModAudioControl.StopVoiceOnAdvance;
            if (!string.IsNullOrWhiteSpace(StartupDiagnostics.SettingsPageToOpen) &&
                AudioControlService.TryOpenSettingsForDiagnostics(StartupDiagnostics.SettingsPageToOpen))
            {
                yield return null;
                Canvas.ForceUpdateCanvases();
                bool valuesStable = Mathf.Approximately(
                                        voiceVolumeBeforeSettingsOpen,
                                        SunnyModAudioControl.VoiceVolume) &&
                                    stopVoiceBeforeSettingsOpen == SunnyModAudioControl.StopVoiceOnAdvance;
                bool uiStable = AudioControlService.ValidateForDiagnostics(out string openDetail);
                if (valuesStable && uiStable)
                {
                    Log.LogInfo(
                        "Startup validation opened settings page " + StartupDiagnostics.SettingsPageToOpen +
                        " without changing audio preferences: " + openDetail + ".");
                }
                else
                {
                    Log.LogError(
                        "Startup validation settings page changed audio preferences or invalidated its UI: " +
                        openDetail + ", valuesStable=" + valuesStable + ".");
                }
            }

            foreach (string textureUri in StartupDiagnostics.GetTextureUris(Registry))
            {
                Texture2D texture = GalAssetLoader.LoadTexture(textureUri);
                if (texture == null)
                {
                    Log.LogError("Startup validation could not decode Mod texture: " + textureUri);
                    continue;
                }

                Log.LogInfo(
                    "Startup validation decoded Mod texture " + textureUri + " (" +
                    texture.width + "x" + texture.height + ").");
                UnityEngine.Object.Destroy(texture);
            }

            foreach (string audioUri in StartupDiagnostics.GetAudioUris(Registry))
            {
                AudioClip loadedClip = null;
                yield return GalAssetLoader.LoadAudioClipAsync(audioUri, clip => loadedClip = clip);
                if (loadedClip == null)
                {
                    Log.LogError("Startup validation could not decode Mod audio: " + audioUri);
                    continue;
                }

                Log.LogInfo(
                    "Startup validation decoded Mod audio " + audioUri + " (" +
                    loadedClip.samples + " samples at " + loadedClip.frequency + " Hz).");
                UnityEngine.Object.Destroy(loadedClip);
            }

            if (StartupDiagnostics.IsBacklogUiRequested)
            {
                Log.LogInfo("Startup validation is loading the gameplay scene for live backlog UI validation.");
                SceneManager.LoadScene("Editor");
                yield return new WaitForSecondsRealtime(2f);
            }
        }

        if (StartupDiagnostics.ShouldQuit)
        {
            if (AudioControlService.RestoreDisplayPreferencesForDiagnostics())
            {
                yield return new WaitForSecondsRealtime(0.25f);
                Log.LogInfo("Startup validation restored the player's saved display preferences.");
            }
            yield return new WaitForSecondsRealtime(1f);
            Log.LogInfo("Startup validation finished; quitting cleanly.");
            FlushLogListeners();
            Application.Quit();
        }
    }

    private static IEnumerator RunSaveCompatibilityMigration()
    {
        const int MaxWaitFrames = 300;
        int waitedFrames = 0;
        while (DebugHelper.Instance == null && waitedFrames++ < MaxWaitFrames)
        {
            yield return null;
        }

        yield return null;
        try
        {
            ModSaveCompatibilityService.MigrateLegacySaves();
        }
        catch (Exception ex)
        {
            Log.LogWarning("Could not run Mod save compatibility migration: " + ex);
        }
    }

    private static void FlushLogListeners()
    {
        foreach (ILogListener listener in BepInEx.Logging.Logger.Listeners.ToArray())
        {
            try
            {
                System.Reflection.PropertyInfo writerProperty = listener.GetType().GetProperty(
                    "LogWriter",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                (writerProperty?.GetValue(listener) as TextWriter)?.Flush();
            }
            catch
            {
            }
        }
    }

}
