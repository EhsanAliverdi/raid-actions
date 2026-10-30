using RaidActions.App.Play;
using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidActions.Game.Memory;
using RaidExtractor.Autoplay;

namespace RaidActions.App;

/// <summary>
/// autoplay [--dry-run] [--once] [--fake] [--enable]: the Auto Play bot (plan/autoplay.md), started and stopped by raid-extractor's
/// Auto Play page. It plays the areas switched on in output/autoplay/settings.json (re-read before every decision) until the master
/// switch goes off, the app asks it to stop (stop.request), too many errors, or Ctrl+Alt+Esc. --dry-run: navigates and shows the
/// clicks that would spend something, presses none; --once: stops when nothing is to be done now; --fake: the made-up arena (no
/// game: for trying the page); --enable: plays the Classic Arena even if the settings say off (testing from a terminal).
/// autoplay-explore: read only, prints what Auto Play sees on the open screen.
/// </summary>
public static class AutoplayCommand
{
    public static async Task<int> RunAsync(string[] args, string outDir, TextWriter output, Action<Action>? abortHook)
    {
        var dir = Path.Combine(GameCommands.LivePaths(outDir).RaidExtractorRoot, "output");
        var at = Array.IndexOf(args, "--dir");
        if (at >= 0 && at + 1 < args.Length) dir = Path.GetFullPath(args[at + 1]);
        void Print(string line) { lock (output) output.WriteLine(line); }

        if (args[0].Equals("autoplay-explore", StringComparison.OrdinalIgnoreCase)) return Explore(outDir, Print);

        var dry = args.Contains("--dry-run");
        var once = args.Contains("--once");
        var fake = args.Contains("--fake");
        // --enable: play the Classic Arena whatever the settings say; --no-yield: keep playing while the person uses the PC (a watched test).
        var enable = args.Contains("--enable");
        var enableFw = args.Contains("--enable-fw");
        var enableCc = args.Contains("--enable-cc");
        var enableEnergy = args.Contains("--enable-energy");
        var enableCb = args.Contains("--enable-cb");
        var enableGifts = args.Contains("--enable-gifts");
        var enableHydra = args.Contains("--enable-hydra");
        var enableTag = args.Contains("--enable-tag");
        var noYield = args.Contains("--no-yield");
        // --cc-difficulty Normal|Hard: the Cursed City difficulty to play; --cc-risky: also risky levels (whatever the settings say).
        var ccAt = Array.IndexOf(args, "--cc-difficulty");
        var ccDifficulty = ccAt >= 0 && ccAt + 1 < args.Length ? args[ccAt + 1] : null;
        var ccRisky = args.Contains("--cc-risky");
        AutoplaySettings Force(AutoplaySettings s)
        {
            if (enable) s = s with { Enabled = true, ClassicArena = s.ClassicArena with { Enabled = true } };
            if (enableFw) s = s with { Enabled = true, FactionWars = s.FactionWars with { Enabled = true } };
            if (enableCc) s = s with { Enabled = true, CursedCity = s.CursedCity with { Enabled = true } };
            if (enableEnergy) s = s with { Enabled = true, Energy = s.Energy with { Enabled = true } };
            if (enableCb) s = s with { Enabled = true, ClanBoss = s.ClanBoss with { Enabled = true } };
            if (enableGifts) s = s with { Enabled = true, FreeRewards = s.FreeRewards with { Enabled = true } };
            if (enableHydra) s = s with { Enabled = true, Hydra = s.Hydra with { Enabled = true } };
            if (enableTag) s = s with { Enabled = true, TagArena = s.TagArena with { Enabled = true } };
            if (ccDifficulty is not null) s = s with { CursedCity = s.CursedCity with { Difficulty = ccDifficulty } };
            if (ccRisky) s = s with { CursedCity = s.CursedCity with { TryRisky = true } };
            if (noYield) s = s with { Pacing = s.Pacing with { YieldToPersonMinutes = 0 } };
            return s;
        }
        Func<AutoplaySettings, AutoplaySettings>? force = enable || enableFw || enableCc || enableEnergy || enableCb || enableGifts || enableHydra || enableTag || noYield || ccDifficulty is not null || ccRisky ? Force : null;

        using var hold = BotLock.TakeAutoplay();
        if (hold is null)
        {
            Print("Auto Play is already running (one bot at a time).");
            return Cli.Refused;
        }
        try { File.Delete(AutoplayStatus.StopPathIn(dir)); } catch (IOException) { }

        using var abort = new CancellationTokenSource();
        abortHook?.Invoke(() => { try { abort.Cancel(); } catch (ObjectDisposedException) { } });
        var host = new LiveAutoplayHost(dir, Print, watchPerson: !fake, force);
        var arena = new ClassicArenaActivity();
        arena.Restore(LiveAutoplayHost.Today(dir), DateOnly.FromDateTime(DateTime.Now));


        if (fake)
        {
            var game = new FakeArenaGame(() => DateTime.UtcNow) { Delay = (d, ct) => Task.Delay(d, ct), Tokens = 6, InboxTokens = 3 };
            var fw = new FakeFactionWarsGame(() => DateTime.UtcNow) { Delay = (d, ct) => Task.Delay(d, ct) };
            fw.Open.Add(new FakeFactionWarsGame.Crypt { Faction = "SacredOrder", Keys = 4, HighestPassed = 10, Team = [1, 2, 3, 4, 5], WinsUpTo = 12 });
            Print($"Auto Play on the FAKE arena and crypts (no game). Files: {dir}");
            var why = await new AutoplayEngine([new FactionWarsActivity(fw), arena], game, host, dry).RunAsync(once, abort.Token);
            Print("Stopped: " + why);
            return Cli.Ok;
        }

        var (live, problem) = LiveGame.Open(GameCommands.LivePaths(outDir));
        if (live is null)
        {
            Print(problem!);
            host.WriteStatus(new AutoplayStatus(DateTime.UtcNow, Environment.ProcessId, dry, "error", problem!, null, [], [], []));
            return Cli.Failed;
        }
        using (live)
        {
            UnityUi ui;
            try { ui = live.Ui(UiCommands.NativeLayoutPath); }
            catch (UiReadException e)
            {
                Print(e.Message);
                return Cli.Failed;
            }
            var folder = Path.Combine(outDir, "autoplay", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(folder);
            var policy = Game.Window.WindowPolicy.Load(GameCommands.WindowConfig);
            var game = new LivePlayGame(live, ui, folder, policy, l => Print($"{DateTime.Now:HH:mm:ss} {l}"), GameCommands.LivePaths(outDir).RaidExtractorRoot, outDir);
            var fwActivity = new FactionWarsActivity(game);
            fwActivity.Restore(LiveAutoplayHost.Today(dir), DateOnly.FromDateTime(DateTime.UtcNow));
            var ccActivity = new CursedCityActivity(game);
            ccActivity.Restore(LiveAutoplayHost.Today(dir));
            var energyActivity = new EnergyActivity(game);
            energyActivity.Restore(LiveAutoplayHost.Today(dir), DateOnly.FromDateTime(DateTime.Now));
            // Priority (Ehsan, 2026-10-30): the arena first (its free refresh every 15 minutes, a token's opponent ready), then energy
            // (plan/energy.md); Faction Wars and Cursed City fill the time while the others wait for tokens and energy. A turn is one
            // battle, so a battle running is always finished first.
            var cbActivity = new ClanBossActivity(game);
            cbActivity.Restore(LiveAutoplayHost.Today(dir), DateOnly.FromDateTime(DateTime.Now));
            // Clan Boss (plan/clan-boss.md) after the arena: its keys come slowly, so a key waiting is spent before the energy places.
            var giftsActivity = new FreeRewardsActivity(game);
            giftsActivity.Restore(LiveAutoplayHost.Today(dir), DateOnly.FromDateTime(DateTime.Now));
            // Free rewards first: a check is a few clicks every 20 minutes (plan/free-rewards.md).
            var hydraActivity = new HydraActivity(game);
            var tagActivity = new TagArenaActivity(game);
            IActivity[] activities = [giftsActivity, arena, tagActivity, cbActivity, hydraActivity, energyActivity, fwActivity, ccActivity];
            host.GameBlocked = game.SessionTaken;
            Print($"Auto Play{(dry ? " (DRY RUN: nothing that spends is pressed)" : "")}. Settings and status: {dir}\\autoplay. Overlays: {folder}. Stop: the app, or Ctrl+Alt+Esc.");
            var engine = new AutoplayEngine(activities, game, host, dry);
            var why = await engine.RunAsync(once, abort.Token);
            Print($"Stopped after {engine.Turns} turn(s): {why}");
            return why.Contains("errors in a row", StringComparison.Ordinal) ? Cli.Failed : Cli.Ok;
        }
    }

    private static int Explore(string outDir, Action<string> print)
    {
        var (live, problem) = LiveGame.Open(GameCommands.LivePaths(outDir));
        if (live is null)
        {
            print(problem!);
            return Cli.Failed;
        }
        using (live)
        {
            UnityUi ui;
            try { ui = live.Ui(UiCommands.NativeLayoutPath); }
            catch (UiReadException e)
            {
                print(e.Message);
                return Cli.Failed;
            }
            var policy = Game.Window.WindowPolicy.Load(GameCommands.WindowConfig);
            var game = new LivePlayGame(live, ui, Path.Combine(outDir, "autoplay", "explore"), policy, print);
            foreach (var line in game.Explore()) print(line);
            return Cli.Ok;
        }
    }
}
