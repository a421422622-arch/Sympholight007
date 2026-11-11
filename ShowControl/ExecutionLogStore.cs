using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace ShowControl;

internal sealed record LogFolderStatus(string Directory, long LostEntries, string? Error);

internal sealed class ExecutionLogStore(string dataDirectory) : BackgroundService
{
    private readonly object recentGate = new();
    private readonly Queue<EventEntry> recent = new();
    public EventEntry[] Recent { get { lock (recentGate) return recent.Reverse().ToArray(); } }
    private readonly string folder = Path.GetFullPath(Path.Combine(dataDirectory, "logs"));
    private readonly Channel<EventEntry> queue = Channel.CreateBounded<EventEntry>(new BoundedChannelOptions(8192)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private long lostEntries;
    private string? error;

    public LogFolderStatus Status => new(folder, Interlocked.Read(ref lostEntries), Volatile.Read(ref error));

    public void Append(EventEntry entry)
    {
        lock (recentGate)
        {
            if (recent.Count >= 300) recent.Dequeue();
            recent.Enqueue(entry);
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
}

internal sealed class CueShutdown(CueEngine engine) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => engine.Drain(cancellationToken);
}
