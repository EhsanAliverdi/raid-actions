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

    /// <summary>The account's resources, shards, items, gear and champions, for the rewards log (Ehsan 2026-11-06).</summary>
    public async Task<Holdings?> ReadHoldingsAsync(CancellationToken ct)
    {
        var data = await game.Account.ReadAsync(ct);
        var items = new Dictionary<string, double>(data.Account.Resources);
        foreach (var (kind, n) in data.Inventory?.Shards ?? []) items[$"{kind} shard"] = n;
        foreach (var (name, n) in data.Inventory?.Items ?? []) items[name] = items.GetValueOrDefault(name) + n;
        var gear = data.Artifacts.ToDictionary(a => a.Id, a => $"{a.Rarity} {a.Rank}★ {a.SetName ?? a.Set} {a.Slot}");
        var heroes = data.Champions.ToDictionary(c => c.Id, c => $"{c.Name ?? "#" + c.Id} ({c.Rarity})");
        return new Holdings(items, gear, heroes);
    }

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
        return new GameResources(DateTime.UtcNow, R("Token"), R("Energy"), R("Gem"), timer, null, cap, All: new Dictionary<string, double>(account.Account.Resources));
    }

    public async Task GoHomeAsync(CancellationToken ct)
    {
        _nav.FitWindow();
        // A frozen game (2026-11-03: stuck in a Live Arena battle, no longer drawing, its UI unreadable): looked at again after 20 s, then restarted.
        try { await ReadState(ct); }
        catch (PlayStopException e) when (e.Message.StartsWith("The UI state could not be read", StringComparison.Ordinal))
        {
            await Task.Delay(20000, ct);
            try { await ReadState(ct); }
            catch (PlayStopException again) when (again.Message.StartsWith("The UI state could not be read", StringComparison.Ordinal))
            {
                log("autoplay: the game's UI cannot be read: the game is frozen; restarting it and playing on");
                LiveAutoplayHost.Relaunch(ExtractorOutput);
            }
        }
        await DismissLevelUp(ct);
        // The arena result has no back button (a tap closes it), so the gear driver's way home cannot leave it.
        if (Has(await ReadState(ct), PlayTargets.ArenaResultView)) await LeaveResult(ct);
        // A Live Arena match under way (a restart mid-draft, 2026-11-03): played to its end; the draft's X would leave it.
        if (InLiveMatch(await ReadState(ct)))
        {
            var outcome = await FinishLiveMatchAsync(AutoplaySettings.Load(ExtractorOutput).LiveArena, DateTime.UtcNow, ct);
            log($"autoplay: the Live Arena match under way ended: {outcome.Result}");
        }
        await HomeAsync(Action("go home"), ct);
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
            if (!NavigatingDriver.AtVillage(s)) { await TowardsBattleMenu(s, ct); continue; }
            await Click("village-battle", null, (s2, _) => Has(s2, PlayTargets.BattleMenuView) || Has(s2, PlayTargets.ArenaMapView), "the battle menu opens", ct);
        }
        throw new PlayStopException("Could not reach the Classic Arena in 8 steps.");
    }

    public Task<BattleOutcome> FightArenaOpponentAsync(long userId, bool dryRun, CancellationToken ct) => FightArena(userId, dryRun, TeamVariant.Default, ct);

    public Task<BattleOutcome> FightArenaVariantAsync(long userId, TeamVariant variant, bool dryRun, CancellationToken ct) => FightArena(userId, dryRun, variant, ct);

    /// <summary>The arena team as it was before the 4-affinity quest fight: put back on the next arena fight (kept across restarts).</summary>
    private string ArenaTeamFile => Path.Combine(ExtractorOutput, "autoplay", "arena-team-before-quest.json");

    private List<int>? SavedArenaTeam()
    {
        try { return File.Exists(ArenaTeamFile) ? System.Text.Json.JsonSerializer.Deserialize<List<int>>(File.ReadAllText(ArenaTeamFile)) : null; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return null; }
    }

    private const string ClassicArenaAreaId = "classic-arena";

    /// <summary>The shown team noted for the page's "use the current team"; never a reason to stop.</summary>
    private void NoteTeam(string area, IReadOnlyList<int> team)
    {
        try { TeamsSeen.Note(ExtractorOutput, area, team); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"autoplay: the team seen was not saved: {e.Message}"); }
    }

    /// <summary>The page's teams for this area, read fresh so a change acts at once.</summary>
    private AreaTeams? TeamsOf(string area) => AutoplaySettings.Load(ExtractorOutput).Teams.GetValueOrDefault(area);

    private string TeamTriesFile => Path.Combine(ExtractorOutput, "autoplay", "team-tries.json");

    private Dictionary<string, List<TeamTry>> LoadTries()
    {
        try { return File.Exists(TeamTriesFile) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<TeamTry>>>(File.ReadAllText(TeamTriesFile), AutoplayJson.Options) ?? [] : []; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The team tried at the stage now (set by <see cref="ApplyDefaultTeam"/> when the bot chose it), recorded with the battle's result.</summary>
    private (string Key, List<int> Ids)? _trying;

    private void RecordTry(BattleResult result)
    {
        if (_trying is not { } t || result is not (BattleResult.Victory or BattleResult.Defeat)) return;
        _trying = null;
        var all = LoadTries();
        var list = all.TryGetValue(t.Key, out var l) ? l : all[t.Key] = [];
        list.Add(new TeamTry(t.Ids, result == BattleResult.Victory, DateTime.UtcNow));
        if (list.Count > 40) list.RemoveRange(0, list.Count - 40);
        try { Directory.CreateDirectory(Path.GetDirectoryName(TeamTriesFile)!); File.WriteAllText(TeamTriesFile, System.Text.Json.JsonSerializer.Serialize(all, AutoplayJson.Options)); }
        catch (IOException) { }
        log($"autoplay: team {string.Join(", ", t.Ids.Select(i => $"#{i}"))} {(result == BattleResult.Victory ? "won" : "lost")} at {t.Key}");
    }

    /// <summary>A team screen open: what it shows noted, and the area's default set when the page says so. False: the game refused a champion.</summary>
    private async Task<bool> ApplyDefaultTeam(string area, string teamView, CancellationToken ct, string? stage = null)
    {
        _trying = null;
        // The bot chooses here and the stage is known (Doom Tower, Faction Wars): the team that won here, else the next one not lost here.
        if (stage is not null && TeamsOf(area)?.Default is { Mode: TeamMode.BotChooses })
        {
            var (_, _, p0) = await Read(ct);
            var (d0, _, _, _) = await Account(ct);
            var key = $"{area}|{stage}";
            var tried = LoadTries().GetValueOrDefault(key) ?? [];
            var size = p0.TeamSlots is > 0 and var n ? n : 5;
            if (TeamTries.Pick(TeamTries.Variants(d0.Champions, size), tried) is not { } choice)
            {
                log($"autoplay: {area}: every team tried at {stage} lost: waiting");
                await Back(teamView, ct);
                return false;
            }
            _trying = (key, choice);
            log($"autoplay: STEP {area} {stage}: the bot's team {string.Join(", ", choice.Select(i => $"#{i}"))} ({tried.Count(t => !t.Won)} lost here so far)");
            if (choice.ToHashSet().SetEquals(p0.TeamIds ?? [])) return true;
            return await SetRunTeam(choice, teamView, ct);
        }
        var (_, _, play) = await Read(ct);
        var shown = play.TeamIds ?? [];
        NoteTeam(area, shown);
        var (data, _, _, _) = await Account(ct);
        // An empty team screen with no team saved for it (Ehsan 2026-11-09: "when there is no team setup for an account for specific area, robot can
        // choose the team"): the strongest geared champions, as many as the screen's slots.
        var pick = TeamRules.Resolve(TeamsOf(area), TeamVariant.Default, shown, data.Champions, slots: play.TeamSlots ?? 0, fillEmpty: play.TeamSlots is > 0);
        if (shown.Count == 0 && pick.Team is { Count: > 0 } chosen)
            log($"autoplay: {area}: no team on its screen and none saved: the bot chooses the {chosen.Count} strongest geared champions ({play.TeamSlots} slots)");
        if (pick.DontPlay)
        {
            log($"autoplay: {area}: a champion of the saved team is missing: not played (Teams card)");
            await Back(teamView, ct);
            return false;
        }
        if (pick.Team is not { } team || team.ToHashSet().SetEquals(shown)) return true;
        log($"autoplay: STEP {area}: the default team from the Teams card");
        return await SetRunTeam(team, teamView, ct);
    }

    /// <summary>The team screen set to this team (the arena's own screen); Cursed City's remembered team is left as it was.</summary>
    private async Task SetArenaTeam(IReadOnlyList<int> team, CancellationToken ct)
    {
        var cc = _ccLastTeam;
        try { await SetTeam(team, ct, PlayTargets.TeamView); }
        finally { _ccLastTeam = cc; }
    }

    private async Task<BattleOutcome> FightArena(long userId, bool dryRun, TeamVariant variant, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var (s, _, play) = await Read(ct);
        if (!OnArena(s) || play.Arena is not { } board) throw new PlayStopException($"Expected the Classic Arena on top; the screen shows {Top(s)}.");
        var row = board.Rows.FirstOrDefault(r => r.UserId == userId) ?? throw new PlayStopException($"Opponent {userId} is not on the board.");
        if (row.Status != 0 || !row.Playable) throw new PlayStopException($"Opponent {row.Name} cannot be fought now (status {row.Status}, playable {row.Playable}).");
        // No defence team (its power reads 0; Ehsan 2026-11-07: "you need to setup defence if defence is not there, otherwise classic arena will not work"):
        // Battle does nothing until one is set. Read fresh (not the 30-minute cache): it is set once and then stays.
        _myArenaPower = default;
        if (await MyArenaPower(ct) is 0)
            throw new PlayStopException("No Classic Arena defence team is set on this account: Battle does nothing until one is (the arena's Defence tab).");
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
        NoteTeam(ClassicArenaAreaId, shownTeam);
        var quest = variant != TeamVariant.Default;
        List<int>? questTeam = null;
        if (quest)
        {
            var (data0, _, _, _) = await Account(ct);
            questTeam = TeamRules.Resolve(TeamsOf(ClassicArenaAreaId), variant, shownTeam, data0.Champions, fillEmpty: true).Team;
            // No team of four affinities on this account (2026-11-11: KaelStormVex's only Void champions are in the Bathhouse): the fight is played with
            // the arena team, so the arena goes on (it was an error that paused Classic Arena for an hour, twice a turn).
            if (questTeam is null)
            {
                log($"autoplay: no {variant} team can be built on this account (an affinity has no champion outside the vault and the Bathhouse): the arena team fights");
                quest = false;
            }
        }
        var restore = !quest ? SavedArenaTeam() : null;
        if (quest)
        {
            var team = questTeam!;
            if (!dryRun && !File.Exists(ArenaTeamFile))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ArenaTeamFile)!);
                File.WriteAllText(ArenaTeamFile, System.Text.Json.JsonSerializer.Serialize(shownTeam));
            }
            log($"autoplay: STEP the {variant} team for the quest: {string.Join(", ", team.Select(i => $"#{i}"))} (the arena team was {string.Join(", ", shownTeam.Select(i => $"#{i}"))})");
            await SetArenaTeam(team, ct);
            if (dryRun) await SetArenaTeam(shownTeam, ct);
        }
        else if (restore is { Count: > 0 } && !restore.ToHashSet().SetEquals(shownTeam))
        {
            log($"autoplay: STEP the arena team back after the quest fight: {string.Join(", ", restore.Select(i => $"#{i}"))}");
            await SetArenaTeam(restore, ct);
        }
        else
        {
            var pick = TeamRules.Resolve(TeamsOf(ClassicArenaAreaId), TeamVariant.Default, shownTeam, (await Account(ct)).Data.Champions, fillEmpty: true);
            if (pick.DontPlay)
            {
                await Back(PlayTargets.TeamView, ct);
                return new BattleOutcome(BattleResult.NotStarted, "a champion of the saved team is missing: the Teams card says not to play");
            }
            if (pick.Team is { } chosen && !chosen.ToHashSet().SetEquals(shownTeam))
            {
                log($"autoplay: STEP the arena's default team: {string.Join(", ", chosen.Select(i => $"#{i}"))}");
                await SetArenaTeam(chosen, ct);
            }
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

    public async Task<int> CollectInboxTokensAsync(bool dryRun, CancellationToken ct)
    {
        // The tokens' "+" first (the refill window takes them from the inbox), then the inbox itself.
        await OpenClassicArenaAsync(ct);
        if (await RefillByPlus("Token", "Classic Arena tokens", dryRun, ct) is > 0 and var got) return got;
        if (!AutoplaySettings.Load(ExtractorOutput).Energy.RefillFor("Token").UseInbox) return 0;
        return (int)await CollectInbox(PlayTargets.IsArenaTokens, "Token", "arena tokens", dryRun, ct);
    }

    /// <summary>The entries the last <see cref="CollectInbox"/> really took (one left at the game's over-the-cap warning is not counted).</summary>
    private int _inboxTaken;

    /// <summary>Collects every inbox entry <paramref name="match"/> takes; what the account gained of <paramref name="resource"/>.</summary>
    private async Task<double> CollectInbox(Func<InboxEntry, bool> match, string resource, string what, bool dryRun, CancellationToken ct)
    {
        _inboxTaken = 0;
        var before = (await game.Account.ReadAsync(ct)).Account.Resources.GetValueOrDefault(resource);
        await GoHomeAsync(ct);
        await Click("village-inbox", null, (s2, _) => Has(s2, PlayTargets.InboxView), "the inbox opens", ct);
        await Pause(1500, ct);
        // The list is built after the dialog (2026-11-08 07:52: read with 0 entries while five Classic Arena refills were there): waited for, up to 8 s.
        for (var wait = 0; wait < 16 && (await Read(ct)).Play.Inbox.Count == 0; wait++) await Pause(500, ct);
        for (var n = 0; n < 10; n++)
        {
            var (_, _, play) = await Read(ct);
            // The soonest to expire first (Ehsan 2026-11-08: "many of them about to expire").
            var entry = play.Inbox.Where(e => match(e) && e.Collect?.NotClickable is null).OrderBy(e => ChoreRules.MinutesLeft(e.Expires) ?? double.MaxValue).FirstOrDefault();
            if (entry is null)
            {
                if (n == 0) log($"autoplay: inbox: no {what} ({play.Inbox.Count} entries: {string.Join("; ", play.Inbox.Select(e => $"[{e.Title}|{e.Message}|{e.Icon}|{e.Expires}]").Take(10))}). Screen dump: {DumpScreen("inbox")}");
                break;
            }
            if (dryRun)
            {
                Shot($"autoplay: WOULD CLICK {entry.Collect!.Name} (dry run: NOT pressed)", entry.Collect.Rect!.Value, always: true);
                break;
            }
            var count = play.Inbox.Count(match);
            // An entry the game keeps after Collect (2026-11-06: KaelStormVex's energy gifts, twice; the turn's error paused its energy farming): logged and
            // left, not the turn's error.
            try
            {
                await Click("inbox-collect", t => t.Id == entry.Collect!.Id, (s2, lazy) => Top(s2) != PlayTargets.InboxView || lazy().Inbox.Count(match) < count,
                    $"\"{entry.Title}\" is collected", ct);
            }
            catch (PlayStopException e)
            {
                log($"autoplay: inbox: Collect on [{entry.Title}|{entry.Message}|{entry.Icon}] did not take it ({e.Message}); left in the inbox. Screen dump: {DumpScreen("inbox collect")}");
                break;
            }
            // The game's "the most is 10: claiming loses the rest" (MissingResourceOverlay, 2026-11-10 07:51: closed and claimed again ten times): closed, the
            // entry left in the inbox, nothing more of it claimed now (the tokens are spent first, the next inbox check claims it).
            var s3 = await Settled(ct);
            if (Top(s3) == "MissingResourceOverlay")
            {
                await Back("MissingResourceOverlay", ct);
                log($"autoplay: inbox: [{entry.Title}|{entry.Message}] would go over the cap (the game warns some would be lost): left in the inbox");
                break;
            }
            _inboxTaken++;
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
            await Pause(500, ct);
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
            // The wheel first; a board that does not follow it is dragged (2026-11-01, as the Tag Team board).
            if (i < 5)
            {
                if (MouseInput.Wheel(w, cx, cy, down ? -2 : 2) is { } notSent) throw new PlayStopException("Not scrolled: " + notSent);
            }
            else
            {
                var dy = Math.Clamp(mine.Rect.Value.Center.Y - cy, -w.Client.Height * 0.3f, w.Client.Height * 0.3f);
                if (MouseInput.Drag(w, cx, cy + dy / 2, cx, cy - dy / 2) is { } notDragged) throw new PlayStopException("Not dragged: " + notDragged);
            }
            log($"autoplay: scrolled the board {(down ? "down" : "up")} towards opponent {userId}");
            await Task.Delay(400, ct);
        }
        throw new PlayStopException($"Opponent {userId} not reachable by scrolling.");
    }

    /// <summary>Waits for the battle to end (no input but the Auto button, once, when auto is off); the result screen's read, if any.</summary>
    private async Task<PlayScan?> WaitBattle(CancellationToken ct, string resultView = PlayTargets.ArenaResultView)
    {
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(20); // dungeon runs took 9-10 minutes at x1 (2026-10-30)
        // An arena round that stalls (healers on both sides, 2026-11-01: 20 minutes in round 1 of 3) is skipped: a defeat, the rest of the game goes on.
        var skipMinutes = AutoplaySettings.Load(ExtractorOutput).Rules.ArenaRoundSkipMinutes;
        var skipAt = skipMinutes > 0 && resultView is PlayTargets.ArenaResultView or "BattleFinishArena3x3Dialog" ? DateTime.UtcNow + TimeSpan.FromMinutes(skipMinutes) : (DateTime?)null;
        var autoTried = false;
        var speedTries = 0;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(500, ct);
            var s = await ReadState(ct);
            // A tutorial step or a level-up in the middle of a battle (a new account's 1-7, 2026-11-03) holds it until it is answered.
            if (Top(s) == "WizardView" || TapAway(Top(s))) { await DismissLevelUp(ct); continue; }
            if (Has(s, resultView))
            {
                await Task.Delay(1200, ct); // the result's numbers fill in
                var (_, _, play) = await Read(ct);
                return play;
            }
            // Live Arena's result overlay before its result dialog (2026-11-05: the bot waited 20 minutes on it): tapped on, the dialog comes next.
            if (Top(s) == "LiveArenaBattleFinishOverlay" && resultView != "LiveArenaBattleFinishOverlay")
            {
                await Task.Delay(1500, ct);
                try { await Click("back", t => t.View == "LiveArenaBattleFinishOverlay", (s2, _) => Top(s2) != "LiveArenaBattleFinishOverlay", "the result overlay closes", ct, within: TimeSpan.FromSeconds(8)); }
                catch (PlayStopException e) { log($"autoplay: the live arena result overlay did not close ({e.Message}); waiting"); }
                continue;
            }
            if (!s.InBattle) return null; // the battle ended somewhere else (the game moved on)
            if (skipAt is { } skip && DateTime.UtcNow >= skip && Has(s, PlayTargets.BattleHudView) && !s.UiBlocked)
            {
                log($"autoplay: this arena round has run {skipMinutes} minutes: skipping it");
                skipAt = DateTime.UtcNow + TimeSpan.FromMinutes(skipMinutes); // the next round's own limit
                await Click("battle-pause", null, (s2, _) => Top(s2) == "BattlePauseDialog", "the pause menu", ct, within: TimeSpan.FromSeconds(6));
                await Task.Delay(500, ct);
                await Click("battle-giveup", null, (s2, _) => Top(s2) == UiState.MessageBoxView, "Skip Battle", ct, within: TimeSpan.FromSeconds(6));
                await Task.Delay(500, ct);
                await Click("battle-giveup-yes", null, (s2, _) => Top(s2) != UiState.MessageBoxView, "the skip confirmed", ct, within: TimeSpan.FromSeconds(6));
                until = DateTime.UtcNow + TimeSpan.FromMinutes(20);
                continue;
            }
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
    /// <summary>
    /// The account's level-up screen (a new account levels every few battles, 2026-11-03) over whatever was open: it has no button and closes on a tap, here in
    /// its top middle, where the result screen under it has no button (2026-11-03: a tap lower opened a champion of the result).
    /// </summary>
    private static bool TapAway(string? top) => top is "LevelUpOverlay" || top?.EndsWith("IntroOverlay", StringComparison.Ordinal) == true;

    /// <summary>
    /// What a new account puts on top, in any order (2026-11-03): the game's tutorial (Next, else where its arrow points), the account's level-up and a new
    /// feature's intro (no button: tapped at the top middle, where the screen under them has no button; a tap lower opened a champion of a result).
    /// </summary>
    private async Task DismissLevelUp(CancellationToken ct)
    {
        for (var i = 0; i < 30; i++)
        {
            var (st, targets, _) = await Read(ct);
            var top = Top(st);
            // The tutorial's arrow or Next over another screen counts too (2026-11-09, the third account's newly opened Chimera: the tutorial lay over the
            // Clan Boss screen, not on top, and swallowed every click on Close; Ehsan: "the game forces you to click on specific areas one after another").
            if (top == "WizardView" || targets.Any(t => t.Kind is "wizard-next" or "wizard-arrow"))
            {
                var step = targets.FirstOrDefault(t => t.Kind == "wizard-next") ?? targets.FirstOrDefault(t => t.Kind == "wizard-arrow");
                if (step is null) { log($"autoplay: a tutorial step with no Next and no arrow. Screen dump: {DumpScreen("tutorial")}"); return; }
                Shot($"autoplay: tutorial: {step.Name}", step.Rect!.Value, always: true);
                await Click(step.Kind, null, (_, _) => true, step.Name, ct);
            }
            else if (TapAway(top))
            {
                var (w, why) = GameWindow.Find();
                if (w is null) throw new PlayStopException(why!);
                if (MouseInput.EnsureForeground(w) is { } behind) throw new PlayStopException(behind);
                if (MouseInput.Click(w, w.Client.Width / 2f, w.Client.Height * 0.07f, new Random()) is { } notSent) throw new PlayStopException(notSent);
                log($"autoplay: tapped {top} away");
            }
            else return;
            await Pause(1500, ct);
        }
    }

    /// <summary>What a result screen has over it (the account's level-up, a drop, a feature unlocked) closed; the result itself stays.</summary>
    private async Task ClearOverResult(CancellationToken ct, string view)
    {
        await DismissLevelUp(ct);
        for (var i = 0; i < 4 && await ReadState(ct) is var st && Has(st, view) && Top(st) is { } over && over != view; i++)
        {
            try { await Back(over, ct); }
            catch (PlayStopException)
            {
                var (w, why) = GameWindow.Find();
                if (w is null) throw new PlayStopException(why!);
                if (MouseInput.Click(w, w.Client.Width / 2f, w.Client.Height * 0.07f, new Random()) is { } notSent) throw new PlayStopException(notSent);
                log($"autoplay: tapped {over} away");
            }
            await Pause(1200, ct);
            await DismissLevelUp(ct);
        }
    }

    private async Task LeaveResult(CancellationToken ct, string view = PlayTargets.ArenaResultView)
    {
        await DismissLevelUp(ct);
        // A window over the result (a champion's drop, a feature unlocked, 2026-11-03): its own close, else a tap at the top middle.
        for (var i = 0; i < 4 && await ReadState(ct) is var st && Has(st, view) && Top(st) is { } over && over != view; i++)
        {
            try { await Back(over, ct); }
            catch (PlayStopException)
            {
                var (w, why) = GameWindow.Find();
                if (w is null) throw new PlayStopException(why!);
                if (MouseInput.Click(w, w.Client.Width / 2f, w.Client.Height * 0.07f, new Random()) is { } notSent) throw new PlayStopException(notSent);
                log($"autoplay: tapped {over} away");
            }
            await Pause(1200, ct);
            await DismissLevelUp(ct);
        }
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
        try { await Click(kind, null, (s2, _) => Top(s2) != view, $"{view} closes", ct, view: view); }
        catch (PlayStopException e) when (e.Message.Contains("outside the client area", StringComparison.Ordinal))
        {
            // Its close is off the window: the game's back key instead (Esc closes the screen on top, nothing else).
            if (Game.Window.GameWindow.Find().Window is not { } w || Input.MouseInput.SendEscape(w) is { } why) throw;
            log($"autoplay: {view}: its close is off the window ({e.Message}): Esc sent");
            await Pause(1200, ct);
            if (Top(await ReadState(ct)) == view) throw;
        }
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
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _nav.FitWindow();
        var fit = clock.ElapsedMilliseconds;
        var (s, targets, play) = await Read(ct);
        var read = clock.ElapsedMilliseconds - fit;
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
        await Task.Delay(_random.Next(40, 110), ct);
        _beforeClick = Signature(s, targets);
        // A small window floating over the point (a screen recorder's; seen 2026-10-31): another point of the same target.
        var sent = MouseInput.Click(w, x, y, _random);
        foreach (var (fx, fy) in new[] { (0.15f, 0.15f), (0.85f, 0.15f), (0.15f, 0.85f), (0.85f, 0.85f) })
        {
            if (sent is null || !sent.StartsWith("The window under", StringComparison.Ordinal)) break;
            (x, y) = (r.X + r.Width * fx, r.Y + r.Height * fy);
            sent = MouseInput.Click(w, x, y, _random);
        }
        if (sent is { } notSent) throw new PlayStopException($"Not clicked ({target.Name}): {notSent}");
        _clickedUtc = MouseInput.LastSentUtc;
        var sentAt = clock.ElapsedMilliseconds;
        var since = _lastClickDone == default ? "" : $"{(DateTime.UtcNow - _lastClickDone).TotalMilliseconds - sentAt:0} ms before, ";
        log($"autoplay: clicked {target.Name} at ({x:0},{y:0}) [{since}fit {fit}, read {read}, sent {sentAt - fit - read} ms]");
        try { await AnswerAsync(); } finally { _lastClickDone = DateTime.UtcNow; }
        if (_lastAnswerMs > 1500) log($"autoplay: the game answered {target.Name} after {_lastAnswerMs} ms");
        return;

        async Task AnswerAsync()
        {
        var tutorialFollowed = false;
        var answered = System.Diagnostics.Stopwatch.StartNew();

        var until = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(8));
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(100, ct);
            var s2 = await ReadState(ct);
            PlayScan? cache = null;
            if (expect(s2, () => cache ??= ReadSync().Play)) { _lastAnswerMs = answered.ElapsedMilliseconds; return; }
            // A tutorial step, level-up or intro the click brought (a new account, 2026-11-03): answered, then the click's screen is waited for again.
            if (target.Kind is not ("wizard-next" or "wizard-arrow") && (Top(s2) == "WizardView" || TapAway(Top(s2))))
            {
                await DismissLevelUp(ct);
                until = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(8));
            }
        }
        // A tutorial over the screen (its arrow or Next, not on top) takes every tap until it is followed: followed, then the click's answer is
        // looked for once more (the click itself is not repeated: the tutorial may have done what it was for).
        if (target.Kind is not ("wizard-next" or "wizard-arrow") && !tutorialFollowed && (await Read(ct)).Targets.Any(t => t.Kind is "wizard-next" or "wizard-arrow"))
        {
            tutorialFollowed = true;
            log($"autoplay: a tutorial is up over {Top(await ReadState(ct))}: following it");
            await DismissLevelUp(ct);
            var after = await ReadState(ct);
            if (expect(after, () => ReadSync().Play)) return;
        }
        var end = await ReadState(ct);
        // The game's "not unlocked yet" (2026-11-04, a new account's Tag Team Arena): the area waits quietly (AreaLockedException); going home closes it.
        if (Top(end) == FeatureLockedView) throw new AreaLockedException($"{target.Name} is not unlocked on this account yet");
        // Clan Boss, Hydra and Chimera without a clan (2026-11-04, a new account): the same quiet wait; joining a clan is Ehsan's.
        if (Top(end)?.EndsWith("AllianceLessDialog", StringComparison.Ordinal) == true) throw new AreaLockedException($"{target.Name}: this account is not in a clan");
        throw new PlayStopException($"After clicking {target.Name}, expected {expected}; the screen shows {Top(end)}.");
        }
    }

    /// <summary>When the last click's answer came (the gap to the next click is the time between) and how long that answer took.</summary>
    private DateTime _lastClickDone;
    private long _lastAnswerMs;

    /// <summary>What the game shows for a feature the account has not unlocked (its BTN_Close closes it).</summary>
    private const string FeatureLockedView = "FeatureLockedOverlay";

    /// <summary>
    /// The visible screens' elements (active ones: name, binding path, bound context, rectangle, clickable) to a text file in the run
    /// folder, so a target rule that found nothing can be fixed from one run. Returns the file.
    /// </summary>
    /// <summary>The screen's element trees to a file (autoplay-explore --tree; read only).</summary>
    public string DumpScreenFile(string why) => DumpScreen(why);

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

    /// <summary>Reads until the top view has stayed the same for 0.8 s (popups that queue up have appeared), at most 8 s.</summary>
    private async Task<UiState> Settled(CancellationToken ct)
    {
        var s = await ReadState(ct);
        var since = DateTime.UtcNow;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(150, ct);
            var s2 = await ReadState(ct);
            if (Top(s2) != Top(s)) since = DateTime.UtcNow;
            s = s2;
            if (DateTime.UtcNow - since >= TimeSpan.FromSeconds(0.8)) break;
        }
        return s;
    }

    /// <summary>The screen as the last <see cref="Click"/> saw it just before clicking, and when that click was sent.</summary>
    private string? _beforeClick;
    private DateTime _clickedUtc = DateTime.MinValue;

    /// <summary>
    /// A wait for the game to answer the last click, at most <paramref name="ms"/> (Ehsan 2026-11-06: "you seem to be very slow between the clicks"; the
    /// fixed waits after every click made most of the time). It ends early once the screen differs from the one the click was made on and has
    /// then held still for two reads in a row (its views, the targets, where they are and whether they can be clicked). Any input sent since that click
    /// (a drag, a wheel, a raw tap), no click to compare with, or a screen that never changes: the whole wait, as before.
    /// </summary>
    private async Task Pause(int ms, CancellationToken ct)
    {
        var until = DateTime.UtcNow + TimeSpan.FromMilliseconds(ms);
        if (_beforeClick is not { } before || MouseInput.LastSentUtc > _clickedUtc || ms <= 300)
        {
            await Task.Delay(ms, ct);
            return;
        }
        await Task.Delay(150, ct);
        string? last = null;
        var same = 0;
        while (DateTime.UtcNow < until)
        {
            var (s, targets, _) = ReadSync();
            var now = s is null ? null : Signature(s, targets);
            if (now is not null && now != before && now == last)
            {
                if (++same >= 2) return;
            }
            else same = 0;
            last = now;
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return;
            await Task.Delay(left < TimeSpan.FromMilliseconds(120) ? left : TimeSpan.FromMilliseconds(120), ct);
        }
    }

    /// <summary>What the screen shows, as one string: its visible views and every target with its rectangle and clickability.</summary>
    private static string Signature(UiState s, List<UiTarget> targets) =>
        string.Join("|", s.Active.Where(v => v.Visible).Select(v => v.Name)) + "#"
        + string.Join("|", targets.Select(t => $"{t.Kind}:{t.Name}:{t.Rect}:{t.NotClickable is null}"));

    private static bool Has(UiState s, string view) => s.Active.Any(v => v.Name == view);

    /// <summary>The Classic Arena dialog is the screen in front (its HUD may sit above it).</summary>
    private static bool OnArena(UiState s) => Top(s) == PlayTargets.ArenaDialogView || Has(s, PlayTargets.ArenaDialogView) && Top(s) == PlayTargets.ArenaHudView;

    /// <summary>The view on top, a HUD counted as the screen under it (ArenaHUD over ArenaMap, BattleHUD over the battle).</summary>
    private static string? Top(UiState s) => s.Top?.Name;

    private string? Shot(string caption, ClientRect r, bool always = false) =>
        always ? Screen.Overlay(folder, $"{++_shot:000}-autoplay.png", caption, r, WindowPolicy.CaptureProblem) : null;

    /// <summary>
    /// The way home for every area: the account's level-up screen and a battle's result screen closed first (2026-11-03: the gear driver waits for a result
    /// screen to go by itself, and a Campaign result stays).
    /// </summary>
    /// <summary>A web page on top (an event's, the tournaments'): its X, a few taps (the page takes a moment to answer).</summary>
    private async Task CloseWebPages(CancellationToken ct)
    {
        for (var i = 0; i < 6 && Top(await ReadState(ct)) is { } top && top.EndsWith("WebView", StringComparison.Ordinal); i++)
        {
            await Click("event-close", null, (_, _) => true, $"{top}'s X", ct);
            await Pause(1500, ct);
        }
    }

    private async Task<List<string>> HomeAsync(UiAction a, CancellationToken ct, string? stopAt = null)
    {
        await DismissLevelUp(ct);
        await CloseWebPages(ct);
        var s = await ReadState(ct);
        // A window over the result (a champion's info, 2026-11-03) is closed by its own button, then the result.
        if (s.Active.Select(v => v.Name).FirstOrDefault(n => n.StartsWith("BattleFinish", StringComparison.Ordinal)) is { } result && !InLiveMatch(s))
        {
            for (var i = 0; i < 3 && Top(s) is { } over && over != result; i++)
            {
                await Back(over, ct);
                await Pause(800, ct);
                s = await ReadState(ct);
            }
            if (Top(s) == result) await LeaveResult(ct, result);
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var left = await _nav.GoHome(a, ct, stopAt);
        // The travel measured (plan/navigation.md): screens closed on the way back and the seconds it took.
        if (left.Count > 0) log($"{a.Text.Replace("autoplay: ", "autoplay: travel: ", StringComparison.Ordinal)}: {left.Count} screen(s) closed in {sw.Elapsed.TotalSeconds:0.0} s");
        return left;
    }

    /// <summary>
    /// Towards an area under the Battle menu (Ehsan 2026-11-12: "as I can see you go to the village always before the next"): when the Battle menu is
    /// under the screens open, only the screens above it are closed (no village and Battle again); otherwise the village.
    /// </summary>
    private async Task<List<string>> TowardsBattleMenu(UiState s, CancellationToken ct) =>
        s.Active.Any(v => v.Name == PlayTargets.BattleMenuView) && !s.Active.Any(v => v.Name.StartsWith("BattleFinish", StringComparison.Ordinal))
            ? await HomeAsync(Action("back to the Battle menu"), ct, PlayTargets.BattleMenuView)
            : await HomeAsync(Action("to the village"), ct);

    private static UiAction Action(string text) => new(ActionKind.OpenChampion, 0, "autoplay: " + text);
}
