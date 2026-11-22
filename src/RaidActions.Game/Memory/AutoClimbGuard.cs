namespace RaidActions.Game.Memory;

/// <summary>
/// The Doom Tower's auto-climb switch (Ehsan: never start an auto-climb). 2026-11-22: Start on the tower's team screen launched the game's background
/// auto-climb three times (the small Checkbox_h above Start was on). The account's stored value (read-only): AppModel._userWrapper, UserWrapper.User,
/// User.GameData, UserGameData.StageData, UserStageData.DoomTowerData, UserDoomTowerData.AutoClimbMode. null when any step is not there.
/// </summary>
public sealed class AutoClimbReader(IGameObjects g)
{
    private const string AppModel = "Client.Model.AppModel";
    private const string UserWrapper = "Client.Model.Guard.UserWrapper";
    private const string User = "Client.Model.Gameplay.User.User";
    private const string GameData = "Client.Model.Gameplay.User.Data.UserGameData";
    private const string StageData = "SharedModel.Meta.Stages.UserStageData";
    private const string Doom = "SharedModel.Meta.Stages.UserDoomTowerData";

    public bool? Read()
    {
        var app = g.Root("AppModel", AppModel);
        var wrapper = app == 0 ? 0 : g.Ref(app, AppModel, "_userWrapper");
        var user = wrapper == 0 ? 0 : g.Ref(wrapper, UserWrapper, "User");
        var data = user == 0 ? 0 : g.Ref(user, User, "GameData");
        var stage = data == 0 ? 0 : g.Ref(data, GameData, "StageData");
        var doom = stage == 0 ? 0 : g.Ref(stage, StageData, "DoomTowerData");
        return doom == 0 ? null : g.Bool(doom, Doom, "AutoClimbMode");
    }
}

/// <summary>
/// The rule before the tower's Start: the auto-climb is as wanted, or Start is not pressed. Off everywhere but a floor climb (bosses and rooms take keys and
/// Super Raid); on for a floor climb (Ehsan 2026-11-23: "on every floor we can do auto climb in game ... these happen in background"). A tap is never made
/// blind (an unknown state could start a climb nobody wanted).
/// </summary>
public static class AutoClimbGuard
{
    /// <param name="CanStart">True only when the state was read as wanted (already, or after one tap).</param>
    public sealed record Result(bool CanStart, string Message);

    /// <param name="read">The stored state (true on, false off, null unknown); may throw when memory cannot be read.</param>
    /// <param name="tap">One tap on the checkbox.</param>
    /// <param name="pause">Waits the given milliseconds.</param>
    public static Task<Result> EnsureOffAsync(Func<bool?> read, Func<Task> tap, Func<int, Task> pause, Action<string> log) => EnsureAsync(false, read, tap, pause, log);

    public static async Task<Result> EnsureAsync(bool wanted, Func<bool?> read, Func<Task> tap, Func<int, Task> pause, Action<string> log)
    {
        var name = wanted ? "on" : "off";
        var now = Safe(read);
        if (now is null) return Fail("the auto-climb state cannot be read", wanted, log);
        if (now == wanted) { log($"autoplay: auto-climb {name}"); return new Result(true, $"auto-climb {name}"); }
        log($"autoplay: auto-climb is {(wanted ? "off" : "on")}: tapping its checkbox once");
        await tap();
        // The server confirms the change a moment later: read again for a few seconds.
        for (var i = 0; i < 8; i++)
        {
            await pause(500);
            now = Safe(read);
            if (now is null) return Fail("the auto-climb state cannot be read after the tap", wanted, log);
            if (now == wanted) { log($"autoplay: auto-climb turned {name}"); return new Result(true, $"auto-climb turned {name}"); }
        }
        return Fail($"auto-climb is still {(wanted ? "off" : "on")} after one tap", wanted, log);
    }

    private static bool? Safe(Func<bool?> read)
    {
        try { return read(); }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
    }

    private static Result Fail(string why, bool wanted, Action<string> log)
    {
        var rule = wanted ? "a floor climb starts only as an auto-climb" : "an auto-climb starts only on a floor climb";
        log($"autoplay: {why}: Start not pressed ({rule})");
        return new Result(false, $"{why}; Start not pressed ({rule})");
    }
}
