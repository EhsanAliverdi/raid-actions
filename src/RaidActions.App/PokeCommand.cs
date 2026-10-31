using RaidActions.App.Input;
using RaidActions.Game.Window;

namespace RaidActions.App;

/// <summary>
/// <c>RaidActions poke tap x y | drag x1 y1 x2 y2 | wheel x y notches</c>: one input at client pixels, for exploring a new screen by hand
/// (a developer's tool, never used by Auto Play; run it only with no bot running).
/// </summary>
public static class PokeCommand
{
    public static int Run(string[] args, TextWriter o)
    {
        var (w, why) = GameWindow.Find();
        if (w is null) { o.WriteLine(why); return Cli.Failed; }
        float F(int i) => float.Parse(args[i], System.Globalization.CultureInfo.InvariantCulture);
        var problem = args[1] switch
        {
            "tap" => MouseInput.Click(w, F(2), F(3), new Random()),
            "drag" => MouseInput.Drag(w, F(2), F(3), F(4), F(5)),
            "wheel" => MouseInput.Wheel(w, F(2), F(3), (int)F(4)),
            _ => "usage: poke tap x y | drag x1 y1 x2 y2 | wheel x y notches",
        };
        o.WriteLine(problem ?? "sent");
        return problem is null ? 0 : Cli.Failed;
    }
}
