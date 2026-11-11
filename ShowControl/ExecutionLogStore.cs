using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace ShowControl;

internal sealed record LogFolderStatus(string Directory, long LostEntries, string? Error);
internal sealed record LogViewSettings(int MaxRows = 300);
internal sealed record LogView(string Version, int MaxRows, EventEntry[]? Events);

internal sealed class ExecutionLogStore(string dataDirectory) : BackgroundService
{
    private readonly object recentGate = new();
    private readonly object settingsGate = new();
    private readonly Queue<EventEntry> recent = new();
    private readonly string viewId = Guid.NewGuid().ToString("N");
    private long viewRevision;
    private int maxRows = LoadMaxRows(Path.Combine(dataDirectory, "log-view.json"));
    private readonly string viewPath = Path.Combine(dataDirectory, "log-view.json");
    private readonly string folder = Path.GetFullPath(Path.Combine(dataDirectory, "logs"));
    private readonly Channel<EventEntry> queue = Channel.CreateBounded<EventEntry>(new BoundedChannelOptions(8192)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private long lostEntries;
    private string? error;

    public LogFolderStatus Status => new(folder, Interlocked.Read(ref lostEntries), Volatile.Read(ref error));

    public LogView View(string? knownVersion)
    {
        lock (recentGate)
        {
            var version = $"{viewId}:{viewRevision}";
            return new(version, maxRows, version == knownVersion ? null : recent.Reverse().ToArray());
        }
    }

    public void SetViewSettings(LogViewSettings settings)
    {
        if (settings.MaxRows is < 1 or > 2000) throw new ArgumentException("日志最大行数须为 1–2000。");
        lock (settingsGate)
        {
            ProjectStore.SaveFile(viewPath, settings);
            lock (recentGate)
            {
                maxRows = settings.MaxRows;
                while (recent.Count > maxRows) recent.Dequeue();
                viewRevision++;
            }
        }
    }

    public void ClearView()
    {
        lock (recentGate)
        {
            recent.Clear();
            viewRevision++;
        }
    }

    public void Append(EventEntry entry)
    {
        lock (recentGate)
        {
            if (recent.Count >= maxRows) recent.Dequeue();
            recent.Enqueue(entry);
            viewRevision++;
        }
        if (!queue.Writer.TryWrite(entry))
            Interlocked.Increment(ref lostEntries);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { Directory.CreateDirectory(folder); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Volatile.Write(ref error, exception.Message);
        }

        var reader = queue.Reader;
        var batch = new List<EventEntry>(256);
        while (await reader.WaitToReadAsync())
        {
            batch.Clear();
            while (batch.Count < 256 && reader.TryRead(out var entry))
                batch.Add(entry);

            foreach (var day in batch.GroupBy(entry => entry.At.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    var file = Path.Combine(folder, day.Key + ".txt");
                    await using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite,
                        bufferSize: 4096, useAsync: true);
                    await using var output = new StreamWriter(stream, new UTF8Encoding(false));
                    foreach (var entry in day)
                        await output.WriteLineAsync(Format(entry));
                    await output.FlushAsync();
                    Volatile.Write(ref error, null);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Interlocked.Add(ref lostEntries, day.Count());
                    Volatile.Write(ref error, exception.Message);
                    if (!stoppingToken.IsCancellationRequested)
                        await Task.Delay(500);
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }

    private static string Format(EventEntry entry) => string.Join('\t',
        new[] { entry.At.ToString("O", CultureInfo.InvariantCulture), entry.Source, entry.List, entry.Cue, entry.Status, entry.Message }
        .Select(value => value.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n")));

    private static int LoadMaxRows(string path)
    {
        try
        {
            if (JsonSerializer.Deserialize<LogViewSettings>(File.ReadAllBytes(path), JsonSerializerOptions.Web) is { MaxRows: >= 1 and <= 2000 } settings)
                return settings.MaxRows;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
        return new LogViewSettings().MaxRows;
    }
}

internal sealed class CueShutdown(CueEngine engine) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => engine.Drain(cancellationToken);
}
