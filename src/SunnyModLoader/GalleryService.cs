using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SunnyModLoader;

internal static class GalleryService
{
    private static readonly HashSet<string> ManagedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly FieldInfo GalleryConfigField = AccessTools.Field(typeof(CGPanel), "galleryConfig");

    internal static void Register(CGPanel panel)
    {
        if (panel == null || GalleryConfigField == null)
        {
            return;
        }

        CGGalleryConfig config = GalleryConfigField.GetValue(panel) as CGGalleryConfig;
        if (config == null)
        {
            return;
        }

        config.Groups ??= new List<CGGalleryGroup>();
        HashSet<string> desiredKeys = GetDesiredKeys();
        for (int i = config.Groups.Count - 1; i >= 0; i--)
        {
            CGGalleryGroup group = config.Groups[i];
            if (group != null && ManagedKeys.Contains(group.GroupKey) && !desiredKeys.Contains(group.GroupKey))
            {
                config.Groups.RemoveAt(i);
                ManagedKeys.Remove(group.GroupKey);
            }
        }

        foreach (ModPackage package in SunnyModLoaderPlugin.Registry.ActiveLowToHigh)
        {
            GalleryDefinition[] entries = package.Gallery;
            if (entries == null)
            {
                continue;
            }

            foreach (GalleryDefinition entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.id) || string.IsNullOrWhiteSpace(entry.image))
                {
                    continue;
                }

                string key = package.Id + ":" + entry.id;
                if (ContainsGroup(config, key))
                {
                    ManagedKeys.Add(key);
                    continue;
                }

                Sprite fullImage = LoadSprite(package, entry.image);
                if (fullImage == null)
                {
                    SunnyModLoaderPlugin.Log.LogError("Gallery image is missing: " + key);
                    continue;
                }

                Sprite thumbnail = string.IsNullOrWhiteSpace(entry.thumbnail)
                    ? fullImage
                    : LoadSprite(package, entry.thumbnail) ?? fullImage;

                config.Groups.Add(new CGGalleryGroup
                {
                    GroupKey = key,
                    GroupName = string.IsNullOrWhiteSpace(entry.title) ? entry.id : entry.title,
                    Thumbnail = thumbnail,
                    Entries = new List<CGGalleryVariant>
                    {
                        new CGGalleryVariant
                        {
                            Key = key,
                            DisplayName = string.IsNullOrWhiteSpace(entry.title) ? entry.id : entry.title,
                            Thumbnail = thumbnail,
                            FullImage = fullImage
                        }
                    }
                });
                ManagedKeys.Add(key);

                if (entry.unlockedByDefault)
                {
                    CGGalleryUnlockStore.Unlock(key);
                }
            }
        }
    }

    private static HashSet<string> GetDesiredKeys()
    {
        HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ModPackage package in SunnyModLoaderPlugin.Registry.ActiveLowToHigh)
        {
            GalleryDefinition[] entries = package.Gallery;
            if (entries == null)
            {
                continue;
            }

            foreach (GalleryDefinition entry in entries)
            {
                if (entry != null && !string.IsNullOrWhiteSpace(entry.id) && !string.IsNullOrWhiteSpace(entry.image))
                {
                    result.Add(package.Id + ":" + entry.id);
                }
            }
        }

        return result;
    }

    private static bool ContainsGroup(CGGalleryConfig config, string key)
    {
        foreach (CGGalleryGroup group in config.Groups)
        {
            if (group != null && string.Equals(group.GroupKey, key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static Sprite LoadSprite(ModPackage package, string relativePath)
    {
        string uri = LoaderUtil.ToModUri(package.Id, relativePath);
        Texture2D texture = GalAssetLoader.LoadTexture(uri);
        if (texture == null)
        {
            return null;
        }

        return Sprite.Create(
            texture,
            new Rect(0f, 0f, texture.width, texture.height),
            new Vector2(0.5f, 0.5f),
            100f);
    }
}

[HarmonyPatch]
internal static class GalleryInitializePatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(CGPanel), "EnsureInitialized");
    }

    private static void Postfix(CGPanel __instance)
    {
        GalleryService.Register(__instance);
    }
}
