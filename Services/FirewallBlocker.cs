using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NLCleaner.Services;

public sealed class FirewallBlocker : IDisposable
{
    private readonly object _policy;
    private readonly object _rules;
    private static string Prefix => $"NL-Cleaner/{Journal.UserId}/";
    private static object CreateCom(string progId) => Activator.CreateInstance(Type.GetTypeFromProgID(progId, true)!)!;
    public FirewallBlocker()
    {
        _policy = CreateCom("HNetCfg.FwPolicy2");
        try { _rules = ((dynamic)_policy).Rules; }
        catch { Marshal.FinalReleaseComObject(_policy); throw; }
    }

    public static string RuleName(string path, bool outbound)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..20];
        return Prefix + hash + (outbound ? "/out" : "/in");
    }

    public void Add(string path, bool outbound)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || !FileTree.IsRobloxExecutable(path))
            throw new ArgumentException("Firewall requires a full Roblox executable path.");
        var ruleObject = CreateCom("HNetCfg.FWRule");
        try
        {
            dynamic rule = ruleObject;
            rule.Name = RuleName(path, outbound);
            rule.Description = "NL-Cleaner Roblox block; remove with Restore access.";
            rule.Grouping = "NL-Cleaner/" + Journal.UserId;
            rule.ApplicationName = path;
            rule.Protocol = 256; // NET_FW_IP_PROTOCOL_ANY
            rule.Direction = outbound ? 2 : 1;
            rule.Action = 0; // NET_FW_ACTION_BLOCK
            rule.Profiles = 0x7FFFFFFF; // ALL
            rule.Enabled = true;
            // INetFwRules.Add replaces an existing rule with the same identifier.
            ((dynamic)_rules).Add(rule);
        }
        finally { Marshal.FinalReleaseComObject(ruleObject); }
    }

    public void Remove(string name)
    {
        if (!name.StartsWith(Prefix, StringComparison.Ordinal)) throw new InvalidOperationException("Foreign firewall rule refused.");
        ((dynamic)_rules).Remove(name); // Removing a nonexistent rule is a no-op.
    }

    public void CheckPolicy()
    {
        dynamic policy = _policy;
        foreach (var profile in new[] { 1, 2, 4 })
            if (!(bool)policy.FirewallEnabled[profile]) throw new InvalidOperationException($"Windows Firewall is disabled for profile {profile}; rules will not enforce there.");
        if ((int)policy.LocalPolicyModifyState != 0)
            throw new InvalidOperationException("Managed firewall policy may override local rules.");
    }

    public void Dispose()
    {
        Marshal.FinalReleaseComObject(_rules);
        Marshal.FinalReleaseComObject(_policy);
    }
}
