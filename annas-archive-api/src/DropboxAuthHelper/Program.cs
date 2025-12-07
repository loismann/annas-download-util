using Dropbox.Api;

Console.WriteLine("Dropbox refresh token helper");
Console.WriteLine("-----------------------------");
Console.WriteLine("This runs the OAuth flow with token_access_type=offline to produce a long-lived refresh token.");
Console.WriteLine("Make sure the redirect URI you provide is whitelisted in your Dropbox app settings.");
Console.WriteLine();

var appKey = Prompt("Dropbox App Key");
var appSecret = Prompt("Dropbox App Secret");
var redirectUri = Prompt("Redirect URI (as registered in the Dropbox console)", "http://localhost:52475/auth-finish");

var state = Guid.NewGuid().ToString("N");
var authorizeUri = DropboxOAuth2Helper.GetAuthorizeUri(
    OAuthResponseType.Code,
    appKey,
    redirectUri,
    state: state,
    tokenAccessType: TokenAccessType.Offline);

Console.WriteLine("1) Open the URL below in your browser and approve access:");
Console.WriteLine(authorizeUri.ToString());
Console.WriteLine();
Console.WriteLine("2) After approval, copy the 'code' query parameter from the redirect URL.");
Console.WriteLine($"   (You should also see state='{state}' if the redirect URI echoes the state value.)");
Console.Write("Paste the code (or full redirect URL) here: ");

var rawInput = Console.ReadLine()?.Trim();
if (string.IsNullOrWhiteSpace(rawInput))
{
    Console.WriteLine("No code provided. Exiting.");
    return;
}

// Support pasting the full redirect URL or a code with &state appended
string? code = null;
string? returnedState = null;

if (rawInput.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
    rawInput.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
{
    if (Uri.TryCreate(rawInput, UriKind.Absolute, out var uri))
    {
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        code = query.Get("code");
        returnedState = query.Get("state");
    }
}
else
{
    // If they pasted "code=...&state=..." or "CODE&state=..."
    var trimmed = rawInput.StartsWith("code=", StringComparison.OrdinalIgnoreCase)
        ? rawInput.Substring("code=".Length)
        : rawInput;

    var parts = trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries);
    code = parts.Length > 0 ? parts[0] : null;

    var statePart = parts.FirstOrDefault(p => p.StartsWith("state=", StringComparison.OrdinalIgnoreCase));
    if (statePart != null)
        returnedState = statePart.Substring("state=".Length);
}

if (string.IsNullOrWhiteSpace(code))
{
    Console.WriteLine("Could not extract an authorization code. Please try again and paste the full redirect URL or just the code value.");
    return;
}

try
{
    var response = await DropboxOAuth2Helper.ProcessCodeFlowAsync(code, appKey, appSecret, redirectUri);

    Console.WriteLine();
    Console.WriteLine("✅ Success! Use these values in appsettings.json under Dropbox:");
    Console.WriteLine($"RefreshToken: {response.RefreshToken}");
    Console.WriteLine($"AccessToken (short-lived): {response.AccessToken}");
    var expiresAt = response.ExpiresAt?.ToLocalTime();
    Console.WriteLine($"ExpiresAt: {(expiresAt.HasValue ? expiresAt.ToString() : "(not provided)")}");
    Console.WriteLine($"UID: {response.Uid}");

    if (!string.IsNullOrEmpty(returnedState) && !string.Equals(returnedState, state, StringComparison.Ordinal))
    {
        Console.WriteLine($"⚠️  Note: state mismatch (expected '{state}', got '{returnedState}'). If this keeps happening, restart and try again.");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Failed to exchange code for tokens: {ex.Message}");
}

static string Prompt(string label, string? defaultValue = null)
{
    while (true)
    {
        Console.Write(string.IsNullOrEmpty(defaultValue)
            ? $"{label}: "
            : $"{label} [{defaultValue}]: ");

        var value = Console.ReadLine();

        if (!string.IsNullOrWhiteSpace(value))
            return value.Trim();

        if (!string.IsNullOrWhiteSpace(defaultValue))
            return defaultValue;

        Console.WriteLine("Value is required.");
    }
}
