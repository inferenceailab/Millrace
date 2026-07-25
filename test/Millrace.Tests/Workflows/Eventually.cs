using Xunit;

namespace Millrace.Tests.Workflows;

/// <summary>
/// Waiting for something to be observed, rather than sleeping and hoping (#87).
/// </summary>
/// <remarks>
/// <para>
/// These tests drive the real worker on purpose (see #83), so they have to wait for another thread
/// to do something. A fixed <c>Task.Delay</c> encodes a guess about how fast the machine is: too
/// short and the test fails on a busy CI runner, too long and every run pays for it. The reported
/// flake was exactly that — <c>Task.Delay(200)</c> asserting the first activity had run, failing
/// under four-suite parallel load because it had not run yet.
/// </para>
/// <para>
/// <b>Polling for a precondition is safe; polling for the absence of something is not.</b> Waiting
/// until the first step appears is deterministic, because the step after it is gated behind a fake
/// clock the test controls and cannot run until the test advances it. That is what makes the
/// assertion that follows meaningful rather than a race the test usually wins.
/// </para>
/// </remarks>
internal static class Eventually
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Polls <paramref name="read"/> until <paramref name="until"/> holds.</summary>
    /// <param name="what">
    /// What is being waited for, phrased to complete "timed out waiting for …" — a timeout here
    /// means a genuine hang, so it must say what never happened.
    /// </param>
    public static async Task<T> ObservedAsync<T>(
        Func<Task<T>> read, Func<T, bool> until, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        T last = default!;

        while (DateTime.UtcNow < deadline)
        {
            last = await read();
            if (until(last))
            {
                return last;
            }

            // 50ms, not 15: every poll reads through the same storage lock the worker needs to make
            // the progress being waited for, so a tight loop competes with the thing it is waiting
            // for. Under four-suite parallel load a 15ms interval starved the worker badly enough to
            // hit the 20s deadline — the fixed sleep it replaced at least left the lock alone.
            await Task.Delay(50);
        }

        Assert.Fail($"Timed out waiting for {what}. Last observed: {last}");
        return last;
    }
}
