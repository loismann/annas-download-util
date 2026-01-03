namespace AnnasArchive.Core.Services;

/// <summary>
/// Service for OpenAI model-specific logic and payload building
/// </summary>
public interface IOpenAiModelHelper
{
    /// <summary>
    /// Checks if the model is part of the GPT-5 family (gpt-5, gpt-5.2, gpt-5-mini, etc.)
    /// </summary>
    bool IsGpt5Family(string model);

    /// <summary>
    /// Checks if the model is part of the o1 family (o1-mini, o1-preview, o3, etc.)
    /// </summary>
    bool IsO1Family(string model);

    /// <summary>
    /// Builds a Chat Completions API payload with model-specific parameters.
    /// Handles GPT-5.2, o1, and GPT-4 model families correctly.
    /// </summary>
    object BuildChatCompletionPayload(
        string model,
        object[] messages,
        int? maxCompletionTokens = null,
        double? temperature = null,
        string? reasoningEffort = null);

    /// <summary>
    /// Gets a user-friendly description of model capabilities
    /// </summary>
    string GetModelDescription(string model);
}
