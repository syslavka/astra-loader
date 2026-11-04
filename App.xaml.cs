using System.IO;
using System.Windows;
using NLCleaner.Services;

namespace NLCleaner;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--restore-and-exit", StringComparer.Ordinal))
        {
            var recoveryDryRun = false;
            var expectedUser = Journal.UserId;
            for (var i = 0; i < e.Args.Length; i++)
            {
                if (e.Args[i] == "--restore-and-exit") continue;
                if (e.Args[i] == "--dry-run") { recoveryDryRun = true; continue; }
                if (e.Args[i] == "--recovery-user" && i + 1 < e.Args.Length) { expectedUser = e.Args[++i]; continue; }
                Console.Error.WriteLine("Invalid recovery arguments."); Shutdown(2); return;
            }
            _ = RestoreAndExitAsync(recoveryDryRun, expectedUser);
            return;
        }
        if (e.Args.Length == 1 && e.Args[0] is "--startup-enable" or "--startup-disable" or "--startup-status")
        {
            try
            {
                if (e.Args[0] != "--startup-status") DesktopPreferences.SetStartup(e.Args[0] == "--startup-enable");
                Console.WriteLine(DesktopPreferences.StartupEnabled ? "Startup menu: enabled" : "Startup menu: disabled");
                Shutdown();
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); Shutdown(1); }
            return;
        }
        var dryRun = e.Args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
        var restore = e.Args.Contains("--restore", StringComparer.OrdinalIgnoreCase);
        var run = e.Args.Contains("--run", StringComparer.OrdinalIgnoreCase);
        var manual = e.Args.Contains("--manual", StringComparer.OrdinalIgnoreCase) || !(run || restore || dryRun);
        var animationTest = e.Args.Contains("--animation-test", StringComparer.OrdinalIgnoreCase);
        if (animationTest && !dryRun) { MessageBox.Show("--animation-test requires --dry-run."); Shutdown(2); return; }
        var known = new[] { "--dry-run", "--manual", "--restore", "--run", "--capture", "--animation-test" };
        if (run && restore) { MessageBox.Show("Choose --run or --restore."); Shutdown(2); return; }
        string? capture = null;
        for (var i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--capture" && dryRun && i + 1 < e.Args.Length)
            { capture = Path.GetFullPath(e.Args[++i]); continue; }
            if (!known.Contains(e.Args[i]) || e.Args[i] == "--capture")
            { MessageBox.Show("Arguments: --run, --manual, --restore, --dry-run [--capture image.png]"); Shutdown(2); return; }
        }
        var elevated = Elevation.IsAdministrator;
        if (!dryRun && !manual && !elevated)
        {
            try { Shutdown(Elevation.StartOperation(restore) ? 0 : 5); }
            catch (Exception ex) { MessageBox.Show(ex.Message); Shutdown(1); }
            return;
        }
        _mutex = new Mutex(false, dryRun ? @"Local\NL-Cleaner-Preview" : elevated ? @"Global\NL-Cleaner-Operation" : @"Local\AstraLoader-Menu");
        try { _ownsMutex = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsMutex = true; }
        if (!_ownsMutex) { MessageBox.Show("Astra Loader is already running."); Shutdown(1); return; }
        new MainWindow(dryRun, manual, restore, capture, animationTest).Show();
    }

    private async Task RestoreAndExitAsync(bool dryRun, string expectedUser)
    {
        try { Shutdown(await RecoveryCommand.RunAsync(dryRun, expectedUser)); }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            if (!dryRun) MessageBox.Show(ex.Message, "Astra Loader — восстановление", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
