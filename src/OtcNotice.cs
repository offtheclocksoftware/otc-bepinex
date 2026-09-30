// otc_bepinex - Off the Clock shared BepInEx helpers. README.md next to src\.
// OtcNotice: a small on-screen box for a moment after a key press (IMGUI, drawn from OtcHost's OnGUI on the main thread).
// Generalized from the on-screen notices of our Vampire Survivors and Risk of Rain 2 plugins.
// Needs UnityEngine.IMGUIModule + TextRenderingModule references (OtcNotice=false in the props leaves this file out).
using System;
using BepInEx.Logging;
using UnityEngine;

namespace OffTheClock.Lib;

/// <summary>
/// <code>
/// var notice = new OtcNotice(Log, "MyMod", () =&gt; ShowNotice.Value);   // config switch (optional)
/// notice.Start();                  // draws from OtcHost's OnGUI
/// notice.Show("Gold x5");          // after a key press; the log line is the caller's job (always write one)
/// </code>
/// If drawing ever fails (e.g. an IMGUI part stripped from a future game build), the notice switches itself off with
/// one WARNING; the mod keeps working and new values stay visible in the log.
/// </summary>
internal sealed class OtcNotice
{
    readonly ManualLogSource _log;
    readonly string _mod;
    readonly Func<bool> _enabled;
    string _text;
    float _until;
    bool _broken, _started;

    /// <summary>Seconds a notice stays on screen (default 2.5).</summary>
    internal float Seconds { get; set; } = 2.5f;

    /// <summary>Font size of the text (default 24).</summary>
    internal int FontSize { get; set; } = 24;

    /// <summary>Clock (default Time.unscaledTime, so it also counts down while the game is paused). Replaceable for tests.</summary>
    internal Func<float> Now { get; set; } = () => Time.unscaledTime;

    internal OtcNotice(ManualLogSource log, string mod, Func<bool> enabled = null)
    {
        _log = log;
        _mod = string.IsNullOrEmpty(mod) ? "Mod" : mod;
        _enabled = enabled;
    }

    internal bool Broken => _broken;

    /// <summary>The text on screen right now, or null.</summary>
    internal string Current
    {
        get
        {
            try { return _text != null && Now() <= _until ? _text : null; }
            catch { return null; }
        }
    }

    /// <summary>Registers Draw with OtcHost's OnGUI. Call after OtcHost.Start.</summary>
    internal void Start()
    {
        if (_started) return;
        _started = true;
        OtcHost.OnGui("notice:" + _mod, Draw);
    }

    internal void Show(string text)
    {
        try
        {
            _text = text;
            _until = Now() + Seconds;
        }
        catch { /* no clock: nothing shown, the log line still comes */ }
    }

    /// <summary>OnGUI only.</summary>
    internal void Draw()
    {
        if (_broken || _text == null) return;
        try
        {
            if (_enabled != null && !_enabled()) return;
            if (Now() > _until) { _text = null; return; }
            float w = Mathf.Max(260f, 14f * _text.Length), h = 44f;
            var rect = new Rect((Screen.width - w) / 2f, Screen.height * 0.12f, w, h);
            GUI.Box(rect, "");
            GUI.Box(rect, "");                         // twice: the default box is quite transparent
            var style = GUI.skin.label;
            int oldSize = style.fontSize;
            var oldAlign = style.alignment;
            style.fontSize = FontSize;
            style.alignment = TextAnchor.MiddleCenter;
            GUI.Label(rect, _text, style);
            style.fontSize = oldSize;
            style.alignment = oldAlign;
        }
        catch (Exception ex)
        {
            _broken = true;
            _log?.LogWarning($"{_mod}: on-screen notice unavailable ({ex.GetType().Name}: {ex.Message}); new values are shown in the log only.");
        }
    }
}
