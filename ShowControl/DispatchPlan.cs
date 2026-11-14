using System.Text;

namespace ShowControl;

internal sealed record DispatchItem(string GroupId, string Group, Node Node, string Payload, string Encoding,
    int Port, int SenderPort, int? ReceiverPort, ReplyPolicy Response,
    string CommandSource, string PortSource, string ResponseSource, bool AllowRetry)
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
        return project with { IpGroups = groups, NodeLibrary = library.Values.ToArray() };
    }

    public static DispatchItem[] Build(Project project, Cue cue, bool strict = true)
    {
        var items = cue.DispatchTargets().SelectMany(target =>
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
                    return strict ? throw new ArgumentException($"节点 {node.Ip} 缺少目标端口，请设置节点默认值、模板端口或 Cue 端口。") : (DispatchItem?)null;
                return new DispatchItem(group.Id, group.Name, node, template?.Payload ?? target.Payload,
                    template?.Encoding ?? target.Encoding, port.Value, node.Communication?.SenderPort ?? 0,
                    node.Communication?.ReceiverPort, response,
                    custom?.TemplateId is not null ? "Cue 节点例外" : template is null ? "Cue 自定义" : "模板 · " + template.Name,
                    custom?.Port is not null ? "Cue 节点例外" : target.Port is not null ? "Cue 目标组" : template?.Port is not null ? "命令模板" : "节点默认值",
                    custom?.Response is not null ? "Cue 节点例外" : !inherited ? "Cue 目标组" : template?.Response is not null ? "命令模板" : "默认不等待",
                    target.AllowRetry ?? template?.AllowRetry ?? false);
            });
        }).ToArray();
        return items.OfType<DispatchItem>().GroupBy(item => (Address: ProjectRules.ParseIp(item.Node.Ip), item.Port,
            Packet: Convert.ToHexString(ProjectRules.Packet(item.Payload, item.Encoding))))
            .Select(group =>
            {
                var first = group.First();
                if (strict && group.Any(item => item.Response != first.Response || item.AllowRetry != first.AllowRetry ||
                    item.Node.Communication != first.Node.Communication))
                    throw new ArgumentException($"节点 {first.Node.Ip}:{first.Port} 的重复指令具有不同回执或重试规则，请统一配置。");
                return first with { Group = string.Join(" / ", group.Select(item => item.Group).Distinct()) };
            }).ToArray();
    }

    public static string[] Conflicts(Project project, CueList list)
    {
        var found = new List<string>();
        foreach (var cue in list.Cues)
        {
            try { _ = Build(project, cue); }
            catch (ArgumentException error)
            {
                found.Add($"列表「{list.Name}」· Cue「{cue.Name}」：{error.Message}");
            }
        }
        return found.ToArray();
    }

    public static void CheckResponse(ReplyPolicy response, bool allowInherit = false)
    {
        if (allowInherit && response.Mode == "inherit") return;
        if (response.Mode == "none") return;
        if (response.Mode is not ("exact" or "exactStrict" or "contains" or "hex") || response.TimeoutMs is < 100 or > 30000 ||
            response.Text is not { Length: > 0 and <= 120 } ||
            (response.Mode != "exactStrict" && string.IsNullOrWhiteSpace(response.Text)))
            throw new ArgumentException("回执需要有效的匹配方式、内容和 100–30000 ms 超时。");
        if (response.Mode == "hex") _ = ProjectRules.Packet(response.Text, "hex");
        else _ = ProjectRules.Packet(response.Text, response.Encoding);
    }

    public static bool Matches(byte[] bytes, ReplyPolicy response)
    {
        if (response.Mode == "hex") return bytes.AsSpan().SequenceEqual(ProjectRules.Packet(response.Text!, "hex"));
        try
        {
            var text = ProjectRules.TextEncoding(response.Encoding).GetString(bytes);
            return response.Mode switch
            {
                "contains" => text.Contains(response.Text!, StringComparison.Ordinal),
                "exactStrict" => string.Equals(text, response.Text, StringComparison.Ordinal),
                _ => string.Equals(text.Trim(), response.Text!.Trim(), StringComparison.Ordinal)
            };
        }
        catch (DecoderFallbackException) { return false; }
    }

    public static string DescribePayload(byte[] bytes, string encoding)
    {
        if (encoding == "hex") return "HEX " + BitConverter.ToString(bytes).Replace('-', ' ');
        return $"String ({encoding}) {ProjectRules.TextEncoding(encoding).GetString(bytes)}";
    }
}
