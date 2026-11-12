using System.Text.Json;

namespace ShowControl;

internal sealed record ScheduleAttempt(string Id, DateOnly Day, string Name, DateTimeOffset Due, string Result, string Detail);
internal sealed record ScheduleState(long? Minute, ScheduleAttempt[] Attempts, long? Revision = null, long? RecoveredThroughMinute = null);
internal sealed record ScheduleStatus(string? Error, ScheduleAttempt[] Attempts, bool NeedsRecovery = false);

internal sealed class ScheduleJournal
{
    private readonly object recoveryGate = new();
    private readonly string path;
    private ScheduleState state = new(null, []);
    private string? error;
    private volatile bool unreadable;

    public ScheduleJournal(string directory, bool live)
    {
        path = Path.Combine(directory, live ? "schedule-live.json" : "schedule-simulation.json");
        if (!File.Exists(path)) return;
        byte[]? bytes = null;
        try
        {
            bytes = File.ReadAllBytes(path);
            state = JsonSerializer.Deserialize<ScheduleState>(bytes, JsonSerializerOptions.Web)
                ?? throw new JsonException("空的定时记录。");
            if (state.Attempts is null || state.Attempts.Any(attempt => attempt is null)) throw new JsonException("定时记录缺少任务。");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException)
        {
            error = "定时记录无法读取，定时触发已暂停。请管理员核对下方已抢救的任务记录后确认恢复。";
            state = new(null, bytes is null ? [] : Salvage(bytes));
            unreadable = true;
        }
    }

    public long? Minute => Volatile.Read(ref state).Minute;
    public long? Revision => Volatile.Read(ref state).Revision;
    public long? RecoveredThroughMinute => Volatile.Read(ref state).RecoveredThroughMinute;
    public bool CanRun => !unreadable;
    public ScheduleStatus Status => new(Volatile.Read(ref error),
        Volatile.Read(ref state).Attempts.OrderByDescending(attempt => attempt.Due).Take(20).ToArray(), unreadable);
    public ScheduleAttempt? Find(string id, DateOnly day) => state.Attempts.FirstOrDefault(attempt => attempt.Id == id && attempt.Day == day);

    public void Record(ScheduleAttempt attempt)
    {
        var remaining = state.Attempts.Where(item => item.Id != attempt.Id || item.Day != attempt.Day);
        Save(state with { Attempts = [.. remaining, attempt] });
    }

    public void Checkpoint(long minute, DateOnly today, long revision) => Save(state with
    {
        Minute = minute,
        Revision = revision,
        Attempts = state.Attempts.Where(attempt => attempt.Day >= today.AddDays(-14)).ToArray()
    });

    // 损坏记录可能缺少当前分钟的执行结果。下一分钟恢复自动触发，避免当前分钟重复起播。
    public void Recover()
    {
        lock (recoveryGate)
        {
            if (!unreadable) return;
            var now = DateTimeOffset.Now;
            if (File.Exists(path))
            {
                var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
                File.Copy(path, Path.Combine(Path.GetDirectoryName(path)!,
                    Path.GetFileNameWithoutExtension(path) + ".damaged-" + stamp + ".json"), overwrite: false);
            }
            var today = DateOnly.FromDateTime(now.DateTime);
            var minute = now.ToUnixTimeSeconds() / 60;
            var next = new ScheduleState(minute,
                state.Attempts.Where(attempt => attempt.Day >= today.AddDays(-14)).ToArray(),
                RecoveredThroughMinute: minute);
            ProjectStore.SaveFile(path, next);
            Volatile.Write(ref state, next);
            Volatile.Write(ref error, null);
            unreadable = false;
        }
    }

    // 文件被截断时整份 JSON 无法解析，所以流式读取 attempts：
    // 逐条解析，遇到截断处停止，保留此前已完整写入的记录。
    private static ScheduleAttempt[] Salvage(byte[] bytes)
    {
        var found = new List<ScheduleAttempt>();
        try
        {
            var reader = new Utf8JsonReader(bytes);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return [];
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
                var isAttempts = reader.ValueTextEquals("attempts");
                if (!reader.Read()) break;
                if (!isAttempts) { reader.Skip(); continue; }
                if (reader.TokenType != JsonTokenType.StartArray) break;
                while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                {
                    using var document = JsonDocument.ParseValue(ref reader);
                    try
                    {
                        if (document.RootElement.Deserialize<ScheduleAttempt>(JsonSerializerOptions.Web) is { Id: not null, Name: not null, Result: not null } attempt)
                            found.Add(attempt with { Detail = attempt.Detail ?? "" });
                    }
                    catch (JsonException) { }
                }
                break;
            }
        }
        catch (JsonException) { }
        return found.ToArray();
    }

    private void Save(ScheduleState next)
    {
        try
        {
            ProjectStore.SaveFile(path, next);
            Volatile.Write(ref state, next);
            Volatile.Write(ref error, null);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            Volatile.Write(ref error, "定时记录写入失败，等待磁盘恢复。");
            throw;
        }
    }
}
