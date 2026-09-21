using ExpenseClassifier.Tools;
using Xunit;

namespace ExpenseClassifier.Tests;

public class PolicyAndSpendingLimitToolTests
{
    [Fact]
    public void CompanyPolicyTool_ShouldReturnGuidelineRules()
    {
        var tool = new CompanyPolicyTool();
        var policy = tool.GetPolicy();

        Assert.False(string.IsNullOrWhiteSpace(policy));
        Assert.Contains("Transportation", policy);
        Assert.Contains("Food", policy);
        Assert.Contains("Accommodation", policy);
        Assert.Contains("Utilities", policy);
        Assert.Contains("Office Supplies", policy);
        Assert.Contains("Software Subscriptions", policy);
    }

    [Fact]
    public void SpendingLimitTool_ShouldReturnLimitRules()
    {
        var tool = new SpendingLimitTool();
        var limits = tool.GetSpendingLimits();

        Assert.False(string.IsNullOrWhiteSpace(limits));
        Assert.Contains("Compliant", limits);
        Assert.Contains("RequiresManagerApproval", limits);
        Assert.Contains("PolicyViolation", limits);
        Assert.Contains("Unverifiable", limits);
    }
}
