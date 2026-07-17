using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using MakeineGalGameQM.Core;
using MakeineGalGameQM.Core.Character;
using MakeineGalGameQM.Core.SaveAndLoad;
using UnityEngine;

namespace SunnyModLoader;

internal static class ModSaveCompatibilityService
{
    private const int SidecarSchemaVersion = 1;
    private const long MaxSidecarBytes = 128L * 1024L * 1024L;
    private const string SidecarSuffix = ".sunny-mod.json";
    private static int _canonicalWriteDepth;

    internal static bool ProtectBeforeSave(SavedData data)
    {
        return ProtectBeforeSave(data, null);
    }

    private static bool ProtectBeforeSave(SavedData data, string legacySaveJson)
    {
        if (_canonicalWriteDepth > 0 || data == null || string.IsNullOrWhiteSpace(data.SavePath))
        {
            return true;
        }

        string currentScene = GetCurrentScene(data);
        if (!LoaderUtil.TryParseModUri(currentScene, out _, out _))
        {
            return true;
        }

        CorePlayer player = SaveManager.Instance?.player;
        GameStateSnapshot actualSnapshot = GetActualSnapshot(data);
        if (!BranchService.TryCreateVanillaFallbackSnapshot(
                player,
                actualSnapshot,
                out GameStateSnapshot fallback,
                out BranchFrame frame))
        {
            int frameCount = BranchService.ReadFrames(actualSnapshot).Length;
            int variableCount = actualSnapshot?.Player?.Variables?.Count ?? 0;
            SunnyModLoaderPlugin.Log.LogError(
                "Refused to overwrite save " + data.SaveIndex +
                " because Mod scene " + currentScene + " has no loadable original return frame " +
                "(frames=" + frameCount + ", variables=" + variableCount + ").");
            return false;
        }

        ModSaveSidecar sidecar = CaptureSidecar(data, actualSnapshot, fallback, legacySaveJson);
        try
        {
            StageSidecar(data.SavePath, sidecar);
        }
        catch (Exception ex)
        {
            SunnyModLoaderPlugin.Log.LogError(
                "Could not preserve Mod progress for save " + data.SaveIndex + ": " + ex.Message);
            return false;
        }

        ApplyVanillaFallback(data, fallback);
        SunnyModLoaderPlugin.Log.LogInfo(
            "Protected Mod save " + data.SaveIndex + " with original fallback " +
            frame.scene + ":" + frame.returnIndex + ".");
        return true;
    }

    internal static LoadSwapState PrepareLoad(SavedData data)
    {
        if (data == null)
        {
            return null;
        }

        data.EnsurePayloadLoaded();
        if (LoaderUtil.TryParseModUri(GetCurrentScene(data), out _, out _) && ProtectBeforeSave(data))
        {
            WriteCanonical(data);
            CommitSidecar(data.SavePath);
        }

        if (!TryReadSidecar(data, out ModSaveSidecar sidecar) || !CanRestoreModProgress(sidecar))
        {
            return null;
        }

        LoadSwapState state = new LoadSwapState(data);
        ApplySidecar(data, sidecar);
        SunnyModLoaderPlugin.Log.LogInfo(
            "Restoring Mod progress for save " + data.SaveIndex + " at " + sidecar.ActualScriptName + ".");
        return state;
    }

    internal static void RestoreAfterLoad(SavedData data, LoadSwapState state)
    {
        state?.Restore(data);
    }

    internal static void DeleteForSaveIndex(int saveIndex)
    {
        DeleteSidecar(SaveManager.GetSaveFilePath(saveIndex));
    }

    internal static bool IsModSaveTarget(SavedData data)
    {
        return LoaderUtil.TryParseModUri(GetCurrentScene(data), out _, out _);
    }

    internal static void CompleteSave(SavedData data, bool wasModSaveTarget)
    {
        if (_canonicalWriteDepth > 0 || data == null)
        {
            return;
        }

        if (!wasModSaveTarget)
        {
            DeleteSidecar(data.SavePath);
            return;
        }

        try
        {
            CommitSidecar(data.SavePath);
        }
        catch (Exception ex)
        {
            SunnyModLoaderPlugin.Log.LogError(
                "Could not commit Mod progress sidecar for save " + data.SaveIndex + ": " + ex.Message);
        }
    }

    internal static void MigrateLegacySaves()
    {
        string directory = SaveManager.GetSaveDirectory();
        if (!Directory.Exists(directory))
        {
            return;
        }

        RecoverPendingSidecars(directory);

        int migrated = 0;
        foreach (string path in Directory.GetFiles(directory, "save_*.json", SearchOption.TopDirectoryOnly))
        {
            if (path.EndsWith(SidecarSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                string legacySaveJson = File.ReadAllText(path, Encoding.UTF8);
                RawNavigationFile raw = JsonCodec.Deserialize<RawNavigationFile>(legacySaveJson);
                SavedData data = SavedData.LoadMeta(path);
                if (data == null)
                {
                    continue;
                }

                if (raw?.Payload?.StateSnapshot?.Player != null)
                {
                    data.Options = raw.Payload.Options != null
                        ? new List<string>(raw.Payload.Options)
                        : new List<string>();
                    data.Variables = ToDictionary(raw.Payload.Variables);
                    data.BackLogHistory = new List<MessageEntry>();
                    data.BackLogSnapshotTable = new List<GameStateSnapshot>();
                    data.BackLogRecords = new List<BackLogSnapshotRecord>();
                    data.StateSnapshot = raw.Payload.StateSnapshot.ToSnapshot();
                }
                if (LoaderUtil.TryParseModUri(GetCurrentScene(data), out _, out _))
                {
                    if (!ProtectBeforeSave(data, legacySaveJson))
                    {
                        continue;
                    }

                    WriteCanonical(data);
                    CommitSidecar(data.SavePath);
                    migrated++;
                    continue;
                }

                if (File.Exists(GetSidecarPath(path)) && !TryReadSidecar(data, out _))
                {
                    DeleteSidecar(path);
                }
            }
            catch (Exception ex)
            {
                SunnyModLoaderPlugin.Log.LogWarning(
                    "Could not inspect save compatibility for " + Path.GetFileName(path) + ": " + ex);
            }
        }

        foreach (string sidecarPath in Directory.GetFiles(
                     directory,
                     "save_*" + SidecarSuffix,
                     SearchOption.TopDirectoryOnly))
        {
            string savePath = sidecarPath.Substring(0, sidecarPath.Length - SidecarSuffix.Length) + ".json";
            if (!File.Exists(savePath))
            {
                TryDeleteFile(sidecarPath);
            }
        }

        if (migrated > 0)
        {
            SunnyModLoaderPlugin.Log.LogInfo(
                "Migrated " + migrated + " legacy Mod save(s) to vanilla-compatible primary files.");
        }
    }

    internal static bool ValidateForDiagnostics(out string detail)
    {
        const string BaseScene = "Script/vol1.txt";
        BranchFramePayload frames = new BranchFramePayload
        {
            frames = new[]
            {
                new BranchFrame
                {
                    modId = "diagnostic.outer",
                    branchId = "base",
                    frameId = "base-frame",
                    scene = BaseScene,
                    returnIndex = 17,
                    background = new BranchBackgroundState { path = "mod://missing/background.png", scale = 1f }
                },
                new BranchFrame
                {
                    modId = "diagnostic.inner",
                    branchId = "nested",
                    frameId = "nested-frame",
                    scene = "mod://diagnostic.outer/story/outer.sunny",
                    returnIndex = 4
                }
            }
        };
        GameStateSnapshot current = new GameStateSnapshot
        {
            Player = new CorePlayerSnapshot
            {
                CurrentScene = "mod://diagnostic.inner/story/inner.sunny",
                CurrentScriptName = "mod://diagnostic.inner/story/inner.sunny",
                CurrentScriptIndex = 9,
                Variables = new List<SerializableStringEntry>
                {
                    new SerializableStringEntry
                    {
                        Key = "__sunny_modloader_frames",
                        Value = JsonCodec.Serialize(frames)
                    }
                }
            }
        };

        if (!BranchService.TryCreateVanillaFallbackSnapshot(null, current, out GameStateSnapshot fallback, out _) ||
            fallback?.Player == null ||
            !string.Equals(fallback.Player.CurrentScene, BaseScene, StringComparison.OrdinalIgnoreCase) ||
            fallback.Player.CurrentScriptIndex != 17)
        {
            detail = "nested original fallback selection failed";
            return false;
        }

        SanitizeFallbackSnapshot(fallback);
        if (fallback.Background != null || fallback.Player.Variables.Any(entry =>
                entry != null && string.Equals(entry.Key, "__sunny_modloader_frames", StringComparison.Ordinal)))
        {
            detail = "fallback resource or return-stack sanitization failed";
            return false;
        }

        ModSaveSidecar expected = new ModSaveSidecar
        {
            SchemaVersion = SidecarSchemaVersion,
            SaveIndex = 7,
            BaseSaveTimeStr = "diagnostic",
            BaseScriptName = BaseScene,
            BaseScriptIndex = 17,
            ActualScriptName = current.Player.CurrentScene,
            ActualScriptIndex = current.Player.CurrentScriptIndex,
            StateSnapshot = current
        };
        string json = JsonUtility.ToJson(expected);
        ModSaveSidecar actual = JsonUtility.FromJson<ModSaveSidecar>(json);
        bool valid = actual?.StateSnapshot?.Player != null && actual.SchemaVersion == SidecarSchemaVersion &&
                     string.Equals(actual.ActualScriptName, expected.ActualScriptName, StringComparison.Ordinal);
        if (!valid)
        {
            detail = "sidecar codec failed";
            return false;
        }

        return ValidateExistingSidecarsForDiagnostics(out detail);
    }

    private static bool ValidateExistingSidecarsForDiagnostics(out string detail)
    {
        string directory = SaveManager.GetSaveDirectory();
        if (!Directory.Exists(directory))
        {
            detail = "nested fallback and sidecar codec; no existing saves";
            return true;
        }

        int checkedCount = 0;
        foreach (string sidecarPath in Directory.GetFiles(
                     directory,
                     "save_*" + SidecarSuffix,
                     SearchOption.TopDirectoryOnly))
        {
            string savePath = sidecarPath.Substring(0, sidecarPath.Length - SidecarSuffix.Length) + ".json";
            SavedData data = SavedData.LoadMeta(savePath);
            if (data == null || !TryReadSidecar(data, out ModSaveSidecar sidecar))
            {
                detail = "existing sidecar did not match its primary save";
                return false;
            }

            string baseScene = data.scriptName;
            bool shouldRestore = CanRestoreModProgress(sidecar);
            LoadSwapState state = null;
            try
            {
                state = PrepareLoad(data);
                bool restored = state != null && string.Equals(
                    GetCurrentScene(data),
                    sidecar.StateSnapshot.Player.CurrentScene,
                    StringComparison.Ordinal);
                if (restored != shouldRestore)
                {
                    detail = "existing sidecar availability selection failed";
                    return false;
                }
            }
            finally
            {
                RestoreAfterLoad(data, state);
            }

            if (shouldRestore &&
                LoaderUtil.TryParseModUri(sidecar.StateSnapshot.Player.CurrentScene, out string modId, out _) &&
                SunnyModLoaderPlugin.Registry.TryGetPackage(modId, out ModPackage package))
            {
                bool wasEnabled = package.RuntimeEnabled;
                try
                {
                    package.RuntimeEnabled = false;
                    LoadSwapState disabledState = PrepareLoad(data);
                    if (disabledState != null || !string.Equals(data.scriptName, baseScene, StringComparison.Ordinal))
                    {
                        RestoreAfterLoad(data, disabledState);
                        detail = "disabled Mod did not select the original fallback";
                        return false;
                    }
                }
                finally
                {
                    package.RuntimeEnabled = wasEnabled;
                }
            }

            if (!string.Equals(data.scriptName, baseScene, StringComparison.Ordinal))
            {
                detail = "existing sidecar load swap did not restore primary metadata";
                return false;
            }
            checkedCount++;
        }

        detail = "nested fallback and sidecar codec; existing sidecars=" + checkedCount;
        return true;
    }

    private static ModSaveSidecar CaptureSidecar(
        SavedData data,
        GameStateSnapshot actualSnapshot,
        GameStateSnapshot fallback,
        string legacySaveJson)
    {
        return new ModSaveSidecar
        {
            SchemaVersion = SidecarSchemaVersion,
            SaveIndex = data.SaveIndex,
            BaseSaveTimeStr = data.SaveTimeStr ?? string.Empty,
            BaseScriptName = fallback.Player.CurrentScene ?? string.Empty,
            BaseScriptIndex = fallback.Player.CurrentScriptIndex,
            ActualSaveContent = data.SaveContent ?? string.Empty,
            ActualScriptName = data.scriptName ?? string.Empty,
            ActualScriptIndex = data.scriptIndex,
            Options = data.Options != null ? new List<string>(data.Options) : new List<string>(),
            Variables = ToEntries(data.Variables),
            BackLogHistory = CloneHistory(data.BackLogHistory),
            BackLogSnapshotTable = CloneSnapshots(data.BackLogSnapshotTable),
            BackLogRecords = CloneRecords(data.BackLogRecords),
            StateSnapshot = actualSnapshot?.Clone(),
            LegacySaveJson = legacySaveJson ?? string.Empty
        };
    }

    private static void ApplyVanillaFallback(SavedData data, GameStateSnapshot fallback)
    {
        GameStateSnapshot safe = fallback.Clone();
        SanitizeFallbackSnapshot(safe);
        data.scriptName = safe.Player.CurrentScene ?? string.Empty;
        data.scriptIndex = safe.Player.CurrentScriptIndex;
        data.Options = safe.Player.CurrentSceneOptions != null
            ? new List<string>(safe.Player.CurrentSceneOptions)
            : new List<string>();
        data.Variables = ToDictionary(safe.Player.Variables);
        data.BackLogHistory = new List<MessageEntry>();
        data.BackLogSnapshotTable = new List<GameStateSnapshot>();
        data.BackLogRecords = new List<BackLogSnapshotRecord>();
        data.StateSnapshot = safe;
    }

    private static void ApplySidecar(SavedData data, ModSaveSidecar sidecar)
    {
        data.SaveContent = sidecar.ActualSaveContent ?? data.SaveContent;
        data.scriptName = sidecar.ActualScriptName ?? string.Empty;
        data.scriptIndex = sidecar.ActualScriptIndex;
        data.Options = sidecar.Options != null ? new List<string>(sidecar.Options) : new List<string>();
        data.Variables = ToDictionary(sidecar.Variables);
        data.BackLogHistory = CloneHistory(sidecar.BackLogHistory);
        data.BackLogSnapshotTable = CloneSnapshots(sidecar.BackLogSnapshotTable);
        data.BackLogRecords = CloneRecords(sidecar.BackLogRecords);
        data.StateSnapshot = sidecar.StateSnapshot?.Clone();
        RestoreLegacyPayload(sidecar, data);
    }

    private static void SanitizeFallbackSnapshot(GameStateSnapshot snapshot)
    {
        if (snapshot?.Player == null)
        {
            return;
        }

        snapshot.Player.IsPlaying = false;
        snapshot.Player.Variables?.RemoveAll(entry => entry != null &&
                                                      string.Equals(
                                                          entry.Key,
                                                          "__sunny_modloader_frames",
                                                          StringComparison.Ordinal));
        if (IsModReference(snapshot.Background?.Path))
        {
            snapshot.Background = null;
        }
        if (IsModReference(snapshot.Music?.Path))
        {
            snapshot.Music = null;
        }

        snapshot.Characters = snapshot.Characters?
            .Where(character => character != null && IsVanillaCharacter(character.Name) &&
                                !IsModReference(character.Illustration) &&
                                !IsModReference(character.Emotion) &&
                                !IsModReference(character.Animation))
            .Select(character => character.Clone())
            .ToList() ?? new List<CharacterStateSnapshot>();
        if (snapshot.Dialogue != null &&
            !string.IsNullOrWhiteSpace(snapshot.Dialogue.SpeakerCharacterId) &&
            !IsVanillaCharacter(snapshot.Dialogue.SpeakerCharacterId))
        {
            snapshot.Dialogue.SpeakerCharacterId = string.Empty;
        }
    }

    private static bool IsVanillaCharacter(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || SpriteService.IsKnownCharacter(name))
        {
            return false;
        }

        try
        {
            return CharacterSpineIllustrationManager.GetConfig(name) != null ||
                   CharacterIllustrationManager.GetConfig(name) != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool CanRestoreModProgress(ModSaveSidecar sidecar)
    {
        if (sidecar?.StateSnapshot?.Player == null || SunnyModLoaderPlugin.Registry == null ||
            !IsAvailableModScene(sidecar.StateSnapshot.Player.CurrentScene))
        {
            return false;
        }

        foreach (BranchFrame frame in BranchService.ReadFrames(sidecar.StateSnapshot))
        {
            if (frame != null && LoaderUtil.TryParseModUri(frame.scene, out _, out _) &&
                !IsAvailableModScene(frame.scene))
            {
                return false;
            }
        }

        return IsAvailableModResource(sidecar.StateSnapshot.Background?.Path) &&
               IsAvailableModResource(sidecar.StateSnapshot.Music?.Path);
    }

    private static bool IsAvailableModScene(string scene)
    {
        return SunnyModLoaderPlugin.Registry.TryGetFlow(scene, out _);
    }

    private static bool IsAvailableModResource(string reference)
    {
        if (!LoaderUtil.TryParseModUri(reference, out string modId, out _))
        {
            return true;
        }

        return SunnyModLoaderPlugin.Registry.TryGetPackage(modId, out ModPackage package) && package.RuntimeEnabled;
    }

    private static bool TryReadSidecar(SavedData data, out ModSaveSidecar sidecar)
    {
        string path = GetSidecarPath(data?.SavePath);
        return TryReadSidecar(data, path, out sidecar);
    }

    private static bool TryReadSidecar(SavedData data, string path, out ModSaveSidecar sidecar)
    {
        sidecar = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            FileInfo info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaxSidecarBytes)
            {
                return false;
            }

            sidecar = JsonUtility.FromJson<ModSaveSidecar>(File.ReadAllText(path, Encoding.UTF8));
            return sidecar != null && sidecar.SchemaVersion == SidecarSchemaVersion &&
                   sidecar.SaveIndex == data.SaveIndex &&
                   string.Equals(sidecar.BaseSaveTimeStr, data.SaveTimeStr ?? string.Empty, StringComparison.Ordinal) &&
                   string.Equals(sidecar.BaseScriptName, data.scriptName ?? string.Empty, StringComparison.Ordinal) &&
                   sidecar.BaseScriptIndex == data.scriptIndex &&
                   sidecar.StateSnapshot?.Player != null &&
                   LoaderUtil.TryParseModUri(sidecar.StateSnapshot.Player.CurrentScene, out _, out _);
        }
        catch (Exception ex)
        {
            SunnyModLoaderPlugin.Log.LogWarning(
                "Could not read Mod save sidecar " + Path.GetFileName(path) + ": " + ex.Message);
            sidecar = null;
            return false;
        }
    }

    private static void StageSidecar(string savePath, ModSaveSidecar sidecar)
    {
        string path = GetSidecarPath(savePath);
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string pendingPath = path + ".pending";
        string tempPath = pendingPath + ".tmp";
        File.WriteAllText(tempPath, JsonUtility.ToJson(sidecar, true), new UTF8Encoding(false));
        if (File.Exists(pendingPath))
        {
            File.Delete(pendingPath);
        }
        File.Move(tempPath, pendingPath);
    }

    private static void CommitSidecar(string savePath)
    {
        string path = GetSidecarPath(savePath);
        string pendingPath = path + ".pending";
        if (!File.Exists(pendingPath))
        {
            return;
        }

        string backupPath = path + ".backup";
        TryDeleteFile(backupPath);
        if (File.Exists(path))
        {
            File.Move(path, backupPath);
        }

        try
        {
            File.Move(pendingPath, path);
            TryDeleteFile(backupPath);
        }
        catch
        {
            if (!File.Exists(path) && File.Exists(backupPath))
            {
                File.Move(backupPath, path);
            }
            throw;
        }
    }

    private static void RecoverPendingSidecars(string directory)
    {
        foreach (string pendingPath in Directory.GetFiles(
                     directory,
                     "save_*" + SidecarSuffix + ".pending",
                     SearchOption.TopDirectoryOnly))
        {
            string sidecarPath = pendingPath.Substring(0, pendingPath.Length - ".pending".Length);
            string savePath = sidecarPath.Substring(0, sidecarPath.Length - SidecarSuffix.Length) + ".json";
            SavedData data = SavedData.LoadMeta(savePath);
            if (data != null && TryReadSidecar(data, pendingPath, out _))
            {
                CommitSidecar(savePath);
                continue;
            }

            TryDeleteFile(pendingPath);
            RestoreSidecarBackup(sidecarPath);
        }

        foreach (string backupPath in Directory.GetFiles(
                     directory,
                     "save_*" + SidecarSuffix + ".backup",
                     SearchOption.TopDirectoryOnly))
        {
            string sidecarPath = backupPath.Substring(0, backupPath.Length - ".backup".Length);
            if (File.Exists(sidecarPath))
            {
                TryDeleteFile(backupPath);
            }
            else
            {
                File.Move(backupPath, sidecarPath);
            }
        }
    }

    private static void RestoreSidecarBackup(string sidecarPath)
    {
        string backupPath = sidecarPath + ".backup";
        if (!File.Exists(sidecarPath) && File.Exists(backupPath))
        {
            File.Move(backupPath, sidecarPath);
        }
    }

    private static void WriteCanonical(SavedData data)
    {
        _canonicalWriteDepth++;
        try
        {
            data.Save();
        }
        finally
        {
            _canonicalWriteDepth--;
        }
    }

    private static void DeleteSidecar(string savePath)
    {
        TryDeleteFile(GetSidecarPath(savePath));
        TryDeleteFile(GetSidecarPath(savePath) + ".pending");
        TryDeleteFile(GetSidecarPath(savePath) + ".pending.tmp");
        TryDeleteFile(GetSidecarPath(savePath) + ".backup");
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            SunnyModLoaderPlugin.Log.LogWarning(
                "Could not delete stale Mod save sidecar " + Path.GetFileName(path) + ": " + ex.Message);
        }
    }

    private static string GetSidecarPath(string savePath)
    {
        return string.IsNullOrWhiteSpace(savePath)
            ? string.Empty
            : Path.Combine(
                Path.GetDirectoryName(savePath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(savePath) + SidecarSuffix);
    }

    private static string GetCurrentScene(SavedData data)
    {
        return data?.StateSnapshot?.Player?.CurrentScene ?? data?.scriptName ?? string.Empty;
    }

    private static GameStateSnapshot GetActualSnapshot(SavedData data)
    {
        if (data?.StateSnapshot?.Player != null)
        {
            return data.StateSnapshot;
        }

        return new GameStateSnapshot
        {
            Player = new CorePlayerSnapshot
            {
                CurrentScene = data?.scriptName ?? string.Empty,
                CurrentScriptName = data?.scriptName ?? string.Empty,
                CurrentScriptIndex = data?.scriptIndex ?? 0,
                CurrentContent = data?.SaveContent ?? string.Empty,
                IsPlaying = false,
                CurrentSceneOptions = data?.Options != null
                    ? new List<string>(data.Options)
                    : new List<string>(),
                Variables = ToEntries(data?.Variables)
            }
        };
    }

    private static void RestoreLegacyPayload(ModSaveSidecar sidecar, SavedData data)
    {
        if (string.IsNullOrWhiteSpace(sidecar?.LegacySaveJson))
        {
            return;
        }

        string tempPath = Path.Combine(
            Path.GetTempPath(),
            "sunny-mod-save-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(tempPath, sidecar.LegacySaveJson, new UTF8Encoding(false));
            SavedData legacy = new SavedData(tempPath);
            data.SaveContent = legacy.SaveContent;
            data.scriptName = legacy.scriptName;
            data.scriptIndex = legacy.scriptIndex;
            data.Options = legacy.Options != null ? new List<string>(legacy.Options) : new List<string>();
            data.Variables = legacy.Variables != null
                ? new Dictionary<string, string>(legacy.Variables)
                : new Dictionary<string, string>();
            data.BackLogHistory = CloneHistory(legacy.BackLogHistory);
            data.BackLogSnapshotTable = CloneSnapshots(legacy.BackLogSnapshotTable);
            data.BackLogRecords = CloneRecords(legacy.BackLogRecords);
            data.StateSnapshot = legacy.StateSnapshot?.Clone();
        }
        catch (Exception ex)
        {
            SunnyModLoaderPlugin.Log.LogWarning(
                "Could not rebuild legacy Mod save payload: " + ex.Message);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static bool IsModReference(string value)
    {
        return LoaderUtil.TryParseModUri(value, out _, out _);
    }

    private static List<SerializableStringEntry> ToEntries(Dictionary<string, string> variables)
    {
        return variables?
            .Where(pair => !string.IsNullOrEmpty(pair.Key))
            .Select(pair => new SerializableStringEntry { Key = pair.Key, Value = pair.Value ?? string.Empty })
            .ToList() ?? new List<SerializableStringEntry>();
    }

    private static Dictionary<string, string> ToDictionary(IEnumerable<SerializableStringEntry> entries)
    {
        Dictionary<string, string> variables = new Dictionary<string, string>();
        if (entries == null)
        {
            return variables;
        }

        foreach (SerializableStringEntry entry in entries)
        {
            if (entry != null && !string.IsNullOrEmpty(entry.Key))
            {
                variables[entry.Key] = entry.Value ?? string.Empty;
            }
        }
        return variables;
    }

    private static Dictionary<string, string> ToDictionary(IEnumerable<RawVariableEntry> entries)
    {
        Dictionary<string, string> variables = new Dictionary<string, string>();
        if (entries == null)
        {
            return variables;
        }

        foreach (RawVariableEntry entry in entries)
        {
            if (entry != null && !string.IsNullOrEmpty(entry.Key))
            {
                variables[entry.Key] = entry.Value ?? string.Empty;
            }
        }
        return variables;
    }

    private static List<MessageEntry> CloneHistory(IEnumerable<MessageEntry> history)
    {
        return history?.Select(entry => entry?.Clone() ?? new MessageEntry()).ToList() ?? new List<MessageEntry>();
    }

    private static List<GameStateSnapshot> CloneSnapshots(IEnumerable<GameStateSnapshot> snapshots)
    {
        return snapshots?.Select(snapshot => snapshot?.Clone()).ToList() ?? new List<GameStateSnapshot>();
    }

    private static List<BackLogSnapshotRecord> CloneRecords(IEnumerable<BackLogSnapshotRecord> records)
    {
        return records?.Select(record => record?.Clone() ?? new BackLogSnapshotRecord()).ToList() ??
               new List<BackLogSnapshotRecord>();
    }

#pragma warning disable CS0649 // Populated by DataContractJsonSerializer.
    [DataContract]
    private sealed class RawNavigationFile
    {
        [DataMember] public RawNavigationPayload Payload;
    }

    [DataContract]
    private sealed class RawNavigationPayload
    {
        [DataMember] public List<string> Options;
        [DataMember] public List<RawVariableEntry> Variables;
        [DataMember] public RawNavigationSnapshot StateSnapshot;
    }

    [DataContract]
    private sealed class RawNavigationSnapshot
    {
        [DataMember] public RawPlayerSnapshot Player;

        internal GameStateSnapshot ToSnapshot()
        {
            return new GameStateSnapshot { Player = Player?.ToSnapshot() };
        }
    }

    [DataContract]
    private sealed class RawPlayerSnapshot
    {
        [DataMember] public string CurrentScene;
        [DataMember] public string CurrentScriptName;
        [DataMember] public int CurrentScriptIndex;
        [DataMember] public string CurrentContent;
        [DataMember] public bool IsPlaying;
        [DataMember] public List<string> CurrentSceneOptions;
        [DataMember] public List<RawVariableEntry> Variables;

        internal CorePlayerSnapshot ToSnapshot()
        {
            return new CorePlayerSnapshot
            {
                CurrentScene = CurrentScene ?? string.Empty,
                CurrentScriptName = CurrentScriptName ?? string.Empty,
                CurrentScriptIndex = CurrentScriptIndex,
                CurrentContent = CurrentContent ?? string.Empty,
                IsPlaying = IsPlaying,
                CurrentSceneOptions = CurrentSceneOptions != null
                    ? new List<string>(CurrentSceneOptions)
                    : new List<string>(),
                Variables = Variables?
                    .Where(entry => entry != null && !string.IsNullOrEmpty(entry.Key))
                    .Select(entry => new SerializableStringEntry
                    {
                        Key = entry.Key,
                        Value = entry.Value ?? string.Empty
                    })
                    .ToList() ?? new List<SerializableStringEntry>()
            };
        }
    }

    [DataContract]
    private sealed class RawVariableEntry
    {
        [DataMember] public string Key;
        [DataMember] public string Value;
    }
#pragma warning restore CS0649

    [Serializable]
    private sealed class ModSaveSidecar
    {
        public int SchemaVersion;
        public int SaveIndex;
        public string BaseSaveTimeStr;
        public string BaseScriptName;
        public int BaseScriptIndex;
        public string ActualSaveContent;
        public string ActualScriptName;
        public int ActualScriptIndex;
        public List<string> Options = new List<string>();
        public List<SerializableStringEntry> Variables = new List<SerializableStringEntry>();
        public List<MessageEntry> BackLogHistory = new List<MessageEntry>();
        public List<GameStateSnapshot> BackLogSnapshotTable = new List<GameStateSnapshot>();
        public List<BackLogSnapshotRecord> BackLogRecords = new List<BackLogSnapshotRecord>();
        public GameStateSnapshot StateSnapshot;
        public string LegacySaveJson;
    }

    internal sealed class LoadSwapState
    {
        private readonly string _saveContent;
        private readonly string _scriptName;
        private readonly int _scriptIndex;
        private readonly List<string> _options;
        private readonly Dictionary<string, string> _variables;
        private readonly List<MessageEntry> _backLogHistory;
        private readonly List<GameStateSnapshot> _backLogSnapshotTable;
        private readonly List<BackLogSnapshotRecord> _backLogRecords;
        private readonly GameStateSnapshot _stateSnapshot;

        internal LoadSwapState(SavedData data)
        {
            _saveContent = data.SaveContent;
            _scriptName = data.scriptName;
            _scriptIndex = data.scriptIndex;
            _options = data.Options;
            _variables = data.Variables;
            _backLogHistory = data.BackLogHistory;
            _backLogSnapshotTable = data.BackLogSnapshotTable;
            _backLogRecords = data.BackLogRecords;
            _stateSnapshot = data.StateSnapshot;
        }

        internal void Restore(SavedData data)
        {
            if (data == null)
            {
                return;
            }

            data.SaveContent = _saveContent;
            data.scriptName = _scriptName;
            data.scriptIndex = _scriptIndex;
            data.Options = _options;
            data.Variables = _variables;
            data.BackLogHistory = _backLogHistory;
            data.BackLogSnapshotTable = _backLogSnapshotTable;
            data.BackLogRecords = _backLogRecords;
            data.StateSnapshot = _stateSnapshot;
        }
    }
}
