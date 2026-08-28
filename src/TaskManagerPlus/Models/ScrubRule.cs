namespace TaskManagerPlus.Models;

/// <summary>#984/#985: what a <see cref="ScrubRule"/> matches. The built-in kinds resolve their
/// actual match text from live environment state at scrub time (the current username, machine
/// name, ...) rather than a fixed pattern baked into scrub-rules.json, since that text is
/// per-machine and per-session; <see cref="MacAddress"/>/<see cref="ProductKey"/> are the two
/// built-ins that really are fixed regexes. <see cref="CustomLiteral"/> is #985's user-extensible
/// "also redact this text" row - an exact (case-insensitive) text match, not a regex, since a
/// simple add-row is the legitimate, real implementation #985 asks for rather than a full pattern
/// editor.</summary>
public enum ScrubRuleKind
{
    Username,
    MachineName,
    Domain,
    WifiSsid,
    MacAddress,
    ProductKey,
    CustomLiteral,
}

/// <summary>
/// #985: one entry in the scrub dictionary (AppPaths.SettingsDirectory\scrub-rules.json) - the
/// built-in patterns #984 lists, plus whatever the user has added via the review screen's
/// "also redact this text" row. <see cref="Enabled"/> lets a built-in rule be turned off (e.g. a
/// machine name that's also a common English word the user doesn't want redacted everywhere)
/// without deleting and later having to re-add it.
/// </summary>
public sealed class ScrubRule
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required ScrubRuleKind Kind { get; init; }

    /// <summary>Stable placeholder prefix for this rule's matches - "USER", "MAC", "HOST", ...
    /// First-seen-gets-next-number within a run (USER1, USER2, ...), per #984.</summary>
    public required string PlaceholderPrefix { get; init; }

    public bool Enabled { get; set; } = true;

    /// <summary>Only meaningful for <see cref="ScrubRuleKind.CustomLiteral"/> - the exact text to
    /// redact wherever it appears.</summary>
    public string? LiteralValue { get; init; }

    public bool IsBuiltIn => Kind != ScrubRuleKind.CustomLiteral;
}

/// <summary>#985: the persisted scrub-rules.json contents - just the rule list. Loading seeds the
/// built-in rules on first run (or on a missing/corrupt file - same fail-silently-to-defaults
/// convention every other settings file in this app follows); ScrubRulesService.Load also merges
/// in any built-in rule kind a saved file predates, so an app update that adds a new built-in
/// pattern doesn't silently vanish from an existing user's file.</summary>
public sealed class ScrubRuleSet
{
    public List<ScrubRule> Rules { get; set; } = new();
}

/// <summary>#984: one row in the scrub review screen - every distinct value a rule matched,
/// collapsed to its assigned placeholder and an occurrence count across every text artifact
/// scrubbed this run. This is the review itself: nothing is finalized into the zip until the user
/// has seen this list.</summary>
public sealed class ScrubReplacementSummary
{
    public string RuleLabel { get; init; } = string.Empty;
    public string Placeholder { get; init; } = string.Empty;
    public string OriginalValue { get; init; } = string.Empty;
    public int OccurrenceCount { get; set; }
}
