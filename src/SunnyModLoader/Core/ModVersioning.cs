using System;
using System.Collections.Generic;
using System.Globalization;

namespace SunnyModLoader;

internal readonly struct ModSemanticVersion : IComparable<ModSemanticVersion>
{
    private readonly ulong _major;
    private readonly ulong _minor;
    private readonly ulong _patch;
    private readonly string[] _prerelease;

    private ModSemanticVersion(ulong major, ulong minor, ulong patch, string[] prerelease)
    {
        _major = major;
        _minor = minor;
        _patch = patch;
        _prerelease = prerelease ?? Array.Empty<string>();
    }

    internal static bool TryParse(string text, out ModSemanticVersion version, out string error)
    {
        version = default;
        if (!ManifestValidator.TryValidateSemVer(text, out error))
        {
            return false;
        }

        int buildSeparator = text.IndexOf('+');
        string coreAndPrerelease = buildSeparator < 0 ? text : text.Substring(0, buildSeparator);
        int prereleaseSeparator = coreAndPrerelease.IndexOf('-');
        string core = prereleaseSeparator < 0
            ? coreAndPrerelease
            : coreAndPrerelease.Substring(0, prereleaseSeparator);
        string prerelease = prereleaseSeparator < 0
            ? null
            : coreAndPrerelease.Substring(prereleaseSeparator + 1);
        string[] parts = core.Split('.');
        if (parts.Length != 3 ||
            !ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out ulong major) ||
            !ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong minor) ||
            !ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out ulong patch))
        {
            error = "must use a valid SemVer core.";
            return false;
        }

        version = new ModSemanticVersion(
            major,
            minor,
            patch,
            prerelease == null ? Array.Empty<string>() : prerelease.Split('.'));
        return true;
    }

    public int CompareTo(ModSemanticVersion other)
    {
        int result = _major.CompareTo(other._major);
        if (result != 0)
        {
            return result;
        }

        result = _minor.CompareTo(other._minor);
        if (result != 0)
        {
            return result;
        }

        result = _patch.CompareTo(other._patch);
        if (result != 0)
        {
            return result;
        }

        bool thisStable = _prerelease.Length == 0;
        bool otherStable = other._prerelease.Length == 0;
        if (thisStable != otherStable)
        {
            return thisStable ? 1 : -1;
        }

        for (int index = 0; index < Math.Min(_prerelease.Length, other._prerelease.Length); index++)
        {
            string left = _prerelease[index];
            string right = other._prerelease[index];
            bool leftNumeric = ulong.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out ulong leftNumber);
            bool rightNumeric = ulong.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out ulong rightNumber);
            if (leftNumeric && rightNumeric)
            {
                result = leftNumber.CompareTo(rightNumber);
            }
            else if (leftNumeric != rightNumeric)
            {
                result = leftNumeric ? -1 : 1;
            }
            else
            {
                result = string.CompareOrdinal(left, right);
            }

            if (result != 0)
            {
                return result;
            }
        }

        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }
}

internal sealed class ModVersionRange
{
    private readonly List<ModVersionConstraint> _constraints;

    private ModVersionRange(List<ModVersionConstraint> constraints)
    {
        _constraints = constraints;
    }

    internal static bool TryParse(string text, out ModVersionRange range, out string error)
    {
        range = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "must not be empty.";
            return false;
        }
        if (text.Length > 256)
        {
            error = "must be at most 256 ASCII characters.";
            return false;
        }

        string normalized = text.Replace(',', ' ');
        string[] tokens = normalized.Split(
            new[] { ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            error = "must contain at least one version constraint.";
            return false;
        }

        List<ModVersionConstraint> constraints = new List<ModVersionConstraint>();
        foreach (string token in tokens)
        {
            if (token == "*")
            {
                if (tokens.Length != 1)
                {
                    error = "'*' cannot be combined with another constraint.";
                    return false;
                }

                range = new ModVersionRange(constraints);
                return true;
            }

            string operatorText = string.Empty;
            if (token.StartsWith(">=", StringComparison.Ordinal) || token.StartsWith("<=", StringComparison.Ordinal))
            {
                operatorText = token.Substring(0, 2);
            }
            else if (token[0] == '>' || token[0] == '<' || token[0] == '=')
            {
                operatorText = token.Substring(0, 1);
            }

            string versionText = token.Substring(operatorText.Length);
            if (!ModSemanticVersion.TryParse(versionText, out ModSemanticVersion version, out string versionError))
            {
                error = "constraint '" + token + "' is invalid: " + versionError;
                return false;
            }

            constraints.Add(new ModVersionConstraint(operatorText, version));
        }

        range = new ModVersionRange(constraints);
        return true;
    }

    internal bool Matches(string versionText)
    {
        if (!ModSemanticVersion.TryParse(versionText, out ModSemanticVersion version, out _))
        {
            return false;
        }

        foreach (ModVersionConstraint constraint in _constraints)
        {
            if (!constraint.Matches(version))
            {
                return false;
            }
        }

        return true;
    }

    private readonly struct ModVersionConstraint
    {
        private readonly string _operator;
        private readonly ModSemanticVersion _version;

        internal ModVersionConstraint(string @operator, ModSemanticVersion version)
        {
            _operator = @operator;
            _version = version;
        }

        internal bool Matches(ModSemanticVersion version)
        {
            int comparison = version.CompareTo(_version);
            return _operator switch
            {
                ">" => comparison > 0,
                ">=" => comparison >= 0,
                "<" => comparison < 0,
                "<=" => comparison <= 0,
                _ => comparison == 0
            };
        }
    }
}
