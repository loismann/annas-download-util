using AnnasArchive.API.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping VPN (Gluetun/PIA) toggle endpoints.
/// </summary>
public static class VpnSettingsEndpoints
{
    public static WebApplication MapVpnSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/vpn/settings", HandleGetSettings)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/vpn/settings", HandleUpdateSettings)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static IResult HandleGetSettings(IVpnSettingsService vpnSettings)
    {
        var current = vpnSettings.Current;
        return Results.Ok(new
        {
            enabled = current.Enabled,
            region = current.Region,
            availableRegions = vpnSettings.AvailableRegions
        });
    }

    private static async Task<IResult> HandleUpdateSettings(
        [FromBody] UpdateVpnSettingsRequest request,
        IVpnSettingsService vpnSettings,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Region))
            return Results.BadRequest(new { error = "region is required." });

        try
        {
            var updated = await vpnSettings.UpdateAsync(request.Enabled, request.Region, cancellationToken);
            return Results.Ok(new
            {
                enabled = updated.Enabled,
                region = updated.Region,
                availableRegions = vpnSettings.AvailableRegions
            });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            Log.Warning("[VpnSettings] Failed to update VPN settings: {Message}", ex.Message);
            return Results.Problem("Failed to update VPN settings — Gluetun's control API may be unreachable.");
        }
    }
}

public record UpdateVpnSettingsRequest(bool Enabled, string Region);
