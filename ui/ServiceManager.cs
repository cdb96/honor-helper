using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace HonorHelper;

/// <summary>
/// Manages the background <c>honor-helper-service</c> from the UI side: detects if
/// it is running, ensures it is started (promoting to admin via UAC when needed so
/// the HONOR WMI calls work), and can toggle auto-start at login. The service exe is
/// expected to sit next to the UI exe (published together) or alongside in the
/// package as <c>honor-helper-service.exe</c>.
/// </summary>
public static class ServiceManager
{
    /// <summary>Locate the service exe next to the current process exe.</summary>
    public static string ServicePath
    {
        get
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return string.Empty;
            var dir = Path.GetDirectoryName(exe);
            return dir is null ? string.Empty : Path.Combine(dir, "honor-helper-service.exe");
        }
    }

    public static bool IsServiceRunning() => ServiceClient.IsServiceRunning();

    /// <summary>Ensure the service is running. Returns true if it is (or started).</summary>
    public static async Task<bool> EnsureServiceAsync()
    {
        if (IsServiceRunning())
            return true;

        var svc = ServicePath;
        if (string.IsNullOrEmpty(svc) || !File.Exists(svc))
            return false;

        // Start it. If the current UI is not elevated, request elevation (UAC) so
        // the service can open the HONOR WMI. The service runs as a separate headless
        // process, so it stays alive after the UI closes.
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = svc,
                UseShellExecute = true,
                CreateNoWindow = true,
                Verb = "runas",   // elevate so WMI opens
            };
            Process.Start(psi);
        }
        catch
        {
            // User cancelled UAC; try without elevation.
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = svc,
                    UseShellExecute = true,
                    CreateNoWindow = true,
                };
                Process.Start(psi);
            }
            catch
            {
                return false;
            }
        }

        // Give it a few seconds to start and bind the pipe.
        for (int i = 0; i < 20; i++)
        {
            if (IsServiceRunning())
                return true;
            await Task.Delay(250);
        }
        return IsServiceRunning();
    }

    /// <summary>Toggle auto-start at login for the service (HKCU Run). Elevated not required.</summary>
    public static async Task<bool> SetAutoStartAsync(bool enabled)
    {
        var svc = ServicePath;
        if (string.IsNullOrEmpty(svc))
            return false;

        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = svc,
                    UseShellExecute = true,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add(enabled ? "--install" : "--uninstall");
                Process.Start(psi)?.WaitForExit();
                return true;
            }
            catch
            {
                return false;
            }
        });
    }
}
