using FluentAssertions;

namespace AnnasArchive.Tests.Configuration;

/// <summary>
/// The compose file has no other test, and its failures are quiet ones.
///
/// <para>A service that loses its restart policy does not error — it starts
/// normally, runs normally, and then simply never comes back after the next NAS
/// reboot. That is the kind of thing worth a cheap guard, especially now that the
/// policy is inherited from an anchor rather than written on each service, so
/// omitting it looks like nothing at all.</para>
///
/// <para>Deliberately line-based rather than a real YAML parse: the test project
/// has no YAML dependency, and taking one on for two structural checks would cost
/// more than it returns. These read the file the way a person skimming it would.</para>
/// </summary>
public sealed class DockerComposeConventionTests
{
    private const string RestartAnchor = "<<: *service-defaults";
    private const string EnvAnchor = "<<: *linuxserver-env";

    private static string ComposePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "annas-archive-util.sln")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the test must run from inside the repository");

        var compose = Path.Combine(dir!.Parent!.FullName, "docker-compose.yml");
        File.Exists(compose).Should().BeTrue($"expected the compose file at {compose}");
        return compose;
    }

    private static string[] Lines() => File.ReadAllLines(ComposePath());

    /// <summary>Service name to the lines that make up its block.</summary>
    private static Dictionary<string, List<string>> Services()
    {
        var services = new Dictionary<string, List<string>>();
        var inServices = false;
        string? current = null;

        foreach (var line in Lines())
        {
            if (line.StartsWith("services:")) { inServices = true; continue; }
            if (!inServices) continue;

            // A top-level key ends the services block.
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#')) break;

            // Exactly two spaces then a name and a colon is a service.
            if (line.Length > 2 && line[0] == ' ' && line[1] == ' ' && line[2] != ' '
                && !line.TrimStart().StartsWith('#') && line.TrimEnd().EndsWith(':'))
            {
                current = line.Trim().TrimEnd(':');
                services[current] = [];
                continue;
            }

            if (current is not null) services[current].Add(line);
        }

        return services;
    }

    [Fact]
    public void TheComposeFileStillDefinesEveryService() =>
        Services().Should().HaveCount(19,
            "if this count changes deliberately, update it — the number is here so that a " +
            "parse that silently stops finding services fails instead of passing vacuously");

    /// <summary>
    /// Every service gets a restart policy, whether inherited or written out. A
    /// container without one is a container that does not survive a reboot, and
    /// nothing about it looks broken until the reboot happens.
    /// </summary>
    [Fact]
    public void EveryServiceHasARestartPolicy()
    {
        var missing = Services()
            .Where(s => !s.Value.Any(l => l.Contains(RestartAnchor) || l.TrimStart().StartsWith("restart:")))
            .Select(s => s.Key)
            .OrderBy(x => x)
            .ToList();

        missing.Should().BeEmpty(
            $"add `{RestartAnchor}` to inherit unless-stopped, or an explicit restart: if this " +
            "service genuinely needs a different policy");
    }

    /// <summary>
    /// The anchors have to be referenced to be doing anything. An anchor that
    /// nothing uses is not a shared default, it is a comment that looks like code.
    /// </summary>
    [Fact]
    public void BothAnchorsAreActuallyUsed()
    {
        var text = string.Join('\n', Lines());

        text.Should().Contain("x-service-defaults: &service-defaults");
        text.Should().Contain("x-linuxserver-env: &linuxserver-env");

        CountOf(text, RestartAnchor).Should().Be(19, "every service inherits the restart policy");
        CountOf(text, EnvAnchor).Should().Be(8, "the eight LinuxServer.io images share PUID/PGID/TZ");
    }

    /// <summary>
    /// The trio must not be written out again alongside the anchor that supplies
    /// it. A service that re-declares PUID locally is one whose value can drift
    /// from the other seven without anything noticing.
    /// </summary>
    [Fact]
    public void NoServiceRedeclaresTheSharedOwnershipVariables()
    {
        var offenders = new List<string>();

        foreach (var (name, body) in Services())
        {
            foreach (var key in new[] { "PUID", "PGID", "TZ" })
            {
                if (body.Any(l => l.TrimStart().StartsWith($"- {key}=")
                               || l.TrimStart().StartsWith($"{key}:")))
                {
                    offenders.Add($"{name} redeclares {key}");
                }
            }
        }

        offenders.Should().BeEmpty($"these come from `{EnvAnchor}`; a local copy is free to drift");
    }

    /// <summary>
    /// YAML merge keys are shallow, so the ownership trio has to be merged inside
    /// <c>environment:</c>. Merged at the service level instead, any service that
    /// also declares its own <c>environment</c> would replace the block wholesale
    /// and silently lose PUID/PGID/TZ — the container then writes files as root
    /// and its volume permissions quietly rot.
    /// </summary>
    [Fact]
    public void TheOwnershipAnchorIsMergedInsideEnvironment()
    {
        var lines = Lines();

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(EnvAnchor)) continue;

            var indent = lines[i].Length - lines[i].TrimStart().Length;
            var parent = Enumerable.Range(0, i).Reverse()
                .Select(j => lines[j])
                .FirstOrDefault(l => l.Trim().Length > 0
                                  && (l.Length - l.TrimStart().Length) < indent);

            parent.Should().NotBeNull();
            parent!.Trim().Should().Be("environment:",
                $"line {i + 1} merges the ownership anchor somewhere other than inside environment:");
        }
    }

    /// <summary>
    /// No host path under a home directory may be written into the committed
    /// compose file.
    ///
    /// <para>A path like <c>/home/someone/Media</c> is a username, and this file is
    /// tracked. It was there 12 times; the media mounts now build from
    /// <c>${MEDIA_ROOT}</c>, whose real value lives in the gitignored
    /// <c>.env</c>.</para>
    ///
    /// <para>Checked as a pattern rather than one name, so it also catches the next
    /// person's home directory, not just the one that was cleaned up.</para>
    /// </summary>
    [Fact]
    public void NoHomeDirectoryPathIsCommittedInTheComposeFile()
    {
        var offenders = Lines()
            .Select((line, i) => (line, number: i + 1))
            .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.line, @"/(home|Users)/[A-Za-z0-9._-]+/"))
            .Select(x => $"line {x.number}: {x.line.Trim()}")
            .ToList();

        offenders.Should().BeEmpty(
            "a home-directory path embeds a username in a committed file; put the real " +
            "path in .env and reference it, the way ${MEDIA_ROOT} does");
    }

    /// <summary>
    /// Every variable used <b>without a default</b> must be documented in
    /// <c>.env.example</c>.
    ///
    /// <para>The distinction matters. <c>${IMMICH_DB_NAME:-immich}</c> resolves
    /// fine on a machine that has never heard of it. <c>${MEDIA_ROOT}</c> resolves
    /// to the <em>empty string</em> — so a volume line becomes <c>/data</c> instead
    /// of <c>/srv/media:/data</c>, and the container starts happily against the
    /// wrong directory. That is the failure this guards, so requiring
    /// documentation for the defaulted ones too would only add noise.</para>
    /// </summary>
    [Fact]
    public void EveryComposeVariableWithoutADefaultIsDocumentedInTheEnvExample()
    {
        var composeDir = Path.GetDirectoryName(ComposePath())!;
        var envExample = Path.Combine(composeDir, ".env.example");
        File.Exists(envExample).Should().BeTrue();

        var documented = File.ReadAllLines(envExample)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains('='))
            .Select(l => l[..l.IndexOf('=')].Trim())
            .ToHashSet();

        // `${NAME}` — no ':-' or ':?' modifier, so nothing covers an unset value.
        var required = System.Text.RegularExpressions.Regex
            .Matches(string.Join('\n', Lines()), @"\$\{([A-Z][A-Z0-9_]*)\}")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        required.Should().NotBeEmpty("the compose file is expected to use .env indirection");
        required.Should().Contain("MEDIA_ROOT", "the media mounts are built from it");

        required.Except(documented).Should().BeEmpty(
            "a variable with no default resolves to the empty string when unset, which " +
            "turns a volume line into a container-only path and starts the service against " +
            "the wrong directory rather than failing");
    }

    /// <summary>
    /// The services whose image must be pinned to an exact digest, and why.
    ///
    /// <para>Pinning is deliberately <b>not</b> applied to everything. These are the
    /// ones where a surprise update is expensive or hard to notice; the rest are
    /// left floating so they keep getting patched without anyone doing anything.</para>
    /// </summary>
    private static readonly Dictionary<string, string> MustBePinned = new()
    {
        ["gluetun"] =
            "the VPN gateway. A bad update drops the tunnel, and its own firewall then " +
            "blocks traffic rather than leaking it — so the symptom is 'downloads stopped' " +
            "with nothing visibly broken",
        ["gluetun-torrent"] = "same image, same reasoning, second instance",

        ["sonarr"] = "shares config schemas with the rest of the *arr stack and with its own on-disk DB",
        ["radarr"] = "same",
        ["prowlarr"] = "same — and it is what feeds indexers to the other two",
        ["qbittorrent"] = "network_mode joins it to gluetun; a surprise change here is a silent no-download",
        ["sabnzbd"] = "same",

        ["listenarr"] = "runs a canary build; the tag itself is not stable",
        ["immich-postgres"] = "Immich requires this exact image, extensions and all",
        ["immich-redis"] = "pinned alongside the rest of the Immich stack",
    };

    /// <summary>
    /// Every service on the list above still carries a digest.
    ///
    /// <para>Dropping one back to a floating tag is a one-character edit that looks
    /// like nothing and is not visible until an unrelated update breaks something.</para>
    /// </summary>
    [Fact]
    public void TheImagesThatMustBePinnedStillAre()
    {
        var unpinned = Services()
            .Where(s => MustBePinned.ContainsKey(s.Key))
            .Where(s => !s.Value.Any(l => l.Contains("image:") && l.Contains("@sha256:")))
            .Select(s => $"{s.Key} ({MustBePinned[s.Key]})")
            .OrderBy(x => x)
            .ToList();

        unpinned.Should().BeEmpty("these are pinned on purpose; see the header comment in docker-compose.yml");
    }

    /// <summary>
    /// Every pinned service is on the list, so a pin cannot arrive without a
    /// recorded reason. Pinning has a real cost — the image stops receiving
    /// patches until someone refreshes the digest by hand — so it should be a
    /// decision, not a habit.
    /// </summary>
    [Fact]
    public void NothingIsPinnedWithoutARecordedReason()
    {
        var undocumented = Services()
            .Where(s => s.Value.Any(l => l.Contains("image:") && l.Contains("@sha256:")))
            .Select(s => s.Key)
            .Where(name => !MustBePinned.ContainsKey(name))
            .OrderBy(x => x)
            .ToList();

        undocumented.Should().BeEmpty(
            "add it to MustBePinned with the reason, or leave it floating so it keeps " +
            "getting patched");
    }

    private static int CountOf(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
