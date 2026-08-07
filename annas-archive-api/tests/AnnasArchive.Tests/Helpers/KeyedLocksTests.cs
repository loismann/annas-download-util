using AnnasArchive.API.Helpers;

namespace AnnasArchive.Tests.Helpers;

/// <summary>
/// Two properties have to hold at once, and they pull against each other: the
/// lock must actually exclude, and the entry must not survive the last caller.
/// Getting only the first is the leak this replaced; getting only the second
/// is worse — it lets two callers into the same critical section.
/// </summary>
public class KeyedLocksTests
{
    [Fact]
    public async Task ExcludesASecondCallerOnTheSameKey()
    {
        var locks = new KeyedLocks();
        using var first = await locks.AcquireAsync("book-1");

        var second = locks.AcquireAsync("book-1");
        var finished = await Task.WhenAny(second, Task.Delay(100));

        finished.Should().NotBeSameAs(second, "the key is already held");

        first.Dispose();
        (await second).Dispose();
    }

    [Fact]
    public async Task DoesNotBlockADifferentKey()
    {
        var locks = new KeyedLocks();
        using var first = await locks.AcquireAsync("book-1");

        // Would hang rather than fail if the keys shared a lock.
        using var second = await locks.AcquireAsync("book-2");

        locks.Count.Should().Be(2);
    }

    [Fact]
    public async Task ForgetsAKeyOnceTheLastHolderLeaves()
    {
        // The whole point. One SemaphoreSlim per book ever opened is what the
        // ConcurrentDictionary version accumulated for the life of the process.
        var locks = new KeyedLocks();

        for (var i = 0; i < 500; i++)
        {
            using var lease = await locks.AcquireAsync($"book-{i}");
        }

        locks.Count.Should().Be(0);
    }

    [Fact]
    public async Task KeepsTheEntryWhileSomeoneIsStillQueued()
    {
        // Removing it here would hand the waiter a different semaphore for the
        // same key, and both callers would run at once.
        var locks = new KeyedLocks();
        var first = await locks.AcquireAsync("book-1");

        var second = locks.AcquireAsync("book-1");
        locks.Count.Should().Be(1);

        first.Dispose();
        var lease = await second;
        locks.Count.Should().Be(1, "the second caller now holds it");

        lease.Dispose();
        locks.Count.Should().Be(0);
    }

    [Fact]
    public async Task LetsOneCallerAtATimeThroughUnderContention()
    {
        var locks = new KeyedLocks();
        var inside = 0;
        var maxInside = 0;
        var gate = new object();

        await Task.WhenAll(Enumerable.Range(0, 50).Select(async _ =>
        {
            using var lease = await locks.AcquireAsync("hot-key");

            lock (gate) maxInside = Math.Max(maxInside, ++inside);
            await Task.Delay(1);
            lock (gate) inside--;
        }));

        maxInside.Should().Be(1);
        locks.Count.Should().Be(0, "and nothing is left behind afterwards");
    }

    [Fact]
    public async Task ReleasesTheKeyWhenTheBodyThrows()
    {
        // A `using` releases on the way out either way; this pins that the
        // registry does not treat a failed caller as a permanent holder.
        var locks = new KeyedLocks();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var lease = await locks.AcquireAsync("book-1");
            await Task.Yield();
            throw new InvalidOperationException("boom");
        });

        locks.Count.Should().Be(0);
        using var reacquired = await locks.AcquireAsync("book-1");
    }

    [Fact]
    public async Task GivesUpTheReservationWhenAWaiterIsCancelled()
    {
        // Otherwise the key stays forever precisely because nobody ever got in.
        var locks = new KeyedLocks();
        using var held = await locks.AcquireAsync("book-1");

        using var cancel = new CancellationTokenSource();
        var queued = locks.AcquireAsync("book-1", cancel.Token);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        locks.Count.Should().Be(1, "only the original holder remains");
    }

    [Fact]
    public async Task DisposingTwiceDoesNotAdmitTwoCallers()
    {
        // A double release on a SemaphoreSlim(1,1) would raise its count to 2
        // and let two callers into the critical section.
        var locks = new KeyedLocks();
        var lease = await locks.AcquireAsync("book-1");
        lease.Dispose();
        lease.Dispose();

        using var first = await locks.AcquireAsync("book-1");
        var second = locks.AcquireAsync("book-1");
        var finished = await Task.WhenAny(second, Task.Delay(100));

        finished.Should().NotBeSameAs(second, "the lock still excludes");

        first.Dispose();
        (await second).Dispose();
    }

    [Fact]
    public async Task ReusesAKeyCleanlyAfterItWasForgotten()
    {
        var locks = new KeyedLocks();

        for (var i = 0; i < 3; i++)
        {
            using var lease = await locks.AcquireAsync("book-1");
            locks.Count.Should().Be(1);
        }

        locks.Count.Should().Be(0);
    }
}
