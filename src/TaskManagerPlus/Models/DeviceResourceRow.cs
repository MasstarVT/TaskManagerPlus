namespace TaskManagerPlus.Models;

public enum DeviceResourceKind
{
    Irq,
    Io,
    Memory,
    Dma,
}

/// <summary>
/// #476: one IRQ/I-O/memory/DMA assignment, read from Win32_PnPAllocatedResource (the
/// association) joined against whichever of Win32_IRQResource/Win32_PortResource/
/// Win32_DeviceMemoryAddress/Win32_DMAChannel it points at for the actual range/number - see
/// ResourceMapService. IsFlagged/FlagText are computed after the full scan
/// (ResourceMapService.FlagConflicts): an IRQ shared by 2+ devices where at least one side is
/// documented non-shareable, or an overlapping I/O port or memory range between two different
/// devices. "Quick flag, not a verdict" per CLAUDE.md applies directly here - a parent bus/bridge
/// controller legitimately containing a child device's range inside its own window looks identical
/// to a real conflict from this data alone, so a flag here is a starting point for "why won't this
/// device start", not a diagnosis.
/// </summary>
public sealed class DeviceResourceRow
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;

    public DeviceResourceKind Kind { get; set; }
    public string KindText => Kind switch
    {
        DeviceResourceKind.Irq => "IRQ",
        DeviceResourceKind.Io => "I/O",
        DeviceResourceKind.Memory => "Memory",
        DeviceResourceKind.Dma => "DMA",
        _ => "?",
    };

    public string RangeText { get; set; } = string.Empty;

    /// <summary>Numeric start/end used for sort order and overlap detection - for IRQ/DMA rows
    /// these are just the channel/IRQ number repeated in both fields (a single point, not a
    /// range).</summary>
    public ulong RangeStart { get; set; }
    public ulong RangeEnd { get; set; }

    /// <summary>Only meaningful for Kind == Irq - Win32_IRQResource is the only one of the four
    /// resource classes that reports a Shareable flag at all; null when WMI didn't report it.</summary>
    public bool? Shareable { get; set; }
    public string ShareableText => Shareable switch { true => "Shareable", false => "Exclusive", _ => "Unknown" };

    /// <summary>Set only for Kind == Irq (as an int, for easy grouping) - lets #477's interrupt-
    /// mode view cross-reference which legacy line a device is actually on without re-parsing.</summary>
    public int? IrqNumber { get; set; }

    /// <summary>True when this device also reports Device Manager problem code 12 ("insufficient
    /// resources") - filled in by the ViewModel from the already-loaded device tree, not computed
    /// here (this class has no device-tree dependency of its own).</summary>
    public bool HasInsufficientResourcesProblem { get; set; }

    public bool IsFlagged { get; set; }
    public string FlagText { get; set; } = string.Empty;
}
