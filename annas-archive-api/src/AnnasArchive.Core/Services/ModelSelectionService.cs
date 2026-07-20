using System;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Service for selecting appropriate OpenAI models based on task requirements
/// </summary>
public class ModelSelectionService : IModelSelectionService
{
    private readonly IConfiguration _configuration;

    public ModelSelectionService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Gets the model for deep reasoning tasks (e.g., analysis, synthesis).
    /// Priority: Config -> Environment Variable -> Default (gpt-5.2)
    /// </summary>
    public string GetModelDeep() =>
        _configuration["OpenAI:ModelDeep"]
        ?? Environment.GetEnvironmentVariable("OPENAI_MODEL_DEEP")
        ?? "gpt-5.2";

    /// <summary>
    /// Gets the model for fast processing tasks (e.g., quick summaries, simple parsing).
    /// Priority: Config -> Environment Variable -> Default (gpt-4o)
    /// </summary>
    public string GetModelFast() =>
        _configuration["OpenAI:ModelFast"]
        ?? Environment.GetEnvironmentVariable("OPENAI_MODEL_FAST")
        ?? "gpt-4o";
}
