namespace TaskManagerPlus.Services;

/// <summary>
/// #469: translates a device's ConfigManagerErrorCode (the CM_PROB_* value Device Manager's own
/// property sheet shows as "This device cannot start. (Code N)") into a short name, a plain-
/// language likely cause, and a concrete next step. This is a small, fixed, well-known code set
/// that's been stable since Windows 2000 - not every value below 60 is one Windows actually
/// returns, and an unrecognized code degrades to a generic "Unknown problem code" message rather
/// than a guess, the same "degrade rather than fabricate" convention this app uses everywhere else.
/// "Quick flag, not a verdict" applies here too: the cause/next-step text is the common, typical
/// explanation for that code - not a certainty for every individual device.
/// </summary>
public static class ProblemCodeDecoder
{
    private sealed record Entry(string Name, string Cause, string NextStep);

    private static readonly Dictionary<int, Entry> Codes = new()
    {
        [1] = new("Not configured",
            "No driver is associated with this device, or Windows hasn't finished setting it up.",
            "Reinstall or update the driver, or run the hardware troubleshooter."),
        [3] = new("Driver corrupted or low resources",
            "The driver failed to load - it may be corrupted, or the system was low on memory when it tried to load.",
            "Reinstall the driver; if it keeps happening, check available memory."),
        [9] = new("Resources not identified",
            "The system firmware reported resource information Windows couldn't reconcile with this device.",
            "Check for a BIOS/UEFI firmware update; try reseating the device."),
        [10] = new("Cannot start",
            "Windows tried to start the device and its driver reported failure - often a resource conflict or a driver/hardware fault.",
            "Update or reinstall the driver, check Device Manager for resource conflicts, or try a different slot/port."),
        [12] = new("Insufficient resources",
            "Not enough free hardware resources (IRQ/memory/I-O range) are available for this device.",
            "Free up resources by disabling a conflicting device, or move the device to a different slot."),
        [14] = new("Restart required",
            "The device's configuration changed and needs a reboot to finish applying.",
            "Restart the computer."),
        [16] = new("Resources not fully identified",
            "Windows could not identify all of the resources this device requires.",
            "Reinstall the driver, or check the device's resource settings in Device Manager."),
        [18] = new("Reinstall needed",
            "The driver itself reported that it needs to be reinstalled.",
            "Uninstall the device in Device Manager, then scan for hardware changes."),
        [19] = new("Registry data corrupted",
            "This device's registry configuration data is invalid or corrupted.",
            "Reinstall the driver; consider a System File Checker (sfc /scannow) pass."),
        [21] = new("Being removed",
            "Windows is in the process of removing this device.",
            "Wait for the removal to finish, or restart if it appears stuck."),
        [22] = new("Disabled",
            "The device has been manually disabled.",
            "Right-click the device in Device Manager and choose Enable."),
        [24] = new("Not present / incomplete install",
            "The device isn't present, isn't working properly, or doesn't have all of its drivers installed.",
            "Reconnect the device, or reinstall its driver package."),
        [28] = new("No driver installed",
            "No driver is installed for this device at all.",
            "Install the manufacturer's driver package, or use Update Driver in Device Manager."),
        [29] = new("Disabled by firmware",
            "The device is disabled at the BIOS/UEFI level, so Windows never gave it any resources.",
            "Enable the device in BIOS/UEFI setup."),
        [31] = new("Driver failed to load",
            "Windows could not load a working driver for this device.",
            "Update or reinstall the driver; check the manufacturer's site for a version matching this Windows build."),
        [32] = new("Driver service disabled",
            "The driver's underlying service is disabled - another driver may be standing in for it.",
            "Check the service's Start type (services.msc or the registry) and re-enable it if it shouldn't be disabled."),
        [33] = new("Resource requirements unknown",
            "Windows could not determine what resources this device requires.",
            "Reinstall the driver, or check for a firmware/BIOS update."),
        [34] = new("Settings undeterminable",
            "Windows could not determine the correct settings for this device.",
            "Manually configure resources in Device Manager, or consult the manufacturer's documentation."),
        [35] = new("Firmware information incomplete",
            "The system firmware doesn't provide enough information to configure this device.",
            "Check for a BIOS/UEFI update."),
        [36] = new("IRQ translation conflict",
            "The device is requesting a PCI interrupt but is configured for an ISA interrupt (or vice versa).",
            "Check BIOS/UEFI plug-and-play/IRQ settings."),
        [37] = new("Driver initialization failed",
            "Windows could not initialize this device's driver.",
            "Reinstall the driver."),
        [38] = new("Previous driver still loaded",
            "A prior instance of this device's driver is still in memory.",
            "Restart the computer."),
        [39] = new("Driver missing or corrupted",
            "Windows could not load the driver file - it may be missing or corrupted.",
            "Reinstall the driver package."),
        [40] = new("Registry service key invalid",
            "This device's service key information in the registry is missing or malformed.",
            "Reinstall the driver - a corrupted service entry usually needs a clean reinstall."),
        [41] = new("Driver loaded, device not found",
            "The driver loaded successfully but Windows can't find the physical hardware.",
            "Reseat/reconnect the device, or uninstall it and rescan for hardware changes."),
        [42] = new("Duplicate device",
            "Windows detected a duplicate of this device already running.",
            "Remove the duplicate/phantom device entry (see the non-present devices list) and rescan."),
        [43] = new("Driver reported a failure",
            "The driver told Windows the device failed - this can be a real hardware problem, not just a driver issue.",
            "Check the manufacturer's diagnostics; consider reinstalling the driver or testing the hardware."),
        [44] = new("Shut down by software",
            "An application or service told Windows to shut down this device.",
            "Check for a management app or power setting that stopped this device, then restart it."),
        [45] = new("Not connected",
            "The device is registered with Windows but isn't currently connected to the computer.",
            "Reconnect the device - this is expected/harmless for hardware that's simply unplugged."),
        [46] = new("Unavailable during shutdown",
            "Windows is shutting down and can no longer access this device.",
            "No action needed - this is expected during shutdown."),
        [47] = new("Prepared for safe removal",
            "The device was prepared for safe removal (Eject/Safely Remove) but hasn't been physically removed.",
            "Physically remove and reconnect the device, or reboot to reset its state."),
        [48] = new("Driver blocked",
            "Windows blocked this driver because it's known to cause problems.",
            "Check Windows Update or the manufacturer's site for a newer, unblocked driver."),
        [49] = new("Registry hive too large",
            "Windows can't start new devices because the system registry hive has grown too large.",
            "Clean up unused/phantom devices (see the non-present devices list)."),
        [50] = new("Properties could not be applied",
            "Windows couldn't apply all of this device's configured properties.",
            "Reinstall the driver, or reset the device's properties to defaults."),
        [51] = new("Waiting on another device",
            "This device is waiting for another device to start first.",
            "Usually resolves on its own; if not, check the device it depends on."),
        [52] = new("Unsigned driver blocked",
            "Windows couldn't verify the driver's digital signature, so it refused to load it.",
            "Obtain a properly signed driver from the manufacturer, or check for a Windows Update replacement."),
        [54] = new("Disabled after repeated failures",
            "Windows disabled this device after it reported additional problems.",
            "Check the manufacturer's diagnostics - the hardware may be failing."),
    };

    public static string DescribeName(int code) =>
        code == 0 ? "OK" : Codes.TryGetValue(code, out var e) ? $"{e.Name} (Code {code})" : $"Unknown problem code ({code})";

    public static string DescribeCause(int code) =>
        code == 0 ? "Working normally." : Codes.TryGetValue(code, out var e) ? e.Cause : "Not a documented Device Manager problem code - shown as-is.";

    public static string DescribeNextStep(int code) =>
        code == 0 ? string.Empty : Codes.TryGetValue(code, out var e) ? e.NextStep : "Check this device's own property sheet in Device Manager for more detail.";
}
