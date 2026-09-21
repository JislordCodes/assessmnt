using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ExpenseClassifier.Agents;
using ExpenseClassifier.Models;
using ExpenseClassifier.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ExpenseClassifier.Tests;

public class ApiEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Client(IExpenseClassifierAgent? agent = null) =>
        agent is null
            ? factory.CreateClient()
            : factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            {
                s.RemoveAll<IExpenseClassifierAgent>();
                s.AddSingleton(agent);
            })).CreateClient();

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadProblem(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    // ------------------------------------------------------------ happy paths (DI wiring works end to end)

    [Fact]
    public async Task Classify_ReturnsStructuredResult()
    {
        var response = await Client().PostAsJsonAsync("/classify",
            new { description = "Client dinner meeting at Terra Kulture Restaurant Victoria Island Lagos for ₦45,000" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ResponseDto>(Json);
        Assert.Equal("Food", dto!.Category);
        Assert.Equal("RequiresManagerApproval", dto.ComplianceStatus);
        Assert.Equal(45000m, dto.ExtractedAmount);
    }

    [Fact]
    public async Task Classify_SingleResponse_HasNoIdProperty()
    {
        var text = await (await Client().PostAsJsonAsync("/classify", new { description = "Bolt ride ₦5,000" })).Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"id\"", text);
        Assert.Contains("\"classificationStage\"", text);
    }

    [Fact]
    public async Task ClassifyBatch_ReadmeExample_ReturnsSummary()
    {
        var response = await Client().PostAsJsonAsync("/classify/batch", new
        {
            department = "Engineering",
            employeeId = "EMP-9402",
            items = new[]
            {
                new { id = "1", description = "Bolt ride to office ₦8,500" },
                new { id = "2", description = "Team lunch at Bukka Hut ₦42,000" },
                new { id = "3", description = "Monthly MTN 5G Broadband data subscription ₦35,000" },
                new { id = "4", description = "GitHub Copilot monthly subscription $19 USD" }
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<BatchExpenseResponse>(Json);
        Assert.Equal(4, dto!.Summary.TotalItems);
        Assert.Equal(85500m, dto.Summary.TotalAmountByCurrency["NGN"]);
        Assert.Equal(19m, dto.Summary.TotalAmountByCurrency["USD"]);
        Assert.Equal(4, dto.Results.Count);
    }

    // ------------------------------------------------------------ validation -> 400 problem+json

    [Theory]
    [InlineData("""{"description":""}""")]
    [InlineData("""{"description":"   "}""")]
    [InlineData("""{"description":null}""")]
    [InlineData("""{}""")]
    [InlineData("""null""")]
    public async Task Classify_InvalidPayload_Returns400ProblemDetails(string body)
    {
        var response = await Client().PostAsync("/classify", Body(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblem(response);
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Classify_MalformedJson_Returns400ProblemDetails()
    {
        var response = await Client().PostAsync("/classify", Body("{not json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await ReadProblem(response);
    }

    [Fact]
    public async Task Classify_DescriptionTooLong_Returns400WithFieldError()
    {
        var response = await Client().PostAsJsonAsync("/classify", new { description = new string('x', 2_001) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblem(response);
        Assert.True(problem.GetProperty("errors").TryGetProperty("description", out _));
    }

    [Theory]
    [InlineData("""{"items":[]}""")]
    [InlineData("""{"items":null}""")]
    [InlineData("""{}""")]
    public async Task ClassifyBatch_EmptyItems_Returns400ProblemDetails(string body)
    {
        var response = await Client().PostAsync("/classify/batch", Body(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await ReadProblem(response);
    }

    [Fact]
    public async Task ClassifyBatch_BlankItem_ReportsWhichItemIsInvalid()
    {
        var response = await Client().PostAsync("/classify/batch",
            Body("""{"items":[{"id":"1","description":"Bolt ride ₦5,000"},{"id":"2","description":"  "}]}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblem(response);
        Assert.True(problem.GetProperty("errors").TryGetProperty("items[1].description", out _));
    }

    [Fact]
    public async Task ClassifyBatch_TooManyItems_Returns400()
    {
        var items = string.Join(",", Enumerable.Range(0, 201).Select(i => $$"""{"description":"Lunch {{i}} $1"}"""));

        var response = await Client().PostAsync("/classify/batch", Body($$"""{"items":[{{items}}]}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await ReadProblem(response);
    }

    [Fact]
    public async Task UnknownRoute_Returns404ProblemDetails()
    {
        var response = await Client().GetAsync("/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await ReadProblem(response);
    }

    // ------------------------------------------------------------ upstream failures -> safe problem details

    private sealed class ThrowingAgent(Exception exception) : IExpenseClassifierAgent
    {
        public Task<ResponseDto> Classify(string expenseDescription, CancellationToken cancellationToken = default) => throw exception;
        public Task<BatchExpenseResponse> ClassifyBatch(BatchExpenseRequest request, CancellationToken cancellationToken = default) => throw exception;
    }

    [Fact]
    public async Task UpstreamRateLimit_Returns429WithRetryAfter()
    {
        var client = Client(new ThrowingAgent(new ClassificationFailedException("The AI service is rate limiting requests.", 429, retryAfterSeconds: 7)));

        var response = await client.PostAsJsonAsync("/classify", new { description = "Lunch $10" });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("7", response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString());
        await ReadProblem(response);
    }

    [Theory]
    [InlineData(504)]
    [InlineData(502)]
    public async Task UpstreamFailure_MapsToGatewayStatus(int status)
    {
        var client = Client(new ThrowingAgent(new ClassificationFailedException("nope", status)));

        var response = await client.PostAsJsonAsync("/classify/batch", new { items = new[] { new { description = "Lunch $10" } } });

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(status, (await ReadProblem(response)).GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task UnexpectedException_Returns500_WithoutLeakingInternals()
    {
        var client = Client(new ThrowingAgent(new InvalidOperationException("secret connection string xyz")));

        var response = await client.PostAsJsonAsync("/classify", new { description = "Lunch $10" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret connection string", text);
        Assert.Contains("problem", response.Content.Headers.ContentType!.MediaType!);
    }
}
