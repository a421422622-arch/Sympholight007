namespace ShowControl;

internal sealed record ScopeOverlap(string ListId, string List, string PlaybackGroup, string[] SharedIps, bool Declared);

internal static class ControlScope
{
    public const string WholeNode = "*";

    public static bool IsDeclared(CueList list) => list.ControlZones is { Length: > 0 };

    public static HashSet<string> Ips(Project project, CueList list)
    {
        var ips = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cue in list.Cues)
            foreach (var target in cue.DispatchTargets())
                foreach (var node in project.IpGroups.FirstOrDefault(group => group.Id == target.IpGroupId)?.Nodes ?? [])
                    ips.Add(ProjectRules.ParseIp(node.Ip).ToString());
        return ips;
    }

    // 共用节点才可能互相影响。两边都声明了分区时，只有分区标签相同或任一方为整节点(*)才算重叠；
    // 任一方未声明时按共用 IP 判断，并标记为未声明，只提示不拦截。
    public static ScopeOverlap? Overlap(Project project, CueList a, CueList b)
        => Overlap(project, a, project, b);

    public static ScopeOverlap? Overlap(Project project, CueList a, Project otherProject, CueList b)
    {
        var shared = Ips(project, a).Intersect(Ips(otherProject, b)).Order(StringComparer.Ordinal).ToArray();
        if (shared.Length == 0) return null;
        var declared = IsDeclared(a) && IsDeclared(b);
        if (declared)
        {
            var zonesA = a.ControlZones!;
            var zonesB = b.ControlZones!;
            var whole = zonesA.Contains(WholeNode) || zonesB.Contains(WholeNode);
            if (!whole && !zonesA.Intersect(zonesB, StringComparer.OrdinalIgnoreCase).Any()) return null;
        }
        return new ScopeOverlap(b.Id, b.Name, b.PlaybackGroup, shared, declared);
    }

    public static ScopeOverlap[] Overlaps(Project project, CueList list, IEnumerable<CueList> others) =>
        others.Where(other => other.Id != list.Id).Select(other => Overlap(project, list, other)).OfType<ScopeOverlap>().ToArray();
}
