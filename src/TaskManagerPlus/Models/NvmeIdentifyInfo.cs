namespace TaskManagerPlus.Models;

/// <summary>
/// Round 13, #322: the handful of NVMe Identify Controller (log-page-free - CNS=1 Identify) fields
/// worth surfacing on the Storage tab - model/serial/firmware for "which drive is this", namespace
/// count and MDTS for capability, and APST (Autonomous Power State Transition) support/enable
/// state, which is where "my NVMe drive disappears after idle" bugs live.
/// </summary>
public sealed class NvmeIdentifyInfo
{
    public bool Available { get; init; }
    public string UnavailableReason { get; init; } = string.Empty;

    public string ModelNumber { get; init; } = string.Empty;
    public string SerialNumber { get; init; } = string.Empty;
    public string FirmwareRevision { get; init; } = string.Empty;
    public uint NamespaceCount { get; init; }

    /// <summary>Maximum Data Transfer Size as reported (byte 77, units of 2^n * minimum memory
    /// page size) - 0 means "no limit reported" per spec, shown as such rather than "0 bytes".</summary>
    public byte MdtsRaw { get; init; }

    /// <summary>APSTA bit 0 (byte 265) - whether the controller supports autonomous power state
    /// transitions at all, independent of whether it's currently enabled.</summary>
    public bool ApstSupported { get; init; }

    /// <summary>Number of power states the controller reports (NPSS + 1, byte 263).</summary>
    public int PowerStateCount { get; init; }

    /// <summary>Whether the Get Features (APST, feature ID 0x0C) follow-up query succeeded - when
    /// false, ApstEnabled/ApstConfiguredStateCount are unknown, not "false"/"0".</summary>
    public bool ApstFeatureQuerySucceeded { get; init; }
    public bool ApstEnabled { get; init; }

    /// <summary>How many of the reported power states have a non-zero Idle Time Prior to
    /// Transition entry in the APST table - i.e. actually participate in autonomous transitions,
    /// vs. merely being present in PowerStateCount.</summary>
    public int ApstConfiguredStateCount { get; init; }
}
