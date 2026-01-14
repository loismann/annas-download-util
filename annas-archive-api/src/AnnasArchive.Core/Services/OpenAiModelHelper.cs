using System;
using System.Collections.Generic;
using Serilog;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Service for OpenAI model-specific logic and payload building
/// </summary>
public class OpenAiModelHelper : IOpenAiModelHelper
{
    /// <summary>
    /// Checks if the model is part of the GPT-5 family (gpt-5, gpt-5.2, gpt-5-mini, etc.)
    /// </summary>
    public bool IsGpt5Family(string model) =>
        model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks if the model is part of the o1 family (o1-mini, o1-preview, o3, etc.)
    /// </summary>
    public bool IsO1Family(string model) =>
        model.StartsWith("o1-", StringComparison.OrdinalIgnoreCase) ||
        model.Equals("o3", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a Chat Completions API payload with model-specific parameters.
    /// Handles GPT-5.2, o1, and GPT-4 model families correctly.
    /// </summary>
    public object BuildChatCompletionPayload(
        string model,
        object[] messages,
        int? maxCompletionTokens = null,
        double? temperature = null,
        string? reasoningEffort = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages
        };

        if (maxCompletionTokens.HasValue)
        {
            payload["max_completion_tokens"] = maxCompletionTokens.Value;
        }

        // Handle temperature and reasoning based on model family
        if (IsGpt5Family(model))
        {
            // GPT-5 family: temperature only works with reasoning_effort = "none"
            if (temperature.HasValue)
            {
                payload["reasoning_effort"] = "none";
                payload["temperature"] = temperature.Value;
                Log.Information("GPT-5 model: Using temperature={Temperature} with reasoning_effort=none", temperature.Value);
            }
            else if (!string.IsNullOrWhiteSpace(reasoningEffort))
            {
                payload["reasoning_effort"] = reasoningEffort;
                Log.Information("GPT-5 model: Using reasoning_effort={ReasoningEffort} (no temperature)", reasoningEffort);
            }
            else
            {
                // Default to "none" for GPT-5.2
                payload["reasoning_effort"] = "none";
                Log.Information("GPT-5 model: Using default reasoning_effort=none");
            }
        }
        else if (IsO1Family(model))
        {
            // o1 family: no temperature support, reasoning is built-in
            Log.Information("o1 model: No temperature or reasoning_effort parameters");
            // o1 models don't support temperature, top_p, or explicit reasoning_effort
        }
        else
        {
            // GPT-4 and earlier: standard temperature support
            if (temperature.HasValue)
            {
                payload["temperature"] = temperature.Value;
                Log.Information("GPT-4 model: Using temperature={Temperature}", temperature.Value);
            }
        }

        return payload;
    }

    /// <summary>
    /// Gets a user-friendly description of model capabilities
    /// </summary>
    public string GetModelDescription(string model)
    {
        if (IsGpt5Family(model))
            return "GPT-5 family (supports reasoning_effort, temperature with effort=none)";
        if (IsO1Family(model))
            return "o1 family (built-in reasoning, no temperature support)";
        return "GPT-4 or earlier (standard temperature support)";
    }
}
