namespace AnnasArchive.API.Extensions;

/// <summary>
/// Extension methods for safe configuration access.
/// </summary>
public static class ConfigurationExtensions
{
    /// <summary>
    /// Gets a required configuration value. Throws if the key is not found or the value is null.
    /// </summary>
    /// <typeparam name="T">The type to retrieve.</typeparam>
    /// <param name="config">The configuration instance.</param>
    /// <param name="key">The configuration key.</param>
    /// <returns>The configuration value.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the configuration key is missing or null.</exception>
    public static T GetRequiredValue<T>(this IConfiguration config, string key)
    {
        var value = config.GetValue<T>(key);
        if (value == null)
            throw new InvalidOperationException($"Configuration key '{key}' is required but was not found or is null.");
        return value;
    }

    /// <summary>
    /// Gets a configuration string value or returns an empty string if not found.
    /// </summary>
    /// <param name="config">The configuration instance.</param>
    /// <param name="key">The configuration key.</param>
    /// <returns>The configuration value or empty string.</returns>
    public static string GetValueOrEmpty(this IConfiguration config, string key)
    {
        return config[key] ?? string.Empty;
    }

    /// <summary>
    /// Gets a configuration string value or returns a default value if not found.
    /// </summary>
    /// <param name="config">The configuration instance.</param>
    /// <param name="key">The configuration key.</param>
    /// <param name="defaultValue">The default value to return if the key is not found.</param>
    /// <returns>The configuration value or the default value.</returns>
    public static string GetValueOrDefault(this IConfiguration config, string key, string defaultValue)
    {
        return config[key] ?? defaultValue;
    }

    /// <summary>
    /// Tries to get a configuration value, returning false if not found.
    /// </summary>
    /// <typeparam name="T">The type to retrieve.</typeparam>
    /// <param name="config">The configuration instance.</param>
    /// <param name="key">The configuration key.</param>
    /// <param name="value">The output value if found.</param>
    /// <returns>True if the value was found, false otherwise.</returns>
    public static bool TryGetValue<T>(this IConfiguration config, string key, out T? value)
    {
        value = config.GetValue<T>(key);
        return value != null;
    }
}
