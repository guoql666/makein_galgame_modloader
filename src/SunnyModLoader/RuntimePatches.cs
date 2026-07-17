using System;
using System.Reflection;
using HarmonyLib;
using MakeineGalGameQM.Core;
using MakeineGalGameQM.Core.Character;
using MakeineGalGameQM.Core.Commands;
using MakeineGalGameQM.Core.SaveAndLoad;
using MakeineGalGameQM.Core.Utils;

namespace SunnyModLoader;

[HarmonyPatch(typeof(SceneScriptsReader), nameof(SceneScriptsReader.ReadSceneScripts))]
internal static class ReadSceneScriptsPatch
{
    private static void Postfix(string fileName, SceneScripts __result)
    {
        try
        {
            StoryPatchService.Apply(fileName, __result, SunnyModLoaderPlugin.Registry);
        }
        catch (Exception ex)
        {
            SunnyModLoaderPlugin.Log.LogError("Failed to apply story patches for " + fileName + ": " + ex);
        }
    }
}

[HarmonyPatch(typeof(CommandManager), nameof(CommandManager.ExecuteCommand))]
internal static class ExecuteCommandPatch
{
    private static bool Prefix(ScriptBase script, CorePlayer player)
    {
        return !(SunnyModLoaderPlugin.ScreenEffects?.TryIntercept(script, player) ?? false) &&
               !BranchService.TryIntercept(script, player);
    }
}

[HarmonyPatch(typeof(Dialogue), nameof(Dialogue.Execute))]
internal static class DialogueVoicePatch
{
    private static void Prefix(ICommandParameter[] parameters)
    {
        SpriteService.ApplyDialogueAlias(parameters);
    }

    private static void Postfix(ICommandParameter[] parameters, CorePlayer player)
    {
        SunnyModLoaderPlugin.Voice.HandleDialogue(parameters, player);
    }
}

[HarmonyPatch(typeof(CharacterPanel), nameof(CharacterPanel.CreateCharacterImage))]
internal static class ExternalSpriteRendererPatch
{
    private static void Postfix(CharacterBase character, ICharacterRenderer __result)
    {
        SpriteService.ConfigureRenderer(character, __result);
    }
}

[HarmonyPatch(typeof(CharacterIllustrationManager), nameof(CharacterIllustrationManager.GetConfig))]
internal static class ExternalSpriteConfigPatch
{
    private static bool Prefix(string character, ref CharacterConfig __result)
    {
        if (!SpriteService.TryGetCharacterConfig(character, out CharacterConfig config))
        {
            return true;
        }

        __result = config;
        return false;
    }
}

[HarmonyPatch(typeof(Dialogue), nameof(Dialogue.UpdateExecuteResult))]
internal static class DialogueRollbackVoicePatch
{
    private static void Postfix(ICommandParameter[] parameters, CorePlayer player)
    {
        SunnyModLoaderPlugin.Voice.HandleDialogue(parameters, player);
    }
}

[HarmonyPatch(typeof(BackLogPanel), nameof(BackLogPanel.Show))]
internal static class BacklogChoiceShowGuardPatch
{
    private static bool Prefix(BackLogPanel __instance)
    {
        return BacklogVoiceReplayService.CanOpen(__instance);
    }
}

[HarmonyPatch(typeof(BackLogPanel), "Start")]
internal static class BacklogVoiceReplayStartPatch
{
    private static void Postfix(BackLogPanel __instance)
    {
        BacklogVoiceReplayService.OnPanelStarted(__instance);
    }
}

[HarmonyPatch(typeof(BackLogPanel), "Update")]
internal static class BacklogChoiceInteractionGuardPatch
{
    private static bool Prefix(BackLogPanel __instance)
    {
        return !BacklogVoiceReplayService.HideIfChoicePending(__instance);
    }
}

[HarmonyPatch(typeof(BackLogPanel), "OnRollbackClicked")]
internal static class BacklogRollbackChoiceGuardPatch
{
    private static bool Prefix(BackLogPanel __instance)
    {
        return BacklogVoiceReplayService.CanOpen(__instance);
    }
}

[HarmonyPatch(typeof(BackLogPanel), "RenderCurrentPage")]
internal static class BacklogVoiceReplayRenderPatch
{
    private static void Postfix(BackLogPanel __instance)
    {
        BacklogVoiceReplayService.RefreshPanel(__instance);
    }
}

[HarmonyPatch(typeof(BackLogPanel), "OnDestroy")]
internal static class BacklogVoiceReplayCleanupPatch
{
    private static void Prefix(BackLogPanel __instance)
    {
        BacklogVoiceReplayService.RemovePanel(__instance);
    }
}

[HarmonyPatch(typeof(CorePlayer), nameof(CorePlayer.Continue))]
internal static class ContinueVoicePatch
{
    private static bool Prefix(CorePlayer __instance)
    {
        if (AdvanceGuard.IsBlocked(__instance))
        {
            __instance.IsFastMode = false;
            __instance.IsTransientSkipActive = false;
            return false;
        }

        if (SunnyModAudioControl.StopVoiceOnAdvance)
        {
            SunnyModLoaderPlugin.Voice.Stop();
        }
        return true;
    }
}

internal static class AdvanceGuard
{
    internal static bool IsBlocked(CorePlayer player)
    {
        return SunnyModLoaderPlugin.ShouldBlockGameAdvance || BranchService.HasPendingChoice(player) ||
               (SunnyModLoaderPlugin.ScreenEffects?.HasBlockingEffect(player) ?? false);
    }
}

[HarmonyPatch]
internal static class ManagerAdvanceGuardPatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(CorePlayer),
            "Next",
            new[] { typeof(object) });
    }

    private static bool Prefix(CorePlayer __instance)
    {
        return !AdvanceGuard.IsBlocked(__instance);
    }
}

[HarmonyPatch]
internal static class ManagerPlaybackGuardPatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(CorePlayer), "Update", Type.EmptyTypes);
    }

    private static bool Prefix(CorePlayer __instance)
    {
        return !AdvanceGuard.IsBlocked(__instance);
    }
}

[HarmonyPatch(typeof(CorePlayer), nameof(CorePlayer.SkipToNextOption))]
internal static class SkipToNextOptionGuardPatch
{
    private static bool Prefix(CorePlayer __instance)
    {
        return !AdvanceGuard.IsBlocked(__instance);
    }
}

[HarmonyPatch(typeof(CorePlayer), nameof(CorePlayer.IsFastMode), MethodType.Setter)]
internal static class FastModeGuardPatch
{
    private static void Prefix(CorePlayer __instance, ref bool value)
    {
        if (value && AdvanceGuard.IsBlocked(__instance))
        {
            value = false;
        }
    }
}

[HarmonyPatch(typeof(CorePlayer), nameof(CorePlayer.IsTransientSkipActive), MethodType.Setter)]
internal static class TransientSkipGuardPatch
{
    private static void Prefix(CorePlayer __instance, ref bool value)
    {
        if (value && AdvanceGuard.IsBlocked(__instance))
        {
            value = false;
        }
    }
}

[HarmonyPatch]
internal static class LoadSceneScriptsPatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(CorePlayer), nameof(CorePlayer.LoadSceneScripts), new[] { typeof(string) });
    }

    private static void Prefix(CorePlayer __instance)
    {
        BranchService.CancelPendingChoice(__instance);
        SunnyModLoaderPlugin.Voice.Stop();
        SunnyModLoaderPlugin.ScreenEffects?.CancelForPlayer(__instance);
    }

    private static void Postfix(CorePlayer __instance)
    {
        SunnyModLoaderPlugin.Registry.SyncSettings(__instance);
    }
}

[HarmonyPatch(typeof(CorePlayer), nameof(CorePlayer.RestoreSnapshot))]
internal static class RestoreSnapshotPatch
{
    private static void Prefix(CorePlayer __instance)
    {
        BranchService.CancelPendingChoice(__instance);
        SunnyModLoaderPlugin.Voice.Stop();
        SunnyModLoaderPlugin.ScreenEffects?.CancelForPlayer(__instance);
    }

    private static void Postfix(CorePlayer __instance)
    {
        SunnyModLoaderPlugin.Registry.SyncSettings(__instance);
    }
}

[HarmonyPatch(typeof(SavedData), nameof(SavedData.Save))]
internal static class VanillaCompatibleSavePatch
{
    private static bool Prefix(SavedData __instance, out bool __state)
    {
        __state = ModSaveCompatibilityService.IsModSaveTarget(__instance);
        return ModSaveCompatibilityService.ProtectBeforeSave(__instance);
    }

    private static void Postfix(SavedData __instance, bool __state)
    {
        ModSaveCompatibilityService.CompleteSave(__instance, __state);
    }
}

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.LoadWithoutTransition))]
internal static class ModProgressLoadPatch
{
    private static void Prefix(
        SavedData data,
        out ModSaveCompatibilityService.LoadSwapState __state)
    {
        __state = ModSaveCompatibilityService.PrepareLoad(data);
    }

    private static Exception Finalizer(
        Exception __exception,
        SavedData data,
        ModSaveCompatibilityService.LoadSwapState __state)
    {
        ModSaveCompatibilityService.RestoreAfterLoad(data, __state);
        return __exception;
    }
}

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.DeleteSave))]
internal static class ModSaveSidecarDeletePatch
{
    private static void Postfix(int saveIndex)
    {
        ModSaveCompatibilityService.DeleteForSaveIndex(saveIndex);
    }
}

[HarmonyPatch(typeof(SwitchScene), nameof(SwitchScene.Execute))]
internal static class SwitchSceneReturnPatch
{
    private static bool Prefix(ICommandParameter[] parameters, CorePlayer player)
    {
        return !(SunnyModLoaderPlugin.ScreenEffects?.TryIntercept(parameters, player, false) ?? false) &&
               !BranchService.TryReturnFromCommand(parameters, player) &&
               !BranchService.TryCallFromCommand(parameters, player);
    }
}

[HarmonyPatch(typeof(SwitchScene), nameof(SwitchScene.LogicExecute))]
internal static class SwitchSceneLogicReturnPatch
{
    private static bool Prefix(ICommandParameter[] parameters, CorePlayer player)
    {
        return !(SunnyModLoaderPlugin.ScreenEffects?.TryIntercept(parameters, player, true) ?? false) &&
               !BranchService.TryReturnFromCommand(parameters, player) &&
               !BranchService.TryCallFromCommand(parameters, player);
    }
}
