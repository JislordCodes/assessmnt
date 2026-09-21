using System.ComponentModel.DataAnnotations;

namespace ExpenseClassifier.Configuration;

/// <summary>
/// Strongly typed settings for the classification pipeline (section "ExpenseClassifier").
/// </summary>
public sealed class ExpenseClassifierOptions
{
    public const string SectionName = "ExpenseClassifier";

    /// <summary>Use the offline deterministic <c>SimulationChatClient</c> instead of Azure OpenAI.</summary>
    public bool UseSimulation { get; set; } = true;

    /// <summary>Absolute lifetime of a cached classification.</summary>
    [Range(1, 24 * 60)]
    public int CacheDurationMinutes { get; set; } = 30;

    /// <summary>Maximum number of concurrent LLM classifications inside one batch (protects upstream rate limits).</summary>
    [Range(1, 64)]
    public int MaxBatchConcurrency { get; set; } = 5;

    /// <summary>Maximum number of line items accepted in one batch request.</summary>
    [Range(1, 1000)]
    public int MaxBatchItems { get; set; } = 200;

    /// <summary>Maximum accepted length of one expense description.</summary>
    [Range(10, 10_000)]
    public int MaxDescriptionLength { get; set; } = 2_000;

    /// <summary>Upper bound for a single LLM stage (including tool round-trips).</summary>
    [Range(1, 300)]
    public int LlmTimeoutSeconds { get; set; } = 45;
}

/// <summary>
/// Azure OpenAI connection settings (section "AzureOpenAI").
/// Leave <see cref="ApiKey"/> empty to authenticate with Managed Identity / developer credentials instead.
/// </summary>
public sealed class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";

    public string Endpoint { get; set; } = string.Empty;

    public string? ApiKey { get; set; }

    public string DeploymentName { get; set; } = "gpt-4o";
}
