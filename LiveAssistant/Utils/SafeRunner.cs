using LiveAssistant.Services;

namespace LiveAssistant.Utils;

public static class SafeRunner
{
    public static async Task RunLoopAsync(
        string name,
        LogService log,
        Func<CancellationToken, Task> action,
        CancellationToken ct,
        TimeSpan? delayOnError = null)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await action(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.Error(name, $"循环异常: {ex.Message}", ex);
                if (delayOnError.HasValue)
                {
                    try
                    {
                        await Task.Delay(delayOnError.Value, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
    }

    public static async Task<T?> GuardAsync<T>(
        string service,
        string operation,
        LogService log,
        Func<Task<T>> action,
        Action<LogService>? logWriter = null)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            logWriter?.Invoke(log);
            log.Error(service, $"{operation} 失败: {ex.Message}", ex);
            return default;
        }
    }

    public static async Task GuardAsync(
        string service,
        string operation,
        LogService log,
        Func<Task> action,
        Action<LogService>? logWriter = null)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logWriter?.Invoke(log);
            log.Error(service, $"{operation} 失败: {ex.Message}", ex);
        }
    }
}
