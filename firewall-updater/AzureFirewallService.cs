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

    /// <summary>
    /// Creates a firewall rule with the specified name and IP range.
    /// If a rule with the given name already exists, retries with incremented suffix (e.g., "Name - 1", "Name - 2").
    /// </summary>
    /// <param name="config">Azure firewall configuration</param>
    /// <param name="ruleName">Base name for the firewall rule</param>
    /// <param name="startIpAddress">Start IP address of the range</param>
    /// <param name="endIpAddress">End IP address of the range</param>
    /// <param name="maxRetries">Maximum number of retry attempts with incremented names (default: 100)</param>
    /// <returns>The created firewall rule or null if failed</returns>
    public async Task<AzureFirewall?> CreateFirewallRuleAsync(
        AzureFirewallConfiguration config,
        string ruleName,
        string startIpAddress,
        string endIpAddress,
        int maxRetries = 100
    )
    {
        if (string.IsNullOrEmpty(config.tenant))
        {
            _logger.LogError(
                "Tenant ID is required for creating firewall rules. Configuration: {Name}",
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

        return await CreateFirewallRuleAsync(
            config.subscriptionId,
            tokenResponse.AccessToken,
            config.resourceGroup,
            config.serverName,
            ruleName,
            startIpAddress,
            endIpAddress,
            maxRetries
        );
    }

    /// <summary>
    /// Creates a firewall rule with the specified name and IP range.
    /// If a rule with the given name already exists, retries with incremented suffix (e.g., "Name - 1", "Name - 2").
    /// </summary>
    /// <param name="subscriptionId">Azure subscription ID</param>
    /// <param name="accessToken">Bearer access token</param>
    /// <param name="resourceGroup">Resource group name</param>
    /// <param name="serverName">SQL server name</param>
    /// <param name="ruleName">Base name for the firewall rule</param>
    /// <param name="startIpAddress">Start IP address of the range</param>
    /// <param name="endIpAddress">End IP address of the range</param>
    /// <param name="maxRetries">Maximum number of retry attempts with incremented names (default: 100)</param>
    /// <returns>The created firewall rule or null if failed</returns>
    public async Task<AzureFirewall?> CreateFirewallRuleAsync(
        string subscriptionId,
        string accessToken,
        string resourceGroup,
        string serverName,
        string ruleName,
        string startIpAddress,
        string endIpAddress,
        int maxRetries = 100
    )
    {
        var currentName = ruleName;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (attempt > 0)
            {
                currentName = $"{ruleName} - {attempt}";
            }

            var result = await TryCreateFirewallRuleAsync(
                subscriptionId,
                accessToken,
                resourceGroup,
                serverName,
                currentName,
                startIpAddress,
                endIpAddress
            );

            if (result.Success)
            {
                return result.Firewall;
            }

            if (!result.NameConflict)
            {
                _logger.LogError(
                    "Failed to create firewall rule '{RuleName}' due to non-recoverable error",
                    currentName
                );
                return null;
            }

            _logger.LogDebug(
                "Firewall rule name '{RuleName}' already exists, trying with incremented suffix",
                currentName
            );
        }

        _logger.LogError(
            "Failed to create firewall rule after {MaxRetries} attempts. Base name: '{RuleName}'",
            maxRetries,
            ruleName
        );
        return null;
    }

    private async Task<(bool Success, bool NameConflict, AzureFirewall? Firewall)> TryCreateFirewallRuleAsync(
        string subscriptionId,
        string accessToken,
        string resourceGroup,
        string serverName,
        string ruleName,
        string startIpAddress,
        string endIpAddress
    )
    {
        string apiUrl =
            $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Sql/servers/{serverName}/firewallRules/{Uri.EscapeDataString(ruleName)}?api-version=2023-08-01";

        _logger.LogDebug(
            "Creating firewall rule '{RuleName}' with IP range {StartIp} - {EndIp}",
            ruleName,
            startIpAddress,
            endIpAddress
        );

        try
        {
            using var httpClient = _httpClientFactory.CreateClient("AzureManagement");
            httpClient.Timeout = TimeSpan.FromSeconds(60);
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                accessToken
            );

            var requestBody = new CreateFirewallRuleRequest
            {
                Properties = new CreateFirewallRuleProperties
                {
                    StartIpAddress = startIpAddress,
                    EndIpAddress = endIpAddress
                }
            };

            var jsonContent = JsonSerializer.Serialize(
                requestBody,
                AzureJsonContext.Default.CreateFirewallRuleRequest
            );
            var content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.PutAsync(apiUrl, content);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                var firewall = JsonSerializer.Deserialize(
                    responseContent,
                    AzureJsonContext.Default.AzureFirewall
                );

                _logger.LogInformation(
                    "Successfully created firewall rule '{RuleName}' with IP range {StartIp} - {EndIp}",
                    ruleName,
                    startIpAddress,
                    endIpAddress
                );

                return (true, false, firewall);
            }

            var errorContent = await response.Content.ReadAsStringAsync();

            // Check if the error is due to name conflict (HTTP 409 Conflict)
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                _logger.LogDebug(
                    "Firewall rule name conflict for '{RuleName}'. Status: {StatusCode}",
                    ruleName,
                    response.StatusCode
                );
                return (false, true, null);
            }

            _logger.LogError(
                "Failed to create firewall rule '{RuleName}'. Status: {StatusCode}, Error: {Error}",
                ruleName,
                response.StatusCode,
                errorContent
            );
            return (false, false, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error creating firewall rule '{RuleName}' for server {ServerName}",
                ruleName,
                serverName
            );
            return (false, false, null);
        }
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

// ============ Request DTOs ============

public class CreateFirewallRuleRequest
{
    [JsonPropertyName("properties")]
    public CreateFirewallRuleProperties Properties { get; set; } = new();
}

public class CreateFirewallRuleProperties
{
    [JsonPropertyName("startIpAddress")]
    public string StartIpAddress { get; set; } = "";

    [JsonPropertyName("endIpAddress")]
    public string EndIpAddress { get; set; } = "";
}

// ============ JSON Serialization Context ============

[JsonSerializable(typeof(AzureTokenResponse))]
[JsonSerializable(typeof(AzureFirewallListResponse))]
[JsonSerializable(typeof(AzureFirewall))]
[JsonSerializable(typeof(List<AzureFirewall>))]
[JsonSerializable(typeof(CreateFirewallRuleRequest))]
[JsonSerializable(typeof(CreateFirewallRuleProperties))]
internal partial class AzureJsonContext : JsonSerializerContext { }
