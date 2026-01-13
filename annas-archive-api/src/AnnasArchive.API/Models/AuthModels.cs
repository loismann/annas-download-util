namespace AnnasArchive.API.Models;

/// <summary>
/// Request model for the login endpoint.
/// </summary>
public record CodeLoginRequest(string Code);

/// <summary>
/// Configuration model for access codes stored in appsettings.
/// </summary>
public record AccessCode(string Code, string Name, bool IsAdmin);
