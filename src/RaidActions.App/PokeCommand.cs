using RaidActions.App.Input;
using RaidActions.Game.Memory;
using RaidActions.Game.Window;

namespace RaidActions.App;

/// <summary>
/// <c>RaidActions poke &lt;TopView&gt; tap x y | drag x1 y1 x2 y2 | wheel x y notches</c>: one input at the usual 1298-wide client's pixels, for
/// exploring a new screen by hand (a developer's tool, never used by Auto Play; run it only with no bot running). Refused unless the game's top
/// screen is exactly the one named, and always on a purchase, offer or payment screen (2026-11-01: a blind tap hit a popup's Buy and opened a real
/// checkout, nothing paid).
/// </summary>
public static class PokeCommand
{
    private static bool Purchase(string view) =>
        new[] { "Bank", "Offer", "Payment", "Shop", "Purchase", "Paid", "Refill" }.Any(w => view.Contains(w, StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args, string output, TextWriter o)
    {
        if (args.Length < 3) { o.WriteLine("usage: poke <TopView> tap x y | drag x1 y1 x2 y2 | wheel x y notches"); return Cli.Usage; }
        var (live, problem) = LiveGame.Open(GameCommands.LivePaths(output));
        if (live is null) { o.WriteLine(problem); return Cli.Failed; }
        using (live)
        {
            var (state, why) = live.ReadUi();
            var top = state?.Top?.Name;
            if (top is null) { o.WriteLine("The top screen cannot be read: nothing sent. " + why); return Cli.Failed; }
            if (Purchase(top)) { o.WriteLine($"Refused: {top} is a purchase, offer or payment screen."); return Cli.Failed; }
            if (top != args[1]) { o.WriteLine($"Refused: the top screen is {top}, not {args[1]}."); return Cli.Failed; }
        }
        var (w, notFound) = GameWindow.Find();
        if (w is null) { o.WriteLine(notFound); return Cli.Failed; }
        var scale = w.Client.Width / 1298f;
        float F(int i) => float.Parse(args[i], System.Globalization.CultureInfo.InvariantCulture) * scale;
        var sent = args[2] switch
        {
            "tap" => MouseInput.Click(w, F(3), F(4), new Random()),
            "drag" => MouseInput.Drag(w, F(3), F(4), F(5), F(6)),
            "wheel" => MouseInput.Wheel(w, F(3), F(4), int.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture)),
            _ => "usage: poke <TopView> tap x y | drag x1 y1 x2 y2 | wheel x y notches",
        };
        o.WriteLine(sent ?? "sent");
        return sent is null ? 0 : Cli.Failed;
    }
}
