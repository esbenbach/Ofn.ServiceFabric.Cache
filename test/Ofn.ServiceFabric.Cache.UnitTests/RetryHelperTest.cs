namespace Ofn.ServiceFabric.Cache.UnitTests;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Data;
using Moq;
using Xunit;

public class RetryHelperTest
{
    [Fact]
    public async Task ExecuteWithRetry_OperationSucceedsFirstTry_ReturnsResultWithoutRetrying()
    {
        int callCount = 0;
        Func<CancellationToken, object?, Task<int>> operation = (_, _) =>
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
        Func<CancellationToken, object?, Task<int>> operation = (_, _) =>
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
        Func<CancellationToken, object?, Task<int>> operation = (_, _) =>
            throw new TimeoutException("exhausted");

        await Assert.ThrowsAsync<TimeoutException>(() =>
            RetryHelper.ExecuteWithRetry(operation, maxAttempts: 2, initialDelay: TimeSpan.FromMilliseconds(1),
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteWithRetry_CustomMaxAttempts_RespectsParameter()
    {
        int callCount = 0;
        Func<CancellationToken, object?, Task<int>> operation = (_, _) =>
        {
            callCount++;
            throw new TimeoutException();
        };

        await Assert.ThrowsAsync<TimeoutException>(() =>
            RetryHelper.ExecuteWithRetry(operation, maxAttempts: 2, initialDelay: TimeSpan.FromMilliseconds(1),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task ExecuteWithRetry_WithStateManager_OperationSucceeds_CommitsTransaction()
    {
        var transaction = new Mock<ITransaction>();
        transaction.Setup(t => t.CommitAsync()).Returns(Task.CompletedTask);

        var stateManager = new Mock<IReliableStateManager>();
        stateManager.Setup(sm => sm.CreateTransaction()).Returns(transaction.Object);

        Func<ITransaction, CancellationToken, object?, Task<int>> operation = (tran, _, _) =>
            Task.FromResult(99);

        var result = await RetryHelper.ExecuteWithRetry(
            stateManager.Object,
            operation,
            initialDelay: TimeSpan.FromMilliseconds(1),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(99, result);
        transaction.Verify(t => t.CommitAsync(), Times.Once);
    }

    [Fact]
    public async Task ExecuteWithRetry_WithStateManager_OperationThrowsTimeout_AbortsTransaction()
    {
        var transaction = new Mock<ITransaction>();

        var stateManager = new Mock<IReliableStateManager>();
        stateManager.Setup(sm => sm.CreateTransaction()).Returns(transaction.Object);

        int callCount = 0;
        Func<ITransaction, CancellationToken, object?, Task<int>> operation = (_, _, _) =>
        {
            callCount++;
            throw new TimeoutException();
        };

        await Assert.ThrowsAsync<TimeoutException>(() =>
            RetryHelper.ExecuteWithRetry(
                stateManager.Object,
                operation,
                maxAttempts: 1,
                initialDelay: TimeSpan.FromMilliseconds(1),
                cancellationToken: TestContext.Current.CancellationToken));

        transaction.Verify(t => t.Abort(), Times.Once);
        transaction.Verify(t => t.CommitAsync(), Times.Never);
    }

    [Fact]
    public async Task ExecuteWithRetry_NonTimeoutException_PropagatesImmediatelyWithoutRetry()
    {
        int callCount = 0;
        Func<CancellationToken, object?, Task<int>> operation = (_, _) =>
        {
            callCount++;
            throw new InvalidOperationException("boom");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RetryHelper.ExecuteWithRetry(operation, maxAttempts: 5, initialDelay: TimeSpan.FromMilliseconds(1),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ExecuteWithRetry_StateParameter_IsForwardedToOperation()
    {
        var expectedState = new object();
        object? capturedState = null;

        Func<CancellationToken, object?, Task<int>> operation = (_, st) =>
        {
            capturedState = st;
            return Task.FromResult(0);
        };

        await RetryHelper.ExecuteWithRetry(operation, state: expectedState,
            initialDelay: TimeSpan.FromMilliseconds(1),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(expectedState, capturedState);
    }

    [Fact]
    public async Task ExecuteWithRetry_WithStateManager_VoidOverload_CommitsOnSuccess()
    {
        var transaction = new Mock<ITransaction>();
        transaction.Setup(t => t.CommitAsync()).Returns(Task.CompletedTask);

        var stateManager = new Mock<IReliableStateManager>();
        stateManager.Setup(sm => sm.CreateTransaction()).Returns(transaction.Object);

        bool operationCalled = false;
        Func<ITransaction, CancellationToken, object?, Task> operation = (_, _, _) =>
        {
            operationCalled = true;
            return Task.CompletedTask;
        };

        await RetryHelper.ExecuteWithRetry(
            stateManager.Object,
            operation,
            initialDelay: TimeSpan.FromMilliseconds(1),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(operationCalled);
        transaction.Verify(t => t.CommitAsync(), Times.Once);
    }
}
