using RaidActions.Autoplay;
using RaidExtractor;
using RaidExtractor.Battle;
using RaidExtractor.Core.GameBattle;

namespace RaidActions.App.Play;

/// <summary>
/// Classic Arena forecast: our attack team against an opponent's defence team from the board, on the new engine (GameSimBuilder's pieces). Our side is built as
/// <see cref="GameSimBuilder.Build"/> builds the player's team (export stats and gear, masteries and gear skills). The enemy as the game builds a slot (WaveSlot):
/// real: the board holds the HeroSlotSetup build (gear, set / relic / blessing / academy bonuses, masteries, skill levels): the bonuses are summed per stat into the
/// slot's percent / flat modifiers (BattleBonuses.InitModifiers adds them, UpdateWithBonuses applies them), the skills they add are listed, the leader slot gives
/// the aura. power-scaled: no build on the board: the no-gear hero's HP / ATK / DEF scaled so the team's power proxy matches the power the board shows, calibrated
/// on our own team (our power over our proxy). none: neither: type, level, grade and skills only (optimistic).
/// Raid-extractor's Core is not changed: this repeats Build's body with the enemy slots given (the arena stage has none).
/// </summary>
public static class ArenaForecaster
{
    /// <summary>The Classic Arena stage ids of the static data (3019001..3019003); the first one the catalog knows.</summary>
    private static readonly int[] ArenaStages = [3019003, 3019002, 3019001];

    private static readonly StatKind[] Kinds = [StatKind.Health, StatKind.Attack, StatKind.Defence, StatKind.Speed, StatKind.Resistance, StatKind.Accuracy, StatKind.CriticalChance, StatKind.CriticalDamage, StatKind.CriticalHeal, StatKind.IgnoreDefence];

    public const double MaxScale = 6;

    /// <summary>"real" when every hero has a build with gear, "power-scaled" when the team's power is known, else "none".</summary>
    public static string Mode(ArenaEnemyTeam enemy, bool canScale) =>
        enemy.Heroes.Count > 0 && enemy.Heroes.All(h => h.Build?.HasGear == true) ? "real" : canScale && enemy.Power is > 0 ? "power-scaled" : "none";

    public static ArenaForecast WinRate(SavedExport data, GameDataLookup gd, string outputDir, IReadOnlyList<int> team, ArenaEnemyTeam enemy, int runs, int seed0 = 1, double? maxSeconds = null, double? ourPower = null)
    {
        if (team.Count == 0) return new ArenaForecast(null, 0, "no attack team known");
        if (enemy.Heroes.Count == 0) return new ArenaForecast(null, 0, "no enemy team");
        var stageId = ArenaStages.FirstOrDefault(id => StageCatalog.Get(outputDir, id) is not null);
        if (stageId == 0) return new ArenaForecast(null, 0, "no Classic Arena stage in the static data");
        var mode = "none";
        double scale = 1;
        try
        {
            mode = Mode(enemy, ourPower is > 0);
            if (mode == "power-scaled")
            {
                var probe = Build(data, gd, outputDir, stageId, team, enemy, 1, 1, out var mine, out var theirs);
                if (probe is null) return new ArenaForecast(null, 0, "the team is not in the data", mode);
                // Our power per proxy unit calibrates the proxy; the enemy's no-gear proxy is raised to what its shown power says.
                var perProxy = ourPower!.Value / Math.Max(1, Proxy(mine));
                scale = Math.Clamp(enemy.Power!.Value / perProxy / Math.Max(1, Proxy(theirs)), 1, MaxScale);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new ArenaForecast(null, 0, $"build failed: {ex.GetType().Name}: {ex.Message}", mode); }
        int wins = 0, done = 0, errs = 0;
        string? first = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < runs; i++)
        {
            if (maxSeconds is { } box && i > 0 && clock.Elapsed.TotalSeconds >= box) break;
            if (i >= 3 && errs == i) break;
            BattleProcessor? e;
            try { e = Build(data, gd, outputDir, stageId, team, enemy, seed0 + i, scale, out _, out _); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return new ArenaForecast(null, 0, $"build failed: {ex.GetType().Name}: {ex.Message}", mode); }
            if (e is null) return new ArenaForecast(null, 0, $"stage {stageId} or the team is not in the data", mode);
            var r = GameSim.Run(e);
            if (r.Error is not null) { errs++; first ??= r.Error; continue; }
            done++;
            if (r.Win) wins++;
        }
        return done > 0 ? new ArenaForecast((double)wins / done, done, errs > 0 ? $"{errs} runs stopped: {first}" : null, mode) : new ArenaForecast(null, 0, first ?? "no run finished", mode);
    }

    /// <summary>Strength proxy of a team: the sum over heroes of (HP x ATK x DEF)^(1/3) (ArenaDuel.TeamProxy's form).</summary>
    public static double Proxy(IEnumerable<(double Hp, double Atk, double Def)> heroes) => heroes.Sum(h => Math.Pow(Math.Max(1, h.Hp) * Math.Max(1, h.Atk) * Math.Max(1, h.Def), 1.0 / 3));

    private static double Proxy(List<BattleHero> heroes) => Proxy(heroes.Select(h => (h.Stats[StatKind.Health].ToDouble(), h.Stats[StatKind.Attack].ToDouble(), h.Stats[StatKind.Defence].ToDouble())));

    /// <summary>The enemy hero's slot: the skills (its own at their levels, else every skill at its maximum; plus what sets, relic, blessing and masteries add) and the summed bonuses as the slot's modifiers.</summary>
    public static WaveSlot Slot(GameStatic gs, GameDataLookup gd, ArenaEnemyHero h, int index, int? leaderSlot, double scale)
    {
        var b = h.Build;
        var formSkills = gd.Champions.GetValueOrDefault(h.TypeId / 10 * 10)?.Ascensions.FirstOrDefault(a => a.TypeId == h.TypeId)?.Forms.FirstOrDefault()?.SkillIds ?? [];
        List<(int Id, int Level)> skills = b is { OwnSkills.Count: > 0 }
            ? [.. b.OwnSkills.Select(s => (s.Id, s.Level))]
            : [.. formSkills.Select(id => (id, 1 + (gs.Skill(id)?.LevelBonuses.Count ?? 0)))];
        if (b is not null)
            foreach (var s in b.ExtraSkills.Concat(b.MasteryIds.Select(m => new SkillLevel(m, 1))))
                if (!skills.Any(x => x.Id == s.Id)) skills.Add((s.Id, Math.Max(1, s.Level)));
        BattleStats? percent = null, flat = null;
        if (b is not null || scale > 1)
        {
            percent = new BattleStats();
            flat = new BattleStats();
            if (b is not null)
                for (var k = 0; k < Kinds.Length; k++)
                {
                    percent[Kinds[k]] = Fixed.FromDouble(b.Percent[k + 1]);
                    flat[Kinds[k]] = Fixed.FromDouble(b.Flat[k + 1]);
                }
            if (scale > 1)
                foreach (var k in new[] { StatKind.Health, StatKind.Attack, StatKind.Defence })
                    percent[k] += Fixed.FromDouble(scale - 1);
        }
        return new WaveSlot(h.TypeId, index + 1, -1, 0, h.Grade, h.Level, false, false, skills)
        {
            TeamLeaderSlot = leaderSlot ?? 0, DoubleAscend = h.DoubleAscend ?? 0, SlotPercent = percent, SlotFlat = flat, HasModifiers = percent is not null,
        };
    }

    /// <summary>GameSimBuilder.Build with the enemy slots from the board (no preset: the arena has none on the accounts seen). scale: power-scaling of the no-gear enemy's HP / ATK / DEF.</summary>
    public static BattleProcessor? Build(SavedExport data, GameDataLookup gd, string outputDir, int stageId, IReadOnlyList<int> team, ArenaEnemyTeam enemy, int seed, double scale,
        out List<BattleHero> mine, out List<BattleHero> theirs)
    {
        mine = [];
        theirs = [];
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
        var slots = enemy.Heroes.OrderBy(h => h.Slot).Select((h, i) => Slot(gs, gd, h, i, enemy.LeaderSlot, scale)).ToList();
        var setup = new BattleSetup
        {
            Id = "sim", RandomSeed = seed, KindId = 1, StageId = stageId, MaxTurnsCount = 1500, DisabledEffects = [], FirstTeamOwnerId = owner, SecondTeamOwnerId = -1,
            SecondTeamWaveSlots = new Dictionary<int, List<WaveSlot>> { [1] = slots },
        };
        var state = new BattleState(setup, heroes, []);
        var cx = new BattleContext(setup, state, new BattleRecord(setup, 10), gs);
        var engine = new BattleProcessor(cx, 10);
        state.SecondTeam.SetGroup(cx);
        mine = heroes;
        theirs = state.SecondTeam.Heroes;
        return engine;
    }
}
