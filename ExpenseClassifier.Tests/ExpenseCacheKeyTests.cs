using ExpenseClassifier.Services;
using Xunit;

namespace ExpenseClassifier.Tests;

public class ExpenseCacheKeyTests
{
    [Theory]
    [InlineData(" Bolt ride to Victoria Island ", "bolt ride to victoria island")]
    [InlineData("BOLT   RIDE\tto\nVictoria Island!!!", "bolt ride to victoria island")]
    [InlineData("Bolt ride, to Victoria-Island.", "bolt ride to victoria island")]
    public void CosmeticVariations_ProduceTheSameKey(string a, string b) =>
        Assert.Equal(ExpenseCacheKey.Create(b), ExpenseCacheKey.Create(a));

    [Fact]
    public void ThousandsSeparator_IsIgnored() =>
        Assert.Equal(ExpenseCacheKey.Create("Lunch ₦15,000"), ExpenseCacheKey.Create("lunch ₦15000"));

    [Fact]
    public void SpaceAfterCurrencySymbol_IsIgnored() =>
        Assert.Equal(ExpenseCacheKey.Create("Lunch ₦ 15000"), ExpenseCacheKey.Create("lunch ₦15000"));

    [Theory]
    [InlineData("Lunch $1.50", "Lunch $150")]          // decimal point is meaningful
    [InlineData("Lunch ₦15,000", "Lunch $15,000")]     // currency is meaningful
    [InlineData("Lunch 15000", "Lunch 15001")]
    [InlineData("Bolt ride", "Uber ride")]
    public void MeaningfulDifferences_ProduceDifferentKeys(string a, string b) =>
        Assert.NotEqual(ExpenseCacheKey.Create(a), ExpenseCacheKey.Create(b));

    [Fact]
    public void PunctuationOnlyInputs_DoNotCollideWithEachOther() =>
        Assert.NotEqual(ExpenseCacheKey.Create("???"), ExpenseCacheKey.Create("!!!"));

    [Fact]
    public void Null_Throws() => Assert.Throws<ArgumentNullException>(() => ExpenseCacheKey.Create(null!));
}
