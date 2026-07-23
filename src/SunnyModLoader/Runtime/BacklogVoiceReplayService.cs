using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MakeineGalGameQM.Core;
using MakeineGalGameQM.Core.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SunnyModLoader;

internal static class BacklogVoiceReplayService
{
    private const string ReplayButtonName = "SunnyMod_VoiceReplay";
    private static readonly FieldInfo CurrentIndexField = AccessTools.Field(typeof(BackLogPanel), "currentIndex");
    private static readonly FieldInfo BacklogHistoryField = AccessTools.Field(typeof(BackLogManager), "backLogHistory");
    private static readonly Dictionary<string, SceneScripts> SceneCache =
        new Dictionary<string, SceneScripts>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, ElementBinding> ElementBindings =
        new Dictionary<int, ElementBinding>();
    private static readonly HashSet<int> DiagnosticPanels = new HashSet<int>();

    internal static void InvalidateScriptCache()
    {
        SceneCache.Clear();
        RefreshAllVisible();
    }

    internal static bool CanOpen(CorePlayer player)
    {
        return !BranchService.HasPendingChoice(player);
    }

    internal static bool CanOpen(BackLogPanel panel)
    {
        CorePlayer player = panel?.manager?.player ?? UnityEngine.Object.FindFirstObjectByType<CorePlayer>();
        return CanOpen(player);
    }

    internal static bool HideIfChoicePending(BackLogPanel panel)
    {
        if (CanOpen(panel))
        {
            return false;
        }
        if (panel != null && panel.IsVisable)
        {
            panel.Hide();
        }
        return true;
    }

    internal static void HideAllVisible()
    {
        foreach (BackLogPanel panel in FindPanels())
        {
            if (panel != null && panel.IsVisable)
            {
                panel.Hide();
            }
        }
    }

    internal static void RefreshAllVisible()
    {
        foreach (BackLogPanel panel in FindPanels())
        {
            if (panel != null && panel.IsVisable)
            {
                RefreshPanel(panel);
            }
        }
    }

    internal static void RefreshPanel(BackLogPanel panel)
    {
        RemoveDeadBindings();
        if (panel?.elements == null || panel.manager == null)
        {
            return;
        }

        IReadOnlyList<MessageEntry> history = panel.manager.GetHistory();
        int currentIndex = CurrentIndexField?.GetValue(panel) is int value ? value : -1;
        int firstHistoryIndex = currentIndex - Math.Max(0, panel.elements.Count - 1);
        for (int elementIndex = 0; elementIndex < panel.elements.Count; elementIndex++)
        {
            BackLogElement element = panel.elements[elementIndex];
            int historyIndex = firstHistoryIndex + elementIndex;
            bool visibleEntry = element != null && element.gameObject.activeSelf &&
                                historyIndex >= 0 && historyIndex < history.Count;
            if (!SunnyModAudioControl.IsAvailable || !visibleEntry ||
                !TryFindVoice(history[historyIndex], out string audioPath))
            {
                SetReplayState(element, false, null);
                continue;
            }
            SetReplayState(element, true, () => SunnyModLoaderPlugin.Voice?.Replay(audioPath));
        }
    }

    internal static void RemovePanel(BackLogPanel panel)
    {
        if (panel?.elements == null)
        {
            return;
        }
        foreach (BackLogElement element in panel.elements)
        {
            if (element != null)
            {
                ElementBindings.Remove(element.GetInstanceID());
            }
        }
    }

    internal static void OnPanelStarted(BackLogPanel panel)
    {
        if (!StartupDiagnostics.IsRequested || panel == null)
        {
            return;
        }
        bool uiReady = ValidatePanelUi(panel, out string uiDetail);
        SunnyModLoaderPlugin.Log.LogInfo(
            "Startup validation inspected live backlog UI: ready=" + uiReady + ", " + uiDetail + ".");
        if (StartupDiagnostics.IsBacklogUiRequested && DiagnosticPanels.Add(panel.GetInstanceID()))
        {
            if (TryPopulateDiagnosticHistory(panel, out string populateDetail))
            {
                panel.Show();
                CorePlayer player = panel.manager.player;
                bool choiceGuard = BranchService.ValidateBacklogGuardForDiagnostics(player, panel);
                player?.Pause();
                panel.Show();
                SunnyModLoaderPlugin.Log.LogInfo(
                    "Startup validation opened synthetic backlog voice UI: " + populateDetail +
                    ", choiceGuard=" + choiceGuard + ".");
            }
            else
            {
                SunnyModLoaderPlugin.Log.LogError(
                    "Startup validation could not open synthetic backlog voice UI: " + populateDetail + ".");
            }
        }
    }

    internal static bool ValidateForDiagnostics(out string detail)
    {
        InvalidateScriptCache();
        bool voiceResolved = false;
        bool silentRejected = false;
        foreach (ModPackage package in SunnyModLoaderPlugin.Registry.ActiveLowToHigh)
        {
            foreach (FlowDefinition flow in package.Flows)
            {
                SceneScripts scripts = GetSceneScripts(flow.SceneUri);
                if (scripts?.scripts == null)
                {
                    continue;
                }
                for (int index = 0; index < scripts.scripts.Count; index++)
                {
                    ScriptBase script = scripts.scripts[index];
                    if (!IsDialogue(script))
                    {
                        continue;
                    }
                    MessageEntry entry = new MessageEntry
                    {
                        sceneName = flow.SceneUri,
                        scriptIndexAfter = index + 1
                    };
                    string expected = LoaderUtil.GetStringParameter(script.parameters, "audiopath");
                    bool resolved = TryFindVoice(entry, out string actual);
                    if (string.IsNullOrWhiteSpace(expected))
                    {
                        silentRejected |= !resolved;
                    }
                    else
                    {
                        voiceResolved |= resolved && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
        }

        BackLogPanel[] panels = FindPanels().ToArray();
        BackLogPanel panel = panels.FirstOrDefault(candidate => candidate?.elements?.Count > 0);
        bool uiReady = ValidatePanelUi(panel, out string elementDetail);
        string uiDetail = "panels=" + panels.Length + ", elements=" + (panel?.elements?.Count ?? 0);
        uiDetail += ", " + elementDetail;

        string uiStatus = panels.Length == 0 ? "deferred" : uiReady.ToString();
        detail = "voiceResolved=" + voiceResolved + ", silentRejected=" + silentRejected +
                 ", uiReady=" + uiStatus + ", " + uiDetail +
                 ", enabled=" + SunnyModAudioControl.IsAvailable;
        return voiceResolved && silentRejected && (panels.Length == 0 || uiReady);
    }

    private static bool ValidatePanelUi(BackLogPanel panel, out string detail)
    {
        BackLogElement testElement = panel?.elements?.FirstOrDefault(element => element != null);
        if (testElement == null)
        {
            detail = "testElement=false";
            return false;
        }
        RectTransform elementRect = testElement.transform as RectTransform;
        RectTransform contentRect = testElement.text_Content?.rectTransform;
        RectTransform rollbackRect = testElement.button_RollBack?.transform as RectTransform;
        Vector2 beforeMin = contentRect?.offsetMin ?? Vector2.zero;
        Vector2 beforeMax = contentRect?.offsetMax ?? Vector2.zero;
        SetReplayState(testElement, true, () => { });
        bool shown = ElementBindings.TryGetValue(testElement.GetInstanceID(), out ElementBinding binding) &&
                     binding.Button != null && binding.Button.gameObject.activeSelf;
        RectTransform buttonRect = binding?.Button?.transform as RectTransform;
        bool withinRow = elementRect != null && buttonRect != null && IsWithin(elementRect, buttonRect);
        bool sameSize = rollbackRect != null && buttonRect != null &&
                        Vector2.Distance(rollbackRect.rect.size, buttonRect.rect.size) < 0.01f;
        bool mirrored = elementRect != null && rollbackRect != null && buttonRect != null &&
                        AreHorizontallyMirrored(elementRect, rollbackRect, buttonRect);
        bool contentUnchanged = contentRect != null &&
                                Vector2.Distance(contentRect.offsetMin, beforeMin) < 0.01f &&
                                Vector2.Distance(contentRect.offsetMax, beforeMax) < 0.01f;
        SetReplayState(testElement, false, null);
        detail = "binding=" + (binding != null) + ", content=" + (contentRect != null) +
                 ", shown=" + shown + ", withinRow=" + withinRow + ", sameAsJump=" + sameSize +
                 ", mirrored=" + mirrored + ", contentUnchanged=" + contentUnchanged +
                 ", row=" + (elementRect == null ? "null" : elementRect.rect.size.ToString("F1")) +
                 ", jump=" + (rollbackRect == null ? "null" : rollbackRect.rect.size.ToString("F1")) +
                 ", button=" + (buttonRect == null ? "null" : buttonRect.rect.size.ToString("F1"));
        return shown && withinRow && sameSize && mirrored && contentUnchanged;
    }

    private static bool TryPopulateDiagnosticHistory(BackLogPanel panel, out string detail)
    {
        MessageEntry voiced = null;
        MessageEntry silent = null;
        foreach (ModPackage package in SunnyModLoaderPlugin.Registry.ActiveLowToHigh)
        {
            foreach (FlowDefinition flow in package.Flows)
            {
                SceneScripts scripts = GetSceneScripts(flow.SceneUri);
                if (scripts?.scripts == null)
                {
                    continue;
                }
                for (int index = 0; index < scripts.scripts.Count; index++)
                {
                    ScriptBase script = scripts.scripts[index];
                    if (!IsDialogue(script))
                    {
                        continue;
                    }
                    MessageEntry entry = new MessageEntry
                    {
                        speakerId = LoaderUtil.GetStringParameter(script.parameters, "id") ?? string.Empty,
                        displayName = LoaderUtil.GetStringParameter(script.parameters, "specialname") ??
                                      LoaderUtil.GetStringParameter(script.parameters, "id") ?? string.Empty,
                        text = LoaderUtil.GetStringParameter(script.parameters, "content") ?? string.Empty,
                        sceneName = flow.SceneUri,
                        scriptIndexAfter = index + 1,
                        snapshotIndex = -1
                    };
                    if (TryFindVoice(entry, out _))
                    {
                        voiced ??= entry;
                    }
                    else
                    {
                        silent ??= entry;
                    }
                }
            }
        }
        if (panel?.manager == null || voiced == null || silent == null ||
            !(BacklogHistoryField?.GetValue(panel.manager) is List<MessageEntry> history))
        {
            detail = "manager=" + (panel?.manager != null) + ", voiced=" + (voiced != null) +
                     ", silent=" + (silent != null) + ", history=" +
                     (panel?.manager != null && BacklogHistoryField?.GetValue(panel.manager) is List<MessageEntry>);
            return false;
        }

        history.Clear();
        history.Add(CloneEntry(silent, "无语音条目不会显示播放按钮。"));
        history.Add(CloneEntry(voiced, "有语音条目会显示播放按钮。"));
        history.Add(CloneEntry(silent, "有无语音都保持原版正文宽度和换行。"));
        history.Add(CloneEntry(voiced, "点击右侧播放图标可重放本句语音。"));
        history.Add(CloneEntry(voiced, "重复点击会重新开始播放。"));
        detail = "entries=" + history.Count + ", voicePath=" +
                 (TryFindVoice(voiced, out string path) ? path : "missing");
        return true;
    }

    private static MessageEntry CloneEntry(MessageEntry source, string text)
    {
        return new MessageEntry
        {
            speakerId = source.speakerId,
            displayName = source.displayName,
            text = text,
            sceneName = source.sceneName,
            scriptIndexAfter = source.scriptIndexAfter,
            snapshotIndex = -1
        };
    }

    private static bool IsWithin(RectTransform root, RectTransform element)
    {
        Rect rect = root.rect;
        Vector3[] corners = new Vector3[4];
        element.GetWorldCorners(corners);
        const float tolerance = 1f;
        foreach (Vector3 worldCorner in corners)
        {
            Vector3 corner = root.InverseTransformPoint(worldCorner);
            if (corner.x < rect.xMin - tolerance || corner.x > rect.xMax + tolerance ||
                corner.y < rect.yMin - tolerance || corner.y > rect.yMax + tolerance)
            {
                return false;
            }
        }
        return true;
    }

    private static bool AreHorizontallyMirrored(
        RectTransform root,
        RectTransform left,
        RectTransform right)
    {
        Bounds leftBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(root, left);
        Bounds rightBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(root, right);
        Vector2 center = root.rect.center;
        const float tolerance = 1f;
        return Mathf.Abs((leftBounds.center.x + rightBounds.center.x) * 0.5f - center.x) <= tolerance &&
               Mathf.Abs(leftBounds.center.y - rightBounds.center.y) <= tolerance;
    }

    private static bool TryFindVoice(MessageEntry entry, out string audioPath)
    {
        audioPath = null;
        if (entry == null || string.IsNullOrWhiteSpace(entry.sceneName) || entry.scriptIndexAfter <= 0)
        {
            return false;
        }
        SceneScripts scripts = GetSceneScripts(entry.sceneName);
        int dialogueIndex = entry.scriptIndexAfter - 1;
        if (scripts?.scripts == null || dialogueIndex < 0 || dialogueIndex >= scripts.scripts.Count)
        {
            return false;
        }
        ScriptBase script = scripts.scripts[dialogueIndex];
        if (!IsDialogue(script))
        {
            return false;
        }
        audioPath = LoaderUtil.GetStringParameter(script.parameters, "audiopath");
        return !string.IsNullOrWhiteSpace(audioPath);
    }

    private static SceneScripts GetSceneScripts(string scene)
    {
        if (string.IsNullOrWhiteSpace(scene))
        {
            return null;
        }
        if (SceneCache.TryGetValue(scene, out SceneScripts cached))
        {
            return cached;
        }
        try
        {
            SceneScripts scripts = SceneScriptsReader.ReadSceneScripts(scene);
            SceneCache[scene] = scripts;
            return scripts;
        }
        catch (Exception ex)
        {
            SunnyModLoaderPlugin.Log.LogWarning("Could not resolve backlog voice metadata for " + scene + ": " + ex.Message);
            SceneCache[scene] = null;
            return null;
        }
    }

    private static bool IsDialogue(ScriptBase script)
    {
        return script != null && string.Equals(script.command, "dialogue", StringComparison.OrdinalIgnoreCase);
    }

    private static void SetReplayState(BackLogElement element, bool visible, UnityAction replay)
    {
        if (element == null)
        {
            return;
        }
        if (!ElementBindings.TryGetValue(element.GetInstanceID(), out ElementBinding binding))
        {
            if (!visible)
            {
                return;
            }
            binding = CreateBinding(element);
            if (binding == null)
            {
                return;
            }
            ElementBindings[element.GetInstanceID()] = binding;
        }

        binding.Button.onClick.RemoveAllListeners();
        if (visible && replay != null)
        {
            binding.Button.onClick.AddListener(replay);
        }
        binding.SetVisible(visible && replay != null);
    }

    private static ElementBinding CreateBinding(BackLogElement element)
    {
        RectTransform elementRect = element.transform as RectTransform;
        Button rollbackButton = element.button_RollBack;
        RectTransform rollbackRect = rollbackButton?.transform as RectTransform;
        if (elementRect == null || rollbackButton == null || rollbackRect == null)
        {
            return null;
        }

        GameObject buttonObject = new GameObject(
            ReplayButtonName,
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(TextMeshProUGUI),
            typeof(Button));
        buttonObject.transform.SetParent(element.transform, false);
        buttonObject.transform.SetAsLastSibling();
        RectTransform buttonRect = (RectTransform)buttonObject.transform;
        buttonRect.anchorMin = new Vector2(1f - rollbackRect.anchorMax.x, rollbackRect.anchorMin.y);
        buttonRect.anchorMax = new Vector2(1f - rollbackRect.anchorMin.x, rollbackRect.anchorMax.y);
        buttonRect.pivot = new Vector2(1f - rollbackRect.pivot.x, rollbackRect.pivot.y);
        float buttonOffset = 28f;
        buttonRect.anchoredPosition = new Vector2(-rollbackRect.anchoredPosition.x + buttonOffset, rollbackRect.anchoredPosition.y);
        buttonRect.sizeDelta = rollbackRect.sizeDelta;
        buttonRect.localScale = rollbackRect.localScale;
        buttonRect.localRotation = rollbackRect.localRotation;

        TextMeshProUGUI label = buttonObject.GetComponent<TextMeshProUGUI>();
        label.text = "▶";
        label.alignment = TextAlignmentOptions.Center;
        label.enableAutoSizing = false;
        label.font = element.text_Content.font;
        label.fontSharedMaterial = element.text_Content.fontSharedMaterial;
        label.fontSize = rollbackRect.rect.height * 0.68f;
        label.color = element.text_Content.color;
        label.raycastTarget = true;

        Button button = buttonObject.GetComponent<Button>();
        button.transition = Selectable.Transition.ColorTint;
        button.colors = rollbackButton.colors;
        button.targetGraphic = label;
        button.interactable = true;

        buttonObject.SetActive(false);
        return new ElementBinding(element, button);
    }

    private static IEnumerable<BackLogPanel> FindPanels()
    {
        return Resources.FindObjectsOfTypeAll<BackLogPanel>()
            .Where(panel => panel != null && panel.gameObject.scene.IsValid());
    }

    private static void RemoveDeadBindings()
    {
        foreach (int id in ElementBindings
                     .Where(item => item.Value?.Element == null || item.Value.Button == null)
                     .Select(item => item.Key)
                     .ToArray())
        {
            ElementBindings.Remove(id);
        }
    }

    private sealed class ElementBinding
    {
        internal readonly BackLogElement Element;
        internal readonly Button Button;

        internal ElementBinding(BackLogElement element, Button button)
        {
            Element = element;
            Button = button;
        }

        internal void SetVisible(bool visible)
        {
            if (Button != null)
            {
                Button.gameObject.SetActive(visible);
            }
        }
    }
}
