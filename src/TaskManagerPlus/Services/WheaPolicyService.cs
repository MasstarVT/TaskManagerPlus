using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #491: reads WHEA's own hardware-error-reporting policy configuration from the registry - the
/// documented location (Microsoft's WDK "WHEA Policy Settings" topic) is
/// HKLM\SYSTEM\CurrentControlSet\Control\WHEA\Policy, which configures WHEA's Predictive Failure
/// Analysis (PFA) behavior for ECC memory: whether WHEA is allowed to take a failing memory page
/// offline (DisableOffline), and whether PFA monitoring itself is disabled outright
/// (MemPfaDisable) - the closest real, documented "is hardware error handling disabled" switch
/// this app can read from the registry. \WHEA\Policies (plural) is also checked, on the chance a
/// specific machine (or a future Windows version) stores additional configuration there - this app
/// doesn't have a verified documented schema for that location, so its contents are shown as plain
/// name/value pairs rather than given an app-supplied meaning it can't back up.
///
/// Critically, this can only ever prove whether a Windows-side *policy override* is set. It can't
/// see whether a specific hardware error *source* is enabled at the firmware/ACPI level - that's
/// owned by the platform's ACPI HEST table, not the registry - so an all-clear reading here doesn't
/// guarantee hardware errors are actually being logged (see the UI text this backs).
/// </summary>
public static class WheaPolicyService
{
    private const string PolicyKeyPath = @"SYSTEM\CurrentControlSet\Control\WHEA\Policy";
    private const string PoliciesKeyPath = @"SYSTEM\CurrentControlSet\Control\WHEA\Policies";

    public static Task<WheaPolicyInfo> ReadAsync() => Task.Run(Read);

    private static WheaPolicyInfo Read()
    {
        var values = new List<WheaPolicyValue>();
        bool policyFound = false;
        bool policiesFound = false;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyKeyPath);
            if (key is not null)
            {
                policyFound = true;
                foreach (var name in key.GetValueNames())
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    if (key.GetValue(name) is not { } raw) continue;
                    values.Add(DescribeDocumentedValue(name, raw));
                }
            }
        }
        catch
        {
            // Access denied/unavailable - degrade to "key not found", the same as a machine that
            // genuinely has no override configured.
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PoliciesKeyPath);
            if (key is not null)
            {
                policiesFound = true;
                foreach (var name in key.GetValueNames())
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    if (key.GetValue(name) is not { } raw) continue;
                    values.Add(new WheaPolicyValue { Name = name, ValueText = raw.ToString() ?? string.Empty });
                }

                // Per-error-source override subkeys, if this Windows version/machine uses this
                // location that way - shown as raw name/value pairs, same reasoning as above.
                foreach (var subName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = key.OpenSubKey(subName);
                        if (sub is null) continue;
                        foreach (var name in sub.GetValueNames())
                        {
                            if (sub.GetValue(name) is not { } raw) continue;
                            values.Add(new WheaPolicyValue
                            {
                                Name = string.IsNullOrEmpty(name) ? subName : $@"{subName}\{name}",
                                ValueText = raw.ToString() ?? string.Empty,
                            });
                        }
                    }
                    catch
                    {
                        // One malformed/inaccessible subkey shouldn't stop the rest of the sweep.
                    }
                }
            }
        }
        catch
        {
            // Access denied/unavailable - degrade to "key not found".
        }

        return new WheaPolicyInfo
        {
            PolicyKeyFound = policyFound,
            PoliciesKeyFound = policiesFound,
            Values = values,
        };
    }

    /// <summary>Attaches a plain-English description (and flags it as concerning) for the values
    /// Microsoft's WDK "WHEA Policy Settings" documentation actually names - everything else under
    /// \Policy is still shown, just without an app-supplied explanation.</summary>
    private static WheaPolicyValue DescribeDocumentedValue(string name, object raw)
    {
        string text = raw.ToString() ?? string.Empty;
        int? asInt = raw is int i ? i : (int.TryParse(text, out var parsed) ? parsed : null);

        return name switch
        {
            "MemPfaDisable" => new WheaPolicyValue
            {
                Name = name,
                ValueText = text,
                Description = asInt is > 0
                    ? "Predictive Failure Analysis for ECC memory is disabled - WHEA will not proactively take a failing memory page offline."
                    : "Predictive Failure Analysis for ECC memory is enabled (default).",
                IsConcerning = asInt is > 0,
            },
            "DisableOffline" => new WheaPolicyValue
            {
                Name = name,
                ValueText = text,
                Description = asInt is > 0
                    ? "WHEA is not allowed to take failing hardware components offline via Predictive Failure Analysis."
                    : "WHEA can take failing components offline via Predictive Failure Analysis (default).",
                IsConcerning = asInt is > 0,
            },
            "MemPfaThreshold" => new WheaPolicyValue
            {
                Name = name, ValueText = text,
                Description = "Maximum corrected errors allowed on an ECC memory page before WHEA takes it offline.",
            },
            "MemPfaPageCount" => new WheaPolicyValue
            {
                Name = name, ValueText = text,
                Description = "Maximum number of ECC memory pages WHEA monitors for Predictive Failure Analysis at once.",
            },
            "MemPfaTimeout" => new WheaPolicyValue
            {
                Name = name, ValueText = text,
                Description = "How long (seconds) an ECC memory page is monitored before Predictive Failure Analysis stops tracking it.",
            },
            "MemPersistOffline" => new WheaPolicyValue
            {
                Name = name, ValueText = text,
                Description = "Whether memory pages WHEA took offline persist across a restart.",
            },
            _ => new WheaPolicyValue { Name = name, ValueText = text },
        };
    }
}
