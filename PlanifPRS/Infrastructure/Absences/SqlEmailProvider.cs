using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace PlanifPRS.Infrastructure.Absences;

public class SqlUserEmailProvider : IUserEmailProvider
{
    private readonly string _connectionString;
    private readonly AbsenceSyncOptions _options;
    private readonly ILogger<SqlUserEmailProvider> _logger;

    public SqlUserEmailProvider(
        IConfiguration configuration,
        IOptions<AbsenceSyncOptions> options,
        ILogger<SqlUserEmailProvider> logger)
    {
        _connectionString = configuration.GetConnectionString("PlanifPRSConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:PlanifPRSConnection manquant.");
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> GetActiveUserEmailsAsync(CancellationToken ct)
    {
        var list = new List<string>();

        var sql = @"
SELECT Mail
FROM dbo.Utilisateurs
WHERE Mail IS NOT NULL
  AND Mail <> ''
  AND (DateDeleted IS NULL)
";

        if (!string.IsNullOrWhiteSpace(_options.ServiceFilter))
        {
            sql += " AND Service = @ServiceFilter";
        }

        sql += " ORDER BY Mail;";

        try
        {
            await using var cn = new SqlConnection(_connectionString);
            await cn.OpenAsync(ct);

            await using var cmd = cn.CreateCommand();
            cmd.CommandType = CommandType.Text;
            cmd.CommandText = sql;

            if (!string.IsNullOrWhiteSpace(_options.ServiceFilter))
            {
                var p = cmd.CreateParameter();
                p.ParameterName = "@ServiceFilter";
                p.Value = _options.ServiceFilter;
                cmd.Parameters.Add(p);
            }

            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var mail = rdr.GetString(0).Trim();
                if (!string.IsNullOrEmpty(mail))
                    list.Add(mail);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération des emails utilisateurs.");
        }

        if (list.Count == 0 && _options.Users.Count > 0)
        {
            _logger.LogWarning("Aucun mail trouvé en base, utilisation de la liste Users (fallback).");
            return _options.Users;
        }

        return list;
    }
}