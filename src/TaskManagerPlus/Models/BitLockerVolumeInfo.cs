namespace TaskManagerPlus.Models;

/// <summary>#389: one BitLocker key protector on a volume - Win32_EncryptableVolume.GetKeyProtectors
/// / GetKeyProtectorType. SECURITY: Id is the protector's opaque GUID identifier only, never the
/// recovery key/passphrase/numerical password itself - this app never calls
/// GetKeyProtectorNumericalPassword or any other method that would return actual secret key
/// material. See BitLockerService's remarks.</summary>
public sealed class BitLockerKeyProtectorInfo
{
    public string Id { get; init; } = string.Empty;
    public string TypeText { get; init; } = "Unknown";
}

/// <summary>Round 20, #389-#393: one fixed volume's BitLocker facts - key protector inventory,
/// suspended-protection/auto-unlock state, cipher/hardware-encryption/live conversion percentage,
/// and (when BitLocker is off) a best-effort reason why. Read once at Storage-tab load, same tier as
/// the rest of the tab's one-time WMI reads. See BitLockerService.ReadAllAsync.</summary>
public sealed class BitLockerVolumeInfo
{
    public string DriveLetter { get; init; } = string.Empty;

    /// <summary>False when no Win32_EncryptableVolume instance exists for this drive at all (not a
    /// BitLocker-capable volume type) - UnavailableReason then reads "Not applicable". A denied
    /// namespace/method (non-Enterprise/Pro edition, policy) instead reads "Unknown" - the same
    /// distinction VolumeDiagnosticsService.ReadBitLockerStatus already establishes.</summary>
    public bool Available { get; init; } = true;
    public string UnavailableReason { get; init; } = string.Empty;

    // ---- #389: key protector inventory ------------------------------------------------------
    public List<BitLockerKeyProtectorInfo> KeyProtectors { get; init; } = new();
    public bool HasKeyProtectors => KeyProtectors.Count > 0;

    // ---- #390: suspended protection + auto-unlock -------------------------------------------
    public bool IsProtectionSuspended { get; init; }

    /// <summary>Reboots remaining before BitLocker protection automatically resumes - only
    /// meaningful (non-null) for the OS volume while it's actually suspended; GetSuspendCount
    /// returns an error for any other volume/state, degrading to null rather than 0.</summary>
    public int? SuspendCount { get; init; }

    /// <summary>Null when the call doesn't apply to this volume (the currently running OS volume
    /// can't be queried for its own auto-unlock state) rather than a guessed false.</summary>
    public bool? IsAutoUnlockEnabled { get; init; }
    public bool? IsAutoUnlockKeyStored { get; init; }

    // ---- #391: cipher, hardware encryption, live conversion percentage ----------------------
    public string ConversionStatusText { get; init; } = "Unknown";
    public int ConversionStatusCode { get; init; } = -1;
    public double? EncryptionPercentage { get; init; }
    public string EncryptionMethodText { get; init; } = string.Empty;
    public string HardwareEncryptionStatusText { get; init; } = string.Empty;

    public bool IsConverting => ConversionStatusCode is 2 or 3; // EncryptionInProgress / DecryptionInProgress

    // ---- #393: Device Encryption blocker report (only populated when BitLocker is off) -------
    public string OffReasonText { get; init; } = string.Empty;
    public bool HasOffReason => OffReasonText.Length > 0;

    public string ProtectionSummaryText => IsProtectionSuspended
        ? SuspendCount is { } n
            ? $"Protection suspended - resumes automatically after {n} more restart(s)."
            : "Protection suspended."
        : string.Empty;

    public string AutoUnlockText => IsAutoUnlockEnabled switch
    {
        true => "Auto-unlock is enabled for this volume.",
        false => "Auto-unlock is not enabled for this volume.",
        null => string.Empty, // not applicable to this volume (e.g. the running OS volume) - say nothing rather than guess
    };

    public string EncryptionPercentageText => EncryptionPercentage is { } p ? $"{p:0.#}%" : string.Empty;
}
