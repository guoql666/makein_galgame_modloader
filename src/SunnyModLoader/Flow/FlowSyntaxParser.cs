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

    private static bool TryGetOrCreateId(
        Dictionary<string, string> values,
        string automaticKind,
        HashSet<string> ids,
        string location,
        List<string> errors,
        out string id,
        params string[] semanticParts)
    {
        if (values.TryGetValue("id", out id))
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                AddError(errors, location + ": declaration id must not be empty when supplied.");
                id = null;
                return false;
            }
        }
        else
        {
            id = CreateAutomaticId(automaticKind, semanticParts);
        }

        return RegisterId(id, ids, location, errors);
    }

    private static string CreateAutomaticId(string kind, params string[] semanticParts)
    {
        StringBuilder input = new StringBuilder();
        input.Append(kind.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(kind);
        foreach (string part in semanticParts)
        {
            string value = part ?? string.Empty;
            input.Append('|')
                .Append(value.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(value);
        }

        byte[] digest;
        using (SHA256 sha = SHA256.Create())
        {
            digest = sha.ComputeHash(StrictUtf8.GetBytes(input.ToString()));
        }

        StringBuilder token = new StringBuilder(16);
        for (int index = 0; index < 8; index++)
        {
            token.Append(digest[index].ToString("x2", CultureInfo.InvariantCulture));
        }
        return "auto-" + kind + "-" + token;
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
