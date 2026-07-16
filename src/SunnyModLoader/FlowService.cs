using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SunnyModLoader;

internal sealed class FlowVoiceBinding
{
    internal int DialogueOrdinal;
    internal string Speaker;
    internal string Text;
    internal string Uri;
    internal float Volume = 1f;
}

internal sealed class OverlayDefinition
{
    internal string id;
    internal string kind;
    internal string target;
    internal string source;
}

internal sealed class DialoguePatchDefinition
{
    internal string id;
    internal DialogueAnchorDefinition anchor;
    internal DialogueSetDefinition set;
}

internal sealed class DialogueAnchorDefinition
{
    internal string scene;
    internal string afterLabel;
    internal int dialogueOrdinal = -1;
    internal string expectedSpeaker;
    internal string expectedText;
}

internal sealed class DialogueSetDefinition
{
    internal string text;
    internal string voice;
    internal float volume = 1f;
}

internal sealed class BranchOptionDefinition
{
    internal string groupId;
    internal string id;
    internal string scene;
    internal string afterLabel;
    internal BranchAnchorDefinition anchor;
    internal string optionText;
    internal string story;
    internal string entryLabel;
    internal string setting;
    internal bool invertSetting;
    internal bool repeatable;
    internal bool continueCurrent;
}

internal sealed class BranchAnchorDefinition
{
    internal string afterLabel;
    internal int dialogueOrdinal;
    internal string expectedSpeaker;
    internal string expectedText;
}

internal sealed class GalleryDefinition
{
    internal string id;
    internal string title;
    internal string image;
    internal string thumbnail;
    internal bool unlockedByDefault;
}

internal sealed class SpriteImageDefinition
{
    internal string id;
    internal string source;
}

internal sealed class SpriteDefinition
{
    internal string id;
    internal string internalName;
    internal string displayName;
    internal string defaultBase;
    internal string defaultEmotion;
    internal float width;
    internal float height;
    internal float pivotX = 0.5f;
    internal float pivotY;
    internal float portraitSize;
    internal float portraitOffsetX;
    internal float portraitOffsetY;
    internal string layer = "front";
    internal int order;
    internal readonly List<SpriteImageDefinition> bases = new List<SpriteImageDefinition>();
    internal readonly List<SpriteImageDefinition> emotions = new List<SpriteImageDefinition>();
}

internal sealed class FlowCallReference
{
    internal string target;
    internal string entryLabel;
    internal string location;
}

internal sealed class FlowDefinition
{
    internal string RelativePath;
    internal string SceneUri;
    internal string PreparedText;
    internal readonly List<FlowVoiceBinding> Voices = new List<FlowVoiceBinding>();
    internal readonly HashSet<string> Labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal readonly HashSet<string> SpriteReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal readonly HashSet<string> AudioResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal readonly List<FlowCallReference> Calls = new List<FlowCallReference>();
}

internal sealed class FlowDeclarationNode
{
    internal string Kind;
    internal int LineNumber;
    internal readonly Dictionary<string, string> Values =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    internal readonly List<FlowDeclarationNode> Children = new List<FlowDeclarationNode>();
}

internal sealed class FlowPackageContent
{
    internal readonly List<FlowDefinition> Flows = new List<FlowDefinition>();
    internal readonly List<DialoguePatchDefinition> DialoguePatches = new List<DialoguePatchDefinition>();
    internal readonly List<BranchOptionDefinition> Branches = new List<BranchOptionDefinition>();
    internal readonly List<GalleryDefinition> Gallery = new List<GalleryDefinition>();
    internal readonly List<OverlayDefinition> Overlays = new List<OverlayDefinition>();
    internal readonly List<SpriteDefinition> Sprites = new List<SpriteDefinition>();
}

internal static class FlowService
{
    private const int MaxFlowFiles = 256;
    private const int MaxErrors = 32;
    private const int MaxDirectiveLength = 16 * 1024;
    private const int MaxBranchOptions = 64;
    private const int MaxVoiceEntries = 256;
    private const string DirectivePrefix = "@";
    private const string LegacyDirectivePrefix = "// @sunny";
    private const string CallUriPrefix = "mod://call/";
    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly HashSet<string> NonDialogueCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "animation", "audio", "background", "bgm", "character", "choice", "define", "endfullscreen",
        "effect", "endvideo", "fullscreen", "hide", "jump", "label", "load", "call", "move", "music", "next",
        "return", "rotate", "scale", "sequence", "set", "show", "showui", "closeui", "stopbgm", "stopmusic",
        "unlockcg", "video", "voice", "wait"
    };
    private static readonly HashSet<string> SpriteTargetCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "animation", "character", "hide", "move", "rotate", "scale", "show"
    };

    internal static bool TryLoadPackage(
        string packageRoot,
        ModManifest manifest,
        out FlowPackageContent content,
        out List<string> errors)
    {
        content = new FlowPackageContent();
        errors = new List<string>();
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.id))
        {
            return true;
        }

        HashSet<string> branchIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> dialogueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> galleryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> overlayIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> spriteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> settingIds = SeedIds(manifest.settings?.Select(item => item?.id));

        string storyRoot = Path.Combine(packageRoot, "story");
        if (!Directory.Exists(storyRoot))
        {
            return true;
        }

        try
        {
            List<string> files = EnumerateFlowFiles(storyRoot, errors);
            if (files.Count > MaxFlowFiles)
            {
                AddError(errors, "story: contains more than " + MaxFlowFiles + " flow files.");
                return false;
            }

            foreach (string fullPath in files)
            {
                string relativePath = NormalizeRelativePath(Path.GetRelativePath(packageRoot, fullPath));
                if (!ManifestValidator.TryValidateAssetReference(
                        packageRoot,
                        relativePath,
                        AssetKind.Text,
                        out string assetError))
                {
                    AddError(errors, relativePath + ": " + assetError);
                    continue;
                }

                string source;
                try
                {
                    source = StrictUtf8.GetString(File.ReadAllBytes(fullPath));
                }
                catch (Exception ex)
                {
                    AddError(errors, relativePath + ": is not valid UTF-8 text: " + ex.Message);
                    continue;
                }

                FlowDefinition flow = ParseFlow(
                    packageRoot,
                    manifest.id,
                    relativePath,
                    source,
                    settingIds,
                    branchIds,
                    dialogueIds,
                    galleryIds,
                    overlayIds,
                    spriteIds,
                    content,
                    errors);
                if (flow != null)
                {
                    content.Flows.Add(flow);
                }
            }

            ValidateFlowTargets(manifest.id, content, errors);
        }
        catch (Exception ex)
        {
            AddError(errors, "story: could not be scanned: " + ex.Message);
        }

        return errors.Count == 0;
    }

    private static FlowDefinition ParseFlow(
        string packageRoot,
        string modId,
        string relativePath,
        string source,
        HashSet<string> settingIds,
        HashSet<string> branchIds,
        HashSet<string> dialogueIds,
        HashSet<string> galleryIds,
        HashSet<string> overlayIds,
        HashSet<string> spriteIds,
        FlowPackageContent content,
        List<string> errors)
    {
        FlowDefinition flow = new FlowDefinition
        {
            RelativePath = relativePath,
            SceneUri = LoaderUtil.ToModUri(modId, relativePath)
        };
        string[] lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        StringBuilder prepared = new StringBuilder(source.Length + 128);
        PendingVoice pendingVoice = null;
        int dialogueOrdinal = 0;
        bool bodyStarted = false;

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            string originalLine = lines[lineIndex];
            string trimmed = originalLine.Trim();
            int lineNumber = lineIndex + 1;
            string location = relativePath + ":" + lineNumber;

            if (trimmed.StartsWith(LegacyDirectivePrefix, StringComparison.OrdinalIgnoreCase))
            {
                AddError(
                    errors,
                    location + ": comments cannot declare flow behavior; use @branch, @dialogue, " +
                    "@text, @voice, @voices, @sprite, @gallery, or @replace.");
                prepared.Append(originalLine).Append('\n');
                continue;
            }

            if (trimmed.StartsWith(DirectivePrefix, StringComparison.Ordinal))
            {
                TryCollectDeclaration(
                    lines,
                    lineIndex,
                    out string declarationText,
                    out int declarationEndLine);
                if (bodyStarted)
                {
                    AddError(errors, location + ": flow declarations must appear before the first DSL statement.");
                    for (int skipped = lineIndex; skipped <= declarationEndLine; skipped++)
                    {
                        prepared.Append('\n');
                    }
                    lineIndex = declarationEndLine;
                    continue;
                }

                if (declarationText.Length > MaxDirectiveLength)
                {
                    AddError(errors, location + ": flow declaration exceeds " + MaxDirectiveLength + " characters.");
                    for (int skipped = lineIndex; skipped <= declarationEndLine; skipped++)
                    {
                        prepared.Append('\n');
                    }
                    lineIndex = declarationEndLine;
                    continue;
                }

                if (!TryParseFlowDeclaration(
                        declarationText,
                        lineNumber,
                        out FlowDeclarationNode declaration,
                        out string declarationError,
                        out int declarationErrorLine))
                {
                    AddError(errors, relativePath + ":" + declarationErrorLine + ": " + declarationError);
                }
                else
                {
                    ParseDirective(
                        declaration,
                        packageRoot,
                        modId,
                        relativePath,
                        settingIds,
                        branchIds,
                        dialogueIds,
                        galleryIds,
                        overlayIds,
                        spriteIds,
                        content,
                        errors);
                }

                for (int skipped = lineIndex; skipped <= declarationEndLine; skipped++)
                {
                    prepared.Append('\n');
                }
                lineIndex = declarationEndLine;
                continue;
            }

            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                prepared.Append(originalLine).Append('\n');
                continue;
            }

            bodyStarted = true;
            if (TryParseLabel(trimmed, out string label) && !flow.Labels.Add(label))
            {
                AddError(errors, location + ": duplicate label '" + label + "'.");
            }

            if (string.Equals(trimmed, "return", StringComparison.OrdinalIgnoreCase))
            {
                if (pendingVoice != null)
                {
                    AddError(errors, location + ": voice must be followed by a dialogue line.");
                    pendingVoice = null;
                }

                prepared.Append(GetIndent(originalLine)).Append("load \"mod://return\"").Append('\n');
                continue;
            }

            if (TryParseVoiceCommand(trimmed, out string voiceReference, out float voiceVolume, out string voiceError))
            {
                if (voiceError != null)
                {
                    AddError(errors, location + ": " + voiceError);
                }
                else if (pendingVoice != null)
                {
                    AddError(errors, location + ": consecutive voice commands are not allowed.");
                }
                else if (TryResolveAndValidateReference(
                             packageRoot,
                             modId,
                             relativePath,
                             voiceReference,
                             AssetKind.Audio,
                             out string voiceUri,
                             out string referenceError))
                {
                    pendingVoice = new PendingVoice { Uri = voiceUri, Volume = voiceVolume };
                }
                else
                {
                    AddError(errors, location + ": voice resource " + referenceError);
                }

                prepared.Append('\n');
                continue;
            }

            string bodyLine = RewriteSpriteReference(originalLine, modId, flow);
            string bodyTrimmed = bodyLine.Trim();
            if (TryParseDialogueLine(bodyTrimmed, out string speaker, out string text, out Dictionary<string, string> attributes))
            {
                PendingVoice inlineVoice = null;
                if (attributes.TryGetValue("voice", out string inlineReference))
                {
                    float inlineVolume = 1f;
                    if (attributes.TryGetValue("voiceVolume", out string volumeText) ||
                        attributes.TryGetValue("voicevolume", out volumeText))
                    {
                        if (!TryParseVolume(volumeText, out inlineVolume))
                        {
                            AddError(errors, location + ": voiceVolume must be between 0 and 1.");
                        }
                    }

                    if (TryResolveAndValidateReference(
                            packageRoot,
                            modId,
                            relativePath,
                            inlineReference,
                            AssetKind.Audio,
                            out string inlineUri,
                            out string referenceError))
                    {
                        inlineVoice = new PendingVoice { Uri = inlineUri, Volume = inlineVolume };
                    }
                    else
                    {
                        AddError(errors, location + ": inline voice resource " + referenceError);
                    }
                }

                if (pendingVoice != null && inlineVoice != null)
                {
                    AddError(errors, location + ": use either a voice command or a [voice=...] attribute, not both.");
                }

                PendingVoice selected = inlineVoice ?? pendingVoice;
                if (selected != null)
                {
                    flow.Voices.Add(new FlowVoiceBinding
                    {
                        DialogueOrdinal = dialogueOrdinal,
                        Speaker = speaker,
                        Text = text,
                        Uri = selected.Uri,
                        Volume = selected.Volume
                    });
                }

                pendingVoice = null;
                dialogueOrdinal++;
                prepared.Append(bodyLine).Append('\n');
                continue;
            }

            if (pendingVoice != null)
            {
                AddError(errors, location + ": voice must be immediately followed by a dialogue line.");
                pendingVoice = null;
            }

            if (TryRewriteScreenEffectCommand(bodyLine, location, errors, out string effectCommand))
            {
                prepared.Append(effectCommand).Append('\n');
                continue;
            }

            string rewritten = RewriteCommandResource(
                bodyLine,
                packageRoot,
                modId,
                relativePath,
                location,
                flow,
                errors);
            prepared.Append(rewritten).Append('\n');
        }

        if (pendingVoice != null)
        {
            AddError(errors, relativePath + ": voice command at end of file has no dialogue line.");
        }

        flow.PreparedText = prepared.ToString();
        return flow;
    }

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
                ParseDialogueDirective(packageRoot, modId, flowPath, location, values, dialogueIds, content, errors);
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
                ParseDialogueDirective(packageRoot, modId, flowPath, location, values, dialogueIds, content, errors);
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
                ParseDialogueDirective(packageRoot, modId, flowPath, location, values, dialogueIds, content, errors);
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

        if (!TryGetRequired(values, "id", location, errors, out string id) ||
            !RegisterId(id, ids, location, errors))
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

            if (!TryGetRequired(optionValues, "id", optionLocation, errors, out string optionId) ||
                !TryGetRequired(optionValues, "text", optionLocation, errors, out string optionText) ||
                !RegisterId(optionId, optionIds, optionLocation, errors))
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

        HashSet<string> spriteIds = new HashSet<string>(
            content.Sprites.Select(sprite => sprite.id),
            StringComparer.OrdinalIgnoreCase);
        foreach (FlowDefinition flow in content.Flows)
        {
            foreach (string spriteId in flow.SpriteReferences)
            {
                if (!spriteIds.Contains(spriteId))
                {
                    AddError(
                        errors,
                        flow.RelativePath + ": references undeclared sprite '$" + spriteId + "'.");
                }
            }
        }
    }

    private static void ParseDialogueDirective(
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
                "id", "scene", "afterLabel", "dialogueOrdinal", "expectedSpeaker", "expectedText",
                "text", "voice", "volume"))
        {
            return;
        }

        if (!TryGetRequired(values, "id", location, errors, out string id) ||
            !TryGetRequired(values, "scene", location, errors, out string scene) ||
            !TryGetRequired(values, "afterLabel", location, errors, out string afterLabel) ||
            !RegisterId(id, ids, location, errors))
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
            !TryGetRequired(values, "id", location, errors, out string groupId) ||
            !TryGetRequired(values, "afterLabel", location, errors, out string afterLabel) ||
            !RegisterId(groupId, ids, location, errors) ||
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

        string directory = Get(values, "directory");
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

            if (!TryGetRequired(childValues, "id", childLocation, errors, out string childId) ||
                !RegisterId(childId, childIds, childLocation, errors) ||
                !TryGetVolume(childValues, "volume", defaultVolume, childLocation, errors, out float volume))
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
            ParseDialogueDirective(packageRoot, modId, flowPath, childLocation, patchValues, ids, content, errors);
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

        if (!TryGetRequired(values, "id", location, errors, out string id) ||
            !TryGetRequired(values, "image", location, errors, out string imageReference) ||
            !RegisterId(id, ids, location, errors))
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
            title = Get(values, "title"),
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

        if (!TryGetRequired(values, "id", location, errors, out string id) ||
            !TryGetRequired(values, "kind", location, errors, out string kindText) ||
            !TryGetRequired(values, "target", location, errors, out string target) ||
            !TryGetRequired(values, "source", location, errors, out string sourceReference) ||
            !RegisterId(id, ids, location, errors))
        {
            return;
        }

        if (!Enum.TryParse(kindText, true, out AssetKind kind) || kind == AssetKind.Any)
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

        content.Overlays.Add(new OverlayDefinition
        {
            id = id,
            kind = kind.ToString(),
            target = target,
            source = sourcePath
        });
    }

    private static bool TryRewriteScreenEffectCommand(
        string line,
        string location,
        List<string> errors,
        out string rewritten)
    {
        rewritten = line;
        string trimmed = line.TrimStart();
        string command = ReadFirstWord(trimmed);
        if (!string.Equals(command, "effect", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string indent = GetIndent(line);
        rewritten = indent + "wait \"0\"";
        int position = command.Length;
        while (position < trimmed.Length && char.IsWhiteSpace(trimmed[position]))
        {
            position++;
        }
        int typeStart = position;
        while (position < trimmed.Length && !char.IsWhiteSpace(trimmed[position]) && trimmed[position] != '[')
        {
            position++;
        }
        string type = trimmed.Substring(typeStart, position - typeStart);
        if (!string.Equals(type, "flash", StringComparison.OrdinalIgnoreCase))
        {
            AddError(errors, location + ": effect type must be 'flash'.");
            return true;
        }

        if (!TryParseStrictBracketAttributes(
                trimmed.Substring(position),
                out Dictionary<string, string> values,
                out string attributeError))
        {
            AddError(errors, location + ": effect flash " + attributeError);
            return true;
        }

        HashSet<string> known = new HashSet<string>(
            new[] { "color", "alpha", "in", "hold", "out", "count", "gap", "wait" },
            StringComparer.OrdinalIgnoreCase);
        bool valid = true;
        foreach (string key in values.Keys)
        {
            if (!known.Contains(key))
            {
                AddError(errors, location + ": effect flash has unknown attribute '" + key + "'.");
                valid = false;
            }
        }

        ScreenEffectRequest request = new ScreenEffectRequest();
        if (values.TryGetValue("color", out string color))
        {
            if (!TryNormalizeEffectColor(color, out request.color))
            {
                AddError(errors, location + ": effect flash color must use #RRGGBB.");
                valid = false;
            }
        }
        if (!TryGetOptionalFloat(values, "alpha", 1f, 0f, 1f, location, errors, out request.alpha) ||
            !TryGetOptionalFloat(values, "in", 0.03f, 0f, 10f, location, errors, out request.fadeIn) ||
            !TryGetOptionalFloat(values, "hold", 0.02f, 0f, 10f, location, errors, out request.hold) ||
            !TryGetOptionalFloat(values, "out", 0.12f, 0f, 10f, location, errors, out request.fadeOut) ||
            !TryGetOptionalFloat(values, "gap", 0.04f, 0f, 10f, location, errors, out request.gap) ||
            !TryGetBoolean(values, "wait", true, location, errors, out request.wait))
        {
            valid = false;
        }

        if (values.TryGetValue("count", out string countText) &&
            (!int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out request.count) ||
             request.count < 1 || request.count > 16))
        {
            AddError(errors, location + ": effect flash count must be an integer between 1 and 16.");
            valid = false;
        }

        float totalDuration = request.count * (request.fadeIn + request.hold + request.fadeOut) +
                              Math.Max(0, request.count - 1) * request.gap;
        if (totalDuration > 30f)
        {
            AddError(errors, location + ": effect flash total duration must not exceed 30 seconds.");
            valid = false;
        }

        if (valid)
        {
            rewritten = indent + "load \"" + EscapeDslString(ScreenEffectService.CreateUri(request)) + "\"";
        }
        return true;
    }

    private static bool TryParseStrictBracketAttributes(
        string text,
        out Dictionary<string, string> values,
        out string error)
    {
        values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = null;
        int position = 0;
        while (position < text.Length)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                position++;
            }
            if (position >= text.Length)
            {
                return true;
            }
            if (text[position] != '[')
            {
                error = "only accepts [name=value] attributes after the effect type.";
                return false;
            }

            int close = text.IndexOf(']', position + 1);
            if (close < 0)
            {
                error = "contains an unclosed attribute.";
                return false;
            }
            string body = text.Substring(position + 1, close - position - 1).Trim();
            int equals = body.IndexOf('=');
            if (equals <= 0)
            {
                error = "attributes must use [name=value].";
                return false;
            }
            string key = body.Substring(0, equals).Trim();
            string rawValue = body.Substring(equals + 1).Trim();
            if (key.Length == 0 || !TryDecodeAttributeValue(rawValue, out string value))
            {
                error = "contains an invalid attribute value.";
                return false;
            }
            if (values.ContainsKey(key))
            {
                error = "contains duplicate attribute '" + key + "'.";
                return false;
            }
            values[key] = value;
            position = close + 1;
        }
        return true;
    }

    private static bool TryNormalizeEffectColor(string value, out string normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length != 7 || value[0] != '#')
        {
            return false;
        }
        for (int index = 1; index < value.Length; index++)
        {
            if (!Uri.IsHexDigit(value[index]))
            {
                return false;
            }
        }
        normalized = value.ToUpperInvariant();
        return true;
    }

    private static string RewriteCommandResource(
        string line,
        string packageRoot,
        string modId,
        string flowPath,
        string location,
        FlowDefinition flow,
        List<string> errors)
    {
        string trimmed = line.TrimStart();
        string command = ReadFirstWord(trimmed);
        if (string.Equals(command, "call", StringComparison.OrdinalIgnoreCase))
        {
            return RewriteCallCommand(line, packageRoot, modId, flowPath, location, flow, errors);
        }

        if (string.Equals(command, "stopbgm", StringComparison.OrdinalIgnoreCase))
        {
            return ReplaceCommandWord(line, command, "stopmusic");
        }

        if (string.Equals(command, "bgm", StringComparison.OrdinalIgnoreCase))
        {
            line = ReplaceCommandWord(line, command, "music");
            trimmed = line.TrimStart();
            command = "music";
        }

        AssetKind kind;
        switch (command.ToLowerInvariant())
        {
            case "background": kind = AssetKind.Texture; break;
            case "audio":
            case "music": kind = AssetKind.Audio; break;
            case "video": kind = AssetKind.Video; break;
            case "load": kind = AssetKind.Text; break;
            default: return line;
        }

        if (!TryFindQuotedValue(line, 0, out int valueStart, out int valueEnd, out string reference))
        {
            return line;
        }

        if (!IsModReference(reference))
        {
            return line;
        }

        if (!TryResolveAndValidateReference(
                packageRoot,
                modId,
                flowPath,
                reference,
                kind,
                out string uri,
                out string referenceError))
        {
            AddError(errors, location + ": " + command + " resource " + referenceError);
            return line;
        }

        if (kind == AssetKind.Audio)
        {
            flow.AudioResources.Add(uri);
        }

        return line.Substring(0, valueStart) + EscapeDslString(uri) + line.Substring(valueEnd);
    }

    private static string RewriteCallCommand(
        string line,
        string packageRoot,
        string modId,
        string flowPath,
        string location,
        FlowDefinition flow,
        List<string> errors)
    {
        string trimmed = line.TrimStart();
        string command = ReadFirstWord(trimmed);
        if (!TryFindQuotedValue(line, line.IndexOf(command, StringComparison.Ordinal) + command.Length,
                out _, out int valueEnd, out string reference))
        {
            AddError(errors, location + ": call expects a quoted flow path.");
            return line;
        }

        if (!TryResolveAndValidateReference(
                packageRoot,
                modId,
                flowPath,
                reference,
                AssetKind.Text,
                out string targetUri,
                out string targetError))
        {
            AddError(errors, location + ": call target " + targetError);
            return line;
        }

        Dictionary<string, string> attributes = ParseBracketAttributes(line.Substring(valueEnd));
        string entryLabel = null;
        if (attributes.TryGetValue("enter", out string enter))
        {
            entryLabel = enter;
        }
        if (attributes.TryGetValue("entryLabel", out string explicitEntry))
        {
            if (!string.IsNullOrWhiteSpace(entryLabel) &&
                !string.Equals(entryLabel, explicitEntry, StringComparison.OrdinalIgnoreCase))
            {
                AddError(errors, location + ": call cannot specify different enter and entryLabel values.");
                return line;
            }
            entryLabel = explicitEntry;
        }

        flow.Calls.Add(new FlowCallReference
        {
            target = targetUri,
            entryLabel = entryLabel,
            location = location
        });
        string indent = GetIndent(line);
        return indent + "load \"" + EscapeDslString(CreateCallUri(targetUri, entryLabel)) + "\"";
    }

    private static string RewriteSpriteReference(string line, string modId, FlowDefinition flow)
    {
        int indentLength = line.Length - line.TrimStart().Length;
        string trimmed = line.Substring(indentLength);
        int tokenStart;
        if (trimmed.StartsWith("$", StringComparison.Ordinal))
        {
            tokenStart = indentLength;
        }
        else
        {
            string command = ReadFirstWord(trimmed);
            if (!SpriteTargetCommands.Contains(command))
            {
                return line;
            }

            tokenStart = indentLength + command.Length;
            while (tokenStart < line.Length && char.IsWhiteSpace(line[tokenStart]))
            {
                tokenStart++;
            }
            if (tokenStart >= line.Length || line[tokenStart] != '$')
            {
                return line;
            }
        }

        int idStart = tokenStart + 1;
        int idEnd = idStart;
        while (idEnd < line.Length &&
               (char.IsLetterOrDigit(line[idEnd]) || line[idEnd] == '_' || line[idEnd] == '-' || line[idEnd] == '.'))
        {
            idEnd++;
        }
        if (idEnd == idStart)
        {
            return line;
        }

        string id = line.Substring(idStart, idEnd - idStart);
        flow.SpriteReferences.Add(id);
        return line.Substring(0, tokenStart) + GetSpriteInternalName(modId, id) + line.Substring(idEnd);
    }

    internal static string CreateCallUri(string targetUri, string entryLabel)
    {
        string payload = (targetUri ?? string.Empty) + "\n" + (entryLabel ?? string.Empty);
        return CallUriPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    internal static bool TryParseCallUri(string value, out string targetUri, out string entryLabel)
    {
        targetUri = null;
        entryLabel = null;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(CallUriPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            string encoded = value.Substring(CallUriPrefix.Length).Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            string payload = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            int separator = payload.IndexOf('\n');
            if (separator <= 0)
            {
                return false;
            }
            targetUri = payload.Substring(0, separator);
            entryLabel = payload.Substring(separator + 1);
            return !string.IsNullOrWhiteSpace(targetUri);
        }
        catch
        {
            targetUri = null;
            entryLabel = null;
            return false;
        }
    }

    private static string ReplaceCommandWord(string line, string command, string replacement)
    {
        int start = line.Length - line.TrimStart().Length;
        return line.Substring(0, start) + replacement + line.Substring(start + command.Length);
    }

    private static bool TryResolveAndValidateReference(
        string packageRoot,
        string modId,
        string flowPath,
        string reference,
        AssetKind kind,
        out string uri,
        out string error)
    {
        uri = null;
        error = null;
        if (!TryResolveFlowReference(modId, flowPath, reference, out string relativePath, out uri, out error))
        {
            return false;
        }

        if (!ManifestValidator.TryValidateAssetReference(packageRoot, relativePath, kind, out error))
        {
            return false;
        }

        return true;
    }

    private static bool TryResolveFlowReference(
        string modId,
        string flowPath,
        string reference,
        out string relativePath,
        out string uri,
        out string error)
    {
        relativePath = null;
        uri = null;
        error = null;
        if (string.IsNullOrWhiteSpace(reference))
        {
            error = "must not be empty.";
            return false;
        }

        if (reference.StartsWith("mod://", StringComparison.OrdinalIgnoreCase))
        {
            error = "must use @/ or ./; explicit mod:// references are not supported in Manifest v2 flows.";
            return false;
        }

        string combined;
        if (reference.StartsWith("@/", StringComparison.Ordinal))
        {
            combined = reference.Substring(2);
        }
        else if (reference.StartsWith("./", StringComparison.Ordinal) ||
                 reference.StartsWith("../", StringComparison.Ordinal))
        {
            string directory = Path.GetDirectoryName(flowPath)?.Replace('\\', '/') ?? string.Empty;
            combined = string.IsNullOrEmpty(directory) ? reference : directory + "/" + reference;
        }
        else
        {
            error = "must use @/ for the Mod root or ./ for the current flow directory.";
            return false;
        }

        if (!TryCollapseRelativePath(combined, out relativePath))
        {
            error = "escapes the Mod package or contains an invalid path component.";
            return false;
        }

        uri = LoaderUtil.ToModUri(modId, relativePath);
        return true;
    }

    private static bool TryCollapseRelativePath(string value, out string result)
    {
        result = null;
        string[] parts = value.Replace('\\', '/').Split('/');
        List<string> stack = new List<string>();
        foreach (string rawPart in parts)
        {
            string part = rawPart.Trim();
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (stack.Count == 0)
                {
                    return false;
                }

                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            if (!string.Equals(part, rawPart, StringComparison.Ordinal) || part.IndexOf(':') >= 0)
            {
                return false;
            }

            stack.Add(part);
        }

        if (stack.Count == 0)
        {
            return false;
        }

        result = string.Join("/", stack);
        return true;
    }

    private static bool IsModReference(string value)
    {
        return value.StartsWith("@/", StringComparison.Ordinal) ||
               value.StartsWith("./", StringComparison.Ordinal) ||
               value.StartsWith("../", StringComparison.Ordinal) ||
               value.StartsWith("mod://", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseVoiceCommand(string line, out string reference, out float volume, out string error)
    {
        reference = null;
        volume = 1f;
        error = null;
        string command = ReadFirstWord(line);
        if (!string.Equals(command, "voice", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryFindQuotedValue(line, command.Length, out _, out int valueEnd, out reference))
        {
            error = "voice expects a quoted resource path.";
            return true;
        }

        Dictionary<string, string> attributes = ParseBracketAttributes(line.Substring(valueEnd));
        if (attributes.TryGetValue("volume", out string volumeText) && !TryParseVolume(volumeText, out volume))
        {
            error = "voice volume must be between 0 and 1.";
        }

        return true;
    }

    private static bool TryParseDialogueLine(
        string line,
        out string speaker,
        out string text,
        out Dictionary<string, string> attributes)
    {
        speaker = null;
        text = null;
        attributes = null;
        string firstWord = ReadFirstWord(line);
        if (string.IsNullOrEmpty(firstWord) || NonDialogueCommands.Contains(firstWord))
        {
            return false;
        }

        int firstQuote = line.IndexOf('"');
        if (firstQuote <= 0)
        {
            return false;
        }

        speaker = line.Substring(0, firstQuote).Trim();
        if (speaker.Length == 0 || speaker.IndexOfAny(new[] { ' ', '\t', ':' }) >= 0 ||
            !TryFindQuotedValue(line, firstQuote, out _, out int valueEnd, out text))
        {
            return false;
        }

        attributes = ParseBracketAttributes(line.Substring(valueEnd));
        return true;
    }

    private static Dictionary<string, string> ParseBracketAttributes(string text)
    {
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int position = 0;
        while (position < text.Length)
        {
            int open = text.IndexOf('[', position);
            if (open < 0)
            {
                break;
            }

            int close = text.IndexOf(']', open + 1);
            if (close < 0)
            {
                break;
            }

            string body = text.Substring(open + 1, close - open - 1).Trim();
            int equals = body.IndexOf('=');
            if (equals > 0)
            {
                string key = body.Substring(0, equals).Trim();
                string rawValue = body.Substring(equals + 1).Trim();
                if (TryDecodeAttributeValue(rawValue, out string value))
                {
                    result[key] = value;
                }
            }

            position = close + 1;
        }

        return result;
    }

    private static bool TryDecodeAttributeValue(string value, out string decoded)
    {
        decoded = value;
        if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
        {
            return TryDecodeQuoted(value, 0, out decoded, out _);
        }

        return value.Length > 0;
    }

    private static bool TryFindQuotedValue(
        string text,
        int startIndex,
        out int valueStart,
        out int valueEnd,
        out string value)
    {
        valueStart = -1;
        valueEnd = -1;
        value = null;
        int quote = text.IndexOf('"', Math.Max(0, startIndex));
        if (quote < 0 || !TryDecodeQuoted(text, quote, out value, out int closingQuote))
        {
            return false;
        }

        valueStart = quote + 1;
        valueEnd = closingQuote;
        return true;
    }

    private static bool TryDecodeQuoted(string text, int quoteIndex, out string value, out int closingQuote)
    {
        value = null;
        closingQuote = -1;
        StringBuilder builder = new StringBuilder();
        for (int i = quoteIndex + 1; i < text.Length; i++)
        {
            char character = text[i];
            if (character == '"')
            {
                value = builder.ToString();
                closingQuote = i;
                return true;
            }

            if (character == '\\' && i + 1 < text.Length)
            {
                char escaped = text[++i];
                builder.Append(escaped == 'n' ? '\n' : escaped == 'r' ? '\r' : escaped == 't' ? '\t' : escaped);
            }
            else
            {
                builder.Append(character);
            }
        }

        return false;
    }

    private static void TryCollectDeclaration(
        string[] lines,
        int startLine,
        out string text,
        out int endLine)
    {
        StringBuilder builder = new StringBuilder();
        int depth = 0;
        bool sawBlock = false;
        bool inQuote = false;
        bool escaped = false;
        int detachedOpeningLine = FindDetachedOpeningBrace(lines, startLine);
        endLine = startLine;

        for (int lineIndex = startLine; lineIndex < lines.Length; lineIndex++)
        {
            string line = lines[lineIndex];
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }
            builder.Append(line);

            for (int position = 0; position < line.Length; position++)
            {
                char character = line[position];
                if (inQuote)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        inQuote = false;
                    }
                    continue;
                }

                if (character == '"')
                {
                    inQuote = true;
                }
                else if (character == '/' && position + 1 < line.Length && line[position + 1] == '/')
                {
                    break;
                }
                else if (character == '{')
                {
                    sawBlock = true;
                    depth++;
                }
                else if (character == '}')
                {
                    depth--;
                }
            }

            endLine = lineIndex;
            if (!sawBlock && detachedOpeningLine >= 0 && lineIndex < detachedOpeningLine)
            {
                continue;
            }

            if (!sawBlock || depth <= 0)
            {
                break;
            }
        }

        text = builder.ToString();
    }

    private static int FindDetachedOpeningBrace(string[] lines, int startLine)
    {
        string header = StripLineComment(lines[startLine]).Trim();
        int position = header.StartsWith(DirectivePrefix, StringComparison.Ordinal) ? 1 : 0;
        while (position < header.Length &&
               (char.IsLetterOrDigit(header[position]) || header[position] == '_' || header[position] == '-'))
        {
            position++;
        }

        if (position <= 1 || header.Substring(position).Trim().Length != 0)
        {
            return -1;
        }

        for (int lineIndex = startLine + 1; lineIndex < lines.Length; lineIndex++)
        {
            string candidate = StripLineComment(lines[lineIndex]).Trim();
            if (candidate.Length == 0)
            {
                continue;
            }

            return candidate.StartsWith("{", StringComparison.Ordinal) ? lineIndex : -1;
        }

        return -1;
    }

    private static string StripLineComment(string line)
    {
        bool inQuote = false;
        bool escaped = false;
        for (int position = 0; position + 1 < line.Length; position++)
        {
            char character = line[position];
            if (inQuote)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inQuote = false;
                }
            }
            else if (character == '"')
            {
                inQuote = true;
            }
            else if (character == '/' && line[position + 1] == '/')
            {
                return line.Substring(0, position);
            }
        }

        return line;
    }

    private static bool TryParseFlowDeclaration(
        string text,
        int startLine,
        out FlowDeclarationNode declaration,
        out string error,
        out int errorLine)
    {
        FlowDeclarationParser parser = new FlowDeclarationParser(text, startLine);
        return parser.TryParse(out declaration, out error, out errorLine);
    }

    private sealed class FlowDeclarationParser
    {
        private const int MaxBlockDepth = 16;
        private readonly string _text;
        private int _position;
        private int _line;

        internal FlowDeclarationParser(string text, int startLine)
        {
            _text = text ?? string.Empty;
            _line = startLine;
        }

        internal bool TryParse(
            out FlowDeclarationNode declaration,
            out string error,
            out int errorLine)
        {
            declaration = null;
            error = null;
            errorLine = _line;
            SkipSeparators();
            if (!TryConsume('@'))
            {
                return Fail("flow declaration must start with '@'.", out error, out errorLine);
            }

            int declarationLine = _line;
            string kind = ReadIdentifier();
            if (string.IsNullOrEmpty(kind))
            {
                return Fail("flow declaration kind is missing after '@'.", out error, out errorLine);
            }

            declaration = new FlowDeclarationNode { Kind = kind, LineNumber = declarationLine };
            SkipSeparators();
            if (TryConsume('{'))
            {
                if (!TryParseBlock(declaration, 1, out error, out errorLine))
                {
                    declaration = null;
                    return false;
                }
            }
            else if (!TryParseFlatFields(declaration, out error, out errorLine))
            {
                declaration = null;
                return false;
            }

            SkipSeparators();
            if (_position != _text.Length)
            {
                declaration = null;
                return Fail("unexpected text after @" + kind + " declaration.", out error, out errorLine);
            }

            return true;
        }

        private bool TryParseBlock(
            FlowDeclarationNode node,
            int depth,
            out string error,
            out int errorLine)
        {
            error = null;
            errorLine = _line;
            if (depth > MaxBlockDepth)
            {
                return Fail(
                    "declaration block nesting exceeds the limit of " + MaxBlockDepth + ".",
                    out error,
                    out errorLine);
            }

            while (true)
            {
                SkipSeparators();
                if (_position >= _text.Length)
                {
                    return Fail("unterminated @" + node.Kind + " block; expected '}'.", out error, out errorLine);
                }

                if (TryConsume('}'))
                {
                    return true;
                }

                int memberLine = _line;
                string name = ReadIdentifier();
                if (string.IsNullOrEmpty(name))
                {
                    return Fail("expected field or child block in @" + node.Kind + ".", out error, out errorLine);
                }

                SkipWhitespaceAndComments();
                if (TryConsume('='))
                {
                    if (!TryReadValue(name, out string value, out error, out errorLine))
                    {
                        return false;
                    }

                    if (node.Values.ContainsKey(name))
                    {
                        return Fail("duplicate value for '" + name + "'.", out error, out errorLine);
                    }
                    node.Values[name] = value;
                }
                else if (TryConsume('{'))
                {
                    FlowDeclarationNode child = new FlowDeclarationNode { Kind = name, LineNumber = memberLine };
                    if (!TryParseBlock(child, depth + 1, out error, out errorLine))
                    {
                        return false;
                    }
                    node.Children.Add(child);
                }
                else
                {
                    return Fail("expected '=' or '{' after '" + name + "'.", out error, out errorLine);
                }
            }
        }

        private bool TryParseFlatFields(
            FlowDeclarationNode node,
            out string error,
            out int errorLine)
        {
            error = null;
            errorLine = _line;
            while (true)
            {
                SkipSeparators();
                if (_position >= _text.Length)
                {
                    return true;
                }

                string key = ReadIdentifier();
                SkipWhitespaceAndComments();
                if (string.IsNullOrEmpty(key) || !TryConsume('='))
                {
                    return Fail("expected key=value after @" + node.Kind + ".", out error, out errorLine);
                }

                if (!TryReadValue(key, out string value, out error, out errorLine))
                {
                    return false;
                }

                if (node.Values.ContainsKey(key))
                {
                    return Fail("duplicate value for '" + key + "'.", out error, out errorLine);
                }
                node.Values[key] = value;
            }
        }

        private bool TryReadValue(
            string key,
            out string value,
            out string error,
            out int errorLine)
        {
            value = null;
            error = null;
            errorLine = _line;
            SkipWhitespaceAndComments();
            if (_position >= _text.Length)
            {
                return Fail("value for '" + key + "' is missing.", out error, out errorLine);
            }

            if (_text[_position] == '"')
            {
                _position++;
                StringBuilder builder = new StringBuilder();
                bool escaped = false;
                while (_position < _text.Length)
                {
                    char character = ReadCharacter();
                    if (escaped)
                    {
                        builder.Append(character == 'n' ? '\n' : character == 'r' ? '\r' : character == 't' ? '\t' : character);
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        value = builder.ToString();
                        return value.Length > 0 || Fail("value for '" + key + "' must not be empty.", out error, out errorLine);
                    }
                    else
                    {
                        builder.Append(character);
                    }
                }

                return Fail("unterminated quoted value for '" + key + "'.", out error, out errorLine);
            }

            int start = _position;
            while (_position < _text.Length &&
                   !char.IsWhiteSpace(_text[_position]) &&
                   _text[_position] != ',' &&
                   _text[_position] != '}')
            {
                _position++;
            }

            value = _text.Substring(start, _position - start);
            if (value.Length == 0)
            {
                return Fail("value for '" + key + "' must not be empty.", out error, out errorLine);
            }

            return true;
        }

        private void SkipSeparators()
        {
            while (true)
            {
                SkipWhitespaceAndComments();
                if (!TryConsume(','))
                {
                    return;
                }
            }
        }

        private void SkipWhitespaceAndComments()
        {
            while (_position < _text.Length)
            {
                if (char.IsWhiteSpace(_text[_position]))
                {
                    ReadCharacter();
                    continue;
                }

                if (_text[_position] == '/' && _position + 1 < _text.Length && _text[_position + 1] == '/')
                {
                    _position += 2;
                    while (_position < _text.Length && _text[_position] != '\n')
                    {
                        _position++;
                    }
                    continue;
                }

                return;
            }
        }

        private string ReadIdentifier()
        {
            int start = _position;
            while (_position < _text.Length &&
                   (char.IsLetterOrDigit(_text[_position]) || _text[_position] == '_' || _text[_position] == '-'))
            {
                _position++;
            }
            return _text.Substring(start, _position - start);
        }

        private char ReadCharacter()
        {
            char character = _text[_position++];
            if (character == '\n')
            {
                _line++;
            }
            return character;
        }

        private bool TryConsume(char expected)
        {
            if (_position >= _text.Length || _text[_position] != expected)
            {
                return false;
            }
            _position++;
            return true;
        }

        private bool Fail(string message, out string error, out int errorLine)
        {
            error = message;
            errorLine = _line;
            return false;
        }
    }

    private static string ReadFirstWord(string value)
    {
        int length = 0;
        while (length < value.Length && !char.IsWhiteSpace(value[length]) && value[length] != ':')
        {
            length++;
        }

        return value.Substring(0, length);
    }

    private static bool TryParseLabel(string line, out string label)
    {
        label = null;
        if (!line.StartsWith("label", StringComparison.OrdinalIgnoreCase) ||
            line.Length <= "label".Length || !char.IsWhiteSpace(line["label".Length]))
        {
            return false;
        }

        string remainder = line.Substring("label".Length).Trim();
        if (!remainder.EndsWith(":", StringComparison.Ordinal))
        {
            return false;
        }

        label = remainder.Substring(0, remainder.Length - 1).Trim();
        return label.Length > 0;
    }

    private static string GetIndent(string line)
    {
        int length = 0;
        while (length < line.Length && char.IsWhiteSpace(line[length]))
        {
            length++;
        }

        return line.Substring(0, length);
    }

    private static string EscapeDslString(string value)
    {
        return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static bool TryGetRequired(
        Dictionary<string, string> values,
        string key,
        string location,
        List<string> errors,
        out string value)
    {
        if (values.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        AddError(errors, location + ": required declaration value '" + key + "' is missing.");
        value = null;
        return false;
    }

    private static string Get(Dictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out string value) ? value : null;
    }

    private static bool RegisterId(string id, HashSet<string> ids, string location, List<string> errors)
    {
        if (!ManifestValidator.TryValidateEntryId(id, out string idError))
        {
            AddError(errors, location + ": id " + idError);
            return false;
        }

        if (!ids.Add(id))
        {
            AddError(errors, location + ": duplicate id '" + id + "'.");
            return false;
        }

        return true;
    }

    private static bool ValidateKnownKeys(
        Dictionary<string, string> values,
        string location,
        List<string> errors,
        params string[] allowedKeys)
    {
        HashSet<string> allowed = new HashSet<string>(allowedKeys, StringComparer.OrdinalIgnoreCase);
        bool valid = true;
        foreach (string key in values.Keys)
        {
            if (allowed.Contains(key))
            {
                continue;
            }

            AddError(errors, location + ": unknown declaration field '" + key + "'.");
            valid = false;
        }

        return valid;
    }

    private static bool TryGetBoolean(
        Dictionary<string, string> values,
        string key,
        bool defaultValue,
        string location,
        List<string> errors,
        out bool value)
    {
        value = defaultValue;
        if (!values.TryGetValue(key, out string text))
        {
            return true;
        }

        switch (text.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "t":
            case "yes":
                value = true;
                return true;
            case "0":
            case "false":
            case "f":
            case "no":
                value = false;
                return true;
            default:
                AddError(errors, location + ": '" + key + "' must be true or false.");
                return false;
        }
    }

    private static bool TryGetVolume(
        Dictionary<string, string> values,
        string key,
        float defaultValue,
        string location,
        List<string> errors,
        out float value)
    {
        value = defaultValue;
        if (!values.TryGetValue(key, out string text))
        {
            return true;
        }

        if (TryParseVolume(text, out value))
        {
            return true;
        }

        AddError(errors, location + ": '" + key + "' must be between 0 and 1.");
        return false;
    }

    private static bool TryGetOptionalFloat(
        Dictionary<string, string> values,
        string key,
        float defaultValue,
        float minimum,
        float maximum,
        string location,
        List<string> errors,
        out float value)
    {
        value = defaultValue;
        if (!values.TryGetValue(key, out string text))
        {
            return true;
        }

        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            !float.IsNaN(value) && !float.IsInfinity(value) && value >= minimum && value <= maximum)
        {
            return true;
        }

        AddError(
            errors,
            location + ": '" + key + "' must be a number between " +
            minimum.ToString(CultureInfo.InvariantCulture) + " and " +
            maximum.ToString(CultureInfo.InvariantCulture) + ".");
        return false;
    }

    private static bool TryParseVolume(string text, out float volume)
    {
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out volume) &&
               volume >= 0f && volume <= 1f;
    }

    private static List<string> EnumerateFlowFiles(string storyRoot, List<string> errors)
    {
        List<string> result = new List<string>();
        Stack<string> pending = new Stack<string>();
        pending.Push(storyRoot);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            if (IsReparsePoint(directory))
            {
                AddError(errors, directory + ": flow directory is a symbolic link or reparse point.");
                continue;
            }

            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                string extension = Path.GetExtension(file);
                if (!string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".sunny", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsReparsePoint(file))
                {
                    AddError(errors, file + ": flow file is a symbolic link or reparse point.");
                    continue;
                }

                result.Add(file);
            }

            foreach (string child in Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                pending.Push(child);
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static bool IsReparsePoint(string path)
    {
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static HashSet<string> SeedIds(IEnumerable<string> values)
    {
        HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (values == null)
        {
            return result;
        }

        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static string NormalizeRelativePath(string value)
    {
        return (value ?? string.Empty).Replace('\\', '/').TrimStart('/');
    }

    private static void AddError(List<string> errors, string error)
    {
        if (errors.Count < MaxErrors)
        {
            errors.Add(error);
        }
    }

    private sealed class PendingVoice
    {
        internal string Uri;
        internal float Volume;
    }
}
