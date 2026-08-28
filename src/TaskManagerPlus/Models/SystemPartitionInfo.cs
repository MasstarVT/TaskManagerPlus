using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>
/// #738/#740: one partition on the system disk, read from MSFT_Partition in the
/// root\Microsoft\Windows\Storage WMI namespace (the same namespace StorageSpacesService/
/// SystemSpecsService already query - see SystemPartitionService's remarks). GptType is the raw
/// GPT partition-type GUID Windows itself assigns; the well-known ones (ESP/MSR/Recovery) are
/// matched against the documented constants rather than guessed from the friendly Type text alone,
/// since that text isn't identical across Windows builds/locales.
/// </summary>
public sealed class DiskPartitionInfo
{
    // Documented GPT partition-type GUIDs (Microsoft's own published constants, not guessed) -
    // kept here on the model so both this class and SystemPartitionService (which fills DiskNumber/
    // PartitionNumber/GptType/etc. in from MSFT_Partition) share the one definition.
    public const string EspGptType = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}";
    public const string MsrGptType = "{e3c9e316-0b5c-4db8-817d-f92df00215ae}";
    public const string RecoveryGptType = "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}";
    public const string BasicDataGptType = "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}";

    public int DiskNumber { get; init; }
    public int PartitionNumber { get; init; }
    public char? DriveLetter { get; init; }
    public long SizeBytes { get; init; }
    public long OffsetBytes { get; init; }
    public string GptType { get; init; } = string.Empty;
    public string TypeFriendlyName { get; init; } = string.Empty;
    public bool IsBoot { get; init; }
    public bool IsHidden { get; init; }
    public bool IsActive { get; init; }

    public bool IsEsp => GptType.Equals(EspGptType, StringComparison.OrdinalIgnoreCase);
    public bool IsMsr => GptType.Equals(MsrGptType, StringComparison.OrdinalIgnoreCase);
    public bool IsRecovery => GptType.Equals(RecoveryGptType, StringComparison.OrdinalIgnoreCase)
        || TypeFriendlyName.Contains("Recovery", StringComparison.OrdinalIgnoreCase);
    public bool IsWindowsData => GptType.Equals(BasicDataGptType, StringComparison.OrdinalIgnoreCase) && !IsRecovery;

    /// <summary>Plain-English role label for the partition-layout map (#740) - falls back to
    /// whatever friendly Type text Windows gave when this isn't one of the well-known GPT types
    /// this app specifically recognizes (never fabricated - see CLAUDE.md's degrade convention).</summary>
    public string RoleLabel =>
        IsEsp ? "EFI System Partition" :
        IsMsr ? "Microsoft Reserved" :
        IsRecovery ? "Recovery" :
        IsWindowsData ? (DriveLetter is { } l ? $"Windows ({l}:)" : "Windows data") :
        string.IsNullOrWhiteSpace(TypeFriendlyName) ? "Unknown" : TypeFriendlyName;

    public string SizeText => Formatting.FormatBytes(SizeBytes);
}

/// <summary>#738: everything read from one system-disk partition enumeration pass - shared by the
/// ESP health card (#738), the WinRE status card (#739), and the recovery-partition layout map
/// (#740) so the disk/partition WMI query runs once per Startup tab refresh, not once per
/// feature.</summary>
public sealed class SystemPartitionLayout
{
    public bool Available { get; init; }
    public string? Error { get; init; }
    public int SystemDiskNumber { get; init; } = -1;
    public string DiskFriendlyName { get; init; } = string.Empty;
    public List<DiskPartitionInfo> Partitions { get; init; } = new();

    public DiskPartitionInfo? Esp => Partitions.FirstOrDefault(p => p.IsEsp);
    public DiskPartitionInfo? Recovery => Partitions.FirstOrDefault(p => p.IsRecovery);
}

/// <summary>#738: EFI System Partition free-space health - FreeBytes is measured on demand by
/// temporarily mounting the partition (`mountvol X: /S` ... `mountvol X: /D`, always unmounted in
/// a finally block - see SystemPartitionService.MeasureEspFreeSpaceAsync), since a plain
/// MSFT_Partition read has no free-space figure for an unmounted partition. Flags the classic
/// "100 MB ESP left over from an OEM image, now too full for a feature update" pattern.</summary>
public sealed class EspHealthInfo
{
    public DiskPartitionInfo? Partition { get; init; }
    public long? FreeBytes { get; init; }
    public string? MeasureError { get; init; }

    public double? PercentFree => Partition is { SizeBytes: > 0 } p && FreeBytes is { } f ? (double)f / p.SizeBytes * 100 : null;

    public string FreeText => FreeBytes is { } f
        ? $"{Formatting.FormatBytes(f)}{(PercentFree is { } p ? $" ({p:0.#}% free)" : string.Empty)}"
        : MeasureError ?? "Not measured yet - click \"Measure free space\".";

    // The classic failure pattern this flags: a small (roughly 100-300 MB, the size Windows Setup
    // has used for the ESP for most of the last decade) partition with very little free space.
    public bool IsNearFull => Partition is { SizeBytes: > 0 and <= 400L * 1024 * 1024 } && PercentFree is { } pct && pct < 10;

    public string? NearFullWarning => Partition is null || !IsNearFull ? null
        : $"This EFI System Partition is small ({Partition.SizeText}) and only {PercentFree:0.#}% free - a common cause of Windows feature-update failure 0x800f0922 (\"not enough free space\" during the pre-install disk-space check).";
}

/// <summary>#739: parsed `reagentc /info` output - Windows Recovery Environment status, location,
/// BCD identifier, and recovery-image location. reagentc's text output is the documented, stable
/// way to read this (same "known tool over raw interop" tradeoff as bcdedit/powercfg elsewhere in
/// this app) - field names are read adaptively by their label text rather than a fixed line
/// index, since reagentc's exact wording/order isn't a versioned contract this app controls.</summary>
public sealed class WinReStatusInfo
{
    public bool Available { get; init; }
    public string? Error { get; init; }
    public bool? Enabled { get; init; }
    public string? Location { get; init; }
    public string? BcdIdentifier { get; init; }
    public string? RecoveryImageLocation { get; init; }

    public string StatusText => Enabled switch { true => "Enabled", false => "Disabled", null => "Unknown" };
}

/// <summary>#740: "recovery partition too small for the WinRE servicing update" quick flag - a
/// sub-750 MB recovery partition with under ~250 MB free is the documented pattern behind Windows
/// Update failure 0x80070643 when a WinRE servicing update needs to land. A flag, not a verdict:
/// this app never repartitions - Message points at Microsoft's own documented resize steps
/// (shrink the Windows partition, delete/recreate the recovery partition larger) as guidance text
/// only.</summary>
public sealed class RecoveryPartitionFlag
{
    public bool TooSmallForServicing { get; init; }
    public long? FreeBytes { get; init; }
    public string? MeasureError { get; init; }
    public string Message { get; init; } = string.Empty;
}
