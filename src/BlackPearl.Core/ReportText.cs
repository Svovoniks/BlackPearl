using System.Text.RegularExpressions;

namespace BlackPearl.Core;

/// <summary>
/// Error text before it leaves the computer in a crash report. Exception messages can quote files and folders (which name
/// people's videos and Windows accounts), web and network addresses (servers, rooms, keys in queries) and long ids
/// (invitations, keys); each is replaced by what it was.
/// </summary>
public static partial class ReportText
{
    public static string? Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = WebAddress().Replace(text, "<url>");
        text = UncPath().Replace(text, "<path>");
        text = DrivePath().Replace(text, "<path>");
        text = UnixPath().Replace(text, "<path>");
        text = NetworkAddress().Replace(text, "<address>");
        return LongId().Replace(text, "<id>");
    }
    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]*://[^\s""'<>]+", RegexOptions.IgnoreCase)] private static partial Regex WebAddress();
    [GeneratedRegex(@"\\\\[^\\\s""'<>|]+\\[^\r\n""'<>|*?]*")] private static partial Regex UncPath();
    [GeneratedRegex(@"\b[a-z]:[\\/][^\r\n""'<>|*?]*", RegexOptions.IgnoreCase)] private static partial Regex DrivePath();
    [GeneratedRegex(@"(?<![\w.])/(?:home|Users|mnt|media|storage|sdcard|data|tmp|var|opt|srv|root)/[^\r\n""'<>|*?]*")] private static partial Regex UnixPath();
    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?\b|\b(?:[0-9a-f]{1,4}:){4,7}[0-9a-f]{1,4}\b|\b[0-9a-f]{1,4}(?::[0-9a-f]{1,4})*::(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4})*)?", RegexOptions.IgnoreCase)] private static partial Regex NetworkAddress();
    [GeneratedRegex(@"\b[0-9a-f]{20,}\b|\b[A-Za-z0-9_-]{32,}\b", RegexOptions.IgnoreCase)] private static partial Regex LongId();
}
