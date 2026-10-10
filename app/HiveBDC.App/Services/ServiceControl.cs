using System.ComponentModel;
using System.ServiceProcess;

namespace Balsoft.Hive.BDC.App.Services;

/// <summary>Result of a start/stop attempt, plus a message safe to show the user.</summary>
public sealed record ServiceActionResult(bool Ok, string Message);

/// <summary>
/// Thin wrapper over <see cref="ServiceController"/> for the "HiveBDC" Windows
/// service. Status queries need no elevation; Start/Stop do, so the access-denied /
/// elevation failure is caught and turned into a friendly "run as administrator"
/// message instead of throwing. Never crashes the UI.
/// </summary>
public sealed class ServiceControl
{
    public const string ServiceName = "HiveBDC";

    private const int ERROR_ACCESS_DENIED = 5;
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Current status text: Running / Stopped / StartPending / … / Not installed / Unknown.</summary>
    public string GetStatus()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            string status = sc.Status.ToString();
            AppLog.Recovered("Reading the Windows service status");
            return status;
        }
        catch (InvalidOperationException)
        {
            return "Not installed";
        }
        catch (Exception ex)
        {
            AppLog.Failed("Reading the Windows service status", ex.Message);
            return "Unknown";
        }
    }

    public bool IsRunning()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            return false;
        }
    }

    public ServiceActionResult Start()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
                return new ServiceActionResult(true, "Service is already running.");
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, WaitTimeout);
            return new ServiceActionResult(true, "Service started.");
        }
        catch (Exception ex)
        {
            return Fail(ex, "start");
        }
    }

    public ServiceActionResult Stop()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
                return new ServiceActionResult(true, "Service is already stopped.");
            if (!sc.CanStop)
                return new ServiceActionResult(false, "The service cannot be stopped right now.");
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, WaitTimeout);
            return new ServiceActionResult(true, "Service stopped.");
        }
        catch (Exception ex)
        {
            return Fail(ex, "stop");
        }
    }

    private static ServiceActionResult Fail(Exception ex, string verb)
    {
        if (IsAccessDenied(ex))
            return new ServiceActionResult(false,
                $"Access denied — run the app as administrator to {verb} the service.");
        if (ex is InvalidOperationException && ex.InnerException is null)
            return new ServiceActionResult(false, $"The '{ServiceName}' service is not installed.");
        return new ServiceActionResult(false, $"Could not {verb} the service: {ex.Message}");
    }

    private static bool IsAccessDenied(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is Win32Exception w32 && w32.NativeErrorCode == ERROR_ACCESS_DENIED)
                return true;
        return false;
    }
}
