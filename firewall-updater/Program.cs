using System.Text.Json.Serialization;
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

app.Run();

public record IpInfo(string IpAddress);

[JsonSerializable(typeof(IpInfo))]
internal partial class AppJsonSerializerContext : JsonSerializerContext { }
