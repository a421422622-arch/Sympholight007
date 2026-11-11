using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShowControl;

internal sealed record Project(string Name, Show[] Shows, IpGroup[] IpGroups, CommandTemplate[] Templates,
    Schedule[] Schedules, PadButton[] Pads, string? UdpInterfaceId = null, UdpRoute[]? UdpRoutes = null,
    int HealthPollSeconds = 0, Node[]? NodeLibrary = null)
{
    [JsonIgnore]
    public IEnumerable<CueList> Lists => Shows.SelectMany(show => show.Groups).SelectMany(group => group.Lists);
}
internal sealed record UdpRoute(string Network, string InterfaceId);
internal sealed record Show(string Id, string Name, ShowGroup[] Groups);
internal sealed record ShowGroup(string Id, string Name, CueList[] Lists);
internal sealed record CueList(string Id, string Name, Cue[] Cues,
    string PlaybackMode = "once", int IntervalMs = 0, string PlaybackGroup = "默认")
{
    public int DelayAfter(Cue cue) => cue.DelayAfterMs ?? IntervalMs;
}
internal sealed record Cue(string Id, string Name, string IpGroupId, string? TemplateId, string Payload,
    string Encoding, int? Port, bool Ping, string Reply, int TimeoutMs, string OnFail, int Retries,
    CueTarget[]? AdditionalTargets = null, int? DelayAfterMs = null, ReplyPolicy? Response = null,
    NodeCommandOverride[]? NodeOverrides = null)
{
    public IEnumerable<CueTarget> DispatchTargets()
    {
        var legacy = new ReplyPolicy("exact", Reply, TimeoutMs);
        yield return new(IpGroupId, TemplateId, Payload, Encoding, Port, Response ?? legacy, NodeOverrides);
        foreach (var target in AdditionalTargets ?? [])
            yield return target with { Response = target.Response ?? legacy };
    }
}
internal sealed record CueTarget(string IpGroupId, string? TemplateId, string Payload, string Encoding, int? Port,
    ReplyPolicy? Response = null, NodeCommandOverride[]? NodeOverrides = null);
internal sealed record NodeCommandOverride(string Ip, string? TemplateId = null, int? Port = null, ReplyPolicy? Response = null);
internal sealed record ReplyPolicy(string Mode, string? Text = null, int TimeoutMs = 1800, string Encoding = "utf8");
internal sealed record IpGroup(string Id, string Name, Node[] Nodes);
internal sealed record Node(string Ip, string Label, string Simulation, HealthQuery? HealthQuery = null,
    NodeCommunication? Communication = null, bool PingEnabled = true, bool Customized = false);
internal sealed record NodeCommunication(int? TargetPort = null, int SenderPort = 0, int? ReceiverPort = null,
    int? ReplySourcePort = null, string ReceiveEncoding = "utf8");
internal sealed record HealthQuery(int Port, string Payload, string Reply, string Match = "exact", int TimeoutMs = 800,
    string Encoding = "utf8", string ReplyEncoding = "utf8");
internal sealed record CommandTemplate(string Id, string Name, string Payload, string Encoding,
    ReplyPolicy? Response = null, int? Port = null);
internal sealed record Schedule(string Id, string Name, string ListId, string At, bool Enabled,
    string Mode = "weekly", DayOfWeek[]? Days = null, DateOnly? StartDate = null, DateOnly? EndDate = null);
internal sealed record PadButton(string Id, string Name, string ListId, string Action = "play");
internal sealed record Snapshot(long Revision, Project Project);

internal static class ProjectRules
{
    static ProjectRules() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    public static Encoding TextEncoding(string encoding) => encoding switch
    {
        "utf8" => new UTF8Encoding(false, true),
        "ascii" => Encoding.GetEncoding(20127, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        "gbk" => Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        "windows1252" => Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        _ => throw new ArgumentException("String 编码须为 UTF-8、ASCII、GBK 或 Windows-1252。")
    };
    private const int MaxIntervalMs = 3_600_000;

    public static void Check(Project project)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        Name(project.Name);
        if (project.HealthPollSeconds != 0 && project.HealthPollSeconds is < 5 or > 3600)
            throw new ArgumentException("节点检测间隔须为 5–3600 秒，或设为 0 使用手动检测。");
        if (project.UdpInterfaceId is { } adapter)
            CheckAdapter(adapter);
        var routes = project.UdpRoutes ?? [];
        Limit(routes, 128);
        var networks = new HashSet<IPNetwork>();
        foreach (var route in routes)
        {
            CheckAdapter(route.InterfaceId);
            if (!networks.Add(UdpNetwork.ParseSubnet(route.Network)))
                throw new ArgumentException($"UDP 目标网段「{route.Network}」已配置。");
        }
        Limit(project.Shows, 50);
        Limit(project.IpGroups, 100);
        Limit(project.Templates, 100);
        Limit(project.Schedules, 100);
        Limit(project.Pads, 100);
        Limit(project.NodeLibrary ?? [], 512);
        var libraryIps = new HashSet<IPAddress>();
        foreach (var node in project.NodeLibrary ?? [])
            if (!libraryIps.Add(ParseIp(node.Ip)))
                throw new ArgumentException("统一节点库包含重复 IP 地址。");
        var queries = new Dictionary<IPAddress, HealthQuery>();
        foreach (var group in project.IpGroups)
        {
            Identity(group.Id, group.Name);
            Limit(group.Nodes, 128);
            var seen = new HashSet<IPAddress>();
            foreach (var node in group.Nodes)
            {
                var address = ParseIp(node.Ip);
                if (node.Label is null || node.Label.Length > 80 || !seen.Add(address) ||
                    node.Simulation is not ("ok" or "pingFail" or "timeout"))
                    throw new ArgumentException($"IP 分组「{group.Name}」包含重复节点或无效配置。");
                if (node.Communication is { } communication &&
                    (communication.TargetPort is < 1 or > 65535 || communication.SenderPort is < 0 or > 65535 ||
                        communication.ReceiverPort is < 1 or > 65535 || communication.ReplySourcePort is < 1 or > 65535))
                    throw new ArgumentException($"节点「{node.Ip}」的通信端口无效。");
                if (node.Communication is { } settings) _ = TextEncoding(settings.ReceiveEncoding);
                if (node.HealthQuery is { } query)
                {
                    if (query.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(query.Reply) || query.Reply.Length > 120)
                        throw new ArgumentException($"节点「{node.Ip}」的健康查询端口或回执无效。");
                    _ = Packet(query.Payload, query.Encoding);
                    DispatchPlan.CheckResponse(new(query.Match, query.Reply, query.TimeoutMs, query.ReplyEncoding));
                    if (query.Match == "none") throw new ArgumentException("软件健康查询需要匹配回执。");
                    if (queries.TryGetValue(address, out var previous) && previous != query)
                        throw new ArgumentException($"节点「{node.Ip}」在多个分组中的健康查询设置不一致。");
                    queries[address] = query;
                }
            }
        }
        if (project.IpGroups.SelectMany(group => group.Nodes).Select(node => node.Ip).Distinct().Count() > 512)
            throw new ArgumentException("项目最多支持 512 个不同的节点地址。");
        foreach (var template in project.Templates)
        {
            Identity(template.Id, template.Name);
            _ = Packet(template.Payload, template.Encoding);
            if (template.Port is < 1 or > 65535) throw new ArgumentException("模板端口须为 1–65535 或留空。");
            if (template.Response is { } response) DispatchPlan.CheckResponse(response);
        }
        foreach (var show in project.Shows)
        {
            Identity(show.Id, show.Name);
            Limit(show.Groups, 50);
            foreach (var group in show.Groups)
            {
                Identity(group.Id, group.Name);
                Limit(group.Lists, 50);
                foreach (var list in group.Lists)
                {
                    Identity(list.Id, list.Name);
                    if (string.IsNullOrWhiteSpace(list.PlaybackGroup) || list.PlaybackGroup.Length > 80 ||
                        list.PlaybackGroup != list.PlaybackGroup.Trim())
                        throw new ArgumentException($"Cue 列表「{list.Name}」的播放组名称须为 1–80 个字符。");
                    Limit(list.Cues, 200);
                    if (list.PlaybackMode is not ("once" or "loop") || list.IntervalMs is < 0 or > MaxIntervalMs)
                        throw new ArgumentException($"Cue 列表「{list.Name}」需要有效的播放模式和 0–3600 秒的默认间隔。");
                    foreach (var cue in list.Cues)
                    {
                        Identity(cue.Id, cue.Name);
                        if (cue.DelayAfterMs is < 0 or > MaxIntervalMs)
                            throw new ArgumentException($"Cue「{cue.Name}」的等待时间须为 0–3600 秒，或留空沿用列表。");
                        Limit(cue.AdditionalTargets ?? [], 99);
                        var groupIds = new HashSet<string>();
                        foreach (var target in cue.DispatchTargets())
                        {
                            var ipGroup = project.IpGroups.FirstOrDefault(group => group.Id == target.IpGroupId);
                            if (ipGroup is null || ipGroup.Nodes.Length == 0 || !groupIds.Add(target.IpGroupId) ||
                                (target.TemplateId is not null && !project.Templates.Any(template => template.Id == target.TemplateId)))
                                throw new ArgumentException($"Cue「{cue.Name}」需要互不重复的非空目标组和有效的模板引用。");
                            if (target.Port is < 1 or > 65535)
                                throw new ArgumentException($"Cue「{cue.Name}」的目标组「{ipGroup.Name}」UDP 端口无效。");
                            var response = target.Response!;
                            DispatchPlan.CheckResponse(response, allowInherit: true);
                            Limit(target.NodeOverrides ?? [], 128);
                            var overrides = new HashSet<IPAddress>();
                            foreach (var custom in target.NodeOverrides ?? [])
                            {
                                var ip = ParseIp(custom.Ip);
                                if (!overrides.Add(ip) || !ipGroup.Nodes.Any(node => ParseIp(node.Ip).Equals(ip)) ||
                                    custom.Port is < 1 or > 65535 || (custom.TemplateId is not null &&
                                        !project.Templates.Any(template => template.Id == custom.TemplateId)))
                                    throw new ArgumentException($"Cue「{cue.Name}」的节点例外无效，请检查地址、端口和模板。");
                                if (custom.Response is { } policy) DispatchPlan.CheckResponse(policy);
                            }
                            if (target.TemplateId is null)
                                _ = Packet(target.Payload, target.Encoding);
                        }
                        foreach (var item in DispatchPlan.Build(project, cue))
                        {
                            _ = Packet(item.Payload, item.Encoding);
                            DispatchPlan.CheckResponse(item.Response);
                        }
                        if (cue.OnFail is not ("retry" or "skip" or "stop") || cue.Retries is < 0 or > 5)
                            throw new ArgumentException($"Cue「{cue.Name}」的失败策略无效。");
                    }
                    if (list.PlaybackMode == "loop" && list.Cues.Length > 0 && list.Cues.Sum(list.DelayAfter) < 100)
                        throw new ArgumentException($"Cue 列表「{list.Name}」循环时每轮至少需要 0.1 秒的等待时间。");
                }
            }
        }
        var listIds = project.Lists.Select(list => list.Id).ToHashSet();
        foreach (var schedule in project.Schedules)
        {
            Identity(schedule.Id, schedule.Name);
            if (!listIds.Contains(schedule.ListId) || !TimeOnly.TryParseExact(schedule.At, "HH:mm", out _))
                throw new ArgumentException($"任务「{schedule.Name}」需要有效的 Cue 列表和 HH:mm 时间。");
            var validCalendar = schedule.Mode switch
            {
                "weekly" => schedule.Days is null || (schedule.Days.Length is >= 1 and <= 7 &&
                    schedule.Days.All(day => Enum.IsDefined(day)) &&
                    schedule.Days.Distinct().Count() == schedule.Days.Length),
                "date" => schedule.StartDate.HasValue,
                "holiday" => schedule.StartDate.HasValue && schedule.EndDate.HasValue && schedule.EndDate >= schedule.StartDate,
                _ => false
            };
            if (!validCalendar)
                throw new ArgumentException($"任务「{schedule.Name}」的星期、指定日期或节假日范围无效。");
        }
        foreach (var pad in project.Pads)
        {
            Identity(pad.Id, pad.Name);
            if (pad.Action is not ("play" or "stopAll") || (pad.Action == "play" && !listIds.Contains(pad.ListId)))
                throw new ArgumentException($"Action Pad「{pad.Name}」需要有效的 Cue 列表。");
        }

        void Identity(string id, string name)
        {
            Name(name);
            if (string.IsNullOrWhiteSpace(id) || id.Length > 72 ||
                id.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-') || !ids.Add(id))
                throw new ArgumentException("项目包含重复或无效的对象标识。");
        }
    }

    private static void CheckAdapter(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id != id.Trim())
            throw new ArgumentException("UDP 网卡标识无效。");
    }

    public static IPAddress ParseIp(string input)
    {
        if (!IPAddress.TryParse(input, out var address) || address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast) || address.IsIPv6Multicast ||
            (address.AddressFamily == AddressFamily.InterNetwork && (address.GetAddressBytes()[0] & 0xf0) == 0xe0))
            throw new ArgumentException($"请输入有效的单播 IP 地址：{input}");
        return address.IsIPv4MappedToIPv6 ? ParseIp(address.MapToIPv4().ToString()) : address;
    }

    public static byte[] Packet(string value, string encoding)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 6000)
            throw new ArgumentException("UDP 指令为空或过长。");
        byte[] bytes;
        try
        {
            bytes = encoding switch
            {
                "hex" => Convert.FromHexString(string.Concat(value.Where(ch => !char.IsWhiteSpace(ch)))),
                _ => TextEncoding(encoding).GetBytes(value)
            };
        }
        catch (FormatException)
        {
            throw new ArgumentException("HEX 指令需要完整的十六进制字节。");
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("String 内容包含所选编码无法表示的字符，请检查编码设置。");
        }
        if (bytes.Length is < 1 or > 1400)
            throw new ArgumentException("UDP 指令须为 1–1400 字节。");
        return bytes;
    }

    private static void Name(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
            throw new ArgumentException("名称须为 1–80 个字符。");
    }

    private static void Limit<T>(T[] entries, int max) where T : class
    {
        if (entries is null || entries.Length > max || entries.Any(entry => entry is null))
            throw new ArgumentException($"集合须包含 0–{max} 个有效条目。");
    }
}

internal sealed class ProjectStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();
    private readonly string path;
    private readonly FileStream writerLock;
    private Snapshot snapshot;

    public ProjectStore(string directory)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "project.json");
        writerLock = File.Open(Path.Combine(directory, "project.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!File.Exists(path))
        {
            snapshot = new(1, DispatchPlan.Normalize(Demo()));
            Persist(snapshot);
            return;
        }
        snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllBytes(path), Json)
            ?? throw new InvalidDataException("无法读取项目文件。");
        ProjectRules.Check(snapshot.Project);
        if (snapshot.Project.NodeLibrary is null)
        {
            var backup = path + ".before-node-library.bak";
            if (!File.Exists(backup)) File.Copy(path, backup);
        }
        snapshot = snapshot with { Project = DispatchPlan.Normalize(snapshot.Project) };
        ProjectRules.Check(snapshot.Project);
    }

    public void Dispose() => writerLock.Dispose();

    public Snapshot Get()
    {
        lock (gate)
            return snapshot;
    }

    public Snapshot? Save(Snapshot edit)
    {
        if (edit.Project is null)
            throw new ArgumentException("缺少项目数据。");
        edit = edit with { Project = DispatchPlan.Normalize(edit.Project) };
        ProjectRules.Check(edit.Project);
        lock (gate)
        {
            if (edit.Revision != snapshot.Revision)
                return null;
            var next = new Snapshot(snapshot.Revision + 1, edit.Project);
            Persist(next);
            snapshot = next;
            return next;
        }
    }

    private void Persist(Snapshot next)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(next, Json));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static Project Demo()
    {
        var shows = new[] { ("daily", "日常"), ("national", "国庆"), ("moon", "中秋"), ("default", "默认") }
            .Select(show => new Show(show.Item1, show.Item2,
                new[] { ("facade", "立面"), ("podium", "裙楼"), ("indoor", "室内") }.Select(group =>
                {
                    var id = show.Item1 + "-" + group.Item1;
                    return new ShowGroup(id, group.Item2, [
                        new(id + "-main", group.Item2 + " · 主播放", [
                            new(id + "-01", "启动画面", "ip-" + group.Item1, "tpl-start", "", "utf8", 7000, true, "IMOK", 1800, "retry", 1),
                            new(id + "-02", "切换主场景", "ip-" + group.Item1, null, "SCENE MAIN", "utf8", 7000, false, "IMOK", 1800, "skip", 0),
                            new(id + "-03", "退出场景", "ip-" + group.Item1, "tpl-stop", "", "utf8", 7000, false, "IMOK", 1800, "stop", 0)
                        ])
                    ]);
                }).ToArray())).ToArray();
        return new("城市光影 · 控制项目", shows,
            [
                new("ip-facade", "立面控制节点", [new("192.0.2.11", "立面主节点", "ok"), new("192.0.2.12", "立面备节点", "ok")]),
                new("ip-podium", "裙楼控制节点", [new("192.0.2.21", "裙楼主节点", "ok")]),
                new("ip-indoor", "室内控制节点", [new("192.0.2.31", "室内主节点", "pingFail")])
            ],
            [new("tpl-start", "启动场景", "SCENE START", "utf8"), new("tpl-stop", "停止场景", "SCENE STOP", "utf8")],
            [new("daily-open", "日常开场", "daily-facade-main", "19:00", false)],
            [new("pad-open", "立面主播放", "daily-facade-main"), new("pad-podium", "裙楼主播放", "daily-podium-main")]);
    }
}
