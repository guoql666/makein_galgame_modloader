using System;

namespace SunnyModLoader;

internal enum AssetKind
{
    Any,
    Text,
    Texture,
    Audio,
    Video,
    AssetBundle
}

internal static class AssetPolicy
{
    private static readonly string[] TextExtensions = { ".txt", ".sunny", ".json", ".csv", ".md" };
    private static readonly string[] TextureExtensions = { ".png", ".jpg", ".jpeg" };
    private static readonly string[] AudioExtensions = { ".ogg", ".wav", ".mp3", ".aif", ".aiff", ".mod" };
    private static readonly string[] VideoExtensions = { ".mp4", ".webm", ".mov" };
    private static readonly string[] AssetBundleExtensions = { ".bundle", ".assetbundle", ".unity3d" };

    internal static string[] GetExtensions(AssetKind kind)
    {
        return kind switch
        {
            AssetKind.Text => TextExtensions,
            AssetKind.Texture => TextureExtensions,
            AssetKind.Audio => AudioExtensions,
            AssetKind.Video => VideoExtensions,
            AssetKind.AssetBundle => AssetBundleExtensions,
            _ => Array.Empty<string>()
        };
    }
}
