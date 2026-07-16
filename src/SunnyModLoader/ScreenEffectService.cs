using System;
using System.Collections;
using System.Globalization;
using System.Runtime.Serialization;
using System.Text;
using MakeineGalGameQM.Core;
using MakeineGalGameQM.Core.Utils;
using UnityEngine;
using UnityEngine.UI;

namespace SunnyModLoader;

[DataContract]
internal sealed class ScreenEffectRequest
{
    [DataMember(Order = 1)] public string type = "flash";
    [DataMember(Order = 2)] public string color = "#FFFFFF";
    [DataMember(Order = 3)] public float alpha = 1f;
    [DataMember(Order = 4)] public float fadeIn = 0.03f;
    [DataMember(Order = 5)] public float hold = 0.02f;
    [DataMember(Order = 6)] public float fadeOut = 0.12f;
    [DataMember(Order = 7)] public int count = 1;
    [DataMember(Order = 8)] public float gap = 0.04f;
    [DataMember(Order = 9)] public bool wait = true;
}

internal sealed class ScreenEffectService
{
    private const string UriPrefix = "mod://effect/";
    private const int OverlaySortingOrder = 32760;

    private readonly SunnyModLoaderPlugin _host;
    private GameObject _canvasObject;
    private Image _overlay;
    private int _generation;
    private CorePlayer _activePlayer;
    private CorePlayer _blockingPlayer;
    private ScriptBlocker _blocker;

    internal ScreenEffectService(SunnyModLoaderPlugin host)
    {
        _host = host;
    }

    internal bool HasBlockingEffect(CorePlayer player)
    {
        return player != null && _blockingPlayer == player && _blocker != null;
    }

    internal bool TryIntercept(ScriptBase script, CorePlayer player)
    {
        if (script == null || !string.Equals(script.command, "switchscene", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryIntercept(script.parameters?.ToArray(), player, false);
    }

    internal bool TryIntercept(ICommandParameter[] parameters, CorePlayer player, bool logicOnly)
    {
        string path = LoaderUtil.GetStringParameter(parameters, "path");
        if (!IsEffectUri(path))
        {
            return false;
        }

        if (!TryParseUri(path, out ScreenEffectRequest request))
        {
            SunnyModLoaderPlugin.Log.LogError("Screen effect command contained an invalid internal payload.");
            return true;
        }

        if (logicOnly || player == null || player.IsFastMode || player.IsTransientSkipActive)
        {
            CancelForPlayer(player);
            return true;
        }

        Play(request, player);
        return true;
    }

    internal void CancelForPlayer(CorePlayer player)
    {
        if (player != null && _activePlayer != player && _blockingPlayer != player)
        {
            return;
        }

        CancelActive();
    }

    internal void Shutdown()
    {
        CancelActive();
        if (_canvasObject != null)
        {
            UnityEngine.Object.Destroy(_canvasObject);
        }
        _canvasObject = null;
        _overlay = null;
    }

    internal static string CreateUri(ScreenEffectRequest request)
    {
        string json = JsonCodec.Serialize(request);
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return UriPrefix + encoded;
    }

    internal static bool TryParseUri(string value, out ScreenEffectRequest request)
    {
        request = null;
        if (!IsEffectUri(value))
        {
            return false;
        }

        try
        {
            string encoded = value.Substring(UriPrefix.Length).Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            string json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            request = JsonCodec.Deserialize<ScreenEffectRequest>(json);
            return IsValid(request);
        }
        catch
        {
            request = null;
            return false;
        }
    }

    internal static bool ValidateForDiagnostics(out string detail)
    {
        ScreenEffectRequest expected = new ScreenEffectRequest
        {
            color = "#F8FCFF",
            alpha = 0.85f,
            fadeIn = 0.02f,
            hold = 0.01f,
            fadeOut = 0.09f,
            count = 2,
            gap = 0.03f,
            wait = false
        };
        bool roundTrip = TryParseUri(CreateUri(expected), out ScreenEffectRequest actual) &&
                         actual.type == expected.type && actual.color == expected.color &&
                         Mathf.Approximately(actual.alpha, expected.alpha) &&
                         Mathf.Approximately(actual.fadeIn, expected.fadeIn) &&
                         Mathf.Approximately(actual.hold, expected.hold) &&
                         Mathf.Approximately(actual.fadeOut, expected.fadeOut) &&
                         actual.count == expected.count && Mathf.Approximately(actual.gap, expected.gap) &&
                         actual.wait == expected.wait;
        detail = "codec=" + roundTrip + ", type=flash, overlay=runtime-lazy";
        return roundTrip;
    }

    private void Play(ScreenEffectRequest request, CorePlayer player)
    {
        CancelActive();
        EnsureOverlay();
        if (_overlay == null)
        {
            SunnyModLoaderPlugin.Log.LogError("Could not create the screen effect overlay.");
            return;
        }

        _activePlayer = player;
        int token = ++_generation;
        ScriptBlocker blocker = null;
        if (request.wait)
        {
            blocker = new ScriptBlocker(_ => { }) { ExclusiveManualRelease = true };
            player.AddScriptBlocker(blocker);
            player.Pause();
            _blockingPlayer = player;
            _blocker = blocker;
        }

        _host.StartCoroutine(RunFlash(request, player, blocker, token));
    }

    private IEnumerator RunFlash(
        ScreenEffectRequest request,
        CorePlayer player,
        ScriptBlocker blocker,
        int token)
    {
        yield return null;
        if (token != _generation || _overlay == null)
        {
            yield break;
        }

        Color color = ParseColor(request.color);
        color.a = 0f;
        _overlay.color = color;
        _overlay.gameObject.SetActive(true);

        for (int index = 0; index < request.count; index++)
        {
            yield return AnimateAlpha(0f, request.alpha, request.fadeIn, token);
            if (token != _generation)
            {
                yield break;
            }
            yield return WaitDuration(request.hold, token);
            if (token != _generation)
            {
                yield break;
            }
            yield return AnimateAlpha(request.alpha, 0f, request.fadeOut, token);
            if (token != _generation)
            {
                yield break;
            }
            if (index + 1 < request.count)
            {
                yield return WaitDuration(request.gap, token);
            }
        }

        Complete(player, blocker, token);
    }

    private IEnumerator AnimateAlpha(float from, float to, float duration, int token)
    {
        if (duration <= 0f)
        {
            SetOverlayAlpha(to);
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < duration && token == _generation)
        {
            elapsed += Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
            float progress = Mathf.Clamp01(elapsed / duration);
            SetOverlayAlpha(Mathf.Lerp(from, to, Mathf.SmoothStep(0f, 1f, progress)));
            yield return null;
        }
        if (token == _generation)
        {
            SetOverlayAlpha(to);
        }
    }

    private IEnumerator WaitDuration(float duration, int token)
    {
        float elapsed = 0f;
        while (elapsed < duration && token == _generation)
        {
            elapsed += Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
            yield return null;
        }
    }

    private void Complete(CorePlayer player, ScriptBlocker blocker, int token)
    {
        if (token != _generation)
        {
            return;
        }

        SetOverlayAlpha(0f);
        if (_overlay != null)
        {
            _overlay.gameObject.SetActive(false);
        }
        _activePlayer = null;

        bool shouldContinue = blocker != null && _blockingPlayer == player && _blocker == blocker;
        _blockingPlayer = null;
        _blocker = null;
        if (blocker != null && player != null)
        {
            player.TryReleaseScriptBlocker(blocker);
        }
        if (shouldContinue && player != null)
        {
            player.Continue();
        }
    }

    private void CancelActive()
    {
        _generation++;
        SetOverlayAlpha(0f);
        if (_overlay != null)
        {
            _overlay.gameObject.SetActive(false);
        }

        CorePlayer player = _blockingPlayer;
        ScriptBlocker blocker = _blocker;
        _activePlayer = null;
        _blockingPlayer = null;
        _blocker = null;
        if (player != null && blocker != null)
        {
            player.TryReleaseScriptBlocker(blocker);
        }
    }

    private void EnsureOverlay()
    {
        if (_overlay != null)
        {
            return;
        }

        _canvasObject = new GameObject("SunnyMod_ScreenEffects", typeof(RectTransform), typeof(Canvas));
        _canvasObject.transform.SetParent(_host.transform, false);
        Canvas canvas = _canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.overrideSorting = true;
        canvas.sortingOrder = OverlaySortingOrder;

        GameObject overlayObject = new GameObject(
            "Overlay",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image));
        overlayObject.transform.SetParent(_canvasObject.transform, false);
        RectTransform rect = (RectTransform)overlayObject.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        _overlay = overlayObject.GetComponent<Image>();
        _overlay.raycastTarget = false;
        _overlay.color = new Color(1f, 1f, 1f, 0f);
        overlayObject.SetActive(false);
    }

    private void SetOverlayAlpha(float alpha)
    {
        if (_overlay == null)
        {
            return;
        }
        Color color = _overlay.color;
        color.a = Mathf.Clamp01(alpha);
        _overlay.color = color;
    }

    private static bool IsEffectUri(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.StartsWith(UriPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValid(ScreenEffectRequest request)
    {
        if (request == null || !string.Equals(request.type, "flash", StringComparison.OrdinalIgnoreCase) ||
            !TryParseColor(request.color, out _) || request.alpha < 0f || request.alpha > 1f ||
            request.fadeIn < 0f || request.hold < 0f || request.fadeOut < 0f || request.gap < 0f ||
            request.count < 1 || request.count > 16)
        {
            return false;
        }
        float total = request.count * (request.fadeIn + request.hold + request.fadeOut) +
                      Math.Max(0, request.count - 1) * request.gap;
        return total <= 30f;
    }

    private static Color ParseColor(string value)
    {
        return TryParseColor(value, out Color color) ? color : Color.white;
    }

    private static bool TryParseColor(string value, out Color color)
    {
        color = Color.white;
        if (string.IsNullOrWhiteSpace(value) || value.Length != 7 || value[0] != '#' ||
            !byte.TryParse(value.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte red) ||
            !byte.TryParse(value.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte green) ||
            !byte.TryParse(value.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte blue))
        {
            return false;
        }
        color = new Color32(red, green, blue, 255);
        return true;
    }
}
