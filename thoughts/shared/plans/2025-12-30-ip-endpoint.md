# IP Endpoint Implementation Plan

## Overview

Add a new `/ip` endpoint to the Azure Firewall Updater service that returns the **public/external IP address** - the IP that other services would see when this service makes outbound calls to them. The service will be deployed in Kubernetes.

## Current State Analysis

The service currently:
- Uses ASP.NET Core Minimal APIs with .NET 10.0
- Has simple `/todos` endpoints as examples
- Uses SlimBuilder for AOT compilation
- Has no complex routing or middleware configuration
- Contains no IP detection functionality
- Will be deployed in Kubernetes

### Key Discoveries:
- Main application logic in `/Users/tpetrina/dev/azure-firewall-updater/firewall-updater/Program.cs:4-40`
- Minimal API pattern with route groups at `Program.cs:30`
- JSON serialization context for AOT at `Program.cs:44-47`
- No HttpClient configuration yet

## Desired End State

A functioning `/ip` endpoint that:
- Returns the service's **public/external IP address** (what other services see)
- Works correctly in Kubernetes with egress NAT
- Includes caching to avoid excessive external calls
- Follows the existing minimal API patterns
- Maintains AOT compatibility
- Has proper error handling for network failures

### Verification:
- GET request to `/ip` returns JSON with public IP information
- Endpoint appears in OpenAPI documentation (development mode)
- Works in Kubernetes returning the cluster's egress IP
- Response is cached appropriately to minimize external service calls
- No compilation or AOT warnings

## What We're NOT Doing

- Not implementing authentication/authorization for the endpoint
- Not detecting client IPs or local pod IPs
- Not handling proxy headers (not needed for public IP detection)
- Not adding complex middleware
- Not adding unit tests (no test project exists)
- Not implementing multiple fallback IP detection services (single reliable source is sufficient)

## Implementation Approach

Use an external IP detection service (ipify.org) to determine the public IP that other services would see. This is the standard approach for Kubernetes environments where the pod's internal IP differs from the egress NAT IP. Implement caching to minimize external calls and reduce latency.

## Phase 1: Basic Public IP Endpoint with HttpClient

### Overview
Add the `/ip` endpoint that calls an external service (ipify.org) to detect the public IP address that other services would see when this service makes outbound calls.

### Changes Required:

#### 1. Register HttpClient in the DI container
**File**: `/Users/tpetrina/dev/azure-firewall-updater/firewall-updater/Program.cs`
**Changes**: Add HttpClient registration after line 9

After line 9, add:

```csharp
builder.Services.AddHttpClient();
```

#### 2. Add IP endpoint with async call to ipify.org
**File**: `/Users/tpetrina/dev/azure-firewall-updater/firewall-updater/Program.cs`
**Changes**: Add new endpoint after the todos API group

After line 38, add:

```csharp
// Public IP information endpoint
app.MapGet("/ip", async (IHttpClientFactory httpClientFactory) =>
{
    try
    {
        using var httpClient = httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var publicIp = await httpClient.GetStringAsync("https://api.ipify.org");
        return Results.Ok(new IpInfo(publicIp.Trim()));
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Failed to retrieve public IP address");
        return Results.Problem("Failed to retrieve public IP address", statusCode: 503);
    }
})
.WithName("GetPublicIp")
.WithSummary("Get the service's public IP address")
.WithDescription("Returns the public/external IP address that other services see when this service makes outbound calls");
```

#### 3. Add IpInfo record type
**File**: `/Users/tpetrina/dev/azure-firewall-updater/firewall-updater/Program.cs`
**Changes**: Add record definition after the Todo record

After line 42, add:

```csharp
public record IpInfo(string IpAddress);
```

#### 4. Update JSON serialization context for AOT
**File**: `/Users/tpetrina/dev/azure-firewall-updater/firewall-updater/Program.cs`
**Changes**: Add IpInfo to the serialization context

Update line 44 to include IpInfo:

```csharp
[JsonSerializable(typeof(Todo[]))]
[JsonSerializable(typeof(IpInfo))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
```

### Success Criteria:

#### Automated Verification:
- [ ] Application builds successfully: `dotnet build`
- [ ] No AOT warnings: `dotnet publish -c Release`
- [ ] Application starts without errors: `dotnet run`

#### Manual Verification:
- [ ] GET request to `http://localhost:5298/ip` returns JSON with public IP
- [ ] Response format is correct: `{"ipAddress": "x.x.x.x"}`
- [ ] OpenAPI documentation shows the new endpoint at `http://localhost:5298/openapi/v1.json`
- [ ] Returns 503 error when ipify.org is unreachable

**Implementation Note**: After completing this phase and all automated verification passes, pause here for manual confirmation from the human that the manual testing was successful before proceeding to the next phase.

---

## Phase 2: Add In-Memory Caching

### Overview
Add caching to minimize calls to the external IP service, reducing latency and avoiding rate limits. Cache the public IP for 5 minutes as it's unlikely to change frequently in a Kubernetes environment.

### Changes Required:

#### 1. Register MemoryCache in DI container
**File**: `/Users/tpetrina/dev/azure-firewall-updater/firewall-updater/Program.cs`
**Changes**: Add MemoryCache registration after HttpClient

After the `AddHttpClient()` line, add:

```csharp
builder.Services.AddMemoryCache();
```

#### 2. Create PublicIpService class
**File**: `/Users/tpetrina/dev/azure-firewall-updater/firewall-updater/Program.cs`
**Changes**: Add a service class to handle IP detection with caching

Before the IpInfo record definition, add:

```csharp
class PublicIpService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PublicIpService> _logger;
    private const string CacheKey = "public_ip";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public PublicIpService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<PublicIpService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<string?> GetPublicIpAsync()
    {
        // Try to get from cache first
        if (_cache.TryGetValue<string>(CacheKey, out var cachedIp))
        {
            _logger.LogDebug("Returning cached public IP: {IpAddress}", cachedIp);
            return cachedIp;
        }

        // Fetch from external service
        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(5);
            var publicIp = await httpClient.GetStringAsync("https://api.ipify.org");
            var trimmedIp = publicIp.Trim();

            // Cache the result
            _cache.Set(CacheKey, trimmedIp, CacheDuration);
            _logger.LogInformation("Retrieved and cached public IP: {IpAddress}", trimmedIp);

            return trimmedIp;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve public IP address");
            return null;
        }
    }
}
```

#### 3. Register PublicIpService in DI container
**File**: `/Users/tpetrina/dev/azure-firewall-updater/firewall-updater/Program.cs`
**Changes**: Register the service

After the `AddMemoryCache()` line, add:

```csharp
builder.Services.AddSingleton<PublicIpService>();
```

#### 4. Update /ip endpoint to use PublicIpService
**File**: `/Users/tpetrina/dev/azure-firewall-updater/firewall-updater/Program.cs`
**Changes**: Simplify endpoint to use the service

Replace the /ip endpoint with:

```csharp
// Public IP information endpoint
app.MapGet("/ip", async (PublicIpService ipService) =>
{
    var publicIp = await ipService.GetPublicIpAsync();

    if (publicIp == null)
    {
        return Results.Problem("Failed to retrieve public IP address", statusCode: 503);
    }

    return Results.Ok(new IpInfo(publicIp));
})
.WithName("GetPublicIp")
.WithSummary("Get the service's public IP address")
.WithDescription("Returns the public/external IP address that other services see when this service makes outbound calls. Cached for 5 minutes.");
```

### Success Criteria:

#### Automated Verification:
- [ ] Application builds successfully: `dotnet build`
- [ ] No compilation errors or warnings: `dotnet build -warnaserror`
- [ ] AOT publish succeeds: `dotnet publish -c Release`

#### Manual Verification:
- [ ] First call to `/ip` fetches from ipify.org (check logs)
- [ ] Subsequent calls within 5 minutes return cached value (check logs)
- [ ] Response time is faster for cached requests
- [ ] After 5 minutes, a new request fetches fresh data

---

## Phase 3: Add HTTP Test File Entry

### Overview
Add an example request to the HTTP test file for easy testing of the new endpoint.

### Changes Required:

#### 1. Add IP endpoint test
**File**: `/Users/tpetrina/dev/azure-firewall-updater/firewall-updater/firewall-updater.http`
**Changes**: Add test request for the new endpoint

After line 9, add:

```http
###

# Get service IP address
GET {{firewall_updater_HostAddress}}/ip/
Accept: application/json
```

### Success Criteria:

#### Automated Verification:
- [ ] File is valid HTTP format (no tool for validation, manual check)

#### Manual Verification:
- [ ] HTTP test file can be used in VS Code REST Client or similar tool
- [ ] Request returns expected JSON response

---

## Testing Strategy

### Manual Testing Steps:
1. Run the application: `dotnet run --project firewall-updater`
2. Test with curl: `curl http://localhost:5298/ip`
3. Verify JSON response contains `ipAddress` field with your public IP
4. Make multiple requests within 5 minutes and check logs to verify caching
5. Check OpenAPI docs at `http://localhost:5298/openapi/v1.json`
6. Test using the HTTP file with REST Client extension

### Edge Cases to Test:
- Network timeout (simulate by blocking https://api.ipify.org) - should return 503
- Cache expiration after 5 minutes - should fetch fresh IP
- Kubernetes deployment - should return cluster egress IP
- Multiple simultaneous requests - caching should prevent duplicate external calls

## Performance Considerations

- **Caching**: First request takes ~100-500ms (external call), subsequent requests are <1ms (cached)
- **Cache Duration**: 5 minutes is appropriate for Kubernetes where egress IP is stable
- **Timeout**: 5 second timeout prevents hanging requests if ipify.org is slow
- **Singleton Service**: Using singleton lifecycle for PublicIpService ensures cache is shared across all requests
- **Memory**: Minimal - only one IP string cached
- **Suitable for health checks**: Yes, with caching it's fast enough for frequent health checks

## Migration Notes

Not applicable - this is a new feature with no existing functionality to migrate.

## Deployment Considerations for Kubernetes

### Expected Behavior:
- In Kubernetes, the endpoint will return the **cluster's egress NAT IP**
- This is the IP that external services (outside the cluster) will see
- Pod-to-pod communication within the cluster uses internal IPs, but this endpoint shows the external-facing IP

### Configuration:
- No special Kubernetes configuration needed
- The service makes an outbound HTTPS call to ipify.org
- Ensure cluster allows outbound HTTPS (port 443) traffic
- If using egress restrictions, whitelist `api.ipify.org` (52.2.155.113, 34.197.236.32, or configure by domain)

### Alternative IP Services (if ipify.org is blocked):
- `https://checkip.amazonaws.com/`
- `https://icanhazip.com/`
- `https://ifconfig.me/ip`

## Future Enhancements (Out of Scope)

If needed in the future, the endpoint could be extended to:
1. Support multiple IP detection services with fallback
2. Add IPv6 support explicitly
3. Return additional metadata (timestamp, cache status, source)
4. Add authentication if exposing publicly
5. Implement background refresh to avoid cold cache hits
6. Add Prometheus metrics for cache hit/miss rates

## References

- Original request: Create an /ip endpoint that returns current ip for the service (public IP for Kubernetes deployment)
- ASP.NET Core Minimal APIs: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis
- ipify API documentation: https://www.ipify.org/
- Similar pattern in codebase: `/Users/tpetrina/dev/azure-firewall-updater/firewall-updater/Program.cs:31-38`