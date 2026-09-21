using ExpenseClassifier.Models;
using Xunit;

namespace ExpenseClassifier.Tests;

public class BatchContractTests
{
    [Fact]
    public void BatchSummary_CanAggregateCurrencyAmounts()
    {
        var summary = new BatchSummary
        {
            TotalItems = 3,
            CompliantCount = 2,
            RequiresApprovalCount = 1,
            PolicyViolationCount = 0,
            ProcessingTimeMs = 120
        };

        summary.TotalAmountByCurrency["NGN"] = 55000m;
        summary.TotalAmountByCurrency["USD"] = 120m;

        Assert.Equal(3, summary.TotalItems);
        Assert.Equal(55000m, summary.TotalAmountByCurrency["NGN"]);
        Assert.Equal(120m, summary.TotalAmountByCurrency["USD"]);
    }
}
