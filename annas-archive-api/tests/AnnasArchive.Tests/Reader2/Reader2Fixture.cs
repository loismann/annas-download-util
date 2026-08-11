using AnnasArchive.API.Data;
using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Tests.Reader2;

/// <summary>
/// A throwaway database, text root, and fake library for one test class.
///
/// Real SQLite and a real temp directory rather than mocks: the behaviour under
/// test is largely SQLite's own (cascades, UNIQUE, upsert guards), and a mock
/// would only assert that we called the methods we wrote.
/// </summary>
public sealed class Reader2Fixture : IDisposable
{
    public string Dir { get; }
    public AppDatabase Db { get; }
    public FakeLibrary Library { get; }
    public ContentHashCache Hashes { get; }
    public ChapterTextStore Text { get; }
    public SqliteArtifactStore Artifacts { get; }
    public BookRegistry Books { get; }

    public Reader2Fixture()
    {
        Dir = Path.Combine(Path.GetTempPath(), "r2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = Path.Combine(Dir, "app.db"),
                ["Reader2:TextRoot"] = Path.Combine(Dir, "text")
            })
            .Build();

        Db = new AppDatabase(config);
        Library = new FakeLibrary(Path.Combine(Dir, "library"));
        Hashes = new ContentHashCache(Library);
        Text = new ChapterTextStore(config);
        Artifacts = new SqliteArtifactStore(Db);
        Books = new BookRegistry(Db, Library, Hashes, Text);
    }

    /// <summary>Writes a file into the library and enrols it, returning its id.</summary>
    public Task<BookRef> EnrolAsync(
        string fileName, string contents, string lensKey = "literary", string title = "A Book") =>
        EnrolBytesAsync(fileName, System.Text.Encoding.UTF8.GetBytes(contents), lensKey, title);

    /// <inheritdoc cref="EnrolAsync"/>
    public async Task<BookRef> EnrolBytesAsync(
        string fileName, byte[] contents, string lensKey = "literary", string title = "A Book")
    {
        Library.WriteBytes(fileName, contents);
        var book = (await Hashes.GetAsync(fileName))!.Value;
        await Books.EnrolAsync(book, fileName, title, ["An Author"], lensKey);
        return book;
    }

    /// <summary>Enrols a real EPUB and hands back the registry's record of it.</summary>
    public async Task<EnrolledBook> EnrolEpubAsync(byte[] epub, string fileName = "book.epub") =>
        (await Books.GetAsync(await EnrolBytesAsync(fileName, epub)))!;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch { /* temp */ }
    }
}

/// <summary>A library directory under the test's control.</summary>
public sealed class FakeLibrary(string root) : ILibraryBookSource
{
    public string Root { get; } = root;

    public void Write(string fileName, string contents)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, fileName), contents);
    }

    public void WriteBytes(string fileName, byte[] contents)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllBytes(Path.Combine(Root, fileName), contents);
    }

    public void Rename(string from, string to) =>
        File.Move(Path.Combine(Root, from), Path.Combine(Root, to));

    public void Delete(string fileName) => File.Delete(Path.Combine(Root, fileName));

    public IReadOnlyList<string> EnumerateEpubFileNames() =>
        Directory.Exists(Root)
            ? Directory.EnumerateFiles(Root, "*.epub").Select(Path.GetFileName).Select(n => n!).ToArray()
            : [];

    public bool Exists(string fileName) => File.Exists(Path.Combine(Root, fileName));

    public Stream? OpenRead(string fileName)
    {
        var path = Path.Combine(Root, fileName);
        return File.Exists(path) ? File.OpenRead(path) : null;
    }

    public (long Length, DateTime LastWriteUtc)? Stat(string fileName)
    {
        var info = new FileInfo(Path.Combine(Root, fileName));
        return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
    }

    /// <summary>
    /// Covers this fake pretends the library knows about, by file name. Empty by
    /// default, which is the honest answer for a directory a test just made.
    /// </summary>
    public Dictionary<string, string> Covers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? CoverUrl(string fileName, string baseUrl) =>
        Covers.TryGetValue(Path.GetFileName(fileName), out var url) ? $"{baseUrl}{url}" : null;
}

/// <summary>A stand-in artifact payload.</summary>
public sealed record TestPayload(string Text, int Number = 0);

/// <summary>
/// Records progress on the calling thread.
///
/// <para><see cref="Progress{T}"/> marshals its callbacks through a
/// synchronisation context, so a test using it has to sleep and hope. This
/// records synchronously, which makes the assertions exact instead of timing
/// dependent.</para>
/// </summary>
public sealed class ProgressRecorder<T> : IProgress<T>
{
    public List<T> Steps { get; } = [];

    public void Report(T value) => Steps.Add(value);
}
