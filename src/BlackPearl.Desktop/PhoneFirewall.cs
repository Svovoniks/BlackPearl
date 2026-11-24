using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

// Whether Windows Firewall lets phones on the home network reach BlackPearl's listener (PHONE-REMOTE.md, "On the home
// network"), read without administrator rights and without changing anything: the firewall's own settings and rules
// (HNetCfg.FwPolicy2), the network's Private or Public category (MSFT_NetConnectionProfile), and other firewalls that
// Windows Security knows of (SecurityCenter2). The installer is per-user and can't add a rule; Windows asks the user once,
// when BlackPearl first listens. Settings › Profile › Phone remote says what was decided and how to change it.
internal static class PhoneFirewall
{
    // The firewall's profiles as Windows numbers them: a network's category picks one.
    public const int Domain = 1, Private = 2, Public = 4;

    public sealed record Profile(int Type, bool Enabled, bool BlockAll, bool DefaultAllow, bool NotificationsDisabled);
    // An inbound rule that may concern BlackPearl: for its program, or for its port with no program named.
    public sealed record Rule(string? Application, bool Enabled, int Profiles, bool Allow, int Protocol, string? LocalPorts, int Direction = 1);

    // Allowed: a rule lets BlackPearl in. Off: the firewall lets everything in on this network. Blocked: a rule or setting
    // keeps it out. Undecided: no rule either way, so Windows keeps it out until the user answers its prompt (or allows
    // it in Windows Security). Unknown: the firewall couldn't be read.
    public enum Verdict { Unknown, Allowed, Off, Blocked, Undecided }

    // What Settings shows. Network is the name Windows gives the home network, when known.
    public sealed record Status(Verdict Verdict, int ProfileType, string? Network, string[] OtherFirewalls)
    {
        public bool PublicNetwork => ProfileType == Public;
    }

    // Whether [port] over TCP, from [exe], gets through on a network of [profile].
    public static Verdict Evaluate(IReadOnlyList<Profile> profiles, IEnumerable<Rule> rules, string exe, int port, int profile)
    {
        if (profiles.FirstOrDefault(p => p.Type == profile) is not { } settings) return Verdict.Unknown;
        if (!settings.Enabled) return Verdict.Off;
        if (settings.BlockAll) return Verdict.Blocked;
        var matching = rules.Where(r => r.Enabled && r.Direction == 1 && (r.Profiles & profile) != 0 && r.Protocol is 6 or 256 && Concerns(r, exe, port)).ToArray();
        // Windows applies a matching block rule before any allow rule.
        if (matching.Any(r => !r.Allow)) return Verdict.Blocked;
        if (matching.Any(r => r.Allow)) return Verdict.Allowed;
        if (settings.DefaultAllow) return Verdict.Off;
        // With its prompts turned off, Windows keeps an app without a rule out and asks nothing.
        return settings.NotificationsDisabled ? Verdict.Blocked : Verdict.Undecided;
    }

    private static bool Concerns(Rule rule, string exe, int port)
    {
        if (!string.IsNullOrWhiteSpace(rule.Application))
        {
            string path;
            try { path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rule.Application.Trim())); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
            if (!string.Equals(path, Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) return false;
            return PortMatches(rule.LocalPorts, port);
        }
        // A rule for the port alone, as someone opening it by hand would write; one for every program and port isn't
        // about BlackPearl and doesn't count.
        return rule.LocalPorts is { Length: > 0 } ports && ports.Trim() != "*" && PortMatches(ports, port);
    }

    internal static bool PortMatches(string? ports, int port)
    {
        if (string.IsNullOrWhiteSpace(ports) || ports.Trim() == "*") return true;
        foreach (var part in ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-');
            if (dash > 0 && int.TryParse(part[..dash], out var low) && int.TryParse(part[(dash + 1)..], out var high) && port >= low && port <= high) return true;
            if (int.TryParse(part, out var single) && single == port) return true;
        }
        return false;
    }

    // Reads the firewall for this exe and port. Slow (hundreds of rules, two COM servers): call it off the UI thread.
    public static Status Read(string exe, int port, string? address)
    {
        var (profileType, network) = HomeNetwork(address);
        Verdict verdict;
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null) return new(Verdict.Unknown, profileType, network, OtherFirewalls());
            dynamic policy = Activator.CreateInstance(type)!;
            try
            {
                var profiles = new List<Profile>();
                foreach (var kind in new[] { Domain, Private, Public })
                    profiles.Add(new(kind, (bool)policy.FirewallEnabled[kind], (bool)policy.BlockAllInboundTraffic[kind], (int)policy.DefaultInboundAction[kind] == 1, (bool)policy.NotificationsDisabled[kind]));
                // The network the phones come in on; unknown, every network this PC is on now.
                var current = profileType != 0 ? profileType : (int)policy.CurrentProfileTypes;
                var rules = new List<Rule>();
                foreach (dynamic rule in policy.Rules)
                {
                    try
                    {
                        if ((int)rule.Direction != 1) continue;
                        string? application = rule.ApplicationName, ports = rule.LocalPorts;
                        if (string.IsNullOrEmpty(application) && string.IsNullOrEmpty(ports)) continue;
                        rules.Add(new(application, (bool)rule.Enabled, (int)rule.Profiles, (int)rule.Action == 1, (int)rule.Protocol, ports));
                    }
                    finally { Marshal.FinalReleaseComObject(rule); }
                }
                // Every profile the phones' network could be counted under must let them in.
                var verdicts = new[] { Domain, Private, Public }.Where(p => (current & p) != 0).Select(p => (Profile: p, Verdict: Evaluate(profiles, rules, exe, port, p))).ToArray();
                verdict = verdicts.Length == 0 ? Verdict.Unknown
                    : verdicts.Any(v => v.Verdict == Verdict.Blocked) ? Verdict.Blocked
                    : verdicts.Any(v => v.Verdict == Verdict.Undecided) ? Verdict.Undecided
                    : verdicts.Any(v => v.Verdict == Verdict.Unknown) ? Verdict.Unknown
                    : verdicts.All(v => v.Verdict == Verdict.Off) ? Verdict.Off : Verdict.Allowed;
                if (profileType == 0 && verdicts.Length == 1) profileType = verdicts[0].Profile;
            }
            finally { Marshal.FinalReleaseComObject(policy); }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException or InvalidOperationException)
        {
            PlaybackDiagnostics.Record("remote-lan-firewall-failed", new { error = ex.GetType().Name });
            verdict = Verdict.Unknown;
        }
        return new(verdict, profileType, network, OtherFirewalls());
    }

    // The category and name of the network the address is on, from Windows' network list. (0, null) when unknown.
    private static (int Profile, string? Name) HomeNetwork(string? address)
    {
        if (address is null) return (0, null);
        int index;
        try
        {
            var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(a => a.GetIPProperties().UnicastAddresses.Any(u => u.Address.ToString() == address));
            if (adapter?.GetIPProperties().GetIPv4Properties() is not { } ipv4) return (0, null);
            index = ipv4.Index;
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { return (0, null); }
        foreach (var profile in Query(@"root\StandardCimv2", "SELECT InterfaceIndex, Name, NetworkCategory FROM MSFT_NetConnectionProfile"))
        {
            if (Convert.ToInt32(profile["InterfaceIndex"]) != index) continue;
            var category = Convert.ToInt32(profile["NetworkCategory"]);
            return (category switch { 1 => Private, 2 => Domain, _ => Public }, profile["Name"] as string);
        }
        return (0, null);
    }

    // Firewalls other than Windows' own that Windows Security lists as on, such as an antivirus suite's.
    private static string[] OtherFirewalls() =>
        Query(@"root\SecurityCenter2", "SELECT displayName, productState FROM FirewallProduct")
            .Where(p => p["displayName"] is string { Length: > 0 } && (Convert.ToInt64(p["productState"] ?? 0L) >> 12 & 0xF) == 1)
            .Select(p => (string)p["displayName"]!).Distinct().ToArray();

    private static List<Dictionary<string, object?>> Query(string space, string query)
    {
        var rows = new List<Dictionary<string, object?>>();
        try
        {
            var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (type is null) return rows;
            dynamic locator = Activator.CreateInstance(type)!;
            dynamic service = locator.ConnectServer(".", space);
            foreach (dynamic row in service.ExecQuery(query))
            {
                var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (dynamic property in row.Properties_) values[(string)property.Name] = property.Value;
                rows.Add(values);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException or InvalidOperationException) { }
        return rows;
    }
}

// Reads the firewall for the running exe and writes what it found to the diagnostics log when that changed.
internal static class PhoneFirewallLog
{
    private static string? logged;

    public static PhoneFirewall.Status Check(int port)
    {
        var status = PhoneFirewall.Read(Environment.ProcessPath ?? "", port, PhoneRemoteProtocol.LanAddresses().FirstOrDefault());
        var profile = status.ProfileType switch { PhoneFirewall.Domain => "domain", PhoneFirewall.Private => "private", PhoneFirewall.Public => "public", _ => "unknown" };
        var key = $"{status.Verdict}|{profile}|{string.Join(',', status.OtherFirewalls)}|{port}";
        if (Interlocked.Exchange(ref logged, key) != key)
            PlaybackDiagnostics.Record("remote-lan", new { state = "firewall", verdict = status.Verdict.ToString().ToLowerInvariant(), profile, port, otherFirewalls = status.OtherFirewalls });
        return status;
    }
}
