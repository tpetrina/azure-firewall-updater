using Microsoft.Extensions.Caching.Memory;

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
        ILogger<PublicIpService> logger
    )
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
