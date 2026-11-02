using System.Globalization;
using System.Text.RegularExpressions;

namespace TourneyMode;

public sealed class OptimizationGuideItem
{
    public string Area { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detected { get; set; } = "";
    public string Recommendation { get; set; } = "";
    public string Why { get; set; } = "";
    public string Tradeoff { get; set; } = "";
    public bool NeedsRestart { get; set; }
    public bool RequiresManualAction { get; set; } = true;
    public string Confidence { get; set; } = "";
    public string[] SourceUrls { get; set; } = [];
}

/// <summary>Pure advisory builder. No I/O, probing, downloads, edits, firmware actions or benchmarks.</summary>
public static class OptimizationGuide
{
    private const string High = "High for mechanism; local performance gain is unmeasured";
    private const string Conditional = "Conditional; support and benefit need a matched local comparison";
    private const string NvidiaLatency = "https://www.nvidia.com/en-us/geforce/guides/system-latency-optimization-guide/";
    private const string NvidiaSettings = "https://www.nvidia.com/content/Control-Panel-Help/vLatest/en-us/mergedProjects/3D%20Settings/Manage_3D_Settings_%28reference%29.htm";
    private const string PresentMon = "https://github.com/GameTechDev/PresentMon";
    private const string NicTuning = "https://learn.microsoft.com/en-us/windows-server/networking/technologies/network-subsystem/net-sub-performance-tuning-nics";
    private const string IntelHybrid = "https://www.intel.com/content/www/us/en/developer/articles/guide/12th-gen-intel-core-processor-gamedev-guide.html";

    public static List<OptimizationGuideItem> Build(PcAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var hardware = analysis.Hardware ?? new PcFacts();
        var gpus = hardware.Gpus ?? [];
        var displays = analysis.Displays ?? [];
        var adapters = hardware.Adapters ?? [];
        var costs = analysis.CandidateCosts ?? [];
        var volumes = analysis.Volumes ?? [];
        var result = new List<OptimizationGuideItem>();
        string cpu = Known(hardware.Cpu), windows = Known(hardware.Windows);
        string board = string.Join(" ", new[] { hardware.MotherboardManufacturer, hardware.MotherboardModel }.Where(IsKnown));
        if (board.Length == 0) board = "Exact motherboard unknown";
        string gpu = gpus.Count == 0 ? "GPU/driver unknown" : string.Join("; ", gpus.Select(g => Known(g.Name) + " / " + Known(g.Driver)));
        bool nvidia = gpus.Any(g => Contains(g.Name, "NVIDIA"));
        bool radeon = gpus.Any(g => Contains(g.Name, "Radeon"));
        bool intelDesktop = Contains(cpu, "Intel") && Regex.IsMatch(cpu, @"\bi[579]-(?:13|14)\d{3}(?:K|KF|F|KS|T)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        bool intel14900Kf = intelDesktop && Regex.IsMatch(cpu, @"\bi9-14900KF\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        bool x3d = (Contains(cpu, "AMD") || Contains(cpu, "Ryzen")) && Contains(cpu, "X3D");
        bool asymmetricX3d = x3d && Regex.IsMatch(cpu, @"\b(?:7900|7950|9900|9950)X3D\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        bool dualCacheX3d = x3d && Regex.IsMatch(cpu, @"\b9950X3D2\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        string display = displays.Count == 0 ? "Refresh unknown" : string.Join("; ", displays.Select(d =>
            (d.Primary ? "Primary " : "") + d.Width + "x" + d.Height + " @ " + (d.RefreshHz?.ToString(CultureInfo.InvariantCulture) ?? "unknown") + " Hz"));
        string memory = (hardware.RamGiB > 0 ? F(hardware.RamGiB) + " GiB" : "RAM capacity unknown")
            + "; reported speed " + (hardware.MemorySpeedMT > 0 ? hardware.MemorySpeedMT + " MT/s" : "unknown");
        string network = adapters.Count == 0 ? "Active adapter unknown" : string.Join("; ", adapters.Select(a => Known(a.Name) + " / " + Known(a.Link)));
        string activity = costs.Count == 0 ? "No candidate resource measurements" : string.Join("; ", costs.Take(3).Select(c =>
            c.Application + " " + F(c.CpuPercent) + "% CPU / " + F(c.PrivateMiB) + " MiB private"));
        void Add(string area, string title, string detected, string verdict, string recommendation, string why, string tradeoff,
            bool restart, string confidence, params string[] urls) => result.Add(new()
        {
            Area = area, Title = title, Detected = detected, Recommendation = verdict + ": " + recommendation,
            Why = why, Tradeoff = tradeoff, NeedsRestart = restart, RequiresManualAction = true,
            Confidence = confidence, SourceUrls = urls
        });

        Add("BIOS", "Use firmware for the exact board", board + "; BIOS " + Known(hardware.Bios) + "; release " + Known(hardware.BiosReleaseDate),
            "Manual", (IsKnown(hardware.MotherboardModel) ? "Check this exact board/model/revision's OEM release notes and supported update procedure."
                : "Confirm the exact PC/board model and revision first; no firmware image or update procedure is selected from this incomplete inventory.")
                + " Preserve settings and the BitLocker recovery key; update for a relevant fix using stable power.",
            "Firmware can fix stability; its date does not prove microcode, power or memory configuration.",
            "An incorrect/interrupted flash can prevent boot and reset settings. TMODE never flashes firmware.", true, High,
            FirmwareSources(hardware.MotherboardManufacturer));
        if (intelDesktop)
            Add("CPU stability", "Intel desktop defaults and microcode first", cpu + "; microcode and Intel Default Settings are unmeasured",
                "Supported", "For 13th/14th-generation desktop CPUs, use the relevant Intel Default Settings and latest exact-board/OEM BIOS containing microcode 0x12F or later. Persistent crashes need Intel/OEM support, not added voltage or disabled protection.",
                "Intel's current Vmin-shift guidance covers this family. " + (intel14900Kf
                    ? "The 14900KF's 125 W base/253 W maximum turbo specifications do not justify removing limits."
                    : "Use this exact SKU's supported limits; do not copy the 14900KF's power values onto another model."),
                "Correct limits may lower an unrestricted benchmark score. Do not assume firmware repairs already degraded hardware.", true, High,
                intel14900Kf
                    ? ["https://www.intel.com/content/www/us/en/support/articles/000102331/processors.html",
                        "https://www.intel.com/content/www/us/en/products/sku/236787/intel-core-i9-processor-14900kf-36m-cache-up-to-6-00-ghz/specifications.html"]
                    : ["https://www.intel.com/content/www/us/en/support/articles/000102331/processors.html"]);
        else if (x3d)
            Add("CPU scheduling", "Retain AMD X3D provisioning", cpu + (asymmetricX3d ? "; recognized conventional asymmetric dual-CCD model"
                    : dualCacheX3d ? "; recognized dual-cache model; live topology unmeasured" : "; CCD/cache topology unknown"),
                "Supported", "Use the supported AMD chipset package and retain PPM provisioning/3D V-Cache optimizer. Follow model-specific game-detection guidance; do not strip these dependencies or copy a CCD affinity mask.",
                asymmetricX3d ? "These models combine different CCD cache/frequency behavior."
                    : dualCacheX3d ? "AMD lists V-Cache on both CCDs for the 9950X3D2 Dual Edition; an asymmetric-cache placement policy must not be inferred."
                    : "X3D alone does not establish asymmetric CCDs: single-CCD and dual-cache models differ.",
                "Game Mode/Game Bar can be scheduling dependencies on relevant models. Do not apply a 7950X3D parking policy to every X3D CPU.", true, High,
                "https://www.amd.com/en/resources/support-articles/release-notes/RN-RYZEN-CHIPSET-8-09-28-1124.html",
                "https://www.amd.com/en/products/processors/technologies/3d-v-cache.html",
                dualCacheX3d
                    ? "https://www.amd.com/en/products/processors/desktops/ryzen/9000-series/amd-ryzen-9-9950x3d2-dual-edition.html"
                    : "https://www.amd.com/en/support");
        Add("Memory/BIOS", "Validate XMP or EXPO stability", memory + "; profile, channels and stability unknown",
            "Optional A/B", "After stock stability, check board QVL, kit and DIMM slots. Try one supported XMP/EXPO profile manually, test memory and actual game workloads, and retain it only without errors or worse frame-time tails.",
            "Memory speed/latency can matter when CPU-limited. Reported MT/s does not establish timings, channel layout or stability.",
            "XMP/EXPO is overclocking; marginal settings can cause corruption/crashes. Avoid copied voltages and universal tight timings.", true, Conditional,
            "https://www.intel.com/content/www/us/en/gaming/extreme-memory-profile-xmp.html",
            "https://www.amd.com/en/products/processors/technologies/expo.html");
        Add("PCIe/BIOS", "Verify Resizable BAR and Above 4G", gpu + "; ReBAR, Above 4G and boot mode unknown",
            "Manual", "Check GPU, driver and exact-board firmware support together. Follow OEM ReBAR/Smart Access Memory instructions, verify active status in the driver, then compare Fortnite.",
            "Above 4G alone does not prove active ReBAR, and supported configurations do not guarantee a per-game gain.",
            "Never disable CSM blindly on legacy/MBR Windows, flash arbitrary VBIOS or force unsupported game profiles.", true, Conditional,
            "https://nvidia.custhelp.com/app/answers/detail/a_id/5165",
            "https://www.intel.com/content/www/us/en/support/articles/000090831/software.html");
        Add("Security/BIOS", "Preserve Secure Boot, TPM and keys", windows + "; security/encryption states not measured",
            "Avoid blind defaults", "Keep Secure Boot/TPM and recovery access. Review current game requirements before firmware changes. Never clear TPM or disable boot protections for a generic FPS preset.",
            "These protect the boot chain and keys; firmware/security changes can affect protected games and BitLocker recovery.",
            "Removing protection is a real security/compatibility cost without an established endgame frame-time benefit.", true, High,
            "https://support.microsoft.com/en-us/windows/security/devicesecurity/enable-tpm-2-0-on-your-pc",
            "https://support.microsoft.com/en-us/windows/experience/compatibility/windows-11-system-requirements");
        Add("Cooling/boost", "Sustain clocks before adding power", cpu + "; temperatures, effective clocks and throttle reasons unmeasured",
            "Manual", "Observe sustained vendor/OEM telemetry in demanding play. Check cooler mounting, airflow, fan/pump and supported limits before PBO/overclock/undervolt experiments. Preserve thermal/current protection.",
            "Boost depends on temperature, power, current and workload. More requested power does not prove more useful sustained performance.",
            "Unlimited power adds heat/noise. No universal Curve Optimizer '-30' or voltage offset is stable on every CPU.", false, High,
            "https://www.amd.com/en/resources/support-articles/faqs/CPU-PB2.html",
            "https://www.intel.com/content/www/us/en/support/articles/000097061.html",
            "https://www.amd.com/content/dam/amd/en/documents/products/software-tools/faq-curve-optimizer.pdf");
        Add("CPU scheduling", "Keep hardware-aware scheduling", cpu + "; " + Positive(hardware.Cores) + " reported cores / " + Positive(hardware.Threads) + " threads; " + Known(hardware.TopologySource),
            "Avoid blind defaults", "Use supported Windows/chipset scheduling. Do not universally disable E-cores, SMT, C-states or parking, or pin Fortnite to copied masks. A diagnosed placement problem can justify a reversible experiment.",
            "Hybrid and cache/preferred-core topology need hardware-aware placement; core count does not identify the best affinity.",
            "Removing cores may reduce workers or move background work onto game cores. Forced active states add heat/power.", true, Conditional, IntelHybrid);
        Add("Power", "Compare supported AC performance modes", "Plan " + Known(analysis.ActivePowerPlan) + "; AC " + Bool(analysis.AcPower) + "; portable " + Bool(hardware.IsPortable),
            "Optional A/B", analysis.AcPower == false ? "Use a suitable OEM power supply before performance testing; preserve battery-safe settings."
                : analysis.AcPower == null ? "Confirm AC status and the OEM's supported plugged-in mode before testing performance plans; preserve the existing policy while power facts are unknown."
                : "Compare the Windows/OEM supported performance mode with the existing plan on AC; keep it only for repeatable sustained improvement and retain the original plan.",
            "A plan name or 100% minimum CPU state does not establish lower latency or useful boost.",
            "Aggressive plans consume cooling/battery budget. On laptops, CPU and GPU power headroom can compete.", false, Conditional,
            "https://support.microsoft.com/en-us/windows/experience/change-the-power-mode-for-your-windows-pc");
        if (nvidia)
            Add("NVIDIA latency", "Use native Reflex before driver queue tweaks", gpu + "; current Reflex/power settings unknown",
                "Supported", "If Fortnite exposes supported Reflex, start with On. Compare On + Boost separately with thermal/power headroom. Driver Ultra Low Latency is a fallback for games without Reflex, not an additional stacked gain.",
                "Reflex coordinates game CPU/GPU submission and overrides the driver low-latency functionality.",
                "Boost can hold higher GPU clocks and consume more power; CPU simulation limits remain.", false, High,
                NvidiaLatency, "https://developer.nvidia.com/performance-rendering-tools/reflex");
        if (radeon)
            Add("AMD latency", "Verify native Anti-Lag support", gpu + "; current game/driver support unknown",
                "Optional A/B", "Use a currently supported game/driver control only. Anti-Lag 2 requires developer integration; do not claim Fortnite support without verification or install third-party injectors.",
                "Native integration targets queueing in supported GPU-bound workloads, rather than speeding every pipeline stage.",
                "Effect and compatibility vary; unsupported hooks introduce anti-cheat/stability risk.", false, Conditional,
                "https://www.amd.com/en/products/software/adrenalin/radeon-software-anti-lag.html", "https://gpuopen.com/anti-lag-2/");
        Add("Frame pacing", "Choose a sustainable endgame cap", display + "; combat FPS/bottleneck unmeasured",
            "Optional A/B", "Compare uncapped with one in-game cap the demanding scene can sustain. Leave headroom when GPU-bound; avoid several competing limiters. Pick from measured slow frames and tearing/VRR preference.",
            "A cap can reduce queue pressure and pacing variation; an unlimited average can hide slow-frame tails.",
            "A cap sacrifices peak FPS and limiter quality matters. It cannot make the CPU complete an overloaded endgame within an impossible budget.", false, Conditional,
            "https://nvidia.custhelp.com/app/answers/detail/a_id/4958/", PresentMon);
        Add("Display latency", "Choose tearing or a verified VRR path", display + "; VRR range, sync and display path unknown",
            "Manual", nvidia ? "For tearing-tolerant low-latency play compare V-Sync off. For tear-free G-SYNC, use NVIDIA's supported G-SYNC + V-Sync + Reflex combination and verify its below-ceiling cap. Confirm actual monitor refresh."
                : "Verify the display's refresh/VRR range. Compare V-Sync off against the vendor-supported VRR/cap path; refresh Hz alone does not prove FreeSync/G-SYNC.",
            "Scan-out, presentation queues and frame processing are different parts of latency.",
            "Minimum-latency and tear-free choices differ. No fixed 'refresh minus 3' fits every limiter/display.", false, Conditional,
            NvidiaLatency, "https://nvidia.custhelp.com/app/answers/detail/a_id/4958/");
        Add("GPU scheduling", "A/B HAGS on this driver", gpu + "; HwSchMode preference " + Value(analysis.HagsPreference),
            "Optional A/B", "Use the Windows Graphics control only if offered. Compare states with the same driver, warmed scene and overlays, restarting Windows between states; a registry preference does not prove active support.",
            "HAGS changes scheduling architecture; Microsoft does not promise a universal FPS/latency gain.",
            "Capture/features/drivers can behave differently. Retain the better repeated result.", true, Conditional,
            "https://devblogs.microsoft.com/directx/hardware-accelerated-gpu-scheduling/");
        Add("Presentation", "Apply windowed optimizations to the right API", windows + "; renderer/window mode unknown",
            "Optional A/B", "Windows 11 Optimizations for windowed games targets compatible DX10/DX11 windowed/borderless apps. Compare presentation; do not disable fullscreen optimizations globally or claim that switch upgrades an existing DX12 path.",
            "Compatible legacy blt presentation can move to flip model, reducing presentation overhead.",
            "Restart the game after changes. API/app compatibility controls applicability.", false, High,
            "https://support.microsoft.com/en-au/windows/hardware/display-graphics/optimizations-for-windowed-games-in-windows-11",
            "https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-flip-model");
        Add("Fortnite rendering", "Compare current Performance mode with DX12", gpu + "; renderer and CPU/GPU frame cost unmeasured",
            "Optional A/B", "Compare the current Performance - Lower Graphical Fidelity option with a warmed preferred DX12 quality path. Reduce costly effects/meshes first; reduce 3D resolution when GPU cost is the constraint.",
            "Epic's current competitive guide describes Performance mode as DX12-based; old advice equating every Performance mode with DX11 is stale.",
            "Lower detail can affect clarity. Reducing GPU work does not eliminate CPU simulation load; restart/warm after renderer changes.", false, High,
            "https://store.epicgames.com/news/fortnite-on-pc-best-settings-for-competitive-play-in-2026",
            "https://dev.epicgames.com/documentation/fortnite/37-00-fortnite-ecosystem-updates-and-release-notes");
        Add("Shader stutter", "Retain healthy caches; repair diagnosed loops", gpu + "; compilation/cache health unknown",
            "Supported", "Keep shader caching and adequate disk space. Warm content after game/driver updates; do not purge before every match. If repeated DX12 recompilation is diagnosed, follow Epic's targeted repair, then warm before comparison.",
            "Caches avoid repeated compilation; repairing a broken cache differs from deleting healthy caches as a routine boost.",
            "Updates/repair can temporarily add shader stutter. Unlimited cache size is not universally better.", false, High,
            NvidiaSettings, "https://www.epicgames.com/help/c-34254770/c-38015632/a11302262");
        Add("Background load", "Target actual contention, not process count", activity + "; sample " + (analysis.SampleSeconds > 0 ? F(analysis.SampleSeconds) : "unknown") + " s, not a combat trace",
            "Supported", "Save work; pause/close apps actually competing for CPU, RAM, GPU encoding or disk. Retain anti-cheat, audio/input, networking, chipset provisioning and security dependencies.",
            "An idle process and busy replay/browser/update work have different costs. Desktop sampling does not diagnose combat stutter.",
            "Closing apps loses functionality and can lose unsaved work. Lower process count is not a frame-time measurement.", false, High,
            "https://support.microsoft.com/en-gb/windows/experience/performance-optimization/tips-to-improve-pc-performance-in-windows",
            "https://learn.microsoft.com/en-us/windows-hardware/test/wpt/windows-performance-analyzer");
        Add("Windows gaming", "Separate Game Mode from background recording", "Game Mode preference " + Value(analysis.GameMode) + "; capture preference " + Value(analysis.BackgroundCapture),
            "Supported", "Keep supported Game Mode; disable unwanted recording through its own settings. Review third-party replay overlays individually. Do not equate recording off with removal of every Game Bar component.",
            "Capture consumes resources, while Game Mode and model-specific game detection serve different purposes.",
            "Capture removal loses replay clips. Other apps can record independently of this registry preference.", false, High,
            "https://support.microsoft.com/en-us/accessibility/windows/understand-and-explore-windows-settings",
            "https://news.xbox.com/en-us/2016/10/14/tips-on-windows-10-and-game-dvr-optimization/");
        Add("Commit/memory", "Support peak commit with a pagefile", memory + "; automatic pagefile " + Bool(hardware.AutomaticPagefile)
            + "; commit " + Number(analysis.CommitUsedGiB) + "/" + Number(analysis.CommitLimitGiB) + " GiB",
            "Supported", "Keep a system-managed pagefile with disk headroom unless measured peak-commit/crash-dump needs justify a specific size. Inspect commitment during demanding play/editing, not RAM capacity alone.",
            "Pagefiles support commit capacity and dumps; disabling one can remove required headroom.",
            "Paging under pressure can stall. A larger file protects capacity; it is not faster RAM or a promised FPS gain.", true, High,
            "https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/introduction-to-the-page-file",
            "https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/how-to-determine-the-appropriate-page-file-size-for-64-bit-versions-of-windows");
        Add("Memory/cache", "Avoid scheduled standby-list cleaners", "Available RAM " + Number(analysis.AvailableRamGiB) + " GiB; memory load " + (analysis.MemoryLoadPercent?.ToString(CultureInfo.InvariantCulture) ?? "unknown") + "%",
            "Avoid blind defaults", "Diagnose memory pressure, leaks and hard faults first. Keep normal cache/compression management; do not add a resident timer to empty reusable standby memory.",
            "Windows counts standby plus free RAM as available. Discarding useful cached data can require later I/O; occupied cache is not itself a leak.",
            "More displayed free RAM is not an FPS result. A specific measured memory-manager issue deserves targeted diagnosis.", false, High,
            "https://learn.microsoft.com/en-us/windows-hardware/test/assessments/results-for-the-memory-footprint-assessment",
            "https://learn.microsoft.com/en-us/windows-server/administration/performance-tuning/subsystem/cache-memory-management/troubleshoot");
        string disk = volumes.Count == 0 ? "Drive capacity/health unknown" : string.Join("; ", volumes.Select(v => v.Drive + " " + F(v.FreeGiB) + " GiB / " + F(v.FreePercent) + "% free"));
        Add("Storage/assets", "Keep maintenance and asset I/O out of matches", disk + "; storage type/health and streaming state unknown",
            "Manual", "Leave room for game/pagefile/caches. Schedule supported drive optimization and downloads outside play. Check asset options only if present: Epic currently reports Pre-download Streamed Assets missing; do not force an obsolete hidden setting.",
            "Asset reads, downloads and cache growth can contend for I/O; Windows selects drive-appropriate maintenance.",
            "Predownloads consume space; maintenance creates I/O. Repeated SSD defragmentation is not a match-time tweak.", false, High,
            "https://support.microsoft.com/en-au/windows/experience/storage-filemanagement/defragment-optimize-your-data-drives-in-windows",
            "https://www.epicgames.com/help/c-34254770/c-38015632/why-can-i-not-find-the-pre-download-streamed-assets-option-for-fortnite-a10987768");
        Add("Network/router", "Diagnose bufferbloat before SQM", network + "; loaded RTT/loss and router capabilities unknown",
            "Manual", "Compare idle/loaded network latency over wired Ethernet. For WAN queue spikes consider the router vendor's supported SQM/CAKE/fq_codel with measured rates and enough router CPU capacity.",
            "Queue management targets shared-line bufferbloat, not CPU/GPU frame processing or every server route.",
            "Shaping can reduce throughput and affect everyone. A weak router can itself become CPU-bound; router changes are separate.", false, Conditional,
            "https://docs.gl-inet.com/router/en/4/interface_guide/sqm/", "https://openwrt.org/docs/guide-user/network/traffic-shaping/sqm");
        Add("Experimental/NIC", "Wired adapter power/batching profile", network + "; physical wired route and advertised EEE/moderation/RSS values require fresh validation",
            "Experimental, off by default", "Use only the supported physical wired adapter on the current route. Existing NIC experiments may disable exposed EEE/moderation and enable supported RSS; preserve exact originals and compare loaded RTT, loss, DPC/CPU cost and frame-time tails.",
            "Reduced batching/power saving can lower packet delay, while RSS distributes work. These mechanisms are not a guaranteed Fortnite gain.",
            "Disabling moderation increases interrupts and may hurt CPU-heavy endgames; changes can reconnect the link. No Wi-Fi, virtual/VPN or guessed driver writes.", false, Conditional,
            NicTuning, "https://learn.microsoft.com/en-us/windows-hardware/drivers/network/interrupt-moderation",
            "https://www.intel.com/content/www/us/en/support/articles/000005593/ethernet-products.html");
        Add("Experimental/QoS", "App-scoped DSCP marking", network + "; network policy and router handling of DSCP unknown",
            "Experimental, off by default", "Only the existing validated, app-scoped Fortnite policy may request DSCP 46. Require supported Ethernet/known game path, exact policy ownership and recovery; keep it only if loaded network measurements improve.",
            "DSCP requests a traffic class; routers/ISPs may ignore, rewrite or police it. It does not reserve Internet bandwidth or improve frame rendering.",
            "EF/46 can conflict with network voice policy and may provide no benefit. Never mark every process or claim lower ping from the setting alone.", false, Conditional,
            "https://learn.microsoft.com/en-us/windows-server/networking/technologies/qos/qos-policy-top",
            "https://www.rfc-editor.org/rfc/rfc3246");
        Add("Experimental/priority", "BelowNormal for measured background apps", activity + "; game-time scheduling contention unmeasured",
            "Experimental, off by default", "Use only known user apps observed consuming resources, with exact original priorities retained. Compare BelowNormal against normal scheduling. Do not alter security, input/audio, anti-cheat, chipset/system work or force Fortnite RealTime.",
            "Lower background priority can reduce competition; it cannot remove memory/I/O costs or create CPU capacity.",
            "Background work may become sluggish or accumulate. RealTime can starve mouse/keyboard/disk flushing and is unsuitable as a generic gaming tweak.", false, Conditional,
            "https://learn.microsoft.com/en-us/windows/win32/procthread/scheduling-priorities");
        Add("Timers/boot", "Avoid HPET and timer-resolution recipes", windows + "; timing defect/BCD overrides unknown",
            "Avoid blind defaults", "Do not force platform clock/ticks, TSC policies or dynamic-tick behavior, or install a resident timer-resolution tool by default. A diagnosed timing defect needs a specific vendor-supported repair and original boot configuration.",
            "Windows selects an appropriate counter; Microsoft's platform-clock/TSC options are debugging tools rather than universal latency recommendations.",
            "Boot edits can break startup and change measurement costs. Synthetic timer resolution is not click-to-photon latency.", true, High,
            "https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/bcdedit--set",
            "https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps");
        Add("Popular tweaks/CTT", "Do not consolidate service hosts for a score", memory + "; service-host contention not measured",
            "Avoid blind defaults", "Review WinUtil actions individually. Its service-host consolidation advice targets low-performance systems with 12 GB or less; do not import it wholesale onto a high-memory gaming PC.",
            "Microsoft's split service hosts improve isolation/reliability/accounting at a memory cost. Fewer svchost processes does not establish faster Fortnite.",
            "WinUtil warns of Xbox-service interference. Restore points do not make every servicing/component change perfectly reversible.", true, High,
            "https://winutil.christitus.com/code-reference/tweaks/z--advanced-tweaks---caution/svchostsplitting/",
            "https://learn.microsoft.com/en-us/windows/application-management/svchost-service-refactoring");
        Add("Popular tweaks/Atlas", "Review forced C0 and security tradeoffs", cpu + "; Atlas changes not inferred",
            "Avoid blind defaults", "Do not copy forced-C0/idle-state, Game Bar/Game Mode or mitigation-removal defaults. Keep security and CPU-vendor scheduling dependencies; assess a specific documented action separately.",
            "Atlas documents heat/power costs and possible newer-CPU/anti-cheat issues. Those defaults can conflict with sustained thermal headroom or X3D game detection.",
            "Disabling HVCI/VBS/mitigations is a security decision, not a silent frame-time fix; it can also regress performance or compatibility.", true, High,
            "https://docs.atlasos.net/post-install/atlas-folder/configuration/", "https://docs.atlasos.net/post-install/atlas-folder/security/",
            "https://learn.microsoft.com/en-us/windows/security/hardware-security/enable-virtualization-based-protection-of-code-integrity");
        Add("Popular tweaks/ReviOS", "Preserve sleep and servicing dependencies", windows + "; portable " + Bool(hardware.IsPortable) + "; Revision changes not inferred",
            "Avoid blind defaults", "Review removed components and sleep behavior before using any Revision preset. Keep working OEM sleep/hibernate, recovery, updates, required runtimes and security; close measured competing work instead.",
            "Revision documents laptop overheating associated with hibernation disable and lists broad removals. Component/disk-space reduction alone does not establish FPS benefit.",
            "Removed features can be hard to restore; changing sleep behavior can leave a laptop active/hot in a bag.", true, High,
            "https://revi.cc/docs/faq/troubleshoot/overheating", "https://revi.cc/docs/features");
        Add("Validation", "Judge repeated demanding frame times", "Snapshot " + analysis.CapturedUtc.ToString("u", CultureInfo.InvariantCulture) + "; no endgame or click-to-photon measurement",
            "Supported", "When separately authorized, change one variable and alternate matched warmed foreground runs with the same scene/game/driver/cap. Compare p95/p99, slow frames, consistently defined 1% lows, FPS, GPU busy, clocks and thermals.",
            "A stationary Creative/editor result cannot establish combat endgame behavior; frame timing, network RTT and input latency are distinct.",
            "Combat is difficult to reproduce. Disclose foreground/scene validity and retain only repeatable stable improvements.", false, High,
            PresentMon, "https://developer.nvidia.com/performance-rendering-tools/reflex");
        return result;
    }

    private static bool Contains(string? text, string value) => text?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
    private static string[] FirmwareSources(string? manufacturer)
    {
        if (Contains(manufacturer, "ASUS")) return ["https://www.asus.com/support/"];
        if (Contains(manufacturer, "Micro-Star") || string.Equals(manufacturer?.Trim(), "MSI", StringComparison.OrdinalIgnoreCase))
            return ["https://www.msi.com/support"];
        if (Contains(manufacturer, "Gigabyte")) return ["https://www.gigabyte.com/Support"];
        if (Contains(manufacturer, "ASRock")) return ["https://www.asrock.com/support/index.asp"];
        if (Contains(manufacturer, "Dell")) return ["https://www.dell.com/support/home"];
        if (Contains(manufacturer, "Hewlett-Packard") || string.Equals(manufacturer?.Trim(), "HP", StringComparison.OrdinalIgnoreCase))
            return ["https://support.hp.com/"];
        if (Contains(manufacturer, "Lenovo")) return ["https://pcsupport.lenovo.com/"];
        if (Contains(manufacturer, "Acer")) return ["https://www.acer.com/support"];
        // Unknown OEM: these are CPU-vendor support portals, not a selected board's flash guide.
        return ["https://www.intel.com/content/www/us/en/support/processors.html", "https://www.amd.com/en/support"];
    }
    private static bool IsKnown(string? value) => !string.IsNullOrWhiteSpace(value)
        && !new[] { "Unavailable", "Unknown", "To be filled by O.E.M.", "Default string", "System Product Name", "Not Applicable" }
            .Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);
    private static string Known(string? value) => IsKnown(value) ? value! : "unknown";
    private static string Bool(bool? value) => value.HasValue ? (value.Value ? "yes" : "no") : "unknown";
    private static string Value(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "absent/unknown";
    private static string Positive(int value) => value > 0 ? value.ToString(CultureInfo.InvariantCulture) : "unknown";
    private static string Number(double? value) => value.HasValue && double.IsFinite(value.Value) ? F(value.Value) : "unknown";
    private static string F(double value) => double.IsFinite(value) ? value.ToString("0.##", CultureInfo.InvariantCulture) : "unknown";
}
