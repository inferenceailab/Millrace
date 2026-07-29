using Microsoft.Extensions.Time.Testing;
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
/// <para>
/// <b>The clock is advanced here rather than by the caller, because callers forget.</b> A worker
/// with nothing to claim parks in <c>WaitForWorkAsync</c> on two things: a storage wakeup signal,
/// and <c>Task.Delay(pollDelay, time)</c> as the ceiling. The signal is explicitly a hint —
/// <c>IStorageNotifier</c> permits it to be dropped — so the delay is the only guaranteed way the
/// worker looks again. On a fake clock nothing advances, that guarantee is switched off and the
/// test hangs until the deadline on any missed signal. Three of the five call sites advanced the
/// clock by hand and two did not; one of those two went red on CI (#87 again, on a docs-only
/// commit). Sweeping it into the helper is what makes forgetting impossible.
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
    /// <param name="time">
    /// The host's fake clock, advanced by <paramref name="advanceEach"/> every poll so the worker's
    /// poll ceiling keeps firing. Required rather than optional: an omitted clock is exactly the
    /// bug this parameter exists to prevent, so there is no defaulting it away.
    /// </param>
    /// <param name="advanceEach">
    /// Fake time added per poll, 200ms by default. Bounded in total by the deadline — 20s of real
    /// time at 50ms a poll is at most ~80 fake seconds — so a wait that must not release a longer
    /// scheduled job stays safe by keeping that product below the job's due time.
    /// </param>
    public static async Task<T> ObservedAsync<T>(
        Func<Task<T>> read,
        Func<T, bool> until,
        string what,
        FakeTimeProvider time,
        TimeSpan? advanceEach = null,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        var step = advanceEach ?? TimeSpan.FromMilliseconds(200);
        T last = default!;

        while (DateTime.UtcNow < deadline)
        {
            last = await read();
            if (until(last))
            {
                return last;
            }

            time.Advance(step);

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
