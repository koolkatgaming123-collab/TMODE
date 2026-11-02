using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TourneyMode;

public sealed record OOSToolInspection(bool Verified, string Message, string ExecutablePath = "",
    string Version = "", string Publisher = "", string Sha256 = "");
public sealed record OOSLaunchResult(bool Started, string Message, int? ProcessId = null);
public sealed record OOSConfigurationSummary(int SettingCount, int EnabledCount, int DisabledCount)
{
    // A cfg records toggle states, not recommendation levels or underlying original values.
    public bool EstablishesRecommendedOnly => false;
    public bool EstablishesExactRecovery => false;
}
internal sealed record OOSFileIdentity(bool Trusted, string Publisher, string Product, Version Version);

/// <summary>
/// Optional integration with a separately obtained, unmodified vendor tool.
/// Opens its normal interface only. No silent apply, import, undo, or undocumented switches.
/// </summary>
public sealed class OOSPrivacy
{
    public const string OfficialProductUrl = "https://www.oo-software.com/en/shutup10";
    public const string OfficialManualUrl = "https://manuals.oo-software.com/ooshutup10/en/docs/first-steps/";
    public const string SilentApplyLimitation = "A supported silent workflow for current Recommended-only selection, exact baseline capture, undo and readback has not been established. Open the vendor interface to review its current recommendations and undo history.";
    public static Version SupportedVersion => new(3, 6, 1135, 0);
    private const string Publisher = "O&O Software GmbH";
    private const long MaximumToolBytes = 200L * 1024 * 1024;
    private const int MaximumConfigBytes = 256 * 1024;
    private readonly Func<string, OOSFileIdentity> identity;
    private readonly Func<ProcessStartInfo, int?> launch;
    private readonly Func<string?> systemBlocker;
    private readonly Func<bool> platform;

    public OOSPrivacy() : this(InspectNativeIdentity, StartInterface, DefaultSystemBlocker, OperatingSystem.IsWindows) { }
    internal OOSPrivacy(Func<string, OOSFileIdentity> identity, Func<ProcessStartInfo, int?> launch,
        Func<string?> systemBlocker, Func<bool> platform)
    {
        this.identity = identity; this.launch = launch; this.systemBlocker = systemBlocker; this.platform = platform;
    }

    /// <summary>Checks a local tool without executing it or changing privacy settings.</summary>
    public OOSToolInspection Inspect(string executablePath)
    {
        try
        {
            if (!platform()) throw new PlatformNotSupportedException("O&O privacy integration requires Windows.");
            string path = ValidateLocalFile(executablePath, ".exe");
            using var file = OpenLocked(path, MaximumToolBytes);
            return InspectLocked(path, file);
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    /// <summary>
    /// Call only from an explicit external-tool button. The caller must reject an active engine,
    /// pending recovery and any unmet restore-point requirement in its safety gate.
    /// The supplied path is fully revalidated; an earlier inspection is not an execution grant.
    /// </summary>
    public OOSLaunchResult OpenInterface(string executablePath, Func<string?> safetyGate)
    {
        ArgumentNullException.ThrowIfNull(safetyGate);
        try
        {
            if (!platform()) return new(false, "O&O privacy integration requires Windows.");
            string? blocked = systemBlocker() ?? safetyGate();
            if (!string.IsNullOrWhiteSpace(blocked)) return new(false, blocked);
            string path = ValidateLocalFile(executablePath, ".exe");
            // Deny write/delete sharing until Process.Start has opened the verified image.
            using var file = OpenLocked(path, MaximumToolBytes);
            var inspection = InspectLocked(path, file);
            if (!inspection.Verified) return new(false, inspection.Message);
            blocked = systemBlocker() ?? safetyGate();
            if (!string.IsNullOrWhiteSpace(blocked)) return new(false, blocked);
            _ = ValidateLocalFile(path, ".exe");
            var info = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(path)!,
                CreateNoWindow = true
            };
            // No cfg argument or switches are ever supplied. Opening is not application success.
            int? id = launch(info);
            return id is > 0
                ? new(true, "Opened the O&O interface. TMODE has not applied privacy settings or verified a change. Use only Recommended settings; retain its undo history.", id)
                : new(false, "The O&O interface did not report a started process.");
        }
        catch (Exception ex) { return new(false, "O&O interface was not opened: " + ex.Message); }
    }

    /// <summary>Review-only cfg parsing. It never establishes current recommendations or exact rollback.</summary>
    public static OOSConfigurationSummary InspectSettingsFile(string configurationPath)
    {
        string path = ValidateLocalFile(configurationPath, ".cfg");
        using var file = OpenLocked(path, MaximumConfigBytes);
        using var reader = new StreamReader(file, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();
        return ParseSettingsText(text);
    }

    internal static OOSConfigurationSummary ParseSettingsText(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaximumConfigBytes || text.Contains('\0'))
            throw new InvalidDataException("The O&O settings file is too large or contains binary data.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int enabled = 0, disabled = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[] columns = line.Split(['\t', ';'], 3);
            if (columns.Length < 2 || !Regex.IsMatch(columns[0].Trim(), "^[A-Z][0-9]{3}$", RegexOptions.CultureInvariant)
                || columns[1].Trim() is not ("+" or "-") || !ids.Add(columns[0].Trim()) || ids.Count > 500)
                throw new InvalidDataException("The O&O cfg contains an invalid, duplicate or unsupported setting line.");
            if (columns[1].Trim() == "+") enabled++; else disabled++;
        }
        if (ids.Count == 0) throw new InvalidDataException("The O&O cfg contains no settings.");
        return new(ids.Count, enabled, disabled);
    }

    private OOSToolInspection InspectLocked(string path, FileStream file)
    {
        OOSFileIdentity found = identity(path);
        if (!found.Trusted || !string.Equals(found.Publisher, Publisher, StringComparison.Ordinal))
            return new(false, "The selected executable does not have a trusted O&O Software GmbH Authenticode signature.");
        if (found.Product is not ("O&O ShutUp10" or "O&O ShutUp10++" or "O&O ShutUp10 Free"))
            return new(false, "The signed executable does not identify the supported O&O ShutUp10 product.");
        if (found.Version != SupportedVersion)
            return new(false, "This O&O version has not been reviewed by TMODE. Expected " + SupportedVersion + "; found " + found.Version + ".");
        file.Position = 0;
        string hash = Convert.ToHexString(SHA256.HashData(file));
        return new(true, "Verified external O&O tool for interactive review only.", path, found.Version.ToString(), found.Publisher, hash);
    }

    private static FileStream OpenLocked(string path, long maximum)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 || file.Length > maximum)
        { file.Dispose(); throw new InvalidDataException("The selected external-tool file has an unsupported size."); }
        return file;
    }

    internal static string ValidateLocalFile(string path, string extension)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 30000 || !Path.IsPathFullyQualified(path)
            || path.StartsWith("\\", StringComparison.Ordinal) || path.Length < 3 || path[1] != ':'
            || !char.IsAsciiLetter(path[0]) || path[2] is not ('\\' or '/') || path[2..].Contains(':'))
            throw new InvalidDataException("Select a fully qualified local file path.");
        foreach (string component in path[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            if (component.EndsWith('.') || component.EndsWith(' ') || component is "." or "..")
                throw new InvalidDataException("Ambiguous external-tool paths are not supported.");
        string full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(full), extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected file must have the " + extension + " extension.");
        for (string? item = full; item != null; item = Path.GetDirectoryName(item))
        {
            FileAttributes attributes = File.GetAttributes(item);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("External-tool files and their parent directories must not be reparse points.");
            if (string.Equals(item, Path.GetPathRoot(item), StringComparison.OrdinalIgnoreCase)) break;
        }
        return full;
    }

    private static string? DefaultSystemBlocker()
    {
        string journal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TourneyMode", "session.json");
        try
        {
            try { _ = File.GetAttributes(journal); return "Restore the pending TMODE session before opening a privacy tool."; }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            var processes = Process.GetProcesses();
            try
            {
                foreach (var process in processes)
                    if (process.ProcessName.StartsWith("FortniteClient", StringComparison.OrdinalIgnoreCase))
                        return "Close Fortnite before opening a privacy configuration tool.";
            }
            finally { foreach (var process in processes) process.Dispose(); }
            return null;
        }
        catch { return "The absence of Fortnite and pending recovery could not be verified."; }
    }

    private static int? StartInterface(ProcessStartInfo info)
    {
        using var process = Process.Start(info);
        return process?.Id;
    }

    private static OOSFileIdentity InspectNativeIdentity(string path)
    {
        string? publisher = AuthenticodeTrust.VerifiedPublisher(path);
        if (publisher == null) return new(false, "", "", new Version(0, 0));
        var version = FileVersionInfo.GetVersionInfo(path);
        return new(true, publisher, version.ProductName ?? "",
            new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart, version.FilePrivatePart));
    }

    private static class AuthenticodeTrust
    {
        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        public static string? VerifiedPublisher(string path)
        {
            IntPtr filePointer = IntPtr.Zero;
            var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
            var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, RevocationChecks = 1,
                UnionChoice = 1, StateAction = 1, ProviderFlags = 0x80 };
            try
            {
                filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
                Marshal.StructureToPtr(file, filePointer, false); data.File = filePointer;
                Guid action = GenericVerifyV2;
                if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0 || data.StateData == IntPtr.Zero) return null;
                // Read the signer that Windows actually verified, rather than independently
                // selecting a certificate from a potentially multi-signature file.
                IntPtr provider = WTHelperProvDataFromStateData(data.StateData);
                if (provider == IntPtr.Zero) return null;
                IntPtr signerPointer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
                if (signerPointer == IntPtr.Zero) return null;
                var signer = Marshal.PtrToStructure<ProviderSignerPrefix>(signerPointer);
                if (signer.CertificateCount == 0 || signer.Certificates == IntPtr.Zero) return null;
                var certificate = Marshal.PtrToStructure<ProviderCertificatePrefix>(signer.Certificates);
                if (certificate.Certificate == IntPtr.Zero) return null;
                uint count = CertGetNameString(certificate.Certificate, 4, 0, IntPtr.Zero, null, 0);
                if (count is <= 1 or > 2048) return null;
                var name = new StringBuilder((int)count);
                if (CertGetNameString(certificate.Certificate, 4, 0, IntPtr.Zero, name, count) != count) return null;
                return name.ToString();
            }
            finally
            {
                if (data.StateData != IntPtr.Zero)
                { data.StateAction = 2; Guid action = GenericVerifyV2; _ = WinVerifyTrust(new IntPtr(-1), ref action, ref data); }
                if (filePointer != IntPtr.Zero)
                { Marshal.DestroyStructure<TrustFile>(filePointer); Marshal.FreeHGlobal(filePointer); }
            }
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr FileHandle; public IntPtr KnownSubject; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TrustData
        {
            public uint Size; public IntPtr PolicyCallback; public IntPtr SipClient; public uint UiChoice; public uint RevocationChecks;
            public uint UnionChoice; public IntPtr File; public uint StateAction; public IntPtr StateData; public IntPtr UrlReference;
            public uint ProviderFlags; public uint UiContext; public IntPtr SignatureSettings;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProviderSignerPrefix
        {
            public uint Size; public uint VerifyTimeLow; public uint VerifyTimeHigh;
            public uint CertificateCount; public IntPtr Certificates;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProviderCertificatePrefix { public uint Size; public IntPtr Certificate; }
        [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);
        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signerIndex,
            [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);
        [DllImport("crypt32.dll", EntryPoint = "CertGetNameStringW", CharSet = CharSet.Unicode)]
        private static extern uint CertGetNameString(IntPtr certificate, uint type, uint flags,
            IntPtr typeParameter, StringBuilder? name, uint characters);
    }
}
