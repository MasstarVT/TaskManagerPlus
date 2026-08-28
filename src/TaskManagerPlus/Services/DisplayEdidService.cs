using System.Management;
using System.Text;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #685: raw EDID identity per monitor - root\wmi's WmiMonitorRawEEdidV1Block class, keyed by the
/// same InstanceName WmiMonitorConnectionParams uses (see SystemSpecsService.ReadMonitors for how
/// the two are paired). Decodes the base 128-byte EDID block per the VESA E-EDID 1.4 standard -
/// manufacturer ID, product code, serial/model descriptor strings, manufacture week/year, physical
/// size, the preferred detailed timing descriptor (native resolution/refresh), and the
/// supported-features byte. Extension blocks (CTA-861, etc.) aren't parsed - #688's HDR/wide-color
/// state comes from a separate, documented Win32 API (DisplayConfigService) instead of the EDID's
/// own CTA HDR static-metadata data block, which would need a materially more involved CTA-861
/// extension-block parser for comparatively little benefit here.
///
/// Every read/decode step degrades to null (never a guessed value) on a missing WMI namespace, a
/// class this Windows build doesn't expose usable instance data for, or a block that fails its own
/// header sanity check.
/// </summary>
public static class DisplayEdidService
{
    public static Dictionary<string, MonitorEdidInfo> ReadAllByInstance()
    {
        var result = new Dictionary<string, MonitorEdidInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorRawEEdidV1Block");
            foreach (ManagementObject mo in searcher.Get())
            {
                try
                {
                    string instanceName = mo["InstanceName"] as string ?? string.Empty;
                    if (instanceName.Length == 0) continue;

                    byte[]? bytes = ExtractBytes(mo);
                    if (bytes is null) continue;

                    var decoded = Decode(bytes);
                    if (decoded is not null) result[instanceName] = decoded;
                }
                catch
                {
                    // This one monitor's block failed to read/decode - skip it, others may still work.
                }
            }
        }
        catch
        {
            // root\wmi monitor classes unavailable entirely (VM/RDP, locked-down policy, or a
            // provider that doesn't expose instance data on this configuration) - empty result,
            // System Specs just won't show an EDID panel for any monitor.
        }
        return result;
    }

    /// <summary>The raw-byte property's exact name isn't documented consistently across Windows
    /// SDK references, so a few known candidates are tried in order - the first one present and at
    /// least 128 bytes long wins.</summary>
    private static byte[]? ExtractBytes(ManagementBaseObject mo)
    {
        foreach (var propertyName in new[] { "Content", "WmiEDID", "RawEEdidRawData" })
        {
            try
            {
                if (mo[propertyName] is byte[] bytes && bytes.Length >= 128) return bytes;
            }
            catch
            {
                // Property not present on this class version - try the next candidate.
            }
        }
        return null;
    }

    /// <summary>Decodes the base 128-byte EDID block. Returns null when the block doesn't start
    /// with EDID's fixed 8-byte header magic (00 FF FF FF FF FF FF 00) - the one sanity check
    /// cheap enough to always run before trusting the rest of a byte layout this app doesn't
    /// otherwise validate.</summary>
    public static MonitorEdidInfo? Decode(byte[] edid)
    {
        if (edid.Length < 128) return null;
        byte[] header = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };
        for (int i = 0; i < 8; i++)
            if (edid[i] != header[i]) return null;

        // Bytes 8-9: manufacturer ID, three 5-bit letters (1=A .. 26=Z), big-endian, top bit 0.
        int mfgWord = (edid[8] << 8) | edid[9];
        char c1 = (char)('A' + ((mfgWord >> 10) & 0x1F) - 1);
        char c2 = (char)('A' + ((mfgWord >> 5) & 0x1F) - 1);
        char c3 = (char)('A' + (mfgWord & 0x1F) - 1);
        string manufacturerId = new(new[] { c1, c2, c3 });

        int productCode = edid[10] | (edid[11] << 8);

        int week = edid[16];
        int year = edid[17] + 1990;

        double widthCm = edid[21];
        double heightCm = edid[22];

        byte featureByte = edid[24];
        var features = new List<string>();
        if ((featureByte & 0x80) != 0) features.Add("standby");
        if ((featureByte & 0x40) != 0) features.Add("suspend");
        if ((featureByte & 0x20) != 0) features.Add("active-off");
        if ((featureByte & 0x04) != 0) features.Add("sRGB default");
        if ((featureByte & 0x02) != 0) features.Add("preferred timing is native");

        string modelName = string.Empty;
        string serialFromDescriptor = string.Empty;
        int nativeWidth = 0, nativeHeight = 0;
        double nativeRefresh = 0;

        // Four 18-byte descriptor blocks starting at offset 54 - each is either a Detailed Timing
        // Descriptor (non-zero pixel clock) or a display-descriptor block (zero pixel clock, a tag
        // byte identifying what it holds: 0xFC = monitor name, 0xFF = serial number, ...). The
        // first Detailed Timing Descriptor encountered is the EDID's own "preferred timing" per
        // the VESA spec.
        for (int offset = 54; offset <= 108; offset += 18)
        {
            if (offset + 18 > edid.Length) break;
            bool isDescriptorBlock = edid[offset] == 0 && edid[offset + 1] == 0 && edid[offset + 2] == 0;
            if (isDescriptorBlock)
            {
                byte tag = edid[offset + 3];
                string text = DecodeDescriptorText(edid, offset);
                if (tag == 0xFC && modelName.Length == 0) modelName = text;
                else if (tag == 0xFF && serialFromDescriptor.Length == 0) serialFromDescriptor = text;
            }
            else if (nativeWidth == 0)
            {
                var timing = DecodeDetailedTiming(edid, offset);
                if (timing is { } t) (nativeWidth, nativeHeight, nativeRefresh) = t;
            }
        }

        string serial = serialFromDescriptor;
        if (serial.Length == 0)
        {
            uint serialNumeric = (uint)(edid[12] | (edid[13] << 8) | (edid[14] << 16) | (edid[15] << 24));
            if (serialNumeric != 0) serial = serialNumeric.ToString();
        }

        return new MonitorEdidInfo
        {
            ManufacturerId = manufacturerId,
            ProductCode = productCode,
            ModelName = modelName,
            SerialNumber = serial,
            ManufactureWeek = week,
            ManufactureYear = year,
            PhysicalWidthCm = widthCm,
            PhysicalHeightCm = heightCm,
            NativeWidthPx = nativeWidth,
            NativeHeightPx = nativeHeight,
            NativeRefreshHz = nativeRefresh,
            FeatureBitsSummary = features.Count > 0 ? string.Join(", ", features) : "None reported",
        };
    }

    /// <summary>Bytes 5-17 (13 bytes) of a display-descriptor block, ASCII, terminated by 0x0A
    /// (LF) with 0x20 padding after it - the standard EDID string-descriptor encoding.</summary>
    private static string DecodeDescriptorText(byte[] edid, int offset)
    {
        var sb = new StringBuilder();
        for (int i = offset + 5; i < offset + 18 && i < edid.Length; i++)
        {
            byte b = edid[i];
            if (b == 0x0A) break;
            if (b is >= 0x20 and < 0x7F) sb.Append((char)b);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Decodes an 18-byte Detailed Timing Descriptor's active resolution and computed
    /// refresh rate (pixel clock / (H total x V total)) - the standard VESA formula. Returns null
    /// for a descriptor whose fields don't add up to a sane, non-zero timing (guards against
    /// mis-detecting malformed EDID data as a real timing).</summary>
    private static (int Width, int Height, double RefreshHz)? DecodeDetailedTiming(byte[] edid, int offset)
    {
        int pixelClockKhz = ((edid[offset] | (edid[offset + 1] << 8))) * 10;
        if (pixelClockKhz <= 0) return null;

        int hActive = edid[offset + 2] | ((edid[offset + 4] & 0xF0) << 4);
        int hBlank = edid[offset + 3] | ((edid[offset + 4] & 0x0F) << 8);
        int vActive = edid[offset + 5] | ((edid[offset + 7] & 0xF0) << 4);
        int vBlank = edid[offset + 6] | ((edid[offset + 7] & 0x0F) << 8);

        int hTotal = hActive + hBlank;
        int vTotal = vActive + vBlank;
        if (hActive <= 0 || vActive <= 0 || hTotal <= 0 || vTotal <= 0) return null;

        double refreshHz = pixelClockKhz * 1000.0 / (hTotal * vTotal);
        if (refreshHz is <= 0 or > 500) return null; // sanity bound

        return (hActive, vActive, Math.Round(refreshHz, 1));
    }
}
