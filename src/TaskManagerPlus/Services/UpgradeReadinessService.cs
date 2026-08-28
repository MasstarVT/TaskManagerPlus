using System.Globalization;
using System.IO;
using System.Management;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #800: the Windows Health tab's top card - detailed activation (#800a), build/servicing-stack
/// lifecycle against a small bundled end-of-servicing table (#800b), and a single "can this PC
/// take the next feature update" verdict (#800c). Deliberately reuses readings earlier chunks of
/// this domain already added rather than re-implementing them: SystemSpecsService's
/// SecurityInfo (TPM/Secure Boot) and FirmwareDiskInfo (partition style), SfcIntegrityService's
/// CurrentBuild/UBR/EditionID/DisplayVersion read, SystemPartitionService's ESP enumeration/
/// free-space measurement, and BcdInspectorService's already-parsed BCD store (for the
/// hypervisorlaunchtype value ReadSecurityInfo needs) - see each read below for exactly which
/// existing method is being called.
/// </summary>
public static class UpgradeReadinessService
{
    private const string WindowsProductAppId = "55c92734-d682-4d71-983e-d6ec3f16059f";

    // #800b: a small bundled table, not an exhaustive/auto-updated one - CLAUDE.md's "quick flag,
    // not a verdict" applies here too. Dates are Home/Pro editions specifically; Enterprise/
    // Education/IoT Enterprise SKUs of the same build get a materially longer servicing window,
    // called out in the returned EndOfServicingInfo text rather than modeled per-edition here.
    private static readonly (int Build, string Release, DateTime EndOfServicing)[] EndOfServicingTable =
    {
        (19044, "Windows 10, version 21H2", new DateTime(2023, 6, 13)),
        (19045, "Windows 10, version 22H2", new DateTime(2025, 10, 14)),
        (22000, "Windows 11, version 21H2", new DateTime(2023, 10, 10)),
        (22621, "Windows 11, version 22H2", new DateTime(2024, 10, 8)),
        (22631, "Windows 11, version 23H2", new DateTime(2025, 11, 11)),
        (26100, "Windows 11, version 24H2", new DateTime(2026, 10, 13)),
    };

    // #800c: a safety margin above Microsoft's documented ~9-11 GB minimum free-space requirement
    // for a feature update - deliberately higher than the bare minimum so this doesn't give false
    // confidence right at the edge.
    private const long RecommendedFreeSpaceBytes = 20L * 1024 * 1024 * 1024;

    /// <summary>#800: measureEsp defaults to false - actually measuring ESP free space requires
    /// briefly mounting it (SystemPartitionService.MeasureEspFreeSpaceAsync's own remarks: "the one
    /// part of this that mutates system state, briefly"), and the Startup tab's own #738 card
    /// already deliberately gates that exact mount behind a manual "Measure free space" button
    /// rather than doing it automatically. This card follows the same rule: the automatic top-card
    /// load (WindowsHealthViewModel's constructor) never mounts anything, only enumerates the ESP's
    /// existence/size via WMI (no mount needed for that); measureEsp:true is only ever passed from
    /// an explicit button click (see WindowsHealthViewModel.MeasureEspForReadinessAsync).</summary>
    public static async Task<UpgradeReadinessSnapshot> ReadSnapshotAsync(bool measureEsp = false)
    {
        var activation = await Task.Run(ReadActivationDetails).ConfigureAwait(false);
        var (edition, build, displayVersion, _, _) = SfcIntegrityService.ReadCurrentImageSpec();

        var bcdStore = await BcdInspectorService.ReadAsync().ConfigureAwait(false);
        string? hypervisorLaunchType = bcdStore.CurrentEntry?.Get("hypervisorlaunchtype");
        var security = SystemSpecsService.ReadSecurityInfo(hypervisorLaunchType);
        var firmwareDisk = SystemSpecsService.ReadFirmwareDiskInfo();

        long? systemDriveFree = ReadSystemDriveFreeBytes();

        bool espFound = false;
        long? espFree = null;
        bool espNearFull = false;
        try
        {
            var layout = await Task.Run(SystemPartitionService.ReadLayout).ConfigureAwait(false);
            if (layout.Available && layout.Esp is { } esp)
            {
                espFound = true;
                if (measureEsp)
                {
                    var (freeBytes, _) = await SystemPartitionService.MeasureEspFreeSpaceAsync().ConfigureAwait(false);
                    espFree = freeBytes;
                    if (freeBytes is { } f && esp.SizeBytes is > 0 and <= 400L * 1024 * 1024)
                    {
                        double pct = (double)f / esp.SizeBytes * 100;
                        espNearFull = pct < 10;
                    }
                }
            }
        }
        catch
        {
            // ESP enumeration/mount failed - leave EspFound false rather than guess.
        }

        var endOfServicing = ReadEndOfServicing(build);

        var blockers = new List<string>();
        if (security.TpmPresent == false) blockers.Add("No TPM detected (Windows 11 requires TPM 2.0).");
        else if (security.TpmPresent == true && security.TpmReady == false) blockers.Add("A TPM is present but not activated/enabled/owned.");

        if (security.SecureBootEnabled == false) blockers.Add("Secure Boot is off.");
        if (firmwareDisk.IsHardBlocker) blockers.Add("The system disk is MBR-partitioned (needs GPT for UEFI/Secure Boot).");
        if (systemDriveFree is { } free && free < RecommendedFreeSpaceBytes)
            blockers.Add($"Only {Formatting.FormatBytes(free)} free on the system drive (recommended: at least {Formatting.FormatBytes(RecommendedFreeSpaceBytes)}).");
        if (espNearFull) blockers.Add($"The EFI System Partition is small and nearly full ({Formatting.FormatBytes(espFree ?? 0)} free) - a common cause of feature-update failure 0x800f0922.");

        return new UpgradeReadinessSnapshot
        {
            Activation = activation,
            EditionText = edition,
            BuildText = build,
            DisplayVersionText = displayVersion,
            EndOfServicing = endOfServicing,
            TpmReady = security.TpmReady,
            SecureBootEnabled = security.SecureBootEnabled,
            SystemDiskIsMbr = firmwareDisk.IsHardBlocker,
            SystemDriveFreeBytes = systemDriveFree,
            EspFound = espFound,
            EspFreeBytes = espFree,
            BlockingItems = blockers,
        };
    }

    /// <summary>#800: filters an already-loaded #770 DISM package list for the servicing-stack
    /// update package(s) and extracts the version encoded in the package identity (e.g.
    /// "Package_for_ServicingStack_..~31bf3856ad364e35~amd64~~19041.1620.1.6" -> "19041.1620.1.6")
    /// - reuses the package list the Update history section's own "Load servicing packages" button
    /// already fetches (a full DISM enumeration) rather than running a second one just for this.
    /// Returns the placeholder text unchanged when that list hasn't been loaded yet this session.</summary>
    public static string DescribeServicingStackVersion(IReadOnlyList<ServicingPackageInfo> loadedPackages)
    {
        var match = loadedPackages
            .Where(p => p.PackageIdentity.Contains("ServicingStack", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.PackageIdentity)
            .FirstOrDefault();
        if (match is null) return "No servicing-stack update package found in the loaded package list.";

        int tilde = match.PackageIdentity.LastIndexOf('~');
        string version = tilde >= 0 && tilde + 1 < match.PackageIdentity.Length ? match.PackageIdentity[(tilde + 1)..] : match.PackageIdentity;
        return $"{version} ({match.State})";
    }

    private static EndOfServicingInfo? ReadEndOfServicing(string buildText)
    {
        string raw = buildText.Split('.')[0];
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int build)) return null;

        var match = EndOfServicingTable.FirstOrDefault(e => e.Build == build);
        if (match.Release is null) return null;

        return new EndOfServicingInfo { ReleaseName = match.Release + " (Home/Pro)", EndOfServicingDate = match.EndOfServicing };
    }

    private static long? ReadSystemDriveFreeBytes()
    {
        try
        {
            string root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch
        {
            return null;
        }
    }

    #region #800a - Detailed activation

    /// <summary>#800a: extends the existing binary Licensed/Not-activated read
    /// (SystemSpecsService.ReadActivationStatus) with SoftwareLicensingProduct's fuller detail plus
    /// SoftwareLicensingService's KMS host/renewal interval - never reads PartialProductKey or any
    /// other product-key field.</summary>
    private static ActivationDetails ReadActivationDetails()
    {
        string description = "Unknown", licenseFamily = "Unknown", productKeyChannel = "Unknown";
        int? licenseStatus = null;
        string licenseStatusText = "Unknown";
        string? licenseStatusReason = null;
        TimeSpan? graceRemaining = null;
        DateTime? evaluationEnd = null;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Description, LicenseFamily, ProductKeyChannel, LicenseStatus, LicenseStatusReason, GracePeriodRemaining, EvaluationEndDate " +
                $"FROM SoftwareLicensingProduct WHERE ApplicationID='{WindowsProductAppId}' AND PartialProductKey IS NOT NULL");
            foreach (ManagementObject mo in searcher.Get())
            {
                int status = Convert.ToInt32(mo["LicenseStatus"] ?? -1);
                uint? reason = mo["LicenseStatusReason"] is { } r ? Convert.ToUInt32(r) : null;
                uint? graceMinutes = mo["GracePeriodRemaining"] is { } g ? Convert.ToUInt32(g) : null;
                string? evalEndRaw = mo["EvaluationEndDate"] as string;

                description = (mo["Description"] as string) is { Length: > 0 } d ? d : "Unknown";
                licenseFamily = (mo["LicenseFamily"] as string) is { Length: > 0 } lf ? lf : "Unknown";
                productKeyChannel = (mo["ProductKeyChannel"] as string) is { Length: > 0 } pkc ? pkc : "Unknown";
                licenseStatus = status;
                licenseStatusText = LicenseStatusText(status);
                licenseStatusReason = reason is null or 0 ? null : $"0x{reason:X8}";
                graceRemaining = graceMinutes is > 0 ? TimeSpan.FromMinutes(graceMinutes.Value) : null;
                evaluationEnd = TryParseWmiDate(evalEndRaw);
                break; // exactly one row expected for the currently-active Windows edition
            }
        }
        catch
        {
            // SoftwareLicensingProduct can be restricted by policy on some editions - stays Unknown.
        }

        string? kmsHost = null;
        int? kmsRenewalMinutes = null;
        try
        {
            using var svcSearcher = new ManagementObjectSearcher("SELECT KeyManagementServiceMachine, VLRenewalInterval FROM SoftwareLicensingService");
            foreach (ManagementObject mo in svcSearcher.Get())
            {
                string? host = mo["KeyManagementServiceMachine"] as string;
                uint? renewal = mo["VLRenewalInterval"] is { } r ? Convert.ToUInt32(r) : null;
                kmsHost = string.IsNullOrWhiteSpace(host) ? null : host;
                kmsRenewalMinutes = renewal is > 0 ? (int)renewal.Value : null;
                break;
            }
        }
        catch
        {
            // SoftwareLicensingService unavailable - KMS fields stay null (not licensed via KMS,
            // or this edition restricts the class).
        }

        return new ActivationDetails
        {
            Description = description,
            LicenseFamily = licenseFamily,
            ProductKeyChannel = productKeyChannel,
            LicenseStatus = licenseStatus,
            LicenseStatusText = licenseStatusText,
            LicenseStatusReason = licenseStatusReason,
            GracePeriodRemaining = graceRemaining,
            EvaluationEndDate = evaluationEnd,
            KmsHost = kmsHost,
            KmsRenewalIntervalMinutes = kmsRenewalMinutes,
        };
    }

    private static string LicenseStatusText(int status) => status switch
    {
        0 => "Unlicensed",
        1 => "Licensed",
        2 => "Initial grace period",
        3 => "Additional grace period",
        4 => "Non-genuine grace period",
        5 => "Notification (unlicensed)",
        6 => "Extended grace period",
        _ => "Unknown",
    };

    private static DateTime? TryParseWmiDate(string? wmiDate)
    {
        if (string.IsNullOrWhiteSpace(wmiDate)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(wmiDate); }
        catch { return null; }
    }

    #endregion
}
