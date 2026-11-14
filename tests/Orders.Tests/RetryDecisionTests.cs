using Orders.Api.Messaging;
using Xunit;

namespace Orders.Tests;

public sealed class RetryDecisionTests
{
    [Theory]
    [InlineData(1, 5, RetryAction.Retry)]
    [InlineData(4, 5, RetryAction.Retry)]
    [InlineData(5, 5, RetryAction.DeadLetter)]
    [InlineData(6, 5, RetryAction.DeadLetter)]
    [InlineData(1, 1, RetryAction.DeadLetter)]
    public void For_ReturnsExpectedAction(int attempt, int maxAttempts, RetryAction expected)
    {
        var decision = RetryDecision.For(attempt, maxAttempts);

        Assert.Equal(expected, decision);
    }
}
