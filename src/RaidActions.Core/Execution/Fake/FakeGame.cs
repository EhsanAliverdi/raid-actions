using RaidExtractor;

namespace RaidActions.Execution.Fake;

/// <summary>
/// A stand-in for the game: both the driver and the account source, with no input to anything. It keeps an account in memory and
/// applies Equip the way the game does (the piece leaves its owner, the recipient's old piece goes to the inventory), so a whole run,
/// with the reads after every step, can be checked without the game. Faults can be injected for tests and demos.
/// </summary>
public sealed class FakeGame : IGameDriver, IAccountSource
{
    private readonly object _lock = new();
    private readonly SavedExport _start;
    private readonly List<ArtifactOut> _artifacts;
    private readonly List<UiAction> _performed = [];
    private int? _openChampion;
    private int? _selected;

    public FakeGame(SavedExport account)
    {
        _start = account;
        _artifacts = account.Artifacts.ToList();
    }

    public string Name => "fake";
    public bool SendsInput => false;

    /// <summary>Wait per action (for watching a run in the UI). Abort cancels it.</summary>
    public TimeSpan ActionDelay { get; set; }

    /// <summary>The game version the reads report (default: the starting account's).</summary>
    public string? GameVersion { get; set; }

    /// <summary>A battle is running: CheckGameIdle fails.</summary>
    public bool BattleRunning { get; set; }

    /// <summary>Fail this action instead of doing it.</summary>
    public Func<UiAction, string?>? FailWhen { get; set; }

    /// <summary>On Equip of this step, put the piece on another champion (a wrong click the read must catch).</summary>
    public (int Step, int WrongChampion)? MisEquip { get; set; }

    /// <summary>Called before every action (tests use it to abort or pause in the middle).</summary>
    public Action<UiAction>? BeforeAction { get; set; }

    public IReadOnlyList<UiAction> Performed { get { lock (_lock) return _performed.ToList(); } }
    public int Reads { get; private set; }

    public async Task<DriverResult> PerformAsync(UiAction a, CancellationToken ct)
    {
        BeforeAction?.Invoke(a);
        ct.ThrowIfCancellationRequested();
        if (ActionDelay > TimeSpan.Zero) await Task.Delay(ActionDelay, ct);
        lock (_lock) _performed.Add(a);
        if (FailWhen?.Invoke(a) is { } failure) return DriverResult.Failure(failure);

        lock (_lock)
            switch (a.Kind)
            {
                case ActionKind.CheckGameIdle:
                    return BattleRunning ? DriverResult.Failure("A battle is running.") : DriverResult.Success("fake: idle, known screen");
                case ActionKind.OpenChampion:
                    _openChampion = a.ChampionId;
                    _selected = null;
                    break;
                case ActionKind.FindPiece:
                    if (_artifacts.All(x => x.Id != a.ArtifactId)) return DriverResult.Failure($"#{a.ArtifactId} is not in the list.");
                    break;
                case ActionKind.SelectPiece:
                    _selected = a.ArtifactId;
                    break;
                case ActionKind.VerifySelection:
                    if (_openChampion != a.ChampionId || _selected != a.ArtifactId)
                        return DriverResult.Failure($"Selected: champion #{_openChampion}, piece #{_selected}; expected #{a.ChampionId}, #{a.ArtifactId}.");
                    return DriverResult.Success($"fake: champion #{a.ChampionId} and piece #{a.ArtifactId} selected");
                case ActionKind.Equip:
                    Equip(a.ArtifactId!.Value, MisEquip is { } m && m.Step == a.Step ? m.WrongChampion : a.ChampionId!.Value);
                    break;
            }
        return DriverResult.Success("fake: no input sent");
    }

    private void Equip(int artifactId, int championId)
    {
        var i = _artifacts.FindIndex(x => x.Id == artifactId);
        var slot = _artifacts[i].Slot;
        for (var j = 0; j < _artifacts.Count; j++)
            if (_artifacts[j].OwnerHeroId == championId && _artifacts[j].Slot == slot && j != i) _artifacts[j] = _artifacts[j] with { OwnerHeroId = null };
        _artifacts[i] = _artifacts[i] with { OwnerHeroId = championId };
    }

    public string? SaveScreenshot(string folder, string name) => null; // nothing to capture

    public Task<SavedExport> ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            Reads++;
            var arts = _artifacts.ToList();
            var champs = _start.Champions.Select(c => c with { Artifacts = arts.Where(a => a.OwnerHeroId == c.Id).ToDictionary(a => a.Slot, a => a.Id) }).ToList();
            var meta = _start.Meta with { ExportedAt = DateTimeOffset.Now.ToString("O"), GameVersion = GameVersion ?? _start.Meta.GameVersion };
            return Task.FromResult(_start with { Meta = meta, Champions = champs, Artifacts = arts });
        }
    }
}
