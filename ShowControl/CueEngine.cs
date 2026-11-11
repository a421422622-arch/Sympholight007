using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace ShowControl;

internal sealed record RuntimeOptions(bool Live);
internal sealed record EventEntry(DateTimeOffset At, string Source, string List, string Cue, string Status, string Message);
internal sealed record ActiveRun(Guid Id, string ListId, string List, string Source, DateTimeOffset At,
    string PlaybackMode, string PlaybackGroup, bool Stopping = false, string? CueId = null, string? CueName = null,
    long Round = 0, bool WaitingForNext = false, string? NextCueName = null, int? RemainingWaitMs = null);
internal sealed record TriggerResult(bool Accepted, string Message);
internal sealed record Health(string Ip, string Status, long? LatencyMs, DateTimeOffset CheckedAt,
    string SoftwareStatus = "notConfigured");

internal sealed class CueEngine(RuntimeOptions options, IHostApplicationLifetime lifetime, ExecutionLogStore logStore)
{
    private readonly object gate = new();
    private readonly Queue<EventEntry> events = new();
    private readonly Dictionary<string, Run> active = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim udpSlots = new(512);

    public (ActiveRun[] Runs, EventEntry[] Events) Status()
    {
        lock (gate)
        {
            var runs = active.Values.Select(run =>
            {
                var info = run.Info;
                var stopping = run.Cancel.IsCancellationRequested;
                int? remaining = !stopping && info.RemainingWaitMs is int delay
                    ? Math.Max(0, (int)Math.Ceiling(delay - Stopwatch.GetElapsedTime(run.WaitStarted).TotalMilliseconds))
                    : null;
                return info with { Stopping = stopping, RemainingWaitMs = remaining };
            }).OrderBy(run => run.At).ToArray();
            return (runs, events.Reverse().ToArray());
        }
    }

    public TriggerResult Trigger(Project project, string listId, string source)
    {
        var list = project.Lists.FirstOrDefault(item => item.Id == listId);
        if (list is null || list.Cues.Length == 0)
            return new(false, "所选 Cue 列表没有可执行条目。");
        if (options.Live && list.Cues.SelectMany(cue => DispatchPlan.Build(project, cue)).Any(item => item.RequiresListener))
            return new(false, "此列表使用固定发送或独立接收端口。目前可保存和模拟预览，真实发送需接入统一收发引擎。请使用自动发送端口和原端口接收。");

        lock (gate)
        {
            if (lifetime.ApplicationStopping.IsCancellationRequested)
                return new(false, "程序正在关闭。");
            var replaced = active.Values.Where(run => run.Info.ListId == list.Id ||
                StringComparer.OrdinalIgnoreCase.Equals(run.Info.PlaybackGroup, list.PlaybackGroup)).ToArray();
            if (source == "schedule" && replaced.Any(run => run.Info.Source != "schedule" && !run.Cancel.IsCancellationRequested))
            {
                var reason = $"「{list.Name}」或其播放组正在手动播放，本次定时触发已跳过。";
                Log(source, list.Name, "", "skipped", reason);
                return new(false, reason);
            }
            foreach (var previous in replaced)
            {
                previous.Cancel.Cancel();
                Log(source, list.Name, "", "priority", $"正在切换播放组「{list.PlaybackGroup}」，等待「{previous.Info.List}」退出。");
            }

            var run = new Run(new(Guid.NewGuid(), list.Id, list.Name, source, DateTimeOffset.Now, list.PlaybackMode, list.PlaybackGroup),
                CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping));
            active[list.PlaybackGroup] = run;
            Log(source, list.Name, "", "queued", options.Live ? "已接受真实 UDP 执行。" : "已接受模拟执行。");
            run.Task = Task.Run(() => Execute(run, project, list, Task.WhenAll(replaced.Select(previous => previous.Task))));
            return new(true, "已接收执行请求。");
        }
    }

    public bool Stop(Guid runId)
    {
        lock (gate)
        {
            var run = active.Values.FirstOrDefault(item => item.Info.Id == runId);
            if (run is null)
                return false;
            run.Cancel.Cancel();
            return true;
        }
    }

    public int StopAll()
    {
        lock (gate)
        {
            var runs = active.Values.ToArray();
            foreach (var run in runs)
                run.Cancel.Cancel();
            return runs.Length;
        }
    }

    public Task Drain(CancellationToken token)
    {
        lock (gate)
            return Task.WhenAll(active.Values.Select(run => run.Task)).WaitAsync(token);
    }

    public void SkipSchedule(Schedule schedule, string reason) =>
        Log("schedule", schedule.Name, "", "skipped", reason);

    private async Task Execute(Run run, Project project, CueList list, Task previous)
    {
        var token = run.Cancel.Token;
        var source = run.Info.Source;
        try
        {
            await previous;
            var skipped = false;
            var loop = list.PlaybackMode == "loop";
            for (long round = 1; ; round++)
            {
                for (var index = 0; index < list.Cues.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var cue = list.Cues[index];
                    UpdateProgress(run, cue, round);
                    var ok = await PlayCue(project, list, cue, source, token);
                    token.ThrowIfCancellationRequested();
                    if (ok)
                        Log(source, list.Name, cue.Name, "success", "目标指令执行完毕。");
                    else if (cue.OnFail != "skip")
                    {
                        Log(source, list.Name, cue.Name, "stopped", cue.OnFail == "retry" ? "重试耗尽，Cue 列表已停止。" : "Cue 失败，Cue 列表已停止。");
                        return;
                    }
                    else
                    {
                        skipped = true;
                        Log(source, list.Name, cue.Name, "skipped", "Cue 失败，继续下一条。");
                    }

                    var last = index == list.Cues.Length - 1;
                    var delay = list.DelayAfter(cue);
                    if (delay == 0 || (last && !loop))
                        continue;
                    UpdateProgress(run, cue, round, list.Cues[last ? 0 : index + 1].Name, delay);
                    var next = last ? "下一轮" : "下一条 Cue";
                    Log(source, list.Name, cue.Name, "interval", $"等待 {delay / 1000d:0.###} 秒后播放{next}。");
                    await Task.Delay(delay, token);
                }
                if (!loop)
                    break;
            }
            Log(source, list.Name, "", skipped ? "completedWithSkips" : "completed",
                skipped ? "Cue 列表完成，包含跳过条目。" : "Cue 列表执行完毕。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Log(source, list.Name, "", "stopped", "执行已取消。");
        }
        catch (Exception error)
        {
            Log(source, list.Name, "", "failed", $"执行失败：{error.Message}");
        }
        finally
        {
            lock (gate)
            {
                if (active.TryGetValue(list.PlaybackGroup, out var current) && current == run)
                    active.Remove(list.PlaybackGroup);
                run.Cancel.Dispose();
            }
        }
    }

    private void UpdateProgress(Run run, Cue cue, long round, string? nextCueName = null, int? delay = null)
    {
        lock (gate)
        {
            if (delay.HasValue)
                run.WaitStarted = Stopwatch.GetTimestamp();
            run.Info = run.Info with
            {
                CueId = cue.Id,
                CueName = cue.Name,
                Round = round,
                WaitingForNext = delay.HasValue,
                NextCueName = nextCueName,
                RemainingWaitMs = delay
            };
        }
    }

    private async Task<bool> PlayCue(Project project, CueList list, Cue cue, string source, CancellationToken token)
    {
        var failed = 0;
        var attempts = cue.OnFail == "retry" ? 1 + cue.Retries : 1;
        var dispatch = DispatchPlan.Build(project, cue).Select(item => new Dispatch(item.Group, item.Node,
            ProjectRules.ParseIp(item.Node.Ip), item.Port, ProjectRules.Packet(item.Payload, item.Encoding), item.Response))
            .GroupBy(item => (item.Address, item.Port)).ToArray();
        var router = options.Live ? new UdpNetwork.Router(project) : null;
        var waves = dispatch.GroupBy(endpoint =>
            (Adapter: router?.Select(endpoint.Key.Address), Family: endpoint.Key.Address.AddressFamily));
        await Task.WhenAll(waves.Select(async wave =>
        {
            var adapter = wave.Key.Adapter;
            var family = wave.Key.Family;
            var initialBinding = adapter is null ? null : await WaitForAdapter(family, adapter, token);
            await Parallel.ForEachAsync(wave, new ParallelOptions
            {
                MaxDegreeOfParallelism = 512,
                CancellationToken = token
            }, async (endpoint, ct) =>
            {
                var firstSend = true;
                foreach (var item in endpoint)
                {
                    var confirmed = false;
                    for (var attempt = 1; attempt <= attempts && !confirmed; attempt++)
                    {
                        ct.ThrowIfCancellationRequested();
                        void Step(string status, string message) =>
                            Log(source, list.Name, cue.Name, status, $"{item.Group} · {item.Node.Ip}:{item.Port} · {message}");
                        if (attempt > 1)
                        {
                            Step("retry", $"第 {attempt}/{attempts} 次发送");
                            await Task.Delay(200, ct);
                        }
                        var binding = adapter is null || firstSend ? initialBinding : await UdpNetwork.Wait(adapter, family, null, ct);
                        firstSend = false;
                        await udpSlots.WaitAsync(ct);
                        try { confirmed = await Send(cue, item, binding, Step, ct); }
                        finally { udpSlots.Release(); }
                    }
                    if (!confirmed)
                        Interlocked.Exchange(ref failed, 1);
                }
            });
        }));
        return failed == 0;

        async Task<UdpNetwork.Binding> WaitForAdapter(AddressFamily family, string id, CancellationToken ct)
        {
            var waited = false;
            var binding = await UdpNetwork.Wait(id, family, () =>
            {
                waited = true;
                Log(source, list.Name, cue.Name, "adapterWait", $"UDP 网卡 {id} 暂不可用，正在等待恢复。");
            }, ct);
            if (waited)
                Log(source, list.Name, cue.Name, "adapterReady", $"UDP 网卡 {id} 已恢复，继续发送。");
            return binding;
        }
    }

    private sealed record Dispatch(string Group, Node Node, System.Net.IPAddress Address, int Port, byte[] Bytes, ReplyPolicy Response);

    private async Task<bool> Send(Cue cue, Dispatch item, UdpNetwork.Binding? binding, Action<string, string> step, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            using var client = options.Live ? UdpNetwork.Connect(item.Address, item.Port, binding) : null;
            if (client is not null)
            {
                await client.SendAsync(item.Bytes.AsMemory(), token);
            }
            var response = item.Response;
            step("send", $"{(options.Live ? "已发送" : "模拟发送")} {item.Bytes.Length} 字节 UDP" +
                (response.Mode == "none" ? " · 不等待回执" : ""));
            if (cue.Ping)
            {
                step("ping", "检查 ICMP Ping");
                if ((await NodeHealth.Probe(item.Node, options, token)).Status != "online")
                {
                    step("failed", "Ping 失败");
                    return false;
                }
            }

            if (response.Mode == "none")
                return true;

            var expected = response.Text!.Trim();
            step("wait", $"等待回执 {expected}");
            if (client is null)
            {
                await Task.Delay(item.Node.Simulation == "timeout" ? response.TimeoutMs : 140, token);
                if (item.Node.Simulation == "timeout")
                {
                    step("failed", $"等待回执 {expected} 超时");
                    return false;
                }
                step("ack", $"模拟收到 {expected}");
                return true;
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(response.TimeoutMs);
            try
            {
                while (true)
                {
                    var packet = await client.ReceiveAsync(wait.Token);
                    if (!DispatchPlan.Matches(packet.Buffer, response))
                        continue;
                    step("ack", $"收到 {expected}");
                    return true;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                step("failed", $"等待回执 {expected} 超时");
                return false;
            }
        }
        catch (SocketException error)
        {
            step("failed", $"UDP 错误 {error.SocketErrorCode}");
            return false;
        }
    }

    private void Log(string source, string list, string cue, string status, string message)
    {
        lock (gate)
        {
            if (events.Count >= 300)
                events.Dequeue();
            var entry = new EventEntry(DateTimeOffset.Now, source, list, cue, status, message);
            events.Enqueue(entry);
            logStore.Append(entry);
        }
    }

    private sealed class Run(ActiveRun info, CancellationTokenSource cancel)
    {
        public ActiveRun Info { get; set; } = info;
        public long WaitStarted { get; set; }
        public CancellationTokenSource Cancel { get; } = cancel;
        public Task Task { get; set; } = Task.CompletedTask;
    }
}

internal sealed class NodeHealth(RuntimeOptions options)
{
    private readonly ConcurrentDictionary<string, (Node Node, string? Adapter, Health Health)> readings = new();
    private readonly SemaphoreSlim refresh = new(1);
    private HealthScan scan = new(false, 0, null);
    public HealthScan Progress => Volatile.Read(ref scan);

    public Health[] Get(Project project)
    {
        var router = new UdpNetwork.Router(project);
        return Nodes(project).Select(node => readings.TryGetValue(node.Ip, out var value) && value.Node == node &&
            value.Adapter == router.Select(ProjectRules.ParseIp(node.Ip))
            ? value.Health : new Health(node.Ip, "unknown", null, default, node.HealthQuery is null ? "notConfigured" : "unknown")).ToArray();
    }

    public async Task<Health[]> Refresh(Project project, CancellationToken token, bool automatic = false)
    {
        if (automatic)
        {
            if (!await refresh.WaitAsync(0, token))
                return [];
        }
        else
        {
            await refresh.WaitAsync(token);
        }
        var current = Progress;
        Volatile.Write(ref scan, current with { InProgress = true });
        try
        {
            var nodes = Nodes(project);
            var router = new UdpNetwork.Router(project);
            var keys = nodes.Select(node => node.Ip).ToHashSet();
            foreach (var ip in readings.Keys.Where(ip => !keys.Contains(ip)))
                readings.TryRemove(ip, out _);
            await Parallel.ForEachAsync(nodes, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token },
                async (node, ct) =>
                {
                    var health = node.PingEnabled ? await Probe(node, options, ct)
                        : new Health(node.Ip, "unknown", null, DateTimeOffset.Now);
                    var adapter = router.Select(ProjectRules.ParseIp(node.Ip));
                    var pendingTransport = options.Live && node.Communication is { } communication &&
                        (communication.SenderPort != 0 || communication.ReceiverPort.HasValue);
                    var software = node.HealthQuery is null ? "notConfigured" : pendingTransport ? "pendingTransport"
                        : await CheckSoftware(node, adapter, ct) ? "online" : "unconfirmed";
                    readings[node.Ip] = (node, adapter, health with { SoftwareStatus = software });
                });
            current = new(false, current.Version + 1, DateTimeOffset.Now);
            return automatic ? [] : Get(project);
        }
        finally
        {
            Volatile.Write(ref scan, current);
            refresh.Release();
        }
    }

    private static Node[] Nodes(Project project) =>
        project.IpGroups.SelectMany(group => group.Nodes).GroupBy(node => node.Ip)
            .Select(group => group.FirstOrDefault(node => node.HealthQuery is not null) ?? group.First()).ToArray();

    private async Task<bool> CheckSoftware(Node node, string? adapter, CancellationToken token)
    {
        var query = node.HealthQuery!;
        if (!options.Live)
        {
            await Task.Delay(70, token);
            return node.Simulation != "timeout";
        }
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(query.TimeoutMs);
        try
        {
            var address = ProjectRules.ParseIp(node.Ip);
            var binding = adapter is null ? null : UdpNetwork.Resolve(adapter, address.AddressFamily);
            if (adapter is not null && binding is null)
                return false;
            using var client = UdpNetwork.Connect(address, query.Port, binding);
            await client.SendAsync(Encoding.UTF8.GetBytes(query.Payload).AsMemory(), wait.Token);
            while (true)
            {
                var packet = await client.ReceiveAsync(wait.Token);
                if (DispatchPlan.Matches(packet.Buffer, new(query.Match, query.Reply, query.TimeoutMs)))
                    return true;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (SocketException) { return false; }
    }

    public static async Task<Health> Probe(Node node, RuntimeOptions options, CancellationToken token)
    {
        if (!options.Live)
        {
            await Task.Delay(70, token);
            return node.Simulation == "pingFail"
                ? new(node.Ip, "offline", null, DateTimeOffset.Now)
                : new(node.Ip, "online", 12, DateTimeOffset.Now);
        }
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(ProjectRules.ParseIp(node.Ip), 800).WaitAsync(token);
            return reply.Status == IPStatus.Success
                ? new(node.Ip, "online", reply.RoundtripTime, DateTimeOffset.Now)
                : new(node.Ip, "offline", null, DateTimeOffset.Now);
        }
        catch (PingException)
        {
            return new(node.Ip, "unknown", null, DateTimeOffset.Now);
        }
    }
}

internal sealed record HealthScan(bool InProgress, long Version, DateTimeOffset? LastCompletedAt);

internal sealed class NodeHealthWorker(ProjectStore store, NodeHealth health, ILogger<NodeHealthWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = 0;
        var next = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var project = store.Get().Project;
            var now = DateTimeOffset.UtcNow;
            if (interval != project.HealthPollSeconds)
            {
                interval = project.HealthPollSeconds;
                next = now.AddSeconds(interval);
            }
            var progress = health.Progress;
            if (interval == 0 || now < next || progress.InProgress || now < progress.LastCompletedAt?.AddSeconds(interval))
                continue;
            try { await health.Refresh(project, stoppingToken, automatic: true); }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(error, "Automatic node health check failed.");
            }
            next = DateTimeOffset.UtcNow.AddSeconds(interval);
        }
    }
}

internal static class ScheduleCalendar
{
    public static int Priority(Schedule schedule, DateOnly day) => schedule.Mode switch
    {
        "date" when schedule.StartDate == day => 3,
        "holiday" when schedule.StartDate <= day && schedule.EndDate >= day => 2,
        "weekly" when schedule.Days is null || schedule.Days.Contains(day.DayOfWeek) => 1,
        _ => 0
    };

    public static int ActivePriority(Project project, DateOnly day)
    {
        var playable = project.Lists.Where(list => list.Cues.Length > 0).Select(list => list.Id).ToHashSet();
        return project.Schedules.Where(schedule => schedule.Enabled && playable.Contains(schedule.ListId))
            .Select(schedule => Priority(schedule, day)).DefaultIfEmpty(0).Max();
    }

    public static string Mode(int priority) => priority switch
    {
        3 => "date",
        2 => "holiday",
        _ => "weekly"
    };
}

internal sealed class ScheduleWorker(ProjectStore store, CueEngine engine) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastMinute = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var attempted = new Dictionary<string, DateOnly>();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = DateTime.Now;
            var minute = now.ToString("yyyy-MM-dd HH:mm");
            if (minute == lastMinute)
                continue;
            lastMinute = minute;
            var date = DateOnly.FromDateTime(now);
            var project = store.Get().Project;
            var priority = ScheduleCalendar.ActivePriority(project, date);
            var startedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var schedule in project.Schedules.Where(item => item.Enabled && item.At == now.ToString("HH:mm")))
            {
                var taskPriority = ScheduleCalendar.Priority(schedule, date);
                if (taskPriority == 0)
                    continue;
                if (attempted.TryGetValue(schedule.Id, out var previous) && previous == date)
                    continue;
                attempted[schedule.Id] = date;
                if (taskPriority < priority)
                    engine.SkipSchedule(schedule, priority == 3 ? "节目模式：指定日任务优先。" : "节目模式：节假日任务优先。");
                else if (project.Lists.FirstOrDefault(list => list.Id == schedule.ListId && list.Cues.Length > 0) is not CueList list)
                    engine.SkipSchedule(schedule, "目标 Cue 列表为空。");
                else if (startedGroups.Contains(list.PlaybackGroup))
                    engine.SkipSchedule(schedule, $"播放组「{list.PlaybackGroup}」同一时刻已有定时任务。");
                else if (engine.Trigger(project, schedule.ListId, "schedule").Accepted)
                    startedGroups.Add(list.PlaybackGroup);
            }
            foreach (var id in attempted.Where(item => item.Value < date.AddDays(-1)).Select(item => item.Key).ToArray())
                attempted.Remove(id);
        }
    }
}
