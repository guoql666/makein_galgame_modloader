using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MakeineGalGameQM.Core.Character;
using MakeineGalGameQM.Core.Utils;
using RotaryHeart.Lib.SerializableDictionary;
using Spine;
using Spine.Unity;
using UnityEngine;

namespace SunnyModLoader;

internal static class SpineService
{
    private static readonly Dictionary<string, SpineDefinition> ActiveDefinitions =
        new Dictionary<string, SpineDefinition>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, SpineDefinition> KnownDefinitions =
        new Dictionary<string, SpineDefinition>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, RuntimeSpineRegistration> Registrations =
        new Dictionary<string, RuntimeSpineRegistration>(StringComparer.OrdinalIgnoreCase);
    private static readonly List<AssetBundle> LoadedBundles = new List<AssetBundle>();
    private static int _runtimeFailureCount;

    internal static IReadOnlyList<ModScanIssue> Rebuild(ModRegistry registry)
    {
        ReleaseActiveResources();
        List<ModScanIssue> issues = new List<ModScanIssue>();
        if (registry == null)
        {
            return issues;
        }

        Dictionary<string, AssetBundle> bundles =
            new Dictionary<string, AssetBundle>(StringComparer.OrdinalIgnoreCase);
        foreach (ModPackage package in registry.ActiveLowToHigh)
        {
            foreach (SpineDefinition definition in package.Spines ?? Array.Empty<SpineDefinition>())
            {
                if (definition == null || string.IsNullOrWhiteSpace(definition.internalName))
                {
                    continue;
                }

                ActiveDefinitions[definition.internalName] = definition;
                KnownDefinitions[definition.internalName] = definition;
                try
                {
                    RuntimeSpineRegistration registration =
                        CreateRegistration(registry, definition, bundles);
                    Registrations[definition.internalName] = registration;
                }
                catch (Exception ex)
                {
                    string message = "Spine '" + definition.id + "' could not be loaded: " + ex.Message;
                    SunnyModLoaderPlugin.Log.LogError(package.Id + ": " + message);
                    issues.Add(new ModScanIssue
                    {
                        RootPath = package.RootPath,
                        SourceDescription = package.SourceDescription,
                        Message = message,
                        IsRuntime = true
                    });
                }
            }
        }

        LoadedBundles.AddRange(bundles.Values.Where(bundle => bundle != null).Distinct());
        _runtimeFailureCount = issues.Count;
        SunnyModLoaderPlugin.Log.LogInfo(
            "Registered " + Registrations.Count + " external Spine character(s) from " +
            LoadedBundles.Count + " AssetBundle(s); " + issues.Count + " failed.");
        return issues;
    }

    internal static bool TryGetCharacterConfig(string characterName, out CharacterSpineConfig config)
    {
        config = null;
        if (string.IsNullOrWhiteSpace(characterName) ||
            !Registrations.TryGetValue(characterName, out RuntimeSpineRegistration registration))
        {
            return false;
        }

        config = registration.Config;
        return config != null && config.SpinePrefab != null;
    }

    internal static bool IsKnownCharacter(string name)
    {
        return !string.IsNullOrWhiteSpace(name) && KnownDefinitions.ContainsKey(name);
    }

    internal static void ApplyDialogueAlias(ICommandParameter[] parameters)
    {
        if (parameters == null)
        {
            return;
        }

        CommandParameter<string> idParameter = parameters
            .OfType<CommandParameter<string>>()
            .FirstOrDefault(parameter => string.Equals(parameter.Name, "id", StringComparison.OrdinalIgnoreCase));
        if (idParameter == null ||
            !KnownDefinitions.TryGetValue(idParameter.Value ?? string.Empty, out SpineDefinition definition))
        {
            return;
        }

        CommandParameter<string> specialName = parameters
            .OfType<CommandParameter<string>>()
            .FirstOrDefault(parameter =>
                string.Equals(parameter.Name, "specialname", StringComparison.OrdinalIgnoreCase));
        if (specialName != null && string.IsNullOrEmpty(specialName.Value))
        {
            specialName.Value = definition.displayName;
        }
    }

    internal static bool ValidateForDiagnostics(out string detail)
    {
        bool valid = Registrations.Count == ActiveDefinitions.Count && _runtimeFailureCount == 0;
        detail = "declared=" + ActiveDefinitions.Count +
                 ", registered=" + Registrations.Count +
                 ", bundles=" + LoadedBundles.Count +
                 ", failures=" + _runtimeFailureCount;
        return valid;
    }

    internal static bool ValidatePrefabForDiagnostics(GameObject prefab, out string detail)
    {
        try
        {
            ValidatePrefab(prefab, null);
            SkeletonGraphic graphic = prefab.GetComponent<SkeletonGraphic>();
            SkeletonData data = graphic.SkeletonDataAsset.GetSkeletonData(true);
            detail = "prefab=" + prefab.name +
                     ", skins=" + data.Skins.Count +
                     ", animations=" + data.Animations.Count;
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    internal static void Shutdown()
    {
        ReleaseActiveResources();
        KnownDefinitions.Clear();
    }

    private static RuntimeSpineRegistration CreateRegistration(
        ModRegistry registry,
        SpineDefinition definition,
        Dictionary<string, AssetBundle> bundles)
    {
        string bundleUri = SelectBundle(definition);
        if (string.IsNullOrWhiteSpace(bundleUri))
        {
            throw new InvalidOperationException(
                "no AssetBundle was declared for runtime platform " + Application.platform + ".");
        }
        if (!registry.TryResolveAsset(bundleUri, AssetKind.AssetBundle, out string fullPath))
        {
            throw new InvalidOperationException("could not resolve AssetBundle " + bundleUri + ".");
        }

        if (!bundles.TryGetValue(fullPath, out AssetBundle bundle))
        {
            bundle = AssetBundle.LoadFromFile(fullPath);
            if (bundle == null)
            {
                throw new InvalidDataException(
                    "Unity rejected the AssetBundle. It must target " + Application.platform +
                    " and the game's Unity version.");
            }
            bundles.Add(fullPath, bundle);
        }

        GameObject prefab = LoadPrefab(bundle, definition.prefab);
        ValidatePrefab(prefab, definition);

        CharacterSpineConfig config = ScriptableObject.CreateInstance<CharacterSpineConfig>();
        config.name = definition.internalName;
        config.CharacterName = definition.internalName;
        config.CharacterShortName = definition.internalName;
        config.SpinePrefab = prefab;
        config.EmotionConvertMap = new SerializableDictionaryBase<string, string>();
        foreach (KeyValuePair<string, string> emotion in definition.emotions)
        {
            config.EmotionConvertMap[emotion.Key] = emotion.Value;
        }
        config.DefaultSpineEmotionAnimation = definition.defaultEmotionAnimation ?? string.Empty;

        return new RuntimeSpineRegistration
        {
            Definition = definition,
            Config = config,
            Prefab = prefab
        };
    }

    private static string SelectBundle(SpineDefinition definition)
    {
        string platformBundle = Application.platform switch
        {
            RuntimePlatform.WindowsEditor => definition.windowsBundle,
            RuntimePlatform.WindowsPlayer => definition.windowsBundle,
            RuntimePlatform.OSXEditor => definition.macosBundle,
            RuntimePlatform.OSXPlayer => definition.macosBundle,
            RuntimePlatform.Android => definition.androidBundle,
            _ => null
        };
        return string.IsNullOrWhiteSpace(platformBundle) ? definition.bundle : platformBundle;
    }

    private static GameObject LoadPrefab(AssetBundle bundle, string requestedName)
    {
        GameObject prefab = bundle.LoadAsset<GameObject>(requestedName);
        if (prefab != null)
        {
            return prefab;
        }

        string normalized = requestedName.Replace('\\', '/').TrimStart('/');
        string suffix = "/" + normalized;
        string[] matches = bundle.GetAllAssetNames()
            .Where(name =>
                string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(name), normalized, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                matches.Length == 0
                    ? "prefab asset was not found in the AssetBundle: " + requestedName
                    : "prefab asset name is ambiguous in the AssetBundle: " + requestedName);
        }

        prefab = bundle.LoadAsset<GameObject>(matches[0]);
        return prefab != null
            ? prefab
            : throw new InvalidDataException("AssetBundle entry is not a GameObject Prefab: " + matches[0]);
    }

    private static void ValidatePrefab(GameObject prefab, SpineDefinition definition)
    {
        if (prefab == null)
        {
            throw new InvalidDataException("Spine Prefab is null.");
        }

        Component[] components = prefab.GetComponentsInChildren<Component>(true);
        if (components.Any(component => component == null))
        {
            throw new InvalidDataException("Spine Prefab contains a Missing Script component.");
        }
        foreach (MonoBehaviour behaviour in components.OfType<MonoBehaviour>())
        {
            Type type = behaviour.GetType();
            Assembly assembly = type.Assembly;
            if (assembly == typeof(SkeletonGraphic).Assembly ||
                typeof(SpineCharacterPhotoMark).IsAssignableFrom(type))
            {
                continue;
            }

            throw new InvalidDataException(
                "Spine Prefab contains unsupported MonoBehaviour " + type.FullName +
                "; only the bundled Spine runtime and SpineCharacterPhotoMark are allowed.");
        }

        SkeletonGraphic graphic = prefab.GetComponent<SkeletonGraphic>();
        if (graphic == null)
        {
            throw new InvalidDataException(
                "Spine Prefab root must contain Spine.Unity.SkeletonGraphic.");
        }
        if (graphic.SkeletonDataAsset == null)
        {
            throw new InvalidDataException("SkeletonGraphic has no SkeletonDataAsset.");
        }

        SkeletonData skeletonData;
        try
        {
            skeletonData = graphic.SkeletonDataAsset.GetSkeletonData(true);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("SkeletonDataAsset could not be initialized.", ex);
        }
        if (skeletonData == null)
        {
            throw new InvalidDataException("SkeletonDataAsset returned no skeleton data.");
        }
        if (skeletonData.Animations == null || skeletonData.Animations.Count == 0)
        {
            throw new InvalidDataException("Spine skeleton contains no animations.");
        }

        if (definition == null)
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(definition.defaultEmotionAnimation) &&
            skeletonData.FindAnimation(definition.defaultEmotionAnimation) == null)
        {
            throw new InvalidDataException(
                "defaultEmotionAnimation was not found in the skeleton: " +
                definition.defaultEmotionAnimation);
        }
        foreach (KeyValuePair<string, string> emotion in definition.emotions)
        {
            if (skeletonData.FindAnimation(emotion.Value) == null)
            {
                throw new InvalidDataException(
                    "emotion '" + emotion.Key + "' references missing animation '" + emotion.Value + "'.");
            }
        }
    }

    private static void ReleaseActiveResources()
    {
        foreach (RuntimeSpineRegistration registration in Registrations.Values)
        {
            if (registration.Config != null)
            {
                UnityEngine.Object.Destroy(registration.Config);
            }
        }
        Registrations.Clear();
        ActiveDefinitions.Clear();

        foreach (AssetBundle bundle in LoadedBundles)
        {
            if (bundle != null)
            {
                bundle.Unload(true);
            }
        }
        LoadedBundles.Clear();
        _runtimeFailureCount = 0;
    }

    private sealed class RuntimeSpineRegistration
    {
        internal SpineDefinition Definition;
        internal CharacterSpineConfig Config;
        internal GameObject Prefab;
    }
}
