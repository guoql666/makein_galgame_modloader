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
}
