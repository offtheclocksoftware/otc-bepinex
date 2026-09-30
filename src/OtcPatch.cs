// otc_bepinex - Off the Clock shared BepInEx helpers. README.md next to src\.
// OtcPatcher: fail-safe Harmony patching, one feature at a time (rule 3). OtcPatch: throttled exception guards (rule 2)
// and a __runOriginal-aware prefix helper (rule 4).
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;

namespace OffTheClock.Lib;

/// <summary>
/// Applies Harmony patches one feature at a time. Each call has its own try/catch: a missing class or method (game
/// update) or a patch that throws skips only that feature, with one WARNING; each applied patch logs one INFO line.
/// A feature flagged <c>required</c> that fails makes <see cref="Finish"/> undo this plugin's patches (UnpatchSelf,
/// never UnpatchAll) and return false, so the plugin can do nothing instead of half its job.
/// <code>
/// var p = new OtcPatcher(new Harmony(PluginInfo.PLUGIN_GUID), Log, "MyMod");
/// p.Class(typeof(RunGuard), required: true, feature: "cheated-run guard");      // [HarmonyPatch] classes
/// p.Class(typeof(Patches), required: true, feature: "slots");
/// p.Class(typeof(LevelUpGuard), feature: "level-up screen guard");               // optional
/// p.Method("co-op count", typeof(CharacterController), "UpdateMaxWeaponCount", Type.EmptyTypes,
///          typeof(Patches), postfix: nameof(Patches.AfterCoopCount));
/// if (!p.Finish()) return;                                                        // required part missing
/// </code>
/// Order matters: apply required features first. Put each feature in its own patch class, because Harmony's
/// PatchAll(Type) keeps the patches of that class that were applied before one of them failed.
/// </summary>
internal sealed class OtcPatcher
{
    readonly ManualLogSource _log;
    readonly string _mod;
    readonly OtcSelfCheck _selfCheck;
    readonly List<string> _applied = new List<string>();
    readonly List<string> _skipped = new List<string>();
    readonly List<string> _requiredMissing = new List<string>();

    internal OtcPatcher(Harmony harmony, ManualLogSource log, string mod, OtcSelfCheck selfCheck = null)
    {
        Harmony = harmony;
        _log = log;
        _mod = string.IsNullOrEmpty(mod) ? "Mod" : mod;
        _selfCheck = selfCheck;
    }

    internal Harmony Harmony { get; }
    internal IReadOnlyList<string> Applied => _applied;
    internal IReadOnlyList<string> Skipped => _skipped;
    internal IReadOnlyList<string> RequiredMissing => _requiredMissing;
    internal bool AllRequiredApplied => _requiredMissing.Count == 0;

    /// <summary>Harmony.PatchAll(patchClass) for a class with [HarmonyPatch] attributes. Returns true when applied.</summary>
    internal bool Class(Type patchClass, bool required = false, string feature = null)
    {
        string name = feature ?? (patchClass != null ? patchClass.Name : "?");
        return Try(name, () =>
        {
            if (patchClass == null) throw new ArgumentNullException(nameof(patchClass));
            Harmony.PatchAll(patchClass);
        }, required, "patched " + (patchClass != null ? patchClass.Name : "?"));
    }

    /// <summary>
    /// Patches type.method(args) with static methods of <paramref name="patchClass"/> named by prefix / postfix /
    /// finalizer (any may be null). args == null means "the only method with that name". declaredOnly = the method
    /// must be declared on that exact type (an override), not inherited (AccessTools.DeclaredMethod).
    /// </summary>
    internal bool Method(string feature, Type type, string method, Type[] args, Type patchClass,
        string prefix = null, string postfix = null, string finalizer = null, bool required = false, bool declaredOnly = false)
    {
        string where = (type != null ? type.Name : "?") + "." + method;
        return Try(feature, () =>
        {
            if (type == null) throw new MissingMemberException("game class not found (game update?)");
            MethodInfo target = declaredOnly
                ? (args == null ? AccessTools.DeclaredMethod(type, method) : AccessTools.DeclaredMethod(type, method, args))
                : (args == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args));
            if (target == null) throw new MissingMethodException($"game method {where} not found (game update?)");
            Harmony.Patch(target,
                prefix: Own(patchClass, prefix),
                postfix: Own(patchClass, postfix),
                finalizer: Own(patchClass, finalizer));
        }, required, "patched " + where);
    }

    /// <summary>Same as the Type overload, with the game class found by full name at run time (AccessTools.TypeByName).</summary>
    internal bool Method(string feature, string typeName, string method, Type[] args, Type patchClass,
        string prefix = null, string postfix = null, string finalizer = null, bool required = false, bool declaredOnly = false)
    {
        Type type = null;
        try { type = AccessTools.TypeByName(typeName); } catch { /* reported below as "not found" */ }
        if (type == null)
        {
            return Try(feature, () => throw new TypeLoadException($"game class {typeName} not found (game update?)"), required, null);
        }
        return Method(feature, type, method, args, patchClass, prefix, postfix, finalizer, required, declaredOnly);
    }

    /// <summary>Runs one patch step (any code) with its own try/catch and records the result.</summary>
    internal bool Try(string feature, Action apply, bool required = false, string appliedText = null)
    {
        feature = string.IsNullOrEmpty(feature) ? "patch" : feature;
        try
        {
            apply();
            _applied.Add(feature);
            _log?.LogInfo($"{_mod}: {feature} - {appliedText ?? "applied"}.");
            return true;
        }
        catch (Exception ex)
        {
            Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            _skipped.Add(feature);
            if (required) _requiredMissing.Add(feature);
            string msg = $"{_mod}: {feature} could not be applied - {(required ? "REQUIRED part" : "this part is skipped")} (game update?): {inner.GetType().Name}: {inner.Message}";
            if (required) _log?.LogError(msg); else _log?.LogWarning(msg);
            return false;
        }
    }

    /// <summary>
    /// Logs one summary line. If a required feature failed: undoes this plugin's patches (Harmony.UnpatchSelf), fails
    /// every open self-check with the reason, and returns false (the plugin should then return without doing anything).
    /// </summary>
    internal bool Finish()
    {
        int total = _applied.Count + _skipped.Count;
        if (_requiredMissing.Count > 0)
        {
            try { Harmony?.UnpatchSelf(); } catch (Exception ex) { _log?.LogWarning($"{_mod}: undoing patches failed: {ex.Message}"); }
            string why = "required part missing: " + string.Join(", ", _requiredMissing);
            _log?.LogError($"{_mod}: {why} - the mod does nothing this session (game update?).");
            _selfCheck?.FailOpen(why);
            return false;
        }
        _log?.LogInfo($"{_mod}: {_applied.Count}/{total} patch step(s) applied" +
                      (_skipped.Count > 0 ? "; skipped (optional): " + string.Join(", ", _skipped) + "." : "."));
        return true;
    }

    static HarmonyMethod Own(Type patchClass, string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (patchClass == null) throw new ArgumentNullException(nameof(patchClass));
        MethodInfo m = AccessTools.Method(patchClass, name);
        if (m == null) throw new MissingMethodException($"patch method {patchClass.Name}.{name} not found (bug in the mod)");
        return new HarmonyMethod(m);
    }
}

/// <summary>
/// Exception guards for patch bodies and callbacks. IL2CPP swallows exceptions that leave a patch and returns 0/null/false
/// to the game (rule 2), Mono lets them into the game's code; so every patch body catches. These helpers log the first
/// <see cref="MaxLogsPerSite"/> exceptions per call site (the first one with its stack trace), then go quiet.
/// <code>
/// OtcPatch.Init(Log, "MyMod");
/// static void Postfix(BuildingMgr __instance) =&gt; OtcPatch.Guard(() =&gt; Apply(__instance));
/// // A lambda can't use ref/out parameters (__result, ref args): catch explicitly there (zero allocations, too):
/// static void Postfix(ref int __result) { try { ... } catch (Exception ex) { OtcPatch.Report(ex, "slots postfix"); } }
/// static bool Prefix(bool __runOriginal, LevelUpUI __instance) =&gt; OtcPatch.Prefix(__runOriginal, () =&gt; { ...; return false; });
/// </code>
/// </summary>
internal static class OtcPatch
{
    sealed class Site
    {
        public int Count;
    }

    static readonly Dictionary<string, Site> Sites = new Dictionary<string, Site>();
    static ManualLogSource _log;
    static string _mod = "Mod";

    /// <summary>How many exceptions per site are logged before it goes quiet (default 3).</summary>
    internal static int MaxLogsPerSite { get; set; } = 3;

    /// <summary>Total exceptions caught by the guards (logged or not).</summary>
    internal static int TotalErrors { get; private set; }

    internal static void Init(ManualLogSource log, string mod, int maxLogsPerSite = 3)
    {
        _log = log;
        _mod = string.IsNullOrEmpty(mod) ? "Mod" : mod;
        MaxLogsPerSite = Math.Max(1, maxLogsPerSite);
    }

    /// <summary>Runs body; an exception is caught and reported for this call site.</summary>
    internal static void Guard(Action body, string site = null,
        [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        try { body(); }
        catch (Exception ex) { Report(ex, site ?? SiteName(member, file, line)); }
    }

    /// <summary>
    /// Returns body(); on an exception returns <paramref name="fallback"/> and reports it. The site comes before the
    /// fallback so that Guard(() =&gt; throw ..., "site") can't bind "site" as a string fallback; pass fallback by name.
    /// </summary>
    internal static T Guard<T>(Func<T> body, string site = null, T fallback = default,
        [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        try { return body(); }
        catch (Exception ex) { Report(ex, site ?? SiteName(member, file, line)); return fallback; }
    }

    /// <summary>
    /// HarmonyX prefix helper. HarmonyX runs EVERY prefix, even after another one returned false (docs rule 4).
    /// <paramref name="runOriginal"/> = the prefix's <c>__runOriginal</c> argument. If another prefix already skipped the
    /// original, body is not run (no double effect) and false is returned (it stays skipped). Otherwise body decides:
    /// true = let the original run, false = this prefix replaced it. An exception in body = the original runs.
    /// </summary>
    internal static bool Prefix(bool runOriginal, Func<bool> body, string site = null,
        [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (!runOriginal) return false;
        try { return body(); }
        catch (Exception ex) { Report(ex, site ?? SiteName(member, file, line)); return true; }
    }

    /// <summary>For explicit catch blocks: counts the exception for this site; logs it while under the limit. Returns true if logged.</summary>
    internal static bool Report(Exception ex, string site)
    {
        site = string.IsNullOrEmpty(site) ? "?" : site;
        int n;
        lock (Sites)
        {
            TotalErrors++;
            if (!Sites.TryGetValue(site, out Site s)) Sites[site] = s = new Site();
            n = ++s.Count;
        }
        if (n > MaxLogsPerSite) return false;
        string text = n == 1 && ex != null ? ex.ToString() : (ex == null ? "(null exception)" : ex.GetType().Name + ": " + ex.Message);
        string tail = n == MaxLogsPerSite ? " - further errors here are not logged." : "";
        _log?.LogWarning($"{_mod}: {site} failed ({n}){tail} {text}");
        return true;
    }

    /// <summary>Exceptions counted for a site so far.</summary>
    internal static int ErrorCount(string site)
    {
        lock (Sites) return Sites.TryGetValue(site ?? "?", out Site s) ? s.Count : 0;
    }

    /// <summary>Tests only.</summary>
    internal static void ResetForTests()
    {
        lock (Sites) { Sites.Clear(); TotalErrors = 0; }
    }

    static string SiteName(string member, string file, int line)
    {
        string f = "";
        try { f = Path.GetFileNameWithoutExtension(file ?? ""); } catch { }
        return (f.Length > 0 ? f + "." : "") + member + ":" + line;
    }
}
