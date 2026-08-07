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
        // The guard pair lives on the group, so a route added below inherits it
        // instead of needing it repeated — which is how one silently ships without.
        var group = app.MapGroup("/api/vpn")
            .RequireAuthorization()
            .RequireRateLimiting("api");

        group.MapGet("/settings", HandleGetSettings);

        group.MapPost("/settings", HandleUpdateSettings);

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
        catch (Exception ex) when (ex is not ArgumentException)
        {
            Log.Warning("[VpnSettings] Failed to update VPN settings: {Message}", ex.Message);
            return Results.Problem("Failed to update VPN settings — Gluetun's control API may be unreachable.");
        }
    }
}

public record UpdateVpnSettingsRequest(bool Enabled, string Region);
