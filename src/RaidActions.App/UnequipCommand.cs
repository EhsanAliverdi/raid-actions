using RaidActions.Execution;
using RaidActions.Game.Memory;

namespace RaidActions.App;

/// <summary>
/// unequip &lt;champion id&gt;... [--dry-run]: takes every piece these champions wear back to the inventory, champion by champion, slot
/// by slot (raid-extractor's Prepare team → Remove gear). The dry run opens each worn piece's popup and shows its Remove without
/// pressing it. Each removal is checked in the account; anything unexpected stops the run.
/// </summary>
public static class UnequipCommand
{
    public static async Task<int> RunAsync(string[] args, string outDir, TextWriter output, Action<Action>? abortHook)
    {
        var ids = args.Skip(1).Where(x => int.TryParse(x, out _)).Select(int.Parse).Distinct().ToList();
        if (ids.Count == 0)
        {
            output.WriteLine("unequip <champion id>... [--dry-run]");
            return Cli.Usage;
        }
        var (game, problem) = LiveGame.Open(GameCommands.LivePaths(outDir));
        if (game is null)
        {
            output.WriteLine(problem);
            return Cli.Failed;
        }
        using (game)
        {
            UnityUi ui;
            try { ui = game.Ui(UiCommands.NativeLayoutPath); }
            catch (UiReadException e)
            {
                output.WriteLine(e.Message);
                return Cli.Failed;
            }
            var folder = Path.Combine(outDir, "unequip", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(folder);
            void Log(string line) => output.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
            var policy = Game.Window.WindowPolicy.Load(GameCommands.WindowConfig);
            var driver = new Input.NavigatingDriver(game, ui, null, folder, policy, Log, unequip: true);
            using var abort = new CancellationTokenSource();
            abortHook?.Invoke(() => { try { abort.Cancel(); } catch (ObjectDisposedException) { } });
            var real = !args.Contains("--dry-run");
            Log(real ? $"Taking the gear off {string.Join(", ", ids.Select(i => $"#{i}"))}. Overlays: {folder}"
                : $"DRY RUN: each worn piece's Remove shown, none pressed. Overlays: {folder}");
            var count = 0;
            try
            {
                foreach (var id in ids)
                {
                    var off = await driver.Unequip(new UiAction(ActionKind.OpenChampion, 0, $"unequip #{id}", id), id, real, abort.Token);
                    count += off.Count;
                }
                Log(real ? $"Done: {count} piece{(count == 1 ? "" : "s")} taken off and checked in the account."
                    : $"Dry run done: {count} Remove{(count == 1 ? "" : "s")} shown, nothing taken off.");
                return Cli.Ok;
            }
            catch (OperationCanceledException) when (abort.IsCancellationRequested)
            {
                Log($"ABORTED after {count} piece(s).");
                return Cli.Failed;
            }
            catch (Input.NavigatingDriver.StopException e)
            {
                Log($"STOPPED after {count} piece(s): {e.Message}");
                driver.SaveScreenshot(folder, "stopped");
                return Cli.Failed;
            }
            finally
            {
                if (driver.RestoreWindow() is { } restored) Log(restored);
            }
        }
    }
}
