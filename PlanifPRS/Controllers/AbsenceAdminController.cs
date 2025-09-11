using Microsoft.AspNetCore.Mvc;
using PlanifPRS.Infrastructure.Absences;

namespace PlanifPRS.Controllers;

[ApiController]
[Route("api/absences")]
public class AbsenceAdminController : ControllerBase
{
    private readonly IAbsenceSyncExecutor _executor;

    public AbsenceAdminController(IAbsenceSyncExecutor executor)
    {
        _executor = executor;
    }

    [HttpPost("sync-now")]
    public async Task<IActionResult> SyncNow(CancellationToken ct)
    {
        var launched = await _executor.RunDailySyncIfNeededAsync(true, ct);
        return Ok(new { forced = true, launched });
    }
}