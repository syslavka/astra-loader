using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NLCleaner.Services;

public static class FolderBlocker
{
    // Deny mutations, retaining READ_CONTROL/WRITE_DAC so an administrator can undo the lock.
    public const FileSystemRights DeniedRights = FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories |
        FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles;

    public static FolderBackup Capture(string path)
    {
        FileTree.EnsureNoReparsePoints(path);
        Directory.CreateDirectory(path);
        if (Directory.EnumerateFileSystemEntries(path).Any()) throw new IOException($"Folder is not empty: {path}");
        var acl = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
        return new FolderBackup(path, acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }

    public static void Apply(string path)
    {
        FileTree.EnsureNoReparsePoints(path);
        if (Directory.EnumerateFileSystemEntries(path).Any()) throw new IOException($"Folder is not empty: {path}");
        var acl = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            DeniedRights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Deny));
        SetDacl(path, acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }

    public static void Restore(FolderBackup backup)
    {
        FileTree.EnsureNoReparsePoints(backup.Path);
        if (!Directory.Exists(backup.Path)) return;
        SetDacl(backup.Path, backup.DaclSddl);
        // Only remove an empty placeholder; never delete files during recovery.
        if (!Directory.EnumerateFileSystemEntries(backup.Path).Any()) Directory.Delete(backup.Path, false);
    }

    private static void SetDacl(string path, string sddl)
    {
        var descriptor = new RawSecurityDescriptor(sddl);
        var dacl = descriptor.DiscretionaryAcl ?? throw new IOException("Refusing a null DACL.");
        var bytes = new byte[dacl.BinaryLength];
        dacl.GetBinaryForm(bytes, 0);
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            const uint daclInfo = 0x4, protectedDacl = 0x80000000, unprotectedDacl = 0x20000000;
            var flags = daclInfo | ((descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0 ? protectedDacl : unprotectedDacl);
            var error = SetNamedSecurityInfoW(path, 1 /* SE_FILE_OBJECT */, flags, IntPtr.Zero, IntPtr.Zero, pinned.AddrOfPinnedObject(), IntPtr.Zero);
            if (error != 0) throw new Win32Exception((int)error);
        }
        finally { pinned.Free(); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint SetNamedSecurityInfoW(string name, int type, uint info, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
}
