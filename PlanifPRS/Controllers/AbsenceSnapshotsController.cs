using Microsoft.AspNetCore.Mvc;
using PlanifPRS.Infrastructure.Absences;

namespace PlanifPRS.Controllers;

[ApiController]
[Route("api/absences/snapshots")]
public class AbsenceSnapshotsController : ControllerBase
{
    private readonly IAbsenceRepository _repo;

    public AbsenceSnapshotsController(IAbsenceRepository repo)
    {
        _repo = repo;
    }

    [HttpGet("latest")]
    public async Task<IActionResult> GetLatest(CancellationToken ct)
    {
        var data = await _repo.GetLatestSnapshotAsync(ct);
        return Ok(data);
    }
}