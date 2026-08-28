using System.Numerics;

namespace TaskManagerPlus.Models;

/// <summary>
/// Round 13, #313-#319: the decoded NVMe SMART/Health Information Log (log page 0x02), read via
/// IOCTL_STORAGE_QUERY_PROPERTY / StorageDeviceProtocolSpecificProperty / ProtocolTypeNvme /
/// NVMeDataTypeLogPage - the documented Windows path for NVMe log pages, no vendor driver
/// required. One instance covers the whole 512-byte page; StorageViewModel derives every #314-319
/// display value from this single read rather than re-querying per field.
/// </summary>
public sealed class NvmeHealthLog
{
    public bool Available { get; init; }
    public string UnavailableReason { get; init; } = string.Empty;

    /// <summary>Full 512-byte page as read, kept around for completeness (#313 - "render the full
    /// 512-byte structure") even though every individual field below is also broken out.</summary>
    public byte[] RawBytes { get; init; } = Array.Empty<byte>();

    // #314: byte 0 critical warning bitmask - each bit named below rather than shown as a raw
    // hex mask, since the point of this card is "which specific condition fired".
    public byte CriticalWarningRaw { get; init; }
    public bool SpareBelowThreshold => (CriticalWarningRaw & 0x01) != 0;
    public bool TemperatureExceeded => (CriticalWarningRaw & 0x02) != 0;
    public bool ReliabilityDegraded => (CriticalWarningRaw & 0x04) != 0;
    public bool MediaReadOnly => (CriticalWarningRaw & 0x08) != 0;
    public bool VolatileBackupFailed => (CriticalWarningRaw & 0x10) != 0;
    public bool PmrReadOnly => (CriticalWarningRaw & 0x20) != 0;
    public bool AnyCriticalWarning => CriticalWarningRaw != 0;

    // #318: composite temperature, Kelvin as reported; 0 means "not reported" (never fabricated).
    public ushort CompositeTemperatureKelvin { get; init; }
    public double? CompositeTemperatureC => CompositeTemperatureKelvin == 0 ? null : CompositeTemperatureKelvin - 273.15;

    // #315: not clamped - Percentage Used legitimately exceeds 100 on a drive past rated endurance.
    public byte AvailableSparePercent { get; init; }
    public byte AvailableSpareThresholdPercent { get; init; }
    public byte PercentageUsed { get; init; }
    public bool AvailableSpareBelowOwnThreshold => AvailableSparePercent < AvailableSpareThresholdPercent;

    // #316: 128-bit little-endian counters, decoded via BigInteger rather than truncated to
    // ulong/long - degrading precision here would violate "never fabricate/guess" just as much as
    // dropping a field would. Data units are in units of 1000 * 512 bytes.
    public BigInteger DataUnitsRead { get; init; }
    public BigInteger DataUnitsWritten { get; init; }
    public BigInteger HostReadCommands { get; init; }
    public BigInteger HostWriteCommands { get; init; }

    public double DataUnitsReadTb => (double)(DataUnitsRead * 512000) / 1_000_000_000_000.0;
    public double DataUnitsWrittenTb => (double)(DataUnitsWritten * 512000) / 1_000_000_000_000.0;

    /// <summary>Average bytes per host read command, derived from Data Units Read paired with
    /// Host Read Commands (#316) - null when there have been no read commands to divide by.</summary>
    public double? AverageReadIoBytes => HostReadCommands > 0 ? (double)(DataUnitsRead * 512000) / (double)HostReadCommands : null;
    public double? AverageWriteIoBytes => HostWriteCommands > 0 ? (double)(DataUnitsWritten * 512000) / (double)HostWriteCommands : null;

    // #319
    public BigInteger ControllerBusyTimeMinutes { get; init; }
    public BigInteger PowerCycles { get; init; }
    public BigInteger PowerOnHours { get; init; }
    public BigInteger UnsafeShutdowns { get; init; }

    // #317: the one unambiguous "this drive is losing data" NVMe signal available without a
    // vendor tool - a non-zero media-error count.
    public BigInteger MediaAndDataIntegrityErrors { get; init; }
    public BigInteger ErrorInfoLogEntryCount { get; init; }

    // #318: cumulative throttle-time counters that survive reboots, unlike a live sensor read.
    public uint WarningCompositeTempTimeMinutes { get; init; }
    public uint CriticalCompositeTempTimeMinutes { get; init; }
    public ushort[] TemperatureSensorsKelvin { get; init; } = new ushort[8];
    public uint ThermalMgmtTemp1TransitionCount { get; init; }
    public uint ThermalMgmtTemp2TransitionCount { get; init; }
    public uint ThermalMgmtTemp1TotalTimeSeconds { get; init; }
    public uint ThermalMgmtTemp2TotalTimeSeconds { get; init; }
}
