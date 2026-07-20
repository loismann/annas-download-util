using Serilog;

namespace AnnasArchive.API.Configuration;

/// <summary>
/// Validates required configuration at application startup.
/// Provides fail-fast behavior with clear error messages for missing configuration.
/// </summary>
public static class StartupValidation
{
    /// <summary>
    /// Configuration key definitions with descriptions for error messages.
    /// </summary>
    private static readonly (string Key, string Description, bool Required)[] ConfigKeys =
    {
        // Authentication (required)
        ("Auth:JwtSecret", "JWT signing key for authentication", true),

        // AI features (required for AI functionality)
        ("OpenAI:ApiKey", "OpenAI API key for AI features", true),

        // Dropbox integration (required for reader functionality)
        ("Dropbox:AppKey", "Dropbox application key", true),
        ("Dropbox:AppSecret", "Dropbox application secret", true),
        ("Dropbox:RefreshToken", "Dropbox refresh token for authentication", true),

        // Anna's Archive downloads (required for download functionality)
        ("Anna:MemberKey", "Anna's Archive member key for downloads", true),

        // Email features (optional - app works without email)
        ("Email:SmtpServer", "SMTP server for email notifications", false),
        ("Email:SenderEmail", "Email sender address", false),
        ("Email:SenderPassword", "Email sender password", false),
    };

    /// <summary>
    /// Validates all required configuration keys are present.
    /// Logs warnings for missing optional keys and throws for missing required keys.
    /// Skips validation in test environment.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <exception cref="InvalidOperationException">Thrown when required configuration is missing.</exception>
    public static void ValidateConfiguration(IConfiguration configuration)
    {
        // Skip validation in test environment
        if (IsTestEnvironment())
        {
            Log.Information("Skipping configuration validation in test environment");
            return;
        }

        var missingRequired = new List<string>();
        var missingOptional = new List<string>();

        foreach (var (key, description, required) in ConfigKeys)
        {
            var value = configuration[key];
            if (string.IsNullOrWhiteSpace(value))
            {
                if (required)
                {
                    missingRequired.Add($"  - {key}: {description}");
                }
                else
                {
                    missingOptional.Add($"  - {key}: {description}");
                }
            }
        }

        // Log warnings for missing optional config
        if (missingOptional.Count > 0)
        {
            Log.Warning("Missing optional configuration (some features may be unavailable):\n{MissingKeys}",
                string.Join("\n", missingOptional));
        }

        // Fail fast for missing required config
        if (missingRequired.Count > 0)
        {
            var errorMessage = $"Missing required configuration:\n{string.Join("\n", missingRequired)}\n\n" +
                "Please ensure all required keys are set in appsettings.json or environment variables.";

            Log.Fatal("Application startup failed - {ErrorMessage}", errorMessage);
            throw new InvalidOperationException(errorMessage);
        }

        Log.Information("Configuration validation passed - all required keys present");
    }

    /// <summary>
    /// Extension method to add configuration validation to the application builder.
    /// </summary>
    public static WebApplicationBuilder ValidateRequiredConfiguration(this WebApplicationBuilder builder)
    {
        ValidateConfiguration(builder.Configuration);
        return builder;
    }

    /// <summary>
    /// Checks if we're running in a test environment.
    /// </summary>
    private static bool IsTestEnvironment()
    {
        // Check environment variable
        var isTestEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Test";

        // Check if running under test host
        var isTestHost = AppDomain.CurrentDomain.GetAssemblies()
            .Any(a => a.FullName?.Contains("testhost") == true ||
                      a.FullName?.Contains("xunit") == true);

        return isTestEnv || isTestHost;
    }
}
