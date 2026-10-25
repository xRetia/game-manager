// Non-UI helpers: steam:// protocol registration, URL parsing and game launching.
// (Formerly Program.cs - the Main entry point now lives in App.xaml.cs.)

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace GameManager;

internal static class Launcher
{
    internal const string ProtocolName = "steam";
    internal static string LinksFolder => Config.Current.LinksFolder;

    private const string ProtocolKey = @"Software\Classes\steam";

    internal static string ExePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

    internal static void RegisterAllUsers()
    {
        try
        {
            WriteProtocol(Registry.LocalMachine);
            MessageBox.Show(Strings.MsgRegisteredAll,
                Strings.AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(Strings.MsgNeedAdmin,
                Strings.AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Strings.AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    internal static void UnregisterAllUsers()
    {
        bool removed = false;
        try
        {
            if (IsRegisteredOn(Registry.LocalMachine))
            {
                Registry.LocalMachine.DeleteSubKeyTree(ProtocolKey, false);
                removed = true;
            }
            if (IsRegisteredOn(Registry.CurrentUser))
            {
                Registry.CurrentUser.DeleteSubKeyTree(ProtocolKey, false);
                removed = true;
            }
            MessageBox.Show(removed ? Strings.MsgRemoved : Strings.MsgNothingToRemove,
                Strings.AppTitle, MessageBoxButton.OK,
                removed ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(Strings.MsgNeedAdmin,
                Strings.AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Strings.AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Registers steam:// for the current user (no admin needed) when nothing is registered yet.</summary>
    internal static void RegisterBestEffort()
    {
        try
        {
            if (IsRegisteredOn(Registry.LocalMachine) || IsRegisteredOn(Registry.CurrentUser)) return;
            WriteProtocol(Registry.CurrentUser);
        }
        catch
        {
            // Best effort - the launcher still works without the protocol.
        }
    }

    private static void WriteProtocol(RegistryKey hive)
    {
        using var key = hive.CreateSubKey(ProtocolKey);
        key.SetValue(null, "URL:Steam Protocol");
        key.SetValue("URL Protocol", string.Empty);
        using var cmd = key.CreateSubKey(@"shell\open\command");
        cmd.SetValue(null, $"\"{ExePath}\" \"%1\"");
    }

    private static bool IsRegisteredOn(RegistryKey hive)
    {
        try
        {
            using var key = hive.OpenSubKey(ProtocolKey + @"\shell\open\command");
            if (key == null) return false;
            string? value = key.GetValue(null) as string;
            return !string.IsNullOrEmpty(value) &&
                   value.Contains("GameManager", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsRegisteredAllUsers() => IsRegisteredOn(Registry.LocalMachine);

    /// <summary>Short status string shown in the Protocol menu.</summary>
    internal static string ProtocolStatusText
    {
        get
        {
            bool all = IsRegisteredAllUsers();
            bool current = IsRegisteredOn(Registry.CurrentUser);
            if (all) return Strings.ProtocolRegisteredAll;
            if (current) return Strings.ProtocolRegisteredUser;
            return Strings.ProtocolNotRegistered;
        }
    }

    // ---------------- remote-session GPU acceleration ----------------

    // Group policy: "Use hardware graphics adapters for all Remote Desktop
    // Services sessions" (Remote Session Environment). Without it RDP-type
    // sessions enumerate the software adapter first, so WPF's D3D9Ex render
    // path silently falls back to CPU rendering - the classic cause of a
    // sluggish multi-seat UI while native D3D11/12 games (Unity/UE) still run
    // on the physical GPU.
    private const string TsPolicyKey =
        @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";

    /// <summary>True when the hardware-graphics-for-RDS-sessions policy is enabled.</summary>
    internal static bool IsRemoteGpuEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(TsPolicyKey);
            return key?.GetValue("bEnumerateHWBeforeSW") is int v && v != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Enables hardware GPU rendering for all RDP-type sessions:
    /// hardware adapters enumerated before software (bEnumerateHWBeforeSW),
    /// AVC444 preferred for quality, and hardware encoding preferred so the
    /// RDP encoder does not eat a CPU core at high framerates. Takes effect
    /// for newly connected sessions (existing ones must reconnect).</summary>
    internal static void EnableRemoteGpu()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(TsPolicyKey);
            key.SetValue("bEnumerateHWBeforeSW", 1, RegistryValueKind.DWord);
            key.SetValue("bAVC444Mode", 1, RegistryValueKind.DWord);
            key.SetValue("AVCHardwareEncodePreferred", 1, RegistryValueKind.DWord);
            MessageBox.Show(Strings.MsgRemoteGpuOn,
                Strings.AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(Strings.MsgNeedAdmin,
                Strings.AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Strings.AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Reverts the remote GPU policy values set by <see cref="EnableRemoteGpu"/>.</summary>
    internal static void DisableRemoteGpu()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(TsPolicyKey, writable: true);
            if (key != null)
            {
                foreach (string name in new[] { "bEnumerateHWBeforeSW", "bAVC444Mode", "AVCHardwareEncodePreferred" })
                {
                    key.DeleteValue(name, throwOnMissingValue: false);
                }
            }
            MessageBox.Show(Strings.MsgRemoteGpuOff,
                Strings.AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(Strings.MsgNeedAdmin,
                Strings.AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Strings.AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Status line for the Advanced menu.</summary>
    internal static string RemoteGpuStatusText =>
        IsRemoteGpuEnabled() ? Strings.RemoteGpuEnabled : Strings.RemoteGpuDisabled;

    internal static void RunElevated(string argument)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = argument,
                Verb = "runas",
                UseShellExecute = true
            });
        }
        catch
        {
            // UAC cancelled by the user - nothing to do.
        }
    }

    /// <summary>Returns true when args contain a steam:// URL; gameName is set for "run/&lt;name&gt;".</summary>
    internal static bool TryParseUrl(string[] args, out string? gameName)
    {
        gameName = null;
        string prefix = ProtocolName + "://";
        foreach (string arg in args)
        {
            if (!arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            string rest = arg[prefix.Length..].TrimStart('/');
            if (rest.StartsWith("run/", StringComparison.OrdinalIgnoreCase))
            {
                gameName = Uri.UnescapeDataString(rest[4..]);
            }
            return true;
        }
        return false;
    }

    internal static bool TryLaunchByName(string name)
    {
        try
        {
            if (!Directory.Exists(LinksFolder)) return false;

            string? match = Directory.GetFiles(LinksFolder, "*.lnk")
                .FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), name,
                    StringComparison.OrdinalIgnoreCase));
            if (match == null) return false;

            Launch(match);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Runs a .lnk shortcut (the shell resolves the real target).</summary>
    internal static void Launch(string lnkPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(lnkPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Strings.AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}