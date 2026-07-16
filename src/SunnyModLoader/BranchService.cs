using System;
using System.Collections.Generic;
using System.Linq;
using MakeineGalGameQM.Core;
using MakeineGalGameQM.Core.Character;
using MakeineGalGameQM.Core.SaveAndLoad;
using MakeineGalGameQM.Core.Utils;
using UnityEngine;

namespace SunnyModLoader;

internal static class BranchService
{
    private const string FrameVariable = "__sunny_modloader_frames";
    private const int MaxRuntimeSnapshotsPerPlayer = 128;
    private const int MaxReturnDepth = 64;
    private static readonly HashSet<string> BypassOnce = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, List<RuntimeReturnSnapshot>> RuntimeReturnSnapshots =
        new Dictionary<int, List<RuntimeReturnSnapshot>>();
    private static readonly Dictionary<int, ScriptBlocker> PendingChoiceBlockers =
        new Dictionary<int, ScriptBlocker>();

    internal static bool HasPendingChoice(CorePlayer player)
    {
        return player != null && PendingChoiceBlockers.ContainsKey(player.GetInstanceID());
    }

    internal static void CancelPendingChoice(CorePlayer player)
    {
        ReleasePendingChoice(player);
    }

    internal static bool TryIntercept(ScriptBase script, CorePlayer player)
    {
        if (script == null || player == null)
        {
            return false;
        }

        string location = MakeRuntimeKey(player, player.currentScene, player.currentScriptIndex);
        if (BypassOnce.Remove(location))
        {
            return false;
        }

        IReadOnlyList<RuntimeBranch> candidates = StoryPatchService.GetBranches(player.currentScene, player.currentScriptIndex);
        List<RuntimeBranch> visible = candidates.Where(branch => IsVisible(branch, player)).ToList();
        if (visible.Count == 0)
        {
            return false;
        }

        OptionPanel panel = UnityEngine.Object.FindFirstObjectByType<OptionPanel>();
        if (panel == null)
        {
            SunnyModLoaderPlugin.Log.LogError("Cannot show mod branch choices because OptionPanel was not found.");
            return false;
        }

        int resumeIndex = player.currentScriptIndex;
        string resumeScene = player.currentScene;
        GameStateSnapshot returnSnapshot = CaptureReturnSnapshot(player);
        player.IsFastMode = false;
        player.IsTransientSkipActive = false;
        AddPendingChoice(player);
        BacklogVoiceReplayService.HideAllVisible();
        player.Pause();
        panel.Clear();

        foreach (RuntimeBranch branch in visible)
        {
            RuntimeBranch captured = branch;
            OptionButton button = panel.AddChoice();
            button.Init(string.IsNullOrWhiteSpace(captured.Definition.optionText) ? captured.Package.DisplayName : captured.Definition.optionText);
            if (captured.Definition.continueCurrent)
            {
                button.AddListener(() => ContinueOriginal(panel, player, captured, resumeScene, resumeIndex));
            }
            else
            {
                button.AddListener(() => EnterBranch(
                    panel,
                    player,
                    captured,
                    resumeScene,
                    resumeIndex,
                    returnSnapshot));
            }
        }

        panel.Show();
        return true;
    }

    internal static bool TryReturnFromCommand(ICommandParameter[] parameters, CorePlayer player)
    {
        string path = LoaderUtil.GetStringParameter(parameters, "path");
        if (!string.Equals(path, "mod://return", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            BranchFramePayload payload = ReadFrames(player);
            if (payload.frames == null || payload.frames.Length == 0)
            {
                SunnyModLoaderPlugin.Log.LogError("mod://return was used with an empty return stack.");
                player.isPlaying = false;
                return true;
            }

            BranchFrame frame = payload.frames[payload.frames.Length - 1];
            BranchFramePayload remaining = new BranchFramePayload
            {
                frames = payload.frames.Take(payload.frames.Length - 1).ToArray()
            };
            string remainingJson = JsonCodec.Serialize(remaining);

            RuntimeReturnSnapshot runtimeSnapshot = FindReturnSnapshot(player, frame);
            if (runtimeSnapshot != null && TryRestoreReturnSnapshot(
                    player,
                    runtimeSnapshot.Snapshot.Clone(),
                    frame,
                    remainingJson))
            {
                return true;
            }

            player.LoadSceneScripts(frame.scene);
            if (!SceneLoaded(player, frame.scene))
            {
                SunnyModLoaderPlugin.Log.LogError("Could not return to parent story because it failed to load: " + frame.scene);
                player.isPlaying = false;
                return true;
            }

            RestorePersistentState(frame);
            player.SetVariable(FrameVariable, remainingJson);
            if (!frame.returnAfterCall)
            {
                BypassOnce.Add(MakeRuntimeKey(player, frame.scene, frame.returnIndex));
            }
            player.currentScriptIndex = frame.returnIndex - 1;
            player.isPlaying = true;
            SunnyModLoaderPlugin.Registry.SyncSettings(player);
        }
        catch (Exception ex)
        {
            SunnyModLoaderPlugin.Log.LogError("Failed to return from Mod story: " + ex);
            player.isPlaying = false;
        }

        return true;
    }

    internal static bool TryCallFromCommand(ICommandParameter[] parameters, CorePlayer player)
    {
        string path = LoaderUtil.GetStringParameter(parameters, "path");
        if (!FlowService.TryParseCallUri(path, out string targetUri, out string entryLabel))
        {
            return false;
        }

        string parentScene = player.currentScene;
        int callIndex = player.currentScriptIndex;
        try
        {
            GameStateSnapshot returnSnapshot = CaptureReturnSnapshot(player);
            BranchFramePayload payload = ReadFrames(player);
            List<BranchFrame> frames = payload.frames?.ToList() ?? new List<BranchFrame>();
            if (frames.Count >= MaxReturnDepth)
            {
                SunnyModLoaderPlugin.Log.LogError(
                    "Mod call return stack reached the limit of " + MaxReturnDepth + ".");
                ResumeOriginal(player, parentScene, callIndex);
                return true;
            }

            LoaderUtil.TryParseModUri(targetUri, out string targetModId, out _);
            BranchFrame frame = new BranchFrame
            {
                modId = targetModId,
                branchId = "call",
                frameId = Guid.NewGuid().ToString("N"),
                scene = parentScene,
                returnIndex = callIndex + 1,
                returnAfterCall = true,
                background = CaptureBackgroundState(returnSnapshot),
                music = CaptureMusicState(returnSnapshot),
                characters = CaptureCharacterStates(returnSnapshot)
            };
            frames.Add(frame);
            string nextFramesJson = JsonCodec.Serialize(new BranchFramePayload { frames = frames.ToArray() });

            player.LoadSceneScripts(targetUri);
            if (!SceneLoaded(player, targetUri))
            {
                SunnyModLoaderPlugin.Log.LogError("Called Mod story failed to load: " + targetUri);
                ResumeOriginal(player, parentScene, callIndex);
                return true;
            }

            if (!string.IsNullOrWhiteSpace(entryLabel))
            {
                int entryIndex = player.GetMarkIndex(entryLabel);
                if (entryIndex < 0)
                {
                    SunnyModLoaderPlugin.Log.LogError(
                        "Called Mod story entry label was not found: " + targetUri + " -> " + entryLabel);
                    ResumeOriginal(player, parentScene, callIndex);
                    return true;
                }
                player.JumpTo(entryIndex);
            }
            else
            {
                player.currentScriptIndex = -1;
            }

            player.SetVariable(FrameVariable, nextFramesJson);
            SunnyModLoaderPlugin.Registry.SyncSettings(player);
            StoreReturnSnapshot(player, frame, returnSnapshot);
            player.isPlaying = true;
        }
        catch (Exception ex)
        {
            SunnyModLoaderPlugin.Log.LogError("Failed to call Mod story " + targetUri + ": " + ex);
            ResumeOriginal(player, parentScene, callIndex);
        }

        return true;
    }

    private static bool IsVisible(RuntimeBranch branch, CorePlayer player)
    {
        BranchOptionDefinition manifest = branch.Definition;
        if (!manifest.continueCurrent && string.IsNullOrWhiteSpace(manifest.story))
        {
            return false;
        }

        if (!manifest.continueCurrent)
        {
            string storyUri = LoaderUtil.ToModUri(branch.Package.Id, manifest.story);
            if (!SunnyModLoaderPlugin.Registry.TryResolveAsset(storyUri, AssetKind.Text, out _))
            {
                SunnyModLoaderPlugin.Log.LogError("Branch story is missing: " + storyUri);
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(manifest.setting))
        {
            bool enabled = SunnyModLoaderPlugin.Registry.GetBoolSetting(branch.Package, manifest.setting);
            if (manifest.invertSetting ? enabled : !enabled)
            {
                return false;
            }
        }

        if (!manifest.repeatable && player.GetVariable(GetConsumedVariable(branch)) == "1")
        {
            return false;
        }

        return true;
    }

    private static void ContinueOriginal(
        OptionPanel panel,
        CorePlayer player,
        RuntimeBranch branch,
        string scene,
        int index)
    {
        panel.Clear();
        panel.Hide();
        ReleasePendingChoice(player);
        if (!branch.Definition.repeatable)
        {
            player.SetVariable(GetConsumedVariable(branch), "1");
        }
        BypassOnce.Add(MakeRuntimeKey(player, scene, index));
        player.currentScriptIndex = index;
        player.Continue();
    }

    private static void EnterBranch(
        OptionPanel panel,
        CorePlayer player,
        RuntimeBranch branch,
        string scene,
        int index,
        GameStateSnapshot returnSnapshot)
    {
        panel.Clear();
        panel.Hide();
        ReleasePendingChoice(player);
        TryEnterBranchForDiagnostics(player, branch, scene, index, returnSnapshot);
    }

    internal static bool TryEnterBranchForDiagnostics(
        CorePlayer player,
        RuntimeBranch branch,
        string scene,
        int index,
        GameStateSnapshot returnSnapshot = null)
    {
        try
        {
            returnSnapshot ??= CaptureReturnSnapshot(player);
            BranchFramePayload payload = ReadFrames(player);
            List<BranchFrame> frames = payload.frames?.ToList() ?? new List<BranchFrame>();
            if (frames.Count >= MaxReturnDepth)
            {
                SunnyModLoaderPlugin.Log.LogError(
                    "Mod branch return stack reached the limit of " + MaxReturnDepth + ".");
                ResumeOriginal(player, scene, index);
                return false;
            }

            BranchFrame frame = new BranchFrame
            {
                modId = branch.Package.Id,
                branchId = branch.Definition.id,
                frameId = Guid.NewGuid().ToString("N"),
                scene = scene,
                returnIndex = index,
                background = CaptureBackgroundState(returnSnapshot),
                music = CaptureMusicState(returnSnapshot),
                characters = CaptureCharacterStates(returnSnapshot)
            };
            frames.Add(frame);
            BranchFramePayload nextPayload = new BranchFramePayload { frames = frames.ToArray() };
            string nextFramesJson = JsonCodec.Serialize(nextPayload);

            string storyUri = LoaderUtil.ToModUri(branch.Package.Id, branch.Definition.story);
            player.LoadSceneScripts(storyUri);
            if (!SceneLoaded(player, storyUri))
            {
                SunnyModLoaderPlugin.Log.LogError("Branch story failed to load: " + storyUri);
                ResumeOriginal(player, scene, index);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(branch.Definition.entryLabel))
            {
                int entryIndex = player.GetMarkIndex(branch.Definition.entryLabel);
                if (entryIndex < 0)
                {
                    SunnyModLoaderPlugin.Log.LogError(
                        "Branch entry label was not found " + branch.Package.Id + ":" + branch.Definition.id +
                        " -> " + branch.Definition.entryLabel);
                    ResumeOriginal(player, scene, index);
                    return false;
                }

                player.JumpTo(entryIndex);
            }

            player.SetVariable(FrameVariable, nextFramesJson);
            if (!branch.Definition.repeatable)
            {
                player.SetVariable(GetConsumedVariable(branch), "1");
            }

            SunnyModLoaderPlugin.Registry.SyncSettings(player);
            StoreReturnSnapshot(player, frame, returnSnapshot);
            player.isPlaying = true;
            return true;
        }
        catch (Exception ex)
        {
            SunnyModLoaderPlugin.Log.LogError("Failed to enter Mod branch " + branch.Package.Id + ":" + branch.Definition.id + ": " + ex);
            ResumeOriginal(player, scene, index);
            return false;
        }
    }

    internal static bool IsVisibleForDiagnostics(RuntimeBranch branch, CorePlayer player)
    {
        return IsVisible(branch, player);
    }

    internal static int GetReturnDepthForDiagnostics(CorePlayer player)
    {
        return ReadFrames(player).frames?.Length ?? 0;
    }

    internal static bool ConsumeBypassForDiagnostics(CorePlayer player, string scene, int index)
    {
        return BypassOnce.Remove(MakeRuntimeKey(player, scene, index));
    }

    internal static bool ValidateChoiceBlockForDiagnostics(CorePlayer player)
    {
        if (player == null)
        {
            return false;
        }

        player.isPlaying = false;
        AddPendingChoice(player);
        player.IsFastMode = true;
        player.IsTransientSkipActive = true;
        player.Continue();
        bool blocked = HasPendingChoice(player) && !player.isPlaying &&
                       !player.IsFastMode && !player.IsTransientSkipActive &&
                       !BacklogVoiceReplayService.CanOpen(player);
        ReleasePendingChoice(player);
        player.Continue();
        bool resumed = !HasPendingChoice(player) && player.isPlaying;
        player.Pause();
        return blocked && resumed;
    }

    internal static bool ValidateBacklogGuardForDiagnostics(CorePlayer player, BackLogPanel panel)
    {
        if (player == null || panel == null || panel.manager == null || panel.manager.Count <= 0)
        {
            return false;
        }

        bool wasPlaying = player.isPlaying;
        bool wasFast = player.IsFastMode;
        bool wasTransient = player.IsTransientSkipActive;
        try
        {
            ReleasePendingChoice(player);
            if (panel.IsVisable)
            {
                panel.Hide();
            }

            AddPendingChoice(player);
            panel.Show();
            bool openingBlocked = !panel.IsVisable && !BacklogVoiceReplayService.CanOpen(player);
            ReleasePendingChoice(player);

            panel.Show();
            bool normalOpen = panel.IsVisable && BacklogVoiceReplayService.CanOpen(player);
            AddPendingChoice(player);
            BacklogVoiceReplayService.HideAllVisible();
            bool visiblePanelClosed = !panel.IsVisable;
            ReleasePendingChoice(player);
            panel.Show();
            return openingBlocked && normalOpen && visiblePanelClosed && panel.IsVisable;
        }
        finally
        {
            ReleasePendingChoice(player);
            player.IsFastMode = wasFast;
            player.IsTransientSkipActive = wasTransient;
            player.isPlaying = wasPlaying;
        }
    }

    internal static bool ValidateSnapshotReuseForDiagnostics(CorePlayer player)
    {
        if (player == null)
        {
            return false;
        }

        BranchFrame frame = new BranchFrame
        {
            modId = "diagnostic.mod",
            branchId = "reusable-return",
            frameId = Guid.NewGuid().ToString("N"),
            scene = "Script/diagnostic",
            returnIndex = 42
        };
        GameStateSnapshot snapshot = new GameStateSnapshot
        {
            Background = new BackgroundSnapshot
            {
                Path = "BGD/diagnostic.png",
                Position = new Vector3(1f, 2f, 3f),
                Scale = 1.25f
            }
        };

        StoreReturnSnapshot(player, frame, snapshot);
        RuntimeReturnSnapshot first = FindReturnSnapshot(player, frame);
        RuntimeReturnSnapshot second = FindReturnSnapshot(player, frame);
        if (RuntimeReturnSnapshots.TryGetValue(player.GetInstanceID(), out List<RuntimeReturnSnapshot> snapshots))
        {
            snapshots.RemoveAll(candidate => string.Equals(candidate.FrameId, frame.frameId, StringComparison.Ordinal));
            if (snapshots.Count == 0)
            {
                RuntimeReturnSnapshots.Remove(player.GetInstanceID());
            }
        }

        return first?.Snapshot?.Background != null && second?.Snapshot?.Background != null &&
               string.Equals(first.FrameId, second.FrameId, StringComparison.Ordinal);
    }

    private static BranchFramePayload ReadFrames(CorePlayer player)
    {
        string json = player?.GetVariable(FrameVariable);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new BranchFramePayload { frames = Array.Empty<BranchFrame>() };
        }

        try
        {
            BranchFramePayload payload = JsonCodec.Deserialize<BranchFramePayload>(json);
            return payload ?? new BranchFramePayload { frames = Array.Empty<BranchFrame>() };
        }
        catch (Exception ex)
        {
            SunnyModLoaderPlugin.Log.LogWarning("Invalid mod return stack was reset: " + ex.Message);
            return new BranchFramePayload { frames = Array.Empty<BranchFrame>() };
        }
    }

    private static void ResumeOriginal(CorePlayer player, string scene, int index)
    {
        if (!SceneLoaded(player, scene))
        {
            player.LoadSceneScripts(scene);
        }

        if (!SceneLoaded(player, scene))
        {
            SunnyModLoaderPlugin.Log.LogError("Could not resume original story because it failed to load: " + scene);
            player.isPlaying = false;
            return;
        }

        BypassOnce.Add(MakeRuntimeKey(player, scene, index));
        player.currentScriptIndex = index;
        player.Continue();
    }

    private static GameStateSnapshot CaptureReturnSnapshot(CorePlayer player)
    {
        if (player == null || player.gameObject == null || !player.gameObject.activeInHierarchy)
        {
            return null;
        }

        try
        {
            return player.CreateSnapshot();
        }
        catch (Exception ex)
        {
            SunnyModLoaderPlugin.Log.LogWarning("Could not capture Mod branch return snapshot: " + ex.Message);
            return null;
        }
    }

    private static void StoreReturnSnapshot(CorePlayer player, BranchFrame frame, GameStateSnapshot snapshot)
    {
        if (player == null || frame == null || snapshot == null)
        {
            return;
        }

        int playerId = player.GetInstanceID();
        if (!RuntimeReturnSnapshots.TryGetValue(playerId, out List<RuntimeReturnSnapshot> snapshots))
        {
            snapshots = new List<RuntimeReturnSnapshot>();
            RuntimeReturnSnapshots[playerId] = snapshots;
        }

        snapshots.Add(new RuntimeReturnSnapshot
        {
            ModId = frame.modId,
            BranchId = frame.branchId,
            FrameId = frame.frameId,
            Snapshot = snapshot.Clone()
        });

        if (snapshots.Count > MaxRuntimeSnapshotsPerPlayer)
        {
            snapshots.RemoveRange(0, snapshots.Count - MaxRuntimeSnapshotsPerPlayer);
        }
    }

    private static RuntimeReturnSnapshot FindReturnSnapshot(CorePlayer player, BranchFrame frame)
    {
        if (player == null || frame == null ||
            !RuntimeReturnSnapshots.TryGetValue(player.GetInstanceID(), out List<RuntimeReturnSnapshot> snapshots))
        {
            return null;
        }

        for (int index = snapshots.Count - 1; index >= 0; index--)
        {
            RuntimeReturnSnapshot candidate = snapshots[index];
            if (!string.IsNullOrWhiteSpace(frame.frameId) &&
                !string.Equals(candidate.FrameId, frame.frameId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(candidate.ModId, frame.modId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(candidate.BranchId, frame.branchId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    private static void AddPendingChoice(CorePlayer player)
    {
        ReleasePendingChoice(player);
        ScriptBlocker blocker = new ScriptBlocker(_ => { })
        {
            ExclusiveManualRelease = true
        };
        player.AddScriptBlocker(blocker);
        PendingChoiceBlockers[player.GetInstanceID()] = blocker;
    }

    private static void ReleasePendingChoice(CorePlayer player)
    {
        if (player == null || !PendingChoiceBlockers.TryGetValue(player.GetInstanceID(), out ScriptBlocker blocker))
        {
            return;
        }

        PendingChoiceBlockers.Remove(player.GetInstanceID());
        player.TryReleaseScriptBlocker(blocker);
    }

    private static BranchBackgroundState CaptureBackgroundState(GameStateSnapshot snapshot)
    {
        if (snapshot?.Background == null)
        {
            return null;
        }

        return new BranchBackgroundState
        {
            path = snapshot.Background.Path,
            x = snapshot.Background.Position.x,
            y = snapshot.Background.Position.y,
            z = snapshot.Background.Position.z,
            scale = snapshot.Background.Scale
        };
    }

    private static BranchMusicState CaptureMusicState(GameStateSnapshot snapshot)
    {
        if (snapshot?.Music == null)
        {
            return null;
        }

        return new BranchMusicState
        {
            path = snapshot.Music.Path,
            volume = snapshot.Music.Volume,
            isPlaying = snapshot.Music.IsPlaying
        };
    }

    private static BranchCharacterState[] CaptureCharacterStates(GameStateSnapshot snapshot)
    {
        return snapshot?.Characters?
            .Where(character => character != null && !string.IsNullOrWhiteSpace(character.Name))
            .Select(character => new BranchCharacterState
            {
                name = character.Name,
                visible = character.IsVisible,
                x = character.Position.x,
                y = character.Position.y,
                rotation = character.Rotation,
                scale = character.Scale,
                emotion = character.Emotion,
                illustration = character.Illustration,
                animation = character.Animation
            })
            .ToArray();
    }

    private static void RestorePersistentState(BranchFrame frame)
    {
        RestoreBackgroundState(frame);

        if (frame?.characters != null)
        {
            CharacterManager characterManager = UnityEngine.Object.FindFirstObjectByType<CharacterManager>();
            if (characterManager != null)
            {
                characterManager.RestoreSnapshot(frame.characters.Select(character => new CharacterStateSnapshot
                {
                    Name = character.name,
                    IsVisible = character.visible,
                    Position = new Vector2(character.x, character.y),
                    Rotation = character.rotation,
                    Scale = character.scale > 0f ? character.scale : 1f,
                    Emotion = character.emotion ?? string.Empty,
                    Illustration = character.illustration ?? string.Empty,
                    Animation = character.animation ?? string.Empty
                }));
            }
        }

        if (frame?.music != null)
        {
            MusicManager.Instance?.InvalidateTrackReuseCache();
            MusicManager.Instance?.RestoreSnapshot(new MusicSnapshot
            {
                Path = frame.music.path ?? string.Empty,
                Volume = frame.music.volume,
                IsPlaying = frame.music.isPlaying
            });
        }
    }

    private static void RestoreBackgroundState(BranchFrame frame)
    {
        if (frame?.background == null)
        {
            return;
        }

        BackGroundManager manager = UnityEngine.Object.FindFirstObjectByType<BackGroundManager>();
        if (manager == null)
        {
            SunnyModLoaderPlugin.Log.LogWarning("Could not restore Mod branch background because BackGroundManager was not found.");
            return;
        }

        manager.RestoreSnapshot(new BackgroundSnapshot
        {
            Path = frame.background.path ?? string.Empty,
            Position = new Vector3(frame.background.x, frame.background.y, frame.background.z),
            Scale = frame.background.scale > 0f ? frame.background.scale : 1f
        });
    }

    private static bool TryRestoreReturnSnapshot(
        CorePlayer player,
        GameStateSnapshot snapshot,
        BranchFrame frame,
        string remainingFramesJson)
    {
        try
        {
            Dictionary<string, string> branchVariables = player.GetVariablesSnapshot();
            player.RestoreSnapshot(snapshot, false);
            if (!SceneLoaded(player, frame.scene))
            {
                SunnyModLoaderPlugin.Log.LogWarning(
                    "Mod branch snapshot restored an unexpected scene; using script-only fallback.");
                return false;
            }

            player.RestoreVariables(branchVariables);
            player.SetVariable(FrameVariable, remainingFramesJson);
            if (!frame.returnAfterCall)
            {
                BypassOnce.Add(MakeRuntimeKey(player, frame.scene, frame.returnIndex));
            }
            player.currentScriptIndex = frame.returnIndex - 1;
            player.isPlaying = true;
            SunnyModLoaderPlugin.Registry.SyncSettings(player);
            return true;
        }
        catch (Exception ex)
        {
            SunnyModLoaderPlugin.Log.LogWarning(
                "Could not restore the full Mod branch snapshot; using script-only fallback: " + ex.Message);
            return false;
        }
    }

    private static string GetConsumedVariable(RuntimeBranch branch)
    {
        return "__sunny_branch_" + LoaderUtil.SafeId(branch.Package.Id) + "_" + LoaderUtil.SafeId(branch.Definition.id);
    }

    private static string MakeRuntimeKey(CorePlayer player, string scene, int index)
    {
        return player.GetInstanceID() + ":" + LoaderUtil.NormalizeScene(scene) + ":" + index;
    }

    private static bool SceneLoaded(CorePlayer player, string scene)
    {
        return player != null && string.Equals(
            LoaderUtil.NormalizeScene(player.currentScene),
            LoaderUtil.NormalizeScene(scene),
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RuntimeReturnSnapshot
    {
        internal string ModId;
        internal string BranchId;
        internal string FrameId;
        internal GameStateSnapshot Snapshot;
    }
}
