namespace TaskManagerPlus.Models;

/// <summary>#330: the three independent "how many bad sectors" sources this app can read for one
/// disk/volume, shown side by side rather than silently reconciled when they disagree - they
/// genuinely measure different things (SMART counts drive-level reallocations/pending sectors,
/// chkdsk reports what NTFS found bad on a specific volume at one point in time, and $BadClus is
/// that same volume's *current* bad-cluster allocation), so a drive can legitimately show non-zero
/// on one and zero on another without either reading being "wrong".</summary>
public sealed class BadSectorSummary
{
    public int? SmartReallocated { get; init; }
    public int? SmartPending { get; init; }
    public int? SmartOfflineUncorrectable { get; init; }

    public long? ChkdskBadSectorsKb { get; init; }
    public DateTime? ChkdskReportDate { get; init; }

    public long? BadClusAllocatedBytes { get; init; }
    public string? BadClusVolume { get; init; }

    public bool HasAnySource => SmartReallocated.HasValue || SmartPending.HasValue || ChkdskBadSectorsKb.HasValue || BadClusAllocatedBytes.HasValue;

    /// <summary>True when at least two of the available sources disagree about "is there a problem
    /// at all" (one reports zero, another reports non-zero) - surfaced as a plain fact in the UI
    /// rather than silently reconciled into one number.</summary>
    public bool SourcesDisagree
    {
        get
        {
            var verdicts = new List<bool>();
            if (SmartReallocated.HasValue || SmartPending.HasValue)
                verdicts.Add((SmartReallocated ?? 0) > 0 || (SmartPending ?? 0) > 0);
            if (ChkdskBadSectorsKb.HasValue) verdicts.Add(ChkdskBadSectorsKb.Value > 0);
            if (BadClusAllocatedBytes.HasValue) verdicts.Add(BadClusAllocatedBytes.Value > 0);
            return verdicts.Count >= 2 && verdicts.Distinct().Count() > 1;
        }
    }
}
