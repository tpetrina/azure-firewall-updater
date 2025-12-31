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
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<PublicIpService>();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

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
    "/firewall-rules",
    () =>
    {
        var rules = builder
            .Configuration.GetSection("FirewallRules")
            .Get<List<AzureFirewallConfiguration>>();
        return Results.Ok(rules);
    }
);

app.MapGet(
    "/firewall-rules/{name}",
    (string name) =>
    {
        var rule = builder
            .Configuration.GetSection("FirewallRules")
            .Get<List<AzureFirewallConfiguration>>()
            ?.FirstOrDefault(r => r.name == name);
        if (rule == null)
        {
            return Results.NotFound(new { message = $"Firewall rule {name} not found" });
        }
        return Results.Ok(new FirewallRule(rule.name, rule.appId, rule.tenant));
    }
);

app.Run();

public class AzureFirewallConfiguration
{
    public string name { get; set; } = "";
    public string appId { get; set; } = "";
    public string tenant { get; set; } = "";
    public string password { get; set; } = "";
}

public record FirewallRules(List<FirewallRule> rules);

public record FirewallRule(string name, string description, string tenant);

[JsonSerializable(typeof(IpInfo))]
[JsonSerializable(typeof(FirewallRule))]
[JsonSerializable(typeof(FirewallRules))]
[JsonSerializable(typeof(List<AzureFirewallConfiguration>))]
internal partial class AppJsonSerializerContext : JsonSerializerContext { }
