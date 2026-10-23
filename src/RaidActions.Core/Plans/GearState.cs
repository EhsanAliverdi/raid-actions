namespace RaidActions.Plans;

/// <summary>
/// Who wears which piece, and how a step changes that the way the game does it: the piece leaves its owner (or the inventory), and
/// the piece the recipient wore in that slot goes to the inventory.
/// </summary>
public sealed class GearState
{
    private readonly Dictionary<int, int?> _owner = [];
    private readonly Dictionary<int, string> _slot = [];

    public GearState(IEnumerable<(int ArtifactId, string Slot, int? Owner)> pieces)
    {
        foreach (var (id, slot, owner) in pieces) { _owner[id] = owner; _slot[id] = slot; }
    }

    public IReadOnlyDictionary<int, int?> Owners => _owner;

    public bool Knows(int artifactId) => _owner.ContainsKey(artifactId);

    public int? OwnerOf(int artifactId) => _owner.GetValueOrDefault(artifactId);

    /// <summary>The piece <paramref name="championId"/> wears in <paramref name="slot"/>, among the pieces this state knows.</summary>
    public int? WornIn(int championId, string slot) =>
        _owner.Where(kv => kv.Value == championId && _slot[kv.Key] == slot).Select(kv => (int?)kv.Key).FirstOrDefault();

    /// <summary>Applies a step; returns the piece it pushes into the inventory, if any.</summary>
    public int? Apply(PlanStep step)
    {
        var displaced = WornIn(step.ToChampionId, step.Slot);
        if (displaced == step.ArtifactId) displaced = null;
        if (displaced is { } d) _owner[d] = null;
        _owner[step.ArtifactId] = step.ToChampionId;
        _slot[step.ArtifactId] = step.Slot;
        return displaced;
    }
}
