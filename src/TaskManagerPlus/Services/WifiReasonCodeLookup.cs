namespace TaskManagerPlus.Services;

/// <summary>
/// Item #542: decodes the reason/status codes carried by WLAN-AutoConfig operational events
/// (#541's 8003 disconnect and 11004/11005 association-phase events) into readable text.
///
/// The IEEE 802.11 reason and status codes below are a small, stable, publicly-documented part of
/// the 802.11 standard itself (not a Microsoft-internal enum) - this table covers the values most
/// commonly seen in practice, not the full spec. Windows' own WLAN_REASON_CODE enum (attached to
/// event 8003's "Reason" field) is a much larger, undocumented-as-a-stable-contract Microsoft-
/// internal numbering scheme spanning several driver/security subsystems, so this deliberately
/// doesn't attempt to hardcode it wholesale - guessing at hex offsets from memory and presenting
/// them as fact would be exactly the kind of fabrication CLAUDE.md's "degrade, never fabricate"
/// rule exists to prevent. Instead, a numeric code is looked up against the 802.11 table first
/// (accurate when it hits - Windows often does forward the raw 802.11 deauth/status code
/// unchanged), then a keyword search over the event's own formatted message text - the same
/// "best-effort classification of real event data, not a fabricated ID mapping" tradeoff
/// DhcpEventLogService/DnsEventLogService already take for their own vendor-specific text. Neither
/// numeric code nor keyword rule anywhere hardcodes text for an event that wasn't actually there.
/// </summary>
public static class WifiReasonCodeLookup
{
    // IEEE 802.11 reason codes - attached to deauthentication/disassociation events (roughly #8003,
    // and the reason side of #11004/#11005).
    private static readonly Dictionary<int, string> Dot11ReasonCodes = new()
    {
        [1] = "Unspecified reason",
        [2] = "Previous authentication is no longer valid",
        [3] = "Deauthenticated because the sending station is leaving (or has left) the network",
        [4] = "Disassociated due to inactivity",
        [5] = "Deauthenticated because the AP is overloaded and cannot handle any more associated stations",
        [6] = "Class 2 frame received from a nonauthenticated station",
        [7] = "Class 3 frame received from a nonassociated station",
        [8] = "Disassociated because the sending station is leaving (or has left) the network",
        [9] = "Station requesting (re)association is not authenticated with the responding station",
        [10] = "Disassociated - the Power Capability element is unacceptable",
        [11] = "Disassociated - the Supported Channels element is unacceptable",
        [13] = "Invalid information element",
        [14] = "Message integrity code (MIC) failure",
        [15] = "4-way handshake timed out",
        [16] = "Group key handshake timed out",
        [17] = "Information element in the 4-way handshake differs from the (re)association request/probe response",
        [18] = "Invalid group cipher",
        [19] = "Invalid pairwise cipher",
        [20] = "Invalid AKMP",
        [21] = "Unsupported RSN information element version",
        [22] = "Invalid RSN information element capabilities",
        [23] = "IEEE 802.1X authentication failed",
        [24] = "Cipher suite rejected because of the network's security policy",
        [33] = "Disassociated for a QoS-related reason",
        [34] = "Disassociated - insufficient bandwidth for this QoS traffic stream",
        [39] = "Disassociated - timeout (missing acknowledgements)",
        [46] = "Deauthenticated because the network is set up for direct-link (peer-to-peer) communication only",
    };

    // IEEE 802.11 status codes - attached to (re)association result events (roughly #11004/#11005);
    // 0 is success, everything else is a specific reason the association attempt was refused.
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

    /// <summary>Keyword rules over the event's own formatted description text - the same
    /// best-effort classification technique DhcpEventLogService.Categorize already uses, for the
    /// Microsoft-specific higher-level reasons (user-initiated, roam, radio off, ...) the plain
    /// 802.11 tables above can't express. First matching rule wins.</summary>
    private static readonly (string[] AnyOf, string Text)[] KeywordRules =
    {
        (new[] { "user has requested", "manually disabled", "user requested a disconnect", "user disconnect" }, "Explicitly disconnected by the user"),
        (new[] { "roam" }, "Roamed to a better AP"),
        (new[] { "unable to handle", "overload" }, "The AP reported it is overloaded"),
        (new[] { "radio state", "radio was turned off", "radio off" }, "The Wi-Fi radio was turned off"),
        (new[] { "out of range", "signal quality", "lost the signal" }, "Signal was lost or the AP went out of range"),
        (new[] { "timed out", "timeout" }, "Timed out waiting for a response"),
        (new[] { "profile" }, "The connection profile changed or was removed"),
        (new[] { "security", "authentication failed", "802.1x" }, "A security/authentication handshake failed"),
    };

    /// <summary>Decodes a raw reason or status code plus the event's own formatted message into one
    /// readable line. Never fabricates: an unrecognized numeric code and no keyword match degrades
    /// to a plain "code N (not decoded)" rather than an invented explanation.</summary>
    public static string? Decode(string? rawCode, bool isStatusCode, string message)
    {
        if (!string.IsNullOrWhiteSpace(rawCode) && int.TryParse(rawCode, out var code))
        {
            var table = isStatusCode ? Dot11StatusCodes : Dot11ReasonCodes;
            if (table.TryGetValue(code, out var text))
                return $"{text} (802.11 {(isStatusCode ? "status" : "reason")} code {code})";
        }

        var keywordHit = ClassifyByKeyword(message);
        if (keywordHit is not null) return keywordHit;

        if (!string.IsNullOrWhiteSpace(rawCode))
            return $"Code {rawCode} (not in the lookup table - see Event Viewer for full detail)";

        return null;
    }

    private static string? ClassifyByKeyword(string message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        foreach (var (anyOf, text) in KeywordRules)
            if (anyOf.Any(k => message.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return text;
        return null;
    }
}
