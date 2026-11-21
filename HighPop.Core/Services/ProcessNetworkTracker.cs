using System.Globalization;
using System.Runtime.InteropServices;

namespace HighPop.Services;

/// <summary>
/// Reads the owning PID of each TCP connection from iphlpapi.dll.
/// Needs no elevated rights (admin) and works on Windows 7+. On Linux the same data comes from
/// /proc/net/tcp and the socket links under /proc/&lt;pid&gt;/fd.
/// </summary>
public static class ProcessNetworkTracker
{
    // TCP_TABLE_OWNER_PID_ALL = 5
    private const int AfInet       = 2;
    private const int TableOwnerPidAll = 5;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder,
        int ulAf, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;   // network byte order → big-endian
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    /// <summary>
    /// Returns the number of TCP connections per PID.
    /// Called from a background thread, not the UI thread.
    /// </summary>
    public static Dictionary<int, int> GetConnectionCountsByPid()
    {
        if (!OperatingSystem.IsWindows()) return GetConnectionCountsByPidLinux();

        var result = new Dictionary<int, int>();

        int bufSize = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref bufSize, false, AfInet, TableOwnerPidAll, 0);

        var buf = Marshal.AllocHGlobal(bufSize);
        try
        {
            uint ret = GetExtendedTcpTable(buf, ref bufSize, false, AfInet, TableOwnerPidAll, 0);
            if (ret != 0) return result;

            int rowCount = Marshal.ReadInt32(buf);
            int rowSize  = Marshal.SizeOf<MibTcpRowOwnerPid>();
            int offset   = 4; // skip dwNumEntries

            for (int i = 0; i < rowCount; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(buf + offset);
                offset += rowSize;

                int pid = (int)row.OwningPid;
                result[pid] = result.TryGetValue(pid, out int c) ? c + 1 : 1;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }

        return result;
    }

    /// <summary>
    /// Returns the ports the process listens on (LISTEN state = 2).
    /// </summary>
    public static List<int> GetListeningPortsForPid(int pid)
    {
        if (!OperatingSystem.IsWindows()) return GetListeningPortsForPidLinux(pid);

        var ports = new List<int>();

        int bufSize = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref bufSize, false, AfInet, TableOwnerPidAll, 0);

        var buf = Marshal.AllocHGlobal(bufSize);
        try
        {
            uint ret = GetExtendedTcpTable(buf, ref bufSize, false, AfInet, TableOwnerPidAll, 0);
            if (ret != 0) return ports;

            int rowCount = Marshal.ReadInt32(buf);
            int rowSize  = Marshal.SizeOf<MibTcpRowOwnerPid>();
            int offset   = 4;

            for (int i = 0; i < rowCount; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(buf + offset);
                offset += rowSize;

                // State 2 = MIB_TCP_STATE_LISTEN
                if ((int)row.OwningPid == pid && row.State == 2)
                    ports.Add((int)(((row.LocalPort & 0xFF) << 8) | (row.LocalPort >> 8 & 0xFF)));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }

        return ports;
    }

    // ── Linux (/proc) ────────────────────────────────────────────────────────

    internal readonly record struct TcpRow(int State, int LocalPort, long Inode);

    /// <summary>Parses /proc/net/tcp or /proc/net/tcp6. State 0A is LISTEN.</summary>
    internal static List<TcpRow> ParseProcNetTcp(string text)
    {
        var rows = new List<TcpRow>();
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10 || !fields[0].EndsWith(':')) continue; // skips the header row
            var colon = fields[1].LastIndexOf(':');
            if (colon < 0) continue;
            if (!int.TryParse(fields[1][(colon + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var port)
                || !int.TryParse(fields[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var state)
                || !long.TryParse(fields[9], NumberStyles.None, CultureInfo.InvariantCulture, out var inode))
                continue;
            rows.Add(new TcpRow(state, port, inode));
        }
        return rows;
    }

    private static List<TcpRow> ReadTcpRows()
    {
        var rows = new List<TcpRow>();
        foreach (var path in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            try { rows.AddRange(ParseProcNetTcp(File.ReadAllText(path))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return rows;
    }

    /// <summary>Maps socket inodes to the process that holds them. Processes we may not inspect are skipped.</summary>
    private static Dictionary<long, int> MapSocketInodesToPids()
    {
        var owners = new Dictionary<long, int>();
        foreach (var processDirectory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(processDirectory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                continue;
            try
            {
                foreach (var descriptor in Directory.EnumerateFileSystemEntries(Path.Combine(processDirectory, "fd")))
                {
                    var target = new FileInfo(descriptor).LinkTarget; // "socket:[12345]"
                    if (target != null && target.StartsWith("socket:[", StringComparison.Ordinal) && target.EndsWith(']')
                        && long.TryParse(target.AsSpan(8, target.Length - 9), NumberStyles.None, CultureInfo.InvariantCulture, out var inode))
                        owners[inode] = pid;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // exited or not ours
        }
        return owners;
    }

    private static Dictionary<int, int> GetConnectionCountsByPidLinux()
    {
        var result = new Dictionary<int, int>();
        var owners = MapSocketInodesToPids();
        foreach (var row in ReadTcpRows())
            if (owners.TryGetValue(row.Inode, out var pid))
                result[pid] = result.GetValueOrDefault(pid) + 1;
        return result;
    }

    private static List<int> GetListeningPortsForPidLinux(int pid)
    {
        var owners = MapSocketInodesToPids();
        return ReadTcpRows()
            .Where(row => row.State == 0x0A && owners.TryGetValue(row.Inode, out var owner) && owner == pid)
            .Select(row => row.LocalPort)
            .Distinct()
            .ToList();
    }
}
