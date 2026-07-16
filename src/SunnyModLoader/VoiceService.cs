using MakeineGalGameQM.Core;
using MakeineGalGameQM.Core.Utils;
using UnityEngine;

namespace SunnyModLoader;

internal sealed class VoiceService
{
    private readonly SunnyModLoaderPlugin _host;
    private readonly AudioSource _source;
    private int _generation;
    private AudioClip _ownedClip;
    private float _lineVolume = 1f;

    internal VoiceService(SunnyModLoaderPlugin host)
    {
        _host = host;
        _source = host.gameObject.AddComponent<AudioSource>();
        _source.playOnAwake = false;
        _source.loop = false;
        _source.spatialBlend = 0f;
        _source.ignoreListenerPause = true;
        RefreshControlSettings();
    }

    internal void HandleDialogue(ICommandParameter[] parameters, CorePlayer player)
    {
        string path = LoaderUtil.GetStringParameter(parameters, "audiopath");
        if (string.IsNullOrWhiteSpace(path))
        {
            if (SunnyModAudioControl.StopVoiceOnAdvance)
            {
                Stop();
            }
            return;
        }

        Stop();
        if (player != null && player.IsFastMode)
        {
            return;
        }

        Play(path, false);
    }

    internal void Replay(string path)
    {
        if (!SunnyModAudioControl.IsAvailable || string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        Stop();
        Play(path, true);
    }

    private void Play(string path, bool backlogReplay)
    {
        bool ownsClip = SunnyModLoaderPlugin.Registry.TryResolveAsset(path, AssetKind.Audio, out _);
        int token = ++_generation;
        _lineVolume = SunnyModLoaderPlugin.Registry.TryGetVoiceVolume(path, out float configured)
            ? Mathf.Clamp01(configured)
            : 1f;
        _host.StartCoroutine(GalAssetLoader.LoadAudioClipAsync(path, clip =>
        {
            if (token != _generation || clip == null)
            {
                if (clip != null && ownsClip)
                {
                    UnityEngine.Object.Destroy(clip);
                }
                return;
            }

            ReleaseOwnedClip();
            _ownedClip = ownsClip ? clip : null;
            _source.clip = clip;
            RefreshControlSettings();
            _source.Play();
            if (backlogReplay && StartupDiagnostics.IsRequested)
            {
                SunnyModLoaderPlugin.Log.LogInfo(
                    "Startup validation started backlog voice replay " + path + " (" +
                    clip.samples + " samples at " + clip.frequency + " Hz).");
            }
        }));
    }

    internal void RefreshControlSettings()
    {
        if (_source != null)
        {
            _source.volume = Mathf.Clamp01(SunnyModAudioControl.VoiceVolume) * _lineVolume;
        }
    }

    internal void Stop()
    {
        _generation++;
        if (_source != null)
        {
            _source.Stop();
            _source.clip = null;
        }
        _lineVolume = 1f;
        ReleaseOwnedClip();
    }

    private void ReleaseOwnedClip()
    {
        if (_ownedClip != null)
        {
            UnityEngine.Object.Destroy(_ownedClip);
            _ownedClip = null;
        }
    }
}
