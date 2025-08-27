using Microsoft.EntityFrameworkCore;
using PlanifPRS.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using PlanifPRS.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages(options =>
{
    // Configuration des conventions de routage
    options.Conventions.AllowAnonymousToPage("/AccessDenied");

    // Route explicite pour la page Edit, nécessaire si vous avez l'erreur 404
    options.Conventions.AddPageRoute("/Prs/Edit", "/Edit/{id:int?}");
});

builder.Services.AddAuthentication(Microsoft.AspNetCore.Server.IISIntegration.IISDefaults.AuthenticationScheme);

// Configuration de l'autorisation
builder.Services.AddAuthorization(options =>
{
    // Politique qui permet à tous les utilisateurs authentifiés d'accéder aux pages
    options.AddPolicy("PrsAccessPolicy", policy =>
        policy.RequireAuthenticatedUser());
});

// Configuration pour gérer les accès refusés
builder.Services.ConfigureApplicationCookie(options =>
{
    options.AccessDeniedPath = "/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
});

// Ajout des services
builder.Services.AddScoped<FileService>();
builder.Services.AddScoped<LienDossierPrsService>();
builder.Services.AddScoped<ChecklistService>(); // Nouveau service pour les checklists
builder.Services.AddScoped<NotificationService>(); // Service de notifications (invitations + mails)

// Configuration du téléchargement de fichiers volumineux
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 104857600; // 100 MB
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
    options.BufferBodyLengthLimit = int.MaxValue;
});

// Configuration de Kestrel pour permettre les uploads volumineux
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 104857600; // 100 MB
});

builder.Services.AddDbContext<PlanifPrsDbContext>(options =>
    options.UseSqlServer("Server=MSLTest20\\test;Database=PlanifPRS;User Id=ssis;Password=ssis;TrustServerCertificate=True;Encrypt=True;"));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

// Middleware de journalisation pour le débogage
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var username = context.User?.Identity?.Name ?? "Non authentifié";
    var isAuthenticated = context.User?.Identity?.IsAuthenticated ?? false;

    Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Requête: {path} | Utilisateur: {username} | Authentifié: {isAuthenticated}");

    await next();
});

app.UseAuthentication();
app.UseAuthorization();

// Middleware personnalisé pour gérer les erreurs 404/403
app.Use(async (context, next) =>
{
    var originalPath = context.Request.Path.Value;

    await next();

    var statusCode = context.Response.StatusCode;

    // Si on a un code 404 et que c'est une tentative d'accès à la page Edit
    if (statusCode == 404 &&
        (originalPath?.Contains("/Edit/") == true || originalPath?.StartsWith("/Prs/Edit/") == true))
    {
        // Extraire l'ID de l'URL
        var pathParts = originalPath.Split('/');
        string id = null;

        for (int i = 0; i < pathParts.Length; i++)
        {
            if (pathParts[i].Equals("Edit", StringComparison.OrdinalIgnoreCase) && i + 1 < pathParts.Length)
            {
                id = pathParts[i + 1];
                break;
            }
        }

        // Si on a trouvé un ID, rediriger vers la bonne URL
        if (!string.IsNullOrEmpty(id) && int.TryParse(id, out _))
        {
            Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Redirection de {originalPath} vers /Prs/Edit/{id}");
            context.Response.Redirect($"/Prs/Edit/{id}");
            return;
        }
    }

    // Pour les autres erreurs 404/403, rediriger vers AccessDenied
    if ((statusCode == 404 || statusCode == 403) && !originalPath.Contains("/AccessDenied"))
    {
        Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Redirection vers AccessDenied pour: {originalPath} (code: {statusCode})");
        context.Response.Redirect($"/AccessDenied?code={statusCode}");
    }
});

// Enregistrement des endpoints
app.MapControllers();
app.MapRazorPages();

app.Run();