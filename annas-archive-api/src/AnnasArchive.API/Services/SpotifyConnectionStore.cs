using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnnasArchive.API.Data;
using AnnasArchive.API.Models;
using Microsoft.AspNetCore.DataProtection;
using Serilog;

namespace AnnasArchive.API.Services;

public interface ISpotifyConnectionStore
{
    SpotifyConnectionRecord? Get(string ownerKey);
    void Save(SpotifyConnectionRecord connection);
    void Delete(string ownerKey);
}

public sealed class SpotifyConnectionStore : ISpotifyConnectionStore
{
    private const string StatePrefix = "spotify.connection.v1:";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppDatabase _database;
    private readonly IDataProtector _protector;

    public SpotifyConnectionStore(AppDatabase database, IDataProtectionProvider dataProtection)
    {
        _database = database;
        _protector = dataProtection.CreateProtector("AnnasArchive.Spotify.Connection.v1");
    }

    public SpotifyConnectionRecord? Get(string ownerKey)
    {
        var protectedJson = _database.GetState(StorageKey(ownerKey));
        if (string.IsNullOrWhiteSpace(protectedJson))
            return null;

        try
        {
            var json = _protector.Unprotect(protectedJson);
            return JsonSerializer.Deserialize<SpotifyConnectionRecord>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // A record protected by a lost/changed key is not recoverable. Remove
            // only that one connection so the admin UI can offer authorization
            // again instead of trapping the user behind a permanent 500.
            Log.Warning(ex, "[Spotify] Stored connection could not be decrypted; removing it for reauthorization");
            _database.DeleteState(StorageKey(ownerKey));
            return null;
        }
    }

    public void Save(SpotifyConnectionRecord connection)
    {
        var json = JsonSerializer.Serialize(connection, JsonOptions);
        _database.SetState(StorageKey(connection.OwnerKey), _protector.Protect(json));
    }

    public void Delete(string ownerKey) => _database.DeleteState(StorageKey(ownerKey));

    private static string StorageKey(string ownerKey)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(ownerKey));
        return StatePrefix + Convert.ToHexString(digest);
    }
}

public interface ISpotifyCurrentUser
{
    string GetRequiredOwnerKey();
}

public sealed class SpotifyCurrentUser(IHttpContextAccessor httpContextAccessor) : ISpotifyCurrentUser
{
    public string GetRequiredOwnerKey()
    {
        var ownerKey = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(ownerKey))
            throw new UnauthorizedAccessException("A signed-in user is required for Spotify.");

        return ownerKey;
    }
}

public interface ISpotifyOAuthStateStore
{
    string Create(string ownerKey);
    bool TryConsume(string state, out string ownerKey);
}

public sealed class SpotifyOAuthStateStore(TimeProvider timeProvider) : ISpotifyOAuthStateStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, StateRecord> _states = new(StringComparer.Ordinal);

    public string Create(string ownerKey)
    {
        RemoveExpired();
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _states[state] = new StateRecord(ownerKey, timeProvider.GetUtcNow().Add(Lifetime));
        return state;
    }

    public bool TryConsume(string state, out string ownerKey)
    {
        ownerKey = string.Empty;
        if (string.IsNullOrWhiteSpace(state) || !_states.TryRemove(state, out var record))
            return false;

        if (record.ExpiresAt <= timeProvider.GetUtcNow())
            return false;

        ownerKey = record.OwnerKey;
        return true;
    }

    private void RemoveExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var pair in _states)
        {
            if (pair.Value.ExpiresAt <= now)
                _states.TryRemove(pair.Key, out _);
        }
    }

    private sealed record StateRecord(string OwnerKey, DateTimeOffset ExpiresAt);
}
