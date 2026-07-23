using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using BepInEx.Logging;

namespace SunnyModLoader;

internal static class ManifestValidator
{
    private const int MaxReportedErrors = 32;
    private const int MaxPackageIdLength = 128;
    private const int MaxEntryIdLength = 64;
    private const int MaxSemVerLength = 256;
    private const int MaxSemVerIdentifierLength = 64;
    private const int MaxModRelationships = 128;
    private const long MaxTextAssetBytes = 16L * 1024L * 1024L;
    private const long MaxTextureAssetBytes = 256L * 1024L * 1024L;
    private const long MaxAudioAssetBytes = 512L * 1024L * 1024L;
    private const long MaxVideoAssetBytes = 2L * 1024L * 1024L * 1024L;
    private const long MaxAssetBundleBytes = 1024L * 1024L * 1024L;
    private const int MaxTextureDimension = 16384;
    private const long MaxTexturePixels = 67108864L;
    private static readonly HashSet<string> ManifestProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "schemaVersion", "id", "name", "version", "authors", "compatibility", "defaults", "settings",
        "dependencies", "conflicts"
    };
    private static readonly HashSet<string> WindowsDeviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    internal static bool Validate(
        ModManifest manifest,
        string packageRoot,
        string manifestPath,
        string gameBuild,
        int loaderApiVersion,
        ManualLogSource log)
    {
        return Validate(
            manifest,
            packageRoot,
            manifestPath,
            gameBuild,
            loaderApiVersion,
            log,
            out _);
    }

    internal static bool Validate(
        ModManifest manifest,
        string packageRoot,
        string manifestPath,
        string gameBuild,
        int loaderApiVersion,
        ManualLogSource log,
        out FlowPackageContent flowContent)
    {
        flowContent = new FlowPackageContent();
        ValidationContext context = new ValidationContext(packageRoot);
        try
        {
            context.Validate(manifest, loaderApiVersion);
        }
        catch (Exception ex)
        {
            log.LogError("Manifest validator rejected " + manifestPath + ": " + ex.Message);
            return false;
        }

        if (context.Errors.Count > 0)
        {
            log.LogError("Invalid mod manifest: " + manifestPath);
            foreach (string error in context.Errors)
            {
                log.LogError("  " + error);
            }

            if (context.SuppressedErrorCount > 0)
            {
                log.LogError("  ... and " + context.SuppressedErrorCount + " more error(s).");
            }

            return false;
        }

        CompatibilityManifest compatibility = manifest.compatibility;
        if (compatibility.gameBuilds != null && compatibility.gameBuilds.Length > 0 &&
            !ContainsOrdinalIgnoreCase(compatibility.gameBuilds, gameBuild))
        {
            log.LogWarning("Mod is not marked compatible with this game build and was disabled: " + manifest.id);
            return false;
        }

        if (!FlowService.TryLoadPackage(packageRoot, manifest, out flowContent, out List<string> flowErrors))
        {
            log.LogError("Invalid Mod flow content: " + manifestPath);
            foreach (string error in flowErrors)
            {
                log.LogError("  " + error);
            }

            return false;
        }

        return true;
    }

    internal static bool TryValidateAssetReference(
        string packageRoot,
        string relativePath,
        AssetKind kind,
        out string error)
    {
        ValidationContext context = new ValidationContext(packageRoot);
        context.ValidateAsset("resource", relativePath, kind);
        error = context.Errors.Count == 0
            ? null
            : context.Errors[0].Substring("resource: ".Length);
        return error == null;
    }

    internal static bool TryValidateRawJson(string json, out string error)
    {
        error = null;
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json ?? string.Empty);
            using XmlDictionaryReader reader = JsonReaderWriterFactory.CreateJsonReader(
                bytes,
                XmlDictionaryReaderQuotas.Max);
            if (!reader.Read() || reader.NodeType != XmlNodeType.Element ||
                !string.Equals(reader.GetAttribute("type"), "object", StringComparison.Ordinal))
            {
                error = "Manifest root must be a JSON object.";
                return false;
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            string section = null;
            while (reader.Read())
            {
                if (reader.Depth == 1 && reader.NodeType == XmlNodeType.EndElement)
                {
                    section = null;
                    continue;
                }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (reader.Depth == 1)
                {
                    string property = reader.LocalName;
                    if (!ManifestProperties.Contains(property))
                    {
                        error = "Unknown Manifest v2 property '" + property + "'. Content belongs in story flow files.";
                        return false;
                    }

                    if (!seen.Add(property))
                    {
                        error = "Duplicate Manifest property '" + property + "'.";
                        return false;
                    }

                    section = property;
                    continue;
                }

                if (!IsKnownNestedProperty(section, reader.Depth, reader.LocalName))
                {
                    error = "Unknown property '" + reader.LocalName + "' inside Manifest section '" + section + "'.";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = "Manifest JSON is invalid: " + ex.Message;
            return false;
        }
    }

    private static bool IsKnownNestedProperty(string section, int depth, string property)
    {
        if (depth == 2)
        {
            return section switch
            {
                "authors" => property == "item",
                "compatibility" => property == "loaderApi" || property == "gameBuilds",
                "defaults" => property == "enabled" || property == "priority",
                "settings" => property == "item",
                "dependencies" => property == "item",
                "conflicts" => property == "item",
                _ => false
            };
        }

        if (depth == 3)
        {
            return section switch
            {
                "compatibility" => property == "item",
                "settings" => property == "id" || property == "label" || property == "defaultValue",
                "dependencies" => property == "id" || property == "version",
                "conflicts" => property == "id" || property == "version",
                _ => false
            };
        }

        return false;
    }

    internal static bool TryValidatePackageId(string value, out string error)
    {
        error = null;
        if (string.IsNullOrEmpty(value))
        {
            error = "must not be empty.";
            return false;
        }

        if (value.Length > MaxPackageIdLength)
        {
            error = "must be at most " + MaxPackageIdLength + " ASCII characters.";
            return false;
        }

        if (value == "." || value == ".." || value.EndsWith(".", StringComparison.Ordinal) ||
            value.EndsWith(" ", StringComparison.Ordinal))
        {
            error = "must not be a dot path or end with a dot or space.";
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] > 0x7f)
            {
                error = "must contain ASCII characters only.";
                return false;
            }
        }

        string[] labels = value.Split('.');
        if (labels.Length < 2)
        {
            error = "must use a lowercase reverse-domain identifier containing at least one dot.";
            return false;
        }

        if (WindowsDeviceNames.Contains(labels[0]))
        {
            error = "must not use the reserved Windows device name '" + labels[0] + "'.";
            return false;
        }

        foreach (string label in labels)
        {
            if (label.Length == 0 || label.Length > 63)
            {
                error = "must contain non-empty domain labels of at most 63 characters.";
                return false;
            }

            if (!IsLowerAsciiLetterOrDigit(label[0]) || !IsLowerAsciiLetterOrDigit(label[label.Length - 1]))
            {
                error = "domain labels must start and end with a lowercase ASCII letter or digit.";
                return false;
            }

            for (int i = 1; i < label.Length - 1; i++)
            {
                char character = label[i];
                if (!IsLowerAsciiLetterOrDigit(character) && character != '-')
                {
                    error = "may contain only lowercase ASCII letters, digits, hyphens, and dots.";
                    return false;
                }
            }

        }

        return true;
    }

    internal static bool TryValidateSemVer(string value, out string error)
    {
        error = null;
        if (string.IsNullOrEmpty(value))
        {
            error = "must not be empty.";
            return false;
        }

        if (value.Length > MaxSemVerLength)
        {
            error = "must be at most " + MaxSemVerLength + " ASCII characters.";
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] > 0x7f)
            {
                error = "must contain ASCII characters only.";
                return false;
            }
        }

        int buildSeparator = value.IndexOf('+');
        if (buildSeparator >= 0 && value.IndexOf('+', buildSeparator + 1) >= 0)
        {
            error = "must contain at most one build metadata separator '+'.";
            return false;
        }

        string coreAndPrerelease = buildSeparator < 0 ? value : value.Substring(0, buildSeparator);
        string build = buildSeparator < 0 ? null : value.Substring(buildSeparator + 1);
        int prereleaseSeparator = coreAndPrerelease.IndexOf('-');
        string core = prereleaseSeparator < 0
            ? coreAndPrerelease
            : coreAndPrerelease.Substring(0, prereleaseSeparator);
        string prerelease = prereleaseSeparator < 0
            ? null
            : coreAndPrerelease.Substring(prereleaseSeparator + 1);

        string[] coreParts = core.Split('.');
        if (coreParts.Length != 3)
        {
            error = "must use SemVer core format MAJOR.MINOR.PATCH.";
            return false;
        }

        for (int i = 0; i < coreParts.Length; i++)
        {
            string partName = i == 0 ? "major" : i == 1 ? "minor" : "patch";
            if (!TryValidateSemVerNumber(coreParts[i], partName, out error))
            {
                return false;
            }
        }

        if (prerelease != null && !TryValidateSemVerIdentifiers(prerelease, true, out error))
        {
            return false;
        }

        if (build != null && !TryValidateSemVerIdentifiers(build, false, out error))
        {
            return false;
        }

        return true;
    }

    internal static bool TryValidateEntryId(string value, out string error)
    {
        error = null;
        if (string.IsNullOrEmpty(value) || value.Length > MaxEntryIdLength)
        {
            error = "must contain 1 to " + MaxEntryIdLength + " ASCII characters.";
            return false;
        }

        if (!IsAsciiAlphaNumeric(value[0]) || !IsAsciiAlphaNumeric(value[value.Length - 1]))
        {
            error = "must start and end with an ASCII letter or digit.";
            return false;
        }

        for (int i = 1; i < value.Length - 1; i++)
        {
            char character = value[i];
            if (!IsAsciiAlphaNumeric(character) && character != '-' && character != '_' && character != '.')
            {
                error = "may contain only ASCII letters, digits, hyphens, underscores, and dots.";
                return false;
            }
        }

        return true;
    }

    private static bool ContainsOrdinalIgnoreCase(string[] values, string expected)
    {
        foreach (string value in values)
        {
            if (string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class ValidationContext
    {
        private readonly string _packageRoot;

        internal ValidationContext(string packageRoot)
        {
            _packageRoot = packageRoot;
        }

        internal List<string> Errors { get; } = new List<string>();
        internal int SuppressedErrorCount { get; private set; }

        internal void Validate(ModManifest manifest, int loaderApiVersion)
        {
            if (manifest == null)
            {
                Error("root", "must be a JSON object.");
                return;
            }

            if (manifest.schemaVersion != 2)
            {
                Error("schemaVersion", "must be explicitly set to 2; missing or other schema versions are not supported.");
            }

            Require("id", manifest.id);
            if (!string.IsNullOrWhiteSpace(manifest.id))
            {
                if (!TryValidatePackageId(manifest.id, out string idError))
                {
                    Error("id", idError);
                }
            }

            Require("name", manifest.name);
            if (Require("version", manifest.version) && !TryValidateSemVer(manifest.version, out string versionError))
            {
                Error("version", versionError);
            }
            ValidateCompatibility(manifest.compatibility, loaderApiVersion);

            ValidateSettings(manifest.settings);
            ValidateRelationships(manifest);
        }

        private void ValidateRelationships(ModManifest manifest)
        {
            HashSet<string> dependencyIds = ValidateRelationshipList(
                "dependencies",
                manifest,
                manifest.dependencies,
                relationship => relationship.id,
                relationship => relationship.version);
            HashSet<string> conflictIds = ValidateRelationshipList(
                "conflicts",
                manifest,
                manifest.conflicts,
                relationship => relationship.id,
                relationship => relationship.version);
            foreach (string id in dependencyIds)
            {
                if (conflictIds.Contains(id))
                {
                    Error("relationships", "cannot both depend on and conflict with Mod '" + id + "'.");
                }
            }
        }

        private HashSet<string> ValidateRelationshipList<T>(
            string section,
            ModManifest manifest,
            T[] relationships,
            Func<T, string> getId,
            Func<T, string> getVersion)
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (relationships == null)
            {
                return ids;
            }

            if (relationships.Length > MaxModRelationships)
            {
                Error(section, "must contain at most " + MaxModRelationships + " entries.");
            }

            for (int i = 0; i < Math.Min(relationships.Length, MaxModRelationships); i++)
            {
                string path = section + "[" + i + "]";
                T relationship = relationships[i];
                if (relationship is null)
                {
                    Error(path, "must be an object.");
                    continue;
                }

                string relationshipId = getId(relationship);
                string relationshipVersion = getVersion(relationship);
                if (!Require(path + ".id", relationshipId))
                {
                    continue;
                }

                if (!TryValidatePackageId(relationshipId, out string idError))
                {
                    Error(path + ".id", idError);
                }
                else if (string.Equals(relationshipId, manifest.id, StringComparison.OrdinalIgnoreCase))
                {
                    Error(path + ".id", "must not refer to the same Mod.");
                }
                else if (!ids.Add(relationshipId))
                {
                    Error(path + ".id", "duplicates Mod '" + relationshipId + "'.");
                }

                if (relationshipVersion == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(relationshipVersion))
                {
                    Error(path + ".version", "must not be empty.");
                    continue;
                }

                if (!ModVersionRange.TryParse(relationshipVersion, out _, out string rangeError))
                {
                    Error(path + ".version", rangeError);
                }
            }

            return ids;
        }

        private void ValidateCompatibility(CompatibilityManifest compatibility, int loaderApiVersion)
        {
            if (compatibility == null)
            {
                Error("compatibility", "is required and must be an object.");
                return;
            }

            if (compatibility.loaderApi != loaderApiVersion)
            {
                Error("compatibility.loaderApi", "must equal the current loader API " + loaderApiVersion + ".");
            }

            if (compatibility.gameBuilds == null)
            {
                return;
            }

            HashSet<string> builds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < compatibility.gameBuilds.Length; i++)
            {
                string build = compatibility.gameBuilds[i];
                string path = "compatibility.gameBuilds[" + i + "]";
                if (!Require(path, build))
                {
                    continue;
                }

                if (!builds.Add(build))
                {
                    Error(path, "duplicates game build '" + build + "'.");
                }
            }
        }

        private HashSet<string> ValidateSettings(BoolSettingManifest[] settings)
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (settings == null)
            {
                return ids;
            }

            for (int i = 0; i < settings.Length; i++)
            {
                string path = "settings[" + i + "]";
                BoolSettingManifest setting = settings[i];
                if (setting == null)
                {
                    Error(path, "must be an object.");
                    continue;
                }

                ValidateEntryId(path, setting.id, ids);
            }

            return ids;
        }

        private void ValidateEntryId(string path, string id, HashSet<string> ids)
        {
            if (!Require(path + ".id", id))
            {
                return;
            }

            if (!TryValidateEntryId(id, out string error))
            {
                Error(path + ".id", error);
                return;
            }

            if (!ids.Add(id))
            {
                Error(path + ".id", "duplicates id '" + id + "' in the same section.");
            }
        }

        internal void ValidateAsset(string path, string relativePath, AssetKind kind)
        {
            try
            {
                string extension = Path.GetExtension(relativePath);
                if (!string.IsNullOrEmpty(extension))
                {
                    if (!IsAllowedExtension(extension, kind))
                    {
                        Error(path, "has unsupported " + kind + " extension '" + extension + "'.");
                        return;
                    }

                    if (!LoaderUtil.TryResolvePackageFile(_packageRoot, relativePath, out string fullPath))
                    {
                        Error(path, "does not resolve to a readable file inside the Mod package: " + relativePath);
                        return;
                    }

                    ValidateAssetFile(path, fullPath, kind);
                    return;
                }

                foreach (string candidateExtension in AssetPolicy.GetExtensions(kind))
                {
                    if (LoaderUtil.TryResolvePackageFile(
                            _packageRoot,
                            relativePath + candidateExtension,
                            out string fullPath))
                    {
                        ValidateAssetFile(path, fullPath, kind);
                        return;
                    }
                }

                Error(path, "does not resolve to a readable " + kind + " file inside the Mod package: " + relativePath);
            }
            catch (Exception ex)
            {
                Error(path, "is not a valid package resource path: " + ex.Message);
            }
        }

        private void ValidateAssetFile(string path, string fullPath, AssetKind kind)
        {
            FileInfo file = new FileInfo(fullPath);
            long maxBytes = kind switch
            {
                AssetKind.Text => MaxTextAssetBytes,
                AssetKind.Texture => MaxTextureAssetBytes,
                AssetKind.Audio => MaxAudioAssetBytes,
                AssetKind.Video => MaxVideoAssetBytes,
                AssetKind.AssetBundle => MaxAssetBundleBytes,
                _ => MaxVideoAssetBytes
            };
            if (file.Length <= 0 || file.Length > maxBytes)
            {
                Error(path, "file size must be between 1 byte and " + maxBytes + " bytes.");
                return;
            }

            if (kind == AssetKind.AssetBundle)
            {
                ValidateAssetBundleHeader(path, fullPath);
                return;
            }

            if (kind != AssetKind.Texture)
            {
                return;
            }

            if (!TryReadImageDimensions(fullPath, out int width, out int height, out string imageError))
            {
                Error(path, "image header is invalid: " + imageError);
                return;
            }

            if (width <= 0 || height <= 0 || width > MaxTextureDimension || height > MaxTextureDimension ||
                (long)width * height > MaxTexturePixels)
            {
                Error(path, "image dimensions " + width + "x" + height + " exceed the texture safety limit.");
            }
        }

        private void ValidateAssetBundleHeader(string path, string fullPath)
        {
            byte[] header = new byte[16];
            int read;
            using (FileStream stream = File.OpenRead(fullPath))
            {
                read = stream.Read(header, 0, header.Length);
            }

            string signature = Encoding.ASCII.GetString(header, 0, read);
            if (!signature.StartsWith("UnityFS", StringComparison.Ordinal) &&
                !signature.StartsWith("UnityWeb", StringComparison.Ordinal) &&
                !signature.StartsWith("UnityRaw", StringComparison.Ordinal) &&
                !signature.StartsWith("UnityArchive", StringComparison.Ordinal))
            {
                Error(path, "does not have a recognized Unity AssetBundle header.");
            }
        }

        private static bool TryReadImageDimensions(
            string fullPath,
            out int width,
            out int height,
            out string error)
        {
            string extension = Path.GetExtension(fullPath);
            if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
            {
                return TryReadPngDimensions(fullPath, out width, out height, out error);
            }

            if (string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                return TryReadJpegDimensions(fullPath, out width, out height, out error);
            }

            width = 0;
            height = 0;
            error = "unsupported texture format.";
            return false;
        }

        private static bool TryReadPngDimensions(
            string fullPath,
            out int width,
            out int height,
            out string error)
        {
            width = 0;
            height = 0;
            error = null;
            byte[] header = new byte[24];
            using (FileStream stream = File.OpenRead(fullPath))
            {
                if (!ReadFully(stream, header, header.Length))
                {
                    error = "PNG header is truncated.";
                    return false;
                }
            }

            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            for (int i = 0; i < signature.Length; i++)
            {
                if (header[i] != signature[i])
                {
                    error = "PNG signature is invalid.";
                    return false;
                }
            }

            if (header[12] != (byte)'I' || header[13] != (byte)'H' ||
                header[14] != (byte)'D' || header[15] != (byte)'R')
            {
                error = "PNG IHDR chunk is missing.";
                return false;
            }

            width = ReadBigEndianInt32(header, 16);
            height = ReadBigEndianInt32(header, 20);
            return true;
        }

        private static bool TryReadJpegDimensions(
            string fullPath,
            out int width,
            out int height,
            out string error)
        {
            width = 0;
            height = 0;
            error = null;
            using FileStream stream = File.OpenRead(fullPath);
            if (stream.ReadByte() != 0xff || stream.ReadByte() != 0xd8)
            {
                error = "JPEG SOI marker is missing.";
                return false;
            }

            long scanLimit = Math.Min(stream.Length, 1024L * 1024L);
            while (stream.Position < scanLimit)
            {
                int prefix;
                do
                {
                    prefix = stream.ReadByte();
                }
                while (prefix >= 0 && prefix != 0xff && stream.Position < scanLimit);

                if (prefix < 0)
                {
                    break;
                }

                int marker;
                do
                {
                    marker = stream.ReadByte();
                }
                while (marker == 0xff && stream.Position < scanLimit);

                if (marker < 0 || marker == 0xd9 || marker == 0xda)
                {
                    break;
                }

                if (marker == 0x00 || marker == 0x01 || marker >= 0xd0 && marker <= 0xd8)
                {
                    continue;
                }

                int segmentLength = ReadBigEndianUInt16(stream);
                if (segmentLength < 2)
                {
                    error = "JPEG segment length is invalid.";
                    return false;
                }

                if (IsJpegStartOfFrame(marker))
                {
                    if (segmentLength < 7 || stream.ReadByte() < 0)
                    {
                        error = "JPEG SOF segment is truncated.";
                        return false;
                    }

                    height = ReadBigEndianUInt16(stream);
                    width = ReadBigEndianUInt16(stream);
                    if (width > 0 && height > 0)
                    {
                        return true;
                    }

                    error = "JPEG dimensions must be positive.";
                    return false;
                }

                long next = stream.Position + segmentLength - 2L;
                if (next > stream.Length || next > scanLimit)
                {
                    break;
                }

                stream.Position = next;
            }

            error = "JPEG dimensions were not found in the first 1 MiB.";
            return false;
        }

        private static bool IsJpegStartOfFrame(int marker)
        {
            return marker >= 0xc0 && marker <= 0xcf &&
                   marker != 0xc4 && marker != 0xc8 && marker != 0xcc;
        }

        private static int ReadBigEndianUInt16(Stream stream)
        {
            int high = stream.ReadByte();
            int low = stream.ReadByte();
            if (high < 0 || low < 0)
            {
                throw new EndOfStreamException();
            }

            return (high << 8) | low;
        }

        private static bool ReadFully(Stream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0)
                {
                    return false;
                }

                offset += read;
            }

            return true;
        }

        private static int ReadBigEndianInt32(byte[] buffer, int offset)
        {
            return buffer[offset] << 24 |
                   buffer[offset + 1] << 16 |
                   buffer[offset + 2] << 8 |
                   buffer[offset + 3];
        }

        private static bool IsAllowedExtension(string extension, AssetKind kind)
        {
            foreach (string allowed in AssetPolicy.GetExtensions(kind))
            {
                if (string.Equals(extension, allowed, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private bool Require(string path, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            Error(path, "is required.");
            return false;
        }

        private void Error(string path, string message)
        {
            if (Errors.Count < MaxReportedErrors)
            {
                Errors.Add(path + ": " + message);
            }
            else
            {
                SuppressedErrorCount++;
            }
        }
    }

    private static bool TryValidateSemVerNumber(string value, string name, out string error)
    {
        error = null;
        if (string.IsNullOrEmpty(value))
        {
            error = "SemVer " + name + " must not be empty.";
            return false;
        }

        if (value.Length > 1 && value[0] == '0')
        {
            error = "SemVer " + name + " must not contain leading zeroes.";
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            if (!IsAsciiDigit(value[i]))
            {
                error = "SemVer " + name + " must contain decimal digits only.";
                return false;
            }
        }

        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            error = "SemVer " + name + " is too large.";
            return false;
        }

        return true;
    }

    private static bool TryValidateSemVerIdentifiers(string value, bool prerelease, out string error)
    {
        error = null;
        string section = prerelease ? "prerelease" : "build metadata";
        if (value.Length == 0)
        {
            error = "SemVer " + section + " must not be empty.";
            return false;
        }

        string[] identifiers = value.Split('.');
        foreach (string identifier in identifiers)
        {
            if (identifier.Length == 0)
            {
                error = "SemVer " + section + " must not contain empty identifiers.";
                return false;
            }

            if (identifier.Length > MaxSemVerIdentifierLength)
            {
                error = "SemVer " + section + " identifiers must be at most " +
                    MaxSemVerIdentifierLength + " characters.";
                return false;
            }

            bool numeric = true;
            for (int i = 0; i < identifier.Length; i++)
            {
                char character = identifier[i];
                if (!IsAsciiAlphaNumeric(character) && character != '-')
                {
                    error = "SemVer " + section + " identifiers may contain only ASCII letters, digits, and hyphens.";
                    return false;
                }

                numeric &= IsAsciiDigit(character);
            }

            if (prerelease && numeric)
            {
                if (identifier.Length > 1 && identifier[0] == '0')
                {
                    error = "Numeric SemVer prerelease identifiers must not contain leading zeroes.";
                    return false;
                }

                if (!ulong.TryParse(identifier, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    error = "Numeric SemVer prerelease identifier is too large.";
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsLowerAsciiLetterOrDigit(char value)
    {
        return (value >= 'a' && value <= 'z') || IsAsciiDigit(value);
    }

    private static bool IsAsciiAlphaNumeric(char value)
    {
        return (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') || IsAsciiDigit(value);
    }

    private static bool IsAsciiDigit(char value)
    {
        return value >= '0' && value <= '9';
    }
}
