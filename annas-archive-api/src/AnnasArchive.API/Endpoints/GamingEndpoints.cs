using AnnasArchive.API.Helpers;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Serilog;

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
        // The guard pair lives on the group, so a route added below inherits it
        // instead of needing it repeated — which is how one silently ships without.
        var group = app.MapGroup("/api/gaming")
            .RequireAuthorization()
            .RequireRateLimiting("api");

        group.MapGet("/status", HandleGamingStatus);

        group.MapPost("/toggle", HandleGamingToggle);

        return app;
    }

    private static async Task<IResult> HandleGamingStatus(IConfiguration cfg)
    {
        // Config, not a literal: a LAN address in tracked source is exactly what
        // the project's own security policy forbids. Unset simply means "gaming
        // control isn't set up here", same shape as the toggle handler below.
        var pcIp = cfg["Gaming:PcIp"];
        if (string.IsNullOrWhiteSpace(pcIp))
            return Results.Problem("Gaming PC control is not configured.");

        try
        {
            Log.Information("→ Checking gaming PC status at {PcIp}", pcIp);

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
            Log.Information("Gaming PC status: {Status}", isOnline ? "ONLINE" : "OFFLINE");

            return Results.Ok(new
            {
                isOnline = isOnline,
                ipAddress = pcIp,
                lastChecked = DateTime.UtcNow
            });
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            Log.Error(ex, "Gaming PC status check failed");
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
            return ApiResponse.BadRequest("Invalid action. Use 1 to wake PC, 2 to sleep PC.");

        var synologyHost = cfg["Gaming:SynologyHost"];
        var synologyUser = cfg["Gaming:SynologyUser"];
        var synologyKeyPath = cfg["Gaming:SynologyKeyPath"];

        if (string.IsNullOrEmpty(synologyHost) || string.IsNullOrEmpty(synologyUser))
            return Results.Problem("Gaming PC control is not configured.");

        try
        {
            var actionName = action == 1 ? "wake" : "sleep";
            Log.Information("→ Gaming PC {ActionName} request received", actionName);

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
                // `output` is process output, so it must be an ARGUMENT, never the
                // template — a stray brace in it would otherwise be parsed as a
                // placeholder and mangle (or drop) the line.
                Log.Information("✅ Gaming PC {ActionName} successful: {Output}", actionName, output);
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
                Log.Warning("❌ Gaming PC {ActionName} failed: {Error}", actionName, error);
                // 502: the wake/shutdown script reached the gaming PC and it refused,
                // or the script itself failed. Either way this request did not do what
                // it was asked, and a 200 said otherwise.
                return Results.Json(
                    new
                    {
                        success = false,
                        action = actionName,
                        message = "Failed to control gaming PC.",
                        error = error
                    },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            Log.Error(ex, "Gaming PC control failed");
            return Results.Problem("An error occurred while controlling the gaming PC.");
        }
    }
}
