using RaidActions.Execution;
using RaidActions.Game.Memory;

namespace RaidActions.App;

/// <summary>
/// remove-gear &lt;champion id&gt;... [--then reserve|master|list] [--dry-run]: takes all the gear off these champions by moving them
/// into the Reserve Vault (the game unequips every piece at once), then leaves them there (reserve, the default), moves them on to the
/// Master Vault, or back to the champion list. raid-extractor's Manage team → Remove gear. Each move is checked in the account.
/// </summary>
public static class RemoveGearCommand
{
    public static async Task<int> RunAsync(string[] args, string outDir, TextWriter output, Action<Action>? abortHook)
    {
        var ids = args.Skip(1).TakeWhile(x => !x.StartsWith("--", StringComparison.Ordinal)).Where(x => int.TryParse(x, out _)).Select(int.Parse).Distinct().ToList();
        var thenAt = Array.IndexOf(args, "--then");
        var then = thenAt >= 0 && thenAt + 1 < args.Length ? args[thenAt + 1].ToLowerInvariant() : "reserve";
        Input.NavigatingDriver.After? after = then switch { "reserve" => Input.NavigatingDriver.After.Reserve, "master" => Input.NavigatingDriver.After.Master, "list" => Input.NavigatingDriver.After.List, _ => null };
        if (ids.Count == 0 || after is null)
        {
            output.WriteLine("remove-gear <champion id>... [--then reserve|master|list] [--dry-run]");
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
            var folder = Path.Combine(outDir, "remove-gear", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(folder);
            void Log(string line) => output.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
            var policy = Game.Window.WindowPolicy.Load(GameCommands.WindowConfig);
            var driver = new Input.NavigatingDriver(game, ui, null, folder, policy, Log, vault: true, deposit: true);
            using var abort = new CancellationTokenSource();
            abortHook?.Invoke(() => { try { abort.Cancel(); } catch (ObjectDisposedException) { } });
            var real = !args.Contains("--dry-run");
            Log(real ? $"Removing the gear of {string.Join(", ", ids.Select(i => $"#{i}"))} through the Reserve Vault, then: {then}. Overlays: {folder}"
                : $"DRY RUN: the champions are ticked, the move to the Reserve Vault shown (not pressed), then unticked. Overlays: {folder}");
            try
            {
                var done = await driver.RemoveGearByVault(new UiAction(ActionKind.OpenChampion, 0, "remove-gear"), ids, after.Value, real, abort.Token);
                Log(real ? $"Done: the gear of {done.Count} champion{(done.Count == 1 ? "" : "s")} is in the inventory; they are {then switch { "master" => "in the Master Vault", "list" => "back in the champion list", _ => "in the Reserve Vault" }}."
                    : "Dry run done: nothing moved.");
                return Cli.Ok;
            }
            catch (OperationCanceledException) when (abort.IsCancellationRequested)
            {
                Log("ABORTED.");
                return Cli.Aborted;
            }
            catch (Input.NavigatingDriver.StopException e)
            {
                Log("STOPPED: " + e.Message);
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
