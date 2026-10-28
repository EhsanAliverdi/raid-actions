using RaidActions.Execution;
using RaidActions.Game.Memory;

namespace RaidActions.App;

/// <summary>
/// vault-open: from whatever screen is open, home, Champions, then Champion Vaults, and leaves the vaults open (to read their screen
/// with ui-tree and capture). Navigation only: nothing is moved in or out of a vault.
/// </summary>
public static class VaultCommand
{
    public static async Task<int> RunAsync(string[] args, string outDir, TextWriter output, Action<Action>? abortHook)
    {
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
            var folder = Path.Combine(outDir, "vault", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(folder);
            void Log(string line) => output.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
            var policy = Game.Window.WindowPolicy.Load(GameCommands.WindowConfig);
            var driver = new Input.NavigatingDriver(game, ui, null, folder, policy, Log, vault: true);
            using var abort = new CancellationTokenSource();
            abortHook?.Invoke(() => { try { abort.Cancel(); } catch (ObjectDisposedException) { } });
            var action = new UiAction(ActionKind.OpenChampion, 0, "vault");
            try
            {
                // vault-withdraw <id>... [--dry-run]: takes these champions out of the vaults into the champion list.
                if (args[0].Equals("vault-withdraw", StringComparison.OrdinalIgnoreCase))
                {
                    var ids = args.Skip(1).Where(x => int.TryParse(x, out _)).Select(int.Parse).Distinct().ToList();
                    if (ids.Count == 0) { Log("vault-withdraw <champion id>... [--dry-run]"); return Cli.Usage; }
                    var real = !args.Contains("--dry-run");
                    var moved = await driver.Withdraw(action, ids, real, abort.Token);
                    Log(real ? $"Done: {moved.Count} champion{(moved.Count == 1 ? "" : "s")} moved to the champion list ({string.Join(", ", moved.Select(i => $"#{i}"))})."
                        : "Dry run done: ticked, the To Collection click shown, unticked. Nothing moved.");
                    return Cli.Ok;
                }
                await driver.GoHome(action, abort.Token);
                await driver.OpenChampions(action, abort.Token);
                var view = await driver.OpenVaults(action, abort.Token);
                if (args.Contains("--reserve")) await driver.VaultTab(action, reserve: true, abort.Token);
                var tapAt = Array.IndexOf(args, "--tap");
                if (tapAt >= 0 && tapAt + 1 < args.Length && int.TryParse(args[tapAt + 1], out var tapId))
                {
                    var after = await driver.TapInVault(action, tapId, abort.Token);
                    Log($"#{tapId} tapped: {(after is null ? "no longer in the grid" : $"{after.Note ?? "-"} at {after.Rect}")}");
                }
                Log($"Vaults open: {view}. Read it with ui-tree {view}; leave with go-home.");
                driver.SaveScreenshot(folder, "vaults");
                return Cli.Ok;
            }
            catch (Input.NavigatingDriver.StopException e)
            {
                Log("STOPPED: " + e.Message);
                return Cli.Failed;
            }
            finally
            {
                if (driver.RestoreWindow() is { } restored) Log(restored);
            }
        }
    }
}
