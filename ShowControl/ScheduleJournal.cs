using System.Text.Json;

namespace ShowControl;

internal sealed record ScheduleAttempt(string Id, DateOnly Day, string Name, DateTimeOffset Due, string Result, string Detail);
internal sealed record ScheduleState(long? Minute, ScheduleAttempt[] Attempts, long? Revision = null);
internal sealed record ScheduleStatus(string? Error, ScheduleAttempt[] Attempts);

internal sealed class ScheduleJournal
{
    private readonly string path;
    private ScheduleState state = new(null, []);
    private string? error;
    private readonly bool unreadable;

    public ScheduleJournal(string directory, bool live)
    {
        path = Path.Combine(directory, live ? "schedule-live.json" : "schedule-simulation.json");
        if (!File.Exists(path)) return;
        try
        {
            state = JsonSerializer.Deserialize<ScheduleState>(File.ReadAllBytes(path), JsonSerializerOptions.Web)
                ?? throw new JsonException("空的定时记录。");
            if (state.Attempts is null || state.Attempts.Any(attempt => attempt is null)) throw new JsonException("定时记录缺少任务。");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException)
        {
            error = "定时记录无法读取，定时触发已暂停。";
            state = new(null, []);
            unreadable = true;
        }
    }

    public long? Minute => Volatile.Read(ref state).Minute;
    public long? Revision => Volatile.Read(ref state).Revision;
    public bool CanRun => !unreadable;
    public ScheduleStatus Status => new(Volatile.Read(ref error), Volatile.Read(ref state).Attempts.OrderByDescending(attempt => attempt.Due).Take(20).ToArray());
    public ScheduleAttempt? Find(string id, DateOnly day) => state.Attempts.FirstOrDefault(attempt => attempt.Id == id && attempt.Day == day);

    public void Record(ScheduleAttempt attempt)
    {
        var remaining = state.Attempts.Where(item => item.Id != attempt.Id || item.Day != attempt.Day);
        Save(state with { Attempts = [.. remaining, attempt] });
    }

    public void Checkpoint(long minute, DateOnly today, long revision) => Save(new(minute,
        state.Attempts.Where(attempt => attempt.Day >= today.AddDays(-14)).ToArray(), revision));

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
