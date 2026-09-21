# Candidate Implementation Notes & Architecture Summary

Please use this document to explain your technical design decisions, trade-offs, and scaling considerations. This provides the evaluation committee with direct insight into your engineering thought process.

**Status:** `dotnet build` = 0 warnings / 0 errors. `dotnet test` = 88 passing (unit tests plus in-memory HTTP integration tests, all offline via `SimulationChatClient`).

---

## 1. Multi-Stage AI Agent Architecture & Orchestration

* **Describe how you structured and initialized `ExpenseClassifierAgent`.**

  `ExpenseClassifierAgent` is a singleton that implements `IExpenseClassifierAgent`. All expensive setup happens exactly once, in its constructor, and the results are shared read-only across requests:
  - the two `AIFunction` tools (reflection and JSON-schema generation),
  - the stage-1 client, built as `chatClient.AsBuilder().UseFunctionInvocation(...).Build()`,
  - the stage-2 client (the raw `IChatClient`, because it has no tools),
  - both system prompts (static strings),
  - the JSON response schema (`ChatResponseFormat.ForJsonSchema<LlmClassification>`, a static field),
  - the cache TTL, LLM timeout and batch concurrency, read once from `IOptions<ExpenseClassifierOptions>`.

  Per request, only a small `ChatOptions` object and the message array are allocated.

  Flow of `Classify`: validate and trim -> build normalized cache key -> cache lookup -> (miss) take the per-key lock -> re-check cache -> Stage 1 -> Stage 2 if needed -> validate/normalize the model output -> store in cache -> return.

* **How did you register and bind `CompanyPolicyTool` and `SpendingLimitTool` to the primary agent?**

  Both tools are registered as singletons in DI and injected into the agent. In the constructor each is bound once with `AIFunctionFactory.Create(policyTool.GetPolicy, "get_company_policy")` and `AIFunctionFactory.Create(spendingLimitTool.GetSpendingLimits, "get_spending_limits")`. The `[Description]` attributes on the methods become the tool descriptions the model sees. The two `AIFunction` objects are stored in a list and passed as `ChatOptions.Tools` for Stage 1 only. `ToolMode = RequireAny` forces the model to call a tool, and the system prompt tells it to call both. The category and compliance verdict are therefore grounded in the policy text rather than the model's memory. `FunctionInvokingChatClient` runs the model -> tool call -> tool result -> model loop, capped at 5 iterations so it cannot loop forever.

* **How did you ensure thread-safety, avoid per-request reflection overhead, and manage the transition/fallback to the secondary general agent?**

  - *Thread-safety:*
    - The agent holds only immutable state after construction.
    - `IMemoryCache` is thread-safe.
    - Same-key concurrency goes through a `KeyedAsyncLock`.
    - Batch workers each write to their own slot of a pre-sized array.
    - Cache entries are never handed out directly: the DTO is mutable, so the cache stores a private clone and every hit returns another clone.
  - *No per-request reflection:* tool schemas, the response JSON schema, the prompts and the middleware pipeline are all built once (constructor or static fields).
  - *Fallback:* Stage 2 runs when Stage 1 returns "Other" (case-insensitive), an empty category, a category outside the six official ones, or malformed output. Stage 2 uses a general-knowledge prompt and no tools. If Stage 2 is unusable but Stage 1 did answer "Other", the Stage 1 answer is returned rather than an error. Only if both stages are malformed does the agent throw `ClassificationFailedException` (502). The result is stamped `PolicyAgentWithTools` or `FallbackGeneralAgent` by code, never by the model.

* **How did you achieve strictly typed, deterministic structured output (`ResponseDto`) from both stages?**

  1. **Constrained generation:** `Temperature = 0` plus `ResponseFormat = ChatResponseFormat.ForJsonSchema<LlmClassification>()`, so the model is asked for JSON that matches a typed schema with per-field descriptions. Both stages use the same schema.
  2. **Defensive parsing:** case-insensitive, tolerant of numbers-as-strings, and tolerant of markdown-fenced JSON. Anything unparseable is treated as "malformed" (fallback or 502), never cached.
  3. **Validation layer (`BuildResponse`), because the model is not trusted:**
     - category and compliance status are canonicalized against whitelists (an invented status becomes `Unverifiable`);
     - currency symbols are mapped to ISO-4217 (`₦`->NGN, `$`->USD, ...) and unknown codes become null;
     - confidence is clamped to 0..1;
     - non-positive amounts are dropped;
     - **a claim with no amount or no currency can never be `Compliant`** and is forced to `Unverifiable`, which covers the missing-amount and currency edge cases;
     - `ClassificationStage` and `ExpenseDescription` are stamped by code.
  4. **Prompt-injection defense:** the raw description is the only user message and the system prompt declares it untrusted data.

  *Trade-off:* the compliance verdict is the model's judgement over the tool text, not a hard-coded rule engine. The README's own example expects a $650 annual licence to be `Compliant`, which a strict rule engine would contradict. A deterministic, escalate-only limit checker is on the roadmap (section 5).

---

## 2. Dependency Injection & Service Lifetime Strategy

* **Summarize your DI configuration in `Program.cs`.**

  - Options: `ExpenseClassifierOptions` (cache TTL, batch concurrency, max batch items, max description length, LLM timeout) and `AzureOpenAIOptions` are bound from configuration with `ValidateDataAnnotations()` and `ValidateOnStart()`, so bad config fails at boot instead of on the first request.
  - `AddMemoryCache()`, `CompanyPolicyTool`, `SpendingLimitTool` registered as singletons.
  - `IChatClient` is either `SimulationChatClient` or an Azure OpenAI chat client, chosen by `ExpenseClassifier:UseSimulation`.
  - `IExpenseClassifierAgent` -> `ExpenseClassifierAgent`, singleton.
  - `AddProblemDetails()` and `AddExceptionHandler<ApiExceptionHandler>()` for RFC 7807 errors, with `UseExceptionHandler()` and `UseStatusCodePages()`.
  - Authentication: an API key when `AzureOpenAI:ApiKey` is set (local and test), otherwise `DefaultAzureCredential` (Managed Identity in Azure, developer login locally). Keys are supplied through environment variables or user-secrets and never committed.

* **What lifetimes did you choose for `AzureOpenAIClient`, `IChatClient`, `CompanyPolicyTool`, `SpendingLimitTool`, and `IExpenseClassifierAgent`, and why?**

  | Service | Lifetime | Why |
  |---|---|---|
  | `AzureOpenAIClient` | Singleton | Thread-safe and owns the HTTP pipeline and connection pool. The starter registered it `Scoped`, which rebuilt it (and its pipeline) on every request. |
  | `IChatClient` | Singleton | Stateless adapter over the client above. The starter's `Scoped` registration also could not be consumed by a singleton agent. |
  | `CompanyPolicyTool` / `SpendingLimitTool` | Singleton | Stateless. Reflection over them happens once. |
  | `IExpenseClassifierAgent` | Singleton | Owns the pre-built tools, pipeline, prompts and per-key lock. It depends only on singletons, so there are no captive dependencies. |

* **How does your solution support offline local development / CI via `SimulationChatClient`?**

  `ExpenseClassifier:UseSimulation=true` (the default in `appsettings.json`) registers `SimulationChatClient` as the `IChatClient`. The agent depends only on the `IChatClient` abstraction, so the same code path (function-invocation wrapper, JSON parsing, validation, cache, batch) runs unchanged offline. The test suite uses it directly for the README scenarios, and uses scripted `IChatClient` doubles for failure cases (malformed output, timeouts, transport errors, a real tool-calling loop). The tests need no network and no API key, and they run through `WebApplicationFactory`, so DI wiring is exercised end to end.

---

## 3. High-Performance Caching & Concurrency

* **Explain your cache key normalization and sanitization strategy (e.g. whitespace, case insensitivity, tokenization).**

  `ExpenseCacheKey.Create` applies, in order:
  1. Unicode NFKC normalization and `ToLowerInvariant()` (case-insensitive).
  2. Thousands separators removed (`₦15,000` == `₦15000`).
  3. Whitespace after a currency symbol removed (`₦ 15000` == `₦15000`).
  4. Every other run of punctuation replaced by a single space, keeping letters, digits, currency symbols and `.`.
  5. Sentence-ending dots removed while decimal points survive.
  6. Whitespace collapsed and trimmed.

  Meaning-bearing parts are deliberately kept, so `$1.50` != `$150`, `₦15,000` != `$15,000`, and different amounts never share an entry. Punctuation-only input falls back to its raw text so unrelated inputs cannot collide. Keys are prefixed `expense:v1:` so the scheme can be versioned. Result: `" Bolt ride to Victoria Island "` and `"bolt ride to victoria island"` hit the same entry. A hit echoes the caller's own wording in `ExpenseDescription` and reports `ClassificationStage = "CacheHit"`.

  Storage rules: only fully validated results reach `cache.Set` (every failure path throws before it), with an absolute TTL (default 30 minutes, configurable). Nothing null, malformed or failed is ever cached.

* **How did you prevent cache stampedes and ensure thread-safety under concurrent load?**

  `KeyedAsyncLock` gives a per-key async mutex with a double-checked cache lookup: check the cache, take the key's lock, check again, and only then call the model. If 25 identical claims arrive together, one calls the LLM and the other 24 wait and read the cache (verified by a test: 1 model call, 1 non-cache result, 24 `CacheHit`). Design details:
  - each waiter uses its **own** cancellation token, so one caller cancelling never fails the others (the reason I did not share a `Lazy<Task>` between callers);
  - lock entries are reference-counted and removed when unused, so the dictionary cannot grow without bound;
  - different keys never block each other;
  - release is idempotent.

  Cached values are cloned on the way in and out, so callers cannot corrupt shared state.

* **How did you implement bounded concurrency in `ClassifyBatch` (e.g. `Parallel.ForEachAsync`, `SemaphoreSlim`)?**

  `Parallel.ForEachAsync` over the item indexes with `MaxDegreeOfParallelism = ExpenseClassifierOptions.MaxBatchConcurrency` (default 5, configurable, validated 1-64) and the caller's `CancellationToken`. Each worker writes its result into a pre-sized array at its own index, so output order equals request order without any locking. After the loop, a single pass builds the `BatchSummary`: total items, counts per compliance status, `TotalAmountByCurrency` (summed per ISO currency, only for items that have both an amount and a currency) and elapsed milliseconds from a `Stopwatch`. A test with 15 slow items and a limit of 3 asserts the peak in-flight calls never exceeds 3. Duplicate descriptions inside one batch are de-duplicated for free by the cache and lock.

---

## 4. Resilience, Error Handling & API Robustness

* **How did you handle rate-limiting (HTTP 429), upstream timeouts, transient failures, and malformed LLM responses?**

  - **429:** the Azure SDK already retries 429/5xx with back-off, so I did not stack a second retry layer on top. When a 429 survives, it is caught as `ClientResultException`, wrapped as `ClassificationFailedException` (429), and the API returns **429 with a `Retry-After` header** copied from the upstream response. The batch concurrency limit exists to keep us under upstream rate limits in the first place.
  - **Timeouts:** each stage runs under a linked `CancellationTokenSource` with `CancelAfter(LlmTimeoutSeconds)`. A timeout is distinguished from the caller cancelling: a timeout becomes **504**, while a client disconnect propagates as cancellation (no body, logged as 499, never counted as a server error).
  - **Transient / transport failures** (`HttpRequestException`, other upstream errors) become **502**. They are never cached, so the next request reaches the model again.
  - **Malformed LLM output:** parsed defensively. Malformed Stage 1 output triggers the fallback stage. Malformed output from both stages gives **502**. Never cached.
  - **Batch partial failure:** one bad line item does not fail the request. It becomes a result with `classificationStage: "Failed"` and an explanation, is counted in `failedCount`, is excluded from the totals, and the batch still returns 200. Cancellation, by contrast, aborts the whole batch.
  - `CancellationToken` is propagated from the HTTP request through the lock, batch workers, function-invocation loop and the call to Azure.

* **How are RFC 7807 `ProblemDetails` formatted for client errors vs internal upstream failures?**

  Every error is `application/problem+json` with `status`, `title` and `detail` (plus `traceId`).
  - **Client errors (400):** `ValidationProblem` with an `errors` dictionary keyed by field, so the caller knows what to fix (for example `items[1].description`). Covers empty, whitespace or oversized descriptions, null or malformed JSON bodies, and empty or oversized batches.
  - **Upstream failures:** 429 ("AI service is rate limited", with `Retry-After`), 502 ("AI service failure"), 504 ("AI service timed out"). The `detail` is a safe, generic sentence: no provider names, keys, endpoints or stack traces.
  - **Unexpected exceptions:** 500 with the generic detail "An unexpected error occurred." The real exception is logged server-side only (a test asserts the internal message never appears in the response).
  - Unknown routes and bodyless 404/405/415 responses are also converted to ProblemDetails by `UseStatusCodePages()`.

  All of this is centralized in `ApiExceptionHandler` (`IExceptionHandler`), so endpoints stay free of try/catch.

**Small, additive contract changes (all backward compatible):** an optional `id` on batch results (omitted for `/classify`), `unverifiableCount` and `failedCount` in `BatchSummary` (so the buckets add up to `totalItems`), and the stage value `Failed` for batch items that could not be classified. I also fixed `ExpenseClassifier.slnx`, which referenced four projects that are not in the repository and made a root `dotnet build` / `dotnet test` fail.

---

## 5. Production & Enterprise Scale Roadmap

* **If you were rolling this service into production at scale (e.g. 100,000 expense claims/day), what architectural changes would you introduce?**

  100,000/day is only about 1.2 requests/second on average, but it bursts (month-end reports), and LLM latency and cost dominate. So:

  1. **Asynchronous processing:** `POST` returns `202 Accepted` with a job id. Claims go onto **Azure Service Bus**. Worker instances classify with a global concurrency and token budget (Polly bulkhead and circuit breaker). Results come back by webhook or polling. Batches become durable, resumable and retryable per item, and a burst just deepens the queue instead of overloading the model.
  2. **Distributed and semantic caching:** replace `IMemoryCache` with **HybridCache** (L1 in-memory plus L2 **Redis**) so all instances share hits, with built-in stampede protection. Add a **semantic cache** (embeddings in Redis, Qdrant or Azure AI Search) so "Bolt to VI" and "Uber to Victoria Island" reuse an answer. Cache only above a similarity and confidence threshold.
  3. **Cost and latency:** route easy, high-confidence claims through rules or a small model (for example gpt-4o-mini) and escalate to the larger model only on low confidence. Use prompt caching, and track tokens and cost per request and per tenant.
  4. **Correctness:** add a versioned, deterministic limit engine as a second opinion that can only escalate the model's verdict. Add FX conversion so cross-currency claims can be checked and summed. Send low-confidence and `Unverifiable` items to a human-review queue. Run a golden-set evaluation against the live model in CI to catch prompt or model regressions.
  5. **Security and identity:** **Managed Identity** only (no API keys), secrets in Key Vault, Azure AI Content Safety / prompt shields, PII redaction before logging, per-tenant authentication and rate limiting at API Management, and an audit trail of every verdict.
  6. **Observability:** **OpenTelemetry** traces and metrics (stage reached, cache hit rate, tokens, latency, 429 rate) exported through **.NET Aspire** / Azure Monitor and **Prometheus**, plus health checks and SLO alerts.
  7. **Persistence and idempotency:** store results in SQL or Cosmos DB for analytics, and accept a client-supplied idempotency key so retried submissions are not double counted.
  8. **Deployment:** stateless containers on Azure Container Apps or AKS with autoscaling on queue depth, and multiple Azure OpenAI deployments or regions behind a gateway for capacity and failover.
