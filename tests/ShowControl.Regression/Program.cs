using System.Net;
using System.Net.Sockets;
using System.Text;
using ShowControl;

var scratch = Path.Combine(Path.GetTempPath(), "codex", "ShowControl", "regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    if (args.Length > 0)
    {
        var saved = System.Text.Json.JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(args[0]),
            System.Text.Json.JsonSerializerOptions.Web) ?? throw new Exception("工程为空");
        ProjectRules.Check(DispatchPlan.Normalize(saved.Project));
        Console.WriteLine("通过 已保存工程兼容性校验");
    }
    using var commandReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var commandPort = ((IPEndPoint)commandReceiver.Client.LocalEndPoint!).Port;
    var node = new Node("127.0.0.1", "LAS", Communication: new(TargetPort: commandPort));
    var cue = new Cue("cue", "播放", "g1", null, "NEXT", "utf8", null, false, "IMOK", 100, "retry", 1,
        AdditionalTargets: [new("g2", null, "NEXT", "utf8", null, new("none"))],
        Response: new("none"));
    var owner = new CueList("owner", "思明节目", [cue], PlaybackGroup: "思明", StopListId: "stop");
    var stop = new CueList("stop", "LAS 停止", [cue with { Id = "stop-cue", Payload = "STOP", AdditionalTargets = [] }],
        PlaybackMode: "loop", IntervalMs: 100);
    var other = new CueList("other", "湖里节目", [cue with { Id = "other-cue", AdditionalTargets = [] }], PlaybackGroup: "湖里");
    var day = new DateOnly(2026, 11, 12);
    var project = new Project("回归工程", [new("show", "演出", [new("section", "区", [owner, stop, other])])],
        [new("g1", "区域", [node]), new("g2", "重点楼栋", [node])],
        [new("template", "启动", "START", "utf8", new("exact", "IMOK", 100), 8000, AllowRetry: true)],
        [new("special", "思明指定日", "owner", "20:00", true, "date", StartDate: day),
         new("daily", "湖里日常", "other", "18:00", true)], []);
    project = DispatchPlan.Normalize(project);
    ProjectRules.Check(project);

    var nicProject = project with {
        UdpProjectInterfaceIds = ["ethernet-1", "ethernet-2"],
        UdpRoutes = [new("192.0.2.0/24", "ethernet-2"), new("192.0.2.64/26", "ethernet-1")]
    };
    ProjectRules.Check(nicProject);
    var router = new UdpNetwork.Router(nicProject);
    Check(router.Select(IPAddress.Parse("192.0.2.21")) == "ethernet-2" &&
          router.Select(IPAddress.Parse("192.0.2.70")) == "ethernet-1" &&
          router.Select(IPAddress.Parse("198.51.100.21")) == "ethernet-1" &&
          router.Select(IPAddress.Loopback) is null, "目标网段、勾选顺序和回环按预期分配网卡");
    Check(new UdpNetwork.Router(nicProject with { UdpProjectInterfaceIds = null })
          .Select(IPAddress.Parse("198.51.100.21")) is null, "旧工程的未匹配目标保持系统路由");
    Reject(() => ProjectRules.Check(nicProject with { UdpProjectInterfaceIds = ["ethernet-1"] }),
        "网段规则不得引用本工程未勾选的网卡");

    var plan = DispatchPlan.Build(project, cue);
    Check(plan.Length == 1 && plan[0].Group.Contains("区域") && plan[0].Group.Contains("重点楼栋"), "重叠组相同指令合并并保留来源");
    Check(plan[0].Port == commandPort && !plan[0].AllowRetry, "节点端口继承且旧配置默认禁用重试");
    var different = cue with { AdditionalTargets = [new("g2", null, "OTHER", "utf8", null, new("none"))] };
    Check(DispatchPlan.Build(project, different).Length == 2, "不同指令保留");
    Reject(() => DispatchPlan.Build(project, cue with {
        AdditionalTargets = [new("g2", null, "NEXT", "utf8", null, new("exact", "IMOK"))]
    }), "重复报文具有不同回执时报告冲突");

    var inherited = cue with { TemplateId = "template", AdditionalTargets = [], Response = new("inherit") };
    plan = DispatchPlan.Build(project, inherited);
    Check(plan[0].Port == 8000 && plan[0].AllowRetry && plan[0].Response.Mode == "exact", "模板端口回执及重试继承");
    Check(DispatchPlan.Build(project, inherited with { Port = 7000, AllowRetry = false })[0] is { Port: 7000, AllowRetry: false },
        "显式 Cue 覆盖得到保留");
    Check(DispatchPlan.Build(project, inherited with { Port = 7000, NodeOverrides = [new(node.Ip, Port: 6000)] })[0].Port == 6000,
        "节点例外优先");
    Check(DispatchPlan.Build(project, cue with { AdditionalTargets = [], Response = null })[0].Response.Text == "IMOK",
        "旧工程回执字段兼容");
    var strictReply = new ReplyPolicy("exactStrict", "ACK");
    DispatchPlan.CheckResponse(strictReply);
    Check(DispatchPlan.Matches("ACK"u8.ToArray(), strictReply) &&
          !DispatchPlan.Matches(" ACK "u8.ToArray(), strictReply) &&
          !DispatchPlan.Matches("ACK\r\n"u8.ToArray(), strictReply), "严格文本匹配区分首尾空白和换行");
    Check(DispatchPlan.Matches(" ACK "u8.ToArray(), strictReply with { Mode = "exact" }),
        "旧文本匹配继续忽略首尾空白");
    Check(ScheduleCalendar.ActivePriority(project, day, "思明") == 3 &&
          ScheduleCalendar.ActivePriority(project, day, "湖里") == 1, "不同播放组独立判断排期");
    var sameGroup = project with { Shows = [new("show", "演出", [new("section", "区",
        [owner, stop, other with { PlaybackGroup = "思明" }])])] };
    Check(ScheduleCalendar.ActivePriority(sameGroup, day, "思明") == 3, "同播放组采用指定日优先");

    var detached = node with { Ip = "127.0.0.2", Label = "暂未分组" };
    var retained = DispatchPlan.Normalize(project with { NodeLibrary = [node, detached] });
    Check(retained.NodeLibrary!.Length == 2, "节点移出控制组后保留台账");
    ProjectRules.Check(retained);
    Reject(() => ProjectRules.Check(retained with { NodeLibrary = [node, detached with { Communication = new(TargetPort: -1) }] }),
        "未分组节点也校验通信参数");

    using var store = new ProjectStore(Path.Combine(scratch, "data"));
    using var logs = new ExecutionLogStore(Path.Combine(scratch, "data"));
    using var transport = new UdpTransport(store, logs);
    var engine = new CueEngine(new Lifetime(), logs, transport);

    async Task<EventEntry[]> RunCue(Cue testCue)
    {
        logs.ClearView();
        var candidate = project with {
            Shows = [new("show", "演出", [new("section", "区", [owner with { Cues = [testCue], StopListId = null }])])],
            IpGroups = [new("g1", "区域", [node]), new("g2", "重点楼栋", [node])]
        };
        Check(engine.Trigger(candidate, "owner", "manual").Accepted, "测试流程接受请求");
        await engine.Drain(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        return logs.View(null).Events!;
    }

    var events = await RunCue(cue);
    Check(events.Count(item => item.Status == "send") == 1, "重叠组本机 UDP 实际发送一次");
    events = await RunCue(cue with { Ping = true, AllowRetry = true,
        AdditionalTargets = [new("g2", null, "NEXT", "utf8", null, new("none"), AllowRetry: true)] });
    Check(events.Count(item => item.Status == "send") == 1 &&
          events.Any(item => item.Status == "ping") &&
          events.Any(item => item.Status == "success"), "Ping 与 UDP 发送分别记录");
    var waiting = cue with { AdditionalTargets = [], Response = new("exact", "IMOK", 100) };
    events = await RunCue(waiting);
    Check(events.Count(item => item.Status == "send") == 1 && events.Any(item => item.Status == "retryBlocked"),
        "未允许重试的命令超时只发送一次");
    events = await RunCue(waiting with { AllowRetry = true });
    Check(events.Count(item => item.Status == "send") == 2, "明确允许的命令按配置重试");

    logs.ClearView();
    var looping = project with { Shows = [new("show", "演出", [new("section", "区",
        [owner with { PlaybackMode = "loop", IntervalMs = 3000 }, stop, other])])] };
    Check(engine.Trigger(looping, "owner", "manual").Accepted, "原现场流程启动");
    await WaitUntil(() => logs.View(null).Events!.Any(item => item.Status == "send"));
    Check(engine.StopScene(looping, "owner").Accepted, "现场停止流程接受请求");
    await engine.Drain(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    Check(logs.View(null).Events!.Count(item => item.Source == "sceneStop" && item.Status == "send" && item.Message.Contains("STOP")) == 1,
        "现场停止等待原流程退出并只执行停止流程一次");
    Check(engine.Status().Length == 0, "现场停止结束后释放播放组");
    Check(!engine.StopScene(project, "other").Accepted, "缺少停止映射时返回明确错误");

    var multipleRuns = project with { Shows = [new("show", "演出", [new("section", "区",
        [owner with { PlaybackMode = "loop", IntervalMs = 3000 }, stop,
         other with { PlaybackMode = "loop", IntervalMs = 3000 }])])] };
    Check(engine.Trigger(multipleRuns, "owner", "manual").Accepted &&
          engine.Trigger(multipleRuns, "other", "manual").Accepted, "两个播放组同时启动");
    var oldRun = engine.Status().Single(run => run.ListId == "owner").Id;
    Check(engine.Trigger(multipleRuns, "owner", "manual").Accepted, "同一列表再次启动");
    var newRun = engine.Status().Single(run => run.ListId == "owner").Id;
    Check(newRun != oldRun && !engine.Stop(oldRun), "旧运行 ID 已失效");
    Check(engine.StopList("owner"), "按列表停止当前运行");
    await WaitUntil(() => engine.Status().All(run => run.ListId != "owner"));
    Check(engine.Status().Any(run => run.ListId == "other" && !run.Stopping), "按列表停止不影响其他播放组");
    engine.StopAll();
    await engine.Drain(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

    var movedRuns = multipleRuns with { Shows = [new("show", "演出", [new("section", "区",
        [owner with { PlaybackGroup = "新播放组", PlaybackMode = "loop", IntervalMs = 3000 }, stop, other])])] };
    var engineGate = typeof(CueEngine).GetField("gate", System.Reflection.BindingFlags.NonPublic |
        System.Reflection.BindingFlags.Instance)!.GetValue(engine)!;
    lock (engineGate)
    {
        Check(engine.Trigger(multipleRuns, "owner", "manual").Accepted &&
            engine.Trigger(movedRuns, "owner", "manual").Accepted, "同一列表移组后重新触发");
        Check(engine.Status().Count(run => run.ListId == "owner") == 2, "移组时两笔运行短暂并存");
        Check(engine.StopList("owner") && engine.Status().Where(run => run.ListId == "owner").All(run => run.Stopping),
            "按列表停止同时取消旧组和新组的运行");
    }
    await engine.Drain(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

    var beforeScheduleEdit = store.Save(store.Get() with { Project = project })!;
    Task<TriggerResult?> scheduledTrigger;
    // Hold the engine lock while saving a change ahead of the queued trigger.
    lock (engineGate)
    {
        scheduledTrigger = Task.Run(() => engine.TriggerScheduled(store, beforeScheduleEdit, owner.Id,
            () => throw new Exception("旧工程版本不得记录定时触发")));
        var disabled = project with { Schedules = project.Schedules.Select(schedule => schedule with { Enabled = false }).ToArray() };
        Check(store.Save(beforeScheduleEdit with { Project = disabled }) is not null, "保存禁用定时任务");
    }
    Check(await scheduledTrigger.WaitAsync(TimeSpan.FromSeconds(5)) is null && engine.Status().Length == 0,
        "保存禁用后旧版本定时任务不会入队");

    Check(ExecutionGuard.Check(7, null) is { Status: 400 }, "执行请求缺少工程版本时被拒绝");
    Check(ExecutionGuard.Check(7, 6) is { Status: 412 }, "执行请求的工程版本过期时返回 412");
    Check(ExecutionGuard.Check(7, 7) is null, "工程版本一致时允许执行");

    var conflicting = cue with { AdditionalTargets = [new("g2", null, "NEXT", "utf8", null, new("exact", "IMOK"))] };
    var conflictProject = project with { Shows = [new("show", "演出", [new("section", "区",
        [owner with { Cues = [conflicting], StopListId = null }, stop, other])])] };
    ProjectRules.Check(conflictProject);
    Console.WriteLine("通过 回执冲突的旧工程仍可加载和保存");
    var conflictList = conflictProject.Lists.Single(item => item.Id == "owner");
    Check(DispatchPlan.Conflicts(conflictProject, conflictList) is { Length: 1 } found && found[0].Contains("播放"),
        "冲突定位到列表和 Cue");
    Check(!engine.Trigger(conflictProject, "owner", "manual").Accepted && engine.Status().Length == 0,
        "有冲突的发送计划整表拒绝且不发送");

    var portless = new Node("127.0.0.3", "无端口节点");
    var noPortCue = cue with { Id = "noport-cue", Name = "缺端口", IpGroupId = "g3", Port = null, AdditionalTargets = [] };
    var noPortProject = DispatchPlan.Normalize(project with {
        IpGroups = [.. project.IpGroups, new IpGroup("g3", "无端口组", [portless])],
        Shows = [new("show", "演出", [new("section", "区", [owner with { Cues = [noPortCue], StopListId = null }, stop, other])])]
    });
    ProjectRules.Check(noPortProject);
    Console.WriteLine("通过 节点缺少目标端口的旧工程仍可加载和保存");
    Check(DispatchPlan.Build(noPortProject, noPortCue, strict: false).Length == 0, "宽松模式跳过缺端口的发送项");
    Reject(() => DispatchPlan.Build(noPortProject, noPortCue), "严格模式下缺少目标端口仍报错");
    var noPortList = noPortProject.Lists.Single(item => item.Id == "owner");
    Check(DispatchPlan.Conflicts(noPortProject, noPortList) is { Length: 1 } missing && missing[0].Contains("缺端口"),
        "缺少目标端口定位到列表和 Cue");
    Check(!engine.Trigger(noPortProject, "owner", "manual").Accepted && engine.Status().Length == 0,
        "缺少目标端口的发送计划整表拒绝且不发送");
    var noPortScheduled = noPortProject with
    {
        Shows = [new("show", "演出", [new("section", "区",
            [noPortProject.Lists.Single(item => item.Id == "owner"), other with { PlaybackGroup = "思明" }])])],
        Schedules = [new("special", "无效指定日", "owner", "20:00", true, "date", StartDate: day),
            new("daily", "有效平日", "other", "20:00", true)]
    };
    ProjectRules.Check(noPortScheduled);
    Check(ScheduleCalendar.ActivePriority(noPortScheduled, day, "思明") == 1,
        "缺少端口的指定日任务不压制同组有效平日任务");
    var conflictScheduled = noPortScheduled with
    {
        Shows = [new("show", "演出", [new("section", "区",
            [owner with { Cues = [conflicting], StopListId = null }, other with { PlaybackGroup = "思明" }])])]
    };
    ProjectRules.Check(conflictScheduled);
    Check(ScheduleCalendar.ActivePriority(conflictScheduled, day, "思明") == 1,
        "回执冲突的指定日任务不压制同组有效平日任务");
    var fixedScheduled = noPortScheduled with
    {
        Shows = [new("show", "演出", [new("section", "区",
            [owner with { StopListId = null }, other with { PlaybackGroup = "思明" }])])]
    };
    Check(ScheduleCalendar.ActivePriority(fixedScheduled, day, "思明") == 3,
        "修复指令配置后指定日任务重新获得优先级");

    var importNode = new Node("127.0.0.1", "LAS", Communication: new(TargetPort: 9000, SenderPort: 5000, ReceiverPort: 6000), Customized: true);
    var importLibrary = new[] { new IpGroup("g1", "区域", [importNode]) };
    byte[] ImportTable(string body) => Encoding.UTF8.GetBytes("IP 分组\tIP 地址\t默认目标端口\r\n" + body);
    var importMerged = IpLibraryFiles.Import(new MemoryStream(ImportTable("新组\t127.0.0.1\t\r\n")), "x.txt", importLibrary);
    Check(importMerged.Length == 2 && importMerged.All(item => item.Nodes.Single() is
        { Customized: true, Communication: { TargetPort: 9000, SenderPort: 5000, ReceiverPort: 6000 } }),
        "已有 IP 加入新组时保留原端口和单独设置标记");
    var importCleared = IpLibraryFiles.Import(new MemoryStream(ImportTable("区域\t127.0.0.1\t-\r\n")), "x.txt", importLibrary);
    Check(importCleared.Single().Nodes.Single().Communication is { TargetPort: null, SenderPort: 5000, ReceiverPort: 6000 },
        "仅用显式清空标记才清除目标端口，其他字段保留");

    using var responder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var port = ((IPEndPoint)responder.Client.LocalEndPoint!).Port;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var reply = Task.Run(async () => {
        var packet = await responder.ReceiveAsync(timeout.Token);
        Check(Encoding.UTF8.GetString(packet.Buffer) == "STOP", "本机 UDP 接收实际字节");
        await responder.SendAsync(Encoding.UTF8.GetBytes("IMOK"), packet.RemoteEndPoint, timeout.Token);
    });
    var result = await transport.Exchange(IPAddress.Loopback, port, Encoding.UTF8.GetBytes("STOP"), "utf8",
        null, null, 0, new("exact", "IMOK", 1500), new("test", "", "", ""), (_, _) => {}, timeout.Token);
    await reply;
    Check(result, "本机真实 UDP 回执匹配成功");

    var ungrouped = node with { PingEnabled = false, HealthQuery = new(port, "HEALTH", " READY \r\n", "exactStrict") };
    var healthProject = project with { Shows = [], IpGroups = [], NodeLibrary = [ungrouped], Schedules = [] };
    Check(store.Save(store.Get() with { Project = healthProject }) is not null, "保存未分组节点健康查询");
    using var healthTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var healthReply = Task.Run(async () =>
    {
        var packet = await responder.ReceiveAsync(healthTimeout.Token);
        Check(packet.Buffer.AsSpan().SequenceEqual("HEALTH"u8), "健康扫描向未分组节点发送查询");
        await responder.SendAsync(" READY \r\n"u8.ToArray(), packet.RemoteEndPoint, healthTimeout.Token);
    });
    var health = new NodeHealth(transport, logs);
    var healthRows = await health.Refresh(store.Get().Project, healthTimeout.Token);
    await healthReply;
    Check(healthRows.Single() is { SoftwareStatus: "online", LatestReceipt.Text: " READY \r\n" },
        "健康扫描返回未分组节点状态与最近回执");
    var renamed = store.Save(store.Get() with { Project = healthProject with { NodeLibrary = [ungrouped with { Label = "改名" }] } })!;
    Check(health.Get(renamed.Project).Single() is { SoftwareStatus: "online", LatestReceipt.Text: " READY \r\n" },
        "改名保留在线状态和回执");
    var changedPort = healthProject with { NodeLibrary = [ungrouped with { HealthQuery = ungrouped.HealthQuery! with { Port = port == 1 ? 2 : 1 } }] };
    Check(health.Get(changedPort).Single().SoftwareStatus == "unknown", "改变实际通信配置后重新确认在线状态");
    foreach (var format in new[] { "xlsx", "txt" })
    {
        var exported = IpLibraryFiles.Export([new("health-group", "健康组", [ungrouped])], format);
        using var input = new MemoryStream(exported.Content);
        var restored = IpLibraryFiles.Import(input, exported.FileName, []);
        Check(restored.Single().Nodes.Single().HealthQuery == ungrouped.HealthQuery,
            $"严格健康回执及空白经 {format} 导出导入保留");
    }
    var journalDir = Path.Combine(scratch, "journal");
    Directory.CreateDirectory(journalDir);
    var journalPath = Path.Combine(journalDir, "schedule-live.json");
    var journalDay = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
    File.WriteAllText(journalPath, "{\"minute\":1,\"attempts\":[{\"id\":\"a\",\"day\":\"" + journalDay + "\",\"name\":\"日常\",\"due\":\"" +
        DateTimeOffset.Now.ToString("o") + "\",\"result\":\"accepted\",\"detail\":\"\"},{\"id\":\"b\",\"day\":\"not-a-date\"}]}");
    var damagedJournal = new ScheduleJournal(journalDir);
    Check(!damagedJournal.CanRun && damagedJournal.Status.NeedsRecovery && damagedJournal.Status.Error is not null,
        "定时记录损坏时暂停定时并提示需要恢复");
    Check(damagedJournal.Status.Attempts.Length == 1 && damagedJournal.Status.Attempts[0].Id == "a",
        "损坏的定时记录中仍可读的任务被抢救出来");
    Check(damagedJournal.Find("a", DateOnly.FromDateTime(DateTime.Now)) is not null, "抢救出的任务继续参与去重");
    damagedJournal.Recover();
    Check(damagedJournal.CanRun && !damagedJournal.Status.NeedsRecovery && damagedJournal.Status.Error is null,
        "确认恢复后定时继续运行");
    Check(Directory.GetFiles(journalDir, "schedule-live.damaged-*.json").Length == 1, "恢复时保留损坏的原记录");
    Check(Math.Abs(damagedJournal.Minute!.Value - DateTimeOffset.Now.ToUnixTimeSeconds() / 60) <= 1 &&
          damagedJournal.Status.Attempts.Length == 1, "恢复时点为当前分钟且保留已执行任务，不补播旧任务");
    var reopenedJournal = new ScheduleJournal(journalDir);
    Check(reopenedJournal.CanRun && reopenedJournal.RecoveredThroughMinute == reopenedJournal.Minute,
        "重启后仍保留恢复所在分钟的起播截止点");
    reopenedJournal.Checkpoint(reopenedJournal.Minute!.Value + 1, DateOnly.FromDateTime(DateTime.Now), 2);
    Check(reopenedJournal.RecoveredThroughMinute == damagedJournal.Minute, "下一分钟检查点保留恢复截止点");
    File.WriteAllText(journalPath, "not json at all");
    var garbage = new ScheduleJournal(journalDir);
    Check(!garbage.CanRun && garbage.Status.Attempts.Length == 0, "完全无法解析的记录抢救为空并仍可恢复");
    garbage.Recover();
    Check(garbage.CanRun, "完全损坏的记录恢复后可继续");

    var cutDue = DateTimeOffset.Now.ToString("o");
    File.WriteAllText(journalPath, $$"""{"minute":1,"attempts":[{"id":"a","day":"{{journalDay}}","name":"日常","due":"{{cutDue}}","result":"accepted","detail":""},{"id":"b","day":"{{journalDay}}","name":"午间","due":"{{cutDue}}","result":"acce""");
    var truncated = new ScheduleJournal(journalDir);
    Check(!truncated.CanRun && truncated.Status.Attempts.Length == 1 && truncated.Status.Attempts[0].Id == "a",
        "文件被截断时保留截断处之前已完整写入的任务记录");
    truncated.Recover();
    Check(truncated.CanRun && truncated.Find("a", DateOnly.FromDateTime(DateTime.Now)) is not null,
        "截断记录恢复后已抢救的任务继续参与去重");

    var zoneNorth = owner with { ControlZones = ["北区"] };
    var zoneSouth = other with { ControlZones = ["南区"] };
    Project WithLists(params CueList[] scoped) => project with { Shows = [new("show", "演出", [new("section", "区", scoped)])] };
    Check(ControlScope.Overlap(project, owner, other) is { Declared: false } legacy && legacy.SharedIps.Contains("127.0.0.1"),
        "未声明分区时按共用节点推断并标记未声明");
    Check(ControlScope.Overlap(project, zoneNorth, zoneSouth) is null, "声明了不同分区的列表互不影响");
    Check(ControlScope.Overlap(project, zoneNorth, zoneSouth with { ControlZones = ["北区"] }) is { Declared: true }, "分区相同时判定重叠");
    Check(ControlScope.Overlap(project, zoneNorth, zoneSouth with { ControlZones = ["*"] }) is { Declared: true }, "整节点与任何分区重叠");
    ProjectRules.Check(WithLists(zoneNorth, stop, zoneSouth));
    Console.WriteLine("通过 声明控制分区的工程可以保存");
    Reject(() => ProjectRules.Check(WithLists(owner with { ControlZones = ["*", "北区"] }, stop, other)), "整节点标记不能与其他分区并用");
    Reject(() => ProjectRules.Check(WithLists(owner with { ControlZones = ["北区", "北区"] }, stop, other)), "控制分区不能重复");

    var activeSouth = zoneSouth with
    {
        PlaybackMode = "loop", IntervalMs = 3000,
        Cues = [zoneSouth.Cues[0] with { IpGroupId = "g2" }]
    };
    var oldScope = WithLists(zoneNorth, stop, activeSouth);
    Check(engine.Trigger(oldScope, activeSouth.Id, "manual").Accepted, "启动有明确分区的播放组");
    var editedScope = WithLists(zoneNorth, stop, activeSouth with { ControlZones = ["新区"] }) with
    {
        IpGroups = [project.IpGroups[0], project.IpGroups[1] with { Nodes = [detached] }]
    };
    Check(engine.RunningOverlaps(editedScope, zoneNorth.Id) is [{ Declared: false, ListId: "other" }],
        "未标注分区的停止命令按正在运行的旧节点配置提示影响范围");
    engine.StopAll();
    await engine.Drain(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

    var padDirectory = Path.Combine(scratch, "pads");
    using (var padStore = new ProjectStore(padDirectory))
    {
        var padProject = project with { Pads = [
            new("pad-scene", "演出和停止现场", owner.Id, StopAction: "scene"),
            new("pad-run", "演出和停止调度", owner.Id, StopAction: "run")
        ] };
        Check(padStore.Save(padStore.Get() with { Project = padProject }) is not null, "保存带附带停止的 Action Pad");
        var padSnapshot = padStore.Get();
        var unboundPad = padProject with { Pads = [.. padProject.Pads, new("pad-unbound", "未绑定", other.Id, Action: "stopScene")] };
        Reject(() => padStore.Save(padSnapshot with { Project = unboundPad }), "不能保存未绑定流程的现场停止按钮");
    }
    using (var reopenedPads = new ProjectStore(padDirectory))
        Check(!reopenedPads.RequiresRecovery && reopenedPads.Get().Project.Pads.Select(pad => pad.StopAction)
            .SequenceEqual(["scene", "run"]), "重启后保留 Action Pad 的两种附带停止动作");

    Console.WriteLine("全部回归验证通过");
}
finally
{
    Directory.Delete(scratch, recursive: true);
}

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("失败：" + name);
    Console.WriteLine("通过 " + name);
}
static void Reject(Action action, string name)
{
    try { action(); }
    catch (ArgumentException) { Console.WriteLine("通过 " + name); return; }
    throw new Exception("失败：" + name);
}
static async Task WaitUntil(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!condition()) await Task.Delay(10, timeout.Token);
}
sealed class Lifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() { }
}
