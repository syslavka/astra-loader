using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace NLCleaner.Services;

public static class Elevation
{
    public static bool IsAdministrator
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
    }

    // Elevation is requested only for an explicit cleanup or recovery operation.
    public static bool StartOperation(bool restoring)
    {
        var start = new ProcessStartInfo(DesktopPreferences.ExecutablePath)
        {
            UseShellExecute = true, Verb = "runas",
            Arguments = restoring ? "--restore" : "--run"
        };
        try { using var process = Process.Start(start); return process is not null; }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; }
    }
}
