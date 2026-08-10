using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using AnnasArchive.API.Reader2.Lenses;
using AnnasArchive.API.Services.Ai;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace AnnasArchive.Tests.Reader2;

/// <summary>
/// The real application, over real HTTP, with a fake model and a temporary
/// library.
///
/// <para>The unit tests above cover the pipeline's decisions. This covers what
/// only an assembled app can answer: that the routes are wired to the handlers
/// anyone thinks they are, that auth actually rejects, that a lens registered
/// through DI reaches the wire, and that a gate response comes back as a status
/// code rather than as an exception halfway through a stream.</para>
///
/// <para><see cref="TestLens"/> is registered here and nowhere in production —
/// the whole extensibility claim, exercised end to end.</para>
/// </summary>
public sealed class Reader2AppFactory : WebApplicationFactory<Program>
{
    private const string JwtSecret =
        "reader2-integration-secret-key-minimum-32-characters-required-for-security";

    public string Root { get; } =
        Path.Combine(Path.GetTempPath(), "r2-app-" + Guid.NewGuid().ToString("N"));

    public FakeCompletions Ai { get; } = new();
    public FakeTokenUsage Usage { get; } = new();

    /// <summary>
    /// Configuration a test wants instead of the defaults below. Set before the
    /// first request, since the host is built lazily on it.
    /// </summary>
    public Dictionary<string, string?> Settings { get; } = [];

    public string LibraryRoot => Path.Combine(Root, "library");

    private readonly string? _previousLibraryRoot;

    public Reader2AppFactory()
    {
        Directory.CreateDirectory(LibraryRoot);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");

        // StoragePaths.LibraryRoot reads an environment variable rather than
        // IConfiguration, so this is the only way to point the app at a temporary
        // library. Restored on dispose, and these tests run in the Sequential
        // collection because a process-wide variable is not safe in parallel.
        _previousLibraryRoot = Environment.GetEnvironmentVariable("LIBRARY_ROOT");
        Environment.SetEnvironmentVariable("LIBRARY_ROOT", LibraryRoot);
    }

    /// <summary>Puts a real EPUB in the library and returns its file name.</summary>
    public string AddBook(byte[] epub, string fileName = "book.epub")
    {
        File.WriteAllBytes(Path.Combine(LibraryRoot, fileName), epub);
        return fileName;
    }

    /// <summary>A client carrying a token for one household member.</summary>
    public HttpClient SignedInAs(string userId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TokenFor(userId));
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.UseSetting(WebHostDefaults.SuppressStatusMessagesKey, "true");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            // File sources bring watchers, which crash the macOS test host.
            foreach (var source in config.Sources
                         .Where(s => s.GetType().Name.Contains("Json") || s.GetType().Name.Contains("File"))
                         .ToList())
                config.Sources.Remove(source);

            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:JwtSecret"] = JwtSecret,
                ["Auth:AccessCodeHash"] = "$2a$12$test",
                ["OpenAI:ApiKey"] = "test-openai-key",
                ["Dropbox:AppKey"] = "k",
                ["Dropbox:AppSecret"] = "s",
                ["Dropbox:RefreshToken"] = "r",
                ["Testing:DisableHealthChecks"] = "true",
                ["Logging:LogLevel:Default"] = "Error",
                ["Library:Path"] = LibraryRoot,
                ["Database:Path"] = Path.Combine(Root, "app.db"),
                ["Reader2:TextRoot"] = Path.Combine(Root, "text"),
                // Small enough that a short fixture chapter still climbs the ladder.
                ["Reader2:ChunkSize"] = "40",
                ["Reader2:DirectSummaryWordThreshold"] = "20"
            });

            // Last, so a test can override any of the above. The alternative is a
            // second factory per setting, and a configuration flag is only worth
            // having if something checks what happens when it is off.
            config.AddInMemoryCollection(Settings);
        });

        builder.ConfigureTestServices(services =>
        {
            foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
                services.Remove(hosted);

            services.RemoveAll<IAiChatCompletion>();
            services.AddSingleton<IAiChatCompletion>(Ai);
            services.RemoveAll<AnnasArchive.Core.Services.ITokenUsageService>();
            services.AddSingleton<AnnasArchive.Core.Services.ITokenUsageService>(Usage);

            // The whole cost of a fourth book type, in a test project. If anything
            // else has to change for it to work, extensibility was never real.
            services.AddSingleton<IReaderLens, TestLens>();

            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                // Left at the default (mapping on), unlike the older integration
                // tests: the reader identifies a household member by
                // ClaimTypes.NameIdentifier, and with mapping off the token's
                // "nameid" never becomes that, so every per-user route 401s.
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSecret)),
                    ValidIssuer = "AnnasArchiveAPI",
                    ValidAudience = "AnnasArchiveApp",
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                    RoleClaimType = "role"
                };
            });
        });
    }

    private static string TokenFor(string userId) =>
        new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, userId),
                    new Claim("role", "user")
                ]),
                Expires = DateTime.UtcNow.AddHours(1),
                Issuer = "AnnasArchiveAPI",
                Audience = "AnnasArchiveApp",
                SigningCredentials = new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSecret)),
                    SecurityAlgorithms.HmacSha256Signature)
            }));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;
        Environment.SetEnvironmentVariable("LIBRARY_ROOT", _previousLibraryRoot);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch { /* temp */ }
    }
}
