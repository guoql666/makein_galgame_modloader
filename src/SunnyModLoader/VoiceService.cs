using System;
using System.Reflection;
using HarmonyLib;
using MakeineGalGameQM.Core;
using MakeineGalGameQM.Core.Utils;
using UnityEngine;
using UnityEngine.Audio;

namespace SunnyModLoader;

internal sealed class VoiceService
{
    private const string MasterMixerGroupName = "Master";
    private static readonly FieldInfo PreferenceMixerField =
        AccessTools.Field(typeof(UserPreferenceApplier), "mixer");

    private readonly SunnyModLoaderPlugin _host;
    private readonly AudioSource _source;
    private int _generation;
    private AudioClip _ownedClip;
    private float _lineVolume = 1f;
    private bool _mixerBindingWarningLogged;

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
            TryBindOriginalMasterMixer();
            _source.volume = Mathf.Clamp01(SunnyModAudioControl.VoiceVolume) * _lineVolume;
        }
    }

    internal bool TryBindOriginalMasterMixer(UserPreferenceApplier preferenceApplier = null)
    {
        if (_source == null)
        {
            return false;
        }
        if (_source.outputAudioMixerGroup != null &&
            string.Equals(
                _source.outputAudioMixerGroup.name,
                MasterMixerGroupName,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        preferenceApplier ??= FindPreferenceApplier();
        if (preferenceApplier == null)
        {
            return false;
        }

        AudioMixer mixer = PreferenceMixerField?.GetValue(preferenceApplier) as AudioMixer;
        AudioMixerGroup master = FindMasterGroup(mixer);
        if (master == null)
        {
            if (!_mixerBindingWarningLogged)
            {
                _mixerBindingWarningLogged = true;
                SunnyModLoaderPlugin.Log.LogWarning(
                    "Voice playback could not bind to the original Master AudioMixer group; " +
                    "the game master-volume setting will not affect voice playback.");
            }
            return false;
        }

        _source.outputAudioMixerGroup = master;
        _mixerBindingWarningLogged = false;
        SunnyModLoaderPlugin.Log.LogInfo(
            "Voice playback uses the original AudioMixer group: " + master.name + ".");
        return true;
    }

    internal bool ValidateMasterMixerForDiagnostics(out string detail)
    {
        bool bound = TryBindOriginalMasterMixer();
        AudioMixerGroup group = _source?.outputAudioMixerGroup;
        detail = group == null ? "unbound" : group.name;
        return bound && group != null &&
               string.Equals(group.name, MasterMixerGroupName, StringComparison.OrdinalIgnoreCase);
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

    private static UserPreferenceApplier FindPreferenceApplier()
    {
        foreach (UserPreferenceApplier candidate in Resources.FindObjectsOfTypeAll<UserPreferenceApplier>())
        {
            if (candidate != null)
            {
                return candidate;
            }
        }
        return null;
    }

    private static AudioMixerGroup FindMasterGroup(AudioMixer mixer)
    {
        if (mixer == null)
        {
            return null;
        }

        AudioMixerGroup[] groups = mixer.FindMatchingGroups(MasterMixerGroupName);
        if (groups == null)
        {
            return null;
        }
        foreach (AudioMixerGroup group in groups)
        {
            if (group != null &&
                string.Equals(group.name, MasterMixerGroupName, StringComparison.OrdinalIgnoreCase))
            {
                return group;
            }
        }
        return null;
    }
}
