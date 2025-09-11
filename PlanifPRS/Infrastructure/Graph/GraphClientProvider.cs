using Azure.Identity;
using Microsoft.Graph;

namespace PlanifPRS.Infrastructure.Graph;

public interface IGraphClientProvider
{
    GraphServiceClient GetClient();
}

public class GraphClientProvider : IGraphClientProvider
{
    private readonly GraphOptions _options;
    private GraphServiceClient? _cached;

    public GraphClientProvider(Microsoft.Extensions.Options.IOptions<GraphOptions> options)
    {
        _options = options.Value;
    }

    public GraphServiceClient GetClient()
    {
        if (_cached != null) return _cached;

        if (string.IsNullOrWhiteSpace(_options.TenantId)
            || string.IsNullOrWhiteSpace(_options.ClientId)
            || string.IsNullOrWhiteSpace(_options.ClientSecret))
        {
            throw new InvalidOperationException("Configuration MicrosoftGraph incomplète (TenantId/ClientId/ClientSecret).");
        }

        var credential = new ClientSecretCredential(
            _options.TenantId,
            _options.ClientId,
            _options.ClientSecret
        );

        var scopes = new[] { "https://graph.microsoft.com/.default" };
        _cached = new GraphServiceClient(credential, scopes);
        return _cached;
    }
}