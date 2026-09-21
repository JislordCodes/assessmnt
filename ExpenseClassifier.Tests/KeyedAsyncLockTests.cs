using ExpenseClassifier.Services;
using Xunit;

namespace ExpenseClassifier.Tests;

public class KeyedAsyncLockTests
{
    [Fact]
    public async Task SameKey_IsMutuallyExclusive()
    {
        var locks = new KeyedAsyncLock();
        var first = await locks.AcquireAsync("k", CancellationToken.None);

        var second = locks.AcquireAsync("k", CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        first.Dispose();
        (await second.WaitAsync(TimeSpan.FromSeconds(2))).Dispose();
    }

    [Fact]
    public async Task DifferentKeys_DoNotBlockEachOther()
    {
        var locks = new KeyedAsyncLock();
        using var a = await locks.AcquireAsync("a", CancellationToken.None);
        using var b = await locks.AcquireAsync("b", CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotBreakTheLockForOthers()
    {
        var locks = new KeyedAsyncLock();
        var holder = await locks.AcquireAsync("k", CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var cancelled = locks.AcquireAsync("k", cts.Token).AsTask();
        var patient = locks.AcquireAsync("k", CancellationToken.None).AsTask();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        holder.Dispose();
        (await patient.WaitAsync(TimeSpan.FromSeconds(2))).Dispose();
    }

    [Fact]
    public async Task DoubleDispose_DoesNotReleaseTwice()
    {
        var locks = new KeyedAsyncLock();
        var h = await locks.AcquireAsync("k", CancellationToken.None);
        h.Dispose();
        h.Dispose();

        var one = await locks.AcquireAsync("k", CancellationToken.None);
        var two = locks.AcquireAsync("k", CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert.False(two.IsCompleted); // would be true if the double dispose had over-released the semaphore
        one.Dispose();
        (await two.WaitAsync(TimeSpan.FromSeconds(2))).Dispose();
    }
}
