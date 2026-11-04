using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace NLCleaner.Services;

public static class HostsBlocker
{
    public static readonly string[] Domains = [
        "roblox.com", "www.roblox.com", "setup.roblox.com", "setup.rbxcdn.com",
        "setup-aws.rbxcdn.com", "setup-ak.rbxcdn.com", "setup-hw.rbxcdn.com",
        "clientsettings.roblox.com", "clientsettingscdn.roblox.com", "versioncompatibility.api.roblox.com",
        "assetdelivery.roblox.com", "apis.roblox.com"
    ];

    // Byte-preserving for UTF-8/ANSI hosts files; explicit BOM support for UTF-16/32.
    public static byte[] Transform(byte[] input, string token, bool install)
    {
        if (token.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))) throw new ArgumentException("Invalid hosts marker.");
        Encoding encoding = Encoding.Latin1;
        var bomLength = 0;
        if (input.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) { encoding = new UTF32Encoding(false, false); bomLength = 4; }
        else if (input.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) { encoding = new UTF32Encoding(true, false); bomLength = 4; }
        else if (input.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = Encoding.Unicode; bomLength = 2; }
        else if (input.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = Encoding.BigEndianUnicode; bomLength = 2; }
        else if (input.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) bomLength = 3;
        var content = encoding.GetString(input, bomLength, input.Length - bomLength);
        var begin = $"# BEGIN NL-CLEANER {token}";
        var end = $"# END NL-CLEANER {token}";
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var result = new StringBuilder();
        var inside = false;
        var seen = false;
        // Keep each original line's terminator, including a missing final newline.
        for (var start = 0; start < content.Length;)
        {
            var index = content.IndexOf('\n', start);
            var length = index < 0 ? content.Length - start : index - start + 1;
            var line = content.Substring(start, length);
            var text = line.TrimEnd('\r', '\n');
            if (text == begin)
            { if (inside || seen) throw new IOException("Duplicate hosts marker."); inside = true; seen = true; }
            else if (text == end)
            { if (!inside) throw new IOException("Unmatched hosts marker."); inside = false; }
            else if (!inside) result.Append(line);
            start += length;
        }
        if (inside) throw new IOException("Unterminated hosts marker; file left intact.");
        if (install)
        {
            if (result.Length > 0 && result[^1] != '\n') result.Append(newline);
            result.Append(begin).Append(newline);
            foreach (var domain in Domains)
                result.Append("0.0.0.0 ").Append(domain).Append(newline).Append(":: ").Append(domain).Append(newline);
            result.Append(end).Append(newline);
        }
        return input.AsSpan(0, bomLength).ToArray().Concat(encoding.GetBytes(result.ToString())).ToArray();
    }

    public static void Update(string path, string token, bool install, string backupDirectory)
    {
        FileTree.EnsureNoReparsePoints(path);
        // Opening the existing file preserves its ACL. A deny-share handle excludes concurrent writers.
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        using var buffer = new MemoryStream();
        file.CopyTo(buffer);
        var original = buffer.ToArray();
        var updated = Transform(original, token, install);
        if (original.AsSpan().SequenceEqual(updated)) return;
        var backup = Path.Combine(backupDirectory, $"hosts-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bak");
        File.WriteAllBytes(backup, original);
        try
        {
            file.Position = 0;
            file.Write(updated);
            file.SetLength(updated.Length);
            file.Flush(true);
        }
        catch
        {
            file.Position = 0;
            file.Write(original);
            file.SetLength(original.Length);
            file.Flush(true);
            throw;
        }
    }

    public static void FlushDns()
    {
        if (!DnsFlushResolverCache()) throw new IOException("DNS cache flush failed.");
    }

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DnsFlushResolverCache();
}
