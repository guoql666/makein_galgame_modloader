using System;
using System.Collections;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace SunnyModLoader;

[HarmonyPatch(typeof(GalAssetLoader), nameof(GalAssetLoader.LoadText))]
internal static class LoadTextPatch
{
    private static bool Prefix(string path, ref string __result)
    {
        if (!SunnyModLoaderPlugin.Registry.TryResolveAsset(path, AssetKind.Text, out string fullPath))
        {
            return true;
        }

        try
        {
            __result = SunnyModLoaderPlugin.Registry.TryGetFlow(path, out FlowDefinition flow)
                ? flow.PreparedText
                : File.ReadAllText(fullPath);
            SunnyModLoaderPlugin.Log.LogDebug("Loaded external text: " + path + " -> " + fullPath);
            return false;
        }
        catch (Exception ex)
        {
            SunnyModLoaderPlugin.Log.LogError("Failed to load external text " + fullPath + ": " + ex);
            return true;
        }
    }
}

[HarmonyPatch(typeof(GalAssetLoader), nameof(GalAssetLoader.LoadTexture))]
internal static class LoadTexturePatch
{
    private static bool Prefix(string path, ref Texture2D __result)
    {
        if (!SunnyModLoaderPlugin.Registry.TryResolveAsset(path, AssetKind.Texture, out string fullPath))
        {
            return true;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(fullPath);
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(bytes))
            {
                UnityEngine.Object.Destroy(texture);
                SunnyModLoaderPlugin.Log.LogError("Unity rejected external image: " + fullPath);
                return true;
            }

            texture.name = Path.GetFileNameWithoutExtension(fullPath);
            __result = texture;
            SunnyModLoaderPlugin.Log.LogDebug("Loaded external texture: " + path + " -> " + fullPath);
            return false;
        }
        catch (Exception ex)
        {
            SunnyModLoaderPlugin.Log.LogError("Failed to load external texture " + fullPath + ": " + ex);
            return true;
        }
    }
}

[HarmonyPatch(typeof(GalAssetLoader), nameof(GalAssetLoader.LoadAudioClipWithLoopMetadataAsync))]
internal static class LoadAudioPatch
{
    private static bool Prefix(
        string path,
        Action<GalAssetLoader.AudioClipLoadResult> onComplete,
        AudioType? overrideType,
        ref IEnumerator __result)
    {
        if (!SunnyModLoaderPlugin.Registry.TryResolveAsset(path, AssetKind.Audio, out string fullPath))
        {
            return true;
        }

        __result = LoadExternal(path, fullPath, onComplete, overrideType);
        return false;
    }

    private static IEnumerator LoadExternal(
        string logicalPath,
        string fullPath,
        Action<GalAssetLoader.AudioClipLoadResult> onComplete,
        AudioType? overrideType)
    {
        AudioLoopMetadata metadata = ReadLoopMetadata(fullPath);
        AudioType audioType = overrideType ?? DetectAudioType(fullPath);
        string url = new Uri(fullPath).AbsoluteUri;

        using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(url, audioType))
        {
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                SunnyModLoaderPlugin.Log.LogError("Failed to load external audio " + fullPath + ": " + request.error);
                onComplete?.Invoke(new GalAssetLoader.AudioClipLoadResult
                {
                    Clip = null,
                    LoopMetadata = AudioLoopMetadata.None,
                    SourcePath = logicalPath
                });
                yield break;
            }

            AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
            if (clip != null)
            {
                clip.name = Path.GetFileNameWithoutExtension(fullPath);
            }

            onComplete?.Invoke(new GalAssetLoader.AudioClipLoadResult
            {
                Clip = clip,
                LoopMetadata = metadata,
                SourcePath = logicalPath
            });
        }
    }

    private static AudioLoopMetadata ReadLoopMetadata(string audioPath)
    {
        string sidecar = Path.Combine(
            Path.GetDirectoryName(audioPath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(audioPath) + "_loopmeta.json");
        if (!File.Exists(sidecar))
        {
            return AudioLoopMetadata.None;
        }

        try
        {
            LoopMetadataPayload payload = JsonUtility.FromJson<LoopMetadataPayload>(File.ReadAllText(sidecar));
            return payload == null
                ? AudioLoopMetadata.None
                : new AudioLoopMetadata(payload.loopStartSample, payload.loopEndSample);
        }
        catch (Exception ex)
        {
            SunnyModLoaderPlugin.Log.LogWarning("Invalid loop metadata " + sidecar + ": " + ex.Message);
            return AudioLoopMetadata.None;
        }
    }

    private static AudioType DetectAudioType(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".mp3": return AudioType.MPEG;
            case ".wav": return AudioType.WAV;
            case ".ogg": return AudioType.OGGVORBIS;
            case ".aif":
            case ".aiff": return AudioType.AIFF;
            case ".mod": return AudioType.MOD;
            default: return AudioType.UNKNOWN;
        }
    }

    [Serializable]
    private sealed class LoopMetadataPayload
    {
        public int loopStartSample = 0;
        public int loopEndSample = 0;
    }
}

[HarmonyPatch(typeof(GalAssetLoader), nameof(GalAssetLoader.GetVideoUrl))]
internal static class GetVideoUrlPatch
{
    private static bool Prefix(string path, ref string __result)
    {
        if (!SunnyModLoaderPlugin.Registry.TryResolveAsset(path, AssetKind.Video, out string fullPath))
        {
            return true;
        }

        __result = new Uri(fullPath).AbsoluteUri;
        return false;
    }
}
