using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Service for interacting with Azure Firewall via REST API
/// Uses IHttpClientFactory pattern for HTTP connections
/// </summary>
public class AzureFirewallService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AzureFirewallService> _logger;

    public AzureFirewallService(
        IHttpClientFactory httpClientFactory,
        ILogger<AzureFirewallService> logger
    )
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets an Azure access token using OAuth2 client credentials flow
    /// </summary>
    /// <param name="tenantId">Azure AD tenant ID</param>
    /// <param name="clientId">Application (client) ID</param>
    /// <param name="clientSecret">Client secret (password)</param>
    /// <returns>Access token or null if failed</returns>
    public async Task<AzureTokenResponse?> GetAccessTokenAsync(
        string tenantId,
        string clientId,
        string clientSecret
    )
    {
        var tokenEndpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";

        _logger.LogDebug(
            "Requesting access token for client {ClientId} from tenant {TenantId}",
            clientId,
            tenantId
        );

        try
        {
            using var httpClient = _httpClientFactory.CreateClient("AzureAuth");
            httpClient.Timeout = TimeSpan.FromSeconds(30);

            var requestBody = new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["scope"] = "https://management.azure.com/.default",
                }
            );

            var response = await httpClient.PostAsync(tokenEndpoint, requestBody);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Failed to get access token. Status: {StatusCode}, Error: {Error}",
                    response.StatusCode,
                    errorContent
                );
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            var tokenResponse = JsonSerializer.Deserialize(
                content,
                AzureJsonContext.Default.AzureTokenResponse
            );

            _logger.LogInformation(
                "Successfully obtained access token for client {ClientId}",
                clientId
            );
            return tokenResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting access token for client {ClientId}", clientId);
            return null;
        }
    }

    /// <summary>
    /// Lists all Azure Firewalls in a subscription
    /// </summary>
    /// <param name="subscriptionId">Azure subscription ID</param>
    /// <param name="accessToken">Bearer access token</param>
    /// <param name="resourceGroup">Optional resource group to filter by</param>
    /// <returns>List of firewalls or null if failed</returns>
    public async Task<AzureFirewallListResponse?> ListFirewallsAsync(
        string subscriptionId,
        string accessToken,
        string resourceGroup,
        string serverName
    )
    {
        string apiUrl =
            $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Sql/servers/{serverName}/firewallRules?api-version=2023-08-01";

        _logger.LogDebug("Listing firewalls from {ApiUrl}", apiUrl);

        try
        {
            using var httpClient = _httpClientFactory.CreateClient("AzureManagement");
            httpClient.Timeout = TimeSpan.FromSeconds(60);
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                accessToken
            );

            var response = await httpClient.GetAsync(apiUrl);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Failed to list firewalls. Status: {StatusCode}, Error: {Error}",
                    response.StatusCode,
                    errorContent
                );
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            var firewallsResponse = JsonSerializer.Deserialize(
                content,
                AzureJsonContext.Default.AzureFirewallListResponse
            );

            _logger.LogInformation(
                "Successfully retrieved {Count} firewalls from subscription {SubscriptionId}",
                firewallsResponse?.Value?.Count ?? 0,
                subscriptionId
            );

            return firewallsResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error listing firewalls for subscription {SubscriptionId}",
                subscriptionId
            );
            return null;
        }
    }

    /// <summary>
    /// Convenience method to list firewalls using configuration
    /// </summary>
    public async Task<AzureFirewallListResponse?> ListFirewallsAsync(
        AzureFirewallConfiguration config
    )
    {
        if (string.IsNullOrEmpty(config.tenant))
        {
            _logger.LogError(
                "Subscription ID is required for listing firewalls. Configuration: {Name}",
                config.name
            );
            return null;
        }

        var tokenResponse = await GetAccessTokenAsync(config.tenant, config.appId, config.password);

        if (tokenResponse == null)
        {
            _logger.LogError("Failed to obtain access token for configuration {Name}", config.name);
            return null;
        }

        return await ListFirewallsAsync(
            config.subscriptionId,
            tokenResponse.AccessToken,
            config.resourceGroup,
            config.serverName
        );
    }
}

// ============ Response DTOs ============

public class AzureTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
}

public class AzureFirewallListResponse
{
    [JsonPropertyName("value")]
    public List<AzureFirewall>? Value { get; set; }

    [JsonPropertyName("nextLink")]
    public string? NextLink { get; set; }
}

public class AzureFirewall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("location")]
    public string Location { get; set; } = "";

    [JsonPropertyName("properties")]
    public AzureFirewallProperties? Properties { get; set; }
}

public class AzureFirewallProperties
{
    [JsonPropertyName("startIpAddress")]
    public string? startIpAddress { get; set; }

    [JsonPropertyName("endIpAddress")]
    public string? endIpAddress { get; set; }
}

// ============ JSON Serialization Context ============

[JsonSerializable(typeof(AzureTokenResponse))]
[JsonSerializable(typeof(AzureFirewallListResponse))]
[JsonSerializable(typeof(AzureFirewall))]
[JsonSerializable(typeof(List<AzureFirewall>))]
internal partial class AzureJsonContext : JsonSerializerContext { }
