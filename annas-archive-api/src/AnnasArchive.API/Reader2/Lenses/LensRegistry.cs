using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace AnnasArchive.API.Reader2.Lenses;

/// <summary>A lens registration that cannot work, discovered at boot.</summary>
public sealed class LensConfigurationException(string message) : Exception(message);

/// <summary>The registered book types, indexed by key.</summary>
public interface ILensRegistry
{
    /// <summary>Every lens in picker order.</summary>
    IReadOnlyList<IReaderLens> All { get; }

    /// <summary>The lens a book gets when none is chosen.</summary>
    IReaderLens Default { get; }

    /// <summary>
    /// Looks up a lens. <paramref name="lens"/> is null on a miss rather than the
    /// default — a caller that ignores the result must get a null reference, not
    /// a book quietly read through the wrong lens.
    /// </summary>
    bool TryGet(string? key, [NotNullWhen(true)] out IReaderLens? lens);

    /// <summary>The lens for a key stored in the database.</summary>
    IReaderLens Get(string key);

    /// <summary>
    /// The lens for a key a client asked for: the default when it asked for
    /// nothing, and <c>null</c> when it asked for something that does not exist.
    ///
    /// <para>Distinguishing those two is the whole job. Treating an unknown key
    /// as "nothing" would leave a reader with a book typed differently from what
    /// they picked and no sign anything went wrong.</para>
    /// </summary>
    IReaderLens? ForRequest(string? requestedKey);
}

/// <summary>
/// Every <see cref="IReaderLens"/> DI can find, validated once at construction.
///
/// <para>Validation happens at boot rather than at first use on purpose. A lens
/// that claims to build a story model with no extraction prompt is a mistake in
/// a source file, and the moment to discover it is the deploy, not a reader's
/// click three chapters into a novel.</para>
/// </summary>
public sealed partial class LensRegistry : ILensRegistry
{
    private readonly IReadOnlyDictionary<string, IReaderLens> _byKey;

    public IReadOnlyList<IReaderLens> All { get; }

    /// <summary>
    /// The first lens in picker order.
    ///
    /// <para>Derived rather than a named key, so the order the reader sees and
    /// the type they get by default cannot disagree — and so the registry holds
    /// no opinion about which lens is special, which is what keeps a fourth one
    /// a pure addition.</para>
    /// </summary>
    public IReaderLens Default => All[0];

    public LensRegistry(IEnumerable<IReaderLens> lenses)
    {
        All = lenses.OrderBy(l => l.SortOrder).ThenBy(l => l.Key, StringComparer.Ordinal).ToArray();

        if (All.Count == 0)
            throw new LensConfigurationException("No reader lenses are registered; Reader II cannot start.");

        foreach (var lens in All) Validate(lens);

        // Ordinal is enough: Validate has already forced every key to lowercase, so
        // there is no case-differing duplicate left to find.
        var duplicate = All.GroupBy(l => l.Key, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
            throw new LensConfigurationException(
                $"Two reader lenses claim the key '{duplicate.Key}': " +
                string.Join(", ", duplicate.Select(l => l.GetType().Name)));

        // Lookup, unlike duplicate detection, is case-insensitive: keys arrive from
        // request bodies as well as from our own rows, and "Literary" is a typo
        // rather than a different book type.
        _byKey = All.ToDictionary(l => l.Key, StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGet(string? key, [NotNullWhen(true)] out IReaderLens? lens)
    {
        lens = key is not null && _byKey.TryGetValue(key, out var found) ? found : null;
        return lens is not null;
    }

    public IReaderLens? ForRequest(string? requestedKey) =>
        string.IsNullOrWhiteSpace(requestedKey)
            ? Default
            : TryGet(requestedKey, out var lens) ? lens : null;

    public IReaderLens Get(string key) =>
        TryGet(key, out var lens)
            ? lens
            : throw new KeyNotFoundException($"No reader lens is registered for '{key}'.");

    /// <summary>Keys reach the database and the URL, so they are kept to one shape.</summary>
    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex KeyShape();

    private static void Validate(IReaderLens lens)
    {
        var name = lens.GetType().Name;

        Require(KeyShape().IsMatch(lens.Key), name, $"key '{lens.Key}' must be lowercase kebab-case");
        Require(lens.DisplayName.Trim().Length > 0, name, "has no display name");
        Require(lens.Description.Trim().Length > 0, name, "has no description");
        Require(lens.Icon.Trim().Length > 0, name, "has no icon");
        foreach (var kind in CallKinds.RequiredOfEveryLens)
            Require(
                !string.IsNullOrWhiteSpace(lens.Prompts[kind]),
                name, $"has no {kind} prompt");

        // Every prompt, not just the required ones: a version of zero on the
        // optional story prompt would make its artifacts un-stale-able forever,
        // which is a silent failure rather than a loud one.
        foreach (var kind in CallKinds.Lens)
            Require(
                lens.Versions[kind] >= 1,
                name, $"must have a {kind} prompt version of at least 1");

        // Both directions. A story prompt on a lens that builds no story model is
        // dead text that reads as a working feature.
        Require(
            lens.BuildsStoryModel == !string.IsNullOrWhiteSpace(lens.Prompts.StoryExtraction),
            name,
            lens.BuildsStoryModel
                ? "builds a story model but supplies no StoryExtraction prompt"
                : "supplies a StoryExtraction prompt but does not build a story model");

        Require(
            lens.BuildsStoryModel == (lens.StoryVocabulary is not null),
            name,
            lens.BuildsStoryModel
                ? "builds a story model but supplies no StoryVocabulary"
                : "supplies a StoryVocabulary but does not build a story model");
    }

    private static void Require(bool condition, string lensName, string problem)
    {
        if (!condition) throw new LensConfigurationException($"Reader lens {lensName} {problem}.");
    }
}
