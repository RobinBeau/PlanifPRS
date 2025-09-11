using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Features;
using PlanifPRS.Data;
using PlanifPRS.Services;

// Absences / Microsoft Graph
using PlanifPRS.Infrastructure.Graph;
using PlanifPRS.Infrastructure.Absences;

var builder = WebApplication.CreateBuilder(args);

// ================================================================
// Razor Pages + Routes existantes
// ================================================================
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AllowAnonymousToPage("/AccessDenied");
    options.Conventions.AddPageRoute("/Prs/Edit", "/Edit/{id:int?}");
});

// ================================================================
// Authentification / Autorisation (Windows)
// ================================================================
builder.Services.AddAuthentication(Microsoft.AspNetCore.Server.IISIntegration.IISDefaults.AuthenticationScheme);
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("PrsAccessPolicy", policy => policy.RequireAuthenticatedUser());
});

// (Si un cookie d’auth personnalisé était utilisé, à conserver – ici Windows auth)
builder.Services.ConfigureApplicationCookie(options =>
{
    options.AccessDeniedPath = "/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
});

// ================================================================
// Services applicatifs existants
// ================================================================
builder.Services.AddScoped<FileService>();
builder.Services.AddScoped<LienDossierPrsService>();
builder.Services.AddScoped<ChecklistService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<ExportCalendarService>();

// ================================================================
// Form upload / Kestrel limites
// ================================================================
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 104_857_600; // 100 MB
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
    options.BufferBodyLengthLimit = int.MaxValue;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 104_857_600; // 100 MB
});

// ================================================================
// DbContext
// ================================================================
builder.Services.AddDbContext<PlanifPrsDbContext>(options =>
{
    var cs = builder.Configuration.GetConnectionString("PlanifPRSConnection")
             ?? "Server=MSLTest20\\test;Database=PlanifPRS;User Id=ssis;Password=ssis;TrustServerCertificate=True;Encrypt=True;";
    options.UseSqlServer(cs);
});

// ================================================================
// Controllers (pour endpoints API absences)
// ================================================================
builder.Services.AddControllers();
// Swagger (optionnel)
// builder.Services.AddEndpointsApiExplorer();
// builder.Services.AddSwaggerGen();

// ================================================================
// SYNCHRO ABSENCES MICROSOFT GRAPH
// ================================================================

// 1. Options de configuration
builder.Services.Configure<GraphOptions>(builder.Configuration.GetSection("MicrosoftGraph"));
builder.Services.Configure<AbsenceSyncOptions>(builder.Configuration.GetSection("AbsenceSync"));

// 2. Services Graph & Absence
builder.Services.AddSingleton<IGraphClientProvider, GraphClientProvider>();
builder.Services.AddScoped<IAbsenceService, AbsenceService>();

// 3. Stockage snapshots / état (fichiers => singleton OK)
builder.Services.AddSingleton<IAbsenceRepository, JsonAbsenceRepository>();
builder.Services.AddSingleton<IAbsenceSyncStateStore, FileAbsenceSyncStateStore>();

// 4. Provider emails utilisateurs
// IMPORTANT: Scoped (utilise probablement le DbContext)
builder.Services.AddScoped<IUserEmailProvider, SqlUserEmailProvider>();

// 5. Exécuteur de synchro
// Reste en singleton mais NE DOIT PAS injecter de services scoped directement.
// (AbsenceSyncExecutor utilise IServiceScopeFactory pour créer un scope à l'exécution)
builder.Services.AddSingleton<IAbsenceSyncExecutor, AbsenceSyncExecutor>();

// ================================================================
// Build
// ================================================================
var app = builder.Build();

// ================================================================
// Pipeline
// ================================================================
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
else
{
    // Swagger si activé plus haut
    // app.UseSwagger();
    // app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

// Log simple des requêtes (existant)
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var username = context.User?.Identity?.Name ?? "Non authentifié";
    var isAuthenticated = context.User?.Identity?.IsAuthenticated ?? false;

    Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Requête: {path} | Utilisateur: {username} | Authentifié: {isAuthenticated}");

    await next();
});

// Protection endpoint POST /api/absences/sync-now par clé API
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api/absences/sync-now"))
    {
        var expected = app.Configuration["AbsenceSync:ApiKey"];
        if (!string.IsNullOrEmpty(expected))
        {
            var provided = ctx.Request.Headers["X-API-KEY"].FirstOrDefault();
            if (provided != expected)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsync("Unauthorized");
                return;
            }
        }
    }
    await next();
});

// Middleware Lazy Sync (déclenche la synchro au 1er accès après l'heure prévue)
app.UseMiddleware<LazySyncMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

// Middleware gestion 404/403 (éviter redirection sur API)
app.Use(async (context, next) =>
{
    var originalPath = context.Request.Path.Value ?? "";
    await next();
    var statusCode = context.Response.StatusCode;
    bool isApi = originalPath.StartsWith("/api", StringComparison.OrdinalIgnoreCase);

    if (!isApi)
    {
        // Redirection spéciale /Prs/Edit/{id}
        if (statusCode == 404 &&
            (originalPath.Contains("/Edit/") || originalPath.StartsWith("/Prs/Edit/")))
        {
            var parts = originalPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string? id = null;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Equals("Edit", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
                {
                    id = parts[i + 1];
                    break;
                }
            }
            if (!string.IsNullOrEmpty(id) && int.TryParse(id, out _))
            {
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Redirection de {originalPath} vers /Prs/Edit/{id}");
                context.Response.Redirect($"/Prs/Edit/{id}");
                return;
            }
        }

        if ((statusCode == 404 || statusCode == 403) && !originalPath.Contains("/AccessDenied"))
        {
            Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Redirection vers /AccessDenied (code={statusCode}) pour {originalPath}");
            context.Response.Redirect($"/AccessDenied?code={statusCode}");
        }
    }
});

// Endpoints
app.MapControllers();
app.MapRazorPages();

app.Run();