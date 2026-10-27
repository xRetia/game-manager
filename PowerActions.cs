// Power & stream actions behind the header power menu:
//   - ExitStream: force-disconnect the Moonlight clients of THIS session's
//     Sunshine/Apollo host by cutting their TCP control connections, without
//     touching sunshine.exe itself. No credentials are needed: we never call
//     the Apollo HTTP API, we simply remove the client's established
//     connections. The RTSP connection (TCP 48010) anchors the stream session,
//     so once it is gone Apollo ends the session on its own and returns to
//     idle - sunshine.exe keeps running and the next client can connect.
//     Requires admin rights (SetTcpEntry); without them this degrades to a
//     no-op and the caller still closes the launcher.
//   - LogOff / Shutdown / Restart: forced session end via the built-in
//     shutdown.exe (handles the SE_SHUTDOWN privilege itself, elevated or not).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;

namespace GameManager;

internal enum PowerAction
{
    Disconnect,
    LogOff,
    Shutdown,
    Restart
}

internal static class PowerActions
{
    // Sunshine/Apollo standard TCP listening ports. Moonlight clients keep
    // established connections here while streaming (47984/47989 = HTTPS/HTTP
    // control, 48010 = RTSP session anchor). UDP carries the media but the
    // session lives and dies with the RTSP TCP connection.
    private static readonly int[] StreamPorts = { 47984, 47989, 48010 };

    private const int AfInet = 2;                 // AF_INET (IPv4 table)
    private const int TcpTableOwnerPidAll = 5;    // TCP_TABLE_OWNER_PID_ALL
    private const int TcpStateEstablished = 5;    // MIB_TCP_STATE_ESTAB
    private const int TcpStateDeleteTcb = 12;     // MIB_TCP_STATE_DELETE_TCB
    private const int RowSize = 24;               // MIB_TCPROW_OWNER_PID: 6 x DWORD

    /// <summary>
    /// Kills every established, non-loopback TCP connection to Sunshine's
    /// stream ports that is owned by a sunshine.exe running in THIS session.
    /// Returns the number of connections cut (0 = nothing streaming, or
    /// admin rights missing).
    /// </summary>
    internal static int DisconnectStreamClients()
    {
        try
        {
            int session = Process.GetCurrentProcess().SessionId;

            var hosts = new HashSet<uint>();
            foreach (Process? p in Process.GetProcessesByName("sunshine"))
            {
                try
                {
                    if (p.SessionId == session) hosts.Add((uint)p.Id);
                }
                catch
                {
                    // protected/inaccessible process - skip it
                }
                finally
                {
                    p.Dispose();
                }
            }
            if (hosts.Count == 0)
            {
                ServiceHost.Log("exit-stream: no sunshine.exe in session " + session + ", nothing to disconnect");
                return 0;
            }

            int size = 0;
            uint err = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
            if (err != 122 /* ERROR_INSUFFICIENT_BUFFER */ || size <= 0)
            {
                ServiceHost.Log("exit-stream: GetExtendedTcpTable sizing failed, err=" + err);
                return 0;
            }

            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                err = GetExtendedTcpTable(buf, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
                if (err != 0)
                {
                    ServiceHost.Log("exit-stream: GetExtendedTcpTable failed, err=" + err);
                    return 0;
                }

                int count = Marshal.ReadInt32(buf, 0);
                int killed = 0, denied = 0;
                for (int i = 0; i < count; i++)
                {
                    IntPtr rowPtr = IntPtr.Add(buf, 4 + i * RowSize);

                    if (Marshal.ReadInt32(rowPtr, 0) != TcpStateEstablished) continue;      // dwState
                    if (!hosts.Contains((uint)Marshal.ReadInt32(rowPtr, 20))) continue;     // dwOwningPid

                    int localPort = HostPort(Marshal.ReadInt32(rowPtr, 8));                 // dwLocalPort
                    if (Array.IndexOf(StreamPorts, localPort) < 0) continue;

                    int remoteAddr = Marshal.ReadInt32(rowPtr, 12);                         // dwRemoteAddr
                    if (IsLoopback(remoteAddr)) continue;

                    var row = new MIB_TCPROW
                    {
                        dwState = TcpStateDeleteTcb,
                        dwLocalAddr = (uint)Marshal.ReadInt32(rowPtr, 4),
                        dwLocalPort = (uint)Marshal.ReadInt32(rowPtr, 8),
                        dwRemoteAddr = (uint)remoteAddr,
                        dwRemotePort = (uint)Marshal.ReadInt32(rowPtr, 16)
                    };
                    if (SetTcpEntry(ref row) == 0) killed++;
                    else denied++;
                }

                ServiceHost.Log("exit-stream: cut " + killed + " client connection(s) (denied " + denied + ")");
                return killed;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch (Exception ex)
        {
            ServiceHost.Log("exit-stream: disconnect failed: " + ex.Message);
            return 0;
        }
    }

    /// <summary>TCP table ports are stored in network byte order.</summary>
    private static int HostPort(int networkPort) =>
        (ushort)IPAddress.NetworkToHostOrder((short)(networkPort & 0xFFFF));

    /// <summary>Remote address is in network byte order; true for 127.x.x.x.</summary>
    private static bool IsLoopback(int networkAddr) =>
        ((uint)IPAddress.NetworkToHostOrder(networkAddr) >> 24) == 127;

    internal static void LogOff() => RunShutdownTool("/l");

    internal static void Shutdown() => RunShutdownTool("/s /f /t 0");

    internal static void Restart() => RunShutdownTool("/r /f /t 0");

    private static void RunShutdownTool(string arguments)
    {
        // Full path on purpose: in sessions spawned by the service (multi-seat
        // seats included) PATH may resolve differently or not at all, and a
        // bare "shutdown" then silently does nothing.
        string exe = Environment.ExpandEnvironmentVariables(
            Environment.SystemDirectory is { Length: > 0 } sysDir
                ? Path.Combine(sysDir, "shutdown.exe")
                : @"C:\Windows\System32\shutdown.exe");

        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p == null)
            {
                ServiceHost.Log("power: shutdown.exe " + arguments + " returned no process");
                return;
            }
            // short exit wait so a refused shutdown (exit code != 0) is visible
            // in install-log.txt instead of vanishing silently
            if (!p.WaitForExit(3000))
            {
                ServiceHost.Log("power: shutdown.exe " + arguments + " still running after 3s");
            }
            else
            {
                ServiceHost.Log("power: shutdown.exe " + arguments + " exit=" + p.ExitCode);
            }
        }
        catch (Exception ex)
        {
            ServiceHost.Log("power: shutdown.exe " + arguments + " failed: " + ex.Message);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen,
        bool sort, int ipVersion, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint SetTcpEntry(ref MIB_TCPROW row);
}
