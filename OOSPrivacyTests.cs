using System.Diagnostics;

namespace TourneyMode;

/// <summary>Pure cfg review and inert-file mocked external launch fixtures. No O&O process is run.</summary>
public static class OOSPrivacyTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("O&O cfg review does not claim recommended selection or exact rollback", () =>
        {
            var result = OOSPrivacy.ParseSettingsText("# comment\nS003;+\nM003\t-\tdescription\n");
            Check(result.SettingCount == 2 && result.EnabledCount == 1 && result.DisabledCount == 1
                && !result.EstablishesRecommendedOnly && !result.EstablishesExactRecovery, "Cfg review made an unsupported recovery or recommendation claim.");
            foreach (string text in new[] { "", "# comment", "S003 +", "S003;+\nS003;-", "S003;maybe", "<CustomProfile />", "S003;+\0", new string('x', 270000) })
                Expect(() => OOSPrivacy.ParseSettingsText(text));
            return Task.CompletedTask;
        });
        await test("O&O external tool inspection rejects untrusted publisher/product/version", () =>
        {
            string path = Fixture();
            foreach (var found in new[]
            {
                new OOSFileIdentity(false, "O&O Software GmbH", "O&O ShutUp10", OOSPrivacy.SupportedVersion),
                new OOSFileIdentity(true, "Other Publisher", "O&O ShutUp10", OOSPrivacy.SupportedVersion),
                new OOSFileIdentity(true, "O&O Software GmbH", "Different O&O app", OOSPrivacy.SupportedVersion),
                new OOSFileIdentity(true, "O&O Software GmbH", "O&O ShutUp10", new Version(1, 9, 1440, 0))
            })
            {
                int launches = 0;
                var tool = new OOSPrivacy(_ => found, _ => { launches++; return 123; }, () => null, () => true);
                Check(!tool.Inspect(path).Verified && !tool.OpenInterface(path, () => null).Started && launches == 0, "An unverified external tool was launched.");
            }
            return Task.CompletedTask;
        });
        await test("O&O interface is gated before inspection and again before launch", () =>
        {
            string path = Fixture(); int inspections = 0, launches = 0, gates = 0;
            var tool = new OOSPrivacy(_ => { inspections++; return Identity(); }, _ => { launches++; return 123; }, () => null, () => true);
            Check(!tool.OpenInterface(path, () => "fixture active mode").Started && inspections == 0 && launches == 0, "A blocked caller inspected or launched a tool.");
            Check(!tool.OpenInterface(path, () => ++gates == 1 ? null : "fixture became active").Started
                && inspections == 1 && launches == 0, "The final safety gate was not checked.");
            Check(!tool.OpenInterface(path, () => throw new IOException("fixture query failure")).Started && launches == 0, "A failed guard query was treated as idle.");
            var systemBlocked = new OOSPrivacy(_ => Identity(), _ => { launches++; return 123; }, () => "fixture Fortnite/pending recovery", () => true);
            Check(!systemBlocked.OpenInterface(path, () => null).Started && launches == 0, "The system safety gate was ignored.");
            return Task.CompletedTask;
        });
        await test("O&O launch uses a verified locked external image without cfg or switches", () =>
        {
            string path = Fixture(); bool locked = false; ProcessStartInfo? captured = null;
            var tool = new OOSPrivacy(_ => Identity(), info =>
            {
                captured = info;
                try { using var unexpected = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }
                catch (IOException) { locked = true; }
                return 123;
            }, () => null, () => true);
            var inspection = tool.Inspect(path); var result = tool.OpenInterface(path, () => null);
            Check(inspection.Verified && inspection.Sha256.Length == 64 && result.Started && result.ProcessId == 123 && locked
                && captured != null && captured.FileName == path && captured.ArgumentList.Count == 0 && captured.Arguments.Length == 0
                && !captured.UseShellExecute, "External launch was not locked, verified and parameterless.");
            Check(result.Message.Contains("has not applied", StringComparison.Ordinal), "Opening an interface claimed successful privacy changes.");
            return Task.CompletedTask;
        });
        await test("O&O interface rejects unsupported platforms and ambiguous local paths", () =>
        {
            string path = Fixture(); int launches = 0;
            var unsupported = new OOSPrivacy(_ => Identity(), _ => { launches++; return 123; }, () => null, () => false);
            Check(!unsupported.Inspect(path).Verified && !unsupported.OpenInterface(path, () => null).Started && launches == 0, "An unsupported platform reached execution.");
            foreach (string invalid in new[] { "OOSU10.exe", "\\\\server\\tool\\OOSU10.exe", "\\\\?\\C:\\OOSU10.exe", "C:\\tool\\OOSU10.exe:stream", "C:\\tool.\\OOSU10.exe", "C:\\tool \\OOSU10.exe", "C:\\tool\\..\\OOSU10.exe" })
                Expect(() => OOSPrivacy.ValidateLocalFile(invalid, ".exe"));
            return Task.CompletedTask;
        });
    }

    private static OOSFileIdentity Identity() => new(true, "O&O Software GmbH", "O&O ShutUp10", OOSPrivacy.SupportedVersion);
    private static string Fixture()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TMODE-OOS-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "OOSU10.exe"); File.WriteAllBytes(path, [1, 2, 3, 4]); return path;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("An invalid O&O input was accepted.");
    }
}
