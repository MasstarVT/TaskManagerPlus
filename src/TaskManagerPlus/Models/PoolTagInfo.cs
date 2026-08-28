namespace TaskManagerPlus.Models;

/// <summary>
/// #416: one four-character kernel pool tag's allocation/free counts and byte totals, split by
/// paged/nonpaged pool - the same per-tag breakdown poolmon.exe shows, read via
/// NtQuerySystemInformation(SystemPoolTagInformation) rather than requiring the WDK's poolmon
/// tool. #417 fills in LikelyDriver (best-effort, from scanning driver images for the literal tag
/// string - see PoolTagInspectionService) and #418 fills in Description/Component (from the
/// curated embedded pooltag.txt subset) once the raw tag list above is read - both start
/// null/"Unknown" and are joined in afterward, never fabricated when no match is found.
/// </summary>
public sealed class PoolTagRow
{
    public string Tag { get; set; } = string.Empty;

    public int PagedAllocs { get; set; }
    public int PagedFrees { get; set; }
    public long PagedBytes { get; set; }

    public int NonpagedAllocs { get; set; }
    public int NonpagedFrees { get; set; }
    public long NonpagedBytes { get; set; }

    public long TotalBytes => PagedBytes + NonpagedBytes;
    public int OutstandingAllocs => Math.Max(0, PagedAllocs - PagedFrees) + Math.Max(0, NonpagedAllocs - NonpagedFrees);

    /// <summary>#417: best-effort - null until a "Scan shared memory"-style driver-attribution
    /// pass has run at least once (results are cached to JSON since the scan is slow), "Unknown"
    /// if the pass ran but found no owning driver, or absent entirely (empty string) before the
    /// scan has run at all.</summary>
    public string? LikelyDriver { get; set; }

    /// <summary>#418: from the curated embedded pooltag.txt subset - null when this tag isn't in
    /// the (deliberately partial) built-in dictionary, never a guessed value.</summary>
    public string? Description { get; set; }
}

/// <summary>#419: one "Capture pool snapshot" result - the full tag table plus when it was taken,
/// so a later capture can diff against it to find which tags grew the most.</summary>
public sealed class PoolTagSnapshot
{
    public DateTime CapturedAtUtc { get; set; }
    public List<PoolTagRow> Tags { get; set; } = new();
}

/// <summary>#419: one tag's growth between two captured PoolTagSnapshots, sorted by
/// TotalByteDelta descending in MemoryViewModel - this is what actually identifies a leaking
/// driver, as opposed to the flat #416 table which just shows a point-in-time size.</summary>
public sealed class PoolTagGrowth
{
    public string Tag { get; set; } = string.Empty;
    public string? LikelyDriver { get; set; }
    public string? Description { get; set; }

    public long PagedByteDelta { get; set; }
    public long NonpagedByteDelta { get; set; }
    public long TotalByteDelta => PagedByteDelta + NonpagedByteDelta;
    public int OutstandingAllocDelta { get; set; }
}

/// <summary>#417: the persisted result of the last driver-attribution scan - PoolTagDriverCacheService's
/// on-disk shape (pool-tag-drivers.json). Keyed by the 4-character tag; a tag with no entry here
/// simply hasn't been matched to a driver yet (shown as "Unknown" once a scan has actually run, or
/// blank before one ever has).</summary>
public sealed class PoolTagDriverCache
{
    public DateTime LastScanUtc { get; set; }
    public Dictionary<string, string> TagToDriver { get; set; } = new(StringComparer.Ordinal);
}
