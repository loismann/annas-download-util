using Serilog;
using Serilog.Events;

namespace AnnasArchive.API.Configuration;

/// <summary>
/// Extension methods for configuring Serilog logging.
/// </summary>
public static class LoggingConfiguration
{
    /// <summary>
    /// Configures Serilog with console and file sinks.
    /// </summary>
    public static WebApplicationBuilder AddSerilogLogging(this WebApplicationBuilder builder)
    {
        var logPath = builder.Configuration["Logging:FilePath"] ?? "logs/annas-api-.log";
        var seqUrl = builder.Configuration["Logging:SeqUrl"];

        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore.Authentication", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithEnvironmentName()
            .Enrich.WithThreadId()
            .Enrich.WithProperty("Application", "AnnasArchive.API")
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {CorrelationId} {SourceContext} {Message:lj}{NewLine}{Exception}");

        // Optional — only wired up when a Seq URL is configured (set via
        // Logging__SeqUrl in docker-compose). Local dev without a Seq
        // container running just skips this sink entirely rather than
        // failing or spamming connection-refused noise.
        if (!string.IsNullOrWhiteSpace(seqUrl))
        {
            loggerConfig = loggerConfig.WriteTo.Seq(seqUrl);
        }

        Log.Logger = loggerConfig.CreateLogger();

        builder.Host.UseSerilog();

        return builder;
    }
}
