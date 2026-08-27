using System.Management;
using System.Runtime.Intrinsics.X86;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Static CPU identification readouts for the CPU tab (Round 8 #25/#28/#29/#30) - queried once
/// (like CpuTopologyService) since none of this changes at runtime. Each field degrades to
/// "Unknown"/empty independently on any failure, the same graceful-degradation shape every other
/// optional data source in this app follows.
/// </summary>
public static class CpuFeatureService
{
    public static CpuFeatureInfo Read()
    {
        var (l1, l2, l3) = ReadCacheSizes();
        return new CpuFeatureInfo
        {
            MicrocodeRevision = ReadMicrocodeRevision(),
            MitigationOverrideText = ReadMitigationOverride(),
            // #29: these IsSupported flags come from .NET 8's own runtime feature detection
            // (System.Runtime.Intrinsics.X86), which is itself CPUID-backed and JIT-verified - a
            // genuinely accurate answer for "can code running on this machine use this
            // instruction set", not a guess, and without this app taking on raw CPUID execution
            // (writing/executing machine code at runtime) or a native helper process, both a much
            // higher risk tier than anything else in this app.
            Sse42Supported = Sse42.IsSupported,
            AvxSupported = Avx.IsSupported,
            Avx2Supported = Avx2.IsSupported,
            Avx512Supported = Avx512F.IsSupported,
            FmaSupported = Fma.IsSupported,
            L1CacheText = l1,
            L2CacheText = l2,
            L3CacheText = l3,
        };
    }

    /// <summary>
    /// #25: reads the microcode revision Windows itself loaded, from the same registry value
    /// tools like CPU-Z read (HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0\"Update
    /// Revision", an 8-byte REG_BINARY whose last 4 bytes are the revision number, little-endian).
    /// Not a documented, versioned Microsoft contract - the same "adaptive, degrade gracefully
    /// rather than guess a wrong exact layout" caveat EventLogService's bugcheck-code extraction
    /// already carries for a different registry/event layout - so any surprise (missing value,
    /// unexpected length) degrades to "Unknown" rather than a wrong number.
    /// </summary>
    private static string ReadMicrocodeRevision()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("Update Revision") is byte[] bytes && bytes.Length >= 8)
            {
                uint revision = BitConverter.ToUInt32(bytes, 4);
                return $"0x{revision:X8}";
            }
        }
        catch
        {
            // Key/value unavailable - fall through to Unknown.
        }
        return "Unknown";
    }

    /// <summary>
    /// #28: best-effort Spectre/Meltdown mitigation status, informational only - Windows offers no
    /// simple "are mitigations currently active" API (Microsoft's own Get-SpeculationControlSettings
    /// script infers it from a mix of registry state and CPU feature bits far beyond what's worth
    /// replicating here). This instead reports whether an administrator has manually overridden the
    /// OS/microcode default via FeatureSettingsOverride/FeatureSettingsOverrideMask - their absence
    /// is the common, healthy case (default mitigation behavior); their presence means *something*
    /// was overridden, without this app decoding the exact bit meaning (undocumented, model-
    /// specific), the same "quick flag, not a verdict" tier as the CPU throttle heuristic.
    /// </summary>
    private static string ReadMitigationOverride()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            var overrideVal = key?.GetValue("FeatureSettingsOverride");
            var maskVal = key?.GetValue("FeatureSettingsOverrideMask");

            if (overrideVal is null && maskVal is null)
                return "No manual override detected - using the OS/microcode default mitigation settings.";

            long overrideNum = ToInt64(overrideVal);
            long maskNum = ToInt64(maskVal);
            return $"Manual override present (FeatureSettingsOverride=0x{overrideNum:X}, Mask=0x{maskNum:X}) - Spectre/Meltdown mitigations may be partially disabled. See Microsoft KB4072698 for the exact bit meanings.";
        }
        catch
        {
            return "Unknown (couldn't read the mitigation registry keys).";
        }
    }

    private static long ToInt64(object? value)
    {
        try { return value is null ? 0 : Convert.ToInt64(value); }
        catch { return 0; }
    }

    /// <summary>
    /// #30: L1/L2/L3 cache sizes. Win32_CacheMemory is the safer WMI-only path (vs. CPUID leaf
    /// parsing), but is frequently entirely unpopulated for modern CPUs on real hardware - when it
    /// comes back empty for L2/L3, Win32_Processor's own L2CacheSize/L3CacheSize fields are tried
    /// as a fallback (no L1 equivalent exists there, so L1 stays "Unknown" in that case).
    /// </summary>
    private static (string L1, string L2, string L3) ReadCacheSizes()
    {
        string l1 = "Unknown", l2 = "Unknown", l3 = "Unknown";
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Level, InstalledSize FROM Win32_CacheMemory");
            long l1Sum = 0, l2Sum = 0, l3Sum = 0;
            bool l1Found = false, l2Found = false, l3Found = false;
            foreach (ManagementObject mo in searcher.Get())
            {
                int level = Convert.ToInt32(mo["Level"] ?? 0);
                long sizeKb = Convert.ToInt64(mo["InstalledSize"] ?? 0L);
                // CIM Level values: 3 = L1, 4 = L2, 5 = L3.
                switch (level)
                {
                    case 3: l1Sum += sizeKb; l1Found = true; break;
                    case 4: l2Sum += sizeKb; l2Found = true; break;
                    case 5: l3Sum += sizeKb; l3Found = true; break;
                }
            }
            if (l1Found) l1 = FormatKb(l1Sum);
            if (l2Found) l2 = FormatKb(l2Sum);
            if (l3Found) l3 = FormatKb(l3Sum);
        }
        catch
        {
            // Win32_CacheMemory unavailable - fall through to the Win32_Processor fallback below.
        }

        if (l2 == "Unknown" || l3 == "Unknown")
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT L2CacheSize, L3CacheSize FROM Win32_Processor");
                foreach (ManagementObject mo in searcher.Get())
                {
                    if (l2 == "Unknown")
                    {
                        long l2Kb = Convert.ToInt64(mo["L2CacheSize"] ?? 0L);
                        if (l2Kb > 0) l2 = FormatKb(l2Kb);
                    }
                    if (l3 == "Unknown")
                    {
                        long l3Kb = Convert.ToInt64(mo["L3CacheSize"] ?? 0L);
                        if (l3Kb > 0) l3 = FormatKb(l3Kb);
                    }
                    break; // only one CPU package's worth of fields needed
                }
            }
            catch
            {
                // Leave whatever was already resolved above.
            }
        }

        return (l1, l2, l3);
    }

    private static string FormatKb(long kb) => kb >= 1024 ? $"{kb / 1024.0:0.#} MB" : $"{kb} KB";
}
