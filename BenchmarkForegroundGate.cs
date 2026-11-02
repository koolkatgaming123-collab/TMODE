namespace TourneyMode;

/// <summary>Bounded foreground preparation; never used during a frame capture.</summary>
internal static class BenchmarkForegroundGate
{
    internal static async Task WaitAsync(int expectedPid, Func<int> foregroundPid, Action validateIdentity,
        Action<int> requestFocus, Func<long> elapsedMilliseconds, Func<CancellationToken, Task> delay,
        CancellationToken cancellation)
    {
        long? stableSince = null;
        int attempts = 0;
        long nextAttempt = 0;
        while (elapsedMilliseconds() < 120_000)
        {
            cancellation.ThrowIfCancellationRequested();
            validateIdentity();
            long now = elapsedMilliseconds();
            if (foregroundPid() == expectedPid)
            {
                stableSince ??= now;
                if (now - stableSince.Value >= 2_000) return;
            }
            else
            {
                stableSince = null;
                if (attempts < 3 && now >= nextAttempt)
                {
                    cancellation.ThrowIfCancellationRequested();
                    requestFocus(++attempts);
                    nextAttempt = now + 5_000;
                }
            }
            await delay(cancellation).ConfigureAwait(false);
        }
        throw new TimeoutException("Fortnite foreground preparation exceeded 120 seconds.");
    }
}