using System.IO;

namespace NLCleaner.Services;

public sealed class TargetPaths
{
    public IReadOnlyList<string> Roots { get; }
    public string HostsFile { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");

    public TargetPaths()
    {
        Roots = new[] {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(p => !string.IsNullOrWhiteSpace(p))
         .Select(p => Path.GetFullPath(Path.Combine(p, "Roblox")))
         .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void ValidateTarget(string path)
    {
        if (!Roots.Contains(Path.GetFullPath(path), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path is outside the Roblox target list: {path}");
        FileTree.EnsureNoReparsePoints(path);
    }
}

public static class FileTree
{
    // Inspect every existing ancestor. Never walk a junction or symbolic link.
    public static void EnsureNoReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Reparse point refused: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static void DeleteTree(string path, CancellationToken token)
    {
        EnsureNoReparsePoints(path);
        if (!Directory.Exists(path)) return;
        DeleteContents(path, token);
        File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        Directory.Delete(path, false);
    }

    private static void DeleteContents(string path, CancellationToken token)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            token.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Reparse point refused: {entry}");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                EnsureNoReparsePoints(entry);
                DeleteContents(entry, token);
                File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                Directory.Delete(entry, false);
            }
            else
            {
                File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                File.Delete(entry);
            }
        }
    }

    public static IEnumerable<string> FindExecutables(string root, Action<string> warning)
    {
        EnsureNoReparsePoints(root);
        if (!Directory.Exists(root)) yield break;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] entries;
            try { EnsureNoReparsePoints(directory); entries = Directory.GetFileSystemEntries(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warning($"Discovery: {ex.Message}"); continue; }
            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { warning($"Discovery: {ex.Message}"); continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) { warning($"Skipped link: {entry}"); continue; }
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else if (IsRobloxExecutable(entry)) yield return entry;
            }
        }
    }

    public static bool IsRobloxExecutable(string path) =>
        Path.GetFileName(path).StartsWith("Roblox", StringComparison.OrdinalIgnoreCase) &&
        Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);
}
