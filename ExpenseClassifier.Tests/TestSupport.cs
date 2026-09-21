using System.Runtime.CompilerServices;
using ExpenseClassifier.Agents;
using ExpenseClassifier.Configuration;
using ExpenseClassifier.Services;
using ExpenseClassifier.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ExpenseClassifier.Tests;

/// <summary>
/// Wraps a real <see cref="IChatClient"/> (or a script) and records every call so tests can assert
/// how many LLM invocations happened, with or without tools, and how many ran at once.
/// </summary>
public sealed class RecordingChatClient(
    Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> handler) : IChatClient
{
    private int _calls, _withTools, _withoutTools, _inFlight, _maxInFlight;

    public int Calls => _calls;
    public int CallsWithTools => _withTools;
    public int CallsWithoutTools => _withoutTools;
    public int MaxInFlight => _maxInFlight;
    public List<string> UserMessages { get; } = [];

    public static RecordingChatClient Over(IChatClient inner) =>
        new((m, o, ct) => inner.GetResponseAsync(m, o, ct));

    public static RecordingChatClient Reply(string json) =>
        new((_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json))));

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Interlocked.Increment(ref _calls);
        if (options?.Tools is { Count: > 0 }) Interlocked.Increment(ref _withTools); else Interlocked.Increment(ref _withoutTools);
        lock (UserMessages) UserMessages.Add(list.Last(m => m.Role == ChatRole.User).Text);

        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while (now > (seen = _maxInFlight) && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen) { }
        try { return await handler(list, options, cancellationToken); }
        finally { Interlocked.Decrement(ref _inFlight); }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var r = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, r.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

public static class Factory
{
    public static ExpenseClassifierAgent CreateAgent(
        IChatClient client, ExpenseClassifierOptions? options = null, IMemoryCache? cache = null) =>
        new(client, new CompanyPolicyTool(), new SpendingLimitTool(),
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ExpenseClassifierAgent>.Instance,
            Microsoft.Extensions.Options.Options.Create(options ?? new ExpenseClassifierOptions()));

    public static RecordingChatClient Simulated() => RecordingChatClient.Over(new SimulationChatClient());

    public static string Json(string category, decimal? amount = 100, string? currency = "USD", string status = "Compliant") =>
        $$"""{"category":"{{category}}","subCategory":"Sub","extractedAmount":{{(amount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")}},"currency":{{(currency is null ? "null" : $"\"{currency}\"")}},"merchant":"M","confidenceScore":0.9,"complianceStatus":"{{status}}","complianceNotes":"n"}""";
}
