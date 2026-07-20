namespace AnnasArchive.Core.Services;

/// <summary>
/// Service for selecting appropriate OpenAI models based on task requirements
/// </summary>
public interface IModelSelectionService
{
    /// <summary>
    /// Gets the model for deep reasoning tasks (e.g., analysis, synthesis).
    /// Priority: Config -> Environment Variable -> Default (gpt-5.2)
    /// </summary>
    string GetModelDeep();

    /// <summary>
    /// Gets the model for fast processing tasks (e.g., quick summaries, simple parsing).
    /// Priority: Config -> Environment Variable -> Default (gpt-4o)
    /// </summary>
    string GetModelFast();
}
