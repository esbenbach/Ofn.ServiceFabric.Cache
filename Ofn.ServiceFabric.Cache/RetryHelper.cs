namespace Ofn.ServiceFabric.Cache;

using Microsoft.ServiceFabric.Data;
using System;
using System.Threading;
using System.Threading.Tasks;

static class RetryHelper
{
    private const int DefaultMaxAttempts = 10;
    private const int MaxBackOffFactor = 1024;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MinimumDelay = TimeSpan.FromMilliseconds(200);

    public static async Task<TResult> ExecuteWithRetry<TResult>(
        IReliableStateManager stateManager,
        Func<ITransaction, CancellationToken, object?, Task<TResult>> operation,
        object? state = null,
        CancellationToken cancellationToken = default,
        int maxAttempts = DefaultMaxAttempts,
        TimeSpan? initialDelay = null,
        Action<int>? onRetry = null,
        Action? onFinalFailure = null)
    {
        ArgumentNullException.ThrowIfNull(stateManager);
        ArgumentNullException.ThrowIfNull(operation);
        if (maxAttempts <= 0) maxAttempts = DefaultMaxAttempts;
        if (initialDelay == null || initialDelay.Value < MinimumDelay)
            initialDelay = InitialDelay;

        Func<CancellationToken, object?, Task<TResult>> wrapped = async (_, _) =>
        {
            TResult result;
            using (var tran = stateManager.CreateTransaction())
            {
                try
                {
                    result = await operation(tran, cancellationToken, state);
                    await tran.CommitAsync();
                }
                catch (TimeoutException)
                {
                    tran.Abort();
                    throw;
                }
            }
            return result;
        };

        var outerResult = await ExecuteWithRetry(wrapped, state, cancellationToken, maxAttempts, initialDelay, onRetry, onFinalFailure);
        return outerResult!;
    }

    public static async Task ExecuteWithRetry(
        IReliableStateManager stateManager, 
        Func<ITransaction, CancellationToken, object?, Task> operation,
        object? state = null,
        CancellationToken cancellationToken = default,
        int maxAttempts = DefaultMaxAttempts,
        TimeSpan? initialDelay = null,
        Action<int>? onRetry = null,
        Action? onFinalFailure = null)
    {
        ArgumentNullException.ThrowIfNull(stateManager);
        ArgumentNullException.ThrowIfNull(operation);
        if (maxAttempts <= 0) maxAttempts = DefaultMaxAttempts;
        if (initialDelay == null || initialDelay.Value < MinimumDelay)
            initialDelay = InitialDelay;

        Func<CancellationToken, object?, Task<object?>> wrapped = async (_, _) =>
        {
            using (var tran = stateManager.CreateTransaction())
            {
                try
                {
                    await operation(tran, cancellationToken, state);
                    await tran.CommitAsync();
                }
                catch (TimeoutException)
                {
                    tran.Abort();
                    throw;
                }
            }
            return null;
        };

        await ExecuteWithRetry(wrapped, state, cancellationToken, maxAttempts, initialDelay, onRetry, onFinalFailure);
    }

    public static async Task<TResult?> ExecuteWithRetry<TResult>(
        Func<CancellationToken, object?, Task<TResult>> operation,
        object? state = null,
        CancellationToken cancellationToken = default,
        int maxAttempts = DefaultMaxAttempts,
        TimeSpan? initialDelay = null,
        Action<int>? onRetry = null,
        Action? onFinalFailure = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (maxAttempts <= 0) maxAttempts = DefaultMaxAttempts;
        if (initialDelay == null || initialDelay.Value < MinimumDelay)
            initialDelay = InitialDelay;

        TResult? result = default;
        for (int attempts = 0; attempts < maxAttempts; attempts++)
        {
            try
            {
                result = await operation(cancellationToken, state);
                break;
            }
            catch (TimeoutException)
            {
                if (attempts >= maxAttempts - 1)
                {
                    onFinalFailure?.Invoke();
                    throw;
                }

                onRetry?.Invoke(attempts);
            }

            //exponential back-off
            int factor = (int)Math.Min(Math.Pow(2, attempts), MaxBackOffFactor) + 1;
            int delay = Random.Shared.Next((int)(initialDelay.Value.TotalMilliseconds * 0.5D), (int)(initialDelay.Value.TotalMilliseconds * 1.5D));
            await Task.Delay(factor * delay, cancellationToken);
        }
        return result;
    }
}
