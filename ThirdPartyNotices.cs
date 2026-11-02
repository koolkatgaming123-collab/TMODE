using System.Reflection;
using System.Text;

namespace TourneyMode;

public static class ThirdPartyNotices
{
    public static string ReadAll()
    {
        var assembly = typeof(ThirdPartyNotices).Assembly;
        var result = new StringBuilder("TMODE — Third-party licenses\r\nBundled .NET runtime and optional Intel PresentMon helper.\r\n\r\n");
        foreach (string name in assembly.GetManifestResourceNames().Where(name => name.Contains(".ThirdParty.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("A bundled license is unavailable.");
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            result.AppendLine(name[(name.IndexOf(".ThirdParty.", StringComparison.Ordinal) + 12)..]).AppendLine().AppendLine(reader.ReadToEnd()).AppendLine();
        }
        return result.ToString();
    }
}
