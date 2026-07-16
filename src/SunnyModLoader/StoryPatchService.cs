using System;
using System.Collections.Generic;
using System.Linq;
using MakeineGalGameQM.Core.Utils;

namespace SunnyModLoader;

internal sealed class RuntimeBranch
{
    internal ModPackage Package;
    internal BranchOptionDefinition Definition;
    internal string Scene;
    internal int TriggerIndex;
}

internal static class StoryPatchService
{
    private static readonly Dictionary<string, List<RuntimeBranch>> BranchesByLocation =
        new Dictionary<string, List<RuntimeBranch>>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AppliedDialoguePatches =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    internal static void Apply(string sceneName, SceneScripts sceneScripts, ModRegistry registry)
    {
        if (sceneScripts?.scripts == null)
        {
            return;
        }

        Dictionary<ScriptBase, DialogueBaseline> baselines = CaptureDialogueBaselines(sceneScripts);
        ApplyFlowVoices(sceneName, sceneScripts, registry);
        foreach (ModPackage package in registry.ActiveLowToHigh)
        {
            ApplyDialoguePatches(sceneName, sceneScripts, package, registry, baselines);
        }

        RegisterSceneBranches(sceneName, sceneScripts, registry, baselines);
    }

    private static void ApplyFlowVoices(string sceneName, SceneScripts sceneScripts, ModRegistry registry)
    {
        if (!registry.TryGetFlow(sceneName, out FlowDefinition flow) || flow.Voices.Count == 0)
        {
            return;
        }

        List<ScriptBase> dialogues = sceneScripts.scripts
            .Where(script => script != null && string.Equals(script.command, "dialogue", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (FlowVoiceBinding binding in flow.Voices)
        {
            if (binding.DialogueOrdinal < 0 || binding.DialogueOrdinal >= dialogues.Count)
            {
                SunnyModLoaderPlugin.Log.LogError(
                    "Flow voice dialogue ordinal is out of range " + flow.SceneUri + "#" + binding.DialogueOrdinal + ".");
                continue;
            }

            ScriptBase dialogue = dialogues[binding.DialogueOrdinal];
            string speaker = LoaderUtil.GetStringParameter(dialogue.parameters, "id") ?? string.Empty;
            string text = LoaderUtil.GetStringParameter(dialogue.parameters, "content") ?? string.Empty;
            if (!string.Equals(binding.Speaker, speaker, StringComparison.Ordinal) ||
                !string.Equals(binding.Text, text, StringComparison.Ordinal))
            {
                SunnyModLoaderPlugin.Log.LogError(
                    "Flow voice dialogue changed after parsing " + flow.SceneUri + "#" + binding.DialogueOrdinal + ".");
                continue;
            }

            LoaderUtil.SetStringParameter(dialogue.parameters, "audiopath", binding.Uri);
            registry.RegisterVoiceVolume(binding.Uri, binding.Volume);
        }
    }

    internal static void RebuildBranchPoints(ModRegistry registry)
    {
        BranchesByLocation.Clear();
        AppliedDialoguePatches.Clear();
    }

    internal static bool WasDialoguePatchAppliedForDiagnostics(string modId, string patchId, string scene)
    {
        return AppliedDialoguePatches.Contains(MakeDialoguePatchKey(modId, patchId, scene));
    }

    internal static IReadOnlyList<RuntimeBranch> GetBranches(string scene, int scriptIndex)
    {
        string key = MakeLocationKey(scene, scriptIndex);
        return BranchesByLocation.TryGetValue(key, out List<RuntimeBranch> branches)
            ? branches
            : Array.Empty<RuntimeBranch>();
    }

    private static void ApplyDialoguePatches(
        string sceneName,
        SceneScripts sceneScripts,
        ModPackage package,
        ModRegistry registry,
        IReadOnlyDictionary<ScriptBase, DialogueBaseline> baselines)
    {
        DialoguePatchDefinition[] patches = package.DialoguePatches;
        if (patches == null)
        {
            return;
        }

        foreach (DialoguePatchDefinition patch in patches)
        {
            DialogueAnchorDefinition anchor = patch?.anchor;
            if (anchor == null || !SceneMatches(anchor.scene, sceneName))
            {
                continue;
            }

            if (!TryFindDialogue(
                    sceneScripts,
                    anchor,
                    baselines,
                    out ScriptBase dialogue,
                    out _,
                    out string failure))
            {
                SunnyModLoaderPlugin.Log.LogError("Dialogue patch anchor failed " + package.Id + ":" + patch.id + " - " + failure);
                continue;
            }

            DialogueSetDefinition set = patch.set;
            if (set == null)
            {
                continue;
            }

            if (set.text != null)
            {
                LoaderUtil.SetStringParameter(dialogue.parameters, "content", set.text);
            }

            if (!string.IsNullOrWhiteSpace(set.voice))
            {
                string voiceUri = LoaderUtil.ToModUri(package.Id, set.voice);
                LoaderUtil.SetStringParameter(dialogue.parameters, "audiopath", voiceUri);
                registry.RegisterVoiceVolume(voiceUri, set.volume);
            }

            AppliedDialoguePatches.Add(MakeDialoguePatchKey(package.Id, patch.id, sceneName));
        }
    }

    private static void RegisterSceneBranches(
        string sceneName,
        SceneScripts sceneScripts,
        ModRegistry registry,
        IReadOnlyDictionary<ScriptBase, DialogueBaseline> baselines)
    {
        string normalizedScene = LoaderUtil.NormalizeScene(sceneName);
        foreach (ModPackage package in registry.ActiveHighToLow)
        {
            BranchOptionDefinition[] branches = package.Branches;
            if (branches == null)
            {
                continue;
            }

            foreach (BranchOptionDefinition branch in branches)
            {
                if (branch == null || !SceneMatches(branch.scene, sceneName))
                {
                    continue;
                }

                int triggerIndex;
                if (branch.anchor != null)
                {
                    DialogueAnchorDefinition dialogueAnchor = new DialogueAnchorDefinition
                    {
                        afterLabel = branch.anchor.afterLabel,
                        dialogueOrdinal = branch.anchor.dialogueOrdinal,
                        expectedSpeaker = branch.anchor.expectedSpeaker,
                        expectedText = branch.anchor.expectedText
                    };
                    if (!TryFindDialogue(
                            sceneScripts,
                            dialogueAnchor,
                            baselines,
                            out _,
                            out int dialogueIndex,
                            out string failure))
                    {
                        SunnyModLoaderPlugin.Log.LogError(
                            "Branch dialogue anchor failed " + package.Id + ":" + branch.id + " - " + failure);
                        continue;
                    }

                    triggerIndex = dialogueIndex + 1;
                }
                else
                {
                    if (sceneScripts.markToIndexDic == null ||
                        !sceneScripts.markToIndexDic.TryGetValue(branch.afterLabel, out int markIndex))
                    {
                        SunnyModLoaderPlugin.Log.LogError(
                            "Branch label not found " + package.Id + ":" + branch.id + " -> " + branch.afterLabel);
                        continue;
                    }

                    triggerIndex = markIndex + 1;
                }

                string key = MakeLocationKey(normalizedScene, triggerIndex);
                if (!BranchesByLocation.TryGetValue(key, out List<RuntimeBranch> list))
                {
                    list = new List<RuntimeBranch>();
                    BranchesByLocation[key] = list;
                }

                if (list.Any(item => string.Equals(item.Package.Id, package.Id, StringComparison.OrdinalIgnoreCase) &&
                                     string.Equals(item.Definition.id, branch.id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                list.Add(new RuntimeBranch
                {
                    Package = package,
                    Definition = branch,
                    Scene = normalizedScene,
                    TriggerIndex = triggerIndex
                });
            }
        }
    }

    private static bool TryFindDialogue(
        SceneScripts sceneScripts,
        DialogueAnchorDefinition anchor,
        IReadOnlyDictionary<ScriptBase, DialogueBaseline> baselines,
        out ScriptBase dialogue,
        out int dialogueIndex,
        out string failure)
    {
        dialogue = null;
        dialogueIndex = -1;
        failure = null;
        if (string.IsNullOrWhiteSpace(anchor?.afterLabel) || sceneScripts.markToIndexDic == null ||
            !sceneScripts.markToIndexDic.TryGetValue(anchor.afterLabel, out int markIndex))
        {
            failure = "label not found: " + (anchor?.afterLabel ?? "<null>");
            return false;
        }

        int ordinal = 0;
        ScriptBase exactMatch = null;
        int exactMatchIndex = -1;
        int exactMatchCount = 0;
        for (int i = markIndex + 1; i < sceneScripts.scripts.Count; i++)
        {
            ScriptBase script = sceneScripts.scripts[i];
            if (script == null)
            {
                continue;
            }

            if (string.Equals(script.command, "mark", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (!string.Equals(script.command, "dialogue", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int currentOrdinal = ordinal++;
            DialogueBaseline baseline = baselines.TryGetValue(script, out DialogueBaseline captured)
                ? captured
                : CaptureDialogueBaseline(script);
            string speaker = baseline.Speaker;
            string content = baseline.Content;

            if (anchor.dialogueOrdinal < 0)
            {
                if ((!string.IsNullOrEmpty(anchor.expectedSpeaker) &&
                     !string.Equals(anchor.expectedSpeaker, speaker, StringComparison.Ordinal)) ||
                    !string.Equals(anchor.expectedText, content, StringComparison.Ordinal))
                {
                    continue;
                }

                exactMatch = script;
                exactMatchIndex = i;
                exactMatchCount++;
                continue;
            }

            if (currentOrdinal != anchor.dialogueOrdinal)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(anchor.expectedSpeaker) && !string.Equals(anchor.expectedSpeaker, speaker, StringComparison.Ordinal))
            {
                failure = "speaker mismatch; expected " + anchor.expectedSpeaker + ", got " + speaker;
                return false;
            }

            if (!string.IsNullOrEmpty(anchor.expectedText) && !string.Equals(anchor.expectedText, content, StringComparison.Ordinal))
            {
                failure = "text mismatch";
                return false;
            }

            dialogue = script;
            dialogueIndex = i;
            return true;
        }

        if (anchor.dialogueOrdinal < 0)
        {
            if (exactMatchCount == 1)
            {
                dialogue = exactMatch;
                dialogueIndex = exactMatchIndex;
                return true;
            }

            failure = exactMatchCount == 0
                ? "exact dialogue text was not found under label " + anchor.afterLabel
                : "exact dialogue text matched " + exactMatchCount + " lines; add line/dialogueOrdinal";
            return false;
        }

        failure = "dialogue ordinal out of range: " + anchor.dialogueOrdinal;
        return false;
    }

    private static Dictionary<ScriptBase, DialogueBaseline> CaptureDialogueBaselines(SceneScripts sceneScripts)
    {
        Dictionary<ScriptBase, DialogueBaseline> result = new Dictionary<ScriptBase, DialogueBaseline>();
        foreach (ScriptBase script in sceneScripts.scripts)
        {
            if (script != null && string.Equals(script.command, "dialogue", StringComparison.OrdinalIgnoreCase))
            {
                result[script] = CaptureDialogueBaseline(script);
            }
        }

        return result;
    }

    private static DialogueBaseline CaptureDialogueBaseline(ScriptBase script)
    {
        return new DialogueBaseline(
            LoaderUtil.GetStringParameter(script?.parameters, "id") ?? string.Empty,
            LoaderUtil.GetStringParameter(script?.parameters, "content") ?? string.Empty);
    }

    private static bool SceneMatches(string expected, string actual)
    {
        return string.Equals(LoaderUtil.NormalizeScene(expected), LoaderUtil.NormalizeScene(actual), StringComparison.OrdinalIgnoreCase);
    }

    private static string MakeLocationKey(string scene, int scriptIndex)
    {
        return LoaderUtil.NormalizeScene(scene) + "#" + scriptIndex;
    }

    private static string MakeDialoguePatchKey(string modId, string patchId, string scene)
    {
        return (modId ?? string.Empty) + ":" + (patchId ?? string.Empty) + "@" + LoaderUtil.NormalizeScene(scene);
    }

    private readonly struct DialogueBaseline
    {
        internal DialogueBaseline(string speaker, string content)
        {
            Speaker = speaker;
            Content = content;
        }

        internal string Speaker { get; }
        internal string Content { get; }
    }
}
