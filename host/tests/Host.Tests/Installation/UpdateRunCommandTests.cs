using HyperHarbor.Host.Service.Installation;

namespace HyperHarbor.Host.Tests.Installation;

public sealed class UpdateRunCommandTests
{
    private static string UniqueName() => $@"Local\HyperHarbor.Tests.{Guid.NewGuid():N}";

    [Fact]
    public void RunExclusive_ReleasesTheMutex_WhenTheWorkResumesOnAnotherThread()
    {
        // Seen live: every update ended with hh-update.exe crashing (0xE0434352), because the mutex was released on
        // the thread the await resumed on, not the one that took it.
        var name = UniqueName();
        var startThread = Environment.CurrentManagedThreadId;
        var resumedThread = startThread;

        var result = UpdateRunCommand.RunExclusive(name, async () =>
        {
            await Task.Delay(20).ConfigureAwait(false);
            resumedThread = Environment.CurrentManagedThreadId;
            return 7;
        });

        Assert.Equal(7, result);
        Assert.NotEqual(startThread, resumedThread);
        Assert.Equal(1, UpdateRunCommand.RunExclusive(name, () => Task.FromResult(1)));
    }

    [Fact]
    public void RunExclusive_ReturnsZeroWithoutRunning_WhileAnotherHelperHoldsTheMutex()
    {
        var name = UniqueName();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: true, name);
            held.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        holder.Start();
        held.Wait();

        var ran = false;
        var result = UpdateRunCommand.RunExclusive(name, () =>
        {
            ran = true;
            return Task.FromResult(5);
        });

        release.Set();
        holder.Join();
        Assert.Equal(0, result);
        Assert.False(ran);
    }

    [Fact]
    public void RunExclusive_TakesOverAnAbandonedMutex()
    {
        var name = UniqueName();
        var holder = new Thread(() =>
        {
            // The thread ends without releasing it, as a killed helper would.
            var mutex = new Mutex(initiallyOwned: true, name);
            GC.KeepAlive(mutex);
        });
        holder.Start();
        holder.Join();

        Assert.Equal(4, UpdateRunCommand.RunExclusive(name, () => Task.FromResult(4)));
    }
}
