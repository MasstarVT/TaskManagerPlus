namespace TaskManagerPlus.Models;

/// <summary>
/// #411: one named Section (pagefile- or file-backed shared memory) found during a "Scan shared
/// memory" pass, aggregated across every process that currently holds a handle to it - see
/// SharedMemoryInspectionService.
/// </summary>
public sealed class SharedMemorySection
{
    public string Name { get; set; } = string.Empty;
    public long? SizeBytes { get; set; }
    public int HandleCount { get; set; }
    public List<string> ProcessNames { get; set; } = new();
}
