using System.Text;

namespace ShowControl;

internal sealed record DispatchItem(string GroupId, string Group, Node Node, string Payload, string Encoding,
    int Port, int SenderPort, int? ReceiverPort, ReplyPolicy Response,
    string CommandSource, string PortSource, string ResponseSource)
{
    public bool RequiresListener => SenderPort != 0 || ReceiverPort.HasValue;
}

internal static class DispatchPlan
{
    public static Project Normalize(Project project)
    {
        var library = (project.NodeLibrary ?? project.IpGroups.SelectMany(group => group.Nodes)
            .GroupBy(node => ProjectRules.ParseIp(node.Ip)).Select(group =>
                group.FirstOrDefault(node => node.HealthQuery is not null) ?? group.First()).ToArray())
            .Select(node => node with { Ip = ProjectRules.ParseIp(node.Ip).ToString() })
            .ToDictionary(node => ProjectRules.ParseIp(node.Ip));
        var groups = project.IpGroups.Select(group => group with
        {
            Nodes = group.Nodes.Select(node => library.GetValueOrDefault(ProjectRules.ParseIp(node.Ip)) ?? node).ToArray()
        }).ToArray();
        foreach (var node in groups.SelectMany(group => group.Nodes))
            library.TryAdd(ProjectRules.ParseIp(node.Ip), node);
        var used = groups.SelectMany(group => group.Nodes).Select(node => ProjectRules.ParseIp(node.Ip)).ToHashSet();
        return project with { IpGroups = groups, NodeLibrary = library.Where(pair => used.Contains(pair.Key)).Select(pair => pair.Value).ToArray() };
    }

    public static DispatchItem[] Build(Project project, Cue cue) => cue.DispatchTargets().SelectMany(target =>
    {
        var group = project.IpGroups.Single(item => item.Id == target.IpGroupId);
        return group.Nodes.Select(node =>
        {
            var custom = (target.NodeOverrides ?? []).SingleOrDefault(item =>
                ProjectRules.ParseIp(item.Ip).Equals(ProjectRules.ParseIp(node.Ip)));
            var templateId = custom?.TemplateId ?? target.TemplateId;
            var template = project.Templates.FirstOrDefault(item => item.Id == templateId);
            var inherited = target.Response?.Mode == "inherit";
            var response = custom?.Response ?? (inherited ? template?.Response : target.Response) ?? new ReplyPolicy("none");
            var port = custom?.Port ?? target.Port ?? template?.Port ?? node.Communication?.TargetPort;
            if (port is null)
                throw new ArgumentException($"节点 {node.Ip} 缺少目标端口，请设置节点默认值、模板端口或 Cue 端口。");
            return new DispatchItem(group.Id, group.Name, node, template?.Payload ?? target.Payload,
                template?.Encoding ?? target.Encoding, port.Value, node.Communication?.SenderPort ?? 0,
                node.Communication?.ReceiverPort, response,
                custom?.TemplateId is not null ? "Cue 节点例外" : template is null ? "Cue 自定义" : "模板 · " + template.Name,
                custom?.Port is not null ? "Cue 节点例外" : target.Port is not null ? "Cue 目标组" : template?.Port is not null ? "命令模板" : "节点默认值",
                custom?.Response is not null ? "Cue 节点例外" : !inherited ? "Cue 目标组" : template?.Response is not null ? "命令模板" : "默认不等待");
        });
    }).ToArray();

    public static void CheckResponse(ReplyPolicy response, bool allowInherit = false)
    {
        if (allowInherit && response.Mode == "inherit") return;
        if (response.Mode == "none") return;
        if (response.Mode is not ("exact" or "contains" or "hex") || response.TimeoutMs is < 100 or > 30000 ||
            string.IsNullOrWhiteSpace(response.Text) || response.Text.Length > 120)
            throw new ArgumentException("回执需要有效的匹配方式、内容和 100–30000 ms 超时。");
        if (response.Mode == "hex") _ = ProjectRules.Packet(response.Text, "hex");
    }

    public static bool Matches(byte[] bytes, ReplyPolicy response) => response.Mode switch
    {
        "hex" => bytes.AsSpan().SequenceEqual(ProjectRules.Packet(response.Text!, "hex")),
        "contains" => Encoding.UTF8.GetString(bytes).Contains(response.Text!, StringComparison.Ordinal),
        _ => Encoding.UTF8.GetString(bytes).Trim() == response.Text!.Trim()
    };
}
