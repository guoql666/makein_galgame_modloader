using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MakeineGalGameQM.Core.Character;
using MakeineGalGameQM.Core.Utils;
using RotaryHeart.Lib.SerializableDictionary;
using UnityEngine;
using UnityEngine.UI;

namespace SunnyModLoader;

internal static class SpriteService
{
    private static readonly Dictionary<string, RuntimeSpriteRegistration> Registrations =
        new Dictionary<string, RuntimeSpriteRegistration>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, SpriteDefinition> ActiveDefinitions =
        new Dictionary<string, SpriteDefinition>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, SpriteDefinition> KnownDefinitions =
        new Dictionary<string, SpriteDefinition>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<GameObject, SpriteDefinition> LiveRenderers =
        new Dictionary<GameObject, SpriteDefinition>();
    private static readonly HashSet<string> RegisteredNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    internal static void Rebuild(ModRegistry registry)
    {
        if (registry == null)
        {
            return;
        }

        if (CharacterIllustrationManager.IllustrationDic != null)
        {
            foreach (string name in RegisteredNames)
            {
                CharacterIllustrationManager.IllustrationDic.Remove(name);
            }
        }
        RegisteredNames.Clear();
        ActiveDefinitions.Clear();

        foreach (ModPackage package in registry.ActiveLowToHigh)
        {
            foreach (SpriteDefinition definition in package.Sprites)
            {
                ActiveDefinitions[definition.internalName] = definition;
                KnownDefinitions[definition.internalName] = definition;
            }
        }

        SunnyModLoaderPlugin.Log.LogInfo("Discovered " + ActiveDefinitions.Count + " external character sprite definition(s).");
    }

    internal static bool TryGetCharacterConfig(string characterName, out CharacterConfig config)
    {
        config = null;
        if (!ActiveDefinitions.TryGetValue(characterName ?? string.Empty, out SpriteDefinition definition))
        {
            return false;
        }

        try
        {
            if (CharacterIllustrationManager.IllustrationDic == null)
            {
                CharacterIllustrationManager.Init();
            }

            string signature = GetSignature(definition);
            if (!Registrations.TryGetValue(definition.internalName, out RuntimeSpriteRegistration registration) ||
                !string.Equals(registration.Signature, signature, StringComparison.Ordinal))
            {
                registration = CreateRegistration(definition);
                Registrations[definition.internalName] = registration;
            }

            CharacterIllustrationManager.IllustrationDic[definition.internalName] = registration.Config;
            RegisteredNames.Add(definition.internalName);
            config = registration.Config;
            return true;
        }
        catch (Exception ex)
        {
            SunnyModLoaderPlugin.Log.LogError(
                "Failed to create external sprite " + definition.id + ": " + ex);
            return false;
        }
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
        if (idParameter == null || !KnownDefinitions.TryGetValue(idParameter.Value ?? string.Empty, out SpriteDefinition definition))
        {
            return;
        }

        CommandParameter<string> specialName = parameters
            .OfType<CommandParameter<string>>()
            .FirstOrDefault(parameter => string.Equals(parameter.Name, "specialname", StringComparison.OrdinalIgnoreCase));
        if (specialName != null && string.IsNullOrEmpty(specialName.Value))
        {
            specialName.Value = definition.displayName;
        }
    }

    internal static bool IsKnownCharacter(string name)
    {
        return !string.IsNullOrWhiteSpace(name) && KnownDefinitions.ContainsKey(name);
    }

    internal static void ConfigureRenderer(CharacterBase character, ICharacterRenderer renderer)
    {
        if (character == null || !(renderer is CharacterImage image) ||
            !ActiveDefinitions.TryGetValue(character.Name ?? string.Empty, out SpriteDefinition definition))
        {
            return;
        }

        RectTransform root = image.GetComponent<RectTransform>();
        Sprite baseSprite = image.illustrationImage != null ? image.illustrationImage.sprite : null;
        float width = definition.width > 0f
            ? definition.width
            : baseSprite != null ? baseSprite.rect.width : 400f;
        float height = definition.height > 0f
            ? definition.height
            : baseSprite != null ? baseSprite.rect.height : 700f;
        if (root != null)
        {
            root.sizeDelta = new Vector2(width, height);
            root.pivot = new Vector2(definition.pivotX, definition.pivotY);
        }

        ConfigureImage(image.illustrationImage);
        ConfigureImage(image.emotionImage);
        CanvasGroup canvasGroup = image.GetComponent<CanvasGroup>() ?? image.gameObject.AddComponent<CanvasGroup>();
        canvasGroup.blocksRaycasts = false;
        canvasGroup.interactable = false;
        LiveRenderers[image.gameObject] = definition;
        ReorderExternalRenderers(image.transform.parent);
    }

    internal static void Shutdown()
    {
        if (CharacterIllustrationManager.IllustrationDic != null)
        {
            foreach (string name in RegisteredNames)
            {
                CharacterIllustrationManager.IllustrationDic.Remove(name);
            }
        }

        foreach (RuntimeSpriteRegistration registration in Registrations.Values.Distinct())
        {
            foreach (Sprite sprite in registration.Sprites)
            {
                if (sprite != null)
                {
                    UnityEngine.Object.Destroy(sprite);
                }
            }
            foreach (Texture2D texture in registration.Textures)
            {
                if (texture != null)
                {
                    UnityEngine.Object.Destroy(texture);
                }
            }
            if (registration.Config != null)
            {
                UnityEngine.Object.Destroy(registration.Config);
            }
        }

        Registrations.Clear();
        ActiveDefinitions.Clear();
        KnownDefinitions.Clear();
        LiveRenderers.Clear();
        RegisteredNames.Clear();
    }

    private static RuntimeSpriteRegistration CreateRegistration(SpriteDefinition definition)
    {
        RuntimeSpriteRegistration registration = new RuntimeSpriteRegistration
        {
            Signature = GetSignature(definition),
            Config = ScriptableObject.CreateInstance<CharacterConfig>()
        };
        registration.Config.name = definition.internalName;
        registration.Config.CharacterName = definition.internalName;
        registration.Config.DefaultBaseName = definition.defaultBase;
        registration.Config.DefaultEmotionName = definition.defaultEmotion ?? string.Empty;
        registration.Config.RectSize = definition.portraitSize;
        registration.Config.offset = new Vector2(definition.portraitOffsetX, definition.portraitOffsetY);
        registration.Config.BaseMap = new SerializableDictionaryBase<string, Sprite>();
        registration.Config.EmotionMap = new SerializableDictionaryBase<string, Sprite>();

        Dictionary<string, Sprite> loadedBySource = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        foreach (SpriteImageDefinition image in definition.bases)
        {
            registration.Config.BaseMap[image.id] = LoadSprite(definition, image.source, loadedBySource, registration);
        }
        foreach (SpriteImageDefinition image in definition.emotions)
        {
            registration.Config.EmotionMap[image.id] = LoadSprite(definition, image.source, loadedBySource, registration);
        }
        return registration;
    }

    private static Sprite LoadSprite(
        SpriteDefinition definition,
        string source,
        Dictionary<string, Sprite> loadedBySource,
        RuntimeSpriteRegistration registration)
    {
        if (loadedBySource.TryGetValue(source, out Sprite existing))
        {
            return existing;
        }

        if (SunnyModLoaderPlugin.Registry == null ||
            !SunnyModLoaderPlugin.Registry.TryResolveAsset(source, AssetKind.Texture, out string fullPath))
        {
            throw new InvalidOperationException("Could not resolve " + source);
        }
        Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!texture.LoadImage(File.ReadAllBytes(fullPath)))
        {
            UnityEngine.Object.Destroy(texture);
            throw new InvalidOperationException("Unity could not decode " + source);
        }
        texture.name = Path.GetFileNameWithoutExtension(fullPath);
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;
        Sprite sprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, texture.width, texture.height),
            new Vector2(definition.pivotX, definition.pivotY),
            100f);
        sprite.name = definition.internalName + "_" + loadedBySource.Count.ToString(CultureInfo.InvariantCulture);
        registration.Textures.Add(texture);
        registration.Sprites.Add(sprite);
        loadedBySource[source] = sprite;
        return sprite;
    }

    private static void ConfigureImage(Image image)
    {
        if (image == null)
        {
            return;
        }
        image.raycastTarget = false;
        image.preserveAspect = true;
        RectTransform rect = image.rectTransform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private static void ReorderExternalRenderers(Transform parent)
    {
        if (parent == null)
        {
            return;
        }

        List<KeyValuePair<GameObject, SpriteDefinition>> live = LiveRenderers
            .Where(pair => pair.Key != null && pair.Key.transform.parent == parent)
            .OrderBy(pair => pair.Value.order)
            .ThenBy(pair => pair.Value.internalName, StringComparer.Ordinal)
            .ToList();
        foreach (KeyValuePair<GameObject, SpriteDefinition> pair in live.Where(
                     pair => string.Equals(pair.Value.layer, "back", StringComparison.OrdinalIgnoreCase)))
        {
            pair.Key.transform.SetAsFirstSibling();
        }
        foreach (KeyValuePair<GameObject, SpriteDefinition> pair in live.Where(
                     pair => string.Equals(pair.Value.layer, "front", StringComparison.OrdinalIgnoreCase)))
        {
            pair.Key.transform.SetAsLastSibling();
        }
    }

    private static string GetSignature(SpriteDefinition definition)
    {
        return string.Join("|", new[]
        {
            definition.internalName,
            definition.defaultBase,
            definition.defaultEmotion,
            definition.width.ToString(CultureInfo.InvariantCulture),
            definition.height.ToString(CultureInfo.InvariantCulture),
            definition.pivotX.ToString(CultureInfo.InvariantCulture),
            definition.pivotY.ToString(CultureInfo.InvariantCulture),
            definition.portraitSize.ToString(CultureInfo.InvariantCulture),
            string.Join(";", definition.bases.Select(image => image.id + "=" + image.source)),
            string.Join(";", definition.emotions.Select(image => image.id + "=" + image.source))
        });
    }

    private sealed class RuntimeSpriteRegistration
    {
        internal string Signature;
        internal CharacterConfig Config;
        internal readonly List<Texture2D> Textures = new List<Texture2D>();
        internal readonly List<Sprite> Sprites = new List<Sprite>();
    }
}
