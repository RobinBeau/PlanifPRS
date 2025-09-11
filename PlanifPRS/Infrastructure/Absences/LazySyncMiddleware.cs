namespace PlanifPRS.Infrastructure.Absences;

public class LazySyncMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<LazySyncMiddleware> _logger;

    public LazySyncMiddleware(RequestDelegate next, ILogger<LazySyncMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context,
        Microsoft.Extensions.Options.IOptions<AbsenceSyncOptions> opt,
        IAbsenceSyncExecutor executor)
    {
        var cfg = opt.Value;
        if (cfg.LazyTriggerEnabled)
        {
            _ = TriggerIfNeeded(executor, context.RequestAborted);
        }

        await _next(context);
    }

    private async Task TriggerIfNeeded(IAbsenceSyncExecutor executor, CancellationToken ct)
    {
        try
        {
            var launched = await executor.RunDailySyncIfNeededAsync(false, ct);
            if (launched)
            {
                _logger.LogInformation("Synchronisation quotidienne lancée (lazy).");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur déclenchement lazy sync.");
        }
    }
}