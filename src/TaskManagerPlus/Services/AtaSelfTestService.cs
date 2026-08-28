namespace TaskManagerPlus.Services;

/// <summary>
/// #334: ATA SMART EXECUTE OFFLINE IMMEDIATE (short/extended self-test) via
/// IOCTL_ATA_PASS_THROUGH_DIRECT, plus polling the self-test execution-status byte for progress and
/// reading back the final result/failing LBA - the ATA (SATA) equivalent of #321's NVMe self-test
/// trigger. Given the same latitude that item was granted: the exact ATA PASS THROUGH command byte
/// layout for issuing SMART EXECUTE OFFLINE IMMEDIATE (subcommand 0xD4, with the self-test type
/// packed into the LBA-low register, and a correct REGISTERS-vs-STATUS-ON-RETURN flag combination
/// that varies subtly by driver) is the least-travelled, highest-risk part of this feature to get
/// right against real hardware without a test rig to verify against - so this is intentionally the
/// structure/UI in place with issuance stubbed, the same "not yet wired to hardware" honesty as
/// NvmeHealthLogService.TriggerSelfTest, rather than an unverified admin command sent to a physical
/// drive. Reading the drive's current SMART status/raw attribute table (SmartRawAttributeService) is
/// real and already works; only *triggering a new* self-test is stubbed here.
/// </summary>
public static class AtaSelfTestService
{
    /// <summary>Rough, vendor-independent duration expectation shown before either button (#334's
    /// "showing the drive's own estimated duration first" ask). A drive's own recommended
    /// polling-time figures live in a separate SMART data structure this app's raw-attribute decoder
    /// doesn't model, so this is stated honestly as a ballpark, not this specific drive's number.</summary>
    public const string EstimatedDurationText =
        "Short test: typically 1-2 minutes on most drives. Extended test: commonly 30 minutes to " +
        "several hours depending on capacity. This app doesn't yet decode the drive's own reported " +
        "self-test time (a separate SMART data field from the attribute table above) - these are " +
        "vendor-independent ballparks, not this specific drive's number.";

    public static (bool Success, string Message) TriggerSelfTest(int diskIndex, bool extended) => (false,
        $"Not yet wired to hardware - issuing SMART EXECUTE OFFLINE IMMEDIATE ({(extended ? "extended" : "short")}) " +
        "needs a verified ATA PASS THROUGH command byte layout this chunk didn't have confidence issuing against " +
        "real hardware. The raw SMART attribute table above (and the pending-sector re-check) already reflect live " +
        "drive data; only triggering a new self-test is stubbed.");
}
