using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnnasArchive.Tests.Configuration;

/// <summary>
/// <c>appsettings.json</c> is gitignored, so <c>appsettings.Template.json</c> is the only
/// committed record of what this app can be configured with. Ten knobs had drifted out
/// of it — read by code, documented nowhere — which is invisible until someone needs to
/// change one and cannot find out that it exists.
///
/// This closes the loop: adding a new <c>cfg["Section:Key"]</c> without adding it to the
/// template now fails a test rather than quietly creating undocumented configuration.
/// </summary>
public class SettingsTemplateConventionTests
{
    /// <summary>
    /// Sections whose keys are supplied per-deployment rather than templated —
    /// secrets and connection details that belong in the real (gitignored) file.
    /// </summary>
    private static readonly HashSet<string> UntemplatedSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConnectionStrings", "Logging", "Serilog", "AllowedHosts", "Kestrel"
    };

    /// <summary>
    /// Keys the code reads but which are set by the environment or the container, not by
    /// a human editing the template. Each needs a reason, not just an entry.
    /// </summary>
    private static readonly Dictionary<string, string> NotTemplatedByDesign = new(StringComparer.OrdinalIgnoreCase)
    {
        // docker-compose sets these; templating them would suggest editing the file instead.
        ["Jellyfin:BaseUrl"] = "Set by docker-compose from the service name.",
        ["Immich:BaseUrl"] = "Set by docker-compose from the service name."
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "annas-archive-util.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the solution root.");
    }

    private static JsonDocument Template() =>
        JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "AnnasArchive.API", "appsettings.Template.json")));

    private static bool TemplateHas(JsonDocument template, string key)
    {
        JsonElement current = template.RootElement;
        foreach (var segment in key.Split(':'))
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out var next))
                return false;
            current = next;
        }
        return true;
    }

    /// <summary>
    /// Every <c>Section:Key</c> the code reads from configuration must appear in the
    /// template, so the set of things this app can be configured with is discoverable
    /// from the repository alone.
    /// </summary>
    [Fact]
    public void EveryConfigurationKeyTheCodeReadsIsInTheTemplate()
    {
        using var template = Template();

        var sources = Directory.EnumerateFiles(
            Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

        var missing = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in sources)
        {
            foreach (System.Text.RegularExpressions.Match match in Regex.Matches(File.ReadAllText(file), @"""([A-Za-z][A-Za-z0-9]*(?::[A-Za-z][A-Za-z0-9]*)+)"""))
            {
                var key = match.Groups[1].Value;

                // Only strings that are plausibly configuration paths, not arbitrary
                // colon-separated text: the section must be one the template declares.
                var section = key.Split(':')[0];
                if (UntemplatedSections.Contains(section)) continue;
                if (NotTemplatedByDesign.ContainsKey(key)) continue;
                if (!template.RootElement.TryGetProperty(section, out _)) continue;

                if (!TemplateHas(template, key))
                    missing.Add(key);
            }
        }

        missing.Should().BeEmpty(
            "every configuration key the code reads should be documented in " +
            "appsettings.Template.json — it is the only committed record of them");
    }

    /// <summary>
    /// The template must stay parseable. It is hand-edited, and a trailing comma here
    /// takes the whole app down at startup rather than failing anything earlier.
    /// </summary>
    [Fact]
    public void TheTemplateIsValidJson()
    {
        var act = () => Template();

        act.Should().NotThrow();
    }

    /// <summary>
    /// Every reason in <see cref="NotTemplatedByDesign"/> has to say something. An empty
    /// string would let the exemption list grow into a way of silencing the test above.
    /// </summary>
    [Fact]
    public void EveryDeliberateOmissionCarriesAReason()
    {
        NotTemplatedByDesign.Should().OnlyContain(kv => kv.Value.Length > 20);
    }
}
