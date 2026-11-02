using Microsoft.Win32;
using System.Text.Json;

namespace TourneyMode;

/// <summary>Dictionary-backed registry fixtures only. No real registry, environment detection or policy writes.</summary>
internal static class WindowsProfileTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        ArgumentNullException.ThrowIfNull(test);
        await test("Windows profile is a finite twelve-value catalogue with no invented performance settings", () =>
        {
            var store = new FakeStore(); var backup = Capture(store);
            Check(backup.Values.Count == 12 && backup.Values.Select(value => value.Id).Distinct().Count() == 12 && store.Writes.Count == 0,
                "The supported catalogue changed size, duplicates identities or writes during capture.");
            Check(backup.Values.All(value => !value.Key.Contains("Services", StringComparison.OrdinalIgnoreCase)
                && !value.Key.Contains("PriorityControl", StringComparison.OrdinalIgnoreCase) && !value.Key.Contains("Multimedia", StringComparison.OrdinalIgnoreCase)),
                "An unreviewed CPU/service/timer setting entered the profile.");
            var delivery = backup.Values.Single(value => value.Id == "delivery-peers");
            Check(delivery.Name == "DODownloadMode" && delivery.Applied == "0" && delivery.Kind == RegistryValueKind.DWord
                && delivery.Note.Contains("remain enabled", StringComparison.Ordinal), "Delivery Optimization was confused with disabling updates or its HTTP-only behavior was omitted.");
            return Task.CompletedTask;
        });
        await test("Windows profile captures and restores exact DWORD, string and missing-value originals", () =>
        {
            var store = new FakeStore(); var catalogue = Capture(store);
            store.Set(Find(catalogue, "transparency"), RegistryValueKind.DWord, "1");
            store.Set(Find(catalogue, "menu-delay"), RegistryValueKind.String, "400");
            var backup = Only(Capture(store), "transparency", "menu-delay", "window-animation"); store.Bind(backup);
            WindowsProfile.Apply(backup, store.Persist, () => true, CancellationToken.None, store);
            Check(backup.Values.All(value => value.Attempted) && store.Read(Find(backup, "menu-delay")).Kind == RegistryValueKind.String,
                "Application changed value kind or omitted journaled attempts.");
            WindowsProfile.Restore(backup, store.Persist, () => true, store);
            Check(backup.Values.All(value => value.Restored) && store.Read(Find(backup, "transparency")).Value == "1"
                && store.Read(Find(backup, "transparency")).Kind == RegistryValueKind.DWord
                && store.Read(Find(backup, "menu-delay")).Value == "400" && !store.Read(Find(backup, "window-animation")).Existed,
                "Recovery lost original kind/value/existence.");
            return Task.CompletedTask;
        });
        await test("Windows profile already-correct settings are omitted and unsupported types are preserved", () =>
        {
            var store = new FakeStore(); var catalogue = Capture(store);
            foreach (var value in catalogue.Values) store.Set(value, value.Kind, value.Applied);
            Check(!Capture(store).HasChanges, "Already-correct settings were selected for writes.");
            store = new FakeStore(); store.Set(Find(catalogue, "transparency"), RegistryValueKind.Binary, "opaque");
            store.Set(Find(catalogue, "menu-delay"), RegistryValueKind.ExpandString, "%USERPROFILE%");
            var backup = Capture(store);
            Check(backup.Values.All(value => value.Id is not "transparency" and not "menu-delay") && backup.Notices.Count >= 2 && store.Writes.Count == 0,
                "Unsupported registry types were coerced or mutated.");
            return Task.CompletedTask;
        });
        await test("Windows profile policy eligibility fails closed for unknown edition and domain state", () =>
        {
            foreach (bool? domain in new bool?[] { true, null })
            {
                var store = new FakeStore(); var facts = Facts(); facts.DomainJoined = domain;
                var backup = WindowsProfile.Capture(facts, store);
                Check(backup.Values.Count == 5 && backup.Values.All(value => !value.Key.Contains("Policies", StringComparison.Ordinal)),
                    "Joined/unknown domain status permitted local policy overrides.");
            }
            foreach (string edition in new[] { "Unavailable", "ProfessionalMystery", "Home", "Core" })
            {
                var store = new FakeStore { EdgeInstalled = false }; var facts = Facts(); facts.WindowsEdition = edition;
                Check(WindowsProfile.Capture(facts, store).Values.Count == 5, "Unknown/unsupported Windows edition enabled OS policies.");
            }
            var proEducation = Facts(); proEducation.WindowsEdition = "ProfessionalEducation";
            var pro = WindowsProfile.Capture(proEducation, new FakeStore());
            Check(pro.Values.All(value => value.Id is not "consumer-features" and not "spotlight"), "Pro Education was incorrectly treated as Enterprise/Education SKU.");
            return Task.CompletedTask;
        });
        await test("Windows profile Edge policy eligibility does not depend on Pro SKU and honors app availability", () =>
        {
            var facts = Facts(); facts.WindowsEdition = "Core";
            var home = WindowsProfile.Capture(facts, new FakeStore());
            Check(home.Values.Count == 7 && home.Values.Count(value => value.Id.StartsWith("edge-", StringComparison.Ordinal)) == 2,
                "Supported Edge policies were incorrectly tied to the Windows Pro SKU.");
            var absent = Capture(new FakeStore { EdgeInstalled = false });
            Check(absent.Values.All(value => !value.Id.StartsWith("edge-", StringComparison.Ordinal)), "Missing Edge received policy requests.");
            return Task.CompletedTask;
        });
        await test("Windows profile minimum build gates Widgets and unsupported Windows versions", () =>
        {
            Check(Capture(new FakeStore { WindowsBuild = 19041 }).Values.All(value => value.Id != "widgets"), "Windows 10 received the Windows 11 Widgets policy.");
            Check(!Capture(new FakeStore { WindowsBuild = 18000 }).HasChanges && !Capture(new FakeStore { WindowsBuild = -1 }).HasChanges,
                "Unsupported or unknown Windows build received profile changes.");
            return Task.CompletedTask;
        });
        await test("Windows profile preserves existing policy values in either registry hive", () =>
        {
            foreach (bool otherHive in new[] { false, true })
            {
                var store = new FakeStore(); var edge = Find(Capture(store), "edge-background");
                store.Set(otherHive ? "HKLM" : edge.Hive, edge.Key, edge.Name, RegistryValueKind.DWord, "1");
                var backup = Capture(store);
                Check(backup.Values.All(value => value.Id != edge.Id) && backup.Notices.Any(note => note.Contains("existing policy", StringComparison.Ordinal)) && store.Writes.Count == 0,
                    "An existing user/device policy was overridden or omitted from disclosure.");
            }
            return Task.CompletedTask;
        });
        await test("Windows profile newly managed policy or stale late value blocks the complete selection before writes", () =>
        {
            var store = new FakeStore(); var backup = Capture(store); store.Bind(backup);
            var last = backup.Values.Last(); store.Set(last, last.Kind, "9");
            Expect<InvalidOperationException>(() => WindowsProfile.Apply(backup, store.Persist, () => true, CancellationToken.None, store));
            Check(store.Writes.Count == 0 && backup.Values.All(value => !value.Attempted), "Stale late evidence was discovered only after earlier writes.");
            store = new FakeStore(); backup = Capture(store); store.Bind(backup); var edge = Find(backup, "edge-background");
            store.Set("HKLM", edge.Key, edge.Name, RegistryValueKind.DWord, "1");
            Expect<InvalidOperationException>(() => WindowsProfile.Apply(backup, store.Persist, () => true, CancellationToken.None, store));
            Check(store.Writes.Count == 0, "Newly managed policy did not invalidate the full preflight.");
            return Task.CompletedTask;
        });
        await test("Windows profile rejects unknown targets, duplicate values and forged existing-policy originals", () =>
        {
            var store = new FakeStore(); var backup = Only(Capture(store), "transparency"); store.Bind(backup); backup.Values[0].Key = "UnreviewedKey";
            Expect<InvalidDataException>(() => WindowsProfile.Apply(backup, store.Persist, () => true, CancellationToken.None, store));
            backup = Only(Capture(store), "transparency"); backup.Values.Add(Clone(backup.Values[0])); store.Bind(backup);
            Expect<InvalidDataException>(() => WindowsProfile.Apply(backup, store.Persist, () => true, CancellationToken.None, store));
            backup = Only(Capture(store), "edge-background"); backup.Values[0].Existed = true; backup.Values[0].Original = "1"; store.Bind(backup);
            Expect<InvalidDataException>(() => WindowsProfile.Apply(backup, store.Persist, () => true, CancellationToken.None, store));
            Check(store.Writes.Count == 0, "An invalid profile wrote to the store.");
            return Task.CompletedTask;
        });
        await test("Windows profile partial write failures retain durable originals for recovery", () =>
        {
            var store = new FakeStore(); var backup = Only(Capture(store), "transparency", "taskbar-animation"); store.Bind(backup); store.FailAfterWrite = true;
            Expect<IOException>(() => WindowsProfile.Apply(backup, store.Persist, () => true, CancellationToken.None, store));
            Check(store.Durable!.Values[0].Attempted && !store.Durable.Values[0].Existed && !store.Durable.Values[0].Restored
                && !backup.Values[1].Attempted && store.Writes.Count == 1, "Partial failure lost originals or continued into unattempted values.");
            var reopened = Clone(store.Durable); store.Bind(reopened);
            WindowsProfile.Restore(reopened, store.Persist, () => true, store);
            Check(reopened.Values[0].Restored && !store.Read(reopened.Values[0]).Existed, "Serialized originals could not repair a partial application.");
            return Task.CompletedTask;
        });
        await test("Windows profile readback failure never declares a value restored or loses its attempt", () =>
        {
            var store = new FakeStore { IgnoreWrite = true }; var backup = Only(Capture(store), "transparency"); store.Bind(backup);
            Expect<InvalidOperationException>(() => WindowsProfile.Apply(backup, store.Persist, () => true, CancellationToken.None, store));
            Check(backup.Values[0].Attempted && !backup.Values[0].Restored && store.Durable!.Values[0].Attempted,
                "Readback failure discarded its recovery intent.");
            int count = store.Writes.Count;
            WindowsProfile.Restore(backup, store.Persist, () => true, store);
            Check(backup.Values[0].Restored && store.Writes.Count == count, "An already-original value was unnecessarily rewritten during recovery.");
            return Task.CompletedTask;
        });
        await test("Windows profile three-way restore preserves external changes and restores independent values", () =>
        {
            var store = new FakeStore(); var catalogue = Capture(store); store.Set(Find(catalogue, "transparency"), RegistryValueKind.DWord, "1");
            var backup = Only(Capture(store), "transparency", "taskbar-animation"); store.Bind(backup);
            WindowsProfile.Apply(backup, store.Persist, () => true, CancellationToken.None, store);
            store.Set(Find(backup, "transparency"), RegistryValueKind.DWord, "2");
            Expect<InvalidOperationException>(() => WindowsProfile.Restore(backup, store.Persist, () => true, store));
            Check(store.Read(Find(backup, "transparency")).Value == "2" && !Find(backup, "transparency").Restored
                && Find(backup, "taskbar-animation").Restored, "An external edit was overwritten or prevented independent recovery.");
            return Task.CompletedTask;
        });
        await test("Windows profile external type changes remain untouched during restore", () =>
        {
            var store = new FakeStore(); var backup = Only(Capture(store), "transparency"); store.Bind(backup);
            WindowsProfile.Apply(backup, store.Persist, () => true, CancellationToken.None, store);
            store.Set(backup.Values[0], RegistryValueKind.String, "0"); int before = store.Writes.Count;
            Expect<InvalidOperationException>(() => WindowsProfile.Restore(backup, store.Persist, () => true, store));
            Check(store.Writes.Count == before && store.Read(backup.Values[0]).Kind == RegistryValueKind.String && !backup.Values[0].Restored,
                "Recovery coerced an external value type.");
            return Task.CompletedTask;
        });
        await test("Windows profile game presence and cancellation block before mutation including after persistence", () =>
        {
            var store = new FakeStore(); var backup = Only(Capture(store), "transparency"); store.Bind(backup);
            Expect<InvalidOperationException>(() => WindowsProfile.Apply(backup, store.Persist, () => false, CancellationToken.None, store));
            Check(store.Writes.Count == 0 && !backup.Values[0].Attempted, "Present/unknown game absence allowed writes.");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Expect<OperationCanceledException>(() => WindowsProfile.Apply(backup, store.Persist, () => true, canceled.Token, store));
            using var mid = new CancellationTokenSource(); store.AfterPersist = mid.Cancel;
            Expect<OperationCanceledException>(() => WindowsProfile.Apply(backup, store.Persist, () => true, mid.Token, store));
            Check(store.Writes.Count == 0 && store.Durable!.Values[0].Attempted, "Cancellation after journal persistence was ignored.");
            store.AfterPersist = null; WindowsProfile.Restore(backup, store.Persist, () => true, store);
            Check(backup.Values[0].Restored, "Canceled pre-write intent could not be resolved.");
            return Task.CompletedTask;
        });
        await test("Windows profile restore waits for verified game absence and optional cancellation", () =>
        {
            var store = new FakeStore(); var backup = Only(Capture(store), "transparency"); store.Bind(backup);
            WindowsProfile.Apply(backup, store.Persist, () => true, CancellationToken.None, store); int before = store.Writes.Count;
            Expect<InvalidOperationException>(() => WindowsProfile.Restore(backup, store.Persist, () => false, store));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Expect<OperationCanceledException>(() => WindowsProfile.Restore(backup, store.Persist, () => true, store, canceled.Token));
            Check(store.Writes.Count == before && !backup.Values[0].Restored, "Blocked recovery modified settings or discarded its originals.");
            return Task.CompletedTask;
        });
        await test("Windows profile subset restore leaves all other values and backups untouched", () =>
        {
            var store = new FakeStore(); var backup = Only(Capture(store), "transparency", "taskbar-animation"); store.Bind(backup);
            WindowsProfile.Apply(backup, store.Persist, () => true, CancellationToken.None, store);
            var selected = Only(backup, "transparency");
            WindowsProfile.Restore(selected, store.Persist, () => true, store);
            Check(Find(backup, "transparency").Restored && !Find(backup, "taskbar-animation").Restored
                && !store.Read(Find(backup, "transparency")).Existed && store.Read(Find(backup, "taskbar-animation")).Value == "0",
                "Subset recovery changed an unselected value or its recovery marker.");
            return Task.CompletedTask;
        });
        await test("Windows profile fingerprints are stable and bind value identity, originals and selections", () =>
        {
            var backup = Capture(new FakeStore()); var same = Clone(backup);
            Check(backup.Fingerprint == same.Fingerprint && backup.Fingerprint.Length == 64, "Equivalent profile evidence has an unstable fingerprint.");
            same.Values[0].Original = "changed"; Check(backup.Fingerprint != same.Fingerprint, "The fingerprint omitted original evidence.");
            Check(backup.Fingerprint != Only(backup, "transparency").Fingerprint, "The fingerprint omitted selection scope.");
            return Task.CompletedTask;
        });
    }

    private static PcFacts Facts() => new() { WindowsEdition = "Enterprise", DomainJoined = false };
    private static WindowsProfileBackup Capture(FakeStore store) => WindowsProfile.Capture(Facts(), store);
    private static WindowsProfileValue Find(WindowsProfileBackup backup, string id) => backup.Values.Single(value => value.Id == id);
    private static WindowsProfileBackup Only(WindowsProfileBackup backup, params string[] ids) => new() { Values = backup.Values.Where(value => ids.Contains(value.Id, StringComparer.Ordinal)).ToList() };
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name + " was not thrown."); }
    private sealed class FakeStore : IWindowsProfileStore
    {
        public int WindowsBuild { get; set; } = 26200;
        public bool EdgeInstalled { get; set; } = true;
        private readonly Dictionary<string, WindowsProfileStoredValue> data = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Writes = [];
        public WindowsProfileBackup? Current, Durable;
        public bool FailAfterWrite, IgnoreWrite;
        public Action? AfterPersist;
        private static string Address(string hive, string key, string name) => hive + "|" + key + "|" + name;
        public WindowsProfileStoredValue Read(string hive, string key, string name) => data.TryGetValue(Address(hive, key, name), out var value) ? value : new();
        public WindowsProfileStoredValue Read(WindowsProfileValue value) => Read(value.Hive, value.Key, value.Name);
        public void Set(WindowsProfileValue value, RegistryValueKind kind, string text) => Set(value.Hive, value.Key, value.Name, kind, text);
        public void Set(string hive, string key, string name, RegistryValueKind kind, string text) => data[Address(hive, key, name)] = new() { Existed = true, Kind = kind, Value = text };
        public void Bind(WindowsProfileBackup backup) { Current = backup; Durable = Clone(backup); }
        public void Persist() { Check(Current != null, "No fixture journal was bound."); Durable = Clone(Current); AfterPersist?.Invoke(); }
        public void Write(string hive, string key, string name, RegistryValueKind kind, string value)
        {
            AssertJournal(hive, key, name); Writes.Add("Write:" + Address(hive, key, name));
            if (!IgnoreWrite) Set(hive, key, name, kind, value);
            if (FailAfterWrite) { FailAfterWrite = false; throw new IOException("Fixture partial registry write failure."); }
        }
        public void Delete(string hive, string key, string name)
        { AssertJournal(hive, key, name); Writes.Add("Delete:" + Address(hive, key, name)); data.Remove(Address(hive, key, name)); }
        private void AssertJournal(string hive, string key, string name)
        {
            var saved = Durable?.Values.SingleOrDefault(value => value.Hive == hive && value.Key == key && value.Name == name);
            Check(saved?.Attempted == true && !saved.Restored, "Registry mutation preceded a durable exact original and attempt.");
        }
    }
}
