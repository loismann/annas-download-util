using System.Net;
using System.Text;
using System.Text.Json;
using AnnasArchive.API.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Tests.Services;

public sealed class ListenarrServiceTests
{
    [Fact]
    public async Task Status_UsesPinnedReadOnlyContract_AndPassesConfiguredGate()
    {
        using var fixture = await LoadFixtureAsync();
        var requests = new List<HttpRequestMessage>();
        var service = CreateService(fixture, requests);

        var result = await service.GetIntegrationStatusAsync();

        result.Enabled.Should().BeFalse("the request surface stays disabled in phase 1");
        result.Configured.Should().BeTrue();
        result.Reachable.Should().BeTrue();
        result.Ready.Should().BeTrue();
        result.Version.Should().Be("1.2.2");
        result.RootFolderCount.Should().Be(1);
        result.QualityProfileCount.Should().Be(1);
        result.EnabledIndexerCount.Should().Be(1);
        result.EnabledDownloadClientCount.Should().Be(1);
        result.LibraryItemCount.Should().Be(0);
        result.ReadOnlyGatePassed.Should().BeTrue();
        result.GateFailures.Should().BeEmpty();

        requests.Should().HaveCount(8);
        foreach (var request in requests)
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.Headers.GetValues("X-Api-Key").Should().ContainSingle()
                .Which.Should().Be("listenarr-test-key");
        }
    }

    [Fact]
    public async Task Status_ReportsMissingReadOnlyDependencies_WithoutMutatingAnything()
    {
        using var fixture = await LoadFixtureAsync();
        var root = fixture.RootElement;
        var emptyCollections = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["rootfolders"] = JsonDocument.Parse("[]").RootElement.Clone(),
            ["qualityprofiles"] = JsonDocument.Parse("[]").RootElement.Clone(),
            ["indexers"] = JsonDocument.Parse("[]").RootElement.Clone(),
            ["downloadclients"] = JsonDocument.Parse("[]").RootElement.Clone(),
            ["library"] = root.GetProperty("library").Clone(),
            ["ready"] = root.GetProperty("ready").Clone(),
            ["health"] = root.GetProperty("health").Clone(),
            ["info"] = root.GetProperty("info").Clone()
        };
        var requests = new List<HttpRequestMessage>();
        var service = CreateService(emptyCollections, requests);

        var result = await service.GetIntegrationStatusAsync();

        result.ReadOnlyGatePassed.Should().BeFalse();
        result.GateFailures.Should().Contain(new[]
        {
            "No audiobook root folder is configured.",
            "No quality profile is configured.",
            "No enabled indexer is configured.",
            "No enabled download client is configured."
        });
        requests.Should().OnlyContain(request => request.Method == HttpMethod.Get);
    }

    private static ListenarrService CreateService(JsonDocument fixture, List<HttpRequestMessage> requests)
    {
        var data = fixture.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        return CreateService(data, requests);
    }

    private static ListenarrService CreateService(
        IReadOnlyDictionary<string, JsonElement> fixture,
        List<HttpRequestMessage> requests)
    {
        var handler = new FixtureHandler(fixture, requests);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Listenarr:Enabled"] = "false",
            ["Listenarr:BaseUrl"] = "http://listenarr:4545",
            ["Listenarr:ApiKey"] = "listenarr-test-key"
        }).Build();

        return new ListenarrService(new HttpClient(handler), configuration);
    }

    private static async Task<JsonDocument> LoadFixtureAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Listenarr", "status-contract-1.2.2.json");
        await using var stream = File.OpenRead(path);
        return await JsonDocument.ParseAsync(stream);
    }

    private sealed class FixtureHandler(
        IReadOnlyDictionary<string, JsonElement> fixture,
        List<HttpRequestMessage> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(request);
            var key = request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/system/ready" => "ready",
                "/api/v1/system/health" => "health",
                "/api/v1/system/info" => "info",
                "/api/v1/rootfolders" => "rootfolders",
                "/api/v1/qualityprofile" => "qualityprofiles",
                "/api/v1/indexers" => "indexers",
                "/api/v1/download-clients" => "downloadclients",
                "/api/v1/library" => "library",
                _ => throw new InvalidOperationException($"Unexpected fixture request: {request.RequestUri}")
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(fixture[key].GetRawText(), Encoding.UTF8, "application/json")
            });
        }
    }
}
