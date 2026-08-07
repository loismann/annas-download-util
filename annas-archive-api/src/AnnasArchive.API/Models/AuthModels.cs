namespace AnnasArchive.API.Models;

/// <summary>
/// Request model for the login endpoint.
/// </summary>
public record CodeLoginRequest(string Code);

/// <summary>
/// Configuration model for access codes stored in appsettings.
/// </summary>
public record AccessCode(string Code, string Name, bool IsAdmin)
{
    /// <summary>
    /// Single letter shown for this person in the presence indicator, and the
    /// opt-in for appearing there at all.
    ///
    /// Optional: with none configured anywhere, the old behaviour of inferring
    /// "M"/"D" from a "(Mom)"/"(Dad)" suffix still applies, so an existing
    /// appsettings.json keeps working untouched. Setting it on any account
    /// switches the whole feature over to the configured values, which is what
    /// makes adding a third person a config edit rather than a code change.
    /// </summary>
    public string? Initial { get; init; }

    /// <summary>
    /// Stable owner key for everything this person owns — Spotify connection and
    /// plans, audiobook requests, photo print runs, AI spend. Optional, and the
    /// only reason to set it is code rotation: with it unset the id is derived
    /// from <see cref="Code"/>, so changing the code moves the person's data out
    /// from under them. See <see cref="Helpers.HouseholdIdentity"/>.
    ///
    /// Any short opaque string works (<c>"paul"</c>, <c>"member-2"</c>); it is
    /// never shown to anyone and never leaves the server except hashed.
    /// </summary>
    public string? Id { get; init; }
}
