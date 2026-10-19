using System.Globalization;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace TaskManagerPlus.Services;

/// <summary>
/// Proof that a journal entry with an Undo was written by this app. The three undo journals
/// (change-journal.jsonl, registry-changes.json, security-actions.json) live in the user's own
/// settings folder, so any unelevated process running as the same user can edit them - and Undo
/// runs elevated. Without this, a forged entry turns the app into a confused deputy (rewrite system
/// environment values, re-enable services/tasks/startup items/firewall rules, run any inverse).
///
/// Each undoable entry carries an HMAC-SHA256 over a canonical serialization of the fields its undo
/// reads. The key is 32 random bytes in HKLM\SOFTWARE\TaskManagerPlus\JournalIntegrity, a key this
/// app creates owned by Administrators with a protected DACL granting only Administrators and
/// SYSTEM - nothing a standard user can read, so nothing it can sign with. Standard users can't
/// create keys under HKLM\SOFTWARE, so the key can't be squatted ahead of time either; an existing
/// key is still only trusted after its owner and every allow ACE check out.
///
/// The key is created lazily on the first journal write (never on a read, never in read-only
/// mode). Any failure - not elevated, key missing or untrusted, MAC absent or wrong - reads as
/// "unverified", which disables that entry's Undo. Entries written before this existed carry no MAC
/// and are therefore unverified too: they still list, they just can't be undone from the app.
///
/// Residual: this proves authorship, not freshness - a line this app genuinely wrote can be
/// replayed (e.g. an undone entry copied back with Undone=false). That can only repeat an inverse
/// of something this app really did.
/// </summary>
public static class JournalIntegrityService
{
    public const string UnverifiedMessage = "Can't verify this app wrote this entry - undo disabled.";

    private const string ParentKeyPath = @"SOFTWARE\TaskManagerPlus";
    private const string KeyPath = @"SOFTWARE\TaskManagerPlus\JournalIntegrity";
    private const string KeyValueName = "MacKey";
    private const int KeyLength = 32;

    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    private static readonly object KeyLock = new();
    private static byte[]? _key;

    /// <summary>MAC for a new entry, or null when no trusted key is available (the entry is then
    /// written unverified and its Undo stays disabled). Creates the key if it doesn't exist yet,
    /// unless the app is in read-only mode.</summary>
    public static string? Sign(string domain, params string?[] fields)
    {
        var key = GetKey(createIfMissing: !ReadOnlyModeService.IsReadOnly);
        if (key is null) return null;
        return Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Canonicalize(domain, fields))));
    }

    /// <summary>True only when <paramref name="mac"/> is the MAC of these exact fields under the
    /// trusted key. Never creates the key.</summary>
    public static bool Verify(string domain, string? mac, params string?[] fields)
    {
        if (string.IsNullOrEmpty(mac)) return false;
        byte[] expected;
        try { expected = Convert.FromBase64String(mac); }
        catch { return false; }

        var key = GetKey(createIfMissing: false);
        if (key is null) return false;
        var actual = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Canonicalize(domain, fields)));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Unambiguous text form of the fields: a domain tag (so a MAC from one journal
    /// can't be replayed as another's), then each field length-prefixed, with null distinct from
    /// empty (registry Undo treats a null old value as "delete the value").</summary>
    internal static string Canonicalize(string domain, IReadOnlyList<string?> fields)
    {
        var sb = new StringBuilder("TaskManagerPlus journal v1\n").Append(domain).Append('\n');
        foreach (var f in fields)
        {
            if (f is null) sb.Append("-\n");
            else sb.Append(f.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(f).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Canonical text for a timestamp - the UTC instant in ticks, so a value that comes
    /// back from JSON as Local (written with an offset) still matches.</summary>
    internal static string Instant(DateTime value) => value.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);

    internal static string Flag(bool value) => value ? "1" : "0";

    private static byte[]? GetKey(bool createIfMissing)
    {
        lock (KeyLock)
        {
            if (_key is not null) return _key;
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using (var existing = hklm.OpenSubKey(KeyPath, writable: false))
                {
                    if (existing is not null)
                    {
                        if (!IsAdminOnly(existing)) return null;
                        if (existing.GetValue(KeyValueName) is byte[] { Length: KeyLength } stored) return _key = stored;
                        if (!createIfMissing) return null;
                    }
                    else if (!createIfMissing)
                    {
                        return null;
                    }
                }

                // The parent just groups the app's machine-wide keys and inherits HKLM\SOFTWARE's ACL;
                // only the child carries the protected, admin-only descriptor.
                using (hklm.CreateSubKey(ParentKeyPath)) { }
                using var key = hklm.CreateSubKey(KeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree, BuildAdminOnlySecurity());
                // CreateSubKey returns an existing key without applying the descriptor, so check
                // what is actually there before putting a secret in it.
                if (key is null || !IsAdminOnly(key)) return null;
                if (key.GetValue(KeyValueName) is byte[] { Length: KeyLength } raced) return _key = raced;

                key.SetValue(KeyValueName, RandomNumberGenerator.GetBytes(KeyLength), RegistryValueKind.Binary);
                return _key = key.GetValue(KeyValueName) is byte[] { Length: KeyLength } written ? written : null;
            }
            catch
            {
                // Not elevated, access denied, registry unavailable - unverified, never an exception.
                return null;
            }
        }
    }

    private static RegistrySecurity BuildAdminOnlySecurity()
    {
        var security = new RegistrySecurity();
        security.SetOwner(AdministratorsSid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new RegistryAccessRule(AdministratorsSid, RegistryRights.FullControl,
            InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new RegistryAccessRule(LocalSystemSid, RegistryRights.FullControl,
            InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    /// <summary>Owner is Administrators/SYSTEM/TrustedInstaller and no allow ACE (of any kind,
    /// inherited or inherit-only included) names anyone else. Unreadable ACL = untrusted.</summary>
    internal static bool IsAdminOnly(RegistryKey key)
    {
        try { return IsAdminOnly(key.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)); }
        catch { return false; }
    }

    internal static bool IsAdminOnly(RegistrySecurity security)
    {
        try
        {
            if (!IsTrusted(security.GetOwner(typeof(SecurityIdentifier)))) return false;
            foreach (RegistryAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Allow && !IsTrusted(rule.IdentityReference)) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTrusted(IdentityReference? id) =>
        id is SecurityIdentifier sid && (sid == AdministratorsSid || sid == LocalSystemSid || sid.Value == TrustedInstallerSid);
}
