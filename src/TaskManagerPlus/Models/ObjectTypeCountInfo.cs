namespace TaskManagerPlus.Models;

/// <summary>
/// #426: one kernel object type's system-wide count vs. its own high-water mark, from
/// NtQueryObject(ObjectTypesInformation) - "Event", "Section", "Token", "File", "Semaphore", ...
/// IsNearHighWaterMark flags a type that's both close to (within NearHighWaterMarkPercent of) and
/// climbing toward its own historical peak - a quick flag that a particular kind of kernel object
/// is accumulating, not a confirmed leak. See KernelObjectTypeService's remarks for why this whole
/// feature degrades to an empty list (hidden section) rather than throwing if the undocumented
/// struct layout doesn't parse cleanly on a given Windows build.
/// </summary>
public sealed class ObjectTypeCount
{
    public string TypeName { get; set; } = string.Empty;
    public long TotalNumberOfObjects { get; set; }
    public long TotalNumberOfHandles { get; set; }
    public long HighWaterNumberOfObjects { get; set; }
    public long HighWaterNumberOfHandles { get; set; }

    public const double NearHighWaterMarkPercent = 0.9;

    /// <summary>Current object count is at/past 90% of this type's own recorded high-water mark -
    /// "climbing toward its own historical peak," not an absolute severity threshold (some types,
    /// like Directory or Type itself, naturally sit at a tiny, stable count where this fires
    /// harmlessly - shown as a quick flag, not a verdict, same as elsewhere in this app).</summary>
    public bool IsNearHighWaterMark => HighWaterNumberOfObjects > 0 &&
        TotalNumberOfObjects >= HighWaterNumberOfObjects * NearHighWaterMarkPercent;
}
