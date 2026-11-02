using System.Text.Json;

namespace TourneyMode;

/// <summary>Pure supplied-inventory fixtures. No files, network, devices, app launch or live changes.</summary>
public static class OptimizationGuideTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        ArgumentNullException.ThrowIfNull(test);
        await test("optimization guide leaves unknown hardware and capability facts unknown", () =>
        {
            var report = Unknown();
            var guide = OptimizationGuide.Build(report);
            Check(guide.Count is >= 20 and <= 30, "Unknown inventory produced an invalid guide size.");
            Check(!guide.Any(i => i.Area is "CPU stability" or "NVIDIA latency" or "AMD latency")
                && !guide.Any(i => i.Title == "Retain AMD X3D provisioning"), "Unknown hardware invented a CPU/GPU family.");
            var bios = Find(guide, "Use firmware for the exact board");
            Check(Has(bios.Detected, "unknown") && Has(bios.Recommendation, "Confirm the exact")
                && !bios.SourceUrls.Any(u => Has(u, "asus")), "Unknown board received a concrete OEM or firmware prescription.");
            Check(Has(Find(guide, "Keep hardware-aware scheduling").Detected, "unknown reported cores / unknown threads"),
                "Missing core/thread counts were presented as measured zero-core hardware.");
            Check(Has(Find(guide, "Compare supported AC performance modes").Recommendation, "Confirm AC status"),
                "Unknown power state was treated as confirmed AC.");
            Check(Has(Find(guide, "Validate XMP or EXPO stability").Detected, "profile, channels and stability unknown")
                && Has(Find(guide, "Verify Resizable BAR and Above 4G").Detected, "unknown")
                && Has(Find(guide, "A/B HAGS on this driver").Detected, "absent/unknown"), "Missing capabilities were treated as active.");
            return Task.CompletedTask;
        });
        await test("optimization guide handles Intel 14900KF defaults without inferring microcode from BIOS date", () =>
        {
            var report = Intel();
            report.Hardware.Bios = "Fixture BIOS 9999";
            report.Hardware.BiosReleaseDate = "2099-01-01";
            var guide = OptimizationGuide.Build(report);
            var intel = Find(guide, "Intel desktop defaults and microcode first");
            Check(Has(intel.Recommendation, "Intel Default Settings") && Has(intel.Recommendation, "0x12F or later")
                && Has(intel.Detected, "microcode and Intel Default Settings are unmeasured")
                && Has(intel.Why, "125 W base/253 W"), "14900KF guidance lost its mitigation, model limits or unknown-state disclosure.");
            Check(guide.Count == OptimizationGuide.Build(Unknown()).Count + 2, "Intel/NVIDIA detection added unexpected items.");
            Check(Has(Find(guide, "Use firmware for the exact board").Why, "date does not prove microcode"),
                "A BIOS date was treated as installed microcode evidence.");
            return Task.CompletedTask;
        });
        await test("optimization guide scopes Intel mitigation and SKU sources to supported desktop families", () =>
        {
            var report = Intel(); report.Hardware.Cpu = "13th Gen Intel(R) Core(TM) i5-13500";
            var intel = Find(OptimizationGuide.Build(report), "Intel desktop defaults and microcode first");
            Check(!Has(Text(intel), "125 W") && !Has(Text(intel), "253 W") && Has(intel.Why, "exact SKU")
                && !intel.SourceUrls.Any(u => Has(u, "/sku/236787/")),
                "i5 inherited 14900KF power specifications.");
            report.Hardware.Cpu = "Intel(R) Core(TM) i9-13900HX";
            Check(!OptimizationGuide.Build(report).Any(i => i.Area == "CPU stability"), "Desktop instability guidance was inferred for a mobile suffix.");
            report.Hardware.Cpu = "Intel(R) Core(TM) i3-14100";
            Check(!OptimizationGuide.Build(report).Any(i => i.Area == "CPU stability"), "The cited i5/i7/i9 mitigation guidance was extended to i3.");
            return Task.CompletedTask;
        });
        await test("optimization guide distinguishes asymmetric X3D from dual-cache and single-CCD models", () =>
        {
            var report = Amd("AMD Ryzen 9 7950X3D 16-Core Processor");
            var asymmetric = Find(OptimizationGuide.Build(report), "Retain AMD X3D provisioning");
            Check(Has(asymmetric.Detected, "recognized conventional asymmetric dual-CCD")
                && Has(asymmetric.Recommendation, "PPM provisioning") && Has(asymmetric.Recommendation, "do not strip"),
                "Conventional asymmetric X3D guidance lost required provisioning.");
            report.Hardware.Cpu = "AMD Ryzen 9 9950X3D2 Dual Edition";
            var dual = Find(OptimizationGuide.Build(report), "Retain AMD X3D provisioning");
            Check(Has(dual.Detected, "recognized dual-cache model") && !Has(dual.Detected, "asymmetric")
                && Has(dual.Why, "V-Cache on both CCDs") && dual.SourceUrls.Any(u => Has(u, "9950x3d2")),
                "9950X3D2 was assigned the conventional asymmetric-cache assertion.");
            report.Hardware.Cpu = "Ryzen 7 9800X3D";
            var single = Find(OptimizationGuide.Build(report), "Retain AMD X3D provisioning");
            Check(!Has(single.Detected, "asymmetric") && Has(single.Detected, "topology unknown"),
                "X3D branding alone invented dual-CCD topology.");
            return Task.CompletedTask;
        });
        await test("optimization guide matches firmware references to the reported OEM only", () =>
        {
            foreach (var (manufacturer, host) in new[]
            {
                ("ASUSTeK COMPUTER INC.", "www.asus.com"), ("Micro-Star International Co., Ltd.", "www.msi.com"),
                ("Gigabyte Technology Co., Ltd.", "www.gigabyte.com"), ("ASRock", "www.asrock.com"),
                ("Dell Inc.", "www.dell.com"), ("HP", "support.hp.com"),
                ("LENOVO", "pcsupport.lenovo.com"), ("Acer", "www.acer.com")
            })
            {
                var report = Unknown();
                report.Hardware.MotherboardManufacturer = manufacturer;
                report.Hardware.MotherboardModel = "Fixture exact board";
                var bios = Find(OptimizationGuide.Build(report), "Use firmware for the exact board");
                Check(bios.SourceUrls.Length == 1 && new Uri(bios.SourceUrls[0]).Host == host,
                    "The " + manufacturer + " firmware card referenced another OEM.");
            }
            var absent = Amd("AMD Ryzen 7 9800X3D");
            absent.Hardware.MotherboardManufacturer = "To be filled by O.E.M.";
            absent.Hardware.MotherboardModel = "Default string";
            var unknown = Find(OptimizationGuide.Build(absent), "Use firmware for the exact board");
            Check(Has(unknown.Detected, "Exact motherboard unknown") && Has(unknown.Recommendation, "Confirm the exact")
                && unknown.SourceUrls.All(u => new Uri(u).Host is "www.intel.com" or "www.amd.com"),
                "OEM placeholders were treated as a known motherboard or mapped to ASUS.");
            return Task.CompletedTask;
        });
        await test("optimization guide presents each detected GPU vendor without inventing native feature support", () =>
        {
            var report = Amd("AMD Ryzen 9 7950X3D");
            report.Hardware.Gpus.Add(new GpuFact { Name = "NVIDIA GeForce RTX 5080", Driver = "fixture" });
            var guide = OptimizationGuide.Build(report);
            Check(guide.Count <= 30 && guide.Count == OptimizationGuide.Build(Unknown()).Count + 3
                && guide.Count(i => i.Area == "NVIDIA latency") == 1 && guide.Count(i => i.Area == "AMD latency") == 1,
                "Mixed GPU inventory duplicated or omitted vendor-specific guidance.");
            Check(Has(guide.Single(i => i.Area == "NVIDIA latency").Recommendation, "If Fortnite exposes")
                && Has(guide.Single(i => i.Area == "AMD latency").Recommendation, "do not claim Fortnite support"),
                "A GPU name was treated as proof of native Reflex/Anti-Lag availability.");
            return Task.CompletedTask;
        });
        await test("optimization guide has exactly three explicitly off experimental advisory items", () =>
        {
            foreach (var report in Cases())
            {
                var guide = OptimizationGuide.Build(report);
                var experimental = guide.Where(i => i.Area.StartsWith("Experimental/", StringComparison.Ordinal)).ToList();
                Check(experimental.Count == 3 && experimental.All(i => i.Recommendation.StartsWith("Experimental, off by default:", StringComparison.Ordinal)),
                    "Experiments were added, enabled by default or lost their verdict.");
                Check(experimental.Select(i => i.Area).ToHashSet().SetEquals(new[] { "Experimental/NIC", "Experimental/QoS", "Experimental/priority" }),
                    "An unsupported experimental mechanism was exposed.");
                Check(Has(experimental.Single(i => i.Area == "Experimental/NIC").Recommendation, "physical wired")
                    && Has(experimental.Single(i => i.Area == "Experimental/QoS").Why, "does not reserve")
                    && Has(experimental.Single(i => i.Area == "Experimental/priority").Recommendation, "known user apps"),
                    "Experimental eligibility or tradeoffs were omitted.");
            }
            return Task.CompletedTask;
        });
        await test("optimization guide is manual, source-linked and deterministic from supplied inventory", () =>
        {
            foreach (var report in Cases())
            {
                string before = JsonSerializer.Serialize(report);
                var first = OptimizationGuide.Build(report); var second = OptimizationGuide.Build(report);
                Check(first.Count is >= 20 and <= 30 && first.Select(i => i.Title).Distinct().Count() == first.Count,
                    "Guide size or unique item identity is invalid.");
                Check(first.All(i => i.RequiresManualAction && !string.IsNullOrWhiteSpace(i.Area)
                    && !string.IsNullOrWhiteSpace(i.Detected) && !string.IsNullOrWhiteSpace(i.Why)
                    && !string.IsNullOrWhiteSpace(i.Tradeoff) && (Has(i.Confidence, "unmeasured") || Has(i.Confidence, "Conditional"))),
                    "Advice lost manual status or its evidence/tradeoff qualification.");
                Check(first.All(i => new[] { "Supported:", "Optional A/B:", "Manual:", "Avoid blind defaults:", "Experimental, off by default:" }
                    .Any(v => i.Recommendation.StartsWith(v, StringComparison.Ordinal))), "Advice has no supported verdict.");
                Check(before == JsonSerializer.Serialize(report) && JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second),
                    "Guide mutated inventory or used nondeterministic/live data instead of supplied facts.");
                foreach (var item in first)
                {
                    Check(item.SourceUrls.Length > 0, "Advice has no source.");
                    foreach (string source in item.SourceUrls)
                        Check(OfficialSource(source), "A source is not an allowed official HTTPS reference: " + source);
                }
            }
            // Also tolerate absent optional list facts without probing their live replacements.
            var missingLists = Unknown();
            missingLists.Hardware.Gpus = null!; missingLists.Hardware.Adapters = null!;
            missingLists.Displays = null!; missingLists.CandidateCosts = null!; missingLists.Volumes = null!;
            Check(OptimizationGuide.Build(missingLists).Count is >= 20 and <= 30, "Missing list facts caused live fallback requirements.");
            var missingHardware = Unknown(); missingHardware.Hardware = null!;
            Check(OptimizationGuide.Build(missingHardware).Count is >= 20 and <= 30, "Missing hardware required a live fallback.");
            return Task.CompletedTask;
        });
    }

    private static bool OfficialSource(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
        if (uri.Host == "github.com") return uri.AbsolutePath == "/GameTechDev/PresentMon";
        return new[]
        {
            "www.intel.com", "www.amd.com", "community.amd.com", "gpuopen.com", "www.nvidia.com", "developer.nvidia.com",
            "nvidia.custhelp.com", "learn.microsoft.com", "support.microsoft.com", "devblogs.microsoft.com", "news.xbox.com",
            "www.epicgames.com", "store.epicgames.com", "dev.epicgames.com", "docs.gl-inet.com", "openwrt.org", "www.rfc-editor.org",
            "winutil.christitus.com", "docs.atlasos.net", "revi.cc", "www.asus.com", "www.msi.com", "www.gigabyte.com",
            "www.asrock.com", "www.dell.com", "support.hp.com", "pcsupport.lenovo.com", "www.acer.com"
        }.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }
    private static IEnumerable<PcAnalysis> Cases()
    {
        yield return Unknown(); yield return Intel(); yield return Amd("AMD Ryzen 9 7950X3D");
        yield return Amd("AMD Ryzen 9 9950X3D2 Dual Edition"); yield return Amd("AMD Ryzen 7 9800X3D");
        var mixed = Intel(); mixed.Hardware.Gpus.Add(new GpuFact { Name = "AMD Radeon RX 9070 XT", Driver = "fixture" }); yield return mixed;
    }
    private static PcAnalysis Unknown() => new() { CapturedUtc = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc), Hardware = new() };
    private static PcAnalysis Intel()
    {
        var report = Unknown(); report.Hardware.Cpu = "Intel(R) Core(TM) i9-14900KF";
        report.Hardware.Cores = 24; report.Hardware.Threads = 32; report.Hardware.RamGiB = 64;
        report.Hardware.Gpus.Add(new GpuFact { Name = "NVIDIA GeForce RTX 4090", Driver = "fixture" }); return report;
    }
    private static PcAnalysis Amd(string cpu)
    {
        var report = Unknown(); report.Hardware.Cpu = cpu;
        report.Hardware.Gpus.Add(new GpuFact { Name = "AMD Radeon RX 9070 XT", Driver = "fixture" }); return report;
    }
    private static OptimizationGuideItem Find(List<OptimizationGuideItem> guide, string title) => guide.Single(i => i.Title == title);
    private static string Text(OptimizationGuideItem item) => item.Detected + " " + item.Recommendation + " " + item.Why + " " + item.Tradeoff;
    private static bool Has(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
