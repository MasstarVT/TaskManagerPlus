namespace TaskManagerPlus.Models;

/// <summary>
/// #496 (NDIS half): one non-base network component (lightweight filter OR a less-common protocol
/// driver - the registry doesn't cleanly distinguish the two, see NetworkFilterService's remarks)
/// bound directly above one or more network adapters, read from each adapter's own Net-class
/// device-setup-class instance key (HKLM\SYSTEM\CurrentControlSet\Control\Class\
/// {4d36e972-e325-11ce-bfc1-08002be10318}\NNNN\Linkage\UpperBind - a REG_MULTI_SZ of service
/// names), with the small set of expected base protocols (TCP/IP, NetBT, ...) filtered out. See
/// NetworkFilterService's remarks for exactly how each field is read and what "third-party" is
/// based on.
/// </summary>
public sealed class NdisFilterBinding
{
    public string FilterServiceName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The service's own ComponentId registry value, when present - informational only;
    /// many services (including some genuinely Microsoft ones) don't set it at all, so its absence
    /// is not itself a third-party signal. See IsThirdParty for the actual classification.</summary>
    public string? ComponentId { get; init; }

    /// <summary>Based on the bound service's own driver file - Microsoft Corporation file-version
    /// company name (the same #461 signal DriverInventoryService uses for ordinary drivers) means
    /// false; anything else, or a file this process couldn't read, means true. Null only when the
    /// service's ImagePath itself couldn't be resolved at all.</summary>
    public bool? IsThirdParty { get; init; }

    /// <summary>Friendly adapter name(s) this component is bound to, resolved from the Net class's
    /// own Connection\Name registry value where a match was found - the adapter's own DriverDesc is
    /// shown instead for any adapter that couldn't be matched by GUID.</summary>
    public List<string> BoundAdapters { get; init; } = new();
}

/// <summary>
/// #496 (Winsock half): one entry from `netsh winsock show catalog` - a Layered Service Provider
/// (LSP) or base provider registered in the Winsock catalog. IsThirdParty is based on whether the
/// provider DLL lives under %SystemRoot%\System32 and carries a Microsoft Corporation file-version
/// company name - the same signal DriverInventoryService's #461 third-party check already uses for
/// drivers, applied here to Winsock provider DLLs instead.
/// </summary>
public sealed class WinsockCatalogEntry
{
    public string EntryType { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? ProviderPath { get; init; }
    public string? CatalogEntryId { get; init; }
    public bool IsThirdParty { get; init; }
}
