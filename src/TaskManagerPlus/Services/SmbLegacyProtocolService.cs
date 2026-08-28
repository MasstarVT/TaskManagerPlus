using Microsoft.Win32;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 19, #884: SMB and legacy protocol posture - SMB1, SMB signing (client/server), insecure
/// guest logon, LM compatibility level, LLMNR, NetBIOS, and mDNS. Every value comes from a plain
/// registry read (the fast fallback the item's own text prefers over shelling
/// Get-WindowsOptionalFeature, which can be slow) - display only, no toggle here, per #884's own
/// framing; each row instead carries the exact registry path/command to change it.
/// </summary>
public static class SmbLegacyProtocolService
{
    public sealed record Row(string Label, string ValueText, string WhyItMatters, string HowToChange);

    public static List<Row> ReadRows()
    {
        var rows = new List<Row>();

        // SMB1 server feature state - registry fallback (Get-WindowsOptionalFeature is the more
        // authoritative source but is slow; the registry value is what LanmanServer itself honors
        // once set, and is None when the feature has never been explicitly toggled either way).
        int? smb1 = ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "SMB1");
        rows.Add(new Row(
            "SMB1 (server)",
            smb1 switch { null => "Not set via registry (modern Windows ships with the SMB1 server feature disabled by default since ~2017 - check Windows Features for certainty)", 0 => "Disabled", _ => "Enabled" },
            "SMB1 has no modern security hardening (no signing-by-default, weak-hash-friendly) and was the protocol WannaCry/EternalBlue exploited - leaving it on is one of the highest-value single things to fix on a home or small-office network.",
            @"Disable via: Turn Windows features on or off > SMB 1.0/CIFS File Sharing Support (or PowerShell: Disable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol)"));

        int? clientReq = ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters", "RequireSecuritySignature");
        int? clientEn = ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters", "EnableSecuritySignature");
        rows.Add(new Row(
            "SMB signing (client)",
            $"Required: {OnOff(clientReq)}, Enabled: {OnOff(clientEn)}",
            "SMB signing stops a network man-in-the-middle from tampering with SMB traffic in transit (a classic relay-attack building block). Windows negotiates it automatically against a domain controller but not always against a plain file server.",
            @"HKLM\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters\RequireSecuritySignature = 1"));

        int? serverReq = ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "RequireSecuritySignature");
        int? serverEn = ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "EnableSecuritySignature");
        rows.Add(new Row(
            "SMB signing (server)",
            $"Required: {OnOff(serverReq)}, Enabled: {OnOff(serverEn)}",
            "Same protection as client-side signing above, but for shares this machine is hosting for others.",
            @"HKLM\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters\RequireSecuritySignature = 1"));

        int? guestAuth = ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters", "AllowInsecureGuestAuth");
        rows.Add(new Row(
            "Insecure guest logon",
            guestAuth switch { null => "Not set (Windows default: disabled since Windows 10 1709/Server 2019)", 0 => "Disabled", _ => "Enabled" },
            "Insecure guest auth lets this machine connect to an SMB share with no real authentication or signing - a legacy compatibility setting for old NAS devices that also removes real protection against a rogue/spoofed server.",
            @"HKLM\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters\AllowInsecureGuestAuth = 0"));

        int? lmLevel = ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Lsa", "LmCompatibilityLevel");
        rows.Add(new Row(
            "LM compatibility level",
            LmLevelText(lmLevel),
            "Controls which legacy NTLM/LM authentication protocol variants this machine will send and accept - lower levels (0-2) allow the weak, crackable LM/NTLMv1 hashes; level 5 is the strongest built-in setting.",
            @"HKLM\SYSTEM\CurrentControlSet\Control\Lsa\LmCompatibilityLevel = 5 (Send NTLMv2 response only, refuse LM & NTLM)"));

        int? llmnr = ReadDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient", "EnableMulticast");
        rows.Add(new Row(
            "LLMNR",
            llmnr == 0 ? "Disabled" : "On (default - this policy value isn't configured; absence means LLMNR is on)",
            "LLMNR (Link-Local Multicast Name Resolution) answers name lookups your DNS server couldn't - a well-known spoofing/credential-relay vector (Responder-style attacks) on any network with an untrusted device present.",
            "PowerShell one-liner: New-ItemProperty -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows NT\\DNSClient' -Name EnableMulticast -PropertyType DWord -Value 0 -Force"));

        var netbios = ReadNetBiosPerAdapter();
        rows.Add(new Row(
            "NetBIOS over TCP/IP (per adapter)",
            netbios.Count == 0 ? "No per-adapter override found (all adapters use the default: enabled if DHCP says so, else enabled)" : string.Join("; ", netbios),
            "NetBIOS name resolution is an older, weaker sibling of LLMNR with the same spoofing/relay exposure - most home/office networks no longer need it.",
            @"HKLM\SYSTEM\CurrentControlSet\Services\NetBT\Parameters\Interfaces\<adapter GUID>\NetbiosOptions = 2 (Disable NetBIOS over TCP/IP)"));

        int? mdns = ReadDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient", "EnableMDNS");
        rows.Add(new Row(
            "mDNS",
            mdns == 0 ? "Disabled" : "On (default - this policy value isn't configured; absence means mDNS is on)",
            "Multicast DNS (mDNS, used by AirPlay/Chromecast/some IoT device discovery) broadcasts this machine's presence on the local network segment - low risk on a trusted home LAN, more relevant on a shared/public network.",
            "PowerShell one-liner: New-ItemProperty -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows NT\\DNSClient' -Name EnableMDNS -PropertyType DWord -Value 0 -Force"));

        return rows;
    }

    private static string OnOff(int? v) => v switch { null => "Not set", 0 => "Off", _ => "On" };

    private static string LmLevelText(int? level) => level switch
    {
        null => "Not set (Windows default behaves like level 3 on current releases - send NTLMv2 only if negotiated)",
        0 => "0 - Send LM & NTLM responses",
        1 => "1 - Send LM & NTLM - use NTLMv2 session security if negotiated",
        2 => "2 - Send NTLM response only",
        3 => "3 - Send NTLMv2 response only",
        4 => "4 - Send NTLMv2 response only, refuse LM",
        5 => "5 - Send NTLMv2 response only, refuse LM & NTLM",
        _ => $"Unknown value ({level})",
    };

    private static List<string> ReadNetBiosPerAdapter()
    {
        var results = new List<string>();
        try
        {
            using var interfacesKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\NetBT\Parameters\Interfaces");
            if (interfacesKey is null) return results;

            foreach (var subKeyName in interfacesKey.GetSubKeyNames())
            {
                using var adapterKey = interfacesKey.OpenSubKey(subKeyName);
                if (adapterKey?.GetValue("NetbiosOptions") is not int option) continue;
                string text = option switch { 0 => "Default (DHCP-controlled)", 1 => "Enabled", 2 => "Disabled", _ => $"Unknown ({option})" };
                results.Add($"{subKeyName}: {text}");
            }
        }
        catch
        {
            // Denied/absent - empty list, the row shows "no per-adapter override found".
        }
        return results;
    }

    private static int? ReadDword(RegistryKey root, string subKey, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue(valueName) as int?;
        }
        catch
        {
            return null;
        }
    }
}
