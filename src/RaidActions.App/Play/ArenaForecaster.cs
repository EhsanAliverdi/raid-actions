using System.Globalization;
using RaidActions.Autoplay;
using RaidExtractor;
using RaidExtractor.Battle;
using RaidExtractor.Core.GameBattle;

namespace RaidActions.App.Play;

/// <summary>
/// Classic Arena forecast: our attack team against an opponent's defence team from the board, on the new engine (GameSimBuilder's pieces). Our side is built as
/// <see cref="GameSimBuilder.Build"/> builds the player's team (export stats and gear, masteries and gear skills); the enemy as the game builds a stage slot
/// (WaveSlot: type, level, grade, every skill at its maximum, NO gear, no leader aura). Raid-extractor's Core is not changed: this repeats Build's body
/// with the enemy slots given instead of the stage's (the arena stage has none).
/// APPROX: a real opponent has gear, so the enemy is weaker here than in the game and the rate is optimistic.
/// </summary>
public static class ArenaForecaster
{
    /// <summary>The Classic Arena stage ids of the static data (3019001..3019003); the first one the catalog knows.</summary>
    private static readonly int[] ArenaStages = [3019003, 3019002, 3019001];

    public static ArenaForecast WinRate(SavedExport data, GameDataLookup gd, string outputDir, IReadOnlyList<int> team, IReadOnlyList<ArenaEnemyHero> enemy, int runs, int seed0 = 1, double? maxSeconds = null)
    {
        if (team.Count == 0) return new ArenaForecast(null, 0, "no attack team known");
        if (enemy.Count == 0) return new ArenaForecast(null, 0, "no enemy team");
        var stageId = ArenaStages.FirstOrDefault(id => StageCatalog.Get(outputDir, id) is not null);
        if (stageId == 0) return new ArenaForecast(null, 0, "no Classic Arena stage in the static data");
        int wins = 0, done = 0, errs = 0; string? first = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < runs; i++)
        {
            if (maxSeconds is { } box && i > 0 && clock.Elapsed.TotalSeconds >= box) break;
            if (i >= 3 && errs == i) break;
            BattleProcessor? e;
            try { e = Build(data, gd, outputDir, stageId, team, enemy, seed0 + i); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return new ArenaForecast(null, 0, $"build failed: {ex.GetType().Name}: {ex.Message}"); }
            if (e is null) return new ArenaForecast(null, 0, $"stage {stageId} or the team is not in the data");
            var r = GameSim.Run(e);
            if (r.Error is not null) { errs++; first ??= r.Error; continue; }
            done++;
            if (r.Win) wins++;
        }
        return done > 0 ? new ArenaForecast((double)wins / done, done, errs > 0 ? $"{errs} runs stopped: {first}" : null) : new ArenaForecast(null, 0, first ?? "no run finished");
    }

    /// <summary>GameSimBuilder.Build with the enemy slots from the board (no preset: the arena has none on the accounts seen).</summary>
    public static BattleProcessor? Build(SavedExport data, GameDataLookup gd, string outputDir, int stageId, IReadOnlyList<int> team, IReadOnlyList<ArenaEnemyHero> enemy, int seed)
    {
        var gs = GameSim.Data;
        var old = GameSimBuilder.OldSetup(data, gd, outputDir, stageId, team, withMasteries: false);
        if (old is null) return null;
        var owner = data.Account.Id ?? 1;
        var heroes = new List<BattleHero>();
        for (var i = 0; i < team.Count && i < old.Team.Count; i++)
        {
            var ch = data.Champions.FirstOrDefault(c => c.Id == team[i]);
            if (ch is null) return null;
            var ht = gs.HeroType(ch.TypeId);
            var st = GameSimBuilder.StatsOf(old.Team[i]);
            var skills = ch.Skills.Select(s => BattleHeroFactory.ResolveSkill(gs, (s.TypeId, s.Level))).Concat(GameSimBuilder.GearSkills(gs, data, ch)).ToList();
            heroes.Add(new BattleHero
            {
                Id = i, TypeId = ch.TypeId, Level = ch.Level, Side = 0, Slot = i + 1, OwnerUserId = owner, DoubleAscend = ch.Awakening, IsBoss = ht?.HasBossData ?? false,
                IsUnkillableBoss = ht?.IsUnkillable ?? false, MaxHp = st[StatKind.Health], Hp = st[StatKind.Health], Speed = st[StatKind.Speed], BaseStats = st, Stats = st.Clone(),
                Tags = ht?.Tags.ToList() ?? [], IsWarlord = ht?.IsWarlord ?? false, Skills = skills,
            });
        }
        var slots = enemy.OrderBy(h => h.Slot).Select((h, i) =>
        {
            var skillIds = gd.Champions.GetValueOrDefault(h.TypeId / 10 * 10)?.Ascensions.FirstOrDefault(a => a.TypeId == h.TypeId)?.Forms.FirstOrDefault()?.SkillIds ?? [];
            var skills = skillIds.Select(id => (id, 1 + (gs.Skill(id)?.LevelBonuses.Count ?? 0))).ToList();
            return new WaveSlot(h.TypeId, i + 1, -1, 0, h.Grade, h.Level, false, false, skills) { TeamLeaderSlot = 0, DoubleAscend = h.DoubleAscend ?? 0 };
        }).ToList();
        var setup = new BattleSetup
        {
            Id = "sim", RandomSeed = seed, KindId = 1, StageId = stageId, MaxTurnsCount = 1500, DisabledEffects = [], FirstTeamOwnerId = owner, SecondTeamOwnerId = -1,
            SecondTeamWaveSlots = new Dictionary<int, List<WaveSlot>> { [1] = slots },
        };
        var state = new BattleState(setup, heroes, []);
        var cx = new BattleContext(setup, state, new BattleRecord(setup, 10), gs);
        var engine = new BattleProcessor(cx, 10);
        state.SecondTeam.SetGroup(cx);
        return engine;
    }
}
