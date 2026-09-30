// otc_bepinex - Off the Clock shared BepInEx helpers. README.md next to src\.
// OtcRunFlag: "set the game's own cheated/modded flag, never clear it" (docs rule 19).
// OtcOnline: "is another player's game involved? then change nothing" with a fail-safe answer.
// Neither knows any game: the plugin passes the game's own getter/setter, found in the game code and documented there.
using System;
using BepInEx.Logging;

namespace OffTheClock.Lib;

/// <summary>
/// Wraps ONE game flag that keeps a changed run/save off leaderboards or marks it as modded. There is deliberately no
/// way to clear it. <see cref="Mark"/> is idempotent, verifies by reading the flag back, and returns false if the
/// flag could not be set: the caller must then leave the run unchanged ("no mark, no change").
/// <code>
/// // BALL x PIT (BattleSaveData.Cheated keeps a run off the leaderboards)
/// static readonly OtcRunFlag Cheated = new OtcRunFlag(Log, "MyMod", "BattleSaveData.Cheated",
///     read: () =&gt; BattleSaveData.I != null &amp;&amp; BattleSaveData.I.Cheated,
///     set:  () =&gt; BattleSaveData.I.Cheated = true,
///     available: () =&gt; BattleSaveData.I != null,
///     effect: "BALL x PIT will not post it to the leaderboards");
/// if (!Cheated.Mark("levels given")) return;          // before the first change to the run
/// </code>
/// Flags found in real games are listed in README.md ("Run flags in real games"). Never use a game's cheat mode that also
/// disables achievements (e.g. Subnautica GameModeUtils cheats / DevConsole) just to get a flag.
/// </summary>
internal sealed class OtcRunFlag
{
    readonly ManualLogSource _log;
    readonly string _mod;
    readonly Func<bool> _read;
    readonly Action _set;
    readonly Func<bool> _available;

    internal OtcRunFlag(ManualLogSource log, string mod, string flagName, Func<bool> read, Action set,
        string effect, Func<bool> available = null)
    {
        _log = log;
        _mod = string.IsNullOrEmpty(mod) ? "Mod" : mod;
        FlagName = flagName ?? "flag";
        Effect = effect ?? "";
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _set = set ?? throw new ArgumentNullException(nameof(set));
        _available = available;
    }

    internal string FlagName { get; }
    internal string Effect { get; }

    /// <summary>How many times this session the flag went from false to true through this helper.</summary>
    internal int TimesMarked { get; private set; }

    /// <summary>The flag as the game reports it now (false if it can't be read).</summary>
    internal bool IsMarked
    {
        get { try { return _read(); } catch { return false; } }
    }

    /// <summary>
    /// Sets the flag if it isn't set yet (by us or anyone). True = the flag is set now (verified by read-back).
    /// False = no run/save to mark, or it could not be set: leave the game unchanged.
    /// </summary>
    internal bool Mark(string reason)
    {
        try
        {
            if (_available != null && !_available()) return false;
            if (_read()) return true;
            _set();
            if (!_read())
            {
                _log?.LogError($"{_mod}: could not set the game's {FlagName} flag, so the mod leaves this unchanged.");
                return false;
            }
            TimesMarked++;
            _log?.LogMessage($"{_mod}: the game's {FlagName} flag is now set ({reason})" +
                             (Effect.Length > 0 ? "; " + Effect : "") + ". The mod never clears it.");
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogError($"{_mod}: could not set the game's {FlagName} flag ({ex.GetType().Name}: {ex.Message}), so the mod leaves this unchanged (game update?).");
            return false;
        }
    }
}

/// <summary>
/// Online guard (generalized from our Vampire Survivors and Risk of Rain 2 plugins). The plugin
/// passes the game's own check; the mod changes nothing while it is true, so other players are never affected.
/// If the check throws (game update?), the answer is "online" - the safe side - with one WARNING.
/// <code>
/// OtcOnline.Configure(Log, "MyMod", () =&gt; MultiplayerManager.Instance != null &amp;&amp; MultiplayerManager.Instance.IsOnlineMultiplayer);
/// if (OtcOnline.IsOnline) return;                 // in every patch that changes something
/// </code>
/// Not configured = false (the game has no online mode we know of). State changes are logged once each.
/// </summary>
internal static class OtcOnline
{
    static Func<bool> _check;
    static ManualLogSource _log;
    static string _mod = "Mod";
    static string _what = "online play";
    static bool? _last;
    static bool _loggedError;

    internal static void Configure(ManualLogSource log, string mod, Func<bool> isOnline, string what = "online play")
    {
        _log = log;
        _mod = string.IsNullOrEmpty(mod) ? "Mod" : mod;
        _check = isOnline;
        _what = string.IsNullOrEmpty(what) ? "online play" : what;
        _last = null;
        _loggedError = false;
    }

    internal static bool Configured => _check != null;

    /// <summary>True while online (or when the check fails). Cheap enough for patch bodies; call it on the main thread.</summary>
    internal static bool IsOnline
    {
        get
        {
            if (_check == null) return false;
            bool online;
            try
            {
                online = _check();
            }
            catch (Exception ex)
            {
                online = true;
                if (!_loggedError)
                {
                    _loggedError = true;
                    _log?.LogWarning($"{_mod}: the {_what} check failed ({ex.GetType().Name}: {ex.Message}); treated as online, so the mod changes nothing (game update?).");
                }
            }
            if (_last != online)
            {
                if (_last.HasValue || online)
                    _log?.LogInfo(online ? $"{_mod}: {_what} detected - the mod changes nothing until you are back offline."
                                         : $"{_mod}: offline again - the mod is active.");
                _last = online;
            }
            return online;
        }
    }
}
