namespace TaskManagerPlus.Models;

/// <summary>#682/#684: one GPU or NVMe PCI Express device's negotiated vs. maximum link speed/width,
/// from `Get-PnpDeviceProperty` (shelled to PowerShell, per the project's prefer-a-known-tool
/// convention - see PciLinkService). CurrentLinkSpeed/MaxLinkSpeed are the documented
/// DEVPKEY_PciDevice_*LinkSpeed generation codes (1 = Gen1/2.5GT/s ... 5 = Gen5/32GT/s), not raw
/// GT/s figures - GenText below converts them to the familiar "Gen4"/"Gen5" labels. Every field is
/// nullable and null means the property wasn't present/readable for this device (an older driver,
/// or a device that simply isn't a discrete PCIe endpoint) - degrades to "Unknown", never a guess.</summary>
public sealed class PciLinkInfo
{
    public string InstanceId { get; init; } = string.Empty;
    public string Name { get; init; } = "Unknown device";

    /// <summary>"GPU" or "NVMe" - which half of #682's "GPU and Storage tabs" note this row is for.</summary>
    public string Kind { get; init; } = string.Empty;

    public int? CurrentLinkGen { get; init; }
    public int? CurrentLinkWidth { get; init; }
    public int? MaxLinkGen { get; init; }
    public int? MaxLinkWidth { get; init; }

    /// <summary>#684: true when this device's instance path was found under a Thunderbolt
    /// controller ancestor (walked via DEVPKEY_Device_Parent, capped at a few hops) - see
    /// PciLinkService.ReadAllAsync for exactly how this walk works and its limits.</summary>
    public bool IsThunderboltAttached { get; init; }

    public string? EnclosureName { get; init; }

    public string GenText(int? gen) => gen switch
    {
        1 => "Gen1", 2 => "Gen2", 3 => "Gen3", 4 => "Gen4", 5 => "Gen5", 6 => "Gen6",
        null => "Unknown",
        _ => $"Gen{gen}",
    };

    public string CurrentLinkText => CurrentLinkGen is null && CurrentLinkWidth is null
        ? "Unknown"
        : $"{GenText(CurrentLinkGen)} x{CurrentLinkWidth?.ToString() ?? "?"}";

    public string MaxLinkText => MaxLinkGen is null && MaxLinkWidth is null
        ? "Unknown"
        : $"{GenText(MaxLinkGen)} x{MaxLinkWidth?.ToString() ?? "?"}";

    /// <summary>#682: negotiated below the device's own reported maximum right now - an x16 card
    /// running at x4, or a Gen4 drive dropped to Gen1, is a dirty slot, a failing riser, or a dying
    /// card/drive. Only true when both current and max are known (never guessed from a partial read).</summary>
    public bool IsBelowMax => CurrentLinkGen is { } cg && MaxLinkGen is { } mg && cg < mg
        || CurrentLinkWidth is { } cw && MaxLinkWidth is { } mw && cw < mw;

    /// <summary>#682: this link changed (generation and/or width) since the previous boot this app
    /// saw the same device instance at - set by PciLinkService after comparing against
    /// PciLinkHistoryService's persisted per-boot record, not derived here. Null until that
    /// comparison has actually run (e.g. no prior-boot record exists yet for this device).</summary>
    public bool? ChangedSincePreviousBoot { get; init; }

    public string? PreviousBootLinkText { get; init; }
}
