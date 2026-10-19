namespace TaskManagerPlus.Services;

/// <summary>
/// Item #542: turns the reason carried by a WLAN-AutoConfig operational event (#541's 8003
/// disconnect and the 8002/11002/11006 failure events) into one readable line.
///
/// What those events actually carry (WLAN-AutoConfig manifest): Windows' own reason text (8003
/// "Reason", 8002/11002 "FailureReason", 11006 "ReasonText"), a WLAN_REASON_CODE ReasonCode, and -
/// on 11002 only - a real IEEE 802.11 Dot11StatusCode. WLAN_REASON_CODE is Windows' own large,
/// subsystem-banded numbering (not an 802.11 code), so it is never looked up in an 802.11 table -
/// doing that would print a confident, wrong explanation. The 802.11 status table below is a small,
/// stable part of the standard and is used only for the 11002 field that really holds one. Keyword
/// rules classify the reason text alone, never the whole templated message (whose labels, e.g.
/// "Profile Name:", would match a rule on every event), and an unmatched reason is shown verbatim -
/// it's Windows' own wording, localized on non-English installs.
/// </summary>
public static class WifiReasonCodeLookup
{
    // IEEE 802.11 status codes - attached to (re)association results (event 11002's
    // Dot11StatusCode); 0 is success, everything else is a specific reason the attempt was refused.
    private static readonly Dictionary<int, string> Dot11StatusCodes = new()
    {
        [0] = "Successful",
        [1] = "Unspecified failure",
        [10] = "Cannot support all requested capabilities",
        [11] = "Reassociation denied - the AP couldn't confirm the prior association exists",
        [12] = "Association denied for a reason outside the 802.11 standard (often a vendor-specific AP policy)",
        [13] = "The AP doesn't support the requested authentication algorithm",
        [14] = "Authentication frame received out of the expected sequence",
        [15] = "Authentication rejected - challenge failure",
        [16] = "Authentication rejected - timed out waiting for the next frame",
        [17] = "Association denied - the AP is unable to handle additional associated stations",
        [18] = "Association denied - this station doesn't support all of the AP's required data rates",
        [19] = "Association denied - short preamble not supported",
        [22] = "Association denied - Spectrum Management capability required",
        [23] = "Association denied - unacceptable Power Capability",
        [24] = "Association denied - unacceptable Supported Channels",
        [25] = "Association denied - short slot time not supported",
        [40] = "Invalid information element",
        [41] = "Invalid group cipher",
        [42] = "Invalid pairwise cipher",
        [43] = "Invalid AKMP",
        [44] = "Unsupported RSN information element version",
        [45] = "Invalid RSN information element capabilities",
        [46] = "Cipher suite rejected because of the network's security policy",
    };

    /// <summary>Keyword rules over the event's reason text only - the same best-effort
    /// classification technique DhcpEventLogService.Categorize already uses. First matching rule
    /// wins; English-only, so a localized reason simply falls through to being shown as-is.</summary>
    private static readonly (string[] AnyOf, string Text)[] KeywordRules =
    {
        (new[] { "user has requested", "manually disabled", "user requested a disconnect", "user disconnect", "disconnected by the user" }, "Explicitly disconnected by the user"),
        (new[] { "roam" }, "Roamed to a better AP"),
        (new[] { "unable to handle", "overload" }, "The AP reported it is overloaded"),
        (new[] { "radio state", "radio was turned off", "radio off" }, "The Wi-Fi radio was turned off"),
        (new[] { "out of range", "signal quality", "lost the signal" }, "Signal was lost or the AP went out of range"),
        (new[] { "timed out", "timeout" }, "Timed out waiting for a response"),
        (new[] { "profile" }, "The connection profile changed or was removed"),
        (new[] { "security", "authentication failed", "802.1x" }, "A security/authentication handshake failed"),
    };

    /// <summary>One readable line for a disconnect/failure event: the decoded 802.11 status code
    /// when <paramref name="dot11StatusRaw"/> holds a known non-zero one, else a keyword
    /// classification of <paramref name="reasonText"/>, else that text verbatim. Null when the event
    /// carried neither.</summary>
    public static string? Describe(string? reasonText, string? dot11StatusRaw)
    {
        string? reason = string.IsNullOrWhiteSpace(reasonText) ? null : reasonText.Trim();

        if (int.TryParse(dot11StatusRaw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var status) && status != 0)
        {
            string decoded = Dot11StatusCodes.TryGetValue(status, out var text)
                ? $"{text} (802.11 status code {status})"
                : $"802.11 status code {status} (not in the lookup table)";
            return reason is null ? decoded : $"{decoded} - {reason}";
        }

        if (reason is null) return null;
        return ClassifyByKeyword(reason) ?? reason;
    }

    private static string? ClassifyByKeyword(string reason)
    {
        foreach (var (anyOf, text) in KeywordRules)
            if (anyOf.Any(k => reason.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return text;
        return null;
    }
}
