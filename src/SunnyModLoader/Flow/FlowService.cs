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
                    "@text, @voice, @voices, @sprite, @spine, @gallery, or @replace.");
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
}
