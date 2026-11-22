using RaidActions.Autoplay;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

public class SimCompareTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "simcompare-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Event_line_names_area_stage_and_both_numbers()
    {
        Assert.Equal("sim2: faction-wars 5011009: old 83% new 90% (50 runs)", SimCompare.EventLine("faction-wars", 5011009, 0.83, 0.9, 50));
        Assert.Equal("sim2: doom-tower 7011020: old 50% new n/a (0 runs)", SimCompare.EventLine("doom-tower", 7011020, 0.5, null, 0));
    }

    [Fact]
    public void Not_ported_line_is_logged_as_the_engine_reports_it()
    {
        Assert.Equal("new engine: not ported: Expressions.Foo (x.txt)", SimCompare.NotPortedLine("not ported: Expressions.Foo (x.txt)"));
        Assert.Equal("new engine: not ported: boom", SimCompare.NotPortedLine("boom"));
    }

    [Fact]
    public void Rows_are_appended_with_a_header_and_quoted_notes()
    {
        var t = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
        SimCompare.Append(_dir, SimCompare.Row(t, 165078741, "doom-tower", 7011020, [1, 2, 3], 0.8, 0.7, 50, "2 runs stopped: a, b"));
        SimCompare.Append(_dir, SimCompare.Row(t, 165078741, "faction-wars", 5011009, [4], 1, null, 0, null));
        var lines = File.ReadAllLines(SimCompare.PathIn(_dir));
        Assert.Equal(SimCompare.Header, lines[0]);
        Assert.Equal(3, lines.Length);
        var f = SimCompare.Fields(lines[1]);
        Assert.Equal(["2026-11-21T10:00:00.0000000Z", "165078741", "doom-tower", "7011020", "1 2 3", "0.8", "0.7", "50", "2 runs stopped: a, b", ""], f);
        Assert.Equal("", SimCompare.Fields(lines[2])[6]);
    }

    [Fact]
    public void An_energy_push_row_gets_the_result_of_a_push_rows_battle_not_of_a_farm_battle()
    {
        var t = new DateTime(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);
        SimCompare.Append(_dir, SimCompare.Row(t, 1, "energy-push", 7041012, [1, 2], 0.9, 0.0, 50, null));
        // A food farm battle right after: not the push's result.
        Assert.False(SimCompare.ResolveFromEvent(_dir, new AutoplayEvent(t.AddMinutes(2), "energy", "battle", "Victory Campaign 12: Brimstone Path Hard 12-3: 6 Energy [row farm-12-3]. 60 s")));
        // The push row's battle: its result.
        Assert.True(SimCompare.ResolveFromEvent(_dir, new AutoplayEvent(t.AddMinutes(4), "energy", "battle", "Defeat Void Keep Normal stage 12: 12 Energy [row push-keep-void]. 80 s")));
        Assert.Equal("loss", SimCompare.Fields(File.ReadAllLines(SimCompare.PathIn(_dir))[1])[9]);
        SimCompare.Append(_dir, SimCompare.Row(t.AddMinutes(10), 1, "energy-push", 1123010, [1], 0.8, 0.9, 50, null));
        Assert.True(SimCompare.ResolveFromEvent(_dir, new AutoplayEvent(t.AddMinutes(12), "energy", "battle", "Victory Campaign 10: Brutal 10-2: 6 Energy [row brutal-push]. 70 s")));
    }

    [Fact]
    public void A_battle_event_fills_the_newest_open_row_of_its_area_only()
    {
        var t = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
        SimCompare.Append(_dir, SimCompare.Row(t, 1, "doom-tower", 7011020, [1, 2], 0.8, 0.7, 50, null));
        SimCompare.Append(_dir, SimCompare.Row(t.AddMinutes(1), 1, "faction-wars", 5011009, [3], 0.9, 0.9, 50, null));
        SimCompare.Append(_dir, SimCompare.Row(t.AddMinutes(2), 1, "doom-tower", 7011030, [1, 2], 0.6, 0.5, 50, null));
        Assert.False(SimCompare.ResolveFromEvent(_dir, new AutoplayEvent(t.AddMinutes(5), "doom-tower", "hold", "Defeat x")));          // not a battle
        Assert.False(SimCompare.ResolveFromEvent(_dir, new AutoplayEvent(t.AddMinutes(5), "doom-tower", "battle", "Unknown tower floor")));   // no result
        Assert.True(SimCompare.ResolveFromEvent(_dir, new AutoplayEvent(t.AddMinutes(5), "doom-tower", "battle", "Defeat Normal tower floor. x")));
        var rows = File.ReadAllLines(SimCompare.PathIn(_dir)).Skip(1).Select(SimCompare.Fields).ToList();
        Assert.Equal("", rows[0][9]);
        Assert.Equal("", rows[1][9]);
        Assert.Equal("loss", rows[2][9]);
        Assert.True(SimCompare.ResolveFromEvent(_dir, new AutoplayEvent(t.AddMinutes(6), "doom-tower", "battle", "Victory Normal tower floor. x")));
        Assert.Equal("win", SimCompare.Fields(File.ReadAllLines(SimCompare.PathIn(_dir))[1])[9]);
    }

    [Fact]
    public void A_row_is_not_joined_to_a_battle_hours_later_or_one_before_it_and_names_must_match()
    {
        var t = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
        SimCompare.Append(_dir, SimCompare.Row(t, 1, "cursed-city", 2099001, [7, 8], 0.8, 0.7, 50, null));
        Assert.False(SimCompare.ResolveFromEvent(_dir, new AutoplayEvent(t.AddMinutes(-1), "cursed-city", "battle", "Victory x")));
        Assert.False(SimCompare.ResolveFromEvent(_dir, new AutoplayEvent(t + SimCompare.JoinWindow + TimeSpan.FromMinutes(1), "cursed-city", "battle", "Victory x")));
        var names = new Dictionary<string, string> { ["7"] = "Kael", ["8"] = "Hodin" };
        Assert.False(SimCompare.ResolveFromEvent(_dir, new AutoplayEvent(t.AddMinutes(3), "cursed-city", "battle", "Victory stage (team Apothecary, Hodin). x"), names));
        Assert.True(SimCompare.ResolveFromEvent(_dir, new AutoplayEvent(t.AddMinutes(3), "cursed-city", "battle", "Victory stage (team Kael, Hodin). x"), names));
    }
}
