// otc_bepinex - Off the Clock shared BepInEx helpers (source-shared, compiled into each plugin). README.md next to src\.
// OtcHost: one per-frame driver on Unity's main thread for everything in this library (keys, notice).
#if !OTC_MONO && !OTC_IL2CPP
#error otc_bepinex: define OTC_MONO (BepInEx 5) or OTC_IL2CPP (BepInEx 6): import OtcBepInEx.props with OtcRuntime set.
#endif
#if OTC_MONO && OTC_IL2CPP
#error otc_bepinex: OTC_MONO and OTC_IL2CPP are both defined; set exactly one OtcRuntime.
#endif
using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
#if OTC_IL2CPP
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
#else
using BepInEx;
#endif

namespace OffTheClock.Lib;

/// <summary>
/// Calls registered per-frame and GUI callbacks from a MonoBehaviour's Update / OnGUI, i.e. on Unity's main thread
/// (rule 1 of docs/ENGINE-BEST-PRACTICES-BEPINEX.md). The behaviour is generated per plugin by OtcBepInEx.props
/// (OtcGenerated.&lt;AssemblyName&gt;.OtcHostBehaviour), so two plugins that use this library never inject the same IL2CPP class name.
/// <code>
/// // BepInEx 6 IL2CPP, in Load():          // BepInEx 5 Mono, in Awake():
/// OtcHost.Start(this, Log, "MyMod");        OtcHost.Start(this, Logger, "MyMod");
/// </code>
/// Every callback runs inside its own try/catch: the first 3 exceptions per callback are logged, then it is dropped
/// with one WARNING, so a broken feature never spams the log or stops the others.
/// </summary>
internal static partial class OtcHost
{
    sealed class Entry
    {
        public string Name;
        public Action Body;
        public int Errors;
    }

    const int MaxErrorsPerCallback = 3;
    static readonly List<Entry> Updates = new List<Entry>();
    static readonly List<Entry> Guis = new List<Entry>();
    static ManualLogSource _log;
    static string _mod = "Mod";
    static bool _started;

    /// <summary>True once the host behaviour was attached (Start or Attach succeeded).</summary>
    internal static bool Running => _started;

    /// <summary>Frames seen by RunUpdate (tests and diagnostics).</summary>
    internal static long Frames { get; private set; }

    /// <summary>Filled in by the generated OtcHost.g.cs (OtcBepInEx.props); stays a no-op when generation is off.</summary>
    static partial void AttachGenerated(object plugin, ref bool attached);

#if OTC_IL2CPP
    /// <summary>Attaches the generated host behaviour to the plugin (IL2CPP: registers it with ClassInjector first).</summary>
    internal static bool Start(BasePlugin plugin, ManualLogSource log, string mod)
#else
    /// <summary>Attaches the generated host behaviour to the plugin's GameObject.</summary>
    internal static bool Start(BaseUnityPlugin plugin, ManualLogSource log, string mod)
#endif
    {
        _log = log;
        _mod = string.IsNullOrEmpty(mod) ? "Mod" : mod;
        if (_started) return true;
        bool attached = false;
        AttachGenerated(plugin, ref attached);
        if (!attached && !_started)
            log?.LogWarning($"{_mod}: no per-frame host (OtcGenerateHost is off and no OtcHost.Attach<T>() was called) - keys and notices stay inactive.");
        return _started;
    }

    /// <summary>
    /// Attaches your own host behaviour type T (one whose Update calls OtcHost.RunUpdate and OnGUI calls RunGui).
    /// IL2CPP: T must be public, have a (IntPtr) constructor and a full name unique to this plugin.
    /// </summary>
    internal static bool Attach<T>(object plugin) where T : MonoBehaviour
    {
        if (_started) return true;
        try
        {
#if OTC_IL2CPP
            if (!ClassInjector.IsTypeRegisteredInIl2Cpp<T>()) ClassInjector.RegisterTypeInIl2Cpp<T>();
            ((BasePlugin)plugin).AddComponent<T>();
#else
            ((BaseUnityPlugin)plugin).gameObject.AddComponent<T>();
#endif
            _started = true;
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogError($"{_mod}: could not start the per-frame host ({ex.GetType().Name}: {ex.Message}) - keys and notices stay inactive.");
            return false;
        }
    }

    /// <summary>Adds a callback that runs every frame (Update). Same name again = replaced.</summary>
    internal static void OnUpdate(string name, Action body) => Add(Updates, name, body);

    /// <summary>Adds a callback that runs in OnGUI (IMGUI drawing only). Same name again = replaced.</summary>
    internal static void OnGui(string name, Action body) => Add(Guis, name, body);

    internal static void Remove(string name)
    {
        Updates.RemoveAll(e => e.Name == name);
        Guis.RemoveAll(e => e.Name == name);
    }

    static void Add(List<Entry> list, string name, Action body)
    {
        if (body == null) return;
        list.RemoveAll(e => e.Name == name);
        list.Add(new Entry { Name = name ?? "callback", Body = body });
    }

    /// <summary>Called by the host behaviour's Update. Public for your own host type; don't call it from other threads.</summary>
    internal static void RunUpdate()
    {
        Frames++;
        Run(Updates, "per-frame");
    }

    /// <summary>Called by the host behaviour's OnGUI.</summary>
    internal static void RunGui() => Run(Guis, "on-screen");

    static void Run(List<Entry> list, string kind)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            try
            {
                e.Body();
            }
            catch (Exception ex)
            {
                e.Errors++;
                if (e.Errors <= MaxErrorsPerCallback)
                    _log?.LogWarning($"{_mod}: {kind} '{e.Name}' failed ({ex.GetType().Name}: {ex.Message})" +
                                     (e.Errors == MaxErrorsPerCallback ? " - switched off after 3 errors." : "."));
                if (e.Errors >= MaxErrorsPerCallback)
                {
                    list.RemoveAt(i);
                    i--;
                }
            }
        }
    }

    /// <summary>Tests only: forget every callback and the started state.</summary>
    internal static void ResetForTests()
    {
        Updates.Clear();
        Guis.Clear();
        _started = false;
        Frames = 0;
    }
}
