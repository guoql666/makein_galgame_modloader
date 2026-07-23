using System;
using System.Collections.Generic;
using System.IO;
using MakeineGalGameQM.Core.Utils;

namespace SunnyModLoader;

internal static class LoaderUtil
{
    private static readonly HashSet<string> BlockedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".bat", ".cmd", ".com", ".msi", ".ps1", ".scr"
    };

    internal static string NormalizeResourceKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string text = value.Trim().Replace('\\', '/').TrimStart('/');
        int queryIndex = text.IndexOfAny(new[] { '?', '#' });
        if (queryIndex >= 0)
        {
            text = text.Substring(0, queryIndex);
        }

        string extension = Path.GetExtension(text);
        if (!string.IsNullOrEmpty(extension))
        {
            text = text.Substring(0, text.Length - extension.Length);
        }

        return text.Trim().ToLowerInvariant();
    }

    internal static string NormalizeScene(string value)
    {
        return NormalizeResourceKey(value);
    }

    internal static bool TryResolvePackageFile(string packageRoot, string relativePath, out string fullPath)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(packageRoot) || string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        try
        {
            if (!string.Equals(relativePath, relativePath.Trim(), StringComparison.Ordinal))
            {
                return false;
            }

            string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);

            if (Path.IsPathRooted(normalized) || normalized.IndexOf(':') >= 0 ||
                BlockedExtensions.Contains(Path.GetExtension(normalized)))
            {
                return false;
            }

            string[] parts = normalized.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.None);
            foreach (string part in parts)
            {
                if (part.Length == 0 || part == "." || part == "..")
                {
                    return false;
                }
            }

            string rootPath = NormalizeDirectoryPath(Path.GetFullPath(packageRoot));
            if (!Directory.Exists(rootPath) || IsReparsePoint(rootPath))
            {
                return false;
            }

            string rootPrefix = AppendDirectorySeparator(rootPath);
            string candidate = Path.GetFullPath(Path.Combine(rootPath, normalized));
            if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate) ||
                HasReparsePointBelowRoot(rootPath, parts))
            {
                return false;
            }

            fullPath = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string ToModUri(string modId, string relativePath)
    {
        string path = (relativePath ?? string.Empty).Replace('\\', '/').TrimStart('/');
        return "mod://" + modId + "/" + path;
    }

    internal static bool TryParseModUri(string value, out string modId, out string relativePath)
    {
        modId = null;
        relativePath = null;
        const string Prefix = "mod://";
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string remainder = value.Substring(Prefix.Length);
        int separator = remainder.IndexOf('/');
        if (separator <= 0 || separator >= remainder.Length - 1)
        {
            return false;
        }

        modId = remainder.Substring(0, separator);
        try
        {
            relativePath = Uri.UnescapeDataString(remainder.Substring(separator + 1));
            return true;
        }
        catch
        {
            modId = null;
            relativePath = null;
            return false;
        }
    }

    internal static string GetStringParameter(IList<ICommandParameter> parameters, string name)
    {
        if (parameters == null)
        {
            return null;
        }

        for (int i = 0; i < parameters.Count; i++)
        {
            if (parameters[i] is CommandParameter<string> parameter &&
                string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return parameter.Value;
            }
        }

        return null;
    }

    internal static string GetStringParameter(ICommandParameter[] parameters, string name)
    {
        return GetStringParameter((IList<ICommandParameter>)parameters, name);
    }

    internal static bool SetStringParameter(IList<ICommandParameter> parameters, string name, string value)
    {
        if (parameters == null)
        {
            return false;
        }

        for (int i = 0; i < parameters.Count; i++)
        {
            if (parameters[i] is CommandParameter<string> parameter &&
                string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                parameter.Value = value ?? string.Empty;
                return true;
            }
        }

        return false;
    }

    internal static string SafeId(string value)
    {
        string source = string.IsNullOrEmpty(value) ? "unnamed" : value;
        char[] chars = source.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_')
            {
                chars[i] = '_';
            }
        }

        return new string(chars) + "_" + StableHash(source).ToString("x8");
    }

    private static bool HasReparsePointBelowRoot(string rootPath, string[] parts)
    {
        string current = rootPath;
        foreach (string part in parts)
        {
            current = Path.Combine(current, part);
            if (IsReparsePoint(current))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReparsePoint(string path)
    {
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static string NormalizeDirectoryPath(string path)
    {
        string root = Path.GetPathRoot(path);
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string AppendDirectorySeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
            path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            return path;
        }

        return path + Path.DirectorySeparatorChar;
    }

    private static uint StableHash(string value)
    {
        const uint OffsetBasis = 2166136261;
        const uint Prime = 16777619;
        uint hash = OffsetBasis;
        foreach (char character in value.ToUpperInvariant())
        {
            hash ^= character;
            hash *= Prime;
        }

        return hash;
    }
}
