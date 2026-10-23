using System.Windows;
using RaidActions.App.ViewModels;
using RaidActions.App.Views;

namespace RaidActions.App;

public partial class App : Application
{
    private KillSwitch? _kill;

    /// <summary>A plan to open at start (<c>RaidActions open &lt;plan&gt;</c>).</summary>
    public string? InitialPlan { get; init; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _kill = new KillSwitch();
        var vm = new MainViewModel(new WindowDialogs(), _kill.Registered ? null : _kill.Problem);
        // The hotkey fires on its own thread; Abort is safe from any thread, the UI update goes through the dispatcher.
        _kill.Pressed += () => Dispatcher.BeginInvoke(() => vm.AbortFromKillSwitch());
        _kill.Pressed += vm.AbortNow;
        MainWindow = new MainWindow { DataContext = vm };
        MainWindow.Show();
        if (InitialPlan is { } plan) _ = vm.LoadAsync(plan);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _kill?.Dispose();
        base.OnExit(e);
    }
}
