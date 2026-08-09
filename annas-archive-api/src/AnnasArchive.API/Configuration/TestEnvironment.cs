namespace AnnasArchive.API.Configuration;

/// <summary>
/// "Are we running under test?", asked by the three startup paths that must not
/// reach the network: health checks, Dropbox client construction, and startup
/// configuration validation.
///
/// It lived as three private copies of the same method — two byte-identical, the
/// third the same minus the configuration flag. Three copies of a predicate that
/// decides whether startup talks to the internet is three chances for them to
/// disagree, and the disagreement would only ever show up as a test that hangs.
/// </summary>
internal static class TestEnvironment
{
    /// <summary>
    /// True when the app is being hosted by a test run.
    /// </summary>
    /// <param name="configuration">
    /// Optional. Supplies the <c>Testing:DisableHealthChecks</c> opt-out; pass
    /// null where no configuration is available, which simply skips that check.
    /// </param>
    internal static bool IsTest(IConfiguration? configuration = null)
    {
        // Explicit opt-out, for a test host that wants the rest of startup.
        var isTestConfig = configuration?.GetValue<bool>("Testing:DisableHealthChecks") ?? false;

        var isTestEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Test";

        // The reliable signal: xunit and the VSTest host are only ever loaded by
        // a test run, whatever the environment happens to be called.
        var isTestHost = AppDomain.CurrentDomain.GetAssemblies()
            .Any(a => a.FullName?.Contains("testhost") == true ||
                      a.FullName?.Contains("xunit") == true);

        return isTestConfig || isTestEnv || isTestHost;
    }
}
