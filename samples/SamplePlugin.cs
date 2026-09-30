// Compile-only sample for otc-bepinex: the whole API used from a real BepInEx 5 / BepInEx 6 plugin class. Build it in
// a project that imports OtcBepInEx.props and references your game's BepInEx/Unity DLLs. Keep it in step with README.md.
using System;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using OffTheClock.Lib;
using UnityEngine;
#if OTC_IL2CPP
using BepInEx.Unity.IL2CPP;
#endif

namespace OtcLibSample;

[BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
#if OTC_IL2CPP
public class Plugin : BasePlugin
#else
public class Plugin : BaseUnityPlugin
#endif
{
    const string ModId = "OtcLibSample";                     // one word: self-check id and log prefix
    internal static ConfigEntry<KeyCode> OneKey;
    internal static ConfigEntry<string> TextKey;
    internal static ConfigEntry<bool> ShowNotice;
    internal static OtcSelfCheck SC;
    internal static OtcRunFlag Marked;
    static bool _gameFlag;                                    // stands in for a game's own cheated flag
    static int _value;

#if OTC_IL2CPP
    public override void Load()
    {
        var log = Log;
#else
    void Awake()
    {
        var log = Logger;
#endif
        OneKey = Config.Bind("Controls", "OneKey", KeyCode.KeypadDivide, "Key that does one thing. None = off.");
        TextKey = Config.Bind("Controls", "OtherKey", "F8", "Key name (KeypadMultiply, F8, Alpha0 ...). None = off.");
        ShowNotice = Config.Bind("Controls", "ShowOnScreenNotice", true, "Show a short notice after a key press.");
        OtcKeys.MoveOldDefault(OneKey, KeyCode.KeypadDivide, log, ModId, KeyCode.Keypad1);

        OtcPatch.Init(log, ModId);
        SC = new OtcSelfCheck(log, ModId);
        SC.Expect("value_set", "load a save and press Numpad /");
        SC.Expect("toggle", "press F8 once", optional: true);

        OtcOnline.Configure(log, ModId, () => false, "online co-op");
        Marked = new OtcRunFlag(log, ModId, "Sample.Cheated", () => _gameFlag, () => _gameFlag = true,
            "the sample leaderboard skips this run");

        var patcher = new OtcPatcher(new Harmony(PluginInfo.PLUGIN_GUID), log, ModId, SC);
        patcher.Class(typeof(SamplePatches), required: true, feature: "sample hook");
        patcher.Method("screen size", "UnityEngine.Screen", "get_width", Type.EmptyTypes, typeof(SamplePatches),
            postfix: nameof(SamplePatches.AfterWidth));
        if (!patcher.Finish()) return;

        OtcHost.Start(this, log, ModId);
        var keys = new OtcKeys(log, ModId) { CanAct = () => !OtcOnline.IsOnline };
        keys.Add("one", OneKey, DoOne, cooldown: 0.25f);
        keys.Add("other", TextKey, () => SC.Pass("toggle", "pressed"));
        keys.Start();
#if OTC_NOTICE
        var notice = new OtcNotice(log, ModId, () => ShowNotice.Value);
        notice.Start();
        _show = notice.Show;
#endif
        log.LogInfo($"{PluginInfo.PLUGIN_NAME} {PluginInfo.PLUGIN_VERSION} loaded.");
    }

    static Action<string> _show = text => { };               // no notice module: log only

    static void DoOne()
    {
        if (!Marked.Mark("value changed")) return;            // no mark, no change
        int before = _value;
        _value++;
        _show?.Invoke("Value " + _value);
        if (SC.Want("value_set")) SC.Check("value_set", _value == before + 1, "value {0} -> {1}", before, _value);
    }
}

internal static class PluginInfo
{
    public const string PLUGIN_GUID = "offtheclock.otclibsample";
    public const string PLUGIN_NAME = "otc_bepinex sample";
    public const string PLUGIN_VERSION = "0.0.1";
}

// [HarmonyPatch] class: OtcPatcher.Class(typeof(SamplePatches)). Compile-only target (a Unity getter).
[HarmonyPatch(typeof(Screen), nameof(Screen.height), MethodType.Getter)]
internal static class SamplePatches
{
    internal static void AfterWidth(ref int __result)
    {
        try { if (__result < 0) __result = 0; }
        catch (Exception ex) { OtcPatch.Report(ex, "width postfix"); }
    }

    // HarmonyX runs every prefix: act only if no other prefix already skipped the original; true = original runs.
    static bool Prefix(bool __runOriginal) => OtcPatch.Prefix(__runOriginal, () => true);

    static void Postfix(ref int __result)
    {
        try { if (__result < 0) __result = 0; }
        catch (Exception ex) { OtcPatch.Report(ex, "height postfix"); }
    }
}
