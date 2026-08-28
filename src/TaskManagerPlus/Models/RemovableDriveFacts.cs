namespace TaskManagerPlus.Models;

/// <summary>
/// Round 18, #376: one currently-attached removable disk's identity + Device Manager "Policies" tab
/// removal-policy setting - see RemovalPolicyService.
/// </summary>
public sealed class RemovableDriveFacts
{
    public string DriveLetter { get; init; } = string.Empty;
    public int DiskIndex { get; init; } = -1;
    public string Model { get; init; } = "Unknown disk";
    public string InterfaceType { get; init; } = string.Empty;
    public string MediaType { get; init; } = string.Empty;
    public string PnpDeviceId { get; init; } = string.Empty;
    public long SizeBytes { get; init; }

    /// <summary>Raw "UserRemovalPolicy" registry value: 1 = Quick removal, 2 = Better performance,
    /// null = not explicitly set by the user (Windows default) or unreadable on this system - see
    /// RemovalPolicyService.ReadRemovalPolicyRaw's remarks on why this isn't one guaranteed
    /// registry location.</summary>
    public int? RemovalPolicyRaw { get; init; }

    public string RemovalPolicyText => RemovalPolicyRaw switch
    {
        1 => "Quick removal (write caching off - safe to unplug without ejecting)",
        2 => "Better performance (write caching on - use Eject before unplugging)",
        _ => "Default (not explicitly set)",
    };
}
