using AnnasArchive.API.Models;
using AnnasArchive.API.Services.Spotify;

namespace AnnasArchive.Tests.Services.Spotify;

/// <summary>
/// The catalog is the security boundary between "the model said so" and "the
/// server will do it". These tests are mostly about what it refuses.
/// </summary>
public class SpotifyActionCatalogTests
{
    [Theory]
    [InlineData("search_tracks", SpotifyReadAction.SearchTracks)]
    [InlineData("list_playlists", SpotifyReadAction.ListPlaylists)]
    [InlineData("inspect_playlist", SpotifyReadAction.InspectPlaylist)]
    [InlineData("list_playlist_items", SpotifyReadAction.ListPlaylistItems)]
    public void ParsesEveryWireNameItAdvertises(string wireName, SpotifyReadAction expected)
    {
        SpotifyActionCatalog.Parse(wireName).Should().Be(expected);
    }

    [Theory]
    [InlineData("create_playlist")]
    [InlineData("delete_playlist")]
    [InlineData("add_tracks")]
    [InlineData("generate_playlist")]
    [InlineData("merge_playlists")]
    public void RefusesWriteActionsTheModelMightInvent(string wireName)
    {
        // The prototype's prompt advertised these. A model still emitting one must
        // land on Unknown rather than reaching anything that mutates the account.
        SpotifyActionCatalog.Parse(wireName).Should().Be(SpotifyReadAction.Unknown);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("nonsense")]
    public void MapsAnythingUnrecognisedToUnknown(string? wireName)
    {
        SpotifyActionCatalog.Parse(wireName).Should().Be(SpotifyReadAction.Unknown);
    }

    [Fact]
    public void EveryAdvertisedActionRoundTripsThroughItsWireName()
    {
        // Guards the catalog against an action being listed in the prompt under a
        // name that does not dispatch — the prompt is generated from this table.
        foreach (var definition in SpotifyActionCatalog.All)
        {
            SpotifyActionCatalog.Parse(definition.WireName).Should().Be(definition.Action);
            SpotifyActionCatalog.WireNameOf(definition.Action).Should().Be(definition.WireName);
        }
    }

    [Fact]
    public void ThePromptListsEveryDispatchableActionAndNothingElse()
    {
        var prompt = SpotifyActionCatalog.PromptActionList();

        foreach (var definition in SpotifyActionCatalog.All)
            prompt.Should().Contain(definition.WireName);

        // Checked as whole action lines, not substrings: `plan_create_playlist` is a
        // legitimate action that happens to contain "create_playlist". What must
        // never appear is a bare write verb the model could dispatch directly.
        prompt.Should().NotContain("- create_playlist:");
        prompt.Should().NotContain("- add_tracks:");
        prompt.Should().NotContain("- delete_playlist:");
    }

    // ─── validation ──────────────────────────────────────────────────────────

    [Fact]
    public void AcceptsAWellFormedCommand()
    {
        var result = SpotifyActionCatalog.Validate(new SpotifyCommandEnvelope(
            SpotifyActionCatalog.SchemaVersion, "list_playlists", Confidence: 0.9));

        result.Action.Should().Be(SpotifyReadAction.ListPlaylists);
        result.Confidence.Should().Be(0.9);
    }

    [Fact]
    public void RejectsAnEnvelopeFromADifferentSchemaVersion()
    {
        var result = SpotifyActionCatalog.Validate(new SpotifyCommandEnvelope(
            SpotifyActionCatalog.SchemaVersion + 1, "list_playlists", Confidence: 1.0));

        result.Action.Should().Be(SpotifyReadAction.Unknown);
    }

    [Fact]
    public void RejectsANullEnvelope()
    {
        SpotifyActionCatalog.Validate(null).Action.Should().Be(SpotifyReadAction.Unknown);
    }

    [Theory]
    [InlineData("search_tracks")]
    [InlineData("find_playlists")]
    public void RefusesAnActionThatNeedsAQueryButWasGivenNone(string wireName)
    {
        var result = SpotifyActionCatalog.Validate(new SpotifyCommandEnvelope(
            SpotifyActionCatalog.SchemaVersion, wireName, new SpotifyCommandArguments(), Confidence: 1.0));

        result.Action.Should().Be(SpotifyReadAction.Unknown);
        result.Clarification.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("inspect_playlist")]
    [InlineData("list_playlist_items")]
    public void RefusesAnActionThatNeedsAPlaylistButWasGivenNone(string wireName)
    {
        var result = SpotifyActionCatalog.Validate(new SpotifyCommandEnvelope(
            SpotifyActionCatalog.SchemaVersion, wireName, new SpotifyCommandArguments(), Confidence: 1.0));

        result.Action.Should().Be(SpotifyReadAction.Unknown);
        result.Clarification.Should().Contain("playlist");
    }

    [Fact]
    public void HighConfidenceDoesNotSubstituteForAMissingArgument()
    {
        // Being certain it understood is not evidence it supplied a playlist name.
        var result = SpotifyActionCatalog.Validate(new SpotifyCommandEnvelope(
            SpotifyActionCatalog.SchemaVersion, "list_playlist_items",
            new SpotifyCommandArguments(), Confidence: 1.0));

        result.Action.Should().Be(SpotifyReadAction.Unknown);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TreatsABlankArgumentAsMissing(string reference)
    {
        var result = SpotifyActionCatalog.Validate(new SpotifyCommandEnvelope(
            SpotifyActionCatalog.SchemaVersion, "inspect_playlist",
            new SpotifyCommandArguments(PlaylistReference: reference), Confidence: 1.0));

        result.Action.Should().Be(SpotifyReadAction.Unknown);
    }

    [Fact]
    public void AllowsActionsThatNeedNoArguments()
    {
        foreach (var wireName in new[] { "list_playlists", "get_recent_playlist_contexts", "explain_capability" })
        {
            SpotifyActionCatalog.Validate(new SpotifyCommandEnvelope(
                    SpotifyActionCatalog.SchemaVersion, wireName, null, 1.0))
                .Action.Should().NotBe(SpotifyReadAction.Unknown);
        }
    }

    [Fact]
    public void KeepsTheModelsClarificationWhenItCouldNotPickAnAction()
    {
        var result = SpotifyActionCatalog.Validate(new SpotifyCommandEnvelope(
            SpotifyActionCatalog.SchemaVersion, "unknown", null, 0.2, "Did you mean a playlist or an artist?"));

        result.Action.Should().Be(SpotifyReadAction.Unknown);
        result.Clarification.Should().Be("Did you mean a playlist or an artist?");
    }
}
