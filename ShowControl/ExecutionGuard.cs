namespace ShowControl;

internal sealed record StaleRequest(int Status, string Message);

internal static class ExecutionGuard
{
    public static StaleRequest? Check(long current, long? requested) => requested switch
    {
        null => new(400, "缺少工程版本，请刷新页面后重试。"),
        var value when value != current => new(412, "工程已在其他窗口更新。请刷新页面，核对后重新确认执行。"),
        _ => null
    };
}
