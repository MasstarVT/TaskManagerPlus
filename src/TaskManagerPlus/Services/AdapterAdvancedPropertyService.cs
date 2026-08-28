using Microsoft.Win32;

namespace TaskManagerPlus.Services;

/// <summary>One `*`-prefixed NDIS advanced-property keyword (#549) - the Device Manager "Advanced"
/// tab nobody can find. <see cref="FriendlyName"/> prefers the driver's own registered
/// <c>ParamDesc</c> string (the exact text Device Manager's Advanced tab shows) and falls back to a
/// small hardcoded map of the common keywords #549 explicitly calls out, then to the raw keyword
/// itself when neither is known - never a guess. <see cref="DisplayValue"/> is the driver's own
/// registered enum label for <see cref="RawValue"/> when one exists (again, exactly what Device
/// Manager's dropdown would show); <see cref="ValueText"/> is whichever of the two is actually
/// worth displaying.</summary>
public sealed record AdapterAdvancedProperty(string Keyword, string FriendlyName, string RawValue, string? DisplayValue)
{
    public string ValueText => string.IsNullOrEmpty(DisplayValue) ? RawValue : DisplayValue;
}

/// <summary>One #550 known-problem flag - deliberately worded as "worth a look, not a verdict", the
/// same framing this app's other pattern-matched heuristics (outdated-driver, thermal-throttle, ...)
/// already use.</summary>
public sealed record AdapterProblemFlag(string Title, string Detail);

/// <summary>
/// Items #549 (advanced-property viewer), #550 (known-problem flags on top of it), and the
/// registry-reading half of #552 (offload keywords) and #553 (configured Speed &amp; Duplex) - all
/// four read the same per-adapter registry surface, so they're grouped in one file the way
/// DhcpAddressingService groups #527/#528/#529/#534 off one shared WMI sweep.
///
/// There is no tool or WMI class that exposes a NIC's advanced properties (they're Device Manager
/// Advanced-tab-only) - per CLAUDE.md's "prefer a known tool/API, reserve raw reads for when nothing
/// else exists" convention, this reads the one place they actually live: the `*`-prefixed value
/// names under the adapter's own numbered subkey of the Net device class registry key, the same
/// class key and NetCfgInstanceId-matching technique WifiPowerSavingService already established for
/// its own single keyword (#545). Every read is wrapped to degrade to an empty list/null/the raw
/// keyword name rather than guess, per CLAUDE.md's "degrade to Unknown - never fabricate" rule -
/// a keyword absent from a given driver just doesn't appear, not "0"/"Unknown" standing in for it.
/// </summary>
public static class AdapterAdvancedPropertyService
{
    private const string NetworkClassKeyPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    // #549's explicitly-named examples, plus the handful #552/#553 need - shown with a real friendly
    // name even on a driver that doesn't register its own ParamDesc string (some third-party/OEM
    // drivers don't). Anything not in this map still shows (with the raw keyword as its name) rather
    // than being hidden - #549 asks for every `*`-prefixed value, not just these.
    private static readonly Dictionary<string, string> FriendlyNameFallback = new(StringComparer.OrdinalIgnoreCase)
    {
        ["*SpeedDuplex"] = "Speed & Duplex",
        ["*FlowControl"] = "Flow Control",
        ["*InterruptModeration"] = "Interrupt Moderation",
        ["*JumboPacket"] = "Jumbo Packet",
        ["*RSS"] = "Receive Side Scaling (RSS)",
        ["*RssBaseProcNumber"] = "RSS Base Processor",
        ["*NumRssQueues"] = "RSS Queues",
        ["*ReceiveBuffers"] = "Receive Buffers",
        ["*TransmitBuffers"] = "Transmit Buffers",
        ["*EEE"] = "Energy Efficient Ethernet",
        ["*GreenEthernet"] = "Green Ethernet",
        ["*PowerSavingMode"] = "Power Saving Mode",
        ["*SelectiveSuspend"] = "Selective Suspend",
        ["*UlpMode"] = "Ultra Low Power Mode",
        ["*LsoV2IPv4"] = "Large Send Offload v2 (IPv4)",
        ["*LsoV2IPv6"] = "Large Send Offload v2 (IPv6)",
        ["*RscIPv4"] = "Receive Segment Coalescing (IPv4)",
        ["*RscIPv6"] = "Receive Segment Coalescing (IPv6)",
        ["*IPChecksumOffloadIPv4"] = "IPv4 Checksum Offload",
        ["*TCPChecksumOffloadIPv4"] = "TCP Checksum Offload (IPv4)",
        ["*TCPChecksumOffloadIPv6"] = "TCP Checksum Offload (IPv6)",
        ["*UDPChecksumOffloadIPv4"] = "UDP Checksum Offload (IPv4)",
        ["*UDPChecksumOffloadIPv6"] = "UDP Checksum Offload (IPv6)",
        ["*WakeOnMagicPacket"] = "Wake on Magic Packet",
        ["*WakeOnPattern"] = "Wake on Pattern Match",
        ["*PMARPOffload"] = "ARP Offload (power management)",
        ["*PMNSOffload"] = "NS Offload (power management)",
    };

    // #552: the offload-related subset of #549's full property list.
    private static readonly string[] OffloadKeywordHints = { "Lso", "Rsc", "ChecksumOffload", "RSS" };

    /// <summary>#549: every `*`-prefixed advanced property this adapter's driver registers, with a
    /// friendly name/value where the driver (or the fallback map above) provides one. Empty when the
    /// adapter can't be matched in the registry, the class key is unreadable, or the driver
    /// registers no NDIS keywords at all.</summary>
    public static List<AdapterAdvancedProperty> ReadAll(string? adapterId)
    {
        var result = new List<AdapterAdvancedProperty>();
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(NetworkClassKeyPath);
            if (classKey is null) return result;
            using var instanceKey = OpenAdapterInstanceKey(classKey, adapterId);
            if (instanceKey is null) return result;

            foreach (var name in instanceKey.GetValueNames())
            {
                if (!name.StartsWith('*')) continue;
                string raw = instanceKey.GetValue(name)?.ToString()?.Trim() ?? string.Empty;
                if (raw.Length == 0) continue;

                var (friendly, display) = DescribeKeyword(instanceKey, name, raw);
                result.Add(new AdapterAdvancedProperty(name, friendly, raw, display));
            }
        }
        catch
        {
            // Access denied or an unexpected class-key layout - degrade to "nothing to show" rather
            // than a partial/misleading list.
        }
        return result.OrderBy(p => p.FriendlyName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Reads one specific registry value under the adapter's instance key, star-prefixed
    /// or not (PnPCapabilities, used by #551, has no star) - the general-purpose building block
    /// ReadAll's `*`-only enumeration above doesn't cover.</summary>
    public static string? ReadRawValue(string? adapterId, string valueName)
    {
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(NetworkClassKeyPath);
            if (classKey is null) return null;
            using var instanceKey = OpenAdapterInstanceKey(classKey, adapterId);
            return instanceKey?.GetValue(valueName)?.ToString()?.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>#552: LSO/RSC/checksum-offload/RSS keywords out of an already-read #549 list -
    /// avoids a second registry sweep for the Adapter health card's offload section.</summary>
    public static List<AdapterAdvancedProperty> FilterOffloadRelated(IEnumerable<AdapterAdvancedProperty> all)
        => all.Where(p => OffloadKeywordHints.Any(hint => p.Keyword.Contains(hint, StringComparison.OrdinalIgnoreCase))).ToList();

    /// <summary>#553: the configured Speed &amp; Duplex keyword specifically, translated the same
    /// friendly way as every other property - null when the driver doesn't register one (some
    /// adapters, especially Wi-Fi, don't - there's nothing to force-duplex on a radio).</summary>
    public static AdapterAdvancedProperty? FindSpeedDuplex(IEnumerable<AdapterAdvancedProperty> all)
        => all.FirstOrDefault(p => p.Keyword.Equals("*SpeedDuplex", StringComparison.OrdinalIgnoreCase));

    /// <summary>#550: flags the handful of advanced settings that commonly cause real, reportable
    /// symptoms - deliberately labelled "worth a look, not a verdict" everywhere it's shown, the
    /// same caveat CLAUDE.md documents for this app's other pattern-matched heuristics. Selective
    /// Suspend (USB adapters only) isn't an NDIS keyword at all - see the isUsbSelectiveSuspendOn
    /// parameter's remarks at the call site (NetworkViewModel), which sources it from the existing
    /// #92 UsbPowerService instead.</summary>
    public static List<AdapterProblemFlag> DetectKnownProblems(IReadOnlyList<AdapterAdvancedProperty> properties, bool? usbSelectiveSuspendOn)
    {
        var flags = new List<AdapterProblemFlag>();

        var eee = properties.FirstOrDefault(p => p.Keyword.Equals("*EEE", StringComparison.OrdinalIgnoreCase))
            ?? properties.FirstOrDefault(p => p.Keyword.Equals("*GreenEthernet", StringComparison.OrdinalIgnoreCase));
        if (eee is not null && LooksOn(eee))
            flags.Add(new AdapterProblemFlag("Energy Efficient Ethernet / Green Ethernet is on",
                "Can cause brief link renegotiation (\"link flaps\") whenever traffic goes idle. Worth a look, not a verdict - only worth disabling if you're actually seeing drops."));

        var ulp = properties.FirstOrDefault(p => p.Keyword.Equals("*UlpMode", StringComparison.OrdinalIgnoreCase));
        if (ulp is not null && LooksOn(ulp))
            flags.Add(new AdapterProblemFlag("Ultra Low Power Mode is on",
                "Trades link stability for battery life on some chipsets. Worth a look, not a verdict."));

        if (usbSelectiveSuspendOn == true)
            flags.Add(new AdapterProblemFlag("USB Selective Suspend is on for this adapter",
                "Windows can suspend a USB NIC between bursts of traffic, which shows up as intermittent drops. Worth a look, not a verdict."));

        var flowControl = properties.FirstOrDefault(p => p.Keyword.Equals("*FlowControl", StringComparison.OrdinalIgnoreCase));
        if (flowControl is not null && LooksOff(flowControl))
            flags.Add(new AdapterProblemFlag("Flow Control is disabled",
                "Can cause packet loss under sustained load on a saturated link. Worth a look, not a verdict."));

        return flags;
    }

    private static bool LooksOn(AdapterAdvancedProperty p)
    {
        string text = p.ValueText;
        if (text == "1") return true;
        if (text == "0") return false;
        return text.Contains("enable", StringComparison.OrdinalIgnoreCase) && !text.Contains("disable", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksOff(AdapterAdvancedProperty p)
    {
        string text = p.ValueText;
        if (text == "0") return true;
        return text.Contains("disable", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Same NetCfgInstanceId-matching walk WifiPowerSavingService.Read already establishes
    /// for a single keyword, generalized to hand back the whole instance key so callers can read as
    /// many values off it as they need without repeating the walk.</summary>
    private static RegistryKey? OpenAdapterInstanceKey(RegistryKey classKey, string? adapterId)
    {
        if (string.IsNullOrEmpty(adapterId)) return null;
        string wantedGuid = adapterId.Trim('{', '}');

        foreach (var subKeyName in classKey.GetSubKeyNames())
        {
            // Adapter instance subkeys are always 4-digit numeric ("0000", "0001", ...) - the class
            // key also has non-numeric siblings ("Properties", etc.) to skip.
            if (subKeyName.Length == 0 || !subKeyName.All(char.IsDigit)) continue;

            var subKey = classKey.OpenSubKey(subKeyName);
            if (subKey is null) continue;

            if (subKey.GetValue("NetCfgInstanceId") is string instanceId &&
                string.Equals(instanceId.Trim('{', '}'), wantedGuid, StringComparison.OrdinalIgnoreCase))
                return subKey;

            subKey.Dispose();
        }
        return null;
    }

    /// <summary>Prefers the driver's own registered <c>Ndi\params\&lt;keyword&gt;</c> metadata
    /// (ParamDesc for the friendly name, the <c>enum</c> subkey for the raw value's display label) -
    /// exactly what Device Manager's own Advanced tab reads - and falls back to the hardcoded map
    /// (then the raw keyword) when a driver doesn't register it. Never throws past this method: a
    /// missing/unexpected metadata shape just means the fallback wins, not an exception the caller
    /// has to guard against.</summary>
    private static (string Friendly, string? Display) DescribeKeyword(RegistryKey instanceKey, string keyword, string rawValue)
    {
        string friendly = FriendlyNameFallback.TryGetValue(keyword, out var mapped) ? mapped : keyword.TrimStart('*');
        string? display = null;
        try
        {
            using var paramsKey = instanceKey.OpenSubKey($@"Ndi\params\{keyword}");
            if (paramsKey is not null)
            {
                if (paramsKey.GetValue("ParamDesc") is string desc && !string.IsNullOrWhiteSpace(desc))
                    friendly = desc.Trim();

                using var enumKey = paramsKey.OpenSubKey("enum");
                if (enumKey?.GetValue(rawValue) is string label && !string.IsNullOrWhiteSpace(label))
                    display = label.Trim();
            }
        }
        catch
        {
            // Best-effort - fall back to whatever friendly/display was already resolved above.
        }
        return (friendly, display);
    }
}
