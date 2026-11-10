using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using ShowControl;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
var port = int.TryParse(builder.Configuration["SHOWCONTROL_PORT"], out var value) && value is >= 1024 and <= 65535
    ? value : 8240;
var options = new RuntimeOptions(builder.Configuration["SHOWCONTROL_LIVE"] == "1");
var desktop = builder.Configuration["SHOWCONTROL_DESKTOP"] == "1";
var directory = builder.Configuration["SHOWCONTROL_DATA_DIR"] ?? Path.Combine(AppContext.BaseDirectory, "data");
builder.WebHost.ConfigureKestrel(server =>
{
    if (desktop)
        server.Listen(IPAddress.Loopback, 0);
    else
        server.ListenLocalhost(port);
    server.Limits.MaxRequestBodySize = 2 * 1024 * 1024;
});
builder.Services.AddSingleton(_ => new ProjectStore(directory));
builder.Services.AddSingleton(_ => new AccessControl(directory));
builder.Services.AddSingleton(_ => new WorkspaceLayout(directory));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(_ => new ExecutionLogStore(directory));
builder.Services.AddHostedService(services => services.GetRequiredService<ExecutionLogStore>());
builder.Services.AddSingleton<CueEngine>();
builder.Services.AddSingleton<NodeHealth>();
builder.Services.AddHostedService<ScheduleWorker>();
builder.Services.AddHostedService<NodeHealthWorker>();
builder.Services.AddHostedService<CueShutdown>();
builder.Services.AddProblemDetails();

var app = builder.Build();
_ = app.Services.GetRequiredService<ProjectStore>();
_ = app.Services.GetRequiredService<AccessControl>();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    var host = context.Request.Host.Host;
    if (host is not ("localhost" or "127.0.0.1" or "[::1]"))
    {
        context.Response.StatusCode = 403;
        return;
    }
    if (context.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(context.Request.Method))
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site" ||
            (origin.Length != 0 && !origin.Equals($"http://{context.Request.Host}", StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = 403;
            return;
        }
    }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
    context.Response.Headers.CacheControl = "no-store";
    if (context.Request.Path.StartsWithSegments("/api") &&
        !context.Request.Path.StartsWithSegments("/api/access") && context.Request.Path != "/api/status")
    {
        var role = context.RequestServices.GetRequiredService<AccessControl>().View(SessionToken(context)).Role;
        var adminOnly = context.Request.Path.StartsWithSegments("/api/ip-library") ||
            (HttpMethods.IsPut(context.Request.Method) && (context.Request.Path.StartsWithSegments("/api/project") ||
                context.Request.Path.StartsWithSegments("/api/layout")));
        if (role is null || (adminOnly && role != "admin"))
        {
            await Denied(role is null).ExecuteAsync(context);
            return;
        }
    }
    await next(context);
});

app.UseDefaultFiles();
app.UseStaticFiles();
var api = app.MapGroup("/api");
api.MapGet("/access", (HttpContext context, AccessControl access) => access.View(SessionToken(context)));
api.MapPost("/access/settings", (HttpContext context, AccessControl access, PasswordSettings settings) =>
{
    try
    {
        SetSession(context, access.Configure(SessionToken(context), settings));
        return Results.Ok(new AccessView(true, "admin"));
    }
    catch (UnauthorizedAccessException) { return Denied(access.View(SessionToken(context)).Role is null); }
    catch (ArgumentException error) { return Results.BadRequest(new { message = error.Message }); }
});
api.MapPost("/access/unlock", (HttpContext context, AccessControl access, UnlockRequest request) =>
{
    var result = access.Unlock(request);
    if (result.Token is null)
        return Results.Json(new { message = result.Limited ? "尝试过多，请稍后重试。" : "角色或密码不正确。" },
            statusCode: result.Limited ? 429 : 401);
    SetSession(context, result.Token);
    return Results.Ok(new AccessView(true, request.Role));
});
api.MapPost("/access/lock", (HttpContext context, AccessControl access) =>
{
    if (!access.Lock(SessionToken(context))) return Denied(locked: true);
    context.Response.Cookies.Delete(CookieName(context), new CookieOptions { Path = "/api" });
    return Results.Ok(new AccessView(true, null));
});
api.MapGet("/layout", (WorkspaceLayout layout) => layout.Get());
api.MapGet("/log-folder", (ExecutionLogStore logs) => logs.Status);
api.MapGet("/udp-interfaces", (ProjectStore store) => UdpNetwork.Interfaces(store.Get().Project));
api.MapPut("/layout", (WorkspaceLayout layout, JsonElement data) =>
{
    try
    {
        var result = layout.Save(data);
        return data.ValueKind == JsonValueKind.Array ? Results.Ok(result.Order) : Results.Ok(result);
    }
    catch (ArgumentException error) { return Results.BadRequest(new { message = error.Message }); }
    catch (JsonException) { return Results.BadRequest(new { message = "面板布局格式无效。" }); }
});
api.MapGet("/project", (ProjectStore store) => store.Get());
api.MapPut("/project", (Snapshot edit, ProjectStore store) =>
{
    try
    {
        var next = store.Save(edit);
        return next is null
            ? Results.Conflict(new { message = "其他窗口已经保存。请刷新后重新编辑。" })
            : Results.Ok(next);
    }
    catch (ArgumentException error)
    {
        return Results.BadRequest(new { message = error.Message });
    }
});
api.MapGet("/status", (HttpContext context, CueEngine engine, RuntimeOptions settings, ProjectStore store, AccessControl access,
    NodeHealth health, ExecutionLogStore logs) =>
{
    var state = engine.Status();
    var priority = ScheduleCalendar.ActivePriority(store.Get().Project, DateOnly.FromDateTime(DateTime.Now));
    var view = access.View(SessionToken(context));
    var runs = view.Role is null ? [] : state.Runs;
    var logStatus = logs.Status;
    return new { settings.Live, Active = runs.FirstOrDefault(), Runs = runs,
        Events = view.Role is null ? [] : state.Events, ActiveCount = state.Runs.Length,
        Access = view, HealthScan = view.Role is null ? null : health.Progress,
        LogStorageWarning = view.Role is not null && (logStatus.Error is not null || logStatus.LostEntries > 0),
        TimeZone = TimeZoneInfo.Local.Id,
        ScheduleMode = ScheduleCalendar.Mode(priority) };
});
api.MapPost("/run/{id}", (string id, ProjectStore store, CueEngine engine) =>
    Result(engine.Trigger(store.Get().Project, id, "manual")));
api.MapPost("/pad/{id}", (string id, ProjectStore store, CueEngine engine) =>
{
    var project = store.Get().Project;
    var pad = project.Pads.FirstOrDefault(item => item.Id == id);
    if (pad is null) return Results.NotFound(new { message = "Action Pad 按钮不存在。" });
    if (pad.Action != "stopAll") return Result(engine.Trigger(project, pad.ListId, "pad"));
    var count = engine.StopAll();
    return Results.Ok(new { accepted = true, message = $"已请求停止 {count} 个播放列表。" });
});
api.MapPost("/stop", (CueEngine engine) =>
{
    var count = engine.StopAll();
    return Results.Ok(new { stopped = count > 0, count });
});
api.MapPost("/stop/{id:guid}", (Guid id, CueEngine engine) => Results.Ok(new { stopped = engine.Stop(id) }));
api.MapGet("/health", (ProjectStore store, NodeHealth health) => health.Get(store.Get().Project));
api.MapPost("/health/refresh", async (ProjectStore store, NodeHealth health, CancellationToken token) =>
    await health.Refresh(store.Get().Project, token));
api.MapPost("/ip-library/import", async (HttpRequest request, CancellationToken token) =>
{
    try
    {
        if (!request.HasFormContentType)
            return Results.BadRequest(new { message = "请选择要导入的文件。" });
        var limit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
            limit.MaxRequestBodySize = IpLibraryFiles.MaxFileSize + 4L * 1024 * 1024;
        var form = await request.ReadFormAsync(token);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length is <= 0 or > IpLibraryFiles.MaxFileSize)
            return Results.BadRequest(new { message = "请选择非空的 .xlsx 或 .txt 文件，大小不超过 8 MB。" });
        var groups = JsonSerializer.Deserialize<IpGroup[]>(form["groups"].ToString(), JsonSerializerOptions.Web)
            ?? throw new ArgumentException("当前 IP 分组库数据无效。");
        using var input = file.OpenReadStream();
        return Results.Ok(IpLibraryFiles.Import(input, file.FileName, groups));
    }
    catch (Exception error) when (error is ArgumentException or JsonException or InvalidDataException)
    {
        return Results.BadRequest(new { message = error is ArgumentException ? error.Message : "导入文件或当前 IP 分组库数据无效。" });
    }
});
api.MapPost("/ip-library/export/{format}", (string format, IpGroup[] groups) =>
{
    try
    {
        var file = IpLibraryFiles.Export(groups, format);
        return Results.File(file.Content, file.ContentType, file.FileName);
    }
    catch (ArgumentException error)
    {
        return Results.BadRequest(new { message = error.Message });
    }
});

if (desktop)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        Console.WriteLine($"SHOWCONTROL_READY {app.Urls.Single()}");
        _ = Task.Run(() =>
        {
            // The desktop process owns stdin. EOF also arrives if it crashes.
            Console.Read();
            app.Lifetime.StopApplication();
        });
    });
}
app.Run();

static IResult Result(TriggerResult result) => result.Accepted ? Results.Accepted(value: result) : Results.Conflict(result);
static string CookieName(HttpContext context) => $"showcontrol-{context.Connection.LocalPort}";
static string? SessionToken(HttpContext context) => context.Request.Cookies[CookieName(context)];
static void SetSession(HttpContext context, string token) => context.Response.Cookies.Append(CookieName(context), token,
    new CookieOptions { Path = "/api", HttpOnly = true, SameSite = SameSiteMode.Strict });
static IResult Denied(bool locked) => Results.Json(new { message = locked ? "请先解锁。" : "此操作需要管理员权限。" },
    statusCode: locked ? 423 : 403);
