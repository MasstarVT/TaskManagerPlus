namespace TaskManagerPlus.Services;

/// <summary>One parameter's documented meaning for a given bugcheck code (#29). IsStatusCode
/// flags a parameter that this specific code documents as an NTSTATUS/exception code (e.g. 0x1E's
/// parameter 1) so BugcheckDecoder knows to additionally run it through #30's NtStatusLookup.</summary>
public readonly struct BugcheckParamLabel
{
    public string Text { get; }
    public bool IsStatusCode { get; }

    public BugcheckParamLabel(string text, bool isStatusCode = false)
    {
        Text = text;
        IsStatusCode = isStatusCode;
    }
}

/// <summary>
/// Round 15, item 29: per-bugcheck-code parameter labels, straight from Microsoft's own Bug Check
/// Code Reference documentation for each code (e.g. 0xA's four parameters are documented as
/// "Memory referenced / IRQL / access type / faulting instruction address", not four anonymous
/// hex numbers). Covers the same "common, not exhaustive" set of codes BugcheckGuidanceLookup and
/// the wider bugcheck-decoding chunk focus on; an unmapped code (or a mapped code's parameter
/// count that doesn't match what was actually read) falls back to a plain "Parameter N" label -
/// never a guess at what an unmapped code's parameters mean.
/// </summary>
public static class BugcheckParameterLookup
{
    private static readonly BugcheckParamLabel[] Default =
    {
        new("Parameter 1"), new("Parameter 2"), new("Parameter 3"), new("Parameter 4"),
    };

    private static readonly Dictionary<uint, BugcheckParamLabel[]> Labels = new()
    {
        [0x0000000A] = new[]
        {
            new BugcheckParamLabel("Memory referenced"),
            new BugcheckParamLabel("IRQL at time of reference"),
            new BugcheckParamLabel("Access type (0 = read, 1 = write, 8 = execute)"),
            new BugcheckParamLabel("Address of the instruction that referenced memory"),
        },
        [0x000000D1] = new[]
        {
            new BugcheckParamLabel("Memory referenced"),
            new BugcheckParamLabel("IRQL at time of reference"),
            new BugcheckParamLabel("Access type (0 = read, 1 = write)"),
            new BugcheckParamLabel("Address of the driver instruction that referenced memory"),
        },
        [0x00000050] = new[]
        {
            new BugcheckParamLabel("Memory referenced"),
            new BugcheckParamLabel("Access type (0 = read, 1 = write)"),
            new BugcheckParamLabel("Address of the instruction that referenced memory"),
            new BugcheckParamLabel("Reserved"),
        },
        [0x0000001E] = new[]
        {
            new BugcheckParamLabel("Exception code that was not handled", isStatusCode: true),
            new BugcheckParamLabel("Address where the exception occurred"),
            new BugcheckParamLabel("Exception parameter 0"),
            new BugcheckParamLabel("Exception parameter 1"),
        },
        [0x0000007E] = new[]
        {
            new BugcheckParamLabel("Exception code that was not handled", isStatusCode: true),
            new BugcheckParamLabel("Address where the exception occurred"),
            new BugcheckParamLabel("Address of the exception record"),
            new BugcheckParamLabel("Address of the context record"),
        },
        [0x0000008E] = new[]
        {
            new BugcheckParamLabel("Exception code that was not handled", isStatusCode: true),
            new BugcheckParamLabel("Address where the exception occurred"),
            new BugcheckParamLabel("Address of the exception record"),
            new BugcheckParamLabel("Reserved"),
        },
        [0x0000003B] = new[]
        {
            new BugcheckParamLabel("Exception code", isStatusCode: true),
            new BugcheckParamLabel("Address of the exception"),
            new BugcheckParamLabel("Reserved"),
            new BugcheckParamLabel("Reserved"),
        },
        [0x00000024] = new[]
        {
            new BugcheckParamLabel("Source file + line number (encoded, NTFS-internal)"),
            new BugcheckParamLabel("Varies by source location"),
            new BugcheckParamLabel("Varies by source location"),
            new BugcheckParamLabel("Varies by source location"),
        },
        [0x0000001A] = new[]
        {
            new BugcheckParamLabel("Subtype code - meaning of the rest depends on this value"),
            new BugcheckParamLabel("Varies by subtype"),
            new BugcheckParamLabel("Varies by subtype"),
            new BugcheckParamLabel("Varies by subtype"),
        },
        [0x0000009F] = new[]
        {
            new BugcheckParamLabel("Subcode - which power transition stalled"),
            new BugcheckParamLabel("Varies by subcode (often a device or driver object)"),
            new BugcheckParamLabel("Varies by subcode"),
            new BugcheckParamLabel("Varies by subcode"),
        },
        [0x000000C2] = new[]
        {
            new BugcheckParamLabel("Subcode - type of pool violation"),
            new BugcheckParamLabel("Varies by subcode (often a pool address or tag)"),
            new BugcheckParamLabel("Varies by subcode"),
            new BugcheckParamLabel("Varies by subcode"),
        },
        [0x000000C5] = new[]
        {
            new BugcheckParamLabel("Address/tag of the corrupted pool allocation"),
            new BugcheckParamLabel("Varies (often the pool block's own header/size)"),
            new BugcheckParamLabel("Varies"),
            new BugcheckParamLabel("Varies"),
        },
        [0x000000EF] = new[]
        {
            new BugcheckParamLabel("Process object that terminated"),
            new BugcheckParamLabel("Reserved"),
            new BugcheckParamLabel("Reserved"),
            new BugcheckParamLabel("Reserved"),
        },
        [0x00000133] = new[]
        {
            new BugcheckParamLabel("0 = a single DPC/ISR ran too long, 1 = cumulative time at DISPATCH_LEVEL exceeded"),
            new BugcheckParamLabel("Varies by subtype"),
            new BugcheckParamLabel("Varies by subtype"),
            new BugcheckParamLabel("Varies by subtype"),
        },
        [0x00000124] = new[]
        {
            new BugcheckParamLabel("Error source type (1 = machine check, 2 = firmware, 3 = PCI Express, 4 = software)"),
            new BugcheckParamLabel("Address of the WHEA_ERROR_RECORD structure"),
            new BugcheckParamLabel("Reserved"),
            new BugcheckParamLabel("Reserved"),
        },
        [0x0000007A] = new[]
        {
            new BugcheckParamLabel("Lock type that was held (1 = shared, 2 = exclusive, 3 = page-table)"),
            new BugcheckParamLabel("I/O status code from the failed read", isStatusCode: true),
            new BugcheckParamLabel("Varies (faulting PTE or reserved)"),
            new BugcheckParamLabel("Varies (faulting virtual address or reserved)"),
        },
        [0x00000116] = new[]
        {
            new BugcheckParamLabel("Pointer to the internal TDR recovery context (or 0)"),
            new BugcheckParamLabel("Reserved"),
            new BugcheckParamLabel("Reserved"),
            new BugcheckParamLabel("Reserved"),
        },
        [0x00000117] = new[]
        {
            new BugcheckParamLabel("Pointer to the internal TDR recovery context (or 0)"),
            new BugcheckParamLabel("Reserved"),
            new BugcheckParamLabel("Reserved"),
            new BugcheckParamLabel("Reserved"),
        },
    };

    public static BugcheckParamLabel[] GetLabels(uint code) => Labels.TryGetValue(code, out var l) ? l : Default;

    public static BugcheckParamLabel GetLabel(uint code, int index)
    {
        var labels = GetLabels(code);
        return index >= 0 && index < labels.Length ? labels[index] : new BugcheckParamLabel($"Parameter {index + 1}");
    }
}
