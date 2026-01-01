using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Serilog;

var builder = WebApplication.CreateSlimBuilder(args);

// Configure Serilog
builder.Host.UseSerilog(
    (context, services, configuration) =>
    {
        configuration.ReadFrom.Configuration(context.Configuration).ReadFrom.Services(services);
    }
);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("AzureAuth");
builder.Services.AddHttpClient("AzureManagement");
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<PublicIpService>();
builder.Services.AddSingleton<AzureFirewallService>();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Log public IP on startup
_ = Task.Run(async () =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    var ipService = app.Services.GetRequiredService<PublicIpService>();

    try
    {
        var publicIp = await ipService.GetPublicIpAsync();
        if (publicIp != null)
        {
            logger.LogInformation("Public IP address: {PublicIp}", publicIp);
        }
        else
        {
            logger.LogWarning("Failed to retrieve public IP address on startup");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error retrieving public IP address on startup");
    }
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Public IP information endpoint
app.MapGet(
        "/ip",
        async (PublicIpService ipService) =>
        {
            var publicIp = await ipService.GetPublicIpAsync();

            if (publicIp == null)
            {
                return Results.Problem("Failed to retrieve public IP address", statusCode: 503);
            }

            return Results.Ok(new IpInfo(publicIp));
        }
    )
    .WithName("GetPublicIp")
    .WithSummary("Get the service's public IP address")
    .WithDescription(
        "Returns the public/external IP address that other services see when this service makes outbound calls. Cached for 5 minutes."
    );

app.MapGet(
    "/definitions",
    () =>
    {
        var rules = builder
            .Configuration.GetSection("FirewallDefinitions")
            .Get<List<AzureFirewallConfiguration>>();
        // Return only relevant info, omitting password
        var trimmedRules = rules
            ?.Select(r => new FirewallRule(
                r.name,
                r.resourceGroup,
                r.serverName,
                r.appId,
                r.tenant,
                string.IsNullOrWhiteSpace(r.password) ? null : "********",
                r.subscriptionId
            ))
            .ToList();
        return Results.Ok(trimmedRules);
    }
);

app.MapGet(
    "/definitions/{name}",
    (string name) =>
    {
        var rule = builder
            .Configuration.GetSection("FirewallDefinitions")
            .Get<List<AzureFirewallConfiguration>>()
            ?.FirstOrDefault(r => r.name == name);
        if (rule == null)
        {
            return Results.NotFound(new { message = $"Firewall rule {name} not found" });
        }
        return Results.Ok(
            new FirewallRule(
                rule.name,
                rule.resourceGroup,
                rule.serverName,
                rule.appId,
                rule.tenant,
                string.IsNullOrWhiteSpace(rule.password) ? null : "********",
                rule.subscriptionId
            )
        );
    }
);

// List Azure Firewalls for a configuration
app.MapGet(
        "/firewall-rules/{name}",
        async (string name, string? resourceGroup, AzureFirewallService firewallService) =>
        {
            var config = builder
                .Configuration.GetSection("FirewallDefinitions")
                .Get<List<AzureFirewallConfiguration>>()
                ?.FirstOrDefault(r => r.name == name);

            if (config == null)
            {
                return Results.NotFound(new { message = $"Configuration '{name}' not found" });
            }

            if (string.IsNullOrWhiteSpace(config.password))
            {
                return Results.BadRequest(
                    new { message = $"Configuration '{name}' has no password configured" }
                );
            }

            if (string.IsNullOrWhiteSpace(config.subscriptionId))
            {
                return Results.BadRequest(
                    new { message = $"Configuration '{name}' has no subscriptionId configured" }
                );
            }

            var firewalls = await firewallService.ListFirewallsAsync(config);

            if (firewalls == null)
            {
                return Results.Problem("Failed to retrieve firewalls from Azure", statusCode: 502);
            }

            return Results.Ok(firewalls);
        }
    )
    .WithName("ListFirewalls")
    .WithSummary("List Azure Firewalls for a configuration")
    .WithDescription(
        "Lists all Azure Firewalls accessible by the specified configuration. Optionally filter by resource group."
    );

// Ensure current IP is in firewall rules
app.MapPost(
        "/firewall-rules/{name}/ensure-ip",
        async (
            string name,
            AzureFirewallService firewallService,
            PublicIpService ipService,
            ILogger<Program> logger
        ) =>
        {
            // Get the current public IP
            var publicIp = await ipService.GetPublicIpAsync();
            if (publicIp == null)
            {
                return Results.Problem("Failed to retrieve public IP address", statusCode: 503);
            }

            // Get the configuration
            var config = builder
                .Configuration.GetSection("FirewallDefinitions")
                .Get<List<AzureFirewallConfiguration>>()
                ?.FirstOrDefault(r => r.name == name);

            if (config == null)
            {
                return Results.NotFound(new { message = $"Configuration '{name}' not found" });
            }

            if (string.IsNullOrWhiteSpace(config.password))
            {
                return Results.BadRequest(
                    new { message = $"Configuration '{name}' has no password configured" }
                );
            }

            if (string.IsNullOrWhiteSpace(config.subscriptionId))
            {
                return Results.BadRequest(
                    new { message = $"Configuration '{name}' has no subscriptionId configured" }
                );
            }

            // List existing firewall rules
            var firewalls = await firewallService.ListFirewallsAsync(config);
            if (firewalls == null)
            {
                return Results.Problem("Failed to retrieve firewalls from Azure", statusCode: 502);
            }

            // Check if current IP is already in the list
            var existingRule = firewalls.Value?.FirstOrDefault(f =>
                f.Properties?.startIpAddress == publicIp && f.Properties?.endIpAddress == publicIp
            );

            if (existingRule != null)
            {
                logger.LogInformation(
                    "IP {PublicIp} already exists in firewall rule '{RuleName}'",
                    publicIp,
                    existingRule.Name
                );
                return Results.Ok(
                    new EnsureIpResponse(
                        publicIp,
                        existingRule.Name,
                        Created: false,
                        Message: $"IP {publicIp} already exists in rule '{existingRule.Name}'"
                    )
                );
            }

            // Create a new firewall rule
            var newRule = await firewallService.CreateFirewallRuleAsync(
                config,
                "Automatic IP",
                publicIp,
                publicIp
            );

            if (newRule == null)
            {
                return Results.Problem("Failed to create firewall rule", statusCode: 502);
            }

            logger.LogInformation(
                "Created new firewall rule '{RuleName}' for IP {PublicIp}",
                newRule.Name,
                publicIp
            );

            return Results.Ok(
                new EnsureIpResponse(
                    publicIp,
                    newRule.Name,
                    Created: true,
                    Message: $"Created new firewall rule '{newRule.Name}' for IP {publicIp}"
                )
            );
        }
    )
    .WithName("EnsureIpInFirewall")
    .WithSummary("Ensure current IP is in firewall rules")
    .WithDescription(
        "Checks if the service's public IP is in the firewall rules. If not, creates a new rule named 'Automatic IP' (with incrementing suffix if needed)."
    );

// Health check endpoints
app.MapGet("/health", () => Results.Ok())
    .WithName("HealthCheck")
    .WithSummary("Health check endpoint")
    .WithDescription("Returns 200 OK if the service is running.");

app.MapGet("/health/live", () => Results.Ok())
    .WithName("LivenessCheck")
    .WithSummary("Liveness probe endpoint")
    .WithDescription("Returns 200 OK if the service is alive.");

app.MapGet("/health/ready", () => Results.Ok())
    .WithName("ReadinessCheck")
    .WithSummary("Readiness probe endpoint")
    .WithDescription("Returns 200 OK if the service is ready to accept requests.");

app.Run();

public class AzureFirewallConfiguration
{
    public string name { get; set; } = "";
    public string resourceGroup { get; set; } = "";
    public string serverName { get; set; } = "";
    public string appId { get; set; } = "";
    public string tenant { get; set; } = "";
    public string password { get; set; } = "";
    public string subscriptionId { get; set; } = "";
}

public record FirewallRules(List<FirewallRule> rules);

public record FirewallRule(
    string name,
    string resourceGroup,
    string serverName,
    string description,
    string tenant,
    string? password = null,
    string subscriptionId = ""
);

public record EnsureIpResponse(string IpAddress, string RuleName, bool Created, string Message);

[JsonSerializable(typeof(IpInfo))]
[JsonSerializable(typeof(EnsureIpResponse))]
[JsonSerializable(typeof(FirewallRule))]
[JsonSerializable(typeof(FirewallRules))]
[JsonSerializable(typeof(List<AzureFirewallConfiguration>))]
[JsonSerializable(typeof(AzureFirewallListResponse))]
[JsonSerializable(typeof(AzureFirewall))]
internal partial class AppJsonSerializerContext : JsonSerializerContext { }
