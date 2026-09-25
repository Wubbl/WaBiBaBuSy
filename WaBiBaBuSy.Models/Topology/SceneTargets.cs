namespace WaBiBaBuSy.Models.Topology;

/// <summary>Targets of a scene started from the editor: the room selection, in chain order.</summary>
public static class SceneTargets
{
    /// <summary>
    /// The selected node ids in chain order (the apply path orders local players by position in this
    /// list), or an empty list — meaning every node — when nothing is selected. Selected ids that are
    /// no longer in the chain are dropped, so a stale selection can also yield an empty list: callers
    /// that require a selection must treat that as "nothing to play", not as "all".
    /// </summary>
    public static List<string> Resolve(IReadOnlyList<string> chainOrder, IReadOnlyCollection<string> selected)
    {
        if (selected.Count == 0) return new List<string>();
        var set = new HashSet<string>(selected);
        return chainOrder.Where(set.Contains).Distinct().ToList();
    }
}
