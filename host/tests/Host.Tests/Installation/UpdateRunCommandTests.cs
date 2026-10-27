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
        uint osThreadId = 0;
        Mutex? mutex = null;
        var holder = new Thread(() =>
        {
            // The thread ends without releasing it, as a killed helper would.
            osThreadId = GetCurrentThreadId();
            mutex = new Mutex(initiallyOwned: true, name);
        });
        holder.Start();
        holder.Join();

        // Windows abandons the mutex when the OS thread exits, which can be a moment after Join returns; under load
        // (the full suite on CI, 2026-10-27) RunExclusive's immediate check came first and found it still owned.
        var handle = OpenThread(Synchronize, false, osThreadId);
        if (handle != IntPtr.Zero)
        {
            try
            {
                Assert.Equal(0u, WaitForSingleObject(handle, 10_000));
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        Assert.Equal(4, UpdateRunCommand.RunExclusive(name, () => Task.FromResult(4)));
        GC.KeepAlive(mutex);
    }

    private const uint Synchronize = 0x00100000;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
