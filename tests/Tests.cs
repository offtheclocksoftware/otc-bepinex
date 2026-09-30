// Plain console test runner for otc_bepinex (no test framework package needed). Exit code 0 = all passed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using OffTheClock.Lib;
using UnityEngine;

namespace OtcLib.Tests;

internal static class Program
{
    static int _passed, _failed, _failedTests;
    static readonly List<string> Failures = new List<string>();
    static readonly List<string> Notes = new List<string>();
    static string _current = "";

    static int Main()
    {
        Run("keys: one action per press, held key does not repeat", Keys_Edge);
        Run("keys: key held at start does not fire", Keys_HeldAtStart);
        Run("keys: typing hook suppresses and never fires later", Keys_Typing);
        Run("keys: cooldown", Keys_Cooldown);
        Run("keys: config change re-arms without firing", Keys_ConfigChange);
        Run("keys: string entries, bad names warn once", Keys_StringEntry);
        Run("keys: CanAct gate and OnBlocked", Keys_CanAct);
        Run("keys: keyboard exception switches keys off once", Keys_InputThrows);
        Run("keys: action exception is guarded, others keep working", Keys_ActionThrows);
        Run("keys: LogActive names keys and warns on duplicates", Keys_LogActive);
        Run("keys: KeyName / ParseKey / MoveOldDefault", Keys_Names);
        Run("host: generated behaviour drives keys; bad callback dropped after 3", Host_Generated);
        Run("selfcheck: exact line format", SelfCheck_Format);
        Run("selfcheck: line-by-line parity with otc_selfcheck.lua", SelfCheck_LuaParity);
        Run("guard: first N per site, then quiet", Guard_Throttle);
        Run("guard: Guard<T> fallback and __runOriginal prefix helper", Guard_FuncAndPrefix);
        Run("patcher: optional skip, required abort, applied lines", Patcher_All);
        Run("notice: show, expire, disabled, self-disable on error", Notice_All);
        Run("runflag: idempotent, read-back, never cleared", RunFlag_All);
        Run("online: fail-safe and logged transitions", Online_All);

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failedTests} failed ({_failed} failed assertion(s))");
        foreach (var n in Notes) Console.WriteLine("  note " + n);
        foreach (var f in Failures) Console.WriteLine("  FAIL " + f);
        return _failed == 0 && _failedTests == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        _current = name;
        OtcHost.ResetForTests();
        OtcPatch.ResetForTests();
        Input.Held.Clear();
        Input.Throw = false;
        GUI.Throw = false;
        Time.unscaledTime = 0f;
        int before = _failed;
        try { test(); }
        catch (Exception ex) { Fail("threw " + ex); }
        if (_failed == before) { _passed++; Console.WriteLine("ok   " + name); }
        else { _failedTests++; Console.WriteLine("FAIL " + name); }
    }

    static void Fail(string msg) { _failed++; Failures.Add(_current + ": " + msg); }
    static void True(bool c, string msg) { if (!c) Fail(msg); }
    static void Eq<T>(T expected, T actual, string msg)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) Fail($"{msg}: expected <{expected}> got <{actual}>");
    }

    static (OtcKeys keys, ManualLogSource log) NewKeys()
    {
        var log = new ManualLogSource("Test Mod");
        OtcPatch.Init(log, "TestMod");
        var keys = new OtcKeys(log, "TestMod") { Now = () => Time.unscaledTime };
        return (keys, log);
    }

    static void Frame(OtcKeys k, params KeyCode[] held)
    {
        Input.Held.Clear();
        foreach (var h in held) Input.Held.Add(h);
        k.Tick();
        Time.unscaledTime += 1f / 60f;
    }

    // ------------------------------------------------------------------ keys
    static void Keys_Edge()
    {
        var (k, _) = NewKeys();
        var e = new ConfigEntry<KeyCode>("Controls", "One", KeyCode.KeypadDivide);
        int fired = 0;
        k.Add("one", e, () => fired++);
        Frame(k);                                        // arms
        Frame(k, KeyCode.KeypadDivide);                  // press
        Eq(1, fired, "first press");
        for (int i = 0; i < 30; i++) Frame(k, KeyCode.KeypadDivide);   // held for half a second
        Eq(1, fired, "held does not repeat");
        k.Tick(); k.Tick();                              // extra ticks in the same frame
        Eq(1, fired, "extra ticks in one frame");
        Frame(k);                                        // release
        Frame(k, KeyCode.KeypadDivide);                  // second press
        Eq(2, fired, "second press");
        Frame(k, KeyCode.KeypadMultiply);                // other key
        Eq(2, fired, "other key");
        Eq(2, k.Presses("one"), "Presses()");
    }

    static void Keys_HeldAtStart()
    {
        var (k, _) = NewKeys();
        int fired = 0;
        k.Add("one", new ConfigEntry<KeyCode>("C", "K", KeyCode.F8), () => fired++);
        Frame(k, KeyCode.F8);                            // already held on the first read
        Frame(k, KeyCode.F8);
        Eq(0, fired, "held at start");
        Frame(k);
        Frame(k, KeyCode.F8);
        Eq(1, fired, "after release + press");
    }

    static void Keys_Typing()
    {
        var (k, _) = NewKeys();
        bool typing = false;
        int fired = 0;
        k.IsTyping = () => typing;
        k.Add("one", new ConfigEntry<KeyCode>("C", "K", KeyCode.Keypad4), () => fired++);
        Frame(k);
        typing = true;
        Frame(k, KeyCode.Keypad4);                       // pressed while typing
        typing = false;
        Frame(k, KeyCode.Keypad4);                       // still held after typing ends
        Eq(0, fired, "typed press never fires");
        Frame(k);
        Frame(k, KeyCode.Keypad4);
        Eq(1, fired, "normal press after typing");
        k.IsTyping = () => throw new InvalidOperationException("ui gone");
        Frame(k);
        Frame(k, KeyCode.Keypad4);
        Eq(2, fired, "typing hook exception = not typing");
    }

    static void Keys_Cooldown()
    {
        var (k, _) = NewKeys();
        int fired = 0;
        k.Add("one", new ConfigEntry<KeyCode>("C", "K", KeyCode.F8), () => fired++, cooldown: 1.0f);
        Frame(k);
        Frame(k, KeyCode.F8); Frame(k); Frame(k, KeyCode.F8); Frame(k);
        Eq(1, fired, "second press inside the cooldown ignored");
        Time.unscaledTime += 1.1f;
        Frame(k, KeyCode.F8);
        Eq(2, fired, "press after the cooldown");
    }

    static void Keys_ConfigChange()
    {
        var (k, _) = NewKeys();
        var e = new ConfigEntry<KeyCode>("C", "K", KeyCode.F8);
        int fired = 0;
        k.Add("one", e, () => fired++);
        Frame(k);
        Frame(k, KeyCode.F9);
        e.Value = KeyCode.F9;                            // player rebinds to a key held right now
        Frame(k, KeyCode.F9);
        Eq(0, fired, "rebind to a held key does not fire");
        Frame(k);
        Frame(k, KeyCode.F9);
        Eq(1, fired, "new key works");
        e.Value = KeyCode.None;
        Frame(k, KeyCode.F9);
        Eq(1, fired, "None = off");
    }

    static void Keys_StringEntry()
    {
        var (k, log) = NewKeys();
        var e = new ConfigEntry<string>("C", "K", "keypadmultiply");
        int fired = 0;
        k.Add("many", e, () => fired++);
        Frame(k);
        Frame(k, KeyCode.KeypadMultiply);
        Eq(1, fired, "case-insensitive name");
        e.Value = "NotAKey";
        for (int i = 0; i < 5; i++) Frame(k, KeyCode.KeypadMultiply);
        Eq(1, log.Count(LogLevel.Warning, "is not a key name"), "bad name warns once");
        e.Value = "Off";
        Frame(k);
        Eq(KeyCode.None, k.KeyOf("many"), "Off = None");
        e.Value = "F2";
        Frame(k); Frame(k, KeyCode.F2);
        Eq(2, fired, "back to a valid key");
    }

    static void Keys_CanAct()
    {
        var (k, _) = NewKeys();
        bool inRun = false;
        int fired = 0; var blocked = new List<string>();
        k.CanAct = () => inRun;
        k.OnBlocked = n => blocked.Add(n);
        k.Add("one", new ConfigEntry<KeyCode>("C", "K", KeyCode.F8), () => fired++);
        Frame(k); Frame(k, KeyCode.F8); Frame(k);
        Eq(0, fired, "blocked outside a run");
        Eq(1, blocked.Count, "OnBlocked called once per press");
        inRun = true;
        Frame(k, KeyCode.F8);
        Eq(1, fired, "fires in a run");
    }

    static void Keys_InputThrows()
    {
        var (k, log) = NewKeys();
        int fired = 0;
        k.Add("one", new ConfigEntry<KeyCode>("C", "K", KeyCode.F8), () => fired++);
        Frame(k);
        Input.Throw = true;
        for (int i = 0; i < 10; i++) Frame(k, KeyCode.F8);
        True(k.Broken, "keys switched off");
        Eq(1, log.Count(LogLevel.Warning, "key reading stopped"), "one warning");
        Input.Throw = false;
        Frame(k, KeyCode.F8);
        Eq(0, fired, "stays off");
    }

    static void Keys_ActionThrows()
    {
        var (k, log) = NewKeys();
        int other = 0;
        k.Add("bad", new ConfigEntry<KeyCode>("C", "A", KeyCode.F1), () => throw new NullReferenceException("game object gone"));
        k.Add("good", new ConfigEntry<KeyCode>("C", "B", KeyCode.F2), () => other++);
        Frame(k);
        for (int i = 0; i < 6; i++) { Frame(k, KeyCode.F1, KeyCode.F2); Frame(k); }
        Eq(6, other, "other key keeps working");
        Eq(3, log.Count(LogLevel.Warning, "key bad failed"), "first 3 errors logged, then quiet");
        Eq(6, OtcPatch.ErrorCount("key bad"), "all 6 counted");
        True(!k.Broken, "keys not switched off by an action error");
    }

    static void Keys_LogActive()
    {
        var (k, log) = NewKeys();
        k.Add("one level", new ConfigEntry<KeyCode>("C", "A", KeyCode.KeypadDivide), null);
        k.Add("many levels", new ConfigEntry<KeyCode>("C", "B", KeyCode.KeypadMultiply), null);
        k.Add("off", new ConfigEntry<KeyCode>("C", "C", KeyCode.None), null);
        k.Add("dup", new ConfigEntry<KeyCode>("C", "D", KeyCode.KeypadDivide), null);
        k.LogActive();
        True(log.Lines.Any(l => l.Level == LogLevel.Info && l.Text == "TestMod: keys - Numpad / = one level, Numpad * = many levels, Numpad / = dup."),
             "active keys line: " + string.Join(" | ", log.Lines.Select(l => l.Text)));
        Eq(1, log.Count(LogLevel.Warning, "is set for both 'one level' and 'dup'"), "duplicate warning");
    }

    static void Keys_Names()
    {
        Eq("Numpad 4", OtcKeys.KeyName(KeyCode.Keypad4), "Keypad4");
        Eq("Numpad .", OtcKeys.KeyName(KeyCode.KeypadPeriod), "KeypadPeriod");
        Eq("0 (top row)", OtcKeys.KeyName(KeyCode.Alpha0), "Alpha0");
        Eq("F8", OtcKeys.KeyName(KeyCode.F8), "F8");
        Eq(KeyCode.F8, OtcKeys.ParseKey(" f8 ", out bool ok1), "parse f8"); True(ok1, "f8 ok");
        Eq(KeyCode.None, OtcKeys.ParseKey("289", out bool ok2), "numbers rejected"); True(!ok2, "289 not ok");
        Eq(KeyCode.None, OtcKeys.ParseKey("", out bool ok3), "empty"); True(ok3, "empty ok");
        var log = new ManualLogSource("T");
        var e = new ConfigEntry<KeyCode>("Controls", "LevelUpKey", KeyCode.Keypad1);
        True(OtcKeys.MoveOldDefault(e, KeyCode.KeypadDivide, log, "TestMod", KeyCode.Keypad1, KeyCode.PageUp), "moved");
        Eq(KeyCode.KeypadDivide, e.Value, "new default");
        var mine = new ConfigEntry<KeyCode>("Controls", "LevelUpKey", KeyCode.F5);
        True(!OtcKeys.MoveOldDefault(mine, KeyCode.KeypadDivide, log, "TestMod", KeyCode.Keypad1), "player's own key kept");
        Eq(KeyCode.F5, mine.Value, "F5 kept");
    }

    // ------------------------------------------------------------------ host
    sealed class FakePlugin : BaseUnityPlugin { }

    static void Host_Generated()
    {
        var log = new ManualLogSource("Test Mod");
        OtcPatch.Init(log, "TestMod");
        var plugin = new FakePlugin();
        True(OtcHost.Start(plugin, log, "TestMod"), "Start attaches the generated behaviour");
        var comp = plugin.gameObject.Components.SingleOrDefault();
        True(comp != null, "component added");
        Eq("OtcGenerated.OtcLib_Tests.OtcHostBehaviour", comp?.GetType().FullName, "unique per-assembly name");
        MethodInfo update = comp?.GetType().GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo gui = comp?.GetType().GetMethod("OnGUI", BindingFlags.Instance | BindingFlags.NonPublic);
        True(update != null && gui != null, "Update and OnGUI generated");

        var keys = new OtcKeys(log, "TestMod") { Now = () => Time.unscaledTime };
        int fired = 0;
        keys.Add("one", new ConfigEntry<KeyCode>("C", "K", KeyCode.F8), () => fired++);
        keys.Start();
        True(log.Lines.Any(l => l.Text == "TestMod: keys - F8 = one."), "Start logs active keys");
        int bad = 0;
        OtcHost.OnUpdate("bad", () => { bad++; throw new Exception("boom"); });
        void UnityFrame(params KeyCode[] held)
        {
            Input.Held.Clear();
            foreach (var h in held) Input.Held.Add(h);
            update.Invoke(comp, null);
            gui.Invoke(comp, null);
        }
        UnityFrame(); UnityFrame(KeyCode.F8); UnityFrame(KeyCode.F8); UnityFrame(); UnityFrame(KeyCode.F8);
        Eq(2, fired, "keys driven by the generated Update");
        Eq(3, bad, "bad callback dropped after 3 errors");
        Eq(1, log.Count(LogLevel.Warning, "switched off after 3 errors"), "drop logged once");
        Eq(5L, OtcHost.Frames, "frames counted");
    }

    // ------------------------------------------------------------------ selfcheck
    static void SelfCheck_Format()
    {
        var lines = new List<string>();
        var sc = new OtcSelfCheck("Level Up Now", lines.Add);
        sc.Expect("level_given", "start a run and press Numpad /");
        sc.Expect("toggle", "press Numpad * once", optional: true);
        True(sc.Want("level_given"), "Want while waiting");
        True(sc.Check("level_given", true, "level {0} -> {1} ({2:0.0} XP)", 3, 4, 12.5f), "check ok");
        True(!sc.Want("level_given"), "Want false after result");
        Eq("[SELFCHECK] Level_Up_Now level_given WAITING how: start a run and press Numpad /", lines[0], "expect line");
        Eq("[SELFCHECK] Level_Up_Now toggle WAITING (optional) how: press Numpad * once", lines[1], "optional line");
        Eq("[SELFCHECK] Level_Up_Now level_given PASS level 3 -> 4 (12.5 XP)", lines[2], "pass line (invariant culture)");
        Eq("PASS", sc.State("level_given"), "state");
        Eq(null, sc.State("nope"), "unknown state");
        True(!sc.Pass("nope"), "unknown id");
        sc.Pass("toggle", "bad {9}", 1);
        Eq("[SELFCHECK] Level_Up_Now toggle PASS bad {9} [format error]", lines[3], "format error kept, no throw");
    }

    // The repository root: the folder that holds OtcBepInEx.props (found by walking up from the test binary).
    static readonly string RepoDir = FindRepoDir();

    static string FindRepoDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "OtcBepInEx.props"))) d = d.Parent;
        if (d == null) throw new DirectoryNotFoundException("OtcBepInEx.props not found above " + AppContext.BaseDirectory);
        return d.FullName;
    }

    static List<string> ParityScenarioCSharp()
    {
        var lines = new List<string>();
        var SC = new OtcSelfCheck("Parity Mod", lines.Add);
        SC.Expect("a", "load a save");
        SC.Expect("b", "press Num 4", optional: true);
        SC.Expect("c", "hold it", grace: 3);
        SC.Expect("a", "dup ignored");
        SC.Wait("a", "not in a run");
        SC.Wait("a", "not in a run");
        SC.Wait("a", "menu");
        SC.Wait("a", "paused");
        SC.Wait("a", "fourth reason");
        SC.Check("c", false, "miss one");
        SC.Check("c", false, "miss two");
        SC.Check("c", false, "miss three");
        SC.Pass("c", "later ok");
        SC.Pass("a", "value 5");
        SC.Pass("a", "again");
        SC.Fail("a", "broke");
        SC.Pass("a", "x");
        SC.Expect("d", "multi\nline");
        SC.FailOpen("gave up\r\nnow");
        SC.Expect("long id with spaces", new string('x', 400));
        SC.Check("unknown", true, "nope");
        SC.Wait("b", "after result");
        SC.Expect("e", "grace one");
        SC.Check("e", false, "missed");
        SC.Expect("f", "direct");
        SC.Check("f", true, "ok at once");
        SC.Check("f", false, "ignored after result");
        return lines;
    }

    static (int code, string stdout, string stderr) Exec(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
        p.WaitForExit(60000);
        return (p.ExitCode, o, e);
    }

    // Runs tests/selfcheck_parity.lua with the Lua interpreter. Lua missing: skipped with a note, unless the environment
    // variable OTC_REQUIRE_LUA=1 is set (CI sets it, so the parity check can never silently disappear there).
    static void SelfCheck_LuaParity()
    {
        string module = Path.Combine(RepoDir, "tests", "lua", "otc_selfcheck.lua");
        string script = Path.Combine(RepoDir, "tests", "selfcheck_parity.lua");
        (int code, string stdout, string stderr)? r = null;
        foreach (var exe in new[] { "lua", "lua5.4", "lua54" })
        {
            try { r = Exec(exe, script, module); break; }
            catch (System.ComponentModel.Win32Exception) { }       // not on PATH: try the next name
        }
        if (r == null)
        {
            if (Environment.GetEnvironmentVariable("OTC_REQUIRE_LUA") == "1") Fail("Lua 5.4 not found on PATH (lua, lua5.4, lua54)");
            else Notes.Add("Lua parity skipped: no Lua 5.4 on PATH (lua, lua5.4 or lua54). Install Lua to run it.");
            return;
        }
        var (code, o, e) = r.Value;
        Eq(0, code, "lua exit code (" + e + ")");
        var lua = o.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToList();
        var cs = ParityScenarioCSharp();
        Eq(lua.Count, cs.Count, "line count");
        for (int i = 0; i < Math.Min(lua.Count, cs.Count); i++)
            if (lua[i] != cs[i]) Fail($"line {i + 1} differs:\n     lua: {lua[i]}\n      c#: {cs[i]}");
        Eq(17, cs.Count, "scenario line count (5 per check max, merged waits)");
    }

    // ------------------------------------------------------------------ guard
    static void Guard_Throttle()
    {
        var log = new ManualLogSource("T");
        OtcPatch.Init(log, "TestMod", maxLogsPerSite: 3);
        for (int i = 0; i < 5; i++) OtcPatch.Guard(() => throw new InvalidOperationException("bad " + i), "site A");
        OtcPatch.Guard(() => throw new ArgumentException("other"), "site B");
        Eq(5, OtcPatch.ErrorCount("site A"), "counted");
        Eq(3, log.Count(LogLevel.Warning, "site A failed"), "3 logged for A");
        Eq(1, log.Count(LogLevel.Warning, "site A failed (3) - further errors here are not logged."), "quiet notice");
        True(log.Lines.First(l => l.Text.Contains("site A failed (1)")).Text.Contains(" at "), "first one has a stack trace");
        True(!log.Lines.First(l => l.Text.Contains("site A failed (2)")).Text.Contains(" at "), "later ones only the message");
        Eq(1, log.Count(LogLevel.Warning, "site B failed"), "site B separate");
        Eq(6, OtcPatch.TotalErrors, "total");
        int ran = 0;
        OtcPatch.Guard(() => ran++);
        Eq(1, ran, "happy path runs");
        OtcPatch.Guard(() => throw new Exception("auto site"));
        True(log.Lines.Any(l => l.Text.Contains("Tests.Guard_Throttle:")), "automatic site name = file.member:line");
    }

    static void Guard_FuncAndPrefix()
    {
        var log = new ManualLogSource("T");
        OtcPatch.Init(log, "TestMod");
        Eq(7, OtcPatch.Guard(() => 7, fallback: -1), "value");
        Eq(-1, OtcPatch.Guard<int>(() => throw new Exception("x"), "f", -1), "fallback");
        Eq(1, OtcPatch.ErrorCount("f"), "site bound as site, not as fallback");
        int body = 0;
        True(!OtcPatch.Prefix(false, () => { body++; return false; }), "another prefix skipped: stays skipped");
        Eq(0, body, "body not run when the original is already skipped");
        True(!OtcPatch.Prefix(true, () => { body++; return false; }), "we replace");
        True(OtcPatch.Prefix(true, () => { body++; return true; }), "we let it run");
        True(OtcPatch.Prefix(true, () => throw new Exception("boom"), "prefix site"), "exception = original runs");
        Eq(2, body, "body ran twice");
        Eq(1, OtcPatch.ErrorCount("prefix site"), "prefix error counted");
    }

    // ------------------------------------------------------------------ patcher
    internal class GameThing
    {
        public int Value() => 1;
        public int Value(int x) => x;
        public virtual void Init() { }
        public void Once() { }
    }

    internal class GameSub : GameThing { }

    internal static class ThingPatches
    {
        static void Post() { }
        static bool Pre() => true;
    }

    [StubPatchFails] internal static class BrokenPatchClass { }
    internal static class GoodPatchClass { }

    static void Patcher_All()
    {
        var log = new ManualLogSource("T");
        var lines = new List<string>();
        var sc = new OtcSelfCheck("TestMod", lines.Add);
        sc.Expect("works", "do it");
        var h = new Harmony("offtheclock.test");
        var p = new OtcPatcher(h, log, "TestMod", sc);
        True(p.Class(typeof(GoodPatchClass), required: true, feature: "guard"), "class applied");
        True(p.Method("once", typeof(GameThing), nameof(GameThing.Once), null, typeof(ThingPatches), postfix: "Post"), "method applied");
        True(p.Method("by name", typeof(GameThing).FullName, nameof(GameThing.Value), new[] { typeof(int) }, typeof(ThingPatches), prefix: "Pre"), "by type name + args");
        True(!p.Method("ambiguous", typeof(GameThing), nameof(GameThing.Value), null, typeof(ThingPatches), postfix: "Post"), "ambiguous overload skipped");
        True(!p.Method("inherited only", typeof(GameSub), nameof(GameThing.Init), Type.EmptyTypes, typeof(ThingPatches), postfix: "Post", declaredOnly: true), "declaredOnly: not declared on the subclass");
        True(p.Method("inherited ok", typeof(GameSub), nameof(GameThing.Init), Type.EmptyTypes, typeof(ThingPatches), postfix: "Post"), "inherited found without declaredOnly");
        True(!p.Method("missing type", "Game.NoSuchClass", "X", null, typeof(ThingPatches), postfix: "Post"), "missing type");
        True(!p.Method("missing method", typeof(GameThing), "Renamed", null, typeof(ThingPatches), postfix: "Post"), "missing method");
        True(!p.Method("our typo", typeof(GameThing), nameof(GameThing.Once), null, typeof(ThingPatches), postfix: "Psot"), "missing patch method");
        True(!p.Class(typeof(BrokenPatchClass), feature: "hover guard"), "failing class");
        Eq(4, p.Applied.Count, "applied count");
        Eq(6, p.Skipped.Count, "skipped count");
        Eq(1, log.Count(LogLevel.Info, "TestMod: once - patched GameThing.Once."), "one INFO per applied patch");
        Eq(1, log.Count(LogLevel.Warning, "TestMod: missing type could not be applied - this part is skipped (game update?): TypeLoadException: game class Game.NoSuchClass not found"), "missing type warning");
        True(p.Finish(), "only optional parts failed -> Finish true");
        True(!h.UnpatchedSelf, "nothing undone");
        True(log.Lines.Any(l => l.Text.StartsWith("TestMod: 4/10 patch step(s) applied; skipped (optional): ambiguous")), "summary line");
        Eq("WAITING", sc.State("works"), "self-check untouched");

        var log2 = new ManualLogSource("T");
        var h2 = new Harmony("offtheclock.test2");
        var p2 = new OtcPatcher(h2, log2, "TestMod", sc);
        p2.Method("slots", typeof(GameThing), nameof(GameThing.Once), null, typeof(ThingPatches), postfix: "Post");
        p2.Class(typeof(BrokenPatchClass), required: true, feature: "cheated-run guard");
        True(!p2.Finish(), "required failed -> Finish false");
        True(h2.UnpatchedSelf, "UnpatchSelf called (never UnpatchAll)");
        Eq(1, log2.Count(LogLevel.Error, "REQUIRED part"), "required failure is an ERROR");
        Eq(1, log2.Count(LogLevel.Error, "required part missing: cheated-run guard - the mod does nothing"), "abort line");
        Eq("FAIL", sc.State("works"), "open self-checks failed");
        True(lines.Last().EndsWith("works FAIL required part missing: cheated-run guard"), "fail_open reason: " + lines.Last());
    }

    // ------------------------------------------------------------------ notice
    static void Notice_All()
    {
        var log = new ManualLogSource("T");
        bool enabled = true;
        var n = new OtcNotice(log, "TestMod", () => enabled) { Now = () => Time.unscaledTime };
        n.Start();
        GUI.Boxes = 0; GUI.Labels = 0;
        OtcHost.RunGui();
        Eq(0, GUI.Labels, "nothing before Show");
        n.Show("Gold x5");
        Eq("Gold x5", n.Current, "current text");
        OtcHost.RunGui();
        Eq(1, GUI.Labels, "drawn via OtcHost.RunGui");
        Eq("Gold x5", GUI.LastLabel, "label text");
        Eq(2, GUI.Boxes, "box drawn twice");
        enabled = false;
        OtcHost.RunGui();
        Eq(1, GUI.Labels, "config switch off -> not drawn");
        enabled = true;
        Time.unscaledTime += 3f;
        OtcHost.RunGui();
        Eq(1, GUI.Labels, "expired after 2.5 s");
        Eq(null, n.Current, "no current text");
        n.Show("XP x3");
        GUI.Throw = true;
        OtcHost.RunGui(); OtcHost.RunGui();
        True(n.Broken, "self-disabled");
        Eq(1, log.Count(LogLevel.Warning, "on-screen notice unavailable"), "one warning");
        GUI.Throw = false;
        OtcHost.RunGui();
        Eq(1, GUI.Labels, "stays off");
    }

    // ------------------------------------------------------------------ run flags / online
    sealed class FakeRun { public bool Cheated; public bool Sticky; }

    static void RunFlag_All()
    {
        var log = new ManualLogSource("T");
        FakeRun run = null;
        var flag = new OtcRunFlag(log, "TestMod", "BattleSaveData.Cheated",
            read: () => run != null && run.Cheated, set: () => { if (!run.Sticky) run.Cheated = true; },
            effect: "the game will not post it to the leaderboards", available: () => run != null);
        True(!flag.Mark("no run"), "no run -> false");
        run = new FakeRun();
        True(flag.Mark("levels given"), "marked");
        True(flag.Mark("again"), "idempotent");
        Eq(1, flag.TimesMarked, "logged once per run");
        Eq(1, log.Count(LogLevel.Message, "the game's BattleSaveData.Cheated flag is now set (levels given); the game will not post it to the leaderboards. The mod never clears it."), "message line");
        run = new FakeRun();                             // next run: new flag, marked again
        True(flag.Mark("next run"), "next run marked");
        Eq(2, flag.TimesMarked, "second run");
        run = new FakeRun { Sticky = true };             // setter has no effect (game update)
        True(!flag.Mark("x"), "read-back failure -> false");
        Eq(1, log.Count(LogLevel.Error, "could not set the game's BattleSaveData.Cheated flag, so the mod leaves this unchanged."), "error line");
        var boom = new OtcRunFlag(log, "TestMod", "F", () => throw new MissingFieldException("gone"), () => { }, "");
        True(!boom.Mark("x"), "exception -> false");
        True(!boom.IsMarked, "IsMarked false on error");
        True(typeof(OtcRunFlag).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                 .All(m => !m.Name.Contains("Clear") && !m.Name.Contains("Unmark")), "no clear API exists");
    }

    static void Online_All()
    {
        var log = new ManualLogSource("T");
        OtcOnline.Configure(log, "TestMod", null);
        True(!OtcOnline.IsOnline, "not configured -> offline");
        bool online = false;
        OtcOnline.Configure(log, "TestMod", () => online, "online co-op");
        True(!OtcOnline.IsOnline, "offline");
        Eq(0, log.Lines.Count, "offline at start is not logged");
        online = true;
        True(OtcOnline.IsOnline && OtcOnline.IsOnline, "online");
        Eq(1, log.Count(LogLevel.Info, "online co-op detected"), "logged once");
        online = false;
        True(!OtcOnline.IsOnline, "offline again");
        Eq(1, log.Count(LogLevel.Info, "offline again"), "back offline logged");
        OtcOnline.Configure(log, "TestMod", () => throw new NullReferenceException("MultiplayerManager gone"), "online co-op");
        True(OtcOnline.IsOnline && OtcOnline.IsOnline, "check failure -> online (safe side)");
        Eq(1, log.Count(LogLevel.Warning, "check failed"), "one warning");
    }
}
