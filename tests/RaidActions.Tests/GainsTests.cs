using RaidActions.Autoplay;

namespace RaidActions.Tests;

public class GainsTests
{
    private static Holdings H(Dictionary<string, double> items, Dictionary<int, string>? gear = null, Dictionary<int, string>? heroes = null) =>
        new(items, gear ?? [], heroes ?? []);

    [Fact]
    public void What_went_up_is_listed_with_new_gear_and_champions_and_spending_and_regeneration_are_not()
    {
        var before = H(new() { ["Energy"] = 20, ["Silver"] = 1_000, ["Gem"] = 50, ["Mystery shard"] = 1 }, new() { [1] = "Rare 4★ Speed Boots" }, new() { [10] = "Kael (Rare)" });
        var after = H(new() { ["Energy"] = 122, ["Silver"] = 900, ["Gem"] = 50, ["Mystery shard"] = 3 },
            new() { [1] = "Rare 4★ Speed Boots", [2] = "Epic 5★ Lifesteal Weapon" }, new() { [10] = "Kael (Rare)", [11] = "Warmaiden (Rare)" });
        Assert.Equal("+102 Energy, +2 Mystery shard, gear: Epic 5★ Lifesteal Weapon, champion: Warmaiden (Rare)", Gains.Describe(before, after));
    }

    [Fact]
    public void Nothing_in_but_the_energy_clock_is_no_reward()
    {
        Assert.Null(Gains.Describe(H(new() { ["Energy"] = 20 }), H(new() { ["Energy"] = 22 })));
    }
}
