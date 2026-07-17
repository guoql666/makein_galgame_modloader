using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MakeineGalGameQM.Core;
using MakeineGalGameQM.Core.Character;
using MakeineGalGameQM.Core.Utils;
using UnityEngine;

namespace SunnyModLoader;

internal static class StartupDiagnostics
{
    internal const string ValidateEnvironmentVariable = "SUNNY_MODLOADER_VALIDATE";
    internal const string ValidateInstallerEnvironmentVariable = "SUNNY_MODLOADER_VALIDATE_INSTALLER";
    internal const string QuitEnvironmentVariable = "SUNNY_MODLOADER_QUIT_AFTER_VALIDATE";
    internal const string SettingsPageEnvironmentVariable = "SUNNY_MODLOADER_VALIDATE_SETTINGS_PAGE";
    internal const string ResolutionIndexEnvironmentVariable = "SUNNY_MODLOADER_VALIDATE_RESOLUTION_INDEX";
    internal const string BacklogUiEnvironmentVariable = "SUNNY_MODLOADER_VALIDATE_BACKLOG_UI";
    private static readonly FieldInfo CurrentSceneScriptsField =
        AccessTools.Field(typeof(CorePlayer), "currentSceneScripts");

    internal static bool IsRequested => IsEnabled(ValidateEnvironmentVariable);
    internal static bool IsInstallerValidationRequested => IsEnabled(ValidateInstallerEnvironmentVariable);
    internal static bool ShouldQuit => IsEnabled(QuitEnvironmentVariable);
    internal static string SettingsPageToOpen =>
        Environment.GetEnvironmentVariable(SettingsPageEnvironmentVariable) ?? string.Empty;
    internal static int ResolutionIndexToApply =>
        int.TryParse(Environment.GetEnvironmentVariable(ResolutionIndexEnvironmentVariable), out int index)
            ? index
            : -1;
    internal static bool IsBacklogUiRequested => IsEnabled(BacklogUiEnvironmentVariable);

    internal static bool Run(ModRegistry registry)
    {
        int failures = 0;
        bool installerValid = !IsInstallerValidationRequested ||
                              ModInstallerDiagnostics.Run(SunnyModLoaderPlugin.Log).Success;
        HashSet<string> baseScenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> modStories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ModPackage package in registry.ActiveLowToHigh)
        {
            foreach (FlowDefinition flow in package.Flows)
            {
                if (!string.IsNullOrWhiteSpace(flow?.SceneUri))
                {
                    modStories.Add(flow.SceneUri);
                }
            }

            if (package.DialoguePatches != null)
            {
                foreach (DialoguePatchDefinition patch in package.DialoguePatches)
                {
                    if (!string.IsNullOrWhiteSpace(patch?.anchor?.scene))
                    {
                        if (LoaderUtil.TryParseModUri(patch.anchor.scene, out _, out _))
                        {
                            modStories.Add(patch.anchor.scene);
                        }
                        else
                        {
                            baseScenes.Add(patch.anchor.scene);
                        }
                    }
                }
            }

            if (package.Branches == null)
            {
                continue;
            }

            foreach (BranchOptionDefinition branch in package.Branches)
            {
                if (branch == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(branch.scene))
                {
                    if (LoaderUtil.TryParseModUri(branch.scene, out _, out _))
                    {
                        modStories.Add(branch.scene);
                    }
                    else
                    {
                        baseScenes.Add(branch.scene);
                    }
                }

                if (!branch.continueCurrent && !string.IsNullOrWhiteSpace(branch.story))
                {
                    modStories.Add(LoaderUtil.ToModUri(package.Id, branch.story));
                }
            }
        }

        foreach (string scene in baseScenes)
        {
            SceneScripts scripts = SceneScriptsReader.ReadSceneScripts(scene);
            if (scripts?.scripts == null)
            {
                failures++;
                SunnyModLoaderPlugin.Log.LogError("Startup validation could not parse base scene: " + scene);
                continue;
            }

            SunnyModLoaderPlugin.Log.LogInfo(
                "Startup validation parsed base scene " + scene + " with " + scripts.scripts.Count + " command(s).");
        }

        foreach (string story in modStories)
        {
            SceneScripts scripts = SceneScriptsReader.ReadSceneScripts(story);
            if (scripts?.scripts == null)
            {
                failures++;
                SunnyModLoaderPlugin.Log.LogError("Startup validation could not parse Mod story: " + story);
                continue;
            }

            SunnyModLoaderPlugin.Log.LogInfo(
                "Startup validation parsed Mod story " + story + " with " + scripts.scripts.Count + " command(s).");
            if (!ValidateFlowVoiceBindings(story, scripts, registry))
            {
                failures++;
            }

            if (!ValidateFlowSpineCharacters(story, scripts))
            {
                failures++;
            }
        }

        if (!ValidateDialoguePatchApplications(registry, baseScenes.Concat(modStories)))
        {
            failures++;
        }

        SunnyModLoaderPlugin.Log.LogInfo(
            "Startup validation completed: " + baseScenes.Count + " base scene(s), " +
            modStories.Count + " Mod story file(s), " + failures + " failure(s).");

        return installerValid && failures == 0 && ValidateFrameCodec() &&
               ValidateAudioControl(registry) &&
               ValidateScreenEffects() &&
               ValidateModSaveCompatibility() &&
               ValidateBacklogVoiceReplay() &&
               ValidateExternalSpriteConfigs(registry) &&
               ValidateManagerWindowInputScope() &&
               ValidateChoiceAdvanceGuard() && ValidateReusableReturnSnapshot() &&
               ValidateBranchRoundTrips(registry) && ValidateNestedBranchRoundTrips(registry) &&
               ValidateCallRoundTrips(registry);
    }

    private static bool ValidateModSaveCompatibility()
    {
        bool valid = ModSaveCompatibilityService.ValidateForDiagnostics(out string detail);
        if (valid)
        {
            SunnyModLoaderPlugin.Log.LogInfo("Startup validation passed Mod save compatibility: " + detail + ".");
        }
        else
        {
            SunnyModLoaderPlugin.Log.LogError("Startup validation failed Mod save compatibility: " + detail + ".");
        }
        return valid;
    }

    private static bool ValidateBacklogVoiceReplay()
    {
        bool valid = BacklogVoiceReplayService.ValidateForDiagnostics(out string detail);
        if (valid)
        {
            SunnyModLoaderPlugin.Log.LogInfo("Startup validation passed backlog voice replay: " + detail + ".");
        }
        else
        {
            SunnyModLoaderPlugin.Log.LogError("Startup validation failed backlog voice replay: " + detail + ".");
        }
        return valid;
    }

    private static bool ValidateScreenEffects()
    {
        bool valid = ScreenEffectService.ValidateForDiagnostics(out string detail);
        if (valid)
        {
            SunnyModLoaderPlugin.Log.LogInfo("Startup validation passed screen effects: " + detail + ".");
        }
        else
        {
            SunnyModLoaderPlugin.Log.LogError("Startup validation failed screen effects: " + detail + ".");
        }
        return valid;
    }

    private static bool ValidateAudioControl(ModRegistry registry)
    {
        bool builtInRegistered = registry.TryGetPackage(
                                     ModRegistry.BuiltInVoiceControlId,
                                     out ModPackage builtIn) &&
                                 builtIn.IsBuiltIn && string.IsNullOrEmpty(builtIn.RootPath) &&
                                 builtIn.AudioControl != null;
        ModPackage expected = builtInRegistered && builtIn.RuntimeEnabled ? builtIn : null;
        bool valid;
        string serviceDetail;
        if (expected == null)
        {
            valid = !SunnyModAudioControl.IsAvailable &&
                    string.IsNullOrEmpty(SunnyModAudioControl.ProviderModId) &&
                    Mathf.Approximately(
                        SunnyModAudioControl.VoiceVolume,
                        SunnyModAudioControl.DefaultVoiceVolume) &&
                    SunnyModAudioControl.StopVoiceOnAdvance ==
                    SunnyModAudioControl.DefaultStopVoiceOnAdvance;
            serviceDetail = "fallback volume=" + SunnyModAudioControl.VoiceVolume.ToString("0.###") +
                            ", stopOnAdvance=" + SunnyModAudioControl.StopVoiceOnAdvance;
        }
        else
        {
            bool initialServiceValid = AudioControlService.ValidateForDiagnostics(out string initialDetail);
            AudioControlService.Rebuild(registry);
            bool rebuiltServiceValid = AudioControlService.ValidateForDiagnostics(out string rebuiltDetail);
            serviceDetail = rebuiltDetail + ", rebuildStable=" +
                            (initialServiceValid && rebuiltServiceValid) +
                            ", initial={" + initialDetail + "}";
            valid = initialServiceValid && rebuiltServiceValid && SunnyModAudioControl.IsAvailable &&
                    string.Equals(
                        SunnyModAudioControl.ProviderModId,
                        expected.Id,
                        StringComparison.OrdinalIgnoreCase) &&
                     SunnyModAudioControl.VoiceVolume >= 0f &&
                     SunnyModAudioControl.VoiceVolume <= 1f;
        }
        bool toggleRoundTrip = !builtInRegistered || !builtIn.RuntimeEnabled ||
                               ValidateBuiltInAudioToggleRoundTrip(registry, builtIn);
        bool applyScopeValid = builtInRegistered && ValidateBuiltInAudioApplyScope(registry, builtIn);
        valid = valid && builtInRegistered && toggleRoundTrip && applyScopeValid &&
                (!builtIn.RuntimeEnabled || ReferenceEquals(expected, builtIn));
        serviceDetail += ", builtIn=" + builtInRegistered + ", toggleRoundTrip=" + toggleRoundTrip +
                         ", applyScope=" + applyScopeValid;

        if (valid)
        {
            SunnyModLoaderPlugin.Log.LogInfo("Startup validation passed public audio control API: " + serviceDetail + ".");
        }
        else
        {
            SunnyModLoaderPlugin.Log.LogError("Startup validation failed public audio control API: " + serviceDetail + ".");
        }
        return valid;
    }

    private static bool ValidateBuiltInAudioToggleRoundTrip(ModRegistry registry, ModPackage builtIn)
    {
        Dictionary<string, ModUserState> original = registry.CaptureRuntimeStates();
        Dictionary<string, ModUserState> disabled = original.ToDictionary(
            item => item.Key,
            item => item.Value.Clone(),
            StringComparer.OrdinalIgnoreCase);
        disabled[builtIn.Id].Enabled = false;
        float originalVolume = SunnyModAudioControl.VoiceVolume;
        bool originalStop = SunnyModAudioControl.StopVoiceOnAdvance;
        try
        {
            registry.ApplyUserStates(disabled, false);
            bool fallbackValid = !SunnyModAudioControl.IsAvailable &&
                                 Mathf.Approximately(
                                     SunnyModAudioControl.VoiceVolume,
                                     SunnyModAudioControl.DefaultVoiceVolume) &&
                                 SunnyModAudioControl.StopVoiceOnAdvance ==
                                 SunnyModAudioControl.DefaultStopVoiceOnAdvance &&
                                 !AudioControlService.HasInjectedSettingsUiForDiagnostics;
            registry.ApplyUserStates(original, false);
            bool restoredValid = SunnyModAudioControl.IsAvailable &&
                                 Mathf.Approximately(SunnyModAudioControl.VoiceVolume, originalVolume) &&
                                 SunnyModAudioControl.StopVoiceOnAdvance == originalStop &&
                                 AudioControlService.ValidateForDiagnostics(out _);
            return fallbackValid && restoredValid;
        }
        finally
        {
            registry.ApplyUserStates(original, false);
        }
    }

    private static bool ValidateBuiltInAudioApplyScope(ModRegistry registry, ModPackage builtIn)
    {
        Dictionary<string, ModUserState> original = registry.CaptureRuntimeStates();
        Dictionary<string, ModUserState> builtInEdit = original.ToDictionary(
            item => item.Key,
            item => item.Value.Clone(),
            StringComparer.OrdinalIgnoreCase);
        builtInEdit[builtIn.Id].Enabled = !builtInEdit[builtIn.Id].Enabled;
        bool builtInDoesNotReload = !SunnyModLoaderPlugin.RequiresSceneReloadForStateChanges(
            registry.All,
            original,
            builtInEdit,
            false);

        ModPackage dataPackage = registry.All.FirstOrDefault(package => !package.IsBuiltIn);
        if (dataPackage == null)
        {
            return builtInDoesNotReload;
        }
        Dictionary<string, ModUserState> dataEdit = original.ToDictionary(
            item => item.Key,
            item => item.Value.Clone(),
            StringComparer.OrdinalIgnoreCase);
        dataEdit[dataPackage.Id].Enabled = !dataEdit[dataPackage.Id].Enabled;
        bool dataReloads = SunnyModLoaderPlugin.RequiresSceneReloadForStateChanges(
            registry.All,
            original,
            dataEdit,
            false);
        bool pendingReloads = SunnyModLoaderPlugin.RequiresSceneReloadForStateChanges(
            registry.All,
            original,
            original,
            true);
        return builtInDoesNotReload && dataReloads && pendingReloads;
    }

    private static bool ValidateDialoguePatchApplications(ModRegistry registry, IEnumerable<string> parsedScenes)
    {
        HashSet<string> scenes = new HashSet<string>(
            parsedScenes.Select(LoaderUtil.NormalizeScene),
            StringComparer.OrdinalIgnoreCase);
        int tested = 0;
        int failures = 0;
        foreach (ModPackage package in registry.ActiveLowToHigh)
        {
            foreach (DialoguePatchDefinition patch in package.DialoguePatches ?? Array.Empty<DialoguePatchDefinition>())
            {
                string scene = patch?.anchor?.scene;
                if (string.IsNullOrWhiteSpace(scene) || !scenes.Contains(LoaderUtil.NormalizeScene(scene)))
                {
                    continue;
                }

                if (!StoryPatchService.WasDialoguePatchAppliedForDiagnostics(package.Id, patch.id, scene))
                {
                    failures++;
                    SunnyModLoaderPlugin.Log.LogError(
                        "Startup validation did not observe dialogue patch application " + package.Id + ":" + patch.id + ".");
                    continue;
                }

                tested++;
            }
        }

        SunnyModLoaderPlugin.Log.LogInfo(
            "Startup validation dialogue patches completed: " + tested + " passed, " + failures + " failed.");
        return failures == 0;
    }

    internal static IReadOnlyCollection<string> GetAudioUris(ModRegistry registry)
    {
        HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ModPackage package in registry.ActiveLowToHigh)
        {
            foreach (FlowDefinition flow in package.Flows)
            {
                foreach (string audio in flow.AudioResources)
                {
                    result.Add(audio);
                }
                foreach (FlowVoiceBinding voice in flow.Voices)
                {
                    if (!string.IsNullOrWhiteSpace(voice.Uri))
                    {
                        result.Add(voice.Uri);
                    }
                }
            }

            DialoguePatchDefinition[] patches = package.DialoguePatches;
            foreach (DialoguePatchDefinition patch in patches ?? Array.Empty<DialoguePatchDefinition>())
            {
                if (!string.IsNullOrWhiteSpace(patch?.set?.voice))
                {
                    result.Add(LoaderUtil.ToModUri(package.Id, patch.set.voice));
                }
            }

            if (package.Overlays == null)
            {
                continue;
            }

            foreach (OverlayDefinition overlay in package.Overlays)
            {
                if (overlay != null && string.Equals(overlay.kind, "Audio", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(overlay.source))
                {
                    result.Add(LoaderUtil.ToModUri(package.Id, overlay.source));
                }
            }
        }

        return result;
    }

    internal static IReadOnlyCollection<string> GetTextureUris(ModRegistry registry)
    {
        HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ModPackage package in registry.ActiveLowToHigh)
        {
            if (package.Gallery != null)
            {
                foreach (GalleryDefinition entry in package.Gallery)
                {
                    if (!string.IsNullOrWhiteSpace(entry?.image))
                    {
                        result.Add(LoaderUtil.ToModUri(package.Id, entry.image));
                    }

                    if (!string.IsNullOrWhiteSpace(entry?.thumbnail))
                    {
                        result.Add(LoaderUtil.ToModUri(package.Id, entry.thumbnail));
                    }
                }
            }

            foreach (SpriteDefinition sprite in package.Sprites ?? Array.Empty<SpriteDefinition>())
            {
                foreach (SpriteImageDefinition image in sprite.bases.Concat(sprite.emotions))
                {
                    if (!string.IsNullOrWhiteSpace(image?.source))
                    {
                        result.Add(image.source);
                    }
                }
            }

            if (package.Overlays == null)
            {
                continue;
            }

            foreach (OverlayDefinition overlay in package.Overlays)
            {
                if (overlay != null && string.Equals(overlay.kind, "Texture", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(overlay.source))
                {
                    result.Add(LoaderUtil.ToModUri(package.Id, overlay.source));
                }
            }
        }

        return result;
    }

    private static bool ValidateFrameCodec()
    {
        try
        {
            BranchFramePayload expected = new BranchFramePayload
            {
                frames = new[]
                {
                    new BranchFrame
                    {
                        modId = "diagnostic.mod",
                        branchId = "roundtrip",
                        frameId = "diagnostic-frame",
                        scene = "Script/vol1",
                        returnIndex = 123,
                        returnAfterCall = true,
                        background = new BranchBackgroundState
                        {
                            path = "BGD/test.png",
                            x = 1f,
                            y = 2f,
                            z = 3f,
                            scale = 1.5f
                        },
                        music = new BranchMusicState { path = "Music/test.ogg", volume = 0.6f, isPlaying = true },
                        characters = new[]
                        {
                            new BranchCharacterState
                            {
                                name = "diagnostic-character",
                                visible = true,
                                x = 10f,
                                y = 20f,
                                scale = 1.2f,
                                emotion = "smile",
                                illustration = "coat"
                            }
                        }
                    }
                }
            };
            string json = JsonCodec.Serialize(expected);
            BranchFramePayload actual = JsonCodec.Deserialize<BranchFramePayload>(json);
            bool valid = actual?.frames != null && actual.frames.Length == 1 &&
                          actual.frames[0].modId == expected.frames[0].modId &&
                          actual.frames[0].branchId == expected.frames[0].branchId &&
                          actual.frames[0].frameId == expected.frames[0].frameId &&
                          actual.frames[0].scene == expected.frames[0].scene &&
                          actual.frames[0].returnIndex == expected.frames[0].returnIndex &&
                          actual.frames[0].returnAfterCall &&
                          actual.frames[0].background?.path == expected.frames[0].background.path &&
                          Math.Abs(actual.frames[0].background.scale - 1.5f) < 0.001f &&
                          actual.frames[0].music?.path == expected.frames[0].music.path &&
                          Math.Abs(actual.frames[0].music.volume - 0.6f) < 0.001f &&
                          actual.frames[0].characters?.Length == 1 &&
                          actual.frames[0].characters[0].name == "diagnostic-character" &&
                          Math.Abs(actual.frames[0].characters[0].scale - 1.2f) < 0.001f;
            if (!valid)
            {
                SunnyModLoaderPlugin.Log.LogError("Startup validation failed the Mod return-stack JSON round trip.");
                return false;
            }

            SunnyModLoaderPlugin.Log.LogInfo("Startup validation passed the Mod return-stack JSON round trip.");
            return true;
        }
        catch (Exception ex)
        {
            SunnyModLoaderPlugin.Log.LogError("Startup validation failed the Mod return-stack JSON round trip: " + ex);
            return false;
        }
    }

    private static bool ValidateExternalSpriteConfigs(ModRegistry registry)
    {
        int tested = 0;
        CharacterPanel panel = UnityEngine.Object.FindFirstObjectByType<CharacterPanel>() ??
                               Resources.FindObjectsOfTypeAll<CharacterPanel>()
                                   .FirstOrDefault(candidate => candidate != null && candidate.gameObject.scene.IsValid() &&
                                                                candidate.staticCharacterImagePrefab != null);

        foreach (ModPackage package in registry.ActiveLowToHigh)
        {
            foreach (SpriteDefinition definition in package.Sprites ?? Array.Empty<SpriteDefinition>())
            {
                if (!SpriteService.TryGetCharacterConfig(definition.internalName, out CharacterConfig config) ||
                    config == null || config.BaseMap == null || config.BaseMap.Count != definition.bases.Count ||
                    definition.bases.Any(image => config.GetBaseSprite(image.id) == null) ||
                    definition.emotions.Any(image => config.GetEmotionSprite(image.id) == null))
                {
                    SunnyModLoaderPlugin.Log.LogError(
                        "Startup validation failed external sprite config " + package.Id + ":" + definition.id + ".");
                    return false;
                }

                if (panel != null)
                {
                    ICharacterRenderer renderer = panel.CreateCharacterImage(new CharacterBase(definition.internalName));
                    if (!(renderer is CharacterImage image) || image.illustrationImage == null ||
                        image.illustrationImage.raycastTarget ||
                        (image.emotionImage != null && image.emotionImage.raycastTarget) ||
                        image.GetComponent<CanvasGroup>()?.blocksRaycasts != false)
                    {
                        SunnyModLoaderPlugin.Log.LogError(
                            "Startup validation failed external sprite renderer " + package.Id + ":" + definition.id + ".");
                        if (renderer is MonoBehaviour invalidRenderer && invalidRenderer != null)
                        {
                            UnityEngine.Object.DestroyImmediate(invalidRenderer.gameObject);
                        }
                        return false;
                    }
                    UnityEngine.Object.DestroyImmediate(image.gameObject);
                }
                tested++;
            }
        }

        SunnyModLoaderPlugin.Log.LogInfo(
            "Startup validation passed " + tested + " external sprite character config(s); renderer prefab " +
            (panel == null ? "was not loaded in the startup scene" : "instantiation passed") + ".");
        return true;
    }

    private static bool ValidateBranchRoundTrips(ModRegistry registry)
    {
        if (CurrentSceneScriptsField == null)
        {
            SunnyModLoaderPlugin.Log.LogError("Startup validation could not access CorePlayer.currentSceneScripts.");
            return false;
        }

        GameObject host = new GameObject("SunnyModLoader Startup Validation");
        host.SetActive(false);
        CorePlayer player = host.AddComponent<CorePlayer>();
        int tested = 0;
        int failures = 0;
        try
        {
            foreach (ModPackage package in registry.ActiveLowToHigh)
            {
                BranchOptionDefinition[] branches = package.Branches;
                if (branches == null)
                {
                    continue;
                }

                foreach (BranchOptionDefinition branch in branches)
                {
                    if (branch.continueCurrent)
                    {
                        continue;
                    }

                    player.LoadSceneScripts(branch.scene);
                    SceneScripts baseScripts = GetCurrentScripts(player);
                    if (!SceneMatches(player.currentScene, branch.scene) || baseScripts?.scripts == null)
                    {
                        failures++;
                        SunnyModLoaderPlugin.Log.LogError(
                            "Startup validation could not prepare branch " + package.Id + ":" + branch.id + ".");
                        continue;
                    }

                    int triggerIndex = -1;
                    RuntimeBranch runtimeBranch = null;
                    for (int scriptIndex = 0; scriptIndex < baseScripts.scripts.Count; scriptIndex++)
                    {
                        runtimeBranch = StoryPatchService.GetBranches(branch.scene, scriptIndex)
                            .FirstOrDefault(candidate =>
                                string.Equals(candidate.Package.Id, package.Id, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(candidate.Definition.id, branch.id, StringComparison.OrdinalIgnoreCase));
                        if (runtimeBranch != null)
                        {
                            triggerIndex = scriptIndex;
                            break;
                        }
                    }
                    if (runtimeBranch == null)
                    {
                        failures++;
                        SunnyModLoaderPlugin.Log.LogError(
                            "Startup validation did not find registered branch " + package.Id + ":" + branch.id + ".");
                        continue;
                    }

                    if (!BranchService.IsVisibleForDiagnostics(runtimeBranch, player))
                    {
                        SunnyModLoaderPlugin.Log.LogInfo(
                            "Startup validation skipped disabled branch " + package.Id + ":" + branch.id + ".");
                        continue;
                    }

                    if (!BranchService.TryEnterBranchForDiagnostics(
                            player,
                            runtimeBranch,
                            branch.scene,
                            triggerIndex))
                    {
                        failures++;
                        continue;
                    }

                    string storyUri = LoaderUtil.ToModUri(package.Id, branch.story);
                    SceneScripts modScripts = GetCurrentScripts(player);
                    ScriptBase returnScript = FindReturnScript(modScripts);
                    if (!SceneMatches(player.currentScene, storyUri) || returnScript == null ||
                        BranchService.GetReturnDepthForDiagnostics(player) != 1)
                    {
                        failures++;
                        SunnyModLoaderPlugin.Log.LogError(
                            "Startup validation entered branch but could not prepare return " + package.Id + ":" + branch.id + ".");
                        continue;
                    }

                    BranchService.TryReturnFromCommand(returnScript.parameters.ToArray(), player);
                    bool returned = SceneMatches(player.currentScene, branch.scene) &&
                                    player.currentScriptIndex == triggerIndex - 1 &&
                                    BranchService.GetReturnDepthForDiagnostics(player) == 0 &&
                                    BranchService.ConsumeBypassForDiagnostics(player, branch.scene, triggerIndex);
                    if (!returned)
                    {
                        failures++;
                        SunnyModLoaderPlugin.Log.LogError(
                            "Startup validation failed branch return " + package.Id + ":" + branch.id + ".");
                        continue;
                    }

                    tested++;
                    SunnyModLoaderPlugin.Log.LogInfo(
                        "Startup validation passed branch enter/return " + package.Id + ":" + branch.id +
                        " at " + branch.scene + "#" + triggerIndex + ".");
                }
            }
        }
        catch (Exception ex)
        {
            failures++;
            SunnyModLoaderPlugin.Log.LogError("Startup validation branch round trip failed unexpectedly: " + ex);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(host);
        }

        SunnyModLoaderPlugin.Log.LogInfo(
            "Startup validation branch round trips completed: " + tested + " passed, " + failures + " failed.");
        return failures == 0;
    }

    private static bool ValidateNestedBranchRoundTrips(ModRegistry registry)
    {
        if (CurrentSceneScriptsField == null)
        {
            return false;
        }

        GameObject host = new GameObject("SunnyModLoader Nested Branch Validation");
        host.SetActive(false);
        CorePlayer player = host.AddComponent<CorePlayer>();
        int tested = 0;
        int failures = 0;
        try
        {
            foreach (ModPackage package in registry.ActiveLowToHigh)
            {
                BranchOptionDefinition[] branches = package.Branches ?? Array.Empty<BranchOptionDefinition>();
                foreach (BranchOptionDefinition outer in branches.Where(branch => branch != null && !branch.continueCurrent))
                {
                    string outerStoryUri = LoaderUtil.ToModUri(package.Id, outer.story);
                    BranchOptionDefinition nested = branches.FirstOrDefault(branch =>
                        branch != null && !branch.continueCurrent &&
                        !string.Equals(branch.id, outer.id, StringComparison.OrdinalIgnoreCase) &&
                        SceneMatches(branch.scene, outerStoryUri));
                    if (nested == null)
                    {
                        continue;
                    }

                    player.LoadSceneScripts(outer.scene);
                    if (!TryFindRuntimeBranch(player, package, outer, out RuntimeBranch outerRuntime, out int outerIndex) ||
                        !BranchService.IsVisibleForDiagnostics(outerRuntime, player) ||
                        !BranchService.TryEnterBranchForDiagnostics(player, outerRuntime, outer.scene, outerIndex) ||
                        BranchService.GetReturnDepthForDiagnostics(player) != 1)
                    {
                        failures++;
                        SunnyModLoaderPlugin.Log.LogError(
                            "Startup validation could not enter outer nested branch " + package.Id + ":" + outer.id + ".");
                        continue;
                    }

                    if (!TryFindRuntimeBranch(player, package, nested, out RuntimeBranch nestedRuntime, out int nestedIndex) ||
                        !BranchService.IsVisibleForDiagnostics(nestedRuntime, player) ||
                        !BranchService.TryEnterBranchForDiagnostics(player, nestedRuntime, outerStoryUri, nestedIndex) ||
                        BranchService.GetReturnDepthForDiagnostics(player) != 2)
                    {
                        failures++;
                        SunnyModLoaderPlugin.Log.LogError(
                            "Startup validation could not enter inner nested branch " + package.Id + ":" + nested.id + ".");
                        continue;
                    }

                    ScriptBase innerReturn = FindReturnScript(GetCurrentScripts(player));
                    if (innerReturn == null)
                    {
                        failures++;
                        continue;
                    }
                    BranchService.TryReturnFromCommand(innerReturn.parameters.ToArray(), player);
                    bool returnedToOuter = SceneMatches(player.currentScene, outerStoryUri) &&
                                           player.currentScriptIndex == nestedIndex - 1 &&
                                           BranchService.GetReturnDepthForDiagnostics(player) == 1 &&
                                           BranchService.ConsumeBypassForDiagnostics(player, outerStoryUri, nestedIndex);
                    if (!returnedToOuter)
                    {
                        failures++;
                        SunnyModLoaderPlugin.Log.LogError(
                            "Startup validation failed B -> A nested return " + package.Id + ":" + nested.id + ".");
                        continue;
                    }

                    ScriptBase outerReturn = FindReturnScript(GetCurrentScripts(player));
                    if (outerReturn == null)
                    {
                        failures++;
                        continue;
                    }
                    BranchService.TryReturnFromCommand(outerReturn.parameters.ToArray(), player);
                    bool returnedToBase = SceneMatches(player.currentScene, outer.scene) &&
                                          player.currentScriptIndex == outerIndex - 1 &&
                                          BranchService.GetReturnDepthForDiagnostics(player) == 0 &&
                                          BranchService.ConsumeBypassForDiagnostics(player, outer.scene, outerIndex);
                    if (!returnedToBase)
                    {
                        failures++;
                        SunnyModLoaderPlugin.Log.LogError(
                            "Startup validation failed A -> base nested return " + package.Id + ":" + outer.id + ".");
                        continue;
                    }

                    tested++;
                    SunnyModLoaderPlugin.Log.LogInfo(
                        "Startup validation passed nested branch base -> A -> B -> A -> base for " +
                        package.Id + ":" + outer.id + " -> " + nested.id + ".");
                }
            }
        }
        catch (Exception ex)
        {
            failures++;
            SunnyModLoaderPlugin.Log.LogError("Startup validation nested branch round trip failed unexpectedly: " + ex);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(host);
        }

        SunnyModLoaderPlugin.Log.LogInfo(
            "Startup validation nested branch round trips completed: " + tested + " passed, " + failures + " failed.");
        return failures == 0;
    }

    private static bool ValidateCallRoundTrips(ModRegistry registry)
    {
        if (CurrentSceneScriptsField == null)
        {
            return false;
        }

        GameObject host = new GameObject("SunnyModLoader Call Validation");
        host.SetActive(false);
        CorePlayer player = host.AddComponent<CorePlayer>();
        int tested = 0;
        int failures = 0;
        try
        {
            foreach (ModPackage package in registry.ActiveLowToHigh)
            {
                foreach (FlowDefinition flow in package.Flows.Where(candidate => candidate.Calls.Count > 0))
                {
                    player.LoadSceneScripts(flow.SceneUri);
                    SceneScripts sourceScripts = GetCurrentScripts(player);
                    if (sourceScripts?.scripts == null)
                    {
                        failures++;
                        continue;
                    }

                    for (int index = 0; index < sourceScripts.scripts.Count; index++)
                    {
                        ScriptBase script = sourceScripts.scripts[index];
                        if (script == null || !string.Equals(script.command, "switchscene", StringComparison.OrdinalIgnoreCase) ||
                            !FlowService.TryParseCallUri(
                                LoaderUtil.GetStringParameter(script.parameters, "path"),
                                out string targetUri,
                                out _))
                        {
                            continue;
                        }

                        player.currentScriptIndex = index;
                        BranchService.TryCallFromCommand(script.parameters.ToArray(), player);
                        ScriptBase returnScript = FindReturnScript(GetCurrentScripts(player));
                        if (!SceneMatches(player.currentScene, targetUri) || returnScript == null ||
                            BranchService.GetReturnDepthForDiagnostics(player) != 1)
                        {
                            failures++;
                            SunnyModLoaderPlugin.Log.LogError(
                                "Startup validation could not enter call " + flow.SceneUri + "#" + index + ".");
                            player.LoadSceneScripts(flow.SceneUri);
                            continue;
                        }

                        BranchService.TryReturnFromCommand(returnScript.parameters.ToArray(), player);
                        bool returned = SceneMatches(player.currentScene, flow.SceneUri) &&
                                        player.currentScriptIndex == index &&
                                        BranchService.GetReturnDepthForDiagnostics(player) == 0 &&
                                        !BranchService.ConsumeBypassForDiagnostics(player, flow.SceneUri, index + 1);
                        if (!returned)
                        {
                            failures++;
                            SunnyModLoaderPlugin.Log.LogError(
                                "Startup validation failed call return " + flow.SceneUri + "#" + index + ".");
                            continue;
                        }

                        tested++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            failures++;
            SunnyModLoaderPlugin.Log.LogError("Startup validation call round trip failed unexpectedly: " + ex);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(host);
        }

        SunnyModLoaderPlugin.Log.LogInfo(
            "Startup validation call round trips completed: " + tested + " passed, " + failures + " failed.");
        return failures == 0;
    }

    private static bool TryFindRuntimeBranch(
        CorePlayer player,
        ModPackage package,
        BranchOptionDefinition definition,
        out RuntimeBranch runtimeBranch,
        out int triggerIndex)
    {
        runtimeBranch = null;
        triggerIndex = -1;
        SceneScripts scripts = GetCurrentScripts(player);
        if (scripts?.scripts == null)
        {
            return false;
        }

        for (int scriptIndex = 0; scriptIndex < scripts.scripts.Count; scriptIndex++)
        {
            runtimeBranch = StoryPatchService.GetBranches(player.currentScene, scriptIndex)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Package.Id, package.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.Definition.id, definition.id, StringComparison.OrdinalIgnoreCase));
            if (runtimeBranch != null)
            {
                triggerIndex = scriptIndex;
                return true;
            }
        }

        return false;
    }

    private static bool ValidateChoiceAdvanceGuard()
    {
        GameObject host = new GameObject("SunnyModLoader Choice Guard Validation");
        host.SetActive(false);
        try
        {
            CorePlayer player = host.AddComponent<CorePlayer>();
            bool valid = BranchService.ValidateChoiceBlockForDiagnostics(player);
            if (valid)
            {
                SunnyModLoaderPlugin.Log.LogInfo("Startup validation passed pending-choice advance blocking.");
            }
            else
            {
                SunnyModLoaderPlugin.Log.LogError("Startup validation failed pending-choice advance blocking.");
            }

            return valid;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(host);
        }
    }

    private static bool ValidateManagerWindowInputScope()
    {
        Rect window = new Rect(100f, 100f, 400f, 300f);
        bool captured = false;
        bool insidePress = SunnyModLoaderPlugin.ShouldBlockManagerInput(
            window,
            new Vector2(200f, 200f),
            true,
            true,
            false,
            false,
            false,
            false,
            ref captured);
        bool capturedDragOutside = SunnyModLoaderPlugin.ShouldBlockManagerInput(
            window,
            new Vector2(50f, 50f),
            false,
            true,
            false,
            false,
            false,
            false,
            ref captured);
        bool capturedReleaseOutside = SunnyModLoaderPlugin.ShouldBlockManagerInput(
            window,
            new Vector2(50f, 50f),
            false,
            false,
            true,
            false,
            false,
            false,
            ref captured);

        bool outsidePress = SunnyModLoaderPlugin.ShouldBlockManagerInput(
            window,
            new Vector2(50f, 50f),
            true,
            true,
            false,
            false,
            false,
            false,
            ref captured);
        bool outsideDragInside = SunnyModLoaderPlugin.ShouldBlockManagerInput(
            window,
            new Vector2(200f, 200f),
            false,
            true,
            false,
            false,
            false,
            false,
            ref captured);
        bool idleInside = SunnyModLoaderPlugin.ShouldBlockManagerInput(
            window,
            new Vector2(200f, 200f),
            false,
            false,
            false,
            false,
            false,
            false,
            ref captured);
        bool scrollInside = SunnyModLoaderPlugin.ShouldBlockManagerInput(
            window,
            new Vector2(200f, 200f),
            false,
            false,
            false,
            true,
            false,
            false,
            ref captured);
        bool focusedKey = SunnyModLoaderPlugin.ShouldBlockManagerInput(
            window,
            new Vector2(50f, 50f),
            false,
            false,
            false,
            false,
            true,
            true,
            ref captured);
        bool keyboardClassification = SunnyModLoaderPlugin.IsManagerKeyboardKey(KeyCode.A) &&
                                      SunnyModLoaderPlugin.IsManagerKeyboardKey(KeyCode.LeftControl) &&
                                      !SunnyModLoaderPlugin.IsManagerKeyboardKey(KeyCode.Mouse0) &&
                                      !SunnyModLoaderPlugin.IsManagerKeyboardKey(KeyCode.WheelDown) &&
                                      !SunnyModLoaderPlugin.IsManagerKeyboardKey(KeyCode.JoystickButton0);
        bool valid = insidePress && capturedDragOutside && capturedReleaseOutside &&
                     !outsidePress && !outsideDragInside && !idleInside && scrollInside && focusedKey &&
                     keyboardClassification && !captured;
        if (valid)
        {
            SunnyModLoaderPlugin.Log.LogInfo("Startup validation passed manager window input scoping.");
        }
        else
        {
            SunnyModLoaderPlugin.Log.LogError("Startup validation failed manager window input scoping.");
        }

        return valid;
    }

    private static bool ValidateReusableReturnSnapshot()
    {
        GameObject host = new GameObject("SunnyModLoader Return Snapshot Validation");
        host.SetActive(false);
        try
        {
            CorePlayer player = host.AddComponent<CorePlayer>();
            bool valid = BranchService.ValidateSnapshotReuseForDiagnostics(player);
            if (valid)
            {
                SunnyModLoaderPlugin.Log.LogInfo("Startup validation passed reusable Mod return snapshots.");
            }
            else
            {
                SunnyModLoaderPlugin.Log.LogError("Startup validation failed reusable Mod return snapshots.");
            }

            return valid;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(host);
        }
    }

    private static SceneScripts GetCurrentScripts(CorePlayer player)
    {
        return CurrentSceneScriptsField.GetValue(player) as SceneScripts;
    }

    private static bool ValidateFlowVoiceBindings(string story, SceneScripts scripts, ModRegistry registry)
    {
        if (!registry.TryGetFlow(story, out FlowDefinition flow) || flow.Voices.Count == 0)
        {
            return true;
        }

        List<ScriptBase> dialogues = scripts.scripts
            .Where(script => script != null && string.Equals(script.command, "dialogue", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (FlowVoiceBinding binding in flow.Voices)
        {
            if (binding.DialogueOrdinal < 0 || binding.DialogueOrdinal >= dialogues.Count ||
                !string.Equals(
                    LoaderUtil.GetStringParameter(dialogues[binding.DialogueOrdinal].parameters, "audiopath"),
                    binding.Uri,
                    StringComparison.OrdinalIgnoreCase))
            {
                SunnyModLoaderPlugin.Log.LogError(
                    "Startup validation failed flow voice binding " + story + "#" + binding.DialogueOrdinal + ".");
                return false;
            }
        }

        SunnyModLoaderPlugin.Log.LogInfo(
            "Startup validation passed " + flow.Voices.Count + " inline flow voice binding(s) in " + story + ".");
        return true;
    }

    private static bool ValidateFlowSpineCharacters(string story, SceneScripts scripts)
    {
        List<string> characterIds = scripts.scripts
            .Where(script => script != null &&
                             string.Equals(script.command, "character", StringComparison.OrdinalIgnoreCase))
            .Select(script => LoaderUtil.GetStringParameter(script.parameters, "id"))
            .Where(id => !string.IsNullOrWhiteSpace(id) && !SpriteService.IsKnownCharacter(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (characterIds.Count == 0)
        {
            return true;
        }

        CharacterSpineConfig[] configs = Resources.LoadAll<CharacterSpineConfig>("CharacterSpineConfigs");
        foreach (string characterId in characterIds)
        {
            CharacterSpineConfig config = configs.FirstOrDefault(candidate =>
                candidate != null &&
                (string.Equals(candidate.CharacterName, characterId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(candidate.CharacterShortName, characterId, StringComparison.OrdinalIgnoreCase)));
            if (config == null || config.SpinePrefab == null)
            {
                string available = string.Join(", ", configs
                    .Where(candidate => candidate != null)
                    .SelectMany(candidate => new[] { candidate.CharacterName, candidate.CharacterShortName })
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
                SunnyModLoaderPlugin.Log.LogError(
                    "Startup validation could not resolve existing Spine character '" + characterId +
                    "' in " + story + ". Available configured names: " + available);
                return false;
            }

            SunnyModLoaderPlugin.Log.LogInfo(
                "Startup validation resolved existing Spine character " + characterId +
                " to prefab " + config.SpinePrefab.name + " in " + story + ".");
        }

        return true;
    }

    private static ScriptBase FindReturnScript(SceneScripts scripts)
    {
        if (scripts?.scripts == null)
        {
            return null;
        }

        foreach (ScriptBase script in scripts.scripts)
        {
            if (script != null && string.Equals(script.command, "switchscene", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    LoaderUtil.GetStringParameter(script.parameters, "path"),
                    "mod://return",
                    StringComparison.OrdinalIgnoreCase))
            {
                return script;
            }
        }

        return null;
    }

    private static bool SceneMatches(string actual, string expected)
    {
        return string.Equals(
            LoaderUtil.NormalizeScene(actual),
            LoaderUtil.NormalizeScene(expected),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEnabled(string name)
    {
        string value = Environment.GetEnvironmentVariable(name);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
