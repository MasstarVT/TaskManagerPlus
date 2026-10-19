using System.Management;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #430: per-page-file configuration inspector - extends the single drive-letter/media-type hint
/// SystemSpecsService.ReadPageFileLocation already reads into a full per-file grid: joins
/// Win32_PageFileSetting (InitialSize/MaximumSize, the *configured* values) with
/// Win32_PageFileUsage (AllocatedBaseSize/CurrentUsage/PeakUsage, the *observed* values) by Name -
/// the same "\\?\C:\pagefile.sys"-shaped path both classes key on. Win32_ComputerSystem's own
/// AutomaticManagedPagefile flag is read alongside so the UI can say "Windows is managing this
/// automatically" instead of just listing raw sizes with no context.
///
/// "No page file configured" (Files empty) is a real, valid system state - not every machine has
/// one, and this service reports that plainly rather than treating an empty WMI result as a query
/// failure.
/// </summary>
public static class PageFileConfigurationService
{
    public static PageFileConfigSnapshot Query()
    {
        try
        {
            var settingsByName = new Dictionary<string, (double InitialMb, double MaxMb)>(StringComparer.OrdinalIgnoreCase);
            using (var settingSearcher = new ManagementObjectSearcher(
                "SELECT Name, InitialSize, MaximumSize FROM Win32_PageFileSetting"))
            {
                foreach (ManagementObject mo in settingSearcher.Get())
                {
                    string name = (mo["Name"] as string ?? string.Empty).Trim();
                    if (name.Length == 0) continue;
                    double initial = Convert.ToDouble(mo["InitialSize"] ?? 0.0);
                    double max = Convert.ToDouble(mo["MaximumSize"] ?? 0.0);
                    settingsByName[name] = (initial, max);
                }
            }

            var files = new List<PageFileConfigInfo>();
            using (var usageSearcher = new ManagementObjectSearcher(
                "SELECT Name, AllocatedBaseSize, CurrentUsage, PeakUsage FROM Win32_PageFileUsage"))
            {
                foreach (ManagementObject mo in usageSearcher.Get())
                {
                    string name = (mo["Name"] as string ?? string.Empty).Trim();
                    if (name.Length == 0) continue;

                    double currentMb = Convert.ToDouble(mo["AllocatedBaseSize"] ?? 0.0);
                    double peakMb = Convert.ToDouble(mo["PeakUsage"] ?? 0.0);
                    settingsByName.TryGetValue(name, out var setting);
                    bool systemManaged = setting.InitialMb <= 0 && setting.MaxMb <= 0;

                    files.Add(new PageFileConfigInfo
                    {
                        Volume = name.Length >= 2 ? name[..2] : name,
                        FilePath = name,
                        InitialSizeMb = setting.InitialMb,
                        MaximumSizeMb = setting.MaxMb,
                        CurrentSizeMb = currentMb,
                        PeakUsageMb = peakMb,
                        IsSystemManaged = systemManaged,
                        // #431: a fixed (not system-managed) file whose recorded peak usage reached
                        // its configured maximum - the commit limit couldn't grow past that ceiling
                        // at least once. PeakUsage is space used inside the file, so it can't pass
                        // the file's size: a full file reads at (or within rounding of) MaxMb, and
                        // a peak above MaxMb means the maximum was lowered since this boot (the
                        // view model words that case separately).
                        IsCappedBelowPeakUsage = !systemManaged && setting.MaxMb > 0 && peakMb >= setting.MaxMb * 0.98,
                    });
                }
            }

            bool? autoManaged = null;
            try
            {
                using var csSearcher = new ManagementObjectSearcher(
                    "SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem");
                foreach (ManagementObject mo in csSearcher.Get())
                {
                    autoManaged = mo["AutomaticManagedPagefile"] is bool b && b;
                    break;
                }
            }
            catch { /* leave null - "unknown", not a guess */ }

            return new PageFileConfigSnapshot { Files = files, IsAutomaticallyManaged = autoManaged };
        }
        catch (Exception ex)
        {
            // WMI namespace/class unavailable - degrade rather than throw, but say so: an empty
            // list here would otherwise read as "no page file is configured".
            return new PageFileConfigSnapshot { ErrorMessage = ex.Message };
        }
    }
}
