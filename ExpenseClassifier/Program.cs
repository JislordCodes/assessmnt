using Azure.AI.OpenAI;
using Azure.Identity;
using ExpenseClassifier.Agents;
using ExpenseClassifier.Configuration;
using ExpenseClassifier.Models;
using ExpenseClassifier.Services;
using ExpenseClassifier.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration (strongly typed, validated at startup) ---
builder.Services
    .AddOptions<ExpenseClassifierOptions>()
    .BindConfiguration(ExpenseClassifierOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

var useSimulation = builder.Configuration.GetValue<bool>($"{ExpenseClassifierOptions.SectionName}:UseSimulation");

// --- Dependency Injection ---
// Lifetimes: everything below is stateless or thread-safe and expensive to build, so it is a singleton.
// The agent is a singleton too (it owns pre-built tool schemas, the function-invocation pipeline and the
// per-key lock); it only depends on singletons, so there are no captive dependencies.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<CompanyPolicyTool>();
builder.Services.AddSingleton<SpendingLimitTool>();

if (useSimulation)
{
    builder.Services.AddSingleton<IChatClient, SimulationChatClient>();
}
else
{
    builder.Services
        .AddOptions<AzureOpenAIOptions>()
        .BindConfiguration(AzureOpenAIOptions.SectionName)
        .Validate(o => Uri.TryCreate(o.Endpoint, UriKind.Absolute, out _), "AzureOpenAI:Endpoint must be an absolute URI.")
        .Validate(o => !string.IsNullOrWhiteSpace(o.DeploymentName), "AzureOpenAI:DeploymentName is required.")
        .ValidateOnStart();

    // AzureOpenAIClient is thread-safe and owns the HTTP pipeline/connection pool: one instance for the process.
    // API key when configured (local/testing), otherwise Managed Identity / developer credentials (production).
    builder.Services.AddSingleton(sp =>
    {
        var azure = sp.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;
        var endpoint = new Uri(azure.Endpoint);
        return string.IsNullOrWhiteSpace(azure.ApiKey)
            ? new AzureOpenAIClient(endpoint, new DefaultAzureCredential())
            : new AzureOpenAIClient(endpoint, new System.ClientModel.ApiKeyCredential(azure.ApiKey));
    });

    builder.Services.AddSingleton<IChatClient>(sp =>
    {
        var azure = sp.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;

        // A bare resource URL (https://<res>.openai.azure.com/) uses the classic Azure protocol. Any endpoint with a path
        // (Azure Foundry ".../openai/v1", or another OpenAI-compatible provider such as ".../compatible-mode/v1") speaks the
        // plain OpenAI protocol with the deployment name as `model`; AzureOpenAIClient would rewrite the URL to
        // /deployments/{name}/... and get a 404.
        var endpointUri = new Uri(azure.Endpoint);
        if (endpointUri.AbsolutePath.Trim('/').Length > 0 && !string.IsNullOrWhiteSpace(azure.ApiKey))
        {
            var v1 = new OpenAI.OpenAIClient(
                new System.ClientModel.ApiKeyCredential(azure.ApiKey),
                new OpenAI.OpenAIClientOptions { Endpoint = new Uri(azure.Endpoint) });
            return v1.GetChatClient(azure.DeploymentName).AsIChatClient();
        }

        return sp.GetRequiredService<AzureOpenAIClient>().GetChatClient(azure.DeploymentName).AsIChatClient();
    });
}

builder.Services.AddSingleton<IExpenseClassifierAgent, ExpenseClassifierAgent>();

// --- RFC 7807 error handling ---
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages(); // bodyless 404/405/415 etc. also become ProblemDetails

// --- API Endpoints ---

// Single Expense Classification Endpoint
app.MapPost("/classify", async (
    IExpenseClassifierAgent agent,
    IOptions<ExpenseClassifierOptions> options,
    RequestDto? request,
    CancellationToken cancellationToken) =>
{
    var errors = new Dictionary<string, string[]>();
    ValidateDescription(errors, "description", request?.Description, options.Value.MaxDescriptionLength);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors, title: "Invalid Request Payload");
    }

    var result = await agent.Classify(request!.Description, cancellationToken);
    return Results.Ok(result);
});

// Batch Expense Classification Endpoint
app.MapPost("/classify/batch", async (
    IExpenseClassifierAgent agent,
    IOptions<ExpenseClassifierOptions> options,
    BatchExpenseRequest? request,
    CancellationToken cancellationToken) =>
{
    var settings = options.Value;
    var errors = new Dictionary<string, string[]>();

    if (request?.Items is null || request.Items.Count == 0)
    {
        errors["items"] = ["Batch items list cannot be null or empty."];
    }
    else if (request.Items.Count > settings.MaxBatchItems)
    {
        errors["items"] = [$"A batch may contain at most {settings.MaxBatchItems} items."];
    }
    else
    {
        for (var i = 0; i < request.Items.Count; i++)
        {
            ValidateDescription(errors, $"items[{i}].description", request.Items[i]?.Description, settings.MaxDescriptionLength);
        }
    }

    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors, title: "Invalid Batch Request Payload");
    }

    var result = await agent.ClassifyBatch(request!, cancellationToken);
    return Results.Ok(result);
});

await app.RunAsync();

static void ValidateDescription(Dictionary<string, string[]> errors, string field, string? description, int maxLength)
{
    if (string.IsNullOrWhiteSpace(description))
    {
        errors[field] = ["Expense description must not be empty or whitespace."];
    }
    else if (description.Length > maxLength)
    {
        errors[field] = [$"Expense description must not exceed {maxLength} characters."];
    }
}

/// <summary>Exposes the entry point to <c>WebApplicationFactory</c> for integration tests.</summary>
public partial class Program;
