using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 19, #890: certificate store anomalies - enumerates the LocalMachine/CurrentUser Root
/// (trusted root CAs) and CA (intermediate) stores looking for TLS-interception-product-looking
/// roots, self-signed roots, expired-but-still-trusted roots, and weak keys/signature hashes.
/// Disallowed store contents are reported as a plain count (that store IS Windows' own "already
/// flagged" list, so it needs no further analysis here, per #890's own text). Inspection only - no
/// removal action; "Open Certificate Manager" launches certlm.msc for the user to act themselves.
///
/// "Installed recently" is deliberately NOT computed: X509Certificate2 exposes NotBefore/NotAfter
/// (the CERTIFICATE's own validity window), not when Windows added it to the local store - there is
/// no reliable API for that, so this degrades honestly to "not available" rather than substituting
/// NotBefore and calling it an install date, per #890's own explicit instruction not to do that.
///
/// Self-signed roots: EVERY self-signed root is flagged "worth reviewing" (the conservative option
/// #890 explicitly allows, chosen over building a curated allowlist of "known-common" self-signed
/// roots that would inevitably go stale) - most machines only have a small, genuinely reviewable
/// number of these, so over-flagging here costs little.
/// </summary>
public static class CertificateStoreAuditService
{
    public sealed record CertificateReviewRow(
        string StoreLocation, string StoreName, string Subject, string Issuer, string Thumbprint,
        DateTime NotBefore, DateTime NotAfter, IReadOnlyList<string> Reasons, FindingSeverity HighestSeverity)
    {
        public string ReasonsText => string.Join("; ", Reasons);
    }

    // A reasonable curated substring list, not exhaustive - checked against the certificate's
    // Subject, case-insensitively - per #890's own explicit "not exhaustive" allowance.
    private static readonly string[] InterceptionProductHints =
    {
        "proxy", "filter", "inspect", "ssl inspection", "fortinet", "zscaler", "netskope",
        "cisco umbrella", "kaspersky", "avast", "avg",
    };

    public static (List<CertificateReviewRow> ReviewRows, int DisallowedCount, List<SecurityFinding> Findings) Scan()
    {
        var rows = new List<CertificateReviewRow>();
        var findings = new List<SecurityFinding>();

        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            ScanOneStore(location, StoreName.Root, rows, findings);
            ScanOneStore(location, StoreName.CertificateAuthority, rows, findings);
        }

        int disallowedCount = 0;
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            disallowedCount += CountStore(location, StoreName.Disallowed);
        }

        return (rows, disallowedCount, findings);
    }

    private static void ScanOneStore(StoreLocation location, StoreName storeName, List<CertificateReviewRow> rows, List<SecurityFinding> findings)
    {
        X509Store? store = null;
        try
        {
            store = new X509Store(storeName, location);
            store.Open(OpenFlags.ReadOnly);

            foreach (var cert in store.Certificates)
            {
                try
                {
                    var reasons = new List<string>();
                    var severity = FindingSeverity.Info;

                    if (InterceptionProductHints.Any(h => cert.Subject.Contains(h, StringComparison.OrdinalIgnoreCase)))
                    {
                        reasons.Add("Subject looks like a TLS-interception/proxy product");
                        severity = Max(severity, FindingSeverity.High);
                    }

                    bool selfSigned = cert.Subject == cert.Issuer;
                    if (selfSigned)
                    {
                        reasons.Add("Self-signed root");
                        severity = Max(severity, FindingSeverity.Low);
                    }

                    if (cert.NotAfter < DateTime.Now)
                    {
                        reasons.Add($"Expired {cert.NotAfter:d} but still present in the trusted store");
                        severity = Max(severity, FindingSeverity.Medium);
                    }

                    if (IsWeakKeyOrHash(cert, out var weakReason))
                    {
                        reasons.Add(weakReason);
                        severity = Max(severity, FindingSeverity.Medium);
                    }

                    if (reasons.Count == 0) continue;

                    rows.Add(new CertificateReviewRow(
                        location.ToString(), storeName.ToString(), cert.Subject, cert.Issuer,
                        cert.Thumbprint, cert.NotBefore, cert.NotAfter, reasons, severity));

                    findings.Add(new SecurityFinding
                    {
                        Severity = severity,
                        Title = $"Certificate store: {ShortSubject(cert.Subject)} ({location}\\{storeName})",
                        Reason = $"{string.Join("; ", reasons)}. Thumbprint {cert.Thumbprint}, valid {cert.NotBefore:d} - {cert.NotAfter:d}. Quick flag, not a verdict - many legitimate enterprise/VPN/dev-tool root CAs are self-signed.",
                        Path = $@"{location}\{storeName}\{cert.Thumbprint}",
                        WhatDisablingDoes = "If you don't recognize this certificate or its purpose, remove it via certlm.msc (Local Machine) or certmgr.msc (Current User) - this app makes no removal changes itself.",
                    });
                }
                finally
                {
                    cert.Dispose();
                }
            }
        }
        catch
        {
            // Store unavailable/denied - whatever was gathered before the failure stands; no crash.
        }
        finally
        {
            store?.Close();
        }
    }

    private static int CountStore(StoreLocation location, StoreName storeName)
    {
        try
        {
            using var store = new X509Store(storeName, location);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates.Count;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsWeakKeyOrHash(X509Certificate2 cert, out string reason)
    {
        try
        {
            using var rsa = cert.GetRSAPublicKey();
            if (rsa is not null && rsa.KeySize < 2048)
            {
                reason = $"RSA key is only {rsa.KeySize} bits (weaker than 2048)";
                return true;
            }
        }
        catch { /* not an RSA key, or the key couldn't be read - fall through to the hash check */ }

        try
        {
            if (cert.SignatureAlgorithm.FriendlyName?.Contains("sha1", StringComparison.OrdinalIgnoreCase) == true)
            {
                reason = "Signed using SHA-1 (deprecated, collision-weak hash)";
                return true;
            }
        }
        catch { /* leave reason empty below */ }

        reason = string.Empty;
        return false;
    }

    private static FindingSeverity Max(FindingSeverity a, FindingSeverity b) => (FindingSeverity)Math.Max((int)a, (int)b);

    private static string ShortSubject(string subject)
    {
        // Pull just the CN= component when present, since a full Subject DN is often long and
        // noisy for a title line - falls back to the whole string if there's no CN.
        var cnIdx = subject.IndexOf("CN=", StringComparison.OrdinalIgnoreCase);
        if (cnIdx < 0) return subject;
        var rest = subject[(cnIdx + 3)..];
        var commaIdx = rest.IndexOf(',');
        return commaIdx > 0 ? rest[..commaIdx] : rest;
    }
}
