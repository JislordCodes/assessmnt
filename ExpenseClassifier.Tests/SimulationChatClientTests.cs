using System.Text.Json;
using ExpenseClassifier.Models;
using ExpenseClassifier.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace ExpenseClassifier.Tests;

public class SimulationChatClientTests
{
    private readonly SimulationChatClient _client = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Theory]
    [InlineData("Bolt ride from Murtala Muhammed Airport to Victoria Island costing ₦15,000", "Transportation", "Bolt", 15000.0, "NGN", "Compliant")]
    [InlineData("Client dinner meeting at Terra Kulture Restaurant Victoria Island Lagos for $180 USD", "Food", "Terra Kulture", 180.0, "USD", "PolicyViolation")]
    [InlineData("Team lunch at Bukka Hut Lekki costing ₦40,000", "Food", "Bukka Hut", 40000.0, "NGN", "RequiresManagerApproval")]
    [InlineData("2 nights hotel accommodation at Eko Hotel and Suites Lagos for ₦350,000", "Accommodation", "Eko Hotel & Suites", 350000.0, "NGN", "RequiresManagerApproval")]
    [InlineData("Ikeja Electric (IKEDC) prepaid meter token recharge for ₦50,000", "Utilities", "Ikeja Electric (IKEDC)", 50000.0, "NGN", "Compliant")]
    [InlineData("Annual JetBrains Rider IDE team license renewal for software engineers $650 USD", "Software Subscriptions", "JetBrains", 650.0, "USD", "Compliant")]
    public async Task GetResponseAsync_ShouldCorrectlyExtractEntitiesAndEvaluateCompliance(
        string prompt,
        string expectedCategory,
        string? expectedMerchant,
        double expectedAmount,
        string? expectedCurrency,
        string expectedCompliance)
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };

        var response = await _client.GetResponseAsync(messages);

        Assert.NotNull(response);
        var messageText = response.Messages.FirstOrDefault()?.Text;
        Assert.False(string.IsNullOrWhiteSpace(messageText));

        var result = JsonSerializer.Deserialize<ResponseDto>(messageText!, JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(expectedCategory, result!.Category);
        Assert.Equal(expectedMerchant, result.Merchant);
        Assert.Equal((decimal)expectedAmount, result.ExtractedAmount);
        Assert.Equal(expectedCurrency, result.Currency);
        Assert.Equal(expectedCompliance, result.ComplianceStatus);
        Assert.True(result.ConfidenceScore > 0.0);
    }

    [Fact]
    public async Task GetResponseAsync_WhenNoAmountProvided_ShouldMarkAsUnverifiable()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "Bolt ride from Airport to office") };

        var response = await _client.GetResponseAsync(messages);
        var result = JsonSerializer.Deserialize<ResponseDto>(response.Messages.First().Text!, JsonOptions);

        Assert.NotNull(result);
        Assert.Equal("Transportation", result!.Category);
        Assert.Null(result.ExtractedAmount);
        Assert.Equal("Unverifiable", result.ComplianceStatus);
    }
}
