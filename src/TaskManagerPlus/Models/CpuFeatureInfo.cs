namespace TaskManagerPlus.Models;

/// <summary>Static, informational CPU identification readouts for the CPU tab (Round 8 #25/#28/
/// #29/#30) - microcode revision, Spectre/Meltdown mitigation override status, instruction-set
/// support, and cache sizes. Queried once (CpuFeatureService.Read, from CpuViewModel's
/// constructor) rather than per tick, the same "static, doesn't change at runtime" treatment
/// CpuTopologyService's own query already gets.</summary>
public sealed class CpuFeatureInfo
{
    /// <summary>#25: CPU microcode/patch revision, read from the same registry value tools like
    /// CPU-Z read - see CpuFeatureService.ReadMicrocodeRevision for the caveat (not a documented,
    /// versioned Microsoft contract).</summary>
    public string MicrocodeRevision { get; init; } = "Unknown";

    /// <summary>#28: best-effort Spectre/Meltdown mitigation override readout, from the
    /// FeatureSettingsOverride/FeatureSettingsOverrideMask registry values - see
    /// CpuFeatureService.ReadMitigationOverride for exactly what this can and can't determine.</summary>
    public string MitigationOverrideText { get; init; } = string.Empty;

    /// <summary>#29: instruction-set support, read from .NET 8's own System.Runtime.Intrinsics.X86
    /// IsSupported flags - these are backed by the runtime's own CPUID-driven feature detection
    /// (JIT-verified, not a guess), so unlike most "best-effort" readouts in this app these are
    /// genuinely accurate for what code running on this machine can actually use. AVX-512 support
    /// in particular also depends on OS/JIT enablement beyond raw hardware capability, so it can
    /// read false on hardware that technically has the silicon but isn't exposed to user-mode code
    /// on this OS/runtime combination - documented in the UI, not just here.</summary>
    public bool Sse42Supported { get; init; }
    public bool AvxSupported { get; init; }
    public bool Avx2Supported { get; init; }
    public bool Avx512Supported { get; init; }
    public bool FmaSupported { get; init; }

    /// <summary>#30: L1/L2/L3 cache sizes, from Win32_CacheMemory (with a Win32_Processor.
    /// L2CacheSize/L3CacheSize fallback for L2/L3 when Win32_CacheMemory reports nothing, which is
    /// common on modern systems) - "Unknown" when neither source has a figure for that level.</summary>
    public string L1CacheText { get; init; } = "Unknown";
    public string L2CacheText { get; init; } = "Unknown";
    public string L3CacheText { get; init; } = "Unknown";
}
