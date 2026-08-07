using AnnasArchive.Core.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;

namespace AnnasArchive.Tests.Integration;

/// <summary>
/// Every route the application actually exposes must be authorized and rate
/// limited.
///
/// <para><b>This reads the built routing table, not the source.</b> That matters
/// more than it sounds: authorization can be spelled at least three ways —
/// <c>.RequireAuthorization()</c> on the route, the same call on a
/// <c>MapGroup</c> the route hangs off, or an <c>[Authorize]</c> attribute on the
/// handler lambda — and the codebase uses all three. A grep for any one of them
/// reports routes as unguarded that are fine, and a grep for the wrong one
/// reports guarded routes it cannot see. Only the assembled endpoint metadata
/// knows the answer, and it is also what the runtime enforces.</para>
///
/// <para>This exists to make the <c>MapGroup</c> conversion safe. Moving a guard
/// from 200-odd individual routes onto their groups is exactly the change that
/// can silently drop one, and no other test in the suite would notice.</para>
/// </summary>
[Collection("Sequential")]
public sealed class RouteGuardTests : IClassFixture<WebApplicationFactory<Program>>
{
    /// <summary>
    /// The only routes allowed to be reachable without a token, each for a reason
    /// that has to survive being written down.
    /// </summary>
    private static readonly Dictionary<string, string> AnonymousByDesign = new()
    {
        ["/api/auth/login"] =
            "the endpoint that issues the token cannot require one",

        ["/api/dev/hash"] =
            "a DEBUG-only BCrypt helper for generating access-code hashes. It cannot " +
            "require a credential because it exists to mint one, and #if DEBUG keeps it " +
            "out of Release builds entirely. Rate limited under the strict 'login' policy " +
            "because BCrypt work factor 12 is expensive on purpose",

        ["/api/spotify/oauth/callback"] =
            "Spotify redirects the user's browser here, so the request cannot carry our " +
            "JWT. Safe because identity comes from the single-use state token rather than " +
            "from the request: SpotifyAuthorizationService calls _states.TryConsume(state) " +
            "and answers state_mismatch otherwise, which is both the CSRF check and the " +
            "owner lookup",

        ["/{*path:nonfile}"] =
            "the SPA fallback that serves the Angular shell. It has to be anonymous — it " +
            "is what serves the login page itself. No data, just index.html",

        ["/health"] = "liveness probe, no data",
        ["/health/live"] = "liveness probe, no data",
        ["/health/ready"] = "readiness probe, no data",
        ["/health/external"] =
            "third-party reachability for the status badges. Anonymous so an external " +
            "monitor can poll it; the deployment is Tailscale-only, so 'external' still " +
            "means inside the tailnet",
    };

    private readonly WebApplicationFactory<Program> _factory;

    public RouteGuardTests(WebApplicationFactory<Program> factory)
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting(WebHostDefaults.SuppressStatusMessagesKey, "true");

            // File-based sources start watchers, which crash the macOS test host.
            builder.ConfigureAppConfiguration((_, config) =>
            {
                foreach (var source in config.Sources
                             .Where(s => s.GetType().Name.Contains("Json") || s.GetType().Name.Contains("File"))
                             .ToList())
                {
                    config.Sources.Remove(source);
                }

                config.AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["Auth:JwtSecret"] = "test-secret-key-for-route-guard-tests-minimum-32-characters-required",
                    ["Auth:AccessCodeHash"] = "$2a$12$test",
                    ["Anna:MemberKey"] = "test-member-key",
                    ["OpenAI:ApiKey"] = "test-openai-key",
                    ["Dropbox:AppKey"] = "test-app-key",
                    ["Dropbox:AppSecret"] = "test-app-secret",
                    ["Dropbox:RefreshToken"] = "test-refresh-token",
                    ["Kindle:EmailAddress"] = "test@example.com",
                    ["Kindle:SmtpServer"] = "smtp.test.com",
                    ["Kindle:SmtpPort"] = "587",
                    ["Kindle:SmtpUsername"] = "test",
                    ["Kindle:SmtpPassword"] = "test",
                    ["Logging:LogLevel:Default"] = "Error",
                    ["Testing:DisableHealthChecks"] = "true",

                    // Conditionally-mapped route families are switched ON here on
                    // purpose. A route that is not mapped cannot be checked, so
                    // leaving these off would quietly shrink the audit.
                    ["Gaming:Enabled"] = "true",
                }!);
            });

            builder.ConfigureTestServices(services =>
            {
                foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
                    services.Remove(hosted);

                var dropbox = services.FirstOrDefault(d => d.ServiceType == typeof(Dropbox.Api.DropboxClient));
                if (dropbox is not null) services.Remove(dropbox);
                services.AddSingleton<Dropbox.Api.DropboxClient>(_ => null!);

                services.RemoveAll<IEmailService>();
                services.AddSingleton(new Mock<IEmailService>().Object);
            });
        });
    }

    private IEnumerable<RouteEndpoint> Routes() =>
        _factory.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>();

    private static string Pattern(RouteEndpoint e) => "/" + e.RoutePattern.RawText?.TrimStart('/');

    /// <summary>
    /// If this ever collapses to a handful, the audit below is passing because it
    /// found nothing rather than because everything is guarded.
    /// </summary>
    [Fact]
    public void TheRoutingTableIsFullyPopulated() =>
        Routes().Count().Should().BeGreaterThan(200,
            "the app maps well over 200 routes; a much smaller number means the host " +
            "did not finish building and this whole class is checking nothing");

    /// <summary>
    /// The rule. A route without authorization metadata is reachable by anyone who
    /// can route to the host.
    /// </summary>
    [Fact]
    public void EveryRouteRequiresAuthorizationUnlessItIsListedAsAnonymousByDesign()
    {
        var unguarded = Routes()
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Where(e => !AnonymousByDesign.ContainsKey(Pattern(e)))
            .Select(e => $"{Pattern(e)} ({string.Join('/', e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? new List<string>())})")
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        unguarded.Should().BeEmpty(
            "an unauthenticated route is reachable by anything that can reach the host; " +
            "if one of these is deliberate, add it to AnonymousByDesign with the reason");
    }

    /// <summary>
    /// A route explicitly marked <c>AllowAnonymous</c> is the same risk as one that
    /// merely forgot the guard, so it goes through the same list.
    /// </summary>
    [Fact]
    public void NoRouteOptsOutOfAuthorizationWithoutBeingListed()
    {
        var anonymous = Routes()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(Pattern)
            .Where(p => !AnonymousByDesign.ContainsKey(p))
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        anonymous.Should().BeEmpty("AllowAnonymous is a deliberate exemption and belongs in AnonymousByDesign");
    }

    /// <summary>
    /// Rate limiting is the second half of the pair that gets repeated on every
    /// route, so it is the other half that a conversion can drop. Several of these
    /// endpoints fan out to third-party services on call.
    /// </summary>
    [Fact]
    public void EveryRouteIsRateLimited()
    {
        var unlimited = Routes()
            .Where(e => e.Metadata.GetMetadata<EnableRateLimitingAttribute>() is null)
            .Select(Pattern)
            .Where(p => p.StartsWith("/api/"))
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        unlimited.Should().BeEmpty(
            "an unlimited route is a free amplification surface, and the expensive ones " +
            "reach out to third parties on every call");
    }

    /// <summary>
    /// Every route as <c>METHOD path [limiter] auth</c>.
    ///
    /// <para>The limiter <em>policy name</em> is part of the line, not just whether
    /// one is present. Moving routes onto a group means picking one policy for the
    /// group, and the files being converted are not uniform — several mix
    /// <c>"api"</c> with the looser <c>"media"</c> bucket. A route quietly
    /// inheriting the wrong bucket is invisible to a presence check and would only
    /// show up as throttling that is too tight or too loose in production.</para>
    /// </summary>
    private string RouteInventory() =>
        string.Join('\n', Routes()
            .SelectMany(e =>
            {
                var limiter = e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "none";

                // Every policy on the endpoint, not just the last. An AdminOnly
                // route landing in a plain-auth group is a privilege escalation
                // that "is it authorized at all" cannot see.
                var policies = e.Metadata.GetOrderedMetadata<IAuthorizeData>()
                    .Select(a => string.IsNullOrWhiteSpace(a.Policy) ? "default" : a.Policy)
                    .Distinct()
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();

                var auth = e.Metadata.GetMetadata<IAllowAnonymous>() is not null ? "anonymous"
                         : policies.Count > 0 ? $"auth:{string.Join('+', policies)}"
                         : "open";

                return (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                    .Select(m => $"{m} {Pattern(e)} [{limiter}] {auth}");
            })
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>
    /// The routing table must be exactly what it was before the <c>MapGroup</c>
    /// conversion.
    ///
    /// <para>The guard checks above are not enough on their own. Moving
    /// <c>/api/anna/book</c> onto a group means splitting the path into a prefix
    /// and a remainder, and getting that split wrong produces a route that is
    /// perfectly well authorized and rate limited at the wrong URL — every check
    /// above still passes while the frontend 404s. So the paths themselves are
    /// pinned.</para>
    ///
    /// <para>The fixture is generated from the pre-conversion build. If you add or
    /// remove a route deliberately, regenerate it — that edit is the point, and it
    /// makes the change visible in review.</para>
    /// </summary>
    [Fact]
    public async Task TheRoutingTableMatchesTheCheckedInInventory()
    {
        var expectedPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "routes.txt");
        File.Exists(expectedPath).Should().BeTrue(
            $"the route inventory fixture must ship with the tests (looked in {expectedPath})");

        var expected = (await File.ReadAllTextAsync(expectedPath)).Replace("\r\n", "\n").Trim();

        RouteInventory().Should().Be(expected,
            "a route changed path, appeared or disappeared; if that was deliberate, " +
            "regenerate Fixtures/routes.txt and review the diff");
    }

    /// <summary>
    /// Every exemption must still name a live route. Without this the list rots:
    /// a path gets renamed, its entry stays, and it silently exempts nothing while
    /// looking like it exempts something.
    /// </summary>
    [Fact]
    public void EveryAnonymousExemptionStillMatchesARealRoute()
    {
        var live = Routes().Select(Pattern).ToHashSet();

        var stale = AnonymousByDesign.Keys.Where(p => !live.Contains(p)).ToList();

        stale.Should().BeEmpty(
            "an exemption for a route that no longer exists is dead weight that hides " +
            "whether the rule is still being enforced (note /api/dev/hash is #if DEBUG, " +
            "so this test requires a Debug build — which is what dotnet test runs)");
    }
}
