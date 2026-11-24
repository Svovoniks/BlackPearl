using System.Diagnostics;
using System.IO;
using BlackPearl.Core;
using BlackPearl.Desktop;
using static BlackPearl.Desktop.PhoneFirewall;

// The phone remote on the home network, the desktop's part (PhoneFirewall.cs, PhoneRemote.cs): what Windows Firewall's
// settings and rules mean for BlackPearl's listener, and what Settings › Profile › Phone remote says about it. The
// listener itself is checked end to end by the smoke suite (tests/BlackPearl.Smoke/PhoneLanFixture.cs).
static class PhoneLanChecks
{
    public static void Run(Action<bool, string> check)
    {
        const string exe = @"C:\Users\someone\AppData\Local\Programs\BlackPearl\BlackPearl.exe";
        const int port = 47830;
        Profile[] On(bool defaultAllow = false, bool notificationsOff = false, bool blockAll = false) =>
            [new(Domain, true, blockAll, defaultAllow, notificationsOff), new(Private, true, blockAll, defaultAllow, notificationsOff), new(Public, true, blockAll, defaultAllow, notificationsOff)];
        // What Windows' prompt writes when the user allows private networks: allow rules for TCP and UDP on private,
        // block rules on public.
        Rule[] prompted =
        [
            new(exe, true, Private, true, 6, "*"), new(exe, true, Private, true, 17, "*"),
            new(exe, true, Public, false, 6, "*"), new(exe, true, Public, false, 17, "*"),
        ];

        check(Evaluate(On(), prompted, exe, port, Private) == Verdict.Allowed, "the rule Windows' prompt adds lets BlackPearl in on a private network");
        check(Evaluate(On(), prompted, exe, port, Public) == Verdict.Blocked, "and keeps it out on a public one, as the prompt's unticked box does");
        check(Evaluate(On(), [], exe, port, Private) == Verdict.Undecided, "no rule at all is undecided: Windows asks, and keeps BlackPearl out meanwhile");
        check(Evaluate(On(notificationsOff: true), [], exe, port, Private) == Verdict.Blocked, "with Windows' prompts turned off, no rule is a block");
        check(Evaluate(On(defaultAllow: true), [], exe, port, Private) == Verdict.Off, "a firewall that lets everything in counts as off");
        check(Evaluate([new(Private, false, false, false, false)], prompted, exe, port, Private) == Verdict.Off, "a firewall turned off on the network is off, whatever its rules");
        check(Evaluate(On(blockAll: true), prompted, exe, port, Private) == Verdict.Blocked, "Block all incoming connections wins over BlackPearl's allow rule");
        check(Evaluate(On(), [.. prompted, new(exe, true, Private, false, 6, null)], exe, port, Private) == Verdict.Blocked, "a block rule wins over an allow rule, as in Windows");
        check(Evaluate(On(), [new(exe, false, Private, false, 6, null), new(exe, true, Private, true, 256, null)], exe, port, Private) == Verdict.Allowed,
            "a disabled block rule doesn't count, and a rule for any protocol does");
        check(Evaluate(On(), [new(exe, true, Private, true, 17, "*")], exe, port, Private) == Verdict.Undecided, "a rule for UDP alone doesn't let the listener's TCP in");
        check(Evaluate(On(), [new(exe, true, Private, true, 6, "*", Direction: 2)], exe, port, Private) == Verdict.Undecided, "an outbound rule doesn't count");
        check(Evaluate(On(), [new(@"%LOCALAPPDATA%\Programs\BlackPearl\BlackPearl.exe".Replace("%LOCALAPPDATA%", @"C:\Users\someone\AppData\Local"), true, Private, true, 6, null)], exe.ToUpperInvariant(), port, Private) == Verdict.Allowed,
            "the program's path is compared without case");
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA")!;
        check(Evaluate(On(), [new(@"%LOCALAPPDATA%\Programs\BlackPearl\BlackPearl.exe", true, Private, true, 6, null)], Path.Combine(localAppData, @"Programs\BlackPearl\BlackPearl.exe"), port, Private) == Verdict.Allowed,
            "a rule whose path names an environment variable matches the program it expands to");
        check(Evaluate(On(), [new(@"C:\Dev\BlackPearl\BlackPearl.exe", true, Private, true, 6, null)], exe, port, Private) == Verdict.Undecided, "a rule for another copy of BlackPearl doesn't count");
        check(Evaluate(On(), [new(null, true, Private, true, 6, "47800-47839")], exe, port, Private) == Verdict.Allowed && Evaluate(On(), [new(null, true, Private, true, 6, "80,443")], exe, port, Private) == Verdict.Undecided,
            "a rule opening the port for every program counts; one for other ports doesn't");
        check(Evaluate(On(), [new(null, true, Private, false, 6, "*")], exe, port, Private) == Verdict.Undecided, "a rule for every program and port isn't about BlackPearl");
        check(Evaluate(On(), [new(exe, true, Private, true, 6, "5000")], exe, port, Private) == Verdict.Undecided && PortMatches("47830, 50000-50010", 47830) && !PortMatches("47831", 47830),
            "a rule for BlackPearl on other ports doesn't let the listener in");

        // What Settings says.
        var listening = new PhoneLanState("listening", port, false);
        string Say(PhoneLanState lan, Status? wall, string? address = "192.168.1.20") => MainWindow.PhoneHomeNetworkText(lan, wall, address).Text;
        bool Offers(Status? wall) => MainWindow.PhoneHomeNetworkText(listening, wall, "192.168.1.20").ShowSettings;
        check(Say(listening, new(Verdict.Allowed, Private, "Home", [])) == "When the room server can't introduce them, phones on your home network connect to this PC directly, at 192.168.1.20. Windows Firewall lets BlackPearl in on Home." && !Offers(new(Verdict.Allowed, Private, "Home", [])),
            "with Windows Firewall allowing it, Settings says where phones connect and that the firewall lets them in, and nothing more");
        check(Say(listening, null).EndsWith("Checking Windows Firewall.", StringComparison.Ordinal) && !Offers(null), "while the firewall is read, Settings says so");
        check(Say(listening, new(Verdict.Undecided, Private, "Home", [])).Contains("choose Allow when Windows asks", StringComparison.Ordinal) && Offers(new(Verdict.Undecided, Private, "Home", [])),
            "no rule yet: Settings says to choose Allow and offers Windows' firewall settings");
        var blocked = Say(listening, new(Verdict.Blocked, Public, "Cafe", []));
        check(blocked.Contains("blocks BlackPearl on Cafe", StringComparison.Ordinal) && blocked.Contains("tick BlackPearl for public networks", StringComparison.Ordinal) &&
            blocked.Contains("set it to Private", StringComparison.Ordinal) && Offers(new(Verdict.Blocked, Public, "Cafe", [])),
            "blocked on a public network: Settings says so, how to allow it, and that a home network should be set to Private");
        check(!Say(listening, new(Verdict.Blocked, Private, "Home", [])).Contains("public", StringComparison.Ordinal), "blocked on a private network says nothing about public networks");
        check(Say(listening, new(Verdict.Unknown, 0, null, [])).EndsWith("can't tell whether Windows Firewall lets phones in.", StringComparison.Ordinal), "a firewall that can't be read isn't called open");
        check(Say(listening, new(Verdict.Allowed, Private, "Home", ["Contoso Security"])).EndsWith("Contoso Security has a firewall of its own, which may need to allow BlackPearl too.", StringComparison.Ordinal),
            "another firewall that Windows lists is named, as one that may also need to allow BlackPearl");
        check(!Say(listening, new(Verdict.Allowed, Private, "Home", [])).Contains("works", StringComparison.OrdinalIgnoreCase), "Settings never claims the home network works, only what the firewall decided");
        check(Say(listening, new(Verdict.Off, Private, null, []), address: null) == "When the room server can't introduce them, phones on your home network connect to this PC directly. Windows Firewall is off on this network.",
            "with no address to give and no network name, Settings leaves them out");
        check(Say(new("busy", null, false), null).Contains("47830 to 47839 are all in use", StringComparison.Ordinal), "every port taken: Settings says phones still connect through the room server");
        check(Say(new("waiting", port, false), null).Contains("handing the home network over", StringComparison.Ordinal), "while the phone host still has the port, Settings says it is handing it over");
        check(Say(new("listening", port, true), null) == $"This build listens for phones on this PC only, at port {port}, for testing.", "a development build listening on this PC only says so");

        // This PC's own firewall, read for the checks' own program: it answers without throwing, in a few seconds.
        var timer = Stopwatch.StartNew();
        var status = Read(Environment.ProcessPath!, port, PhoneRemoteProtocol.LanAddresses().FirstOrDefault());
        Console.WriteLine($"This PC's firewall for the checks' program: {status.Verdict}, profile {status.ProfileType}, other firewalls: {status.OtherFirewalls.Length}, {timer.ElapsedMilliseconds} ms");
        check(timer.ElapsedMilliseconds < 10000, "reading this PC's firewall takes seconds at most, and needs no administrator");
    }
}
