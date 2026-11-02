using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TourneyMode;

/// <summary>Temporary INI fixtures only. Never reads or writes the actual Fortnite path.</summary>
public static class FortniteSettingsTests
{
    private const string Game = "/Script/FortniteGame.FortGameUserSettings";
    private const string Fixture = "; fixture only\r\n[" + Game + "]\r\nFrameRateLimit = 144.000000 ; cap\r\nbUseVSync=True\r\nbMotionBlur=True\r\nbUseNanite=True\r\nbEnableDLSSFrameGeneration=True\r\nbUseDynamicResolution=True\r\nbLatencyTweak1=True\r\nLatencyTweak2=1\r\nUserOption=one\r\n[ScalabilityGroups]\r\nsg.ResolutionQuality=100\r\nsg.ShadowQuality=3\r\nsg.EffectsQuality=2\r\nsg.TextureQuality=3\r\n[Other]\r\nFrameRateLimit=77\r\nsg.ShadowQuality=4\r\n[D3DRHIPreference]\r\nPreferredRHI=dx11\r\nPreferredFeatureLevel=es31";

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        ArgumentNullException.ThrowIfNull(test);
        string directory = Path.Combine(Path.GetTempPath(), "TourneyMode-FortniteSettings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        int index = 0;
        string Write(string content = Fixture, Encoding? encoding = null)
        {
            string path = Path.Combine(directory, (++index) + ".ini");
            File.WriteAllText(path, content, encoding ?? new UTF8Encoding(false)); return path;
        }
        FpsLatencyOptions Options() => new() { ApplyGameProfile = true, CompetitiveVisuals = false, Reflex = FortniteReflex.KeepCurrent,
            FrameCap = FortniteFrameCap.Target, TargetFps = 240 };
        Task Run(Action action) { action(); return Task.CompletedTask; }
        try
        {
            await test("Fortnite INI preview is read-only and scopes scalar edits to recognized sections", () => Run(() =>
            {
                string path = Write(); byte[] original = File.ReadAllBytes(path);
                var backup = FortniteSettings.Preview(path, Options(), true, true);
                Check(backup.HasChanges && backup.Edits.Count == 1 && !backup.ApplyAttempted && !backup.Restored, "Preview did not describe one existing frame cap.");
                Check(File.ReadAllBytes(path).SequenceEqual(original), "Preview modified its fixture.");
                string proposed = Encoding.UTF8.GetString(backup.AppliedBytes);
                Check(proposed == Fixture.Replace("144.000000", "240.000000", StringComparison.Ordinal), "Section, whitespace, comments, ending or unrelated values changed.");
                Check(backup.OriginalSHA256 == Hash(original) && backup.AppliedSHA256 == Hash(backup.AppliedBytes), "Snapshot hashes are not the actual bytes.");
            }));
            await test("Fortnite INI apply journals before mutation and exact original recovery is durable", () => Run(() =>
            {
                string path = Write(); byte[] original = File.ReadAllBytes(path); var backup = FortniteSettings.Preview(path, Options(), true, true); int saves = 0;
                FortniteSettings.Apply(backup, () =>
                {
                    saves++; Check(backup.ApplyAttempted, "Apply attempt was not set before persistence.");
                    Check(Hash(File.ReadAllBytes(path)) == (saves == 1 ? backup.OriginalSHA256 : backup.AppliedSHA256), "Persistence was not ordered around the write.");
                }, () => true);
                Check(saves == 2 && Hash(File.ReadAllBytes(path)) == backup.AppliedSHA256, "Apply was not durably verified.");
                int restores = 0;
                FortniteSettings.Restore(backup, () => { restores++; Check(backup.RestoreExpectedSHA256.Length > 0, "Restore candidate was not journaled."); }, () => true);
                Check(backup.Restored && restores == 2 && File.ReadAllBytes(path).SequenceEqual(original), "Exact original recovery failed.");
                FortniteSettings.Restore(backup, () => throw new Exception("Repeated restore persisted again."), () => true);
            }));
            await test("Fortnite INI preserves UTF8 BOM and UTF16/UTF32 encodings through apply/restore", () => Run(() =>
            {
                foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true), new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true) })
                {
                    string path = Write(Fixture + "\r\n; unicode: \u00e9 \u6e2c\u8a66", encoding); byte[] original = File.ReadAllBytes(path);
                    var backup = FortniteSettings.Preview(path, Options(), true, true);
                    Check(backup.HasChanges && backup.AppliedBytes.AsSpan().StartsWith(encoding.GetPreamble()), "Encoding/BOM was not retained.");
                    FortniteSettings.Apply(backup, () => { }, () => true); FortniteSettings.Restore(backup, () => { }, () => true);
                    Check(File.ReadAllBytes(path).SequenceEqual(original), "Byte-exact encoded restoration failed.");
                }
            }));
            await test("Fortnite INI duplicate keys and repeated sections retain order and line endings", () => Run(() =>
            {
                string text = "[" + Game + "]\r\nFrameRateLimit=144\n[Other]\rFrameRateLimit=33\n[" + Game.ToUpperInvariant() + "]\nframeratelimit = 165 ; second\r\n";
                string path = Write(text); var backup = FortniteSettings.Preview(path, Options(), false, false);
                Check(backup.Edits.Count == 2 && backup.Edits.All(edit => edit.OriginalOccurrenceCount == 2), "Duplicate occurrence evidence was lost.");
                Check(Encoding.UTF8.GetString(backup.AppliedBytes) == text.Replace("=144", "=240.000000", StringComparison.Ordinal).Replace("= 165", "= 240.000000", StringComparison.Ordinal), "Duplicate/section/newline semantics changed.");
                FortniteSettings.Apply(backup, () => { }, () => true); FortniteSettings.Restore(backup, () => { }, () => true);
                Check(File.ReadAllText(path) == text, "Duplicate original values did not recover.");
            }));
            await test("Fortnite competitive profile changes only present supported visual keys", () => Run(() =>
            {
                string path = Write(); var options = Options(); options.FrameCap = FortniteFrameCap.KeepCurrent; options.CompetitiveVisuals = true;
                var backup = FortniteSettings.Preview(path, options, true, false); string text = Encoding.UTF8.GetString(backup.AppliedBytes);
                Check(text.Contains("sg.ShadowQuality=0") && text.Contains("sg.EffectsQuality=0") && text.Contains("sg.TextureQuality=0")
                    && text.Contains("bMotionBlur=False") && text.Contains("bUseNanite=False") && text.Contains("bEnableDLSSFrameGeneration=False"), "Supported competitive values were not proposed.");
                Check(text.Contains("[Other]\r\nFrameRateLimit=77\r\nsg.ShadowQuality=4") && !text.Contains("r.OneFrameThreadLag")
                    && !text.Contains("sg.FoliageQuality="), "Unknown/missing/other-section keys were added or changed.");
                var nonNvidia = FortniteSettings.Preview(path, options, false, false);
                Check(Encoding.UTF8.GetString(nonNvidia.AppliedBytes).Contains("bEnableDLSSFrameGeneration=True"), "NVIDIA-only setting changed without NVIDIA evidence.");
            }));
            await test("Fortnite profile enforces cap/resolution/tearing ranges and preserves unknown Reflex mapping", () => Run(() =>
            {
                string path = Write(); var options = Options(); options.ResolutionPercent = 80; options.DisableVSync = true; options.Reflex = FortniteReflex.OnPlusBoost;
                var backup = FortniteSettings.Preview(path, options, true, true); string proposed = Encoding.UTF8.GetString(backup.AppliedBytes);
                Check(proposed.Contains("sg.ResolutionQuality=80") && proposed.Contains("bUseDynamicResolution=False") && proposed.Contains("bUseVSync=False"), "Explicit valid settings were not proposed.");
                Check(proposed.Contains("bLatencyTweak1=True\r\nLatencyTweak2=1") && backup.Warnings.Any(w => w.Contains("Manual action required: set NVIDIA Reflex")), "Unknown Reflex keys were guessed or manual action hidden.");
                options.ResolutionPercent = 101; options.TargetFps = 0;
                var invalid = FortniteSettings.Preview(path, options, false, false);
                Check(!invalid.HasChanges && invalid.Warnings.Any(w => w.Contains("tearing was not accepted")), "Invalid ranges or unaccepted tearing caused writes.");
            }));
            await test("Fortnite uncapped uses the recognized zero cap without changing frontend FPS", () => Run(() =>
            {
                string path = Write(Fixture.Replace("FrameRateLimit =", "FrontendFrameRateLimit=60\r\nFrameRateLimit =", StringComparison.Ordinal));
                var options = Options(); options.FrameCap = FortniteFrameCap.Uncapped;
                var backup = FortniteSettings.Preview(path, options, true, false); string text = Encoding.UTF8.GetString(backup.AppliedBytes);
                Check(text.Contains("FrameRateLimit = 0.000000 ; cap") && text.Contains("FrontendFrameRateLimit=60"), "Uncapped touched the frontend limiter or used a guessed sentinel.");
            }));
            await test("Fortnite unverified renderer choices remain manual and do not guess serialized keys", () => Run(() =>
            {
                string path = Write(); var options = Options(); options.FrameCap = FortniteFrameCap.KeepCurrent; options.Renderer = FortniteRenderer.PerformanceDx12;
                var backup = FortniteSettings.Preview(path, options, true, false);
                Check(!backup.HasChanges && backup.Warnings.Any(w => w.Contains("Manual action required: select the requested rendering mode")), "Unverified renderer mapping was written.");
                options.Renderer = FortniteRenderer.PerformanceDx11;
                var already = FortniteSettings.Preview(path, options, true, false);
                Check(!already.HasChanges && already.AppliedSHA256 == already.OriginalSHA256, "Already matching observed renderer was rewritten.");
            }));
            await test("Fortnite missing keys, array operators and unknown scalar formats remain untouched", () => Run(() =>
            {
                foreach (string text in new[] { "[Other]\nFrameRateLimit=144", "[" + Game + "]\nFrameRateLimit=\"144\"", "[" + Game + "]\nFrameRateLimit=NaN", "[" + Game + "]\n+FrameRateLimit=144\nFrameRateLimit=144" })
                {
                    string path = Write(text); var backup = FortniteSettings.Preview(path, Options(), false, false);
                    Check(!backup.HasChanges && backup.AppliedBytes.SequenceEqual(backup.OriginalBytes) && backup.Warnings.Count > 0, "Unknown INI syntax/value was modified.");
                }
                string malformed = "[" + Game + "] malformed\nFrameRateLimit=144";
                Check(!FortniteSettings.Preview(Write(malformed), Options(), false, false).HasChanges, "Malformed section inherited a previous recognized context.");
            }));
            await test("Fortnite missing/oversized/invalid-encoding/read-only files are skipped safely", () => Run(() =>
            {
                string missing = Path.Combine(directory, "missing.ini"); var absent = FortniteSettings.Preview(missing, Options(), true, true);
                Check(!absent.Exists && !absent.HasChanges && !File.Exists(missing), "Missing settings were invented.");
                string large = Write(); File.WriteAllBytes(large, new byte[FortniteSettings.MaxFileBytes + 1]);
                Check(!FortniteSettings.Preview(large, Options(), true, true).HasChanges, "Oversized settings were accepted.");
                string unknown = Write(); File.WriteAllBytes(unknown, [0xff, 0xff, 0x61]);
                Check(!FortniteSettings.Preview(unknown, Options(), true, true).HasChanges && File.ReadAllBytes(unknown).SequenceEqual(new byte[] { 0xff, 0xff, 0x61 }), "Unknown encoding changed.");
                string readOnly = Write(); File.SetAttributes(readOnly, FileAttributes.ReadOnly);
                try { Check(!FortniteSettings.Preview(readOnly, Options(), true, true).HasChanges && File.GetAttributes(readOnly).HasFlag(FileAttributes.ReadOnly), "Read-only attributes were removed."); }
                finally { File.SetAttributes(readOnly, FileAttributes.Normal); }
            }));
            await test("Fortnite apply refuses stale files before any mutation", () => Run(() =>
            {
                string path = Write(); var backup = FortniteSettings.Preview(path, Options(), true, true);
                File.AppendAllText(path, "\r\nUserAdded=1"); byte[] changed = File.ReadAllBytes(path); int saves = 0;
                ExpectFailure(() => FortniteSettings.Apply(backup, () => saves++, () => true));
                Check(saves == 0 && !backup.ApplyAttempted && File.ReadAllBytes(path).SequenceEqual(changed), "Stale apply wrote or journaled a false successful attempt.");
            }));
            await test("Fortnite apply refuses running or unknown game and catches a late game launch", () => Run(() =>
            {
                string path = Write(); byte[] original = File.ReadAllBytes(path); var backup = FortniteSettings.Preview(path, Options(), true, true);
                ExpectFailure(() => FortniteSettings.Apply(backup, () => { }, () => false));
                ExpectFailure(() => FortniteSettings.Apply(backup, () => { }, () => throw new IOException("Unknown process state")));
                int checks = 0; ExpectFailure(() => FortniteSettings.Apply(backup, () => { }, () => ++checks < 3));
                Check(backup.ApplyAttempted && File.ReadAllBytes(path).SequenceEqual(original) && !Directory.GetFiles(directory, "*.tmp").Any(), "Late-game refusal changed settings or left its temp file.");
                FortniteSettings.Restore(backup, () => { }, () => true); Check(backup.Restored, "Unmutated journal could not be cleared by exact-original recovery.");
            }));
            await test("Fortnite final game-state callback cannot hide an external INI edit", () => Run(() =>
            {
                string path = Write(); var backup = FortniteSettings.Preview(path, Options(), true, true); int checks = 0;
                ExpectFailure(() => FortniteSettings.Apply(backup, () => { }, () =>
                { if (++checks == 3) File.AppendAllText(path, "\r\nExternal=keep"); return true; }));
                Check(File.ReadAllText(path).Contains("FrameRateLimit = 144.000000") && File.ReadAllText(path).EndsWith("External=keep", StringComparison.Ordinal), "Final callback changes were overwritten.");
            }));
            await test("Fortnite failed pre-mutation persistence never changes file bytes", () => Run(() =>
            {
                string path = Write(); byte[] original = File.ReadAllBytes(path); var backup = FortniteSettings.Preview(path, Options(), true, true);
                ExpectFailure(() => FortniteSettings.Apply(backup, () => throw new IOException("Journal unavailable"), () => true));
                Check(File.ReadAllBytes(path).SequenceEqual(original), "Failed journal persistence allowed a settings mutation.");
            }));
            await test("Fortnite recovery preserves unrelated game-exit INI rewrites", () => Run(() =>
            {
                string path = Write(); var options = Options(); options.DisableVSync = true;
                var backup = FortniteSettings.Preview(path, options, true, true); FortniteSettings.Apply(backup, () => { }, () => true);
                string current = File.ReadAllText(path).Replace("UserOption=one", "UserOption=game-exit", StringComparison.Ordinal) + "\r\nUnrelatedGameTimestamp=1234";
                File.WriteAllText(path, current, new UTF8Encoding(false)); int saves = 0;
                FortniteSettings.Restore(backup, () => { saves++; Check(backup.RestoreExpectedSHA256.Length > 0, "Merge candidate not durable before restore."); }, () => true);
                string expected = Fixture.Replace("UserOption=one", "UserOption=game-exit", StringComparison.Ordinal) + "\r\nUnrelatedGameTimestamp=1234";
                Check(backup.Restored && saves == 2 && File.ReadAllText(path) == expected && Hash(File.ReadAllBytes(path)) == backup.RestoreExpectedSHA256,
                    "Three-way restore lost unrelated game changes or original targeted values.");
            }));
            await test("Fortnite merged recovery preserves current encoding and semantically equal applied values", () => Run(() =>
            {
                string path = Write(); var backup = FortniteSettings.Preview(path, Options(), true, true); FortniteSettings.Apply(backup, () => { }, () => true);
                string changed = File.ReadAllText(path).Replace("240.000000", "240.0", StringComparison.Ordinal).Replace("UserOption=one", "UserOption=changed", StringComparison.Ordinal);
                var encoding = new UnicodeEncoding(false, true, true); File.WriteAllText(path, changed, encoding);
                FortniteSettings.Restore(backup, () => { }, () => true);
                Check(backup.Restored && File.ReadAllBytes(path).AsSpan().StartsWith(encoding.GetPreamble())
                    && File.ReadAllText(path) == Fixture.Replace("UserOption=one", "UserOption=changed", StringComparison.Ordinal), "Merged recovery changed encoding or rejected equivalent applied number formatting.");
            }));
            await test("Fortnite targeted user edits and changed duplicate semantics cause recovery conflict", () => Run(() =>
            {
                string path = Write(); var backup = FortniteSettings.Preview(path, Options(), true, true); FortniteSettings.Apply(backup, () => { }, () => true);
                File.WriteAllText(path, File.ReadAllText(path).Replace("240.000000", "180.000000", StringComparison.Ordinal)); byte[] changed = File.ReadAllBytes(path);
                ExpectFailure(() => FortniteSettings.Restore(backup, () => { }, () => true));
                Check(!backup.Restored && backup.ApplyAttempted && File.ReadAllBytes(path).SequenceEqual(changed), "Targeted user preference was overwritten or conflict discarded.");
                string duplicatePath = Write("[" + Game + "]\nFrameRateLimit=144\nFrameRateLimit=165");
                var duplicate = FortniteSettings.Preview(duplicatePath, Options(), true, true); FortniteSettings.Apply(duplicate, () => { }, () => true);
                File.WriteAllText(duplicatePath, "[" + Game + "]\nFrameRateLimit=240");
                ExpectFailure(() => FortniteSettings.Restore(duplicate, () => { }, () => true)); Check(!duplicate.Restored, "Changed duplicate occurrence semantics were guessed.");
            }));
            await test("Fortnite recovery defers while the game runs and resumes a journaled merged write after crash", () => Run(() =>
            {
                string path = Write(); var backup = FortniteSettings.Preview(path, Options(), true, true); FortniteSettings.Apply(backup, () => { }, () => true);
                File.AppendAllText(path, "\r\nKeepAfterExit=1"); byte[] appliedPlusUser = File.ReadAllBytes(path);
                ExpectFailure(() => FortniteSettings.Restore(backup, () => { }, () => false));
                Check(!backup.Restored && File.ReadAllBytes(path).SequenceEqual(appliedPlusUser), "In-game restore wrote settings.");
                string durable = ""; int saves = 0;
                ExpectFailure(() => FortniteSettings.Restore(backup, () =>
                {
                    if (++saves == 2) throw new IOException("Crash after atomic write, before completion save");
                    durable = JsonSerializer.Serialize(backup);
                }, () => true));
                var reopened = JsonSerializer.Deserialize<FortniteSettingsBackup>(durable)!;
                Check(!reopened.Restored && Hash(File.ReadAllBytes(path)) == reopened.RestoreExpectedSHA256, "Fixture did not retain the durable expected merged state.");
                File.SetAttributes(path, FileAttributes.ReadOnly);
                try { FortniteSettings.Restore(reopened, () => { }, () => true); Check(reopened.Restored, "Crash retry attempted another write instead of recognizing its merged candidate."); }
                finally { File.SetAttributes(path, FileAttributes.Normal); }
                Check(File.ReadAllText(path) == Fixture + "\r\nKeepAfterExit=1", "Resumed merge overwrote unrelated state.");
            }));
            await test("Fortnite journal validation rejects arbitrary byte or unsupported edit injection", () => Run(() =>
            {
                string path = Write(); var backup = FortniteSettings.Preview(path, Options(), true, true); byte[] original = File.ReadAllBytes(path);
                backup.AppliedBytes = Encoding.UTF8.GetBytes("Arbitrary=payload"); backup.AppliedSHA256 = Hash(backup.AppliedBytes);
                ExpectFailure(() => FortniteSettings.Apply(backup, () => { }, () => true));
                var unknown = FortniteSettings.Preview(path, Options(), true, true); unknown.Edits[0].Key = "r.UnknownConsoleVariable";
                ExpectFailure(() => FortniteSettings.Apply(unknown, () => { }, () => true));
                Check(File.ReadAllBytes(path).SequenceEqual(original), "Tampered settings journal reached file mutation.");
            }));
        }
        finally
        {
            // Delete only files created in this unique fixture directory; no recursive traversal.
            foreach (string path in Directory.GetFiles(directory))
            { try { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); } catch { } }
            try { Directory.Delete(directory); } catch { }
        }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Check(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
    private static void ExpectFailure(Action action) { bool failed = false; try { action(); } catch { failed = true; } Check(failed, "Expected refusal/conflict was silently accepted."); }
}
