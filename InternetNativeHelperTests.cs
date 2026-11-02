namespace TourneyMode;

/// <summary>Real hidden PowerShell plumbing, inert fixture scripts and GUID-scoped locks only. No NIC commands.</summary>
internal static class InternetNativeHelperTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Internet native helper accepts long scripts through stdin without command-line truncation", () =>
        {
            string actual = InternetNicSystem.RunPowerShellFixture("#" + new string('a', 50000) + "\n'VERIFIED'", 10000, Name());
            Check(actual == "VERIFIED", "The bounded stdin helper truncated an inert script.");
            return Task.CompletedTask;
        });
        await test("Internet native helper rejects fresh helper work while a prior helper owns its execution guard", async () =>
        {
            string mutex = Name(), signal = Path.Combine(Path.GetTempPath(), "TModeHelperSignal-" + Guid.NewGuid().ToString("N"));
            string literal = signal.Replace("'", "''");
            var holder = Task.Run(() => InternetNicSystem.RunPowerShellFixture("[IO.File]::WriteAllText('" + literal + "','ready'); Start-Sleep -Seconds 6; 'HOLDER EXITED'", 15000, mutex));
            try
            {
                Check(SpinWait.SpinUntil(() => File.Exists(signal) || holder.IsCompleted, TimeSpan.FromSeconds(10)) && File.Exists(signal), "The inert helper did not acquire its guard.");
                bool refused = false;
                try { _ = InternetNicSystem.RunPowerShellFixture("'SHOULD NOT EXECUTE'", 5000, mutex); }
                catch (InvalidOperationException) { refused = true; }
                Check(refused, "A new helper bypassed another helper's process-owned guard.");
                Check(await holder == "HOLDER EXITED", "The original guarded helper did not complete.");
                Check(InternetNicSystem.RunPowerShellFixture("'AFTER EXIT'", 5000, mutex) == "AFTER EXIT", "The guard did not release after observed helper exit.");
            }
            finally { try { await holder; } finally { if (File.Exists(signal)) File.Delete(signal); } }
        });
        await test("Internet native helper timeout terminates only its fixture child before another helper can run", () =>
        {
            string mutex = Name(); bool timedOut = false;
            try { _ = InternetNicSystem.RunPowerShellFixture("Start-Sleep -Seconds 30; 'LATE'", 1000, mutex); }
            catch (TimeoutException) { timedOut = true; }
            Check(timedOut && InternetNicSystem.RunPowerShellFixture("'RECOVERED'", 5000, mutex) == "RECOVERED",
                "The bounded child failed timeout cleanup or left its execution guard held.");
            return Task.CompletedTask;
        });
        await test("Internet helper fixture hook refuses the production guard name", () =>
        {
            bool rejected = false;
            try { _ = InternetNicSystem.RunPowerShellFixture("throw 'must not execute'", 1000, @"Local\TourneyMode.InternetNicHelperExecution"); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "A fixture could touch the production helper guard.");
            return Task.CompletedTask;
        });
    }
    private static string Name() => @"Local\TourneyMode.InternetNicHelperFixture-" + Guid.NewGuid().ToString("N");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
