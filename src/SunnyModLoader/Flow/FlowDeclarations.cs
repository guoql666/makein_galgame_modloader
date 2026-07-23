using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SunnyModLoader;

internal static partial class FlowService
{
    private static void ParseDirective(
        FlowDeclarationNode declaration,
        string packageRoot,
        string modId,
        string flowPath,
        HashSet<string> settingIds,
        HashSet<string> branchIds,
        HashSet<string> dialogueIds,
        HashSet<string> galleryIds,
        HashSet<string> overlayIds,
        HashSet<string> spriteIds,
        FlowPackageContent content,
        List<string> errors)
    {
        string location = flowPath + ":" + declaration.LineNumber;
        Dictionary<string, string> values = declaration.Values;

        switch (declaration.Kind.ToLowerInvariant())
        {
            case "branch":
                if (!NormalizeAliases(
                        values,
                        location,
                        errors,
                        "label", "afterLabel",
                        "line", "dialogueOrdinal",
                        "speaker", "expectedSpeaker",
                        "expect", "expectedText"))
                {
                    break;
                }
                ParseBranchDirective(
                    packageRoot,
                    modId,
                    flowPath,
                    declaration,
                    settingIds,
                    branchIds,
                    content,
                    errors);
                break;
            case "dialogue":
                if (!ValidateNoChildren(declaration, flowPath, errors))
                {
                    break;
                }
                if (!NormalizeDialogueAliases(values, location, errors))
                {
                    break;
                }
                ParseDialogueDirective(
                    packageRoot, modId, flowPath, location, "dialogue", values, dialogueIds, content, errors);
                break;
            case "text":
                if (!ValidateNoChildren(declaration, flowPath, errors))
                {
                    break;
                }
                if (!ValidateKnownKeys(
                        values,
                        location,
                        errors,
                        "id", "scene", "label", "line", "speaker", "expect", "value") ||
                    !NormalizeAliases(
                        values,
                        location,
                        errors,
                        "label", "afterLabel",
                        "line", "dialogueOrdinal",
                        "speaker", "expectedSpeaker",
                        "expect", "expectedText",
                        "value", "text"))
                {
                    break;
                }
                ParseDialogueDirective(
                    packageRoot, modId, flowPath, location, "text", values, dialogueIds, content, errors);
                break;
            case "voice":
                if (!ValidateNoChildren(declaration, flowPath, errors))
                {
                    break;
                }
                if (!ValidateKnownKeys(
                        values,
                        location,
                        errors,
                        "id", "scene", "label", "line", "speaker", "expect", "text", "source", "volume") ||
                    !NormalizeAliases(
                        values,
                        location,
                        errors,
                        "label", "afterLabel",
                        "line", "dialogueOrdinal",
                        "speaker", "expectedSpeaker",
                        "expect", "expectedText",
                        "text", "expectedText",
                        "source", "voice"))
                {
                    break;
                }
                ParseDialogueDirective(
                    packageRoot, modId, flowPath, location, "voice", values, dialogueIds, content, errors);
                break;
            case "voices":
                if (!NormalizeAliases(values, location, errors, "label", "afterLabel"))
                {
                    break;
                }
                ParseVoicesDirective(
                    packageRoot,
                    modId,
                    flowPath,
                    declaration,
                    dialogueIds,
                    content,
                    errors);
                break;
            case "replace":
                if (!ValidateNoChildren(declaration, flowPath, errors))
                {
                    break;
                }
                ParseOverlayDirective(packageRoot, modId, flowPath, location, values, overlayIds, content, errors);
                break;
            case "gallery":
                if (!ValidateNoChildren(declaration, flowPath, errors))
                {
                    break;
                }
                ParseGalleryDirective(packageRoot, modId, flowPath, location, values, galleryIds, content, errors);
                break;
            case "overlay":
                if (!ValidateNoChildren(declaration, flowPath, errors))
                {
                    break;
                }
                ParseOverlayDirective(packageRoot, modId, flowPath, location, values, overlayIds, content, errors);
                break;
            case "sprite":
                ParseSpriteDirective(
                    packageRoot,
                    modId,
                    flowPath,
                    declaration,
                    spriteIds,
                    content,
                    errors);
                break;
            case "spine":
                ParseSpineDirective(
                    packageRoot,
                    modId,
                    flowPath,
                    declaration,
                    spriteIds,
                    content,
                    errors);
                break;
            default:
                AddError(errors, location + ": unknown flow declaration '@" + declaration.Kind + "'.");
                break;
        }
    }

    private static bool NormalizeDialogueAliases(
        Dictionary<string, string> values,
        string location,
        List<string> errors)
    {
        return NormalizeAliases(
            values,
            location,
            errors,
            "label", "afterLabel",
            "line", "dialogueOrdinal",
            "speaker", "expectedSpeaker",
            "expect", "expectedText",
            "value", "text",
            "source", "voice");
    }

    private static bool NormalizeAliases(
        Dictionary<string, string> values,
        string location,
        List<string> errors,
        params string[] aliases)
    {
        bool valid = true;
        for (int index = 0; index + 1 < aliases.Length; index += 2)
        {
            string alias = aliases[index];
            string canonical = aliases[index + 1];
            if (!values.TryGetValue(alias, out string value))
            {
                continue;
            }

            if (values.ContainsKey(canonical))
            {
                AddError(errors, location + ": use either '" + alias + "' or '" + canonical + "', not both.");
                valid = false;
                continue;
            }

            values.Remove(alias);
            values[canonical] = value;
        }

        return valid;
    }

    private static void ParseBranchDirective(
        string packageRoot,
        string modId,
        string flowPath,
        FlowDeclarationNode declaration,
        HashSet<string> settingIds,
        HashSet<string> ids,
        FlowPackageContent content,
        List<string> errors)
    {
        string location = flowPath + ":" + declaration.LineNumber;
        Dictionary<string, string> values = declaration.Values;
        if (!ValidateKnownKeys(
                values,
                location,
                errors,
                "id", "scene", "afterLabel", "dialogueOrdinal", "expectedSpeaker", "expectedText"))
        {
            return;
        }

        string sceneReference = Get(values, "scene");
        string scene;
        if (string.IsNullOrWhiteSpace(sceneReference))
        {
            scene = LoaderUtil.ToModUri(modId, flowPath);
        }
        else if (IsModReference(sceneReference))
        {
            if (!TryResolveAndValidateReference(
                    packageRoot,
                    modId,
                    flowPath,
                    sceneReference,
                    AssetKind.Text,
                    out scene,
                    out string sceneError))
            {
                AddError(errors, location + ": branch scene resource " + sceneError);
                return;
            }
        }
        else
        {
            scene = sceneReference;
        }

        BranchAnchorDefinition anchor = null;
        string branchAfterLabel = null;
        string afterLabel = Get(values, "afterLabel");
        if (values.TryGetValue("dialogueOrdinal", out string ordinalText))
        {
            if (string.IsNullOrWhiteSpace(afterLabel) ||
                !int.TryParse(ordinalText, NumberStyles.None, CultureInfo.InvariantCulture, out int ordinal) || ordinal < 0)
            {
                AddError(errors, location + ": branch dialogueOrdinal requires afterLabel and a non-negative integer.");
                return;
            }

            anchor = new BranchAnchorDefinition
            {
                afterLabel = afterLabel,
                dialogueOrdinal = ordinal,
                expectedSpeaker = Get(values, "expectedSpeaker"),
                expectedText = Get(values, "expectedText")
            };
        }
        else if (string.IsNullOrWhiteSpace(afterLabel))
        {
            AddError(errors, location + ": branch requires afterLabel.");
            return;
        }
        else
        {
            branchAfterLabel = afterLabel;
        }

        string anchorLabel = anchor?.afterLabel ?? branchAfterLabel;
        string anchorOrdinal = anchor == null
            ? "label"
            : anchor.dialogueOrdinal.ToString(CultureInfo.InvariantCulture);
        if (!TryGetOrCreateId(
                values,
                "branch",
                ids,
                location,
                errors,
                out string id,
                scene,
                anchorLabel,
                anchorOrdinal))
        {
            return;
        }

        if (declaration.Children.Count == 0)
        {
            AddError(errors, location + ": branch requires one or more option { ... } blocks.");
            return;
        }

        if (declaration.Children.Count > MaxBranchOptions)
        {
            AddError(errors, location + ": branch contains more than " + MaxBranchOptions + " option blocks.");
            return;
        }

        HashSet<string> optionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FlowDeclarationNode option in declaration.Children)
        {
            string optionLocation = flowPath + ":" + option.LineNumber;
            if (!string.Equals(option.Kind, "option", StringComparison.OrdinalIgnoreCase))
            {
                AddError(errors, optionLocation + ": branch only accepts option { ... } child blocks.");
                continue;
            }

            if (option.Children.Count != 0)
            {
                AddError(errors, optionLocation + ": option does not accept nested child blocks.");
                continue;
            }

            Dictionary<string, string> optionValues = option.Values;
            if (!NormalizeAliases(
                    optionValues,
                    optionLocation,
                    errors,
                    "enter", "entryLabel",
                    "condition", "setting",
                    "invert", "invertSetting",
                    "repeat", "repeatable") ||
                !ValidateKnownKeys(
                    optionValues,
                    optionLocation,
                    errors,
                    "id", "text", "story", "entryLabel", "setting", "invertSetting", "repeatable", "continue"))
            {
                continue;
            }

            if (!TryGetRequired(optionValues, "text", optionLocation, errors, out string optionText))
            {
                continue;
            }

            if (!TryGetBoolean(optionValues, "continue", false, optionLocation, errors, out bool continueCurrent) ||
                !TryGetBoolean(optionValues, "invertSetting", false, optionLocation, errors, out bool invertSetting) ||
                !TryGetBoolean(optionValues, "repeatable", false, optionLocation, errors, out bool repeatable))
            {
                continue;
            }

            string story = null;
            string entryLabel = Get(optionValues, "entryLabel");
            if (continueCurrent)
            {
                if (optionValues.ContainsKey("story") || !string.IsNullOrWhiteSpace(entryLabel))
                {
                    AddError(errors, optionLocation + ": a continue=true option cannot declare story or entryLabel.");
                    continue;
                }
            }
            else
            {
                string storyReference = Get(optionValues, "story");
                if (string.IsNullOrWhiteSpace(storyReference))
                {
                    story = flowPath;
                }
                else if (!TryResolveAndValidateReference(
                             packageRoot,
                             modId,
                             flowPath,
                             storyReference,
                             AssetKind.Text,
                             out string storyUri,
                             out string storyError))
                {
                    AddError(errors, optionLocation + ": option story resource " + storyError);
                    continue;
                }
                else
                {
                    LoaderUtil.TryParseModUri(storyUri, out _, out story);
                }
            }

            string setting = Get(optionValues, "setting");
            if (!string.IsNullOrWhiteSpace(setting) && !settingIds.Contains(setting))
            {
                AddError(errors, optionLocation + ": option references unknown setting '" + setting + "'.");
                continue;
            }

            if (!TryGetOrCreateId(
                    optionValues,
                    "option",
                    optionIds,
                    optionLocation,
                    errors,
                    out string optionId,
                    continueCurrent ? "continue" : story,
                    entryLabel,
                    setting,
                    invertSetting ? "inverted" : "normal"))
            {
                continue;
            }

            content.Branches.Add(new BranchOptionDefinition
            {
                groupId = id,
                id = id + "." + optionId,
                scene = scene,
                afterLabel = branchAfterLabel,
                anchor = anchor,
                optionText = optionText,
                story = story,
                entryLabel = entryLabel,
                setting = setting,
                invertSetting = invertSetting,
                repeatable = repeatable,
                continueCurrent = continueCurrent
            });
        }
    }

    private static bool ValidateNoChildren(
        FlowDeclarationNode declaration,
        string flowPath,
        List<string> errors)
    {
        if (declaration.Children.Count == 0)
        {
            return true;
        }

        FlowDeclarationNode child = declaration.Children[0];
        AddError(
            errors,
            flowPath + ":" + child.LineNumber + ": @" + declaration.Kind + " does not accept child blocks.");
        return false;
    }

    private static void ValidateFlowTargets(
        string modId,
        FlowPackageContent content,
        List<string> errors)
    {
        Dictionary<string, FlowDefinition> flows = content.Flows.ToDictionary(
            flow => flow.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> reportedAnchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> reportedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (BranchOptionDefinition branch in content.Branches)
        {
            if (LoaderUtil.TryParseModUri(branch.scene, out string sceneModId, out string scenePath) &&
                string.Equals(sceneModId, modId, StringComparison.OrdinalIgnoreCase))
            {
                string anchorLabel = branch.anchor?.afterLabel ?? branch.afterLabel;
                string anchorKey = branch.groupId + "|" + scenePath + "|" + anchorLabel;
                if (!flows.TryGetValue(scenePath, out FlowDefinition anchorFlow))
                {
                    if (reportedAnchors.Add(anchorKey))
                    {
                        AddError(errors, "branch '" + branch.groupId + "' scene flow was not found: " + scenePath);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(anchorLabel) && !anchorFlow.Labels.Contains(anchorLabel) &&
                         reportedAnchors.Add(anchorKey))
                {
                    AddError(
                        errors,
                        "branch '" + branch.groupId + "' anchor label was not found in " + scenePath + ": " + anchorLabel);
                }
            }

            if (branch.continueCurrent)
            {
                continue;
            }

            string targetKey = branch.id + "|" + branch.story + "|" + branch.entryLabel;
            if (!flows.TryGetValue(branch.story, out FlowDefinition targetFlow))
            {
                if (reportedTargets.Add(targetKey))
                {
                    AddError(errors, "branch option '" + branch.id + "' story flow was not found: " + branch.story);
                }
            }
            else if (!string.IsNullOrWhiteSpace(branch.entryLabel) && !targetFlow.Labels.Contains(branch.entryLabel) &&
                     reportedTargets.Add(targetKey))
            {
                AddError(
                    errors,
                    "branch option '" + branch.id + "' entryLabel was not found in " +
                    branch.story + ": " + branch.entryLabel);
            }
        }

        foreach (FlowDefinition sourceFlow in content.Flows)
        {
            foreach (FlowCallReference call in sourceFlow.Calls)
            {
                if (!LoaderUtil.TryParseModUri(call.target, out string targetModId, out string targetPath) ||
                    !string.Equals(targetModId, modId, StringComparison.OrdinalIgnoreCase) ||
                    !flows.TryGetValue(targetPath, out FlowDefinition targetFlow))
                {
                    AddError(errors, call.location + ": call target flow was not found: " + call.target);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(call.entryLabel) && !targetFlow.Labels.Contains(call.entryLabel))
                {
                    AddError(
                        errors,
                        call.location + ": call entry label was not found in " + targetPath + ": " + call.entryLabel);
                }
            }
        }

        HashSet<string> externalCharacterIds = new HashSet<string>(
            content.Sprites.Select(sprite => sprite.id)
                .Concat(content.Spines.Select(spine => spine.id)),
            StringComparer.OrdinalIgnoreCase);
        foreach (FlowDefinition flow in content.Flows)
        {
            foreach (string spriteId in flow.SpriteReferences)
            {
                if (!externalCharacterIds.Contains(spriteId))
                {
                    AddError(
                        errors,
                        flow.RelativePath + ": references undeclared external character '$" + spriteId + "'.");
                }
            }
        }
    }

    private static void ParseDialogueDirective(
        string packageRoot,
        string modId,
        string flowPath,
        string location,
        string automaticIdKind,
        Dictionary<string, string> values,
        HashSet<string> ids,
        FlowPackageContent content,
        List<string> errors)
    {
        if (!ValidateKnownKeys(
                values,
                location,
                errors,
                "id", "scene", "afterLabel", "dialogueOrdinal", "expectedSpeaker", "expectedText",
                "text", "voice", "volume"))
        {
            return;
        }

        if (!TryGetRequired(values, "scene", location, errors, out string scene) ||
            !TryGetRequired(values, "afterLabel", location, errors, out string afterLabel))
        {
            return;
        }

        int ordinal = -1;
        if (values.TryGetValue("dialogueOrdinal", out string ordinalText) &&
            (!int.TryParse(ordinalText, NumberStyles.None, CultureInfo.InvariantCulture, out ordinal) || ordinal < 0))
        {
            AddError(errors, location + ": dialogueOrdinal must be a non-negative integer.");
            return;
        }

        string expectedText = Get(values, "expectedText");
        if (ordinal < 0 && string.IsNullOrWhiteSpace(expectedText))
        {
            AddError(errors, location + ": dialogue without dialogueOrdinal requires expectedText for unique matching.");
            return;
        }

        string selectorKind = ordinal >= 0 ? "ordinal" : "text";
        string selectorValue = ordinal >= 0
            ? ordinal.ToString(CultureInfo.InvariantCulture)
            : expectedText;
        string selectorSpeaker = ordinal >= 0 ? null : Get(values, "expectedSpeaker");
        if (!TryGetOrCreateId(
                values,
                automaticIdKind,
                ids,
                location,
                errors,
                out string id,
                scene,
                afterLabel,
                selectorKind,
                selectorValue,
                selectorSpeaker))
        {
            return;
        }

        DialogueSetDefinition set = new DialogueSetDefinition { text = Get(values, "text") };
        if (values.TryGetValue("voice", out string voiceReference))
        {
            if (!TryResolveAndValidateReference(
                    packageRoot,
                    modId,
                    flowPath,
                    voiceReference,
                    AssetKind.Audio,
                    out set.voice,
                    out string referenceError))
            {
                AddError(errors, location + ": dialogue voice resource " + referenceError);
                return;
            }

            LoaderUtil.TryParseModUri(set.voice, out _, out string voicePath);
            set.voice = voicePath;
        }

        if (set.text == null && string.IsNullOrWhiteSpace(set.voice))
        {
            AddError(errors, location + ": dialogue requires text, voice, or both.");
            return;
        }

        if (!TryGetVolume(values, "volume", 1f, location, errors, out set.volume))
        {
            return;
        }

        content.DialoguePatches.Add(new DialoguePatchDefinition
        {
            id = id,
            anchor = new DialogueAnchorDefinition
            {
                scene = scene,
                afterLabel = afterLabel,
                dialogueOrdinal = ordinal,
                expectedSpeaker = Get(values, "expectedSpeaker"),
                expectedText = expectedText
            },
            set = set
        });
    }

    private static void ParseVoicesDirective(
        string packageRoot,
        string modId,
        string flowPath,
        FlowDeclarationNode declaration,
        HashSet<string> ids,
        FlowPackageContent content,
        List<string> errors)
    {
        string location = flowPath + ":" + declaration.LineNumber;
        Dictionary<string, string> values = declaration.Values;
        if (!ValidateKnownKeys(values, location, errors, "id", "scene", "afterLabel", "directory", "volume") ||
            !TryGetRequired(values, "afterLabel", location, errors, out string afterLabel) ||
            !TryGetVolume(values, "volume", 1f, location, errors, out float defaultVolume))
        {
            return;
        }

        string sceneReference = Get(values, "scene");
        string scene;
        if (string.IsNullOrWhiteSpace(sceneReference))
        {
            scene = LoaderUtil.ToModUri(modId, flowPath);
        }
        else if (IsModReference(sceneReference))
        {
            if (!TryResolveAndValidateReference(
                    packageRoot,
                    modId,
                    flowPath,
                    sceneReference,
                    AssetKind.Text,
                    out scene,
                    out string sceneError))
            {
                AddError(errors, location + ": voices scene resource " + sceneError);
                return;
            }
        }
        else
        {
            scene = sceneReference;
        }

        string directory = Get(values, "directory");
        if (!TryGetOrCreateId(
                values,
                "voices",
                ids,
                location,
                errors,
                out string groupId,
                scene,
                afterLabel,
                directory))
        {
            return;
        }

        if (declaration.Children.Count == 0)
        {
            AddError(errors, location + ": voices requires one or more line { ... } blocks.");
            return;
        }

        if (declaration.Children.Count > MaxVoiceEntries)
        {
            AddError(errors, location + ": voices contains more than " + MaxVoiceEntries + " line blocks.");
            return;
        }

        HashSet<string> childIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FlowDeclarationNode child in declaration.Children)
        {
            string childLocation = flowPath + ":" + child.LineNumber;
            if (!string.Equals(child.Kind, "line", StringComparison.OrdinalIgnoreCase))
            {
                AddError(errors, childLocation + ": voices only accepts line { ... } child blocks.");
                continue;
            }

            if (child.Children.Count != 0)
            {
                AddError(errors, childLocation + ": voices line does not accept nested child blocks.");
                continue;
            }

            Dictionary<string, string> childValues = child.Values;
            if (!NormalizeAliases(
                    childValues,
                    childLocation,
                    errors,
                    "line", "dialogueOrdinal",
                    "speaker", "expectedSpeaker",
                    "expect", "expectedText",
                    "text", "expectedText",
                    "source", "voice") ||
                !ValidateKnownKeys(
                    childValues,
                    childLocation,
                    errors,
                    "id", "dialogueOrdinal", "expectedSpeaker", "expectedText", "voice", "file", "volume"))
            {
                continue;
            }

            if (!TryGetVolume(childValues, "volume", defaultVolume, childLocation, errors, out float volume))
            {
                continue;
            }

            string childSelectorKind = childValues.TryGetValue("dialogueOrdinal", out string childOrdinal)
                ? "ordinal"
                : "text";
            string childSelectorValue = childSelectorKind == "ordinal"
                ? childOrdinal
                : Get(childValues, "expectedText");
            string childSelectorSpeaker = childSelectorKind == "ordinal"
                ? null
                : Get(childValues, "expectedSpeaker");
            if (!TryGetOrCreateId(
                    childValues,
                    "line",
                    childIds,
                    childLocation,
                    errors,
                    out string childId,
                    scene,
                    afterLabel,
                    childSelectorKind,
                    childSelectorValue,
                    childSelectorSpeaker))
            {
                continue;
            }

            bool hasSource = childValues.TryGetValue("voice", out string voiceReference);
            bool hasFile = childValues.TryGetValue("file", out string file);
            if (hasSource == hasFile)
            {
                AddError(errors, childLocation + ": voices line requires exactly one of source or file.");
                continue;
            }

            if (hasFile && !TryCombineVoiceReference(directory, file, out voiceReference, out string fileError))
            {
                AddError(errors, childLocation + ": voices line file " + fileError);
                continue;
            }

            Dictionary<string, string> patchValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = groupId + "." + childId,
                ["scene"] = scene,
                ["afterLabel"] = afterLabel,
                ["voice"] = voiceReference,
                ["volume"] = volume.ToString(CultureInfo.InvariantCulture)
            };
            CopyIfPresent(childValues, patchValues, "dialogueOrdinal");
            CopyIfPresent(childValues, patchValues, "expectedSpeaker");
            CopyIfPresent(childValues, patchValues, "expectedText");
            ParseDialogueDirective(
                packageRoot, modId, flowPath, childLocation, "voice", patchValues, ids, content, errors);
        }
    }

    private static bool TryCombineVoiceReference(
        string directory,
        string file,
        out string reference,
        out string error)
    {
        reference = null;
        error = null;
        if (string.IsNullOrWhiteSpace(directory) || !IsModReference(directory))
        {
            error = "requires a voices directory using @/, ./, or ../.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(file) || !string.Equals(file, file.Trim(), StringComparison.Ordinal))
        {
            error = "must not be empty or contain leading/trailing whitespace.";
            return false;
        }

        string normalizedFile = file.Replace('\\', '/');
        if (normalizedFile.StartsWith("/", StringComparison.Ordinal) ||
            IsModReference(normalizedFile) || normalizedFile.IndexOf(':') >= 0)
        {
            error = "must be a path relative to the voices directory; use source for a full Mod reference.";
            return false;
        }

        reference = directory.Replace('\\', '/').TrimEnd('/') + "/" + normalizedFile;
        return true;
    }

    private static void CopyIfPresent(
        Dictionary<string, string> source,
        Dictionary<string, string> target,
        string key)
    {
        if (source.TryGetValue(key, out string value))
        {
            target[key] = value;
        }
    }

    private static void ParseSpriteDirective(
        string packageRoot,
        string modId,
        string flowPath,
        FlowDeclarationNode declaration,
        HashSet<string> ids,
        FlowPackageContent content,
        List<string> errors)
    {
        string location = flowPath + ":" + declaration.LineNumber;
        Dictionary<string, string> values = declaration.Values;
        if (!ValidateKnownKeys(
                values,
                location,
                errors,
                "id", "name", "source", "defaultBase", "defaultEmotion", "width", "height",
                "pivotX", "pivotY", "portraitSize", "portraitOffsetX", "portraitOffsetY", "layer", "order") ||
            !TryGetRequired(values, "id", location, errors, out string id) ||
            !RegisterId(id, ids, location, errors))
        {
            return;
        }

        SpriteDefinition definition = new SpriteDefinition
        {
            id = id,
            internalName = GetSpriteInternalName(modId, id),
            displayName = string.IsNullOrWhiteSpace(Get(values, "name")) ? id : Get(values, "name"),
            defaultBase = Get(values, "defaultBase"),
            defaultEmotion = Get(values, "defaultEmotion"),
            layer = string.IsNullOrWhiteSpace(Get(values, "layer")) ? "front" : Get(values, "layer").ToLowerInvariant()
        };

        if (!TryGetOptionalFloat(values, "width", 0f, 0f, float.MaxValue, location, errors, out definition.width) ||
            !TryGetOptionalFloat(values, "height", 0f, 0f, float.MaxValue, location, errors, out definition.height) ||
            !TryGetOptionalFloat(values, "pivotX", 0.5f, 0f, 1f, location, errors, out definition.pivotX) ||
            !TryGetOptionalFloat(values, "pivotY", 0f, 0f, 1f, location, errors, out definition.pivotY) ||
            !TryGetOptionalFloat(values, "portraitSize", 0f, 0f, float.MaxValue, location, errors, out definition.portraitSize) ||
            !TryGetOptionalFloat(values, "portraitOffsetX", 0f, -100000f, 100000f, location, errors, out definition.portraitOffsetX) ||
            !TryGetOptionalFloat(values, "portraitOffsetY", 0f, -100000f, 100000f, location, errors, out definition.portraitOffsetY))
        {
            return;
        }

        if ((definition.width <= 0f) != (definition.height <= 0f))
        {
            AddError(errors, location + ": sprite width and height must either both be omitted or both be greater than zero.");
            return;
        }

        if (!string.Equals(definition.layer, "front", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(definition.layer, "back", StringComparison.OrdinalIgnoreCase))
        {
            AddError(errors, location + ": sprite layer must be front or back.");
            return;
        }

        if (values.TryGetValue("order", out string orderText) &&
            !int.TryParse(orderText, NumberStyles.Integer, CultureInfo.InvariantCulture, out definition.order))
        {
            AddError(errors, location + ": sprite order must be an integer.");
            return;
        }

        HashSet<string> baseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> emotionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (values.TryGetValue("source", out string defaultSource))
        {
            if (!TryResolveSpriteImage(
                    packageRoot,
                    modId,
                    flowPath,
                    location,
                    "default",
                    defaultSource,
                    definition.bases,
                    baseIds,
                    errors))
            {
                return;
            }
        }

        foreach (FlowDeclarationNode child in declaration.Children)
        {
            string childLocation = flowPath + ":" + child.LineNumber;
            bool isBase = string.Equals(child.Kind, "base", StringComparison.OrdinalIgnoreCase);
            bool isEmotion = string.Equals(child.Kind, "emotion", StringComparison.OrdinalIgnoreCase);
            if (!isBase && !isEmotion)
            {
                AddError(errors, childLocation + ": sprite only accepts base { ... } or emotion { ... } child blocks.");
                continue;
            }

            if (child.Children.Count != 0 ||
                !ValidateKnownKeys(child.Values, childLocation, errors, "id", "source") ||
                !TryGetRequired(child.Values, "id", childLocation, errors, out string childId) ||
                !TryGetRequired(child.Values, "source", childLocation, errors, out string childSource))
            {
                if (child.Children.Count != 0)
                {
                    AddError(errors, childLocation + ": sprite image blocks do not accept nested blocks.");
                }
                continue;
            }

            TryResolveSpriteImage(
                packageRoot,
                modId,
                flowPath,
                childLocation,
                childId,
                childSource,
                isBase ? definition.bases : definition.emotions,
                isBase ? baseIds : emotionIds,
                errors);
        }

        if (definition.bases.Count == 0)
        {
            AddError(errors, location + ": sprite requires source or at least one base { ... } block.");
            return;
        }

        if (string.IsNullOrWhiteSpace(definition.defaultBase))
        {
            definition.defaultBase = definition.bases[0].id;
        }
        else if (!baseIds.Contains(definition.defaultBase))
        {
            AddError(errors, location + ": sprite defaultBase does not match a declared base: " + definition.defaultBase);
            return;
        }

        if (string.IsNullOrWhiteSpace(definition.defaultEmotion))
        {
            definition.defaultEmotion = definition.emotions.FirstOrDefault()?.id ?? string.Empty;
        }
        else if (!emotionIds.Contains(definition.defaultEmotion))
        {
            AddError(errors, location + ": sprite defaultEmotion does not match a declared emotion: " + definition.defaultEmotion);
            return;
        }

        content.Sprites.Add(definition);
    }

    private static void ParseSpineDirective(
        string packageRoot,
        string modId,
        string flowPath,
        FlowDeclarationNode declaration,
        HashSet<string> ids,
        FlowPackageContent content,
        List<string> errors)
    {
        string location = flowPath + ":" + declaration.LineNumber;
        Dictionary<string, string> values = declaration.Values;
        if (!ValidateKnownKeys(
                values,
                location,
                errors,
                "id", "name", "bundle", "windowsBundle", "macosBundle", "androidBundle",
                "prefab", "defaultEmotionAnimation") ||
            !TryGetRequired(values, "id", location, errors, out string id) ||
            !TryGetRequired(values, "prefab", location, errors, out string prefab) ||
            !RegisterId(id, ids, location, errors))
        {
            return;
        }

        if (prefab.Length > 512 || prefab.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
        {
            AddError(errors, location + ": spine prefab asset name is invalid or exceeds 512 characters.");
            return;
        }

        SpineDefinition definition = new SpineDefinition
        {
            id = id,
            internalName = GetSpriteInternalName(modId, id),
            displayName = string.IsNullOrWhiteSpace(Get(values, "name")) ? id : Get(values, "name"),
            prefab = prefab,
            defaultEmotionAnimation = Get(values, "defaultEmotionAnimation")
        };

        bool bundleValid =
            TryResolveOptionalSpineBundle(
                packageRoot, modId, flowPath, location, values, "bundle", out definition.bundle, errors) &
            TryResolveOptionalSpineBundle(
                packageRoot, modId, flowPath, location, values, "windowsBundle", out definition.windowsBundle, errors) &
            TryResolveOptionalSpineBundle(
                packageRoot, modId, flowPath, location, values, "macosBundle", out definition.macosBundle, errors) &
            TryResolveOptionalSpineBundle(
                packageRoot, modId, flowPath, location, values, "androidBundle", out definition.androidBundle, errors);
        if (!bundleValid)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(definition.bundle) &&
            string.IsNullOrWhiteSpace(definition.windowsBundle) &&
            string.IsNullOrWhiteSpace(definition.macosBundle) &&
            string.IsNullOrWhiteSpace(definition.androidBundle))
        {
            AddError(
                errors,
                location + ": spine requires bundle or at least one platform bundle.");
            return;
        }

        HashSet<string> emotionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FlowDeclarationNode child in declaration.Children)
        {
            string childLocation = flowPath + ":" + child.LineNumber;
            if (!string.Equals(child.Kind, "emotion", StringComparison.OrdinalIgnoreCase))
            {
                AddError(errors, childLocation + ": spine only accepts emotion { ... } child blocks.");
                continue;
            }
            if (child.Children.Count != 0 ||
                !ValidateKnownKeys(child.Values, childLocation, errors, "id", "animation") ||
                !TryGetRequired(child.Values, "id", childLocation, errors, out string emotionId) ||
                !TryGetRequired(child.Values, "animation", childLocation, errors, out string animation))
            {
                if (child.Children.Count != 0)
                {
                    AddError(errors, childLocation + ": spine emotion blocks do not accept nested blocks.");
                }
                continue;
            }
            if (animation.Length > 256 || animation.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            {
                AddError(errors, childLocation + ": spine emotion animation is invalid or exceeds 256 characters.");
                continue;
            }
            if (RegisterId(emotionId, emotionIds, childLocation, errors))
            {
                definition.emotions[emotionId] = animation;
            }
        }

        content.Spines.Add(definition);
    }

    private static bool TryResolveOptionalSpineBundle(
        string packageRoot,
        string modId,
        string flowPath,
        string location,
        Dictionary<string, string> values,
        string key,
        out string uri,
        List<string> errors)
    {
        uri = null;
        if (!values.TryGetValue(key, out string reference))
        {
            return true;
        }
        if (!TryResolveAndValidateReference(
                packageRoot,
                modId,
                flowPath,
                reference,
                AssetKind.AssetBundle,
                out uri,
                out string sourceError))
        {
            AddError(errors, location + ": spine " + key + " resource " + sourceError);
            return false;
        }
        return true;
    }

    private static bool TryResolveSpriteImage(
        string packageRoot,
        string modId,
        string flowPath,
        string location,
        string id,
        string source,
        List<SpriteImageDefinition> images,
        HashSet<string> ids,
        List<string> errors)
    {
        if (!RegisterId(id, ids, location, errors))
        {
            return false;
        }

        if (!TryResolveAndValidateReference(
                packageRoot,
                modId,
                flowPath,
                source,
                AssetKind.Texture,
                out string uri,
                out string sourceError))
        {
            AddError(errors, location + ": sprite image resource " + sourceError);
            return false;
        }

        images.Add(new SpriteImageDefinition { id = id, source = uri });
        return true;
    }

    internal static string GetSpriteInternalName(string modId, string id)
    {
        return "__sunny_sprite_" + LoaderUtil.SafeId(modId) + "_" + LoaderUtil.SafeId(id);
    }

    private static void ParseGalleryDirective(
        string packageRoot,
        string modId,
        string flowPath,
        string location,
        Dictionary<string, string> values,
        HashSet<string> ids,
        FlowPackageContent content,
        List<string> errors)
    {
        if (!ValidateKnownKeys(
                values,
                location,
                errors,
                "id", "title", "image", "thumbnail", "unlockedByDefault"))
        {
            return;
        }

        if (!TryGetRequired(values, "image", location, errors, out string imageReference))
        {
            return;
        }

        if (!TryResolveAndValidateReference(
                packageRoot,
                modId,
                flowPath,
                imageReference,
                AssetKind.Texture,
                out string imageUri,
                out string imageError))
        {
            AddError(errors, location + ": gallery image resource " + imageError);
            return;
        }
        LoaderUtil.TryParseModUri(imageUri, out _, out string imagePath);

        if (!TryGetOrCreateId(
                values,
                "gallery",
                ids,
                location,
                errors,
                out string id,
                imageUri))
        {
            return;
        }

        string thumbnailUri = null;
        if (values.TryGetValue("thumbnail", out string thumbnailReference) &&
            !TryResolveAndValidateReference(
                packageRoot,
                modId,
                flowPath,
                thumbnailReference,
                AssetKind.Texture,
                out thumbnailUri,
                out string thumbnailError))
        {
            AddError(errors, location + ": gallery thumbnail resource " + thumbnailError);
            return;
        }
        string thumbnailPath = null;
        if (!string.IsNullOrWhiteSpace(thumbnailUri))
        {
            LoaderUtil.TryParseModUri(thumbnailUri, out _, out thumbnailPath);
        }

        if (!TryGetBoolean(values, "unlockedByDefault", false, location, errors, out bool unlocked))
        {
            return;
        }

        content.Gallery.Add(new GalleryDefinition
        {
            id = id,
            title = string.IsNullOrWhiteSpace(Get(values, "title"))
                ? Path.GetFileNameWithoutExtension(imagePath)
                : Get(values, "title"),
            image = imagePath,
            thumbnail = thumbnailPath,
            unlockedByDefault = unlocked
        });
    }

    private static void ParseOverlayDirective(
        string packageRoot,
        string modId,
        string flowPath,
        string location,
        Dictionary<string, string> values,
        HashSet<string> ids,
        FlowPackageContent content,
        List<string> errors)
    {
        if (!ValidateKnownKeys(values, location, errors, "id", "kind", "target", "source"))
        {
            return;
        }

        if (!TryGetRequired(values, "kind", location, errors, out string kindText) ||
            !TryGetRequired(values, "target", location, errors, out string target) ||
            !TryGetRequired(values, "source", location, errors, out string sourceReference))
        {
            return;
        }

        if (!Enum.TryParse(kindText, true, out AssetKind kind) ||
            kind == AssetKind.Any ||
            kind == AssetKind.AssetBundle)
        {
            AddError(errors, location + ": overlay kind must be Text, Texture, Audio, or Video.");
            return;
        }

        if (!TryResolveAndValidateReference(
                packageRoot,
                modId,
                flowPath,
                sourceReference,
                kind,
                out string sourceUri,
                out string sourceError))
        {
            AddError(errors, location + ": overlay source resource " + sourceError);
            return;
        }
        LoaderUtil.TryParseModUri(sourceUri, out _, out string sourcePath);

        if (!TryGetOrCreateId(
                values,
                "replace",
                ids,
                location,
                errors,
                out string id,
                kind.ToString(),
                LoaderUtil.NormalizeResourceKey(target)))
        {
            return;
        }

        content.Overlays.Add(new OverlayDefinition
        {
            id = id,
            kind = kind.ToString(),
            target = target,
            source = sourcePath
        });
    }
}
