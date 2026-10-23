using System.Runtime.InteropServices;

namespace RaidActions.App;

/// <summary>With arguments: the CLI. Without: the window (the console the exe gets from Explorer is hidden).</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && !(args[0] == "open" && args.Length == 2)) return RunCli(args);
        HideOwnConsole();
        var app = new App { InitialPlan = args.Length == 2 ? Path.GetFullPath(args[1]) : null };
        app.InitializeComponent();
        return app.Run();
    }

    private static int RunCli(string[] args)
    {
        Action? abort = null;
        using var kill = new KillSwitch();
        kill.Pressed += () =>
        {
            Console.Error.WriteLine($"{KillSwitch.Keys}: aborting.");
            abort?.Invoke();
        };
        Console.CancelKeyPress += (_, e) =>
        {
            if (abort is null) return; // nothing running: let Ctrl+C end the program
            e.Cancel = true;
            Console.Error.WriteLine("Ctrl+C: aborting.");
            abort();
        };
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (!kill.Registered) Console.Error.WriteLine(kill.Problem);
        return Cli.RunAsync(args, Console.Out, Console.In, interactive: !Console.IsInputRedirected, a => abort = a).GetAwaiter().GetResult();
    }

    /// <summary>Started from Explorer, the exe has a console of its own: hide it. Started from a terminal, leave the terminal alone.</summary>
    private static void HideOwnConsole()
    {
        var list = new uint[2];
        if (GetConsoleProcessList(list, 2) != 1) return;
        ShowWindow(GetConsoleWindow(), 0);
        FreeConsole();
    }

    [DllImport("kernel32.dll")] private static extern uint GetConsoleProcessList(uint[] list, uint count);
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
