using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 15, #344's "\Device\HarddiskVolumeN -&gt; drive letter" resolver, pulled out into its own
/// small shared class so round 18's #370 unified storage event timeline can reuse the exact same
/// QueryDosDeviceW-based lookup instead of duplicating it a second time (both classes were doing
/// this identically, per #370's own brief: "reuse/extend that rather than adding a second one").
/// NtfsCorruptionEventService now calls into this rather than owning a private copy.
/// </summary>
public static class DevicePathResolver
{
    private static readonly Regex HarddiskVolumeRegex = new(@"\\Device\\HarddiskVolume(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Maps every fixed drive's letter to its NT device path (e.g. "C:" ->
    /// "\Device\HarddiskVolume3") via QueryDosDeviceW - the standard, minimal Win32 call for this;
    /// there's no WMI class that exposes a volume's raw NT device path directly, so this is one of
    /// the few raw-interop cases in this app (alongside CpuTopologyService/the PEB walk/the
    /// handle-table walk), reserved for exactly that "no tool or WMI class available" situation per
    /// CLAUDE.md. Best-effort: a drive this can't resolve just doesn't appear in the map, so callers
    /// fall back to "Unknown volume" rather than guessing.</summary>
    public static Dictionary<string, string> BuildDeviceToLetterMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed) continue;
                string letter = drive.Name.TrimEnd('\\', ':');
                var buffer = new StringBuilder(260);
                uint len = QueryDosDeviceW($"{letter}:", buffer, (uint)buffer.Capacity);
                if (len > 0) map[buffer.ToString()] = letter;
            }
        }
        catch { /* best-effort - an empty map just means every event shows "Unknown volume" */ }
        return map;
    }

    /// <summary>Finds the first "\Device\HarddiskVolumeN" reference embedded in an event's formatted
    /// message and resolves it against <paramref name="deviceToLetter"/> - "Unknown volume" when the
    /// message has no such reference, or the reference doesn't resolve to a currently-mounted fixed
    /// drive.</summary>
    public static string ResolveVolumeFromMessage(string message, Dictionary<string, string> deviceToLetter)
    {
        var match = HarddiskVolumeRegex.Match(message);
        if (!match.Success) return "Unknown volume";
        string devicePath = $@"\Device\HarddiskVolume{match.Groups[1].Value}";
        return deviceToLetter.TryGetValue(devicePath, out var letter) ? $"{letter}:" : "Unknown volume";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string lpDeviceName, StringBuilder lpTargetPath, uint ucchMax);
}
