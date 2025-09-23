using System.Globalization;
using Microsoft.AspNetCore.Mvc;

namespace PlanifPRS.Controllers
{
    [ApiController]
    [Route("api/absences")]
    public class AbsencesController : ControllerBase
    {
        private readonly IWebHostEnvironment _env;
        public AbsencesController(IWebHostEnvironment env) => _env = env;

        [HttpGet("latest")]
        public IActionResult GetLatest()
        {
            var dir = Path.Combine(_env.ContentRootPath, "Data", "Absences");
            if (!Directory.Exists(dir)) return NotFound("Absences folder not found");

            var files = Directory.GetFiles(dir, "absences-*.json", SearchOption.TopDirectoryOnly);
            if (files.Length == 0) return NotFound("No absences file");

            string? latest = null;
            DateTime latestDate = DateTime.MinValue;
            foreach (var f in files)
            {
                var name = Path.GetFileNameWithoutExtension(f); // absences-YYYYMMDD
                var parts = name.Split('-', 2);
                if (parts.Length == 2 && DateTime.TryParseExact(parts[1], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                {
                    if (d > latestDate) { latestDate = d; latest = f; }
                }
            }
            latest ??= files.OrderByDescending(x => x).First();
            var json = System.IO.File.ReadAllText(latest);
            return Content(json, "application/json");
        }
    }
}