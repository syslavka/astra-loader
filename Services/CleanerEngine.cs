using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace NLCleaner.Services;

public sealed record Activity(string Time, string Level, string Message)
{
    public string Color => Level switch { "OK" => "#8ECEB0", "WARN" => "#E6B578", "ERROR" => "#EE879F", "PLAN" => "#B89AEC", _ => "#9D8DB4" };
    public override string ToString() => $"{Time} [{Level}] {Message}";
}

public sealed record RunResult(int Errors, bool Cancelled, bool DryRun, bool Restoring);

public sealed class CleanerEngine(bool dryRun, Action<Activity> activity, Action<int, string> phase)
{
    private readonly TargetPaths _paths = new();
    private int _errors;
    private StreamWriter? _fileLog;
    private CancellationToken _token;

    private void Log(string level, string message)
    {
        var entry = new Activity(DateTime.Now.ToString("HH:mm:ss"), level, message);
        activity(entry);
        if (_fileLog is null) return;
        try { _fileLog.WriteLine(entry); }
        catch (IOException) { _fileLog.Dispose(); _fileLog = null; activity(new Activity(entry.Time, "WARN", "Disk logging stopped; live log remains available.")); }
    }

    private bool Step(string description, Action action)
    {
        _token.ThrowIfCancellationRequested();
        if (dryRun) { Log("PLAN", description); return true; }
        try { action(); Log("OK", description); return true; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _errors++; Log("ERROR", $"{description}: {ex.Message}"); return false; }
    }

    public RunResult Run(bool restoring, CancellationToken token)
    {
        _token = token;
        var cancelled = false;
        try
        {
            var journal = new Journal(!dryRun);
            if (!dryRun)
            {
                var logPath = Path.Combine(journal.DirectoryPath, $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
                _fileLog = new StreamWriter(new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
                Log("INFO", $"Log: {logPath}");
            }
            Log("INFO", dryRun ? "DRY RUN — actions are listed without changing Windows." : "Elevated session started.");
            Log("INFO", $"Account: {Environment.UserDomainName}\\{Environment.UserName}; current profile + machine Roblox locations.");
            if (restoring) Restore(journal);
            else Clean(journal);
        }
        catch (OperationCanceledException) { cancelled = true; Log("WARN", "Stopped between operations. Applied blocks remain recoverable with Restore access."); }
        catch (Exception ex) { _errors++; Log("ERROR", ex.Message); }
        finally { _fileLog?.Dispose(); _fileLog = null; }
        Log("INFO", cancelled ? "Operation stopped." : dryRun ? "Preview complete. No system changes were made." : _errors == 0 ? "Operation complete." : $"Finished with {_errors} issue(s). Review the log.");
        return new RunResult(_errors, cancelled, dryRun, restoring);
    }

    private void Clean(Journal journal)
    {
        phase(0, "Working");
        var before = _errors;
        var executables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _paths.Roots)
        {
            _token.ThrowIfCancellationRequested();
            try
            {
                _paths.ValidateTarget(root);
                foreach (var exe in FileTree.FindExecutables(root, message => { _errors++; Log("WARN", message); })) executables.Add(exe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _errors++; Log("ERROR", $"Discover {root}: {ex.Message}"); }
            // These exact root paths remain blocked even after their files have been removed.
            foreach (var name in new[] { "RobloxPlayerBeta.exe", "RobloxPlayerLauncher.exe", "RobloxStudioBeta.exe", "RobloxStudioLauncherBeta.exe" })
                executables.Add(Path.Combine(root, name));
        }
        StopProcesses(executables);
        Step("Remove current-user Roblox Microsoft Store package", RemoveStorePackage);
        RestoreFolders(journal);
        foreach (var root in _paths.Roots)
        {
            if (journal.State.Folders.Any(f => f.Path.Equals(root, StringComparison.OrdinalIgnoreCase)))
            { _errors++; Log("ERROR", $"Previous lock could not be restored; skipping {root}"); continue; }
            Step($"Delete {root}", () => { _paths.ValidateTarget(root); FileTree.DeleteTree(root, _token); });
        }
        CleanRegistry();
        phase(0, _errors == before ? dryRun ? "Previewed" : "Removed" : "Review log");

        phase(1, "Working");
        before = _errors;
        foreach (var root in _paths.Roots)
        {
            Step($"Lock empty Roblox folder: {root}", () =>
            {
                _paths.ValidateTarget(root);
                if (journal.State.Folders.Any(f => f.Path.Equals(root, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("An unresolved prior lock exists.");
                var backup = FolderBlocker.Capture(root);
                journal.State.Folders.Add(backup);
                journal.Save(); // Persist recovery information BEFORE changing the ACL.
                FolderBlocker.Apply(root);
            });
        }
        phase(1, _errors == before ? dryRun ? "Previewed" : "Locked" : "Review log");

        phase(2, "Working");
        before = _errors;
        Step($"Block {HostsBlocker.Domains.Length} exact hosts names (IPv4 + IPv6)", () =>
        {
            journal.State.HostsPendingOrApplied = true;
            journal.Save();
            HostsBlocker.Update(_paths.HostsFile, Journal.UserId, true, journal.DirectoryPath);
        });
        Step("Flush Windows DNS resolver cache", HostsBlocker.FlushDns);
        WithFirewall(firewall =>
        {
            foreach (var executable in executables.Order(StringComparer.OrdinalIgnoreCase))
                foreach (var outbound in new[] { false, true })
                    Step($"Firewall {(outbound ? "OUT" : "IN")}: {executable}", () =>
                    {
                        var name = FirewallBlocker.RuleName(executable, outbound);
                        if (!journal.State.FirewallRules.Contains(name, StringComparer.Ordinal))
                        { journal.State.FirewallRules.Add(name); journal.Save(); }
                        firewall!.Add(executable, outbound);
                    });
            Step("Verify Windows Firewall profiles and local-policy enforcement", () => firewall!.CheckPolicy());
        });
        Log("INFO", "Firewall rules cover exact discovered/known paths. Hosts entries match exact names. No background watcher is installed.");
        phase(2, _errors == before ? dryRun ? "Previewed" : "Applied" : "Review log");
    }

    private void Restore(Journal journal)
    {
        phase(0, "Not required");
        phase(1, "Working");
        var before = _errors;
        RestoreFolders(journal);
        phase(1, _errors == before ? dryRun ? "Previewed" : "Restored" : "Review log");
        phase(2, "Working");
        before = _errors;
        if (journal.State.HostsPendingOrApplied || dryRun)
            Step("Remove this account's Astra Loader hosts block; preserve other entries", () =>
            {
                HostsBlocker.Update(_paths.HostsFile, Journal.UserId, false, journal.DirectoryPath);
                journal.State.HostsPendingOrApplied = false;
                journal.Save();
            });
        WithFirewall(firewall =>
        {
            foreach (var name in journal.State.FirewallRules.ToArray())
                Step($"Remove firewall rule {name}", () =>
                {
                    firewall!.Remove(name);
                    journal.State.FirewallRules.Remove(name);
                    journal.Save();
                });
        });
        Step("Flush Windows DNS resolver cache", HostsBlocker.FlushDns);
        Log("INFO", "Recovery removes Astra Loader blocks. Deleted installations, projects, and registry entries are not recreated.");
        phase(2, _errors == before ? dryRun ? "Previewed" : "Restored" : "Review log");
    }

    private void RestoreFolders(Journal journal)
    {
        foreach (var backup in journal.State.Folders.ToArray())
            Step($"Restore original folder access: {backup.Path}", () =>
            {
                _paths.ValidateTarget(backup.Path);
                FolderBlocker.Restore(backup);
                journal.State.Folders.Remove(backup);
                journal.Save();
            });
    }

    private void WithFirewall(Action<FirewallBlocker?> action)
    {
        if (dryRun) { action(null); return; }
        FirewallBlocker? firewall = null;
        if (!Step("Connect to Windows Firewall policy", () => firewall = new FirewallBlocker())) return;
        using (firewall) action(firewall);
    }

    private void StopProcesses(HashSet<string> executables)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                try { name = process.ProcessName; }
                catch (InvalidOperationException) { continue; }
                if (!name.StartsWith("Roblox", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is not null && FileTree.IsRobloxExecutable(path)) executables.Add(path);
                }
                catch (Exception ex) { Log("WARN", $"Cannot read process image {name}: {ex.Message}"); }
                Step($"Terminate {name} (PID {process.Id})", () =>
                {
                    if (process.HasExited) return;
                    process.Kill(); // .NET calls Win32 TerminateProcess on Windows.
                    if (!process.WaitForExit(5000)) throw new IOException("Process did not exit within five seconds.");
                });
            }
        }
        if (dryRun) Log("PLAN", "Terminate any Roblox* processes found at execution time.");
    }

    private void RemoveStorePackage()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; Get-AppxPackage -Name 'ROBLOXCORPORATION.ROBLOX' | Remove-AppxPackage -ErrorAction Stop" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start Store package removal.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        var timeout = Stopwatch.StartNew();
        try
        {
            while (!process.WaitForExit(150))
            {
                _token.ThrowIfCancellationRequested();
                if (timeout.Elapsed > TimeSpan.FromSeconds(90)) throw new TimeoutException("Store package removal timed out.");
            }
            if (process.ExitCode != 0) throw new IOException(error.GetAwaiter().GetResult().Trim());
            _ = output.GetAwaiter().GetResult();
        }
        catch { if (!process.HasExited) process.Kill(); throw; }
    }

    private void CleanRegistry()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry32 })
            {
                foreach (var key in new[] { @"Software\Roblox", @"Software\ROBLOX Corporation" })
                    DeleteRegistry(hive, view, key);
                // HKCR merges these per-user and machine Classes branches.
                foreach (var name in new[] { "roblox", "roblox-player", "roblox-studio", "Roblox.Player", "Roblox.Place", "Roblox.Model", "Roblox.Protocol" })
                    DeleteRegistry(hive, view, @"Software\Classes\" + name);
                foreach (var extension in new[] { ".rbxl", ".rbxlx", ".rbxm", ".rbxmx" })
                    Step($"Clean Roblox association {hive}/{view}/{extension}", () =>
                    {
                        using var root = RegistryKey.OpenBaseKey(hive, view);
                        var path = @"Software\Classes\" + extension;
                        using (var key = root.OpenSubKey(path))
                        {
                            if (key is null) return;
                            if (key.GetValue("") is not string association || !association.StartsWith("Roblox", StringComparison.OrdinalIgnoreCase))
                            { Log("INFO", $"Preserved {extension}: default association belongs to another application or is unspecified."); return; }
                        }
                        root.DeleteSubKeyTree(path, false);
                    });
                // Remove stale Add/Remove Programs records owned by Roblox.
                Step($"Clean Roblox uninstall records ({hive}/{view})", () =>
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", true);
                    if (uninstall is null) return;
                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        bool owned;
                        using (var key = uninstall.OpenSubKey(name))
                            owned = (key?.GetValue("DisplayName") as string)?.StartsWith("Roblox", StringComparison.OrdinalIgnoreCase) == true &&
                                    (key?.GetValue("Publisher") as string)?.Contains("Roblox", StringComparison.OrdinalIgnoreCase) == true;
                        if (owned) uninstall.DeleteSubKeyTree(name, false);
                    }
                });
            }
    }

    private void DeleteRegistry(RegistryHive hive, RegistryView view, string path) =>
        Step($"Delete registry {hive}/{view}/{path}", () =>
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            root.DeleteSubKeyTree(path, false);
        });
}
