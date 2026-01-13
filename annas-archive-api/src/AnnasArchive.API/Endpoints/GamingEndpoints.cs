using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping gaming PC control endpoints.
/// </summary>
public static class GamingEndpoints
{
    /// <summary>
    /// Maps gaming PC control endpoints to the application.
    /// </summary>
    public static WebApplication MapGamingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/gaming/status", HandleGamingStatus)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/gaming/toggle", HandleGamingToggle)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static async Task<IResult> HandleGamingStatus(IConfiguration cfg)
    {
        var pcIp = "192.168.0.80"; // Gaming PC IP

        try
        {
            Console.WriteLine($"→ Checking gaming PC status at {pcIp}");

            // Use ping to check if PC is reachable
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/ping",
                    Arguments = $"-c 1 -W 1 {pcIp}", // 1 ping with 1 second timeout
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.WaitForExitAsync();

            var isOnline = process.ExitCode == 0;
            Console.WriteLine($"✅ Gaming PC status: {(isOnline ? "ONLINE" : "OFFLINE")}");

            return Results.Ok(new
            {
                isOnline = isOnline,
                ipAddress = pcIp,
                lastChecked = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Gaming PC status check exception: {ex.Message}");
            return Results.Ok(new
            {
                isOnline = false,
                ipAddress = pcIp,
                lastChecked = DateTime.UtcNow,
                error = "Failed to check PC status"
            });
        }
    }

    private static async Task<IResult> HandleGamingToggle(
        [FromQuery] int action,
        IConfiguration cfg)
    {
        if (action != 1 && action != 2)
            return Results.BadRequest(new { error = "Invalid action. Use 1 to wake PC, 2 to sleep PC." });

        var synologyHost = cfg["Gaming:SynologyHost"];
        var synologyUser = cfg["Gaming:SynologyUser"];
        var synologyKeyPath = cfg["Gaming:SynologyKeyPath"];

        if (string.IsNullOrEmpty(synologyHost) || string.IsNullOrEmpty(synologyUser))
            return Results.Problem("Gaming PC control is not configured.");

        try
        {
            var actionName = action == 1 ? "wake" : "sleep";
            Console.WriteLine($"→ Gaming PC {actionName} request received");

            // SSH into Synology and run the wake-steam.sh script
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/ssh",
                    Arguments = string.IsNullOrEmpty(synologyKeyPath)
                        ? $"{synologyUser}@{synologyHost} \"/usr/local/bin/wake-steam.sh {action}\""
                        : $"-i {synologyKeyPath} {synologyUser}@{synologyHost} \"/usr/local/bin/wake-steam.sh {action}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                Console.WriteLine($"✅ Gaming PC {actionName} successful");
                Console.WriteLine(output);
                return Results.Ok(new
                {
                    success = true,
                    action = actionName,
                    message = action == 1
                        ? "Gaming PC is waking up and launching Steam..."
                        : "Gaming PC is shutting down...",
                    output = output
                });
            }
            else
            {
                Console.WriteLine($"❌ Gaming PC {actionName} failed: {error}");
                return Results.Ok(new
                {
                    success = false,
                    action = actionName,
                    message = "Failed to control gaming PC.",
                    error = error
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Gaming PC control exception: {ex.Message}");
            return Results.Problem("An error occurred while controlling the gaming PC.");
        }
    }
}
