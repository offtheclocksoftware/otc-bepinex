// otc_bepinex - Off the Clock shared BepInEx helpers. README.md next to src\.
// OtcSelfCheck: the C# twin of otc_selfcheck.lua v1.0 from ue4ss-mod-test-kit (copy in tests/lua/). Same line format,
// same semantics; tests/selfcheck_parity.lua proves it line by line.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using BepInEx.Logging;

namespace OffTheClock.Lib;

/// <summary>
/// In-game self-checks. The mod registers checks and reports what it READ BACK from the game:
/// <c>[SELFCHECK] &lt;Mod&gt; &lt;check&gt; PASS|FAIL|WAITING &lt;detail&gt;</c> (one log line each; a log parser can turn
/// them into a PASS/FAIL table at the end of a test session).
/// <code>
/// var SC = new OtcSelfCheck(Log, "MyMod");                    // mod id (one word)
/// SC.Expect("level_given", "start a run and press Numpad /");
/// SC.Expect("toggle_off", "press Numpad * once", optional: true);
/// SC.Expect("held", "hold it 3 s", grace: 3);                  // FAIL only after 3 misses
/// if (SC.Want("level_given")) SC.Check("level_given", lvl &gt; before, "level {0} -&gt; {1}", before, lvl);
/// SC.Pass(id, ...)  SC.Fail(id, ...)  SC.Wait(id, "reason")  SC.FailOpen("mod switched itself off")
/// </code>
/// Rules (same as the Lua module): checks only read, never write game state; no timers, keys or hooks of their own;
/// each check logs only when its state changes (max 5 lines per check, max 3 distinct wait reasons). Not thread-safe
/// by design: call it from the main thread (patch bodies, Update), like everything else in a plugin.
/// </summary>
internal sealed class OtcSelfCheck
{
    internal const string Version = "1.0";
    internal const int MaxLinesPerCheck = 5;
    internal const int MaxWaitReasons = 3;
    internal const int MaxDetail = 300;

    sealed class Entry
    {
        public string State = "WAITING";
        public int Misses;
        public int Grace = 1;
        public int Lines;
        public readonly HashSet<string> Reasons = new HashSet<string>();
        public bool Optional;
    }

    readonly Dictionary<string, Entry> _checks = new Dictionary<string, Entry>();
    readonly List<string> _order = new List<string>();
    readonly Action<string> _sink;

    /// <summary>Mod id as written in each line (whitespace replaced by _).</summary>
    internal string Mod { get; }

    /// <summary>Writes lines with log.LogInfo (BepInEx adds "[Info   :&lt;source&gt;] ", which a parser should skip).</summary>
    internal OtcSelfCheck(ManualLogSource log, string mod) : this(mod, log == null ? (Action<string>)null : log.LogInfo) { }

    /// <summary>Custom sink (tests, or a different log level). A null sink = quiet (counts only).</summary>
    internal OtcSelfCheck(string mod, Action<string> sink)
    {
        Mod = SafeId(string.IsNullOrEmpty(mod) ? "Mod" : mod);
        _sink = sink;
    }

    /// <summary>Registers a check. how = what the tester does in game to reach it (test sheet + report). Returns this.</summary>
    internal OtcSelfCheck Expect(string id, string how, bool optional = false, int grace = 1)
    {
        id = SafeId(id);
        if (_checks.ContainsKey(id)) return this;
        _checks[id] = new Entry { Grace = Math.Max(1, grace), Optional = optional };
        _order.Add(id);
        Emit(id, "WAITING", (optional ? "(optional) " : "") + "how: " + (how ?? "?"));
        return this;
    }

    /// <summary>True while the check is registered and has no result yet: the cheap gate for extra read-backs.</summary>
    internal bool Want(string id) => _checks.TryGetValue(id ?? "", out Entry c) && c.State == "WAITING";

    /// <summary>"WAITING", "PASS", "FAIL", or null if the check was never registered.</summary>
    internal string State(string id) => _checks.TryGetValue(id ?? "", out Entry c) ? c.State : null;

    /// <summary>Registered check ids, in registration order.</summary>
    internal IReadOnlyList<string> Ids => _order;

    /// <summary>The read-back proved the effect. False if unknown id or already PASS.</summary>
    internal bool Pass(string id, string detail = null) => SetState(id, "PASS", detail, "FAIL", " (after an earlier FAIL)");

    internal bool Pass(string id, string format, params object[] args) => Pass(id, Fmt(format, args));

    /// <summary>The read-back proved the effect is NOT there (also after an earlier PASS).</summary>
    internal bool Fail(string id, string detail = null) => SetState(id, "FAIL", detail, "PASS", " (after an earlier PASS)");

    internal bool Fail(string id, string format, params object[] args) => Fail(id, Fmt(format, args));

    /// <summary>ok -> PASS. Not ok -> a miss; FAIL once grace misses were counted. No-op once the check has a result.</summary>
    internal bool Check(string id, bool ok, string detail = null)
    {
        if (!_checks.TryGetValue(id ?? "", out Entry c) || c.State != "WAITING") return false;
        if (ok) return Pass(id, detail);
        c.Misses++;
        if (c.Misses >= c.Grace)
            return Fail(id, string.Format(CultureInfo.InvariantCulture, "{0} ({1} time{2})", detail ?? "", c.Misses, c.Misses == 1 ? "" : "s"));
        return false;
    }

    internal bool Check(string id, bool ok, string format, params object[] args) => Check(id, ok, Fmt(format, args));

    /// <summary>Still waiting, with a reason. Each distinct reason is logged once (max 3 per check).</summary>
    internal bool Wait(string id, string reason)
    {
        if (!_checks.TryGetValue(id ?? "", out Entry c) || c.State != "WAITING") return false;
        string r = Clean(reason);
        if (c.Reasons.Contains(r) || c.Reasons.Count >= MaxWaitReasons) return false;
        c.Reasons.Add(r);
        Emit(id, "WAITING", r);
        return true;
    }

    internal bool Wait(string id, string format, params object[] args) => Wait(id, Fmt(format, args));

    /// <summary>Every check without a result -> FAIL with this reason (the mod gave up / a feature was disabled). Returns how many.</summary>
    internal int FailOpen(string reason)
    {
        int n = 0;
        foreach (string id in _order)
        {
            if (_checks[id].State != "WAITING") continue;
            Fail(id, reason ?? "");
            n++;
        }
        return n;
    }

    internal int FailOpen(string format, params object[] args) => FailOpen(Fmt(format, args));

    bool SetState(string id, string state, string detail, string earlier, string earlierText)
    {
        if (!_checks.TryGetValue(id ?? "", out Entry c) || c.State == state) return false;
        string was = c.State;
        c.State = state;
        Emit(id, state, (detail ?? "") + (was == earlier ? earlierText : ""));
        return true;
    }

    void Emit(string id, string state, string detail)
    {
        if (_checks.TryGetValue(id, out Entry c))
        {
            if (c.Lines >= MaxLinesPerCheck) return;
            c.Lines++;
        }
        string line = "[SELFCHECK] " + Mod + " " + id + " " + state;
        if (!string.IsNullOrEmpty(detail)) line += " " + Clean(detail);
        try { _sink?.Invoke(line); } catch { /* logging must never break the mod */ }
    }

    // One line per call; newlines inside a detail would break the report parser. Same limits as the Lua module.
    internal static string Clean(string s)
    {
        s = Regex.Replace(s ?? "", "[\r\n]+", " ");
        if (s.Length > MaxDetail) s = s.Substring(0, MaxDetail - 3) + "...";
        return s;
    }

    internal static string SafeId(string id) => Regex.Replace(id ?? "?", @"\s+", "_");

    static string Fmt(string format, object[] args)
    {
        if (format == null) return "";
        if (args == null || args.Length == 0) return format;
        try { return string.Format(CultureInfo.InvariantCulture, format, args); }
        catch (FormatException) { return format + " [format error]"; }
    }
}
