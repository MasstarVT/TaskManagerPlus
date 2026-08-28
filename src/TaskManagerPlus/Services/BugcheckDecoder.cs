using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 15, items 28-37: builds a BugcheckDecodedInfo from a bugcheck code + its raw parameter
/// strings - the one place every "Minidumps"/"Dump analysis" detail expander pulls its labelled
/// parameter rows, guidance text, and per-code sub-lines from (see BugcheckDecodedInfo's own
/// remarks on why this is shared between the event-log path and the binary-parse path). Pure
/// decoding over data already read/parsed elsewhere (EventLogService's BugCheck-1001 parse,
/// MinidumpParserService's binary header/stream parse) - this does no event-log or file I/O of
/// its own except item #35's pool-tag resolution (PoolTagLookup), which is why Decode should
/// always be called off the UI thread.
/// </summary>
public static class BugcheckDecoder
{
    public static BugcheckDecodedInfo Decode(string? bugcheckCode, IReadOnlyList<string>? parameters)
    {
        parameters ??= Array.Empty<string>();

        if (!BugcheckHex.TryParseCode(bugcheckCode, out var code))
            return new BugcheckDecodedInfo { ParameterRows = BuildGenericRows(parameters) };

        var labels = BugcheckParameterLookup.GetLabels(code);
        var rows = new List<BugcheckParameterRow>();
        for (int i = 0; i < parameters.Count; i++)
        {
            var label = i < labels.Length ? labels[i] : new BugcheckParamLabel($"Parameter {i + 1}");
            rows.Add(new BugcheckParameterRow
            {
                Label = label.Text,
                Value = parameters[i],
                StatusText = label.IsStatusCode ? NtStatusLookup.TryDescribeName(parameters[i]) : null,
            });
        }

        string? poolTag = null;
        string? poolTagRaw = null;
        if (PoolTagLookup.AppliesTo(code))
        {
            var tag = PoolTagLookup.TryExtractTag(parameters);
            if (tag is not null)
            {
                poolTagRaw = tag;
                var (driver, source) = PoolTagLookup.Resolve(tag);
                poolTag = driver is not null
                    ? $"Pool tag '{tag}' — likely {driver} ({source}, best-effort match)"
                    : $"Pool tag '{tag}' — owning driver not identified (best-effort, no pooltag.txt or driver-binary match found)";
            }
        }

        return new BugcheckDecodedInfo
        {
            ParameterRows = rows,
            Guidance = BugcheckGuidanceLookup.TryGetGuidance(bugcheckCode),
            DpcWatchdogSubtypeText = code == 0x00000133 ? DescribeDpcWatchdogSubtype(parameters) : null,
            DriverPowerStateSubcodeText = code == 0x0000009F ? DescribeDriverPowerStateSubcode(parameters) : null,
            PoolTagText = poolTag,
            PoolTagRaw = poolTagRaw,
            FaultAddressClassification = IsFaultAddressCode(code) && parameters.Count > 0
                ? FaultAddressClassifier.Classify(parameters[0])
                : null,
            // Round 19, item 85: 0xC4/0xC9/0xE6's own Parameter-1 subcode meaning.
            VerifierViolationText = VerifierViolationLookup.Describe(code, parameters),
        };
    }

    private static bool IsFaultAddressCode(uint code) => code is 0x0000000A or 0x000000D1 or 0x00000050;

    private static List<BugcheckParameterRow> BuildGenericRows(IReadOnlyList<string> parameters)
    {
        var rows = new List<BugcheckParameterRow>();
        for (int i = 0; i < parameters.Count; i++)
            rows.Add(new BugcheckParameterRow { Label = $"Parameter {i + 1}", Value = parameters[i] });
        return rows;
    }

    /// <summary>#32: 0x133 DPC_WATCHDOG_VIOLATION's own parameter 1 distinguishes a single DPC/
    /// ISR that ran too long (0) from the system spending too long at DISPATCH_LEVEL overall (1) -
    /// two different culprits, the latter classically an old storage-controller driver.</summary>
    private static string? DescribeDpcWatchdogSubtype(IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 0 || !BugcheckHex.TryParse(parameters[0], out var subtype)) return null;
        return subtype switch
        {
            0 => "Subtype: a single DPC (or ISR) ran far longer than allowed — look at the blamed/loaded driver below, not necessarily storage.",
            1 => "Subtype: the system spent too long at DISPATCH_LEVEL overall (cumulative) — the classic cause is an old/buggy storage-controller driver (AHCI/RAID/NVMe) rather than one single offender.",
            _ => $"Subtype: unrecognized value {subtype}.",
        };
    }

    /// <summary>#33: 0x9F DRIVER_POWER_STATE_FAILURE's parameter 1 subcode. The sleep/resume
    /// cross-reference (whether this crash happened while entering/leaving sleep) is computed
    /// separately by EventLogService, which has the crash's own timestamp - this only decodes the
    /// raw subcode number.</summary>
    private static string? DescribeDriverPowerStateSubcode(IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 0 || !BugcheckHex.TryParse(parameters[0], out var subcode)) return null;
        return subcode switch
        {
            1 => "Subcode 1: a driver failed to complete a power IRP within the allowed time.",
            2 => "Subcode 2: an IRP queued to a device stack never completed.",
            3 => "Subcode 3: a device power-down/up request failed.",
            _ => $"Subcode {subcode} — see the blamed driver/analysis below for which device stack.",
        };
    }
}
