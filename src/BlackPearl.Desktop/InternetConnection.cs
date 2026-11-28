using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace BlackPearl.Desktop;

// Whether this PC has the internet, as Windows judges it for the network icon in the taskbar (its Network List Manager).
// Windows works this out from its own probes, so asking sends nothing. Nothing here may touch WPF (see CrashReports).
internal static class InternetConnection
{
    private static readonly Guid ManagerClass = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    // netlistmgr.h; only IsConnectedToInternet is called, and the methods before it hold its place in the table.
    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworks(int flags);
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetwork(Guid networkId);
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworkConnections();
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworkConnection(Guid connectionId);
        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    }

    /// <summary>Windows' answer, or null when it gave none (its service is off, or the call failed).</summary>
    internal static bool? Ask()
    {
        object? manager = null;
        try
        {
            manager = Type.GetTypeFromCLSID(ManagerClass, throwOnError: false) is { } type ? Activator.CreateInstance(type) : null;
            return manager is INetworkListManager list ? list.IsConnectedToInternet : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException or InvalidComObjectException) { return null; }
        finally { if (manager is not null && Marshal.IsComObject(manager)) Marshal.ReleaseComObject(manager); }
    }

    // An offline launch fails every title in turn, so one answer serves the next few seconds of them.
    private static readonly TimeSpan Reuse = TimeSpan.FromSeconds(10);
    private static readonly object gate = new();
    private static long askedAt;
    private static bool? answer;
    // Checks set this to play a PC with or without the internet.
    internal static Func<bool?>? AnswerForCheck { get; set; }

    /// <summary>False only when Windows says this PC has no internet. An unknown answer counts as online, so nothing is left
    /// unreported because Windows could not say.</summary>
    internal static bool Online()
    {
        if (AnswerForCheck is { } check) return check() ?? true;
        lock (gate)
        {
            if (askedAt == 0 || Stopwatch.GetElapsedTime(askedAt) > Reuse) { answer = Ask(); askedAt = Stopwatch.GetTimestamp(); }
            return answer ?? true;
        }
    }

    /// <summary>A request that never reached the server: its name didn't resolve, no connection was made, or it timed out.
    /// A server that answered, even with an error, did reach it.</summary>
    internal static bool Unreached(Exception error)
    {
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { StatusCode: not null }) return false;
            if (e is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError }
                or SocketException or TimeoutException) return true;
        }
        return false;
    }
}
