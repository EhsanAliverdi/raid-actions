using RaidActions.App.Input;
using RaidActions.Autoplay;
using RaidActions.Execution;
using RaidActions.Game.Dry;
using RaidActions.Game.Memory;
using RaidActions.Game.Window;
using RaidExtractor.Autoplay;

namespace RaidActions.App.Play;

/// <summary>
/// Auto Play's hands on the running game (plan/autoplay.md): the Classic Arena flow and the inbox, with clicks only on the play
/// targets (<see cref="PlayTargets"/>) and the back/close elements, each resolved fresh from memory right before it, sent only to
/// Raid's window and followed by a read that must show the expected change; anything else stops the turn (the engine sends the game
/// home). Never a gem: a refresh only when the game says it is free, and any purchase or refill window is closed. A dry run shows
/// the clicks that would spend something (overlay pictures) and presses none of them.
/// </summary>
public sealed partial class LivePlayGame(LiveGame game, UnityUi ui, string folder, WindowPolicy policy, Action<string> log, string extractorRoot = "", string outDir = "") : IPlayGame
{
    private readonly NavigatingDriver _nav = new(game, ui, null, folder, policy, log);
    private readonly Random _random = new();
    private int _shot;

    /// <summary>The kinds this game may click: the play targets and the ways back. Nothing else, ever.</summary>
    private static readonly HashSet<string> Allowed = [.. PlayTargets.Kinds, "back", "dialog-close", "screen-close"];

    private static readonly string[] BattleViews = ["Battle", "BattleHUD", "BattleLoadingDialog"];

    // ------------------------------------------------------------------ IPlayGame

    public async Task<GameResources> ReadResourcesAsync(CancellationToken ct)
    {
        var account = await game.Account.ReadAsync(ct);
        double R(string name) => account.Account.Resources.TryGetValue(name, out var v) ? v : 0;
        TimeSpan? timer = null;
        int? cap = null;
        // The HUD's own counter (the arena screens show the tokens with "next in" and "7/10").
        var (s, _, play) = await Read(ct);
        if (play.Hud.FirstOrDefault(h => h.Resource == "Token") is { } hud)
        {
            timer = GameText.TimeLeft(hud.Timer);
            cap = GameText.Amount(hud.Value)?.Max;
        }
        return new GameResources(DateTime.UtcNow, R("Token"), R("Energy"), R("Gem"), timer, null, cap);
    }

    public async Task GoHomeAsync(CancellationToken ct)
    {
        _nav.FitWindow();
        // The arena result has no back button (a tap closes it), so the gear driver's way home cannot leave it.
        if (Has(await ReadState(ct), PlayTargets.ArenaResultView)) await LeaveResult(ct);
        await _nav.GoHome(Action("go home"), ct);
    }

    public async Task<ArenaBoard> OpenClassicArenaAsync(CancellationToken ct)
    {
        _nav.FitWindow();
        for (var hop = 0; hop < 8; hop++)
        {
            var s = await Settled(ct);
            if (s.InBattle && !Has(s, PlayTargets.ArenaResultView))
            {
                log("autoplay: a battle is running; waiting for it to end (no input)");
                await WaitBattle(ct);
                continue;
            }
            if (Has(s, PlayTargets.ArenaResultView)) { await LeaveResult(ct); continue; }
            if (OnArena(s)) return await Board(ct);
            if (Top(s) == PlayTargets.TeamView) { await Back(PlayTargets.TeamView, ct); continue; }
            if (Has(s, PlayTargets.ArenaMapView) && Top(s) is PlayTargets.ArenaMapView or PlayTargets.ArenaHudView)
            {
                await Click("arena-classic", null, (s2, _) => OnArena(s2), "the Classic Arena opens", ct);
                continue;
            }
            if (Top(s) == PlayTargets.BattleMenuView)
            {
                await Click("mode-arena", null, (s2, _) => Has(s2, PlayTargets.ArenaMapView), "the arena map opens", ct);
                continue;
            }
            if (!NavigatingDriver.AtVillage(s)) { await _nav.GoHome(Action("to the village"), ct); continue; }
            await Click("village-battle", null, (s2, _) => Has(s2, PlayTargets.BattleMenuView) || Has(s2, PlayTargets.ArenaMapView), "the battle menu opens", ct);
        }
        throw new PlayStopException("Could not reach the Classic Arena in 8 steps.");
    }

    public Task<BattleOutcome> FightArenaOpponentAsync(long userId, bool dryRun, CancellationToken ct) => FightArena(userId, dryRun, false, ct);

    public Task<BattleOutcome> FightArenaAffinitiesAsync(long userId, bool dryRun, CancellationToken ct) => FightArena(userId, dryRun, true, ct);

    /// <summary>The arena team as it was before the 4-affinity quest fight: put back on the next arena fight (kept across restarts).</summary>
    private string ArenaTeamFile => Path.Combine(ExtractorOutput, "autoplay", "arena-team-before-quest.json");

    private List<int>? SavedArenaTeam()
    {
        try { return File.Exists(ArenaTeamFile) ? System.Text.Json.JsonSerializer.Deserialize<List<int>>(File.ReadAllText(ArenaTeamFile)) : null; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>The team screen set to this team (the arena's own screen); Cursed City's remembered team is left as it was.</summary>
    private async Task SetArenaTeam(IReadOnlyList<int> team, CancellationToken ct)
    {
        var cc = _ccLastTeam;
        try { await SetTeam(team, ct, PlayTargets.TeamView); }
        finally { _ccLastTeam = cc; }
    }

    private async Task<BattleOutcome> FightArena(long userId, bool dryRun, bool affinities, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var (s, _, play) = await Read(ct);
        if (!OnArena(s) || play.Arena is not { } board) throw new PlayStopException($"Expected the Classic Arena on top; the screen shows {Top(s)}.");
        var row = board.Rows.FirstOrDefault(r => r.UserId == userId) ?? throw new PlayStopException($"Opponent {userId} is not on the board.");
        if (row.Status != 0 || !row.Playable) throw new PlayStopException($"Opponent {row.Name} cannot be fought now (status {row.Status}, playable {row.Playable}).");
        await ScrollTo(userId, ct);
        var who = $"{row.Name} ({row.Power:N0})";

        // The row's Battle: the team selection opens (or the game says something: out of tokens, set a defence team, ...).
        await Click("arena-opponent", t => t.Key == userId, (s2, _) => Top(s2) is PlayTargets.TeamView or PlayTargets.RefillView or UiState.MessageBoxView,
            $"the team selection for {who} opens", ct);
        var after = await Settled(ct);
        if (Top(after) != PlayTargets.TeamView) await Refuse(after, $"Battle {who}", ct);

        // The quest "a team from 4 different Affinities" (Ehsan 2026-10-31): the team built from the one shown, which is saved first and
        // put back before the next arena fight. A dry run sets it and puts the old one back at once (nothing is saved without Start).
        var (_, _, teamRead) = await Read(ct);
        var shownTeam = teamRead.TeamIds ?? [];
        var restore = !affinities ? SavedArenaTeam() : null;
        if (affinities)
        {
            var (data, _, _, _) = await Account(ct);
            var team = ArenaAffinityTeam.Build(shownTeam, data.Champions)
                ?? throw new PlayStopException("No team of 4 different affinities can be built (a geared champion of each affinity is needed).");
            if (!dryRun && !File.Exists(ArenaTeamFile))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ArenaTeamFile)!);
                File.WriteAllText(ArenaTeamFile, System.Text.Json.JsonSerializer.Serialize(shownTeam));
            }
            log($"autoplay: STEP the 4-affinity team for the quest: {string.Join(", ", team.Select(i => $"#{i}"))} (the arena team was {string.Join(", ", shownTeam.Select(i => $"#{i}"))})");
            await SetArenaTeam(team, ct);
            if (dryRun) await SetArenaTeam(shownTeam, ct);
        }
        else if (restore is { Count: > 0 } && !restore.ToHashSet().SetEquals(shownTeam))
        {
            log($"autoplay: STEP the arena team back after the quest fight: {string.Join(", ", restore.Select(i => $"#{i}"))}");
            await SetArenaTeam(restore, ct);
        }

        if (dryRun)
        {
            var (_, _, team) = await Read(ct);
            var start = team.Targets.FirstOrDefault(t => t.Kind == "team-start");
            if (start?.Rect is { } r) Shot($"autoplay: WOULD CLICK Start vs {who} (dry run: NOT pressed)", r, always: true);
            await Back(PlayTargets.TeamView, ct);
            return new BattleOutcome(BattleResult.NotStarted, start?.NotClickable is null && start is not null
                ? "Start shown, not pressed" : $"Start not identified: {start?.Note ?? "no team-start target"}");
        }

        await Click("team-start", null, (s2, _) => s2.Active.Any(v => BattleViews.Contains(v.Name)) || Top(s2) is PlayTargets.RefillView or UiState.MessageBoxView,
            "the battle starts", ct, within: TimeSpan.FromSeconds(25), shot: true);
        var inBattle = await ReadState(ct);
        if (!inBattle.Active.Any(v => BattleViews.Contains(v.Name))) await Refuse(inBattle, $"Start vs {who}", ct);
        log($"autoplay: battle vs {who} started");
        if (restore is not null) try { File.Delete(ArenaTeamFile); } catch (IOException) { }

        var result = (await WaitBattle(ct))?.Result;
        await LeaveResult(ct);
        var took = DateTime.UtcNow - started;
        var outcome = result?.ResultType switch { 1 => BattleResult.Victory, 2 => BattleResult.Defeat, 3 => BattleResult.Draw, _ => BattleResult.Unknown };
        return new BattleOutcome(outcome, $"{took.TotalSeconds:0} s" + (result?.EnemyPower is { } ep ? $", enemy power {ep:N0}" : ""));
    }

    public async Task<ArenaBoard?> RefreshArenaFreeAsync(bool dryRun, CancellationToken ct)
    {
        var (s, targets, play) = await Read(ct);
        if (!OnArena(s) || play.Arena is not { } board) throw new PlayStopException($"Expected the Classic Arena on top; the screen shows {Top(s)}.");
        if (board.FreeRefreshAvailable != true) return null; // never a paid refresh
        var before = board.Rows.Select(r => r.UserId).OrderBy(x => x).ToList();
        if (dryRun)
        {
            if (targets.FirstOrDefault(t => t.Kind == "arena-refresh") is { Rect: { } r } t) Shot($"autoplay: WOULD CLICK {t.Name} (free; dry run: NOT pressed)", r, always: true);
            return null;
        }
        await Click("arena-refresh", null, (_, lazy) => lazy().Arena is { } b && b.Loading != true && b.Rows.Count > 0
            && !b.Rows.Select(r => r.UserId).OrderBy(x => x).SequenceEqual(before), "a new board", ct, within: TimeSpan.FromSeconds(12),
            before: p => p.Arena?.FreeRefreshAvailable == true ? null : "the refresh is not free any more");
        return await Board(ct);
    }

    public async Task<int> CollectInboxTokensAsync(bool dryRun, CancellationToken ct) =>
        (int)await CollectInbox(PlayTargets.IsArenaTokens, "Token", "arena tokens", dryRun, ct);

    /// <summary>Collects every inbox entry <paramref name="match"/> takes; what the account gained of <paramref name="resource"/>.</summary>
    private async Task<double> CollectInbox(Func<InboxEntry, bool> match, string resource, string what, bool dryRun, CancellationToken ct)
    {
        var before = (await game.Account.ReadAsync(ct)).Account.Resources.GetValueOrDefault(resource);
        await GoHomeAsync(ct);
        await Click("village-inbox", null, (s2, _) => Has(s2, PlayTargets.InboxView), "the inbox opens", ct);
        await Task.Delay(1500, ct);
        for (var n = 0; n < 10; n++)
        {
            var (_, _, play) = await Read(ct);
            var entry = play.Inbox.FirstOrDefault(e => match(e) && e.Collect?.NotClickable is null);
            if (entry is null)
            {
                if (n == 0) log($"autoplay: inbox: no {what} ({play.Inbox.Count} entries: {string.Join("; ", play.Inbox.Select(e => e.Title ?? e.Message).Take(8))})");
                break;
            }
            if (dryRun)
            {
                Shot($"autoplay: WOULD CLICK {entry.Collect!.Name} (dry run: NOT pressed)", entry.Collect.Rect!.Value, always: true);
                break;
            }
            var count = play.Inbox.Count(match);
            await Click("inbox-collect", t => t.Path == entry.Collect!.Path, (s2, lazy) => Top(s2) != PlayTargets.InboxView || lazy().Inbox.Count(match) < count,
                $"\"{entry.Title}\" is collected", ct);
            // A rewards popup after collecting: closed with its own close.
            var s3 = await Settled(ct);
            if (Top(s3) is { } top && top != PlayTargets.InboxView && top.EndsWith("Overlay", StringComparison.Ordinal)) await Back(top, ct);
        }
        await GoHomeAsync(ct);
        var after = (await game.Account.ReadAsync(ct)).Account.Resources.GetValueOrDefault(resource);
        return Math.Max(0, Math.Round(after - before));
    }

    /// <summary>
    /// What Auto Play sees now, read only (autoplay-explore): the views, every play target and way back with whether it may be clicked
    /// and why, the arena board, the HUD counters and the inbox entries. For checking the target rules on a live screen.
    /// </summary>
    public List<string> Explore()
    {
        var (s, targets, play) = ReadSync();
        if (s is null) return ["The UI state could not be read."];
        var lines = new List<string> { $"top: {Top(s)}   battle: {(s.InBattle ? "yes" : "no")}   views: {string.Join(", ", s.Active.Where(v => v.Visible && !v.Name.EndsWith("NotificationsView", StringComparison.Ordinal)).Select(v => v.Name))}", "" };
        lines.Add("targets (Auto Play clicks only these kinds):");
        foreach (var t in targets.Where(t => Allowed.Contains(t.Kind)).OrderBy(t => t.View).ThenBy(t => t.Kind))
            lines.Add($"  {t.Kind,-15} {t.Name,-44} {t.View,-28} {t.Rect?.ToString() ?? "-",-22} {t.NotClickable ?? "ok"}{(t.Note is { } n ? $"   [{n}]" : "")}");
        if (play.Arena is { } a)
        {
            lines.Add("");
            lines.Add($"arena board: tab {a.Tab?.ToString() ?? "?"}, loading {a.Loading?.ToString() ?? "?"}, free refresh {(a.FreeRefreshAvailable == true ? "available" : $"not now ({a.FreeRefreshText ?? "?"})")}");
            foreach (var r in a.Rows)
                lines.Add($"  {r.Name,-22} power {r.Power,9:N0}  status {r.Status}  {(r.Playable ? "playable" : "not playable")}{(r.Quarantined ? ", quarantine" : "")}  team power text \"{r.TeamPowerText}\"  battle: {r.Battle?.NotClickable ?? "ok"}");
        }
        if (play.AutoOn is { } auto) lines.Add($"battle: auto {(auto ? "on" : "off")}");
        if (play.CcDifficulty is { } ccd) lines.Add($"cursed city map: difficulty {ccd} (1 Normal, 2 Hard, 3 Brutal)");
        if (play.Result is { } res) lines.Add($"result: type {res.ResultType} (1 victory, 2 defeat, 3 draw), enemy {res.EnemyName} power {res.EnemyPower:N0}");
        if (play.Hud.Count > 0)
        {
            lines.Add("");
            lines.Add("HUD counters:");
            foreach (var h in play.Hud) lines.Add($"  {h.Resource,-14} \"{h.Value}\"   next: \"{h.Timer}\" → {GameText.TimeLeft(h.Timer)?.ToString() ?? "-"}");
        }
        if (play.Inbox.Count > 0)
        {
            lines.Add("");
            lines.Add("inbox:");
            foreach (var e in play.Inbox) lines.Add($"  {(PlayTargets.IsArenaTokens(e) ? "TOKENS " : "       ")}\"{e.Title}\" / \"{e.Message}\" icon {e.Icon}  collect: {e.Collect?.NotClickable ?? "ok"}");
        }
        if (play.Notes.Count > 0)
        {
            lines.Add("");
            lines.Add("read problems:");
            lines.AddRange(play.Notes.Select(n => "  " + n));
        }
        return lines;
    }

    // ------------------------------------------------------------------ flow pieces

    /// <summary>The board as the activity sees it, once the battle tab has loaded (at most 10 s).</summary>
    private async Task<ArenaBoard> Board(CancellationToken ct)
    {
        for (var i = 0; i < 20; i++)
        {
            var (s, targets, play) = await Read(ct);
            if (!OnArena(s)) throw new PlayStopException($"Expected the Classic Arena on top; the screen shows {Top(s)}.");
            if (play.Arena is { Tab: { } tab } && tab != 0)
            {
                await Click("arena-tab", t => t.Id == 0, (_, lazy) => lazy().Arena?.Tab == 0, "the battle tab is on", ct);
                continue;
            }
            if (play.Arena is { Loading: not true } b && b.Rows.Count > 0)
            {
                var rows = b.Rows.Where(r => r.Battle?.Active == true).DefaultIfEmpty().Where(r => r is not null).Select(r => r!).ToList();
                if (rows.Count == 0) rows = b.Rows;
                var left = GameText.TimeLeft(b.FreeRefreshText);
                return new ArenaBoard(DateTime.UtcNow, [.. rows.DistinctBy(r => r.UserId).Select(r => new ArenaOpponentInfo(r.UserId, r.Name, r.Power,
                    r.Status switch { 1 => OpponentStatus.Defeated, 2 => OpponentStatus.Won, _ => OpponentStatus.None }, r.Playable && !r.Quarantined))],
                    b.FreeRefreshAvailable == true, left, await MyArenaPower(ct));
            }
            await Task.Delay(500, ct);
        }
        throw new PlayStopException("The Classic Arena board did not load in 10 s (no opponent rows read).");
    }

    /// <summary>Wheel-scrolls the board until the opponent's Battle button is fully visible (at most 12 turns).</summary>
    private async Task ScrollTo(long userId, CancellationToken ct, string kind = "arena-opponent")
    {
        for (var i = 0; i < 12; i++)
        {
            var (_, targets, _) = await Read(ct);
            var mine = targets.FirstOrDefault(t => t.Kind == kind && t.Key == userId);
            if (mine is null) throw new PlayStopException($"Opponent {userId} has no Battle target on the board.");
            if (mine.NotClickable is null) return;
            if (mine.Rect is null || mine.Interactable is null) throw new PlayStopException($"Opponent {userId}'s Battle button: {mine.NotClickable} ({mine.Note}).");
            var shown = targets.Where(t => t.Kind == kind && t.FullyVisible && t.Rect is not null).Select(t => t.Rect!.Value).ToList();
            if (shown.Count == 0) throw new PlayStopException("No opponent row is fully shown to scroll from.");
            var cx = shown.Average(r => r.Center.X);
            var cy = shown.Average(r => r.Center.Y);
            var (w, why) = GameWindow.Find();
            if (w is null) throw new PlayStopException(why!);
            var down = mine.Rect.Value.Center.Y > cy;
            if (MouseInput.Wheel(w, cx, cy, down ? -2 : 2) is { } notSent) throw new PlayStopException("Not scrolled: " + notSent);
            log($"autoplay: scrolled the board {(down ? "down" : "up")} towards opponent {userId}");
            await Task.Delay(400, ct);
        }
        throw new PlayStopException($"Opponent {userId} not reachable by scrolling.");
    }

    /// <summary>Waits for the battle to end (no input but the Auto button, once, when auto is off); the result screen's read, if any.</summary>
    private async Task<PlayScan?> WaitBattle(CancellationToken ct, string resultView = PlayTargets.ArenaResultView)
    {
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(20); // dungeon runs took 9-10 minutes at x1 (2026-10-30)
        var autoTried = false;
        var speedTries = 0;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(1500, ct);
            var s = await ReadState(ct);
            if (Has(s, resultView))
            {
                await Task.Delay(1200, ct); // the result's numbers fill in
                var (_, _, play) = await Read(ct);
                return play;
            }
            if (!s.InBattle) return null; // the battle ended somewhere else (the game moved on)
            // Another battle's result screen (a name not known for this battle; 2026-10-31 the Iron Twins' waited 20 minutes): the end too.
            if (Top(s) is { } other && other.StartsWith("BattleFinish", StringComparison.Ordinal) && other != resultView)
            {
                log($"autoplay: the battle ended on {other} (expected {resultView})");
                await Task.Delay(1200, ct);
                var (_, _, play) = await Read(ct);
                await LeaveResult(ct, other);
                return play with { Pve = play.Pve ?? new PveResultRead(null, null, null, null) };
            }
            if (!autoTried && Has(s, PlayTargets.BattleHudView) && !s.UiBlocked)
            {
                var (_, _, play) = await Read(ct);
                if (play.AutoOn == false)
                {
                    autoTried = true;
                    await Click("battle-auto", null, (_, lazy) => lazy().AutoOn == true, "auto is on", ct, view: PlayTargets.BattleHudView);
                    log("autoplay: auto switched on");
                }
                else if (play.AutoOn == true) autoTried = true;
            }
            // Battle speed x2 (Ehsan 2026-10-30: the most the game offers); tried twice at most, a battle never stops for it.
            if (speedTries < 2 && Has(s, PlayTargets.BattleHudView) && !s.UiBlocked)
            {
                var (_, targets, _) = await Read(ct);
                var speed = targets.FirstOrDefault(t => t.Kind == "battle-speed");
                if (speed is null || AtX2(speed.Note)) speedTries = 2;
                else if (speed.NotClickable is not null) { speedTries = 2; log($"autoplay: the speed button is not identified ({speed.Note}; {speed.NotClickable})"); }
                else
                {
                    speedTries++;
                    try
                    {
                        await Click("battle-speed", null, (_, lazy) => AtX2(lazy().Targets.FirstOrDefault(t => t.Kind == "battle-speed")?.Note), "speed x2", ct,
                            view: PlayTargets.BattleHudView, within: TimeSpan.FromSeconds(4));
                        log("autoplay: speed x2");
                        speedTries = 2;
                    }
                    catch (PlayStopException e) { log($"autoplay: speed not set to x2 ({e.Message})"); }
                }
            }
        }
        throw new PlayStopException("The battle did not end in 20 minutes.");
    }

    private (DateTime At, int? Power) _myArenaPower;

    /// <summary>My Classic Arena defense team's power from the account (read at most every 30 minutes; null when it cannot be read).</summary>
    private async Task<int?> MyArenaPower(CancellationToken ct)
    {
        if (DateTime.UtcNow - _myArenaPower.At < TimeSpan.FromMinutes(30)) return _myArenaPower.Power;
        int? power = null;
        try { power = (await game.Account.ReadAsync(ct)).Progress?.ClassicArena?.DefensePower; }
        catch (Exception e) when (e is not OperationCanceledException) { log($"autoplay: my arena power not read ({e.Message})"); }
        _myArenaPower = (DateTime.UtcNow, power);
        return power;
    }

    /// <summary>The speed button's note says x2 ("speed x2 (2)"; the string's form to confirm live).</summary>
    private static bool AtX2(string? note) => note is { } n && System.Text.RegularExpressions.Regex.IsMatch(n, @"^speed \S*2");

    /// <summary>A result screen left by its way back (the arena's tap bar; else its back/return element, never Replay or Next).</summary>
    private async Task LeaveResult(CancellationToken ct, string view = PlayTargets.ArenaResultView)
    {
        if (!Has(await ReadState(ct), view)) return; // already left (WaitBattle leaves a result screen it did not expect)
        var (_, now, _) = await Read(ct);
        var kind = now.Any(x => x.Kind == "result-close" && x.View == view) ? "result-close" : "back";
        // Up to three taps, a little longer apart each time (a tap during the result's animations can be ignored).
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await Click(kind, null, (s2, _) => !Has(s2, view), "the result screen closes", ct, view: view,
                    within: TimeSpan.FromSeconds(6));
                break;
            }
            catch (PlayStopException) when (attempt < 3)
            {
                log($"autoplay: the result screen is still open; tapping again ({attempt + 1} of 3)");
                await Task.Delay(1500 * attempt, ct);
            }
        }
        await Settled(ct);
    }

    /// <summary>A screen's back or close element (its own; an overlay's BTN_Close1).</summary>
    private async Task Back(string view, CancellationToken ct)
    {
        var (_, targets, _) = await Read(ct);
        var kind = targets.Any(t => t.View == view && t.Kind == "dialog-close" && t.NotClickable is null) ? "dialog-close" : "back";
        await Click(kind, null, (s2, _) => Top(s2) != view, $"{view} closes", ct, view: view);
    }

    /// <summary>A screen that is not the one the click should open: a purchase/refill is closed, anything else is left; the turn stops.</summary>
    private async Task Refuse(UiState s, string what, CancellationToken ct)
    {
        var top = Top(s);
        if (top == PlayTargets.RefillView)
        {
            await Back(top, ct);
            throw new PlayStopException($"{what}: the game offered to buy arena tokens (out of tokens); closed, nothing bought.");
        }
        if (top == UiState.MessageBoxView)
        {
            var text = s.MessageBox is { } m ? $"\"{m.Title}\": \"{m.Message}\"" : "(unread)";
            throw new PlayStopException($"{what}: the game shows a message {text}; nothing pressed. Answer it, then start Auto Play again.");
        }
        throw new PlayStopException($"{what}: expected the next screen; the screen shows {top}.");
    }

    // ------------------------------------------------------------------ clicking and reading

    /// <summary>
    /// One click on a play target: allowed kind, no purchase window on top, exactly one clickable target (of <paramref name="which"/>),
    /// the window unchanged, then reads until <paramref name="expect"/> holds (its second argument walks the UI tree only when called).
    /// </summary>
    private async Task Click(string kind, Func<UiTarget, bool>? which, Func<UiState, Func<PlayScan>, bool> expect, string expected, CancellationToken ct,
        string? view = null, TimeSpan? within = null, Func<PlayScan, string?>? before = null, bool shot = false)
    {
        if (!Allowed.Contains(kind)) throw new PlayStopException($"Refused: {kind} is not an Auto Play click.");
        _nav.FitWindow();
        var (s, targets, play) = await Read(ct);
        var top = Top(s);
        if (top is { } t0 && kind is not ("dialog-close" or "back" or "market-item" or "market-get" or "shop-free" or "shop-claim") && IsPurchase(t0)) throw new PlayStopException($"Refused: a purchase window ({t0}) is on top.");
        if (view is not null && top != view && !(view == PlayTargets.BattleHudView && Has(s, view))) throw new PlayStopException($"Expected {view} on top; the screen shows {top}.");
        if (before?.Invoke(play) is { } changed) throw new PlayStopException(changed);
        var all = targets.Where(t => t.Kind == kind && (which is null || which(t)) && (view is null || t.View == view)).ToList();
        if (kind == "dialog-close") all = all.Where(t => t.View == top && t.Path.EndsWith("/BTN_Close1", StringComparison.Ordinal)).ToList();
        var ok = all.Where(t => t.NotClickable is null).ToList();
        if (ok.Count != 1)
        {
            var dump = DumpScreen($"{kind} not found");
            throw new PlayStopException((all.Count == 0 ? $"{kind}: not on screen (top {top})." : ok.Count > 1 ? $"{kind}: {ok.Count} clickable elements claim it; refused."
                : $"{kind}: {string.Join("; ", all.Select(t => $"{t.NotClickable} ({t.Note})").Distinct())}.") + $" Screen dump: {dump}");
        }
        var target = ok[0];
        var (w, why) = GameWindow.Find();
        if (w is null) throw new PlayStopException(why!);
        if (policy.Problem(w) is { } bad) throw new PlayStopException(bad);
        if (target.ClientWidth is { } cw && (cw != w.Client.Width || target.ClientHeight != w.Client.Height))
            throw new PlayStopException("The window changed size before the click.");
        var r = target.Rect!.Value;
        var x = r.X + r.Width * (0.3f + 0.4f * (float)_random.NextDouble());
        var y = r.Y + r.Height * (0.3f + 0.4f * (float)_random.NextDouble());
        if (shot) Shot($"autoplay: CLICK {target.Name} at {r}", r, always: true);
        await Task.Delay(_random.Next(120, 320), ct);
        // A small window floating over the point (a screen recorder's; seen 2026-10-31): another point of the same target.
        var sent = MouseInput.Click(w, x, y, _random);
        foreach (var (fx, fy) in new[] { (0.15f, 0.15f), (0.85f, 0.15f), (0.15f, 0.85f), (0.85f, 0.85f) })
        {
            if (sent is null || !sent.StartsWith("The window under", StringComparison.Ordinal)) break;
            (x, y) = (r.X + r.Width * fx, r.Y + r.Height * fy);
            sent = MouseInput.Click(w, x, y, _random);
        }
        if (sent is { } notSent) throw new PlayStopException($"Not clicked ({target.Name}): {notSent}");
        log($"autoplay: clicked {target.Name} at ({x:0},{y:0})");

        var until = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(8));
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(250, ct);
            var s2 = await ReadState(ct);
            PlayScan? cache = null;
            if (expect(s2, () => cache ??= ReadSync().Play)) return;
        }
        var end = await ReadState(ct);
        throw new PlayStopException($"After clicking {target.Name}, expected {expected}; the screen shows {Top(end)}.");
    }

    /// <summary>
    /// The visible screens' elements (active ones: name, binding path, bound context, rectangle, clickable) to a text file in the run
    /// folder, so a target rule that found nothing can be fixed from one run. Returns the file.
    /// </summary>
    private string DumpScreen(string why)
    {
        var file = Path.Combine(folder, $"{DateTime.Now:HHmmss}-screen.txt");
        try
        {
            var (s, _) = game.ReadUi();
            var (w, _) = GameWindow.Find();
            var lines = new List<string> { why, $"top: {s?.Top?.Name}" };
            if (s is not null && w is not null)
                foreach (var (name, view) in game.Views().Where(v => s.Active.Any(a => a.Visible && a.Name == v.Name) && !v.Name.EndsWith("NotificationsView", StringComparison.Ordinal)))
                {
                    try
                    {
                        var tree = ui.Read(view, name, w.Client.Width, w.Client.Height);
                        lines.Add($"== {name}");
                        foreach (var n in tree.Nodes.Where(n => n.ActiveInHierarchy && (n.Interactable is not null || n.Context is not null || n.BindingPath is not null)))
                            lines.Add($"{new string(' ', Math.Min(n.Depth, 30))}{n.Name}  binding '{n.BindingPath}'  ctx {n.Context?.Class.Split('.')[^1]}  {n.Screen}  {(n.Interactable is { } ia ? (ia ? "CLICKABLE" : "disabled") : "")}");
                    }
                    catch (Exception e) when (e is UiReadException or RaidExtractor.ReadCheckException) { lines.Add($"== {name}: {e.Message}"); }
                }
            Directory.CreateDirectory(folder);
            File.WriteAllLines(file, lines);
            return file;
        }
        catch (IOException e) { return "(not written: " + e.Message + ")"; }
    }

    private static bool IsPurchase(string view) =>
        view.Contains("Bank", StringComparison.Ordinal) || view.Contains("Offer", StringComparison.Ordinal) || view.Contains("Shop", StringComparison.Ordinal)
        || view == PlayTargets.RefillView || view.Contains("Purchase", StringComparison.Ordinal)
        // A location that is closed today offers to open it for gems (seen 2026-10-31 on Void Keep): never paid, only closed.
        || view.Contains("Paid", StringComparison.Ordinal);

    private async Task<(UiState State, List<UiTarget> Targets, PlayScan Play)> Read(CancellationToken ct)
    {
        for (var i = 0; i < 15; i++)
        {
            ct.ThrowIfCancellationRequested();
            var r = ReadSync();
            if (r.State is not null) return (r.State, r.Targets, r.Play);
            await Task.Delay(300, ct);
        }
        throw new PlayStopException("The UI state could not be read.");
    }

    /// <summary>Every visible view's tree, once: the gear targets (for the ways back) and the play targets with their facts.</summary>
    private (UiState? State, List<UiTarget> Targets, PlayScan Play) ReadSync()
    {
        var (s, _) = game.ReadUi();
        if (s is null) return (null, [], PlayScan.Empty);
        var (w, _) = GameWindow.Find();
        if (w is null || w.NotUsable is not null) return (s, [], PlayScan.Empty);
        var visible = s.Active.Where(v => v.Visible).Select(v => v.Name).ToHashSet();
        var targets = new List<UiTarget>();
        var play = PlayScan.Empty;
        foreach (var (name, view) in game.Views().Where(v => visible.Contains(v.Name) && !v.Name.EndsWith("NotificationsView", StringComparison.Ordinal)))
        {
            try
            {
                var tree = ui.Read(view, name, w.Client.Width, w.Client.Height);
                targets.AddRange(UiTargets.Find(tree, game.Objects));
                var p = PlayTargets.Scan(tree, game.Objects);
                targets.AddRange(p.Targets);
                play = play.Merge(p with { Targets = [] });
            }
            catch (Exception e) when (e is UiReadException or RaidExtractor.ReadCheckException) { }
        }
        return (s, targets, play with { Targets = targets.Where(t => PlayTargets.Kinds.Contains(t.Kind)).ToList() });
    }

    private async Task<UiState> ReadState(CancellationToken ct)
    {
        for (var i = 0; i < 15; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (s, _) = game.ReadUi();
            if (s is not null) return s;
            await Task.Delay(300, ct);
        }
        throw new PlayStopException("The UI state could not be read.");
    }

    /// <summary>Reads until the top view has stayed the same for 1.2 s (popups that queue up have appeared), at most 8 s.</summary>
    private async Task<UiState> Settled(CancellationToken ct)
    {
        var s = await ReadState(ct);
        var since = DateTime.UtcNow;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(400, ct);
            var s2 = await ReadState(ct);
            if (Top(s2) != Top(s)) since = DateTime.UtcNow;
            s = s2;
            if (DateTime.UtcNow - since >= TimeSpan.FromSeconds(1.2)) break;
        }
        return s;
    }

    private static bool Has(UiState s, string view) => s.Active.Any(v => v.Name == view);

    /// <summary>The Classic Arena dialog is the screen in front (its HUD may sit above it).</summary>
    private static bool OnArena(UiState s) => Top(s) == PlayTargets.ArenaDialogView || Has(s, PlayTargets.ArenaDialogView) && Top(s) == PlayTargets.ArenaHudView;

    /// <summary>The view on top, a HUD counted as the screen under it (ArenaHUD over ArenaMap, BattleHUD over the battle).</summary>
    private static string? Top(UiState s) => s.Top?.Name;

    private string? Shot(string caption, ClientRect r, bool always = false) =>
        always ? Screen.Overlay(folder, $"{++_shot:000}-autoplay.png", caption, r, WindowPolicy.CaptureProblem) : null;

    private static UiAction Action(string text) => new(ActionKind.OpenChampion, 0, "autoplay: " + text);
}
