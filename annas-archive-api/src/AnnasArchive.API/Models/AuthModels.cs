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
}
