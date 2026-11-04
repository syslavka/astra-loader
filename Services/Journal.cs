using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace NLCleaner.Services;

public sealed record FolderBackup(string Path, string DaclSddl);
public sealed class BlockState
{
    public int Version { get; set; } = 1;
    public List<FolderBackup> Folders { get; set; } = [];
    public List<string> FirewallRules { get; set; } = [];
    public bool HostsPendingOrApplied { get; set; }
}

public sealed class Journal
{
    public static string UserId => WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Missing user SID.");
    public string DirectoryPath { get; }
    private string StatePath => Path.Combine(DirectoryPath, "blocks.json");
    public BlockState State { get; private set; } = new();
    public Journal(bool initialize)
    {
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NL-Cleaner");
        DirectoryPath = Path.Combine(parent, UserId);
        if (!initialize) return;
        FileTree.EnsureNoReparsePoints(DirectoryPath);
        ProtectDirectory(parent);
        ProtectDirectory(DirectoryPath);
        if (File.Exists(StatePath))
        {
            FileTree.EnsureNoReparsePoints(StatePath);
            State = JsonSerializer.Deserialize<BlockState>(File.ReadAllText(StatePath)) ?? throw new IOException("Empty recovery journal.");
            if (State.Version != 1 || State.Folders is null || State.FirewallRules is null)
                throw new IOException("Unsupported recovery journal. Existing blocks were left intact.");
        }
    }

    private static void ProtectDirectory(string path)
    {
        FileTree.EnsureNoReparsePoints(path);
        Directory.CreateDirectory(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    public void Save()
    {
        FileTree.EnsureNoReparsePoints(StatePath);
        var temp = Path.Combine(DirectoryPath, $"{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(file, State, new JsonSerializerOptions { WriteIndented = true }); file.Flush(true); }
            File.Move(temp, StatePath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
