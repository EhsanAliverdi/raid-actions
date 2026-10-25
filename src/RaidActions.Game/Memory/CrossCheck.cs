namespace RaidActions.Game.Memory;

public enum CrossCheckOutcome { Agrees, NoTemplate, Mismatch }

/// <param name="Overlap">Intersection over union of the two rectangles (0..1), when the template matched.</param>
public sealed record CrossCheckResult(CrossCheckOutcome Outcome, double Overlap, string Text)
{
    /// <summary>Only a disagreement stops a step; a template that did not match on its own does not.</summary>
    public bool Refuses => Outcome == CrossCheckOutcome.Mismatch;
}

/// <summary>
/// The optional template cross-check of a click target. Memory is the authority for where to click; a template that matched
/// somewhere else means one of the two is wrong, and the step refuses.
/// </summary>
public static class CrossCheck
{
    public const double MinOverlap = 0.5;

    public static CrossCheckResult Compare(ClientRect memory, ClientRect? template)
    {
        if (template is not { } t) return new(CrossCheckOutcome.NoTemplate, 0, "no template match (not a refusal)");
        var x1 = Math.Max(memory.X, t.X);
        var y1 = Math.Max(memory.Y, t.Y);
        var x2 = Math.Min(memory.Right, t.Right);
        var y2 = Math.Min(memory.Bottom, t.Bottom);
        var inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        var union = memory.Width * memory.Height + t.Width * t.Height - inter;
        var iou = union <= 0 ? 0 : inter / union;
        return iou >= MinOverlap
            ? new(CrossCheckOutcome.Agrees, iou, $"template agrees (overlap {iou:P0})")
            : new(CrossCheckOutcome.Mismatch, iou, $"template at {t} but memory says {memory} (overlap {iou:P0}): refuse");
    }
}
