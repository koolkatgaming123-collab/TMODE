using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TourneyMode;

public sealed class FortniteSettingEdit
{
    public string Section { get; set; } = "";
    public string Key { get; set; } = "";
    public int Occurrence { get; set; }
    public int OriginalOccurrenceCount { get; set; }
    public string OriginalValue { get; set; } = "";
    public string AppliedValue { get; set; } = "";
    public string ValueKind { get; set; } = "";
}

/// <summary>Only the session journal needs bytes. Analysis reports should expose hashes/changes/warnings.</summary>
public sealed class FortniteSettingsBackup
{
    public string Path { get; set; } = "";
    public string OriginalSHA256 { get; set; } = "";
    public string AppliedSHA256 { get; set; } = "";
    public byte[] OriginalBytes { get; set; } = [];
    public byte[] AppliedBytes { get; set; } = [];
    public List<string> Changes { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<FortniteSettingEdit> Edits { get; set; } = [];
    public bool Exists { get; set; }
    public bool ApplyAttempted { get; set; }
    public bool Restored { get; set; }
    public string RestoreExpectedSHA256 { get; set; } = "";
    public byte[] RestoreExpectedBytes { get; set; } = [];
    public bool HasChanges => Exists && Edits.Count > 0 && OriginalSHA256 != AppliedSHA256;
}

/// <summary>Bounded, reversible edits to existing recognized Fortnite settings, never arbitrary console variables.</summary>
public static class FortniteSettings
{
    public const int MaxFileBytes = 2 * 1024 * 1024;
    private const string GameSection = "/Script/FortniteGame.FortGameUserSettings";
    private const string ScalabilitySection = "ScalabilityGroups";
    private const string RendererSection = "D3DRHIPreference";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    public static string DefaultPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FortniteGame", "Saved", "Config", "WindowsClient", "GameUserSettings.ini");

    public static FortniteSettingsBackup Preview(string path, FpsLatencyOptions options, bool nvidiaGpu, bool acceptTearing)
    {
        ArgumentNullException.ThrowIfNull(options);
        var backup = new FortniteSettingsBackup { Path = LocalIniPath(path) };
        if (!File.Exists(backup.Path)) { backup.Warnings.Add("Fortnite settings file is missing. Launch and configure the game normally first; no file or key will be invented."); return backup; }
        backup.Exists = true;
        try
        {
            backup.OriginalBytes = ReadBounded(backup.Path);
            backup.OriginalSHA256 = Hash(backup.OriginalBytes);
            backup.AppliedBytes = backup.OriginalBytes.ToArray();
            backup.AppliedSHA256 = backup.OriginalSHA256;
            if (!options.ApplyGameProfile) return backup;
            if ((File.GetAttributes(backup.Path) & FileAttributes.ReadOnly) != 0)
            { backup.Warnings.Add("The settings file is read-only. Its attributes were preserved; remove read-only through normal file controls before reviewing another profile."); return backup; }
            var document = IniDocument.Parse(backup.OriginalBytes);

            void Change(string section, string key, string value, string kind)
            {
                var entries = document.Find(section, key);
                if (document.HasOperator(section, key))
                { backup.Warnings.Add($"[{section}] {key}: array/operator syntax is not a supported scalar setting; preserved."); return; }
                if (entries.Count == 0) { backup.Warnings.Add($"[{section}] {key}: missing key; preserved rather than added."); return; }
                if (entries.Count > 256) { backup.Warnings.Add($"[{section}] {key}: excessive duplicate scalar entries; preserved."); return; }
                if (entries.Any(entry => !KnownValue(entry.Value, kind)))
                { backup.Warnings.Add($"[{section}] {key}: an existing value has an unknown format; all occurrences preserved."); return; }
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (EqualValue(entry.Value, value, kind)) continue;
                    if (backup.Edits.Count >= 256) throw new InvalidDataException("The proposed profile exceeds 256 bounded scalar edits; no settings were changed.");
                    backup.Edits.Add(new() { Section = section, Key = key, Occurrence = i, OriginalOccurrenceCount = entries.Count,
                        OriginalValue = entry.Value, AppliedValue = value, ValueKind = kind });
                    backup.Changes.Add($"[{section}] {key}" + (entries.Count > 1 ? $" (occurrence {i + 1}/{entries.Count})" : "") + $": {entry.Value} -> {value}");
                    document.Replace(entry, value);
                }
            }

            if (options.CompetitiveVisuals)
            {
                foreach (string key in new[] { "sg.ViewDistanceQuality", "sg.AntiAliasingQuality", "sg.ShadowQuality", "sg.GlobalIlluminationQuality",
                    "sg.ReflectionQuality", "sg.PostProcessQuality", "sg.TextureQuality", "sg.EffectsQuality", "sg.FoliageQuality", "sg.ShadingQuality", "sg.LandscapeQuality" })
                    Change(ScalabilitySection, key, "0", "quality");
                Change(GameSection, "bMotionBlur", "False", "bool");
                Change(GameSection, "bUseNanite", "False", "bool");
                if (nvidiaGpu) Change(GameSection, "bEnableDLSSFrameGeneration", "False", "bool");
            }
            if (options.FrameCap == FortniteFrameCap.Uncapped) Change(GameSection, "FrameRateLimit", "0.000000", "number");
            else if (options.FrameCap == FortniteFrameCap.Target)
            {
                if (options.TargetFps is < 30 or > 1000) backup.Warnings.Add("Target FPS must be 30-1000, or choose Uncapped. FrameRateLimit was preserved.");
                else Change(GameSection, "FrameRateLimit", options.TargetFps.ToString("0.000000", Invariant), "number");
            }
            if (options.ResolutionPercent != 0)
            {
                if (options.ResolutionPercent is < 25 or > 100) backup.Warnings.Add("Resolution quality must be 25-100%, or 0 to preserve it. Resolution settings were preserved.");
                else
                {
                    Change(ScalabilitySection, "sg.ResolutionQuality", options.ResolutionPercent.ToString(Invariant), "percent");
                    Change(GameSection, "bUseDynamicResolution", "False", "bool");
                    backup.Warnings.Add("Resolution quality changes image sharpness. Verify the game's displayed 3D resolution after launch; upscaler/game-version behavior can normalize this setting.");
                }
            }
            if (options.DisableVSync)
            {
                if (acceptTearing) Change(GameSection, "bUseVSync", "False", "bool");
                else backup.Warnings.Add("VSync was preserved because tearing was not accepted. Review actual VRR/sync behavior in game.");
            }
            if (options.Reflex != FortniteReflex.KeepCurrent)
                backup.Warnings.Add(nvidiaGpu
                    ? "Manual action required: set NVIDIA Reflex in Fortnite's supported graphics menu. Existing latency-tweak keys do not establish an authoritative On/On+Boost mapping, so no Reflex key was changed."
                    : "NVIDIA Reflex was not configured: a supported NVIDIA GPU was not established. Existing latency settings were preserved.");
            if (options.Renderer != FortniteRenderer.KeepCurrent)
            {
                // This pair is observed in the installed game's existing performance profile.
                // Other UI renderer names do not establish a stable serialized Fortnite mapping.
                if (options.Renderer == FortniteRenderer.PerformanceDx11
                    && RendererPairKnown(document, "dx11", "es31"))
                {
                    Change(RendererSection, "PreferredRHI", "dx11", "rhi");
                    Change(RendererSection, "PreferredFeatureLevel", "es31", "feature");
                }
                else backup.Warnings.Add("Manual action required: select the requested rendering mode in Fortnite and restart the game. Its serialized renderer/feature-level mapping was not verified for this existing file; no renderer key was changed.");
            }
            if (backup.Edits.Count > 0)
            {
                backup.AppliedBytes = document.Encode();
                backup.AppliedSHA256 = Hash(backup.AppliedBytes);
                backup.Warnings.Add("Settings are requested through existing INI keys. The game may normalize them; in-game acceptance and performance benefit remain unmeasured.");
            }
            return backup;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException or InvalidDataException)
        {
            backup.Edits.Clear(); backup.Changes.Clear();
            backup.AppliedBytes = backup.OriginalBytes.ToArray(); backup.AppliedSHA256 = backup.OriginalSHA256;
            backup.Warnings.Add("Settings profile unavailable; file preserved: " + error.Message);
            return backup;
        }
    }

    public static void Apply(FortniteSettingsBackup backup, Action persist, Func<bool> gameAbsent)
    {
        ArgumentNullException.ThrowIfNull(persist); ArgumentNullException.ThrowIfNull(gameAbsent);
        ValidateBackup(backup);
        if (!backup.HasChanges) return;
        if (backup.ApplyAttempted || backup.Restored) throw new InvalidOperationException("This game-settings attempt is already journaled. Restore it before applying another profile.");
        RequireGameAbsent(gameAbsent);
        RequireHash(backup.Path, backup.OriginalSHA256, "Fortnite settings changed after review. Review a fresh plan before applying.");
        backup.ApplyAttempted = true; persist();
        AtomicWrite(backup.Path, backup.OriginalSHA256, backup.AppliedBytes, gameAbsent);
        RequireHash(backup.Path, backup.AppliedSHA256, "Applied settings readback did not match; recovery remains journaled.");
        persist();
    }

    public static void Restore(FortniteSettingsBackup backup, Action persist, Func<bool> gameAbsent)
    {
        ArgumentNullException.ThrowIfNull(persist); ArgumentNullException.ThrowIfNull(gameAbsent);
        ValidateBackup(backup);
        if (!backup.ApplyAttempted || backup.Restored) return;
        RequireGameAbsent(gameAbsent);
        byte[] current = ReadBounded(backup.Path); string currentHash = Hash(current);
        if (currentHash == backup.OriginalSHA256 || ValidRestoreCandidate(backup) && currentHash == backup.RestoreExpectedSHA256)
        { backup.Restored = true; persist(); return; }
        byte[] candidate;
        if (currentHash == backup.AppliedSHA256) candidate = backup.OriginalBytes.ToArray();
        else
        {
            var document = IniDocument.Parse(current);
            foreach (var edit in backup.Edits)
            {
                var matches = document.Find(edit.Section, edit.Key);
                if (document.HasOperator(edit.Section, edit.Key) || matches.Count != edit.OriginalOccurrenceCount
                    || edit.Occurrence >= matches.Count || !EqualValue(matches[edit.Occurrence].Value, edit.AppliedValue, edit.ValueKind))
                    throw new InvalidOperationException($"Fortnite settings recovery conflict at [{edit.Section}] {edit.Key}. User/game changes were preserved; keep the journal for review.");
                document.Replace(matches[edit.Occurrence], edit.OriginalValue);
            }
            candidate = document.Encode();
        }
        backup.RestoreExpectedBytes = candidate;
        backup.RestoreExpectedSHA256 = Hash(candidate);
        persist(); // An interrupted restore can recognize its exact candidate on retry.
        AtomicWrite(backup.Path, currentHash, candidate, gameAbsent);
        RequireHash(backup.Path, backup.RestoreExpectedSHA256, "Restored settings readback did not match; recovery remains journaled.");
        backup.Restored = true; persist();
    }

    private static bool RendererPairKnown(IniDocument document, string rhi, string feature)
    {
        var rhis = document.Find(RendererSection, "PreferredRHI"); var features = document.Find(RendererSection, "PreferredFeatureLevel");
        return rhis.Count > 0 && features.Count > 0 && rhis.All(entry => entry.Value.Equals(rhi, StringComparison.OrdinalIgnoreCase))
            && features.All(entry => entry.Value.Equals(feature, StringComparison.OrdinalIgnoreCase));
    }
    private static void ValidateBackup(FortniteSettingsBackup backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        if (LocalIniPath(backup.Path) != backup.Path) throw new InvalidDataException("The settings journal path is not canonical.");
        if (!backup.HasChanges && !backup.ApplyAttempted) return;
        if (!backup.Exists || backup.OriginalBytes.Length is 0 or > MaxFileBytes || backup.AppliedBytes.Length is 0 or > MaxFileBytes
            || Hash(backup.OriginalBytes) != backup.OriginalSHA256 || Hash(backup.AppliedBytes) != backup.AppliedSHA256
            || backup.Edits.Count is 0 or > 256)
            throw new InvalidDataException("The game-settings journal is incomplete or its byte/hash evidence changed.");
        var reconstructed = IniDocument.Parse(backup.OriginalBytes);
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in backup.Edits)
        {
            if (edit.Occurrence < 0 || edit.OriginalOccurrenceCount <= edit.Occurrence || edit.OriginalOccurrenceCount > 256
                || edit.ValueKind != ExpectedKind(edit.Section, edit.Key) || !KnownValue(edit.OriginalValue, edit.ValueKind) || !KnownValue(edit.AppliedValue, edit.ValueKind)
                || !identities.Add(edit.Section + "\n" + edit.Key + "\n" + edit.Occurrence))
                throw new InvalidDataException("The game-settings journal contains an unrecognized scalar edit.");
            var entries = reconstructed.Find(edit.Section, edit.Key);
            if (reconstructed.HasOperator(edit.Section, edit.Key) || entries.Count != edit.OriginalOccurrenceCount
                || entries[edit.Occurrence].Value != edit.OriginalValue)
                throw new InvalidDataException("The game-settings scalar evidence does not match the original bytes.");
            reconstructed.Replace(entries[edit.Occurrence], edit.AppliedValue);
        }
        if (Hash(reconstructed.Encode()) != backup.AppliedSHA256)
            throw new InvalidDataException("The proposed settings include changes outside the recognized scalar edit journal.");
        if (backup.RestoreExpectedSHA256.Length > 0 && !ValidRestoreCandidate(backup)) throw new InvalidDataException("The recorded restore candidate has invalid byte/hash evidence.");
    }
    private static bool ValidRestoreCandidate(FortniteSettingsBackup backup) => backup.RestoreExpectedBytes.Length is > 0 and <= MaxFileBytes
        && Hash(backup.RestoreExpectedBytes) == backup.RestoreExpectedSHA256;
    private static string ExpectedKind(string section, string key) => section.Equals(ScalabilitySection, StringComparison.OrdinalIgnoreCase)
        ? key.Equals("sg.ResolutionQuality", StringComparison.OrdinalIgnoreCase) ? "percent"
            : new[] { "sg.ViewDistanceQuality", "sg.AntiAliasingQuality", "sg.ShadowQuality", "sg.GlobalIlluminationQuality", "sg.ReflectionQuality", "sg.PostProcessQuality", "sg.TextureQuality", "sg.EffectsQuality", "sg.FoliageQuality", "sg.ShadingQuality", "sg.LandscapeQuality" }.Contains(key, StringComparer.OrdinalIgnoreCase) ? "quality" : ""
        : section.Equals(GameSection, StringComparison.OrdinalIgnoreCase)
            ? key.Equals("FrameRateLimit", StringComparison.OrdinalIgnoreCase) ? "number"
                : new[] { "bMotionBlur", "bUseNanite", "bEnableDLSSFrameGeneration", "bUseVSync", "bUseDynamicResolution" }.Contains(key, StringComparer.OrdinalIgnoreCase) ? "bool" : ""
            : section.Equals(RendererSection, StringComparison.OrdinalIgnoreCase)
                ? key.Equals("PreferredRHI", StringComparison.OrdinalIgnoreCase) ? "rhi" : key.Equals("PreferredFeatureLevel", StringComparison.OrdinalIgnoreCase) ? "feature" : "" : "";
    private static bool KnownValue(string value, string kind)
    {
        if (kind == "bool") return value.Equals("True", StringComparison.OrdinalIgnoreCase) || value.Equals("False", StringComparison.OrdinalIgnoreCase);
        if (kind == "rhi") return value is "dx11" or "dx12" || value.Equals("dx11", StringComparison.OrdinalIgnoreCase) || value.Equals("dx12", StringComparison.OrdinalIgnoreCase);
        if (kind == "feature") return new[] { "es31", "sm5", "sm6" }.Contains(value, StringComparer.OrdinalIgnoreCase);
        if (!double.TryParse(value, NumberStyles.Float, Invariant, out double number) || !double.IsFinite(number)) return false;
        return kind switch { "quality" => number is >= 0 and <= 4 && number == Math.Truncate(number), "percent" => number is >= 0 and <= 100,
            "number" => number is >= 0 and <= 10000, _ => false };
    }
    private static bool EqualValue(string left, string right, string kind) => KnownValue(left, kind) && KnownValue(right, kind)
        && (kind is "quality" or "number" or "percent" ? double.Parse(left, Invariant) == double.Parse(right, Invariant) : left.Equals(right, StringComparison.OrdinalIgnoreCase));
    private static string LocalIniPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)
            || !System.IO.Path.GetExtension(path).Equals(".ini", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose a fully qualified local INI file.", nameof(path));
        return System.IO.Path.GetFullPath(path);
    }
    private static byte[] ReadBounded(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("A settings-file reparse point is not supported; its target was not modified.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is 0 or > MaxFileBytes) throw new InvalidDataException("Settings files must contain 1 byte to 2 MiB.");
        using var output = new MemoryStream((int)stream.Length);
        byte[] buffer = new byte[8192]; int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        { if (output.Length + count > MaxFileBytes) throw new InvalidDataException("Settings file grew beyond 2 MiB."); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void RequireHash(string path, string expected, string detail)
    { if (Hash(ReadBounded(path)) != expected) throw new InvalidOperationException(detail); }
    private static void RequireGameAbsent(Func<bool> gameAbsent)
    {
        bool absent;
        try { absent = gameAbsent(); }
        catch (Exception error) { throw new InvalidOperationException("Fortnite absence could not be established. No settings write is permitted.", error); }
        if (!absent) throw new InvalidOperationException("Fortnite is running or its absence is unknown. Settings changes and recovery wait for the client to close.");
    }
    private static void AtomicWrite(string path, string expectedCurrentHash, byte[] bytes, Func<bool> gameAbsent)
    {
        if (bytes.Length is 0 or > MaxFileBytes) throw new InvalidDataException("Proposed settings exceed the bounded file size.");
        string temporary = path + ".tmode-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            RequireGameAbsent(gameAbsent);
            RequireHash(path, expectedCurrentHash, "Settings changed before the atomic write; user/game changes were preserved.");
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) throw new InvalidOperationException("Settings became read-only. File attributes were preserved.");
            RequireGameAbsent(gameAbsent);
            RequireHash(path, expectedCurrentHash, "Settings changed during the final game-state check; user/game changes were preserved.");
            File.Replace(temporary, path, null); // Same-directory atomic replace; Windows preserves destination security metadata.
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class IniDocument
    {
        private sealed record Line(string Text, string Ending);
        internal sealed record Entry(int LineIndex, string Section, string Key, string Value, string Prefix, string Suffix);
        private readonly List<Line> lines = [];
        private readonly List<Entry> entries = [];
        private readonly HashSet<string> operators = new(StringComparer.OrdinalIgnoreCase);
        private readonly Encoding encoding;
        private readonly byte[] preamble;
        private IniDocument(Encoding encoding, byte[] preamble) { this.encoding = encoding; this.preamble = preamble; }
        public static IniDocument Parse(byte[] bytes)
        {
            Encoding encoding; int bom;
            if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { encoding = new UTF32Encoding(false, false, true); bom = 4; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { encoding = new UTF32Encoding(true, false, true); bom = 4; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) { encoding = new UTF8Encoding(false, true); bom = 3; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, false, true); bom = 2; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, false, true); bom = 2; }
            else { encoding = new UTF8Encoding(false, true); bom = 0; }
            string text = encoding.GetString(bytes, bom, bytes.Length - bom);
            if (text.IndexOf('\0') >= 0) throw new InvalidDataException("Unknown or binary INI encoding; no setting was changed.");
            var result = new IniDocument(encoding, bytes[..bom]);
            string section = ""; int offset = 0;
            while (offset < text.Length)
            {
                int end = offset; while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
                int next = end; if (next < text.Length) { next++; if (text[end] == '\r' && next < text.Length && text[next] == '\n') next++; }
                string line = text[offset..end], ending = text[end..next];
                result.lines.Add(new(line, ending)); result.ParseLine(line, result.lines.Count - 1, ref section); offset = next;
            }
            return result;
        }
        private void ParseLine(string line, int index, ref string section)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] is ';' or '#') return;
            if (trimmed[0] == '[')
            {
                int close = trimmed.IndexOf(']');
                section = close > 1 && (trimmed[(close + 1)..].Trim() is "" || trimmed[(close + 1)..].TrimStart().StartsWith(';') || trimmed[(close + 1)..].TrimStart().StartsWith('#'))
                    ? trimmed[1..close].Trim() : "";
                return;
            }
            int equal = line.IndexOf('='); if (equal < 0) return;
            string key = line[..equal].Trim();
            if (key.Length == 0) return;
            if (key[0] is '+' or '-' or '.' or '!') { operators.Add(section + "\n" + key[1..].Trim()); return; }
            if (!key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.')) return;
            int start = equal + 1; while (start < line.Length && char.IsWhiteSpace(line[start])) start++;
            int end = line.Length;
            for (int i = start; i < line.Length; i++) if (line[i] is ';' or '#') { end = i; break; }
            while (end > start && char.IsWhiteSpace(line[end - 1])) end--;
            entries.Add(new(index, section, key, line[start..end], line[..start], line[end..]));
        }
        public List<Entry> Find(string section, string key) => entries.Where(entry => entry.Section.Equals(section, StringComparison.OrdinalIgnoreCase) && entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
        public bool HasOperator(string section, string key) => operators.Contains(section + "\n" + key);
        public void Replace(Entry entry, string value) { var line = lines[entry.LineIndex]; lines[entry.LineIndex] = new(entry.Prefix + value + entry.Suffix, line.Ending); }
        public byte[] Encode()
        {
            string text = string.Concat(lines.Select(line => line.Text + line.Ending));
            byte[] body = encoding.GetBytes(text);
            if (preamble.Length + body.Length > MaxFileBytes) throw new InvalidDataException("Edited settings would exceed 2 MiB.");
            return preamble.Concat(body).ToArray();
        }
    }
}
