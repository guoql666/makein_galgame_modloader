using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UserPreferenceVal;

namespace SunnyModLoader;

internal sealed class AudioControlDefinition
{
    internal string providerModId;
    internal string id;
    internal string volumePageName = "系统设置";
    internal string stopPageName = "文本设置";
    internal string volumeLabel = "语音音量";
    internal string stopLabel = "换句停止语音";
    internal string offText = "关闭";
    internal string onText = "开启";
    internal float defaultVolume = 1f;
    internal bool defaultStopOnAdvance;
}

public static class SunnyModAudioControl
{
    public const float DefaultVoiceVolume = 1f;
    public const bool DefaultStopVoiceOnAdvance = true;

    public static bool IsAvailable => AudioControlService.IsAvailable;
    public static string ProviderModId => AudioControlService.ProviderModId;
    public static float VoiceVolume => AudioControlService.VoiceVolume;
    public static bool StopVoiceOnAdvance => AudioControlService.StopVoiceOnAdvance;

    public static event Action SettingsChanged;

    internal static void NotifySettingsChanged()
    {
        Delegate[] handlers = SettingsChanged?.GetInvocationList();
        if (handlers == null)
        {
            return;
        }
        foreach (Action handler in handlers.Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                SunnyModLoaderPlugin.Log?.LogWarning("Audio control subscriber failed: " + ex.Message);
            }
        }
    }
}

internal static class AudioControlService
{
    internal const string VolumePreferenceKey = "SunnyModVoiceVolume";
    internal const string StopPreferenceKey = "SunnyModStopVoiceOnAdvance";

    private static readonly FieldInfo DefaultValuesField =
        AccessTools.Field(typeof(UserPreferenceManager), "DefaultValueDic");
    private static readonly FieldInfo PageButtonsField = AccessTools.Field(typeof(PageMgr), "buttons");
    private static readonly FieldInfo PageTargetField = AccessTools.Field(typeof(PageButton), "target");
    private static readonly FieldInfo ToggleOptionsField = AccessTools.Field(typeof(SettingToggle), "toggleOptions");
    private static readonly FieldInfo SliderControlField = AccessTools.Field(typeof(SettingSlider), "slider");
    private static readonly Dictionary<int, SettingsUiBinding> UiBindings =
        new Dictionary<int, SettingsUiBinding>();

    private static AudioControlDefinition _active;
    private static SliderVal _volumeValue;
    private static ToggleVal _stopValue;
    private static DiagnosticDisplaySnapshot _diagnosticDisplaySnapshot;

    internal static bool IsAvailable => _active != null;
    internal static bool HasInjectedSettingsUiForDiagnostics => UiBindings.Values.Any(binding =>
        binding?.Slider != null || binding?.Toggle != null);
    internal static string ProviderModId => _active?.providerModId ?? string.Empty;
    internal static float VoiceVolume => IsAvailable
        ? Mathf.Clamp01(_volumeValue?.Val ?? _active.defaultVolume)
        : SunnyModAudioControl.DefaultVoiceVolume;
    internal static bool StopVoiceOnAdvance => IsAvailable
        ? (_stopValue?.Val ?? (_active.defaultStopOnAdvance ? 1 : 0)) != 0
        : SunnyModAudioControl.DefaultStopVoiceOnAdvance;

    internal static AudioControlDefinition CreateBuiltInDefinition(string providerModId)
    {
        return new AudioControlDefinition
        {
            providerModId = providerModId,
            id = "voice-control",
            volumePageName = "系统设置",
            stopPageName = "文本设置",
            volumeLabel = "语音音量",
            stopLabel = "换句停止语音",
            offText = "关闭",
            onText = "开启",
            defaultVolume = 1f,
            defaultStopOnAdvance = false
        };
    }

    internal static void Rebuild(ModRegistry registry)
    {
        AudioControlDefinition next = null;
        if (registry != null &&
            registry.TryGetPackage(ModRegistry.BuiltInVoiceControlId, out ModPackage builtIn) &&
            builtIn.IsBuiltIn && builtIn.RuntimeEnabled)
        {
            next = builtIn.AudioControl;
        }
        if (!DefinitionsEquivalent(_active, next))
        {
            ResetInjectedUi();
        }
        _active = next;

        if (_active != null)
        {
            EnsurePreferences(UserPreferenceManager.Instance);
            EnsureSettingsUi(SettingManager.Instance, false);
        }
        BacklogVoiceReplayService.RefreshAllVisible();
        NotifyChanged();
    }

    private static bool DefinitionsEquivalent(AudioControlDefinition left, AudioControlDefinition right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left == null || right == null)
        {
            return false;
        }
        return string.Equals(left.providerModId, right.providerModId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.id, right.id, StringComparison.Ordinal) &&
               string.Equals(left.volumePageName, right.volumePageName, StringComparison.Ordinal) &&
               string.Equals(left.stopPageName, right.stopPageName, StringComparison.Ordinal) &&
               string.Equals(left.volumeLabel, right.volumeLabel, StringComparison.Ordinal) &&
               string.Equals(left.stopLabel, right.stopLabel, StringComparison.Ordinal) &&
               string.Equals(left.offText, right.offText, StringComparison.Ordinal) &&
               string.Equals(left.onText, right.onText, StringComparison.Ordinal) &&
               Mathf.Approximately(left.defaultVolume, right.defaultVolume) &&
               left.defaultStopOnAdvance == right.defaultStopOnAdvance;
    }

    internal static void EnsurePreferences(UserPreferenceManager manager)
    {
        if (_active == null || manager == null)
        {
            return;
        }

        SliderVal volume = manager.Datas?.OfType<SliderVal>()
            .FirstOrDefault(value => string.Equals(value.keyID, VolumePreferenceKey, StringComparison.Ordinal));
        if (volume == null)
        {
            volume = new SliderVal
            {
                keyID = VolumePreferenceKey,
                Min = 0f,
                Max = 1f,
                Val = _active.defaultVolume
            };
            manager.Datas ??= new List<Val>();
            manager.Datas.Add(volume);
        }

        ToggleVal stop = manager.Datas?.OfType<ToggleVal>()
            .FirstOrDefault(value => string.Equals(value.keyID, StopPreferenceKey, StringComparison.Ordinal));
        if (stop == null)
        {
            stop = new ToggleVal
            {
                keyID = StopPreferenceKey,
                Val = _active.defaultStopOnAdvance ? 1 : 0
            };
            manager.Datas ??= new List<Val>();
            manager.Datas.Add(stop);
        }

        BindValueCallbacks(volume, stop);
        if (manager.DataReferenceDic != null)
        {
            AddRuntimePreference(manager, volume, _active.defaultVolume);
            AddRuntimePreference(manager, stop, _active.defaultStopOnAdvance ? 1 : 0);
        }
        _volumeValue = volume;
        _stopValue = stop;
    }

    internal static void BindPreferences(UserPreferenceManager manager)
    {
        if (_active == null || manager?.DataReferenceDic == null)
        {
            return;
        }
        if (manager.DataReferenceDic.TryGetValue(VolumePreferenceKey, out Val volume) && volume is SliderVal slider)
        {
            _volumeValue = slider;
        }
        if (manager.DataReferenceDic.TryGetValue(StopPreferenceKey, out Val stop) && stop is ToggleVal toggle)
        {
            _stopValue = toggle;
        }
        BindValueCallbacks(_volumeValue, _stopValue);
        NotifyChanged();
    }

    internal static void EnsureSettingsUi(SettingManager manager, bool beforeStart)
    {
        if (_active == null || manager == null || manager.config?.pages == null || manager.pageMgr == null)
        {
            return;
        }

        int volumePageIndex = FindAudioPage(manager.config.pages, _active.volumePageName);
        int stopPageIndex = FindPage(manager.config.pages, _active.stopPageName);
        if (volumePageIndex < 0 || stopPageIndex < 0)
        {
            SunnyModLoaderPlugin.Log.LogError(
                "Audio control could not find the requested volume or text settings page.");
            return;
        }

        SettingPageConfig volumePage = manager.config.pages[volumePageIndex];
        SettingPageConfig stopPage = manager.config.pages[stopPageIndex];
        SliderSettingConfig sliderConfig = volumePage.elements.OfType<SliderSettingConfig>()
            .FirstOrDefault(config => string.Equals(config.KeyID, VolumePreferenceKey, StringComparison.Ordinal));
        if (sliderConfig == null)
        {
            sliderConfig = new SliderSettingConfig { KeyID = VolumePreferenceKey };
            volumePage.elements.Add(sliderConfig);
        }
        sliderConfig.LabelName = _active.volumeLabel;

        ToggleSettingConfig toggleConfig = stopPage.elements.OfType<ToggleSettingConfig>()
            .FirstOrDefault(config => string.Equals(config.KeyID, StopPreferenceKey, StringComparison.Ordinal));
        if (toggleConfig == null)
        {
            toggleConfig = new ToggleSettingConfig { KeyID = StopPreferenceKey };
            stopPage.elements.Add(toggleConfig);
        }
        toggleConfig.LabelName = _active.stopLabel;
        toggleConfig.options = new List<ToggleOption>
        {
            new ToggleOption { OptionName = _active.offText },
            new ToggleOption { OptionName = _active.onText }
        };

        EnsurePreferences(manager.userMgr ?? UserPreferenceManager.Instance);
        SettingsUiBinding binding = GetOrCreateBinding(
            manager,
            volumePage,
            volumePageIndex,
            stopPage,
            stopPageIndex,
            sliderConfig,
            toggleConfig);
        EnsureLayoutWatcher(manager);
        if (beforeStart)
        {
            binding.ExpectPageManagerBinding = true;
        }
        bool initialized = PageButtonsField?.GetValue(manager.pageMgr) != null;
        if (manager.pageMgr.useManualPageHierarchy)
        {
            Canvas.ForceUpdateCanvases();
            EnsureManualSliderUi(manager.pageMgr, binding, initialized && !beforeStart);
            EnsureManualToggleUi(manager.pageMgr, binding, initialized && !beforeStart);
            Canvas.ForceUpdateCanvases();
        }
        else if (initialized && !beforeStart)
        {
            CaptureOrCreateGeneratedUi(manager.pageMgr, binding);
        }
        binding.SetVisible(true);
    }

    internal static void BindSettingsUiAfterStart(SettingManager manager)
    {
        if (_active == null || manager == null)
        {
            return;
        }
        if (UiBindings.TryGetValue(manager.GetInstanceID(), out SettingsUiBinding existing) &&
            existing.ExpectPageManagerBinding)
        {
            if (!manager.pageMgr.useManualPageHierarchy)
            {
                CaptureGeneratedUi(manager.pageMgr, existing);
            }
            existing.SliderBound = existing.Slider != null;
            existing.ToggleBound = existing.Toggle != null;
            existing.ExpectPageManagerBinding = false;
        }
        EnsureSettingsUi(manager, false);
        if (!UiBindings.TryGetValue(manager.GetInstanceID(), out SettingsUiBinding binding))
        {
            return;
        }
        BindUiElements(binding);
        SunnyModLoaderPlugin.Log.LogInfo(
            "Audio control added voice volume to system settings and advance-stop to text settings.");
    }

    internal static bool ValidateForDiagnostics(out string detail)
    {
        detail = null;
        if (!IsAvailable)
        {
            detail = "audio control provider is not enabled";
            return true;
        }
        bool dataReady = _volumeValue != null && _stopValue != null &&
                         _volumeValue.Min == 0f && _volumeValue.Max == 1f;
        int optionCount = -1;
        bool layoutStable = true;
        string layoutDetail = "no-setting-manager";
        bool uiReady = SettingManager.Instance == null;
        if (SettingManager.Instance != null &&
            UiBindings.TryGetValue(SettingManager.Instance.GetInstanceID(), out SettingsUiBinding binding))
        {
            if (binding.Toggle != null)
            {
                optionCount =
                    (ToggleOptionsField?.GetValue(binding.Toggle) as ICollection<ToggleOptionObject>)?.Count ?? -1;
            }
            int sliderConfigs = binding.VolumePage?.elements?.Count(element =>
                element is SliderSettingConfig &&
                string.Equals(element.KeyID, VolumePreferenceKey, StringComparison.Ordinal)) ?? 0;
            int toggleConfigs = binding.StopPage?.elements?.Count(element =>
                element is ToggleSettingConfig &&
                string.Equals(element.KeyID, StopPreferenceKey, StringComparison.Ordinal)) ?? 0;
            Vector2 sliderPosition = (binding.Slider?.transform as RectTransform)?.anchoredPosition ?? Vector2.zero;
            Vector2 togglePosition = (binding.Toggle?.transform as RectTransform)?.anchoredPosition ?? Vector2.zero;
            EnsureSettingsUi(SettingManager.Instance, false);
            layoutStable = Vector2.Distance(
                               sliderPosition,
                               (binding.Slider?.transform as RectTransform)?.anchoredPosition ?? Vector2.zero) < 0.01f &&
                           Vector2.Distance(
                               togglePosition,
                               (binding.Toggle?.transform as RectTransform)?.anchoredPosition ?? Vector2.zero) < 0.01f;
            bool placementReady = ValidatePagePlacement(SettingManager.Instance.pageMgr, binding);
            uiReady = binding.Slider != null && binding.Toggle != null &&
                      sliderConfigs == 1 && toggleConfigs == 1 && optionCount == 2 &&
                      placementReady && layoutStable;
            layoutDetail = DescribeLayout(SettingManager.Instance.pageMgr, binding);
        }
        detail = "provider=" + ProviderModId + ", volume=" + VoiceVolume.ToString("0.###") +
                 ", stopOnAdvance=" + StopVoiceOnAdvance + ", data=" + dataReady +
                 ", ui=" + uiReady + ", options=" + optionCount +
                 ", layoutStable=" + layoutStable + ", " + layoutDetail;
        return dataReady && uiReady;
    }

    internal static bool TryOpenSettingsForDiagnostics(string page)
    {
        SettingManager manager = SettingManager.Instance;
        if (manager == null || !UiBindings.TryGetValue(manager.GetInstanceID(), out SettingsUiBinding binding))
        {
            return false;
        }
        bool stopPage = string.Equals(page, "text", StringComparison.OrdinalIgnoreCase);
        if (!stopPage && !string.Equals(page, "system", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        manager.Open();
        manager.pageMgr.Focus(stopPage ? binding.StopPageIndex : binding.VolumePageIndex);
        return true;
    }

    internal static bool TryApplyResolutionForDiagnostics(int index)
    {
        UserPreferenceManager manager = UserPreferenceManager.Instance;
        if (manager?.DataReferenceDic == null || index < 0 || index > 14)
        {
            return false;
        }
        manager.DataReferenceDic.TryGetValue("ScreenMode", out Val mode);
        if (!manager.DataReferenceDic.TryGetValue("ScreenResolution", out Val resolution) ||
            !TryReadIntValue(resolution, out int originalResolution))
        {
            return false;
        }
        int originalMode = 0;
        bool hasMode = mode != null && TryReadIntValue(mode, out originalMode);
        _diagnosticDisplaySnapshot ??= new DiagnosticDisplaySnapshot(
            mode,
            originalMode,
            hasMode,
            resolution,
            originalResolution,
            Screen.width,
            Screen.height,
            Screen.fullScreenMode,
            PlayerPrefs.HasKey("ScreenMode"),
            PlayerPrefs.GetInt("ScreenMode", originalMode),
            PlayerPrefs.HasKey("ScreenResolution"),
            PlayerPrefs.GetInt("ScreenResolution", originalResolution));

        if (hasMode && !TryWriteIntValue(mode, 0))
        {
            return false;
        }
        return TryWriteIntValue(resolution, index);
    }

    internal static bool RestoreDisplayPreferencesForDiagnostics()
    {
        DiagnosticDisplaySnapshot snapshot = _diagnosticDisplaySnapshot;
        _diagnosticDisplaySnapshot = null;
        if (snapshot == null)
        {
            return false;
        }

        bool restored = TryWriteIntValue(snapshot.Resolution, snapshot.ResolutionValue);
        if (snapshot.HasMode)
        {
            restored = TryWriteIntValue(snapshot.Mode, snapshot.ModeValue) && restored;
        }
        Screen.SetResolution(snapshot.ScreenWidth, snapshot.ScreenHeight, snapshot.FullScreenMode);
        RestorePlayerPreference("ScreenMode", snapshot.HadModePreference, snapshot.ModePreferenceValue);
        RestorePlayerPreference(
            "ScreenResolution",
            snapshot.HadResolutionPreference,
            snapshot.ResolutionPreferenceValue);
        PlayerPrefs.Save();
        return restored;
    }

    private static void RestorePlayerPreference(string key, bool existed, int value)
    {
        if (existed)
        {
            PlayerPrefs.SetInt(key, value);
        }
        else
        {
            PlayerPrefs.DeleteKey(key);
        }
    }

    private static bool TryReadIntValue(Val value, out int result)
    {
        switch (value)
        {
            case ToggleVal toggle:
                result = toggle.Val;
                return true;
            case DropDownVal dropDown:
                result = dropDown.Val;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryWriteIntValue(Val value, int result)
    {
        switch (value)
        {
            case ToggleVal toggle:
                toggle.Val = result;
                return true;
            case DropDownVal dropDown:
                dropDown.Val = result;
                return true;
            default:
                return false;
        }
    }

    private static string DescribeLayout(PageMgr pageMgr, SettingsUiBinding binding)
    {
        UnityEngine.UI.CanvasScaler scaler = binding.Manager?.transform.root
            .GetComponentInChildren<UnityEngine.UI.CanvasScaler>(true);
        string scalerDetail = scaler == null
            ? "none"
            : scaler.uiScaleMode + "/" + scaler.referenceResolution.ToString("F0") +
              "/match=" + scaler.matchWidthOrHeight.ToString("0.##") +
              "/screen=" + Screen.width + "x" + Screen.height;
        return "manual=" + (pageMgr?.useManualPageHierarchy == true) +
               ", scaler=" + scalerDetail +
               ", volumePage=" + binding.VolumePage?.PageName +
               ", stopPage=" + binding.StopPage?.PageName +
               ", placement=" + ValidatePagePlacement(pageMgr, binding) +
               ", slider=" + DescribeElement(binding.Slider) +
               ", toggle=" + DescribeElement(binding.Toggle);
    }

    private static bool ValidatePagePlacement(PageMgr pageMgr, SettingsUiBinding binding)
    {
        if (pageMgr?.useManualPageHierarchy != true)
        {
            return true;
        }
        if (pageMgr.manualPages == null ||
            binding.VolumePageIndex < 0 || binding.VolumePageIndex >= pageMgr.manualPages.Count ||
            binding.StopPageIndex < 0 || binding.StopPageIndex >= pageMgr.manualPages.Count)
        {
            return false;
        }
        RectTransform volumeRoot = pageMgr.manualPages[binding.VolumePageIndex]?.pageRoot?.transform as RectTransform;
        RectTransform stopRoot = pageMgr.manualPages[binding.StopPageIndex]?.pageRoot?.transform as RectTransform;
        return volumeRoot != null && stopRoot != null &&
               binding.Slider != null && binding.Slider.transform.IsChildOf(volumeRoot) &&
               binding.Toggle != null && binding.Toggle.transform.IsChildOf(stopRoot) &&
               IsWithinRoot(volumeRoot, binding.Slider.transform as RectTransform) &&
               IsWithinRoot(stopRoot, binding.Toggle.transform as RectTransform) &&
               IsWithinClippingAncestors(volumeRoot, binding.Slider.transform as RectTransform) &&
               IsWithinClippingAncestors(stopRoot, binding.Toggle.transform as RectTransform) &&
               binding.ValidateManualVolumeLayout(
                   volumeRoot,
                   binding.Slider.transform as RectTransform) &&
               HasNoElementOverlap(volumeRoot, binding.Slider) &&
               HasNoElementOverlap(stopRoot, binding.Toggle);
    }

    private static bool IsWithinRoot(RectTransform root, RectTransform element)
    {
        if (root == null || element == null)
        {
            return false;
        }
        Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(root, element);
        Rect rect = root.rect;
        const float tolerance = 1f;
        return bounds.min.x >= rect.xMin - tolerance && bounds.max.x <= rect.xMax + tolerance &&
               bounds.min.y >= rect.yMin - tolerance && bounds.max.y <= rect.yMax + tolerance;
    }

    private static bool IsWithinClippingAncestors(RectTransform root, RectTransform element)
    {
        for (Transform current = element?.parent; current != null; current = current.parent)
        {
            if (current is RectTransform rect &&
                (current.GetComponent<UnityEngine.UI.Mask>() != null ||
                 current.GetComponent<UnityEngine.UI.RectMask2D>() != null) &&
                !IsWithinRoot(rect, element))
            {
                return false;
            }
            if (current == root)
            {
                break;
            }
        }
        return true;
    }

    private static bool HasNoElementOverlap(RectTransform root, SettingUIElement element)
    {
        RectTransform elementRect = element?.transform as RectTransform;
        Transform parent = elementRect?.parent;
        if (root == null || elementRect == null || parent == null)
        {
            return false;
        }

        Bounds elementBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(root, elementRect);
        foreach (SettingUIElement other in parent.GetComponentsInChildren<SettingUIElement>(true))
        {
            if (other == null || other == element || other.transform.parent != parent ||
                other.transform is not RectTransform otherRect)
            {
                continue;
            }
            Bounds otherBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(root, otherRect);
            float overlapX = Mathf.Min(elementBounds.max.x, otherBounds.max.x) -
                             Mathf.Max(elementBounds.min.x, otherBounds.min.x);
            float overlapY = Mathf.Min(elementBounds.max.y, otherBounds.max.y) -
                             Mathf.Max(elementBounds.min.y, otherBounds.min.y);
            if (overlapX > 1f && overlapY > 1f)
            {
                return false;
            }
        }
        return true;
    }

    private static string DescribeElement(SettingUIElement element)
    {
        if (element == null)
        {
            return "null";
        }
        RectTransform rect = element.transform as RectTransform;
        string position = rect == null
            ? "n/a"
            : rect.anchoredPosition.ToString("F1") + "/" + rect.rect.size.ToString("F1");
        return element.name + "@" + GetTransformPath(element.transform.parent) +
               "[active=" + element.gameObject.activeInHierarchy + ", rect=" + position + "]";
    }

    private static string GetTransformPath(Transform transform)
    {
        if (transform == null)
        {
            return "<root>";
        }
        Stack<string> names = new Stack<string>();
        while (transform != null)
        {
            names.Push(transform.name);
            transform = transform.parent;
        }
        return string.Join("/", names);
    }

    private static void AddRuntimePreference(UserPreferenceManager manager, SliderVal value, float defaultValue)
    {
        if (!manager.DataReferenceDic.ContainsKey(value.keyID))
        {
            value.Val = PlayerPrefs.HasKey(value.keyID)
                ? Mathf.Clamp(PlayerPrefs.GetFloat(value.keyID), value.Min, value.Max)
                : defaultValue;
            manager.DataReferenceDic[value.keyID] = value;
        }
        if (DefaultValuesField?.GetValue(manager) is Dictionary<string, Val> defaults &&
            !defaults.ContainsKey(value.keyID))
        {
            defaults[value.keyID] = new SliderVal
            {
                keyID = value.keyID,
                Min = value.Min,
                Max = value.Max,
                Val = defaultValue
            };
        }
    }

    private static void AddRuntimePreference(UserPreferenceManager manager, ToggleVal value, int defaultValue)
    {
        if (!manager.DataReferenceDic.ContainsKey(value.keyID))
        {
            value.Val = PlayerPrefs.HasKey(value.keyID) ? PlayerPrefs.GetInt(value.keyID) : defaultValue;
            manager.DataReferenceDic[value.keyID] = value;
        }
        if (DefaultValuesField?.GetValue(manager) is Dictionary<string, Val> defaults &&
            !defaults.ContainsKey(value.keyID))
        {
            defaults[value.keyID] = new ToggleVal { keyID = value.keyID, Val = defaultValue };
        }
    }

    private static void BindValueCallbacks(SliderVal volume, ToggleVal stop)
    {
        if (volume != null)
        {
            volume.OnValueChange = _ => NotifyChanged();
        }
        if (stop != null)
        {
            stop.OnValueChange = _ => NotifyChanged();
        }
    }

    private static void NotifyChanged()
    {
        SunnyModLoaderPlugin.Voice?.RefreshControlSettings();
        SunnyModAudioControl.NotifySettingsChanged();
    }

    private static int FindAudioPage(IReadOnlyList<SettingPageConfig> pages, string requestedName)
    {
        for (int index = 0; index < pages.Count; index++)
        {
            SettingPageConfig page = pages[index];
            if (page?.elements != null && page.elements.Any(element =>
                    string.Equals(element?.KeyID, "AudioVolume", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(element?.KeyID, "MusicVolume", StringComparison.OrdinalIgnoreCase)))
            {
                return index;
            }
        }
        return FindPage(pages, requestedName);
    }

    private static int FindPage(IReadOnlyList<SettingPageConfig> pages, string requestedName)
    {
        for (int index = 0; index < pages.Count; index++)
        {
            if (string.Equals(pages[index]?.PageName, requestedName, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        return -1;
    }

    private static SettingsUiBinding GetOrCreateBinding(
        SettingManager manager,
        SettingPageConfig volumePage,
        int volumePageIndex,
        SettingPageConfig stopPage,
        int stopPageIndex,
        SliderSettingConfig sliderConfig,
        ToggleSettingConfig toggleConfig)
    {
        RemoveDeadBindings();
        int id = manager.GetInstanceID();
        if (!UiBindings.TryGetValue(id, out SettingsUiBinding binding) || binding.Manager != manager)
        {
            binding = new SettingsUiBinding { Manager = manager };
            UiBindings[id] = binding;
        }
        binding.VolumePage = volumePage;
        binding.VolumePageIndex = volumePageIndex;
        binding.StopPage = stopPage;
        binding.StopPageIndex = stopPageIndex;
        binding.SliderConfig = sliderConfig;
        binding.ToggleConfig = toggleConfig;
        return binding;
    }

    private static void EnsureLayoutWatcher(SettingManager manager)
    {
        AudioControlSettingsLayoutWatcher watcher =
            manager.GetComponent<AudioControlSettingsLayoutWatcher>() ??
            manager.gameObject.AddComponent<AudioControlSettingsLayoutWatcher>();
        watcher.Bind(manager);
    }

    private static void RemoveDeadBindings()
    {
        int[] dead = UiBindings
            .Where(pair => pair.Value?.Manager == null)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (int key in dead)
        {
            UiBindings.Remove(key);
        }
    }

    internal static void RemoveBinding(SettingManager manager)
    {
        if (manager != null)
        {
            UiBindings.Remove(manager.GetInstanceID());
        }
        else
        {
            RemoveDeadBindings();
        }
    }

    internal static void RefreshSettingsUiLayout(SettingManager manager)
    {
        if (_active == null || manager == null)
        {
            return;
        }
        Canvas.ForceUpdateCanvases();
        EnsureSettingsUi(manager, false);
        Canvas.ForceUpdateCanvases();
    }

    private static void EnsureManualSliderUi(PageMgr pageMgr, SettingsUiBinding binding, bool bindNow)
    {
        if (pageMgr.manualPages == null || binding.VolumePageIndex >= pageMgr.manualPages.Count)
        {
            return;
        }
        ManualSettingPageSlot slot = pageMgr.manualPages[binding.VolumePageIndex];
        if (slot?.pageRoot == null)
        {
            return;
        }
        SettingSlider musicSlider = FindManualElement<SettingSlider>(slot, binding.VolumePage, "MusicVolume");
        Transform parent = musicSlider?.transform.parent ?? slot.pageRoot.transform;
        if (binding.Slider == null)
        {
            binding.ResetSliderUi();
            binding.Slider = CreateUiElement<SettingSlider>(pageMgr.SliderElementPrefab, parent);
            AddManualElement(slot, binding.Slider);
        }
        PositionManualSlider(parent, slot, binding);
        if (bindNow)
        {
            BindUiElements(binding);
        }
    }

    private static void EnsureManualToggleUi(PageMgr pageMgr, SettingsUiBinding binding, bool bindNow)
    {
        if (pageMgr.manualPages == null || binding.StopPageIndex >= pageMgr.manualPages.Count)
        {
            return;
        }
        ManualSettingPageSlot slot = pageMgr.manualPages[binding.StopPageIndex];
        if (slot?.pageRoot == null)
        {
            return;
        }
        SettingToggle textClickToggle = FindManualElement<SettingToggle>(slot, binding.StopPage, "OnTextClick");
        Transform parent = textClickToggle?.transform.parent ?? slot.pageRoot.transform;
        if (binding.Toggle == null)
        {
            binding.ResetToggleUi();
            binding.Toggle = CreateUiElement<SettingToggle>(pageMgr.ToggleElementPrefab, parent);
            AddManualElement(slot, binding.Toggle);
        }
        PositionManualToggle(parent, slot, binding);
        if (bindNow)
        {
            BindUiElements(binding);
        }
    }

    private static IEnumerable<SettingUIElement> GetManualElements(ManualSettingPageSlot slot)
    {
        if (slot?.elements != null && slot.elements.Count > 0)
        {
            return slot.elements;
        }
        return slot?.pageRoot?.GetComponentsInChildren<SettingUIElement>(true) ??
               Array.Empty<SettingUIElement>();
    }

    private static T FindManualElement<T>(
        ManualSettingPageSlot slot,
        SettingPageConfig page,
        string keyId) where T : SettingUIElement
    {
        if (slot == null || page?.elements == null || string.IsNullOrWhiteSpace(keyId))
        {
            return null;
        }
        int index = page.elements.FindIndex(config =>
            string.Equals(config?.KeyID, keyId, StringComparison.Ordinal));
        SettingUIElement[] elements = GetManualElements(slot).ToArray();
        return index >= 0 && index < elements.Length ? elements[index] as T : null;
    }

    private static void PositionManualSlider(
        Transform parent,
        ManualSettingPageSlot slot,
        SettingsUiBinding binding)
    {
        if (parent == null || parent.GetComponent<UnityEngine.UI.LayoutGroup>() != null)
        {
            return;
        }

        SettingSlider[] sliderTemplates =
        {
            FindManualElement<SettingSlider>(slot, binding.VolumePage, "MainVolume"),
            FindManualElement<SettingSlider>(slot, binding.VolumePage, "AudioVolume"),
            FindManualElement<SettingSlider>(slot, binding.VolumePage, "MusicVolume")
        };
        RectTransform sliderRect = binding.Slider?.transform as RectTransform;
        if (sliderTemplates.Any(slider => slider == null || slider.transform.parent != parent) || sliderRect == null)
        {
            return;
        }

        RectTransform volumeHeading = parent.Cast<Transform>()
            .FirstOrDefault(child => string.Equals(child.name, "Label_Audio", StringComparison.Ordinal)) as RectTransform;
        RectTransform preferenceHeading = parent.Cast<Transform>()
            .FirstOrDefault(child => string.Equals(child.name, "Label_Audio (1)", StringComparison.Ordinal)) as RectTransform;
        RectTransform preferenceToggle = FindManualElement<SettingToggle>(
            slot,
            binding.VolumePage,
            "OnRightClick")?.transform as RectTransform;
        if (volumeHeading == null || preferenceHeading == null || preferenceToggle == null ||
            preferenceToggle.parent != parent)
        {
            return;
        }

        binding.RestoreManualVolumeLayout();
        RectTransform[] originalSliders = sliderTemplates
            .Select(slider => (RectTransform)slider.transform)
            .ToArray();
        RectTransform sliderReference = originalSliders[originalSliders.Length - 1];
        CopyRectTransformLayout(sliderReference, sliderRect);

        float firstStep = originalSliders[0].anchoredPosition.y - originalSliders[1].anchoredPosition.y;
        float secondStep = originalSliders[1].anchoredPosition.y - originalSliders[2].anchoredPosition.y;
        float step = Mathf.Max(1f, (firstStep + secondStep) * 0.5f);
        float halfStep = step * 0.5f;
        Vector2 sliderPosition = sliderRect.anchoredPosition;
        sliderPosition.y = sliderReference.anchoredPosition.y - halfStep;
        sliderRect.anchoredPosition = sliderPosition;

        binding.ApplyBalancedVolumeLayout(
            originalSliders,
            volumeHeading,
            preferenceHeading,
            preferenceToggle,
            halfStep);
    }

    private static void PositionManualToggle(
        Transform parent,
        ManualSettingPageSlot slot,
        SettingsUiBinding binding)
    {
        if (parent == null || parent.GetComponent<UnityEngine.UI.LayoutGroup>() != null)
        {
            return;
        }
        SettingToggle previous = FindManualElement<SettingToggle>(slot, binding.StopPage, "FastMode");
        SettingToggle template = FindManualElement<SettingToggle>(slot, binding.StopPage, "OnTextClick");
        RectTransform toggleRect = binding.Toggle?.transform as RectTransform;
        if (previous == null || template == null || toggleRect == null ||
            previous.transform.parent != parent || template.transform.parent != parent)
        {
            return;
        }
        RectTransform previousRect = (RectTransform)previous.transform;
        RectTransform templateRect = (RectTransform)template.transform;
        CopyRectTransformLayout(templateRect, toggleRect);
        float rowHeight = Mathf.Max(1f, previousRect.anchoredPosition.y - templateRect.anchoredPosition.y);
        Vector2 togglePosition = toggleRect.anchoredPosition;
        togglePosition.y = templateRect.anchoredPosition.y - rowHeight;
        toggleRect.anchoredPosition = togglePosition;
    }

    private static void CopyRectTransformLayout(RectTransform source, RectTransform target)
    {
        target.anchorMin = source.anchorMin;
        target.anchorMax = source.anchorMax;
        target.pivot = source.pivot;
        target.sizeDelta = source.sizeDelta;
        target.anchoredPosition = source.anchoredPosition;
        target.localRotation = source.localRotation;
        target.localScale = source.localScale;
    }

    private static void CaptureOrCreateGeneratedUi(PageMgr pageMgr, SettingsUiBinding binding)
    {
        GameObject volumeTarget = CaptureGeneratedElement(
            pageMgr,
            binding.VolumePage,
            binding.SliderConfig,
            ref binding.Slider);
        GameObject stopTarget = CaptureGeneratedElement(
            pageMgr,
            binding.StopPage,
            binding.ToggleConfig,
            ref binding.Toggle);
        if (volumeTarget != null)
        {
            binding.Slider ??= CreateUiElement<SettingSlider>(pageMgr.SliderElementPrefab, volumeTarget.transform);
        }
        if (stopTarget != null)
        {
            binding.Toggle ??= CreateUiElement<SettingToggle>(pageMgr.ToggleElementPrefab, stopTarget.transform);
        }
        BindUiElements(binding);
    }

    private static void CaptureGeneratedUi(PageMgr pageMgr, SettingsUiBinding binding)
    {
        CaptureGeneratedElement(pageMgr, binding.VolumePage, binding.SliderConfig, ref binding.Slider);
        CaptureGeneratedElement(pageMgr, binding.StopPage, binding.ToggleConfig, ref binding.Toggle);
    }

    private static GameObject CaptureGeneratedElement<T>(
        PageMgr pageMgr,
        SettingPageConfig page,
        SettingElementConfig config,
        ref T element) where T : SettingUIElement
    {
        GameObject target = FindGeneratedPageTarget(pageMgr, page);
        if (target == null)
        {
            return null;
        }
        SettingUIElement[] elements = target.GetComponentsInChildren<SettingUIElement>(true);
        int index = page.elements.IndexOf(config);
        if (element == null && index >= 0 && index < elements.Length)
        {
            element = elements[index] as T;
        }
        return target;
    }

    private static GameObject FindGeneratedPageTarget(PageMgr pageMgr, SettingPageConfig page)
    {
        if (!(PageButtonsField?.GetValue(pageMgr) is IEnumerable<PageButton> buttons))
        {
            return null;
        }
        PageButton button = buttons.FirstOrDefault(candidate => candidate != null && candidate.PageConfig == page);
        return button == null ? null : PageTargetField?.GetValue(button) as GameObject;
    }

    private static T CreateUiElement<T>(GameObject prefab, Transform parent) where T : SettingUIElement
    {
        if (prefab == null || parent == null)
        {
            return null;
        }
        GameObject instance = UnityEngine.Object.Instantiate(prefab, parent);
        instance.name = "SunnyMod_" + typeof(T).Name;
        instance.transform.SetAsLastSibling();
        instance.SetActive(true);
        return instance.GetComponent<T>();
    }

    private static void AddManualElement(ManualSettingPageSlot slot, SettingUIElement element)
    {
        if (element == null || slot.elements == null || slot.elements.Count == 0)
        {
            return;
        }
        if (!slot.elements.Contains(element))
        {
            slot.elements.Add(element);
        }
    }

    private static void BindUiElements(SettingsUiBinding binding)
    {
        if (binding.Slider != null && binding.SliderConfig != null && _volumeValue != null && !binding.SliderBound)
        {
            float originalVolume = _volumeValue.Val;
            if (SliderControlField?.GetValue(binding.Slider) is UnityEngine.UI.Slider sliderControl)
            {
                sliderControl.onValueChanged.RemoveAllListeners();
            }
            binding.Slider.Init(binding.SliderConfig, _volumeValue);
            if (!Mathf.Approximately(_volumeValue.Val, originalVolume))
            {
                SunnyModLoaderPlugin.Log.LogError(
                    "Voice volume changed while binding its settings slider; restoring the previous value.");
                _volumeValue.Val = originalVolume;
            }
            binding.SliderBound = true;
        }
        if (binding.Toggle != null && binding.ToggleConfig != null && _stopValue != null && !binding.ToggleBound)
        {
            binding.Toggle.Init(binding.ToggleConfig, _stopValue);
            binding.ToggleBound = true;
        }
    }

    private static void ResetInjectedUi()
    {
        foreach (SettingsUiBinding binding in UiBindings.Values.ToArray())
        {
            binding.RestoreManualVolumeLayout();
            if (binding.SliderConfig != null && binding.VolumePage?.elements != null)
            {
                binding.VolumePage.elements.Remove(binding.SliderConfig);
            }
            if (binding.ToggleConfig != null && binding.StopPage?.elements != null)
            {
                binding.StopPage.elements.Remove(binding.ToggleConfig);
            }
            RemoveManualElement(
                binding.Manager?.pageMgr,
                binding.VolumePageIndex,
                binding.Slider);
            RemoveManualElement(
                binding.Manager?.pageMgr,
                binding.StopPageIndex,
                binding.Toggle);
            DestroyInjectedElement(binding.Slider);
            DestroyInjectedElement(binding.Toggle);
        }
        UiBindings.Clear();
    }

    private static void RemoveManualElement(PageMgr pageMgr, int pageIndex, SettingUIElement element)
    {
        if (element == null || pageMgr?.manualPages == null ||
            pageIndex < 0 || pageIndex >= pageMgr.manualPages.Count)
        {
            return;
        }
        pageMgr.manualPages[pageIndex]?.elements?.Remove(element);
    }

    private static void DestroyInjectedElement(SettingUIElement element)
    {
        if (element != null)
        {
            element.gameObject.SetActive(false);
            element.transform.SetParent(null, false);
            UnityEngine.Object.Destroy(element.gameObject);
        }
    }

    private sealed class SettingsUiBinding
    {
        internal SettingManager Manager;
        internal SettingPageConfig VolumePage;
        internal int VolumePageIndex;
        internal SettingPageConfig StopPage;
        internal int StopPageIndex;
        internal SliderSettingConfig SliderConfig;
        internal ToggleSettingConfig ToggleConfig;
        internal SettingSlider Slider;
        internal SettingToggle Toggle;
        internal bool SliderBound;
        internal bool ToggleBound;
        internal bool ExpectPageManagerBinding;
        private readonly List<ShiftedRect> _volumeShiftedRects = new List<ShiftedRect>();

        internal void ResetSliderUi()
        {
            RestoreManualVolumeLayout();
            SliderBound = false;
        }

        internal void ResetToggleUi()
        {
            ToggleBound = false;
        }

        internal void ApplyBalancedVolumeLayout(
            IEnumerable<RectTransform> originalSliders,
            RectTransform volumeHeading,
            RectTransform preferenceHeading,
            RectTransform preferenceToggle,
            float halfStep)
        {
            RestoreManualVolumeLayout();
            foreach (RectTransform slider in originalSliders.Where(slider => slider != null))
            {
                _volumeShiftedRects.Add(new ShiftedRect(slider, slider.anchoredPosition, 1f));
            }
            if (volumeHeading != null)
            {
                _volumeShiftedRects.Add(new ShiftedRect(volumeHeading, volumeHeading.anchoredPosition, 1f));
            }
            if (preferenceHeading != null)
            {
                _volumeShiftedRects.Add(new ShiftedRect(preferenceHeading, preferenceHeading.anchoredPosition, -1f));
            }
            if (preferenceToggle != null)
            {
                _volumeShiftedRects.Add(new ShiftedRect(preferenceToggle, preferenceToggle.anchoredPosition, -1f));
            }

            foreach (ShiftedRect item in _volumeShiftedRects)
            {
                if (item.Rect != null)
                {
                    item.Rect.anchoredPosition =
                        item.OriginalPosition + Vector2.up * (halfStep * item.Direction);
                }
            }
        }

        internal void RestoreManualVolumeLayout()
        {
            foreach (ShiftedRect item in _volumeShiftedRects)
            {
                if (item.Rect != null)
                {
                    item.Rect.anchoredPosition = item.OriginalPosition;
                }
            }
            _volumeShiftedRects.Clear();
        }

        internal bool ValidateManualVolumeLayout(RectTransform root, RectTransform voiceSlider)
        {
            if (root == null || voiceSlider == null || _volumeShiftedRects.Count != 6)
            {
                return false;
            }

            RectTransform[] rects = _volumeShiftedRects
                .Select(item => item.Rect)
                .Append(voiceSlider)
                .Where(rect => rect != null)
                .Distinct()
                .ToArray();
            if (rects.Length != 7 || rects.Any(rect =>
                    !IsWithinRoot(root, rect) || !IsWithinClippingAncestors(root, rect)))
            {
                return false;
            }

            for (int left = 0; left < rects.Length; left++)
            {
                Bounds leftBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(root, rects[left]);
                for (int right = left + 1; right < rects.Length; right++)
                {
                    Bounds rightBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(root, rects[right]);
                    float overlapX = Mathf.Min(leftBounds.max.x, rightBounds.max.x) -
                                     Mathf.Max(leftBounds.min.x, rightBounds.min.x);
                    float overlapY = Mathf.Min(leftBounds.max.y, rightBounds.max.y) -
                                     Mathf.Max(leftBounds.min.y, rightBounds.min.y);
                    if (overlapX > 1f && overlapY > 1f)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        internal void SetVisible(bool visible)
        {
            if (Slider != null)
            {
                Slider.gameObject.SetActive(visible);
            }
            if (Toggle != null)
            {
                Toggle.gameObject.SetActive(visible);
            }
        }

        private sealed class ShiftedRect
        {
            internal readonly RectTransform Rect;
            internal readonly Vector2 OriginalPosition;
            internal readonly float Direction;

            internal ShiftedRect(RectTransform rect, Vector2 originalPosition, float direction)
            {
                Rect = rect;
                OriginalPosition = originalPosition;
                Direction = direction;
            }
        }
    }

    private sealed class DiagnosticDisplaySnapshot
    {
        internal readonly Val Mode;
        internal readonly int ModeValue;
        internal readonly bool HasMode;
        internal readonly Val Resolution;
        internal readonly int ResolutionValue;
        internal readonly int ScreenWidth;
        internal readonly int ScreenHeight;
        internal readonly FullScreenMode FullScreenMode;
        internal readonly bool HadModePreference;
        internal readonly int ModePreferenceValue;
        internal readonly bool HadResolutionPreference;
        internal readonly int ResolutionPreferenceValue;

        internal DiagnosticDisplaySnapshot(
            Val mode,
            int modeValue,
            bool hasMode,
            Val resolution,
            int resolutionValue,
            int screenWidth,
            int screenHeight,
            FullScreenMode fullScreenMode,
            bool hadModePreference,
            int modePreferenceValue,
            bool hadResolutionPreference,
            int resolutionPreferenceValue)
        {
            Mode = mode;
            ModeValue = modeValue;
            HasMode = hasMode;
            Resolution = resolution;
            ResolutionValue = resolutionValue;
            ScreenWidth = screenWidth;
            ScreenHeight = screenHeight;
            FullScreenMode = fullScreenMode;
            HadModePreference = hadModePreference;
            ModePreferenceValue = modePreferenceValue;
            HadResolutionPreference = hadResolutionPreference;
            ResolutionPreferenceValue = resolutionPreferenceValue;
        }
    }
}

internal sealed class AudioControlSettingsLayoutWatcher : MonoBehaviour
{
    private SettingManager _manager;
    private int _screenWidth;
    private int _screenHeight;

    internal void Bind(SettingManager manager)
    {
        _manager = manager;
        _screenWidth = Screen.width;
        _screenHeight = Screen.height;
    }

    private void Update()
    {
        if (_manager == null || (_screenWidth == Screen.width && _screenHeight == Screen.height))
        {
            return;
        }
        _screenWidth = Screen.width;
        _screenHeight = Screen.height;
        AudioControlService.RefreshSettingsUiLayout(_manager);
    }

    private void OnDestroy()
    {
        AudioControlService.RemoveBinding(_manager);
    }
}

[HarmonyPatch(typeof(UserPreferenceManager), "Awake")]
internal static class AudioControlPreferencePatch
{
    private static void Prefix(UserPreferenceManager __instance)
    {
        AudioControlService.EnsurePreferences(__instance);
    }

    private static void Postfix(UserPreferenceManager __instance)
    {
        AudioControlService.BindPreferences(__instance);
    }
}

[HarmonyPatch(typeof(SettingManager), "Start")]
internal static class AudioControlSettingsUiPatch
{
    private static void Prefix(SettingManager __instance)
    {
        AudioControlService.EnsureSettingsUi(__instance, true);
    }

    private static void Postfix(SettingManager __instance)
    {
        AudioControlService.BindSettingsUiAfterStart(__instance);
    }
}

[HarmonyPatch(typeof(SettingManager), "Open")]
internal static class AudioControlSettingsOpenPatch
{
    private static void Postfix(SettingManager __instance)
    {
        AudioControlService.RefreshSettingsUiLayout(__instance);
    }
}
