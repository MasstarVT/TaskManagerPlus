using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace TaskManagerPlus.Services;

/// <summary>Round 14, #836: richer than the old plain "Signed"/"Unsigned"/"Unknown" string - a
/// trusted embedded Authenticode signature and a trusted catalog signature are now told apart from
/// a signature that exists but has a problem (expired cert / untrusted or revoked chain).</summary>
public enum SignatureVerification
{
    SignedEmbedded,
    SignedCatalog,
    UntrustedChain,
    Expired,
    Unsigned,
    Unknown,
}

/// <summary>#836/#837/#838: the richer result behind <see cref="SignatureCheckService.GetResult"/> -
/// verification outcome plus whatever signer-chain info was cheaply available. SubjectCn/IssuerCn/
/// ValidFrom/ValidTo/SelfSigned are all null/false for a catalog-signed file (see GetVerification's
/// remarks on why a full cert isn't available on that path) and for Unsigned/Unknown.</summary>
public sealed record SignatureResult(
    SignatureVerification Verification,
    string? SubjectCn,
    string? IssuerCn,
    DateTime? ValidFrom,
    DateTime? ValidTo,
    bool SelfSigned,
    bool IsSha1Signature)
{
    public static readonly SignatureResult UnknownResult = new(SignatureVerification.Unknown, null, null, null, null, false, false);
}

/// <summary>
/// Signature/trust check for a file path, cached per path (a signature check reads the file from
/// disk, and the same executable is often checked from more than one caller - several svchost.exe
/// processes in the Processes tab, or the same app.exe as both a running process and a startup
/// item). A ConcurrentDictionary since callers run this from more than one background thread
/// (ProcessMonitorService's poll timer, StartupViewModel's/SecurityViewModel's background scans).
///
/// #836 upgrade: the original implementation used only X509Certificate.CreateFromSignedFile
/// (embedded Authenticode signature only - no chain validation, no catalog awareness, so most
/// stock Windows system files showed as "Unsigned" since they rely on catalog signing instead of
/// an embedded one). This now calls WinVerifyTrust with WINTRUST_ACTION_GENERIC_VERIFY_V2 for a
/// real chain-validated embedded-signature check, and falls back to the CryptCATAdmin* catalog
/// APIs (the same primitive Sysinternals' Sigcheck uses for catalog-signature detection) when no
/// embedded signature is found at all - this is what actually fixes "svchost.exe shows Unsigned".
///
/// Catalog-signed detection here is a hash-membership check only (CryptCATAdminEnumCatalogFromHash),
/// not a full WTD_CHOICE_CATALOG re-verification of that catalog's own signature (which needs a
/// second, deeper WINTRUST_CATALOG_INFO-based WinVerifyTrust call) - a hash match only proves the
/// file's bytes are recorded in *some* system catalog. On Windows 10/11 that can include OS
/// package/servicing manifests as well as driver/code-signing catalogs, so this is corroborating
/// evidence, not a chain-validated verdict - same "quick flag, not a verdict" tradeoff as every
/// other heuristic in this app. In practice this only produces a false "signed" reading for content
/// that happens to be byte-identical to something Microsoft shipped, which is not a meaningful risk
/// for the executables/DLLs this service is actually asked about.
///
/// Safety: every native call is wrapped so a P/Invoke failure (missing export, marshaling problem,
/// unexpected OS behavior) falls back to the pre-#836 embedded-only check rather than crashing or
/// hanging the app - see ComputeResult. Revocation checking (which can call out to the network) is
/// OFF by default here (WTD_REVOKE_NONE) since GetResult runs in hot per-tick paths across every
/// process on the system; a revocation-aware variant exists separately
/// (<see cref="TryCheckRevocation"/>) for on-demand callers only (AutorunsService's Scan button),
/// run on an abandoned background thread with a hard timeout - the same "never let a native call
/// hang forever" pattern this app already uses for lsass module enumeration and the handle-table
/// walk.
///
/// #844: backed by a small JSON cache under AppPaths.SettingsDirectory
/// (signature-cache.json), keyed by "path|size|lastWriteTimeUtc" so a replaced binary at the same
/// path automatically invalidates and re-verifies. Loaded once lazily; writes are throttled to at
/// most once every 2 seconds so a burst of many new entries (a fresh Autoruns scan, a cold process
/// list) doesn't rewrite the file on every single one.
/// </summary>
public static class SignatureCheckService
{
    private static readonly ConcurrentDictionary<string, SignatureResult> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object PersistLock = new();
    private static Dictionary<string, PersistedEntry>? _persisted;
    private static DateTime _lastPersistSaveUtc = DateTime.MinValue;
    private static readonly TimeSpan PersistThrottle = TimeSpan.FromSeconds(2);

    private static string PersistPath => AppPaths.GetPath("signature-cache.json");

    // ---- Public API -------------------------------------------------------------------------

    /// <summary>Backward-compatible "Signed"/"Unsigned"/"Unknown" - every existing string-typed
    /// consumer (ProcessRow/StartupItem/AutorunEntry's SignatureStatus columns) keeps working
    /// unchanged. A signature that exists but has a chain/expiry problem (UntrustedChain/Expired)
    /// maps to "Unsigned" rather than "Signed" - old consumers already color "Unsigned" as a
    /// warning, and a distrusted/expired chain deserves that same visual flag, not a false-clean
    /// "Signed".</summary>
    public static string GetStatus(string? filePath)
    {
        var result = GetResult(filePath);
        return result.Verification switch
        {
            SignatureVerification.SignedEmbedded or SignatureVerification.SignedCatalog => "Signed",
            SignatureVerification.Unknown => "Unknown",
            _ => "Unsigned",
        };
    }

    /// <summary>#836: the richer verification outcome for new callers.</summary>
    public static SignatureVerification GetVerification(string? filePath) => GetResult(filePath).Verification;

    /// <summary>#837: signing certificate's subject/issuer CN and validity window, when cheaply
    /// available. For a catalog-signed file (no embedded cert to read), IssuerCn comes back as
    /// "Unknown (catalog-signed)" and the rest stay null - noted here rather than pretending a full
    /// chain was inspected.</summary>
    public static (string? SubjectCn, string? IssuerCn, DateTime? ValidFrom, DateTime? ValidTo, bool SelfSigned) GetSignerInfo(string? filePath)
    {
        var r = GetResult(filePath);
        return (r.SubjectCn, r.IssuerCn, r.ValidFrom, r.ValidTo, r.SelfSigned);
    }

    /// <summary>The full result record - verification outcome plus signer info plus the #838
    /// SHA-1-file-digest flag, all from one cached lookup.</summary>
    public static SignatureResult GetResult(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return SignatureResult.UnknownResult;

        string cacheKey = filePath;
        bool haveStat = false;
        try
        {
            var fi = new FileInfo(filePath);
            if (fi.Exists)
            {
                cacheKey = $"{filePath}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
                haveStat = true;
            }
        }
        catch
        {
            // Stat failed (denied, or a transient race) - fall back to a path-only cache key
            // below; the result for this lookup just won't be persisted to disk.
        }

        if (Cache.TryGetValue(cacheKey, out var cached)) return cached;

        if (haveStat)
        {
            var fromDisk = TryLoadPersisted(cacheKey);
            if (fromDisk is not null)
            {
                Cache[cacheKey] = fromDisk;
                return fromDisk;
            }
        }

        var computed = ComputeResult(filePath);
        Cache[cacheKey] = computed;
        if (haveStat) SavePersisted(cacheKey, computed);
        return computed;
    }

    /// <summary>#838: revocation-aware check, kept entirely separate from the cached hot-path
    /// GetResult above since it can make a network call. Runs on an abandoned background thread
    /// with a hard timeout so a network stall can never hang the caller - "couldn't check" (not a
    /// false "not revoked") is returned on timeout or on any native failure, per the
    /// "degrade to Unknown, never fabricate" rule. Only ever called from an explicit on-demand
    /// action (AutorunsService.Scan, itself behind the Security tab's "Scan" button), never from a
    /// polled path.</summary>
    public static (bool CouldCheck, bool Revoked) TryCheckRevocation(string filePath, TimeSpan timeout)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return (false, false);

        int hresult = 0;
        bool completed = false;
        var thread = new Thread(() =>
        {
            try
            {
                hresult = CallWinVerifyTrust(filePath, revocationCheck: true);
                completed = true;
            }
            catch
            {
                // Leave completed = false - reported as "couldn't check" below.
            }
        })
        { IsBackground = true };

        thread.Start();
        if (!thread.Join(timeout) || !completed)
        {
            // Timed out or failed - the thread is abandoned (background, dies with the process);
            // never block the caller waiting on a stuck native call.
            return (false, false);
        }

        if (hresult == CryptERevocationOffline || hresult == TrustETimeStamp)
            return (false, false); // offline/no timestamp - "couldn't check", not "not revoked"

        return (true, hresult == CertERevoked);
    }

    // ---- Computation --------------------------------------------------------------------------

    private static SignatureResult ComputeResult(string filePath)
    {
        try
        {
            return ComputeResultNative(filePath);
        }
        catch
        {
            // Any native P/Invoke failure at all (unexpected marshaling issue, missing export,
            // ...) - fall back to the pre-#836 embedded-only check rather than risk crashing or
            // hanging. This must never make the app less reliable than it was before #836.
            return ComputeResultLegacy(filePath);
        }
    }

    private static SignatureResult ComputeResultNative(string filePath)
    {
        int hresult = CallWinVerifyTrust(filePath, revocationCheck: false);

        if (hresult == 0)
        {
            var info = TryReadCertInfo(filePath);
            return info with { Verification = SignatureVerification.SignedEmbedded };
        }

        if (IsNoSignatureCode(hresult))
        {
            return TryIsCatalogSigned(filePath)
                ? new SignatureResult(SignatureVerification.SignedCatalog, null, "Unknown (catalog-signed)", null, null, false, false)
                : new SignatureResult(SignatureVerification.Unsigned, null, null, null, null, false, false);
        }

        if (hresult == CertEExpired)
        {
            var info = TryReadCertInfo(filePath);
            return info with { Verification = SignatureVerification.Expired };
        }

        if (IsUntrustedChainCode(hresult))
        {
            var info = TryReadCertInfo(filePath);
            return info with { Verification = SignatureVerification.UntrustedChain };
        }

        // An HRESULT this service doesn't specifically recognize - degrade to Unknown rather than
        // guess which bucket it belongs in.
        return SignatureResult.UnknownResult;
    }

    /// <summary>Pre-#836 behavior, preserved verbatim as the safety-net fallback.</summary>
    private static SignatureResult ComputeResultLegacy(string filePath)
    {
        try
        {
            using var cert = X509Certificate.CreateFromSignedFile(filePath);
            if (cert is null) return new SignatureResult(SignatureVerification.Unsigned, null, null, null, null, false, false);
            return ReadCertInfo(cert) with { Verification = SignatureVerification.SignedEmbedded };
        }
        catch (FileNotFoundException)
        {
            return SignatureResult.UnknownResult;
        }
        catch (UnauthorizedAccessException)
        {
            return SignatureResult.UnknownResult;
        }
        catch
        {
            // CreateFromSignedFile throws CryptographicException for a file with no embedded
            // signature at all - the expected, common case for an unsigned binary.
            return new SignatureResult(SignatureVerification.Unsigned, null, null, null, null, false, false);
        }
    }

    private static SignatureResult TryReadCertInfo(string filePath)
    {
        try
        {
            using var cert = X509Certificate.CreateFromSignedFile(filePath);
            return ReadCertInfo(cert);
        }
        catch
        {
            // No embedded cert to read (a catalog-signed-only file that still failed WinVerifyTrust
            // for some other reason, e.g. an expired/untrusted catalog entry) - degrade to the bare
            // verification outcome with no signer detail rather than throwing.
            return SignatureResult.UnknownResult;
        }
    }

    private static SignatureResult ReadCertInfo(X509Certificate cert)
    {
        using var cert2 = new X509Certificate2(cert);
        string? subject = NullIfEmpty(cert2.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        string? issuer = NullIfEmpty(cert2.GetNameInfo(X509NameType.SimpleName, forIssuer: true));
        bool selfSigned = string.Equals(cert2.Subject, cert2.Issuer, StringComparison.Ordinal);

        // #838: approximate SHA-1-file-digest flag - checks the certificate's own signature hash
        // algorithm (what the issuing CA used to sign the cert), the cheapest available signal,
        // not the Authenticode file-digest algorithm itself. Noted as an approximation per #838's
        // own guidance.
        bool isSha1 = cert2.SignatureAlgorithm.FriendlyName?.Contains("sha1", StringComparison.OrdinalIgnoreCase) == true
            || cert2.SignatureAlgorithm.Value is "1.2.840.113549.1.1.5" or "1.2.840.10040.4.3" or "1.3.14.3.2.29" or "1.2.840.10045.4.1";

        return new SignatureResult(SignatureVerification.Unknown, subject, issuer, cert2.NotBefore.ToUniversalTime(), cert2.NotAfter.ToUniversalTime(), selfSigned, isSha1);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // ---- WinVerifyTrust HRESULT classification --------------------------------------------

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int TrustESubjectNotTrusted = unchecked((int)0x800B0004);
    private const int CertEExpired = unchecked((int)0x800B0101);
    private const int CertEUntrustedRoot = unchecked((int)0x800B0109);
    private const int CertEChaining = unchecked((int)0x800B010A);
    private const int CertERevoked = unchecked((int)0x800B010C);
    private const int CryptERevocationOffline = unchecked((int)0x80092013);
    private const int TrustETimeStamp = unchecked((int)0x80096005);

    private static bool IsNoSignatureCode(int hr) =>
        hr == TrustENoSignature || hr == TrustESubjectFormUnknown || hr == TrustEProviderUnknown;

    /// <summary>#838: TRUST_E_SUBJECT_NOT_TRUSTED / CERT_E_UNTRUSTEDROOT / CERT_E_CHAINING /
    /// CERT_E_REVOKED all mean "there is a signature, but the chain doesn't check out" - surfaced
    /// as one UntrustedChain bucket rather than four separate enum values, since the actionable
    /// takeaway ("don't trust this chain") is the same for all four. Revocation checking is off by
    /// default (see class remarks), so CERT_E_REVOKED only appears here when a caller explicitly
    /// used a revocation-checking path.</summary>
    private static bool IsUntrustedChainCode(int hr) =>
        hr == TrustESubjectNotTrusted || hr == CertEUntrustedRoot || hr == CertEChaining || hr == CertERevoked;

    // ---- WinVerifyTrust P/Invoke ------------------------------------------------------------
    // Verified against real embedded-signed (explorer.exe/svchost.exe -> S_OK), catalog-signed-
    // only (notepad.exe/cmd.exe -> TRUST_E_NOSIGNATURE, then found via catalog lookup), and
    // non-PE (a plain text file -> TRUST_E_NOSIGNATURE, no catalog match by chance) files during
    // development - no crash, no hang, on a live Windows 11 machine.

    private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdRevokeWholechain = 1;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdSaferFlag = 0x100;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

    private static int CallWinVerifyTrust(string filePath, bool revocationCheck)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = filePath,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };

        IntPtr fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData = IntPtr.Zero,
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = revocationCheck ? WtdRevokeWholechain : WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = fileInfoPtr,
                dwStateAction = WtdStateActionVerify,
                hWVTStateData = IntPtr.Zero,
                pwszURLReference = IntPtr.Zero,
                dwProvFlags = WtdSaferFlag,
                dwUIContext = 0,
                pSignatureSettings = IntPtr.Zero,
            };

            int result;
            try
            {
                result = WinVerifyTrust(IntPtr.Zero, WinTrustActionGenericVerifyV2, ref data);
            }
            finally
            {
                // Always release the per-verify state WinVerifyTrust allocated, even if the verify
                // call itself threw - a leaked WVTStateData handle on every check would add up over
                // a long-running app.
                data.dwStateAction = WtdStateActionClose;
                try { WinVerifyTrust(IntPtr.Zero, WinTrustActionGenericVerifyV2, ref data); } catch { /* best-effort cleanup */ }
            }
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    // ---- Catalog signature lookup (CryptCATAdmin*) ------------------------------------------

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptCATAdminAcquireContext2(
        out IntPtr phCatAdmin, IntPtr pgSubsystem, string? pwszHashAlgorithm, IntPtr pStrongHashPolicy, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle(SafeFileHandle hFile, ref uint pcbHash, byte[]? pbHash, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr hCatAdmin, byte[] pbHash, uint cbHash, uint dwFlags, ref IntPtr phPrevCatInfo);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin, IntPtr hCatInfo, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);

    private static bool TryIsCatalogSigned(string filePath)
    {
        IntPtr hCatAdmin = IntPtr.Zero;
        IntPtr hCatInfo = IntPtr.Zero;
        try
        {
            if (!CryptCATAdminAcquireContext2(out hCatAdmin, IntPtr.Zero, null, IntPtr.Zero, 0))
                return false;

            using var handle = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            uint cbHash = 0;
            CryptCATAdminCalcHashFromFileHandle(handle, ref cbHash, null, 0); // size probe - expected to "fail" with the required size in cbHash
            if (cbHash == 0) return false;

            var hash = new byte[cbHash];
            if (!CryptCATAdminCalcHashFromFileHandle(handle, ref cbHash, hash, 0)) return false;

            IntPtr prev = IntPtr.Zero;
            hCatInfo = CryptCATAdminEnumCatalogFromHash(hCatAdmin, hash, cbHash, 0, ref prev);
            return hCatInfo != IntPtr.Zero;
        }
        catch
        {
            // Catalog probing failed for some other reason (locked file, odd handle state, ...) -
            // treat as "no catalog match" rather than letting this bubble up and force the whole
            // WinVerifyTrust result down to the legacy fallback.
            return false;
        }
        finally
        {
            if (hCatInfo != IntPtr.Zero) CryptCATAdminReleaseCatalogContext(hCatAdmin, hCatInfo, 0);
            if (hCatAdmin != IntPtr.Zero) CryptCATAdminReleaseContext(hCatAdmin, 0);
        }
    }

    // ---- #844: persistent JSON cache ---------------------------------------------------------

    private sealed class PersistedEntry
    {
        public string Verification { get; set; } = nameof(SignatureVerification.Unknown);
        public string? SubjectCn { get; set; }
        public string? IssuerCn { get; set; }
        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }
        public bool SelfSigned { get; set; }
        public bool IsSha1Signature { get; set; }

        public SignatureResult ToResult() =>
            new(Enum.TryParse<SignatureVerification>(Verification, out var v) ? v : SignatureVerification.Unknown,
                SubjectCn, IssuerCn, ValidFrom, ValidTo, SelfSigned, IsSha1Signature);
    }

    private static void EnsurePersistedLoaded()
    {
        if (_persisted is not null) return;
        lock (PersistLock)
        {
            _persisted ??= LoadPersistedFromDisk();
        }
    }

    private static Dictionary<string, PersistedEntry> LoadPersistedFromDisk()
    {
        try
        {
            if (File.Exists(PersistPath))
            {
                var json = File.ReadAllText(PersistPath);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, PersistedEntry>>(json);
                if (loaded is not null) return new Dictionary<string, PersistedEntry>(loaded, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Corrupt or unreadable cache file - fail silently to an empty cache (cold-scan
            // fallback), same as every other settings file in this app.
        }
        return new Dictionary<string, PersistedEntry>(StringComparer.OrdinalIgnoreCase);
    }

    private static SignatureResult? TryLoadPersisted(string cacheKey)
    {
        EnsurePersistedLoaded();
        lock (PersistLock)
        {
            return _persisted!.TryGetValue(cacheKey, out var entry) ? entry.ToResult() : null;
        }
    }

    private static void SavePersisted(string cacheKey, SignatureResult result)
    {
        EnsurePersistedLoaded();
        lock (PersistLock)
        {
            _persisted![cacheKey] = new PersistedEntry
            {
                Verification = result.Verification.ToString(),
                SubjectCn = result.SubjectCn,
                IssuerCn = result.IssuerCn,
                ValidFrom = result.ValidFrom,
                ValidTo = result.ValidTo,
                SelfSigned = result.SelfSigned,
                IsSha1Signature = result.IsSha1Signature,
            };

            var now = DateTime.UtcNow;
            if (now - _lastPersistSaveUtc < PersistThrottle) return;
            _lastPersistSaveUtc = now;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PersistPath)!);
                File.WriteAllText(PersistPath, JsonSerializer.Serialize(_persisted));
            }
            catch
            {
                // Best-effort - if we can't persist, the app still works for this session (same
                // as every other settings file's Save()).
            }
        }
    }
}
