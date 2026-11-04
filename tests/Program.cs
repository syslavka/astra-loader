using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using NLCleaner.Services;

var failures = 0;
var passed = 0;
var scratch = Path.Combine(Path.GetTempPath(), "NL-Cleaner-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
void Check(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
const string marker = "S-1-5-21-1234";
try
{
    Check("Hosts install is idempotent and reverses without touching other entries", () =>
    {
        var original = Encoding.UTF8.GetBytes("# Keep café\r\n127.0.0.1 localhost\r\n192.0.2.1 example.test\r\n");
        var applied = HostsBlocker.Transform(original, marker, true);
        Assert(applied.SequenceEqual(HostsBlocker.Transform(applied, marker, true)), "Duplicate block on repeat.");
        Assert(original.SequenceEqual(HostsBlocker.Transform(applied, marker, false)), "Unrelated bytes changed.");
        var text = Encoding.UTF8.GetString(applied);
        Assert(text.Contains("0.0.0.0 setup.rbxcdn.com") && text.Contains(":: setup.rbxcdn.com"), "IPv4/IPv6 block missing.");
    });
    foreach (var encoding in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.UTF32 })
        Check("Hosts preserves BOM and " + encoding.WebName, () =>
        {
            var original = encoding.GetPreamble().Concat(encoding.GetBytes("# Keep Ω\n127.0.0.1 localhost\n")).ToArray();
            var applied = HostsBlocker.Transform(original, marker, true);
            Assert(original.SequenceEqual(HostsBlocker.Transform(applied, marker, false)), "Encoding or original content changed.");
        });
    Check("Hosts markers from other accounts remain intact", () =>
    {
        var original = Encoding.ASCII.GetBytes("127.0.0.1 localhost\n");
        var other = HostsBlocker.Transform(original, "S-1-5-21-9876", true);
        var mine = HostsBlocker.Transform(other, marker, true);
        Assert(other.SequenceEqual(HostsBlocker.Transform(mine, marker, false)), "Other account's block changed.");
    });
    Check("Malformed hosts blocks fail before writing", () =>
    {
        Throws<IOException>(() => HostsBlocker.Transform(Encoding.ASCII.GetBytes("# BEGIN NL-CLEANER " + marker + "\n"), marker, true));
        Throws<IOException>(() => HostsBlocker.Transform(Encoding.ASCII.GetBytes("# END NL-CLEANER " + marker + "\n"), marker, false));
        Throws<ArgumentException>(() => HostsBlocker.Transform([], "bad\nmarker", true));
    });
    Check("Deletion target allowlist rejects paths outside Roblox roots", () =>
    {
        var paths = new TargetPaths();
        Throws<InvalidOperationException>(() => paths.ValidateTarget(Path.GetPathRoot(scratch)!));
        Throws<InvalidOperationException>(() => paths.ValidateTarget(Path.Combine(paths.Roots[0], "..")));
        Throws<InvalidOperationException>(() => paths.ValidateTarget(Path.Combine(paths.Roots[0], "Versions")));
    });
    Check("Hidden/read-only files are deleted inside a scratch tree", () =>
    {
        var path = Path.Combine(scratch, "tree");
        Directory.CreateDirectory(Path.Combine(path, "nested"));
        var file = Path.Combine(path, "nested", "file.bin");
        File.WriteAllText(file, "test"); File.SetAttributes(file, FileAttributes.Hidden | FileAttributes.ReadOnly);
        FileTree.DeleteTree(path, CancellationToken.None);
        Assert(!Directory.Exists(path), "Tree remains.");
    });
    Check("Cancellation prevents deletion", () =>
    {
        var path = Path.Combine(scratch, "cancel"); Directory.CreateDirectory(path);
        var file = Path.Combine(path, "keep"); File.WriteAllText(file, "keep");
        Throws<OperationCanceledException>(() => FileTree.DeleteTree(path, new CancellationToken(true)));
        Assert(File.Exists(file), "Cancelled deletion removed a file.");
    });
    Check("Reparse points are refused and external content survives", () =>
    {
        var target = Path.Combine(scratch, "external"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        var link = Path.Combine(scratch, "link");
        // A junction requires no developer-mode or symbolic-link privilege.
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null");
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit(); Assert(process.ExitCode == 0, "Junction creation failed.");
        try
        {
            Throws<IOException>(() => FileTree.DeleteTree(link, CancellationToken.None));
            Throws<IOException>(() => FileTree.EnsureNoReparsePoints(Path.Combine(link, "keep.txt")));
            Assert(File.Exists(Path.Combine(target, "keep.txt")), "External file was removed.");
        }
        finally { Directory.Delete(link, false); }
    });
    Check("Native ACL write-deny round trip is recoverable", () =>
    {
        var path = Path.Combine(scratch, "acl");
        var backup = FolderBlocker.Capture(path);
        try
        {
            FolderBlocker.Apply(path);
            var acl = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
            var found = acl.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(rule =>
                rule.IdentityReference.Value == "S-1-1-0" && rule.AccessControlType == AccessControlType.Deny &&
                (rule.FileSystemRights & FolderBlocker.DeniedRights) == FolderBlocker.DeniedRights);
            Assert(found, "Deny ACE is missing.");
            Throws<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(path, "blocked"), "x"));
        }
        finally { FolderBlocker.Restore(backup); }
        Assert(!Directory.Exists(path), "Placeholder was not removed.");
    });
    Check("Windows Firewall COM policy can be inspected without modification", () =>
    {
        using var firewall = new FirewallBlocker();
        try { firewall.CheckPolicy(); }
        catch (InvalidOperationException ex) { Console.WriteLine("  Policy status: " + ex.Message); }
    });
    Check("Dry-run engine completes all stages without initializing the recovery store", () =>
    {
        var events = new List<Activity>(); var stages = new List<string>();
        var result = new CleanerEngine(true, events.Add, (_, state) => stages.Add(state)).Run(false, CancellationToken.None);
        Assert(!result.Cancelled && result.DryRun, "Preview failed.");
        Assert(stages.Count(s => s == "Previewed" || s == "Review log") == 3, "Preview did not visit every stage.");
        Assert(events.Any(e => e.Level == "PLAN" && e.Message.Contains("Lock empty")), "No ACL plan.");
        Assert(events.Any(e => e.Level == "PLAN" && e.Message.Contains("Firewall OUT")), "No outbound firewall plan.");
        Assert(!events.Any(e => e.Level == "OK"), "Preview claims a real action.");
    });
}
finally
{
    // This absolute scratch path is created above and cannot be a system Roblox target.
    if (!Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new Exception("Scratch path escaped temp.");
    FileTree.DeleteTree(scratch, CancellationToken.None);
}
Console.WriteLine($"{passed} passed; {failures} failed.");
return failures == 0 ? 0 : 1;
