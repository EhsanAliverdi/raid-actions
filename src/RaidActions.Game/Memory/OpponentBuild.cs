namespace RaidActions.Game.Memory;

/// <summary>
/// What an arena opponent's HeroSlotSetup holds about the hero's build (dump 11.95.0 layouts, raw offsets): the stat bonuses of every source summed per stat
/// (index = StatKindId 1..10: Health, Attack, Defence, Speed, Resistance, Accuracy, CritChance, CritDamage, CritHeal, IgnoreDefence; Fixed 32.32 as double),
/// the skills it adds and its counts. Percent bonuses are fractions (0.25 = +25%), flat ones absolute.
/// </summary>
/// <param name="Artifacts">ArtifactSetups count (0x78): each has PercentBonus 0x38 / FlatBonus 0x40 (BattleStats).</param>
/// <param name="ArtifactSets">ArtifactSetSetups count (0x80): each has SkillBonus 0x10 (SkillTypeId), StatBonus 0x18, StatBonuses 0x20.</param>
/// <param name="Masteries">MasteryIds count (0x88).</param>
/// <param name="Skills">SkillSetups count (0x70), the hero's own skills with their levels.</param>
/// <param name="Power">HeroSlotSetup.Power (0xD0), the game's power for the hero; null when unset.</param>
public sealed record HeroBuildRead(int Artifacts, int ArtifactSets, int Masteries, int Skills, bool Blessing, bool Relic, bool Academy, double? Power,
    double[] Percent, double[] Flat, List<int> MasteryIds, List<(int Id, int Level)> OwnSkills, List<(int Id, int Level)> ExtraSkills)
{
    /// <summary>Whether the setup carries any gear at all.</summary>
    public bool HasGear => Artifacts > 0 || ArtifactSets > 0 || Relic || Blessing || Academy;

    public override string ToString() => $"build art {Artifacts}, sets {ArtifactSets}, mast {Masteries}, skills {Skills}, bless {(Blessing ? 1 : 0)}, relic {(Relic ? 1 : 0)}, academy {(Academy ? 1 : 0)}, power {(Power is { } p ? p.ToString("N0") : "unset")}";
}
