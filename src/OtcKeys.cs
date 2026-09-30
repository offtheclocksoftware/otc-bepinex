// otc_bepinex - Off the Clock shared BepInEx helpers. README.md next to src\.
// OtcKeys: configurable hotkeys read on Unity's main thread (from OtcHost's Update), one action per press.
// Needs a UnityEngine.InputLegacyModule reference (OtcKeys=false in the props leaves this file out).
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace OffTheClock.Lib;

/// <summary>
/// Hotkeys from BepInEx config entries (<c>ConfigEntry&lt;KeyCode&gt;</c>, or <c>ConfigEntry&lt;string&gt;</c> holding a
/// KeyCode name such as "KeypadDivide" / "F8"; "None", "" or "Off" = no key). Keys are read every frame on Unity's main
/// thread with UnityEngine.Input.GetKey and turned into presses by edge detection (one action per key-down; holding does
/// not repeat; a key already held when the keys start does not fire; the key is read again after each config change).
/// <code>
/// var keys = new OtcKeys(Log, "MyMod");
/// keys.Add("one level", OneKey, () =&gt; GiveLevels(1));
/// keys.Add("many levels", ManyKey, () =&gt; GiveLevels(ManyCount.Value), cooldown: 0.25f);
/// keys.IsTyping = () =&gt; RoR2.UI.ChatBox.IsActive;   // hook point: ignore keys while a text field has focus
/// keys.Start();                                      // logs the active keys once and ticks from OtcHost
/// </code>
/// Rules: pick defaults that the game (Rewired dump / hard-coded Input reads) and popular mods don't use
/// (docs/ENGINE-BEST-PRACTICES-BEPINEX.md rule 18); numpad digits need Num Lock on; actions run on the main thread.
/// If reading the keyboard throws, keys switch themselves off with one WARNING (config values keep working).
/// </summary>
internal sealed class OtcKeys
{
    sealed class Binding
    {
        public string Name;
        public ConfigEntry<KeyCode> KeyEntry;
        public ConfigEntry<string> TextEntry;
        public string LastText;
        public KeyCode ParsedText;
        public Action OnPress;
        public float Cooldown;
        public float NextAllowed;
        public bool? WasDown;          // null = not read yet (a key held at start does not fire)
        public KeyCode LastKey;
        public int Presses;
    }

    readonly ManualLogSource _log;
    readonly string _mod;
    readonly List<Binding> _bindings = new List<Binding>();
    bool _broken, _started;

    /// <summary>Input source: KeyCode -> held? Default UnityEngine.Input.GetKey. Replaceable for tests.</summary>
    internal Func<KeyCode, bool> IsHeld { get; set; } = DefaultIsHeld;

    /// <summary>Clock in seconds for cooldowns. Default Time.unscaledTime (works while the game is paused).</summary>
    internal Func<float> Now { get; set; } = DefaultNow;

    /// <summary>
    /// Hook point: return true while the player types in a text field (chat, console, name entry). Presses made while it
    /// is true are ignored (and don't fire later when typing ends). An exception counts as "not typing".
    /// </summary>
    internal Func<bool> IsTyping { get; set; }

    /// <summary>Optional gate: presses fire only while it returns true (e.g. "in a run"). Otherwise OnBlocked runs.</summary>
    internal Func<bool> CanAct { get; set; }

    /// <summary>Called with the binding name when a press is ignored because CanAct returned false.</summary>
    internal Action<string> OnBlocked { get; set; }

    internal OtcKeys(ManualLogSource log, string mod)
    {
        _log = log;
        _mod = string.IsNullOrEmpty(mod) ? "Mod" : mod;
    }

    internal bool Broken => _broken;

    /// <summary>Adds a key from a KeyCode config entry. name = what the key does (log text).</summary>
    internal OtcKeys Add(string name, ConfigEntry<KeyCode> entry, Action onPress, float cooldown = 0f)
    {
        _bindings.Add(new Binding { Name = name ?? "key", KeyEntry = entry, OnPress = onPress, Cooldown = Math.Max(0f, cooldown) });
        return this;
    }

    /// <summary>Adds a key from a text config entry holding a KeyCode name (case-insensitive).</summary>
    internal OtcKeys Add(string name, ConfigEntry<string> entry, Action onPress, float cooldown = 0f)
    {
        _bindings.Add(new Binding { Name = name ?? "key", TextEntry = entry, OnPress = onPress, Cooldown = Math.Max(0f, cooldown) });
        return this;
    }

    /// <summary>Presses counted per binding name (tests and self-checks).</summary>
    internal int Presses(string name)
    {
        foreach (var b in _bindings) if (b.Name == name) return b.Presses;
        return 0;
    }

    /// <summary>The key currently bound to a name (KeyCode.None if unset or unknown).</summary>
    internal KeyCode KeyOf(string name)
    {
        foreach (var b in _bindings) if (b.Name == name) return Resolve(b);
        return KeyCode.None;
    }

    /// <summary>Logs the active keys once and registers Tick with OtcHost. Call after OtcHost.Start.</summary>
    internal void Start()
    {
        if (_started) return;
        _started = true;
        LogActive();
        OtcHost.OnUpdate("keys:" + _mod, Tick);
    }

    /// <summary>One INFO line: "keys - Numpad / = one level, Numpad * = many levels"; WARNING for duplicates and bad names.</summary>
    internal void LogActive()
    {
        var parts = new List<string>();
        var seen = new Dictionary<KeyCode, string>();
        foreach (var b in _bindings)
        {
            KeyCode k = Resolve(b);
            if (k == KeyCode.None) continue;
            parts.Add($"{KeyName(k)} = {b.Name}");
            if (seen.TryGetValue(k, out string other))
                _log?.LogWarning($"{_mod}: {KeyName(k)} is set for both '{other}' and '{b.Name}' - one press does both. Change one in the config.");
            else
                seen[k] = b.Name;
        }
        _log?.LogInfo($"{_mod}: keys - " + (parts.Count == 0 ? "none set (all None)." : string.Join(", ", parts) + "."));
    }

    /// <summary>Reads every key once. Main thread only (OtcHost calls it from Update).</summary>
    internal void Tick()
    {
        if (_broken || _bindings.Count == 0) return;
        bool typing = false;
        if (IsTyping != null)
        {
            try { typing = IsTyping(); } catch { typing = false; }
        }
        float now;
        try
        {
            now = Now();
        }
        catch (Exception ex)
        {
            Stop("the game clock", ex);
            return;
        }
        for (int i = 0; i < _bindings.Count; i++)
        {
            var b = _bindings[i];
            KeyCode k = Resolve(b);
            if (k != b.LastKey) { b.LastKey = k; b.WasDown = null; }   // config changed: re-arm without firing
            if (k == KeyCode.None) { b.WasDown = false; continue; }
            bool down;
            try
            {
                down = IsHeld(k);
            }
            catch (Exception ex)
            {
                Stop("the keyboard", ex);
                return;
            }
            bool edge = down && b.WasDown == false;
            b.WasDown = down;
            if (!edge || typing) continue;
            if (b.Cooldown > 0f && now < b.NextAllowed) continue;
            if (CanAct != null && !SafeBool(CanAct))
            {
                if (OnBlocked != null) OtcPatch.Guard(() => OnBlocked(b.Name), "key " + b.Name + " (blocked)");
                continue;
            }
            b.NextAllowed = now + b.Cooldown;
            b.Presses++;
            if (b.OnPress != null) OtcPatch.Guard(b.OnPress, "key " + b.Name);
        }
    }

    void Stop(string what, Exception ex)
    {
        _broken = true;
        _log?.LogWarning($"{_mod}: key reading stopped ({what}: {ex.GetType().Name}: {ex.Message}); the config values still work.");
    }

    static bool SafeBool(Func<bool> f)
    {
        try { return f(); } catch { return false; }
    }

    KeyCode Resolve(Binding b)
    {
        try
        {
            if (b.KeyEntry != null) return b.KeyEntry.Value;
            if (b.TextEntry == null) return KeyCode.None;
            string text = b.TextEntry.Value ?? "";
            if (text == b.LastText) return b.ParsedText;
            b.LastText = text;
            b.ParsedText = ParseKey(text, out bool ok);
            if (!ok) _log?.LogWarning($"{_mod}: '{text}' is not a key name for '{b.Name}' (use names like KeypadDivide, F8, Alpha0; None = off). The key is off.");
            return b.ParsedText;
        }
        catch
        {
            return KeyCode.None;
        }
    }

    /// <summary>KeyCode from a name ("KeypadDivide", "f8", "Alpha0"); "", "None", "Off" = None. ok=false for unknown names.</summary>
    internal static KeyCode ParseKey(string text, out bool ok)
    {
        ok = true;
        string t = (text ?? "").Trim();
        if (t.Length == 0 || t.Equals("None", StringComparison.OrdinalIgnoreCase) || t.Equals("Off", StringComparison.OrdinalIgnoreCase))
            return KeyCode.None;
        if (!int.TryParse(t, out _) && Enum.TryParse(t, true, out KeyCode k) && Enum.IsDefined(typeof(KeyCode), k)) return k;
        ok = false;
        return KeyCode.None;
    }

    /// <summary>Player-facing key name ("Numpad /", "Numpad 4", "F8").</summary>
    internal static string KeyName(KeyCode k)
    {
        switch (k)
        {
            case KeyCode.KeypadMultiply: return "Numpad *";
            case KeyCode.KeypadDivide: return "Numpad /";
            case KeyCode.KeypadPlus: return "Numpad +";
            case KeyCode.KeypadMinus: return "Numpad -";
            case KeyCode.KeypadPeriod: return "Numpad .";
            case KeyCode.KeypadEnter: return "Numpad Enter";
            case KeyCode.None: return "(no key)";
        }
        if (k >= KeyCode.Keypad0 && k <= KeyCode.Keypad9) return "Numpad " + (k - KeyCode.Keypad0);
        if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) return "" + (k - KeyCode.Alpha0) + " (top row)";
        return k.ToString();
    }

    /// <summary>
    /// Moves players off an old default key (e.g. a key the game turned out to use). Returns true if the value changed.
    /// Call right after Config.Bind. Never touches a key the player picked themselves (anything not in oldDefaults).
    /// </summary>
    internal static bool MoveOldDefault(ConfigEntry<KeyCode> entry, KeyCode newDefault, ManualLogSource log, string mod, params KeyCode[] oldDefaults)
    {
        if (entry == null || oldDefaults == null) return false;
        foreach (var old in oldDefaults)
        {
            if (entry.Value != old || old == newDefault) continue;
            log?.LogInfo($"{mod}: {entry.Definition.Key} moved from {KeyName(old)} to {KeyName(newDefault)} (the old default clashed).");
            entry.Value = newDefault;
            return true;
        }
        return false;
    }

#pragma warning disable CS0618 // IL2CPP interop marks some legacy Input members obsolete; they are what the game uses.
    static bool DefaultIsHeld(KeyCode k) => Input.GetKey(k);
#pragma warning restore CS0618
    static float DefaultNow() => Time.unscaledTime;
}
