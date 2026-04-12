namespace Ofn.ServiceFabric.Cache.UnitTests;

using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

public class RetryHelperTest
{
    [Fact]
    public async Task ExecuteWithRetry_OperationSucceedsFirstTry_ReturnsResultWithoutRetrying()
    {
        int callCount = 0;
        Func<CancellationToken, object, Task<int>> operation = (_, _) =>
        {
            callCount++;
            return Task.FromResult(42);
        };

        var result = await RetryHelper.ExecuteWithRetry(operation, initialDelay: TimeSpan.FromMilliseconds(1),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(42, result);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ExecuteWithRetry_OperationThrowsTimeoutThenSucceeds_ReturnsResult()
    {
        int callCount = 0;
        Func<CancellationToken, object, Task<int>> operation = (_, _) =>
        {
            callCount++;
            if (callCount == 1)
                throw new TimeoutException();
            return Task.FromResult(42);
        };

        var result = await RetryHelper.ExecuteWithRetry(operation, maxAttempts: 3, initialDelay: TimeSpan.FromMilliseconds(1),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(42, result);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task ExecuteWithRetry_AllAttemptsExhausted_ThrowsTimeoutException()
    {
        Func<CancellationToken, object, Task<int>> operation = (_, _) =>
            throw new TimeoutException("exhausted");

        await Assert.ThrowsAsync<TimeoutException>(() =>
            RetryHelper.ExecuteWithRetry(operation, maxAttempts: 2, initialDelay: TimeSpan.FromMilliseconds(1),
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteWithRetry_CustomMaxAttempts_RespectsParameter()
    {
        int callCount = 0;
        Func<CancellationToken, object, Task<int>> operation = (_, _) =>
        {
            callCount++;
            throw new TimeoutException();
        };

        await Assert.ThrowsAsync<TimeoutException>(() =>
            RetryHelper.ExecuteWithRetry(operation, maxAttempts: 2, initialDelay: TimeSpan.FromMilliseconds(1),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(2, callCount);
    }
}
