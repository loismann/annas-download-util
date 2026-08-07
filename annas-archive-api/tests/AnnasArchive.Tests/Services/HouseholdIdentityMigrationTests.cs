using AnnasArchive.API.Data;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using AnnasArchive.Core.Helpers;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// Changing the owner key without moving the data is indistinguishable from
/// deleting it — nothing errors, the person's Spotify connection and requests
/// and AI spend simply stop existing. These drive the real SQLite database and
/// the real usage files, because the failure this guards against is precisely
/// "the migration hashed something slightly different from what the store wrote".
/// </summary>
public class HouseholdIdentityMigrationTests : IDisposable
{
    private const string Code = "$2a$11$abcdefghijklmnopqrstuv";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"identity-migration-{Guid.NewGuid():N}");

    private readonly string _databasePath;
    private readonly string _usageDirectory;

    public HouseholdIdentityMigrationTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "app.db");
        _usageDirectory = Path.Combine(_root, "ai-usage");
        Directory.CreateDirectory(_usageDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
        GC.SuppressFinalize(this);
    }

    private IConfiguration Config(AccessCode member)
    {
        var values = new Dictionary<string, string?>
        {
            ["Database:Path"] = _databasePath,
            ["TokenUsage:StoragePath"] = _usageDirectory,
            ["Auth:AccessCodes:0:Code"] = member.Code,
            ["Auth:AccessCodes:0:Name"] = member.Name,
            ["Auth:AccessCodes:0:IsAdmin"] = member.IsAdmin.ToString()
        };

        if (member.Id is { } id)
            values["Auth:AccessCodes:0:Id"] = id;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static async Task RunAsync(AppDatabase database, IConfiguration config) =>
        await new HouseholdIdentityMigration(database, config).StartAsync(CancellationToken.None);

    private static void SeedPlan(AppDatabase database, string ownerKey, string planId)
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO spotify_change_plan (owner_hash, plan_id, status, json, created_at, updated_at)
            VALUES ($owner, $plan, 'Draft', '{}', $now, $now)
            """;
        cmd.Parameters.AddWithValue("$owner", HouseholdIdentity.OwnerHash(ownerKey));
        cmd.Parameters.AddWithValue("$plan", planId);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static List<string> PlanIdsFor(AppDatabase database, string ownerKey)
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT plan_id FROM spotify_change_plan WHERE owner_hash = $owner";
        cmd.Parameters.AddWithValue("$owner", HouseholdIdentity.OwnerHash(ownerKey));

        using var reader = cmd.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
            ids.Add(reader.GetString(0));

        return ids;
    }

    [Fact]
    public async Task MovesOwnerScopedRowsFromTheAccessCodeOntoTheDerivedId()
    {
        var member = new AccessCode(Code, "Paul (Admin)", true);
        var config = Config(member);
        var database = new AppDatabase(config);
        SeedPlan(database, Code, "plan-1");

        await RunAsync(database, config);

        PlanIdsFor(database, Code).Should().BeEmpty("nothing is left under the old key");
        PlanIdsFor(database, HouseholdIdentity.ResolveId(member)).Should().Equal("plan-1");
    }

    [Fact]
    public async Task MovesRowsFromTheDerivedIdOntoALaterConfiguredId()
    {
        // The second migration a member can go through: an explicit Id is added
        // to config after the first deploy has already moved them once.
        var config = Config(new AccessCode(Code, "Paul", true) { Id = "paul" });
        var database = new AppDatabase(config);
        SeedPlan(database, HouseholdIdentity.DeriveId(Code), "plan-2");

        await RunAsync(database, config);

        PlanIdsFor(database, "paul").Should().Equal("plan-2");
    }

    [Fact]
    public async Task MovesTheSpotifyConnectionState()
    {
        var member = new AccessCode(Code, "Paul", true);
        var config = Config(member);
        var database = new AppDatabase(config);
        const string prefix = "spotify.connection.v1:";
        database.SetState(prefix + HouseholdIdentity.OwnerHash(Code), "\"protected-blob\"");

        await RunAsync(database, config);

        var newId = HouseholdIdentity.ResolveId(member);
        database.GetState(prefix + HouseholdIdentity.OwnerHash(newId))
            .Should().Be("\"protected-blob\"", "the person stays signed in to Spotify");
        database.GetState(prefix + HouseholdIdentity.OwnerHash(Code)).Should().BeNull();
    }

    [Fact]
    public async Task MovesAudiobookRequestAttribution()
    {
        var member = new AccessCode(Code, "Paul", true);
        var config = Config(member);
        var database = new AppDatabase(config);

        using (var conn = database.OpenConnection())
        {
            using var seed = conn.CreateCommand();
            seed.CommandText = """
                INSERT INTO audiobook_request
                    (listenarr_id, asin, isbn_json, title_snapshot, author_snapshot,
                     last_observed_status, created_at, updated_at)
                VALUES (7, 'B000000001', '[]', 'A Book', 'An Author', 'Queued', $now, $now);

                INSERT INTO audiobook_request_user
                    (listenarr_id, app_user_id, owner_label, requested_at)
                VALUES (7, $owner, 'Paul', $now);
                """;
            seed.Parameters.AddWithValue("$owner", HouseholdIdentity.OwnerHash(Code));
            seed.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            seed.ExecuteNonQuery();
        }

        await RunAsync(database, config);

        var store = new AudiobookRequestStore(database);
        var newId = HouseholdIdentity.ResolveId(member);
        store.ListForUser(HouseholdIdentity.OwnerHash(newId)).Should().ContainSingle()
            .Which.ListenarrId.Should().Be(7);
    }

    [Fact]
    public async Task RenamesThePerPersonAiSpendFile()
    {
        var member = new AccessCode(Code, "Paul", true);
        var config = Config(member);
        var database = new AppDatabase(config);
        var oldFile = Path.Combine(_usageDirectory, $"{SafeFileName.ForKey(Code)}.json");
        File.WriteAllText(oldFile, """{"PromptTokens":900000,"CompletionTokens":10}""");

        await RunAsync(database, config);

        var newFile = Path.Combine(
            _usageDirectory, $"{SafeFileName.ForKey(HouseholdIdentity.ResolveId(member))}.json");
        File.Exists(newFile).Should().BeTrue("otherwise the deploy hands out a fresh allowance");
        File.Exists(oldFile).Should().BeFalse();
        File.ReadAllText(newFile).Should().Contain("900000");
    }

    [Fact]
    public async Task IsIdempotent()
    {
        var member = new AccessCode(Code, "Paul", true);
        var config = Config(member);
        var database = new AppDatabase(config);
        SeedPlan(database, Code, "plan-1");

        await RunAsync(database, config);
        await RunAsync(database, config);
        await RunAsync(database, config);

        PlanIdsFor(database, HouseholdIdentity.ResolveId(member)).Should().Equal("plan-1");
    }

    [Fact]
    public async Task KeepsTheNewerRowWhenBothKeysHoldTheSamePrimaryKey()
    {
        // Only reachable if someone used the app between the deploy and the
        // migration finishing. The row already under the new id is the live one.
        var member = new AccessCode(Code, "Paul", true);
        var config = Config(member);
        var database = new AppDatabase(config);
        var newId = HouseholdIdentity.ResolveId(member);
        SeedPlan(database, Code, "plan-1");
        SeedPlan(database, newId, "plan-1");

        await RunAsync(database, config);

        PlanIdsFor(database, newId).Should().Equal("plan-1");
        PlanIdsFor(database, Code).Should().BeEmpty("the stale duplicate is not left to shadow it");
    }

    [Fact]
    public async Task LeavesEveryoneElsesDataAlone()
    {
        var member = new AccessCode(Code, "Paul", true);
        var config = Config(member);
        var database = new AppDatabase(config);
        SeedPlan(database, "someone-else", "not-mine");

        await RunAsync(database, config);

        PlanIdsFor(database, "someone-else").Should().Equal("not-mine");
    }

    [Fact]
    public async Task DoesNotBlockStartupWhenNothingIsConfigured()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = _databasePath })
            .Build();

        var act = async () => await RunAsync(new AppDatabase(config), config);

        await act.Should().NotThrowAsync();
    }
}
