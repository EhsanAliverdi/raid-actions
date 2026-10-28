using RaidActions.Plans;
using RaidActions.Verification;

namespace RaidActions.Execution;

public enum RunMode { DryRun, Real }

public enum ActionKind
{
    /// <summary>Save the gear snapshot (before any input).</summary>
    SaveSnapshot,
    /// <summary>Make sure no battle runs and the game shows a known screen.</summary>
    CheckGameIdle,
    /// <summary>Bring the game window to the foreground (only after Start).</summary>
    FocusGame,
    OpenChampion,
    OpenSlot,
    /// <summary>Find the piece in the slot's list (inventory, or worn by the donor).</summary>
    FindPiece,
    SelectPiece,
    /// <summary>Confirm, by reading the game, that the right champion and the right piece are selected.</summary>
    VerifySelection,
    /// <summary>Dry run only: mark the piece on screen instead of equipping it.</summary>
    Highlight,
    Equip,
    /// <summary>The game's prompt to take the piece off its current owner.</summary>
    ConfirmRemoveFromDonor,
    /// <summary>Re-read the account: every plan piece is where the steps done so far put it.</summary>
    VerifyStep,
    /// <summary>Re-read the account: every plan piece is at its expected end owner.</summary>
    VerifyEnd,
}

/// <param name="Step">Plan step number, or 0 for the actions around the steps.</param>
/// <param name="DoneThrough">For <see cref="ActionKind.VerifyStep"/>: the steps that must show as done after this action.</param>
public sealed record UiAction(ActionKind Kind, int Step, string Text, int? ChampionId = null, string? Slot = null, int? ArtifactId = null,
    int? FromChampionId = null, IReadOnlyList<int>? DoneThrough = null)
{
    /// <summary>Actions that change the account. Never in a dry run.</summary>
    public bool ChangesGear => Kind is ActionKind.Equip or ActionKind.ConfirmRemoveFromDonor;
}

public sealed record ActionScript(RunMode Mode, IReadOnlyList<UiAction> Actions, IReadOnlyList<int> SkippedDone);

/// <summary>
/// Turns a checked plan into the input sequence, one step at a time, exactly in plan order. Steps the account already shows as done
/// are skipped (no input). Refuses a plan whose check did not pass. Never reorders, merges or adds moves (the runner may let the
/// driver pick the order of consecutive interchangeable steps: see <see cref="IStepPicker"/>).
/// </summary>
public static class ActionPlanner
{
    /// <param name="only">Run only these steps (the rest stay pending); then the end check covers only them.</param>
    public static ActionScript Build(Plan plan, CheckReport check, RunMode mode, IReadOnlySet<int>? only = null)
    {
        if (!check.CanStart)
            throw new InvalidOperationException("The plan did not pass the account check: "
                + string.Join(" ", check.Issues.Where(i => i.Severity == Severity.Error).Select(i => i.ToString()).DefaultIfEmpty("nothing to do.")));

        var actions = new List<UiAction>
        {
            new(ActionKind.SaveSnapshot, 0, "Save the gear snapshot"),
            new(ActionKind.CheckGameIdle, 0, "Check that no battle is running and the game shows a known screen"),
            new(ActionKind.FocusGame, 0, "Bring the game to the foreground"),
        };
        var done = check.Steps.Where(s => s.State == StepState.Done).Select(s => s.Step.Number).ToList();
        var skipped = done.ToList();
        foreach (var sc in check.Pending.Where(s => only is null || only.Contains(s.Step.Number)))
        {
            var s = sc.Step;
            var n = s.Number;
            var from = s.FromChampionId is null ? "the inventory" : s.FromName;
            actions.Add(new(ActionKind.OpenChampion, n, $"Open {s.ToName}", s.ToChampionId));
            actions.Add(new(ActionKind.OpenSlot, n, $"Open {s.ToName}'s {s.Slot}", s.ToChampionId, s.Slot));
            actions.Add(new(ActionKind.FindPiece, n, $"Find #{s.ArtifactId} ({from})", s.ToChampionId, s.Slot, s.ArtifactId, s.FromChampionId));
            actions.Add(new(ActionKind.SelectPiece, n, $"Select #{s.ArtifactId}", s.ToChampionId, s.Slot, s.ArtifactId, s.FromChampionId));
            actions.Add(new(ActionKind.VerifySelection, n, $"Confirm {s.ToName} and #{s.ArtifactId} are selected", s.ToChampionId, s.Slot, s.ArtifactId, s.FromChampionId));
            if (mode == RunMode.DryRun)
            {
                actions.Add(new(ActionKind.Highlight, n, $"Highlight #{s.ArtifactId} (dry run: no Equip)", s.ToChampionId, s.Slot, s.ArtifactId, s.FromChampionId));
                actions.Add(new(ActionKind.VerifyStep, n, "Confirm nothing changed", DoneThrough: done.ToList()));
                continue;
            }
            actions.Add(new(ActionKind.Equip, n, $"Equip #{s.ArtifactId} on {s.ToName}", s.ToChampionId, s.Slot, s.ArtifactId, s.FromChampionId));
            if (s.FromChampionId is { } donor)
                actions.Add(new(ActionKind.ConfirmRemoveFromDonor, n, $"Confirm taking #{s.ArtifactId} off {s.FromName}", s.ToChampionId, s.Slot, s.ArtifactId, donor));
            done.Add(n);
            actions.Add(new(ActionKind.VerifyStep, n, $"Confirm #{s.ArtifactId} is on {s.ToName}", s.ToChampionId, s.Slot, s.ArtifactId, s.FromChampionId, done.Order().ToList()));
        }
        if (mode == RunMode.Real && (only is null || check.Pending.All(s => only.Contains(s.Step.Number))))
            actions.Add(new(ActionKind.VerifyEnd, 0, "Confirm every piece is where the plan ends"));
        return new(mode, actions, skipped);
    }
}
