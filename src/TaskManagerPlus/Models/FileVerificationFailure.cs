namespace TaskManagerPlus.Models;

/// <summary>#333: one file that failed a full-byte read verification pass - turns an abstract
/// bad-sector count into a concrete "this file is already gone" list.</summary>
public sealed record FileVerificationFailure(string Path, string Error);
