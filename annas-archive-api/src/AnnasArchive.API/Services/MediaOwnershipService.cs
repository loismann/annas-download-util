using System.Text.Json;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>
/// Tracks who requested a given Sonarr series / Radarr movie ("Paul"/"Mom"/"Dad")
/// so the media library page can filter/badge downloads the same way the ebook
/// library tags books by owner. Sonarr/Radarr own the actual media files (and
/// reorganize/rename them on import), so ownership is kept out-of-band here —
/// a small JSON file keyed by Sonarr/Radarr's own record ID — rather than tagging
/// the files themselves.
/// </summary>
public interface IMediaOwnershipService
{
    void SetOwner(string type, int id, string? owner);
    string? GetOwner(string type, int id);
    IReadOnlyDictionary<string, string> GetAllOwners();
}

public class MediaOwnershipService : IMediaOwnershipService
{
    private readonly string _storagePath;
    private readonly object _fileLock = new();

    public MediaOwnershipService(string storagePath)
    {
        _storagePath = storagePath;
    }

    public void SetOwner(string type, int id, string? owner)
    {
        var key = $"{type}:{id}";
        lock (_fileLock)
        {
            var data = LoadUnsafe();
            if (string.IsNullOrWhiteSpace(owner))
                data.Remove(key);
            else
                data[key] = owner;

            SaveUnsafe(data);
        }
    }

    public string? GetOwner(string type, int id)
    {
        lock (_fileLock)
        {
            return LoadUnsafe().GetValueOrDefault($"{type}:{id}");
        }
    }

    public IReadOnlyDictionary<string, string> GetAllOwners()
    {
        lock (_fileLock)
        {
            return LoadUnsafe();
        }
    }

    private Dictionary<string, string> LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_storagePath))
                return new Dictionary<string, string>();

            var json = File.ReadAllText(_storagePath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            Log.Warning("[MediaOwnership] Failed to load {Path}: {Message}", _storagePath, ex.Message);
            return new Dictionary<string, string>();
        }
    }

    private void SaveUnsafe(Dictionary<string, string> data)
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_storagePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warning("[MediaOwnership] Failed to save {Path}: {Message}", _storagePath, ex.Message);
        }
    }
}
