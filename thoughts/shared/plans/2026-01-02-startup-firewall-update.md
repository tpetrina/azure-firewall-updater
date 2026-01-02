# Startup Firewall Update Implementation Plan

## Overview

Add logic to automatically update firewall rules for all configured definitions when the application starts. Currently, the app only logs the public IP on startup. We need to extend this to also ensure the IP is added to all configured Azure SQL Server firewall rules.

## Current State Analysis

### Startup IP Fetch (`Program.cs:32-54`)
The application already fetches and logs the public IP on startup via a fire-and-forget `Task.Run`:

```csharp
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
        // ... error handling
    }
    // ... catch
});
```

### Existing Firewall Update Logic (`Program.cs:177-280`)
The `/firewall-rules/{name}/ensure-ip` endpoint already contains complete logic to:
1. Get current public IP
2. Look up a definition by name
3. Validate configuration (password, subscriptionId)
4. List existing firewall rules via `ListFirewallsAsync()`
5. Check if IP already exists in rules
6. Create new rule via `CreateFirewallRuleAsync()` if needed

### Configuration (`appsettings.json:22-41`)
Two definitions are configured under `FirewallDefinitions`:
- "gigpinapp" - gigpin resource group
- "emguest" - hotelcms resource group

## Desired End State

On startup:
1. Fetch the public IP (already done)
2. Load all firewall definitions from configuration
3. For each definition with valid credentials:
   - List existing firewall rules
   - If current IP is not present, create a new rule
   - Log success/failure for each definition
4. Continue application startup regardless of firewall update success/failure

### Key Discoveries:
- `AzureFirewallService.ListFirewallsAsync(config)` handles auth internally (`AzureFirewallService.cs:160-187`)
- `AzureFirewallService.CreateFirewallRuleAsync(config, ruleName, startIp, endIp)` handles auth and retries (`AzureFirewallService.cs:199-234`)
- Definitions with empty passwords should be skipped (gracefully)

## What We're NOT Doing

- Not creating a new endpoint
- Not changing the existing `/firewall-rules/{name}/ensure-ip` endpoint
- Not adding complex configuration options (ntfy endpoint is hardcoded like in GHA)
- Not blocking application startup on firewall updates
- Not failing if notification delivery fails

## Implementation Approach

Extend the startup `Task.Run` block to:
1. Iterate through all definitions and ensure the IP is in each firewall
2. Track results (created, already existed, skipped, failed) for each definition
3. Send a summary notification to ntfy.sh at the end (similar pattern to GHA workflow)

## Phase 1: Add Startup Firewall Update Logic

### Overview
Modify the startup `Task.Run` block to iterate through all definitions and ensure the IP is in each firewall.

### Changes Required:

#### 1. Modify Startup Block
**File**: `firewall-updater/Program.cs`
**Location**: Lines 32-54 (replace existing `Task.Run` block)

**Changes**: Extend the startup task to:
1. Get all definitions from configuration
2. For each valid definition, ensure IP is in firewall
3. Track results for each definition
4. Send summary notification to ntfy.sh

```csharp
// Log public IP on startup, ensure IP is in all configured firewalls, and send notification
_ = Task.Run(async () =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    var ipService = app.Services.GetRequiredService<PublicIpService>();
    var firewallService = app.Services.GetRequiredService<AzureFirewallService>();
    var httpClientFactory = app.Services.GetRequiredService<IHttpClientFactory>();

    // Track results for notification
    var created = new List<string>();
    var alreadyExisted = new List<string>();
    var skipped = new List<string>();
    var failed = new List<string>();
    string? publicIp = null;

    try
    {
        publicIp = await ipService.GetPublicIpAsync();
        if (publicIp == null)
        {
            logger.LogWarning("Failed to retrieve public IP address on startup");
            failed.Add("IP fetch failed");
        }
        else
        {
            logger.LogInformation("Public IP address: {PublicIp}", publicIp);

            // Get all firewall definitions
            var definitions = builder.Configuration
                .GetSection("FirewallDefinitions")
                .Get<List<AzureFirewallConfiguration>>();

            if (definitions == null || definitions.Count == 0)
            {
                logger.LogInformation("No firewall definitions configured, skipping firewall updates");
                skipped.Add("No definitions configured");
            }
            else
            {
                logger.LogInformation("Updating firewall rules for {Count} definitions", definitions.Count);

                foreach (var config in definitions)
                {
                    try
                    {
                        // Skip definitions without required credentials
                        if (string.IsNullOrWhiteSpace(config.password))
                        {
                            logger.LogWarning(
                                "Skipping definition '{Name}': no password configured",
                                config.name
                            );
                            skipped.Add($"{config.name} (no password)");
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(config.subscriptionId))
                        {
                            logger.LogWarning(
                                "Skipping definition '{Name}': no subscriptionId configured",
                                config.name
                            );
                            skipped.Add($"{config.name} (no subscriptionId)");
                            continue;
                        }

                        // List existing firewall rules
                        var firewalls = await firewallService.ListFirewallsAsync(config);
                        if (firewalls == null)
                        {
                            logger.LogError(
                                "Failed to list firewalls for definition '{Name}'",
                                config.name
                            );
                            failed.Add($"{config.name} (list failed)");
                            continue;
                        }

                        // Check if current IP is already in the list
                        var existingRule = firewalls.Value?.FirstOrDefault(f =>
                            f.Properties?.startIpAddress == publicIp &&
                            f.Properties?.endIpAddress == publicIp
                        );

                        if (existingRule != null)
                        {
                            logger.LogInformation(
                                "Definition '{Name}': IP {PublicIp} already exists in rule '{RuleName}'",
                                config.name,
                                publicIp,
                                existingRule.Name
                            );
                            alreadyExisted.Add(config.name);
                            continue;
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
                            logger.LogError(
                                "Definition '{Name}': Failed to create firewall rule for IP {PublicIp}",
                                config.name,
                                publicIp
                            );
                            failed.Add($"{config.name} (create failed)");
                            continue;
                        }

                        logger.LogInformation(
                            "Definition '{Name}': Created firewall rule '{RuleName}' for IP {PublicIp}",
                            config.name,
                            newRule.Name,
                            publicIp
                        );
                        created.Add(config.name);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            ex,
                            "Error updating firewall for definition '{Name}'",
                            config.name
                        );
                        failed.Add($"{config.name} (exception)");
                    }
                }
            }

            logger.LogInformation("Completed firewall rule updates for all definitions");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error during startup firewall update process");
        failed.Add("Startup process exception");
    }

    // Send notification to ntfy.sh
    await SendStartupNotificationAsync(
        httpClientFactory,
        logger,
        publicIp,
        created,
        alreadyExisted,
        skipped,
        failed
    );
});

// Helper method for sending ntfy notification
async Task SendStartupNotificationAsync(
    IHttpClientFactory httpClientFactory,
    ILogger logger,
    string? publicIp,
    List<string> created,
    List<string> alreadyExisted,
    List<string> skipped,
    List<string> failed)
{
    try
    {
        var hasFailures = failed.Count > 0;
        var emoji = hasFailures ? "⚠️" : "✅";

        var messageParts = new List<string>();

        // Header with IP
        if (publicIp != null)
        {
            messageParts.Add($"{emoji} Firewall Updater started (IP: {publicIp})");
        }
        else
        {
            messageParts.Add($"{emoji} Firewall Updater started (IP: unknown)");
        }

        // Results summary
        if (created.Count > 0)
        {
            messageParts.Add($"Created: {string.Join(", ", created)}");
        }
        if (alreadyExisted.Count > 0)
        {
            messageParts.Add($"Already existed: {string.Join(", ", alreadyExisted)}");
        }
        if (skipped.Count > 0)
        {
            messageParts.Add($"Skipped: {string.Join(", ", skipped)}");
        }
        if (failed.Count > 0)
        {
            messageParts.Add($"Failed: {string.Join(", ", failed)}");
        }

        // If nothing happened, say so
        if (created.Count == 0 && alreadyExisted.Count == 0 && skipped.Count == 0 && failed.Count == 0)
        {
            messageParts.Add("No definitions processed");
        }

        var message = string.Join("\n", messageParts);

        using var httpClient = httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromSeconds(10);

        var content = new StringContent(message);
        var response = await httpClient.PostAsync("https://ntfy.sh/massivepixel_45h6jk", content);

        if (response.IsSuccessStatusCode)
        {
            logger.LogDebug("Startup notification sent successfully");
        }
        else
        {
            logger.LogWarning(
                "Failed to send startup notification. Status: {StatusCode}",
                response.StatusCode
            );
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Error sending startup notification");
    }
}
```

### Success Criteria:

#### Automated Verification:
- [ ] Application builds successfully: `dotnet build`
- [ ] Application starts without errors: `dotnet run`

#### Manual Verification:
- [ ] On startup with valid credentials, logs show firewall update attempts for each definition
- [ ] Definitions without passwords are skipped with warning log
- [ ] Existing IPs are recognized and not duplicated
- [ ] New IPs trigger rule creation
- [ ] Notification is received on ntfy.sh/massivepixel_45h6jk with correct summary
- [ ] Notification shows ✅ emoji when no failures, ⚠️ when there are failures

---

## Testing Strategy

### Manual Testing Steps:
1. Start the application with valid credentials in appsettings or user secrets
2. Check logs for:
   - "Public IP address: X.X.X.X"
   - "Updating firewall rules for N definitions"
   - For each definition: either "already exists" or "Created firewall rule"
   - "Completed firewall rule updates for all definitions"
   - "Startup notification sent successfully" (at Debug level)
3. Check ntfy.sh notification (subscribe to `massivepixel_45h6jk` topic):
   - Should show IP address
   - Should list created/existed/skipped/failed definitions
   - Should show ✅ if all succeeded, ⚠️ if any failures
4. Restart the application - should see "already exists" messages since IP hasn't changed
5. Test with empty password - should see "Skipping definition" warning and notification shows it as skipped

### Example Notification Messages:

**All successful (new IP):**
```
✅ Firewall Updater started (IP: 1.2.3.4)
Created: gigpinapp, emguest
```

**All already existed:**
```
✅ Firewall Updater started (IP: 1.2.3.4)
Already existed: gigpinapp, emguest
```

**Mixed results with failures:**
```
⚠️ Firewall Updater started (IP: 1.2.3.4)
Created: gigpinapp
Failed: emguest (create failed)
```

**Missing credentials:**
```
✅ Firewall Updater started (IP: 1.2.3.4)
Skipped: gigpinapp (no password), emguest (no password)
```

**IP fetch failed:**
```
⚠️ Firewall Updater started (IP: unknown)
Failed: IP fetch failed
```

### Edge Cases:
- No definitions configured → graceful skip with info log, notification shows "Skipped: No definitions configured"
- Definition missing password → skip with warning, notification shows as skipped
- Definition missing subscriptionId → skip with warning, notification shows as skipped
- Azure API failure → error log, continue to next definition, notification shows as failed
- Network failure getting IP → warning log, notification shows IP as unknown and failure
- Notification delivery fails → warning log, app continues normally

## References

- Existing ensure-ip logic: `Program.cs:177-280`
- AzureFirewallService methods: `AzureFirewallService.cs:160-234`
- Configuration section: `appsettings.json:22-41`
- GHA ntfy.sh notification pattern: `.github/workflows/build-and-publish.yml:40-56`
