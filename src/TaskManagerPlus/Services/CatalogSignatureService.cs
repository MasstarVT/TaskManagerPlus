using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace TaskManagerPlus.Services;

/// <summary>
/// #454: catalog-aware driver signature verification - the gap CLAUDE.md and
/// SignatureCheckService's own remarks call out explicitly ("only sees embedded signatures... a
/// full WinVerifyTrust chain-and-catalog check needs native interop this app doesn't otherwise take
/// on"). Most in-box Windows kernel drivers are signed via a system catalog (a .cat file listing
/// file hashes) rather than an embedded Authenticode signature, so SignatureCheckService alone
/// mis-reports the large majority of drivers as "Unsigned". This does the real thing:
///   1. CryptCATAdminCalcHashFromFileHandle - hash the driver file the same way Windows itself does
///      for catalog membership lookups.
///   2. CryptCATAdminEnumCatalogFromHash - find which system catalog (if any) lists that hash.
///   3. WinVerifyTrust with WTD_CHOICE_CATALOG - ask Windows' own trust provider to validate the
///      catalog's signature chain for that member, exactly like Driver Signature Enforcement does
///      at load time.
///   4. Falls back to WinVerifyTrust with WTD_CHOICE_FILE (embedded Authenticode) when no catalog
///      membership is found at all - still a real trust decision, not just "a certificate blob
///      exists" the way the legacy X509Certificate.CreateFromSignedFile check is.
///
/// Revocation checking is deliberately skipped (fdwRevocationChecks = WTD_REVOKE_NONE) so this
/// never makes a network call on its own. Every native call is wrapped to degrade to "Unknown"
/// rather than throw - a locked-down system, a missing catalog, or an SDK/OS quirk in a struct
/// layout are all real, expected conditions here, the same tier as this app's other native-interop
/// services (KernelModuleService, HandleInspectionService, ...).
///
/// "Quick flag, not a verdict" applies here just as much as it does to SignatureCheckService: this
/// tells you what Windows' own trust provider concluded about one file at one point in time, not a
/// guarantee the driver is safe or malicious.
/// </summary>
public static class CatalogSignatureService
{
    public sealed record CatalogSignatureResult(string Status, string? SignerName, bool IsWhql, bool IsCatalogSigned);

    private static readonly CatalogSignatureResult UnknownResult = new("Unknown", null, false, false);

    /// <summary>Microsoft's own catalog-signing certificate for WHQL-certified drivers (#457) - the
    /// one signer name that means "passed Windows Hardware Lab Kit certification", as opposed to a
    /// vendor's own Authenticode certificate signing an uncertified (but not necessarily untrusted)
    /// driver.</summary>
    private const string WhqlSignerName = "Microsoft Windows Hardware Compatibility Publisher";

    public static Task<CatalogSignatureResult> VerifyAsync(string? filePath) => Task.Run(() => Verify(filePath));

    public static CatalogSignatureResult Verify(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return UnknownResult;

        try
        {
            if (!File.Exists(filePath)) return UnknownResult;

            var (catalogFilePath, memberTag) = TryFindCatalog(filePath);
            return catalogFilePath is not null && memberTag is not null
                ? VerifyViaCatalog(filePath, catalogFilePath, memberTag)
                : VerifyEmbedded(filePath);
        }
        catch
        {
            return UnknownResult;
        }
    }

    /// <summary>Hashes the file the way CryptCATAdmin expects and looks up which system catalog (if
    /// any) lists that hash, returning the catalog's own file path plus the hex member tag
    /// WinVerifyTrust needs to look the member back up inside it. Every CryptCATAdmin handle
    /// acquired here is released before returning, success or failure.</summary>
    private static (string? CatalogFilePath, string? MemberTag) TryFindCatalog(string filePath)
    {
        IntPtr catAdmin = IntPtr.Zero;
        IntPtr catInfo = IntPtr.Zero;
        try
        {
            var subsystem = DriverActionVerifyGuid;
            if (!CryptCATAdminAcquireContext(out catAdmin, ref subsystem, 0)) return (null, null);

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            uint hashSize = 0;
            CryptCATAdminCalcHashFromFileHandle(stream.SafeFileHandle, ref hashSize, null, 0);
            if (hashSize == 0) return (null, null);

            var hash = new byte[hashSize];
            if (!CryptCATAdminCalcHashFromFileHandle(stream.SafeFileHandle, ref hashSize, hash, 0)) return (null, null);

            IntPtr prevCatInfo = IntPtr.Zero;
            catInfo = CryptCATAdminEnumCatalogFromHash(catAdmin, hash, hashSize, 0, ref prevCatInfo);
            if (catInfo == IntPtr.Zero) return (null, null);

            var catalogInfo = new CATALOG_INFO { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>() };
            if (!CryptCATCatalogInfoFromContext(catInfo, ref catalogInfo, 0)) return (null, null);

            string memberTag = Convert.ToHexString(hash); // uppercase hex, no separators - what WinVerifyTrust's pcwszMemberTag expects
            return (catalogInfo.wszCatalogFile, memberTag);
        }
        catch
        {
            return (null, null);
        }
        finally
        {
            if (catInfo != IntPtr.Zero) { try { CryptCATAdminReleaseCatalogContext(catAdmin, catInfo, 0); } catch { /* best-effort */ } }
            if (catAdmin != IntPtr.Zero) { try { CryptCATAdminReleaseContext(catAdmin, 0); } catch { /* best-effort */ } }
        }
    }

    private static CatalogSignatureResult VerifyViaCatalog(string filePath, string catalogFilePath, string memberTag)
    {
        var catalogInfo = new WINTRUST_CATALOG_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_CATALOG_INFO>(),
            pcwszCatalogFilePath = catalogFilePath,
            pcwszMemberTag = memberTag,
            pcwszMemberFilePath = filePath,
        };

        uint result = CallWinVerifyTrust(WTD_CHOICE_CATALOG, ref catalogInfo);
        string status = ClassifyResult(result);

        string? signer = ReadSignerCommonName(catalogFilePath);
        bool isWhql = signer is not null && signer.Equals(WhqlSignerName, StringComparison.OrdinalIgnoreCase);
        return new CatalogSignatureResult(status, signer, isWhql, IsCatalogSigned: true);
    }

    private static CatalogSignatureResult VerifyEmbedded(string filePath)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = filePath,
        };

        uint result = CallWinVerifyTrust(WTD_CHOICE_FILE, ref fileInfo);
        string status = ClassifyResult(result);

        string? signer = ReadSignerCommonName(filePath);
        bool isWhql = signer is not null && signer.Equals(WhqlSignerName, StringComparison.OrdinalIgnoreCase);
        return new CatalogSignatureResult(status, signer, isWhql, IsCatalogSigned: false);
    }

    private static uint CallWinVerifyTrust<TInfo>(uint unionChoice, ref TInfo info) where TInfo : struct
    {
        IntPtr infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<TInfo>());
        try
        {
            Marshal.StructureToPtr(info, infoPtr, false);

            var wintrustData = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = unionChoice,
                pInfoStruct = infoPtr,
                dwStateAction = WTD_STATEACTION_IGNORE,
            };

            IntPtr dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
            try
            {
                Marshal.StructureToPtr(wintrustData, dataPtr, false);
                var actionGuid = WintrustActionGenericVerifyV2;
                return WinVerifyTrust(IntPtr.Zero, ref actionGuid, dataPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(dataPtr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(infoPtr);
        }
    }

    /// <summary>#455: absent/self-signed/test-root drivers surface here as either "Unsigned" (no
    /// signature found at all) or "Test-signed / untrusted root" (a signature chain WinVerifyTrust
    /// itself won't trust - the same result a driver signed with a locally-generated test
    /// certificate under `bcdedit /set testsigning on` produces). Any other nonzero result is
    /// reported with its raw HRESULT rather than guessed at, since the remaining codes (expired
    /// cert, revoked, wrong usage, ...) are each a distinct condition worth showing verbatim.</summary>
    private static string ClassifyResult(uint result) => result switch
    {
        0 => "Signed",
        0x800B0100 => "Unsigned", // TRUST_E_NOSIGNATURE
        0x80096010 => "Unsigned", // TRUST_E_BAD_DIGEST - not actually a member of the catalog it was pulled from
        0x800B0109 => "Test-signed / untrusted root", // CERT_E_UNTRUSTEDROOT
        0x800B0111 => "Test-signed / untrusted root", // TRUST_E_EXPLICIT_DISTRUST
        0x800B0004 => "Test-signed / untrusted root", // TRUST_E_SUBJECT_NOT_TRUSTED
        0x800B0101 => "Signature expired", // CERT_E_EXPIRED
        _ => $"Signature check failed (0x{result:X8})",
    };

    /// <summary>Extracts the signer's common name from a file's embedded Authenticode signature -
    /// used both for the embedded-fallback path (signer of the driver file itself) and the
    /// catalog path (signer of the .cat file, which is itself Authenticode-signed the same way).
    /// Null on any failure - no signature present, an unreadable certificate, etc.</summary>
    private static string? ReadSignerCommonName(string signedFilePath)
    {
        try
        {
            using var cert1 = X509Certificate.CreateFromSignedFile(signedFilePath);
            using var cert2 = new X509Certificate2(cert1);
            string name = cert2.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    // --- native interop ---

    private static readonly Guid DriverActionVerifyGuid = new("F750E6C3-38EE-11D1-85E5-00C04FC295EE");
    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_CHOICE_CATALOG = 2;
    private const uint WTD_STATEACTION_IGNORE = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_CATALOG_INFO
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pcwszCatalogFilePath;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pcwszMemberTag;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pcwszFilePath;
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
        public IntPtr pInfoStruct;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CATALOG_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string wszCatalogFile;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptCATAdminAcquireContext(out IntPtr catAdmin, ref Guid subsystem, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle(Microsoft.Win32.SafeHandles.SafeFileHandle fileHandle, ref uint hashSize, byte[]? hash, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr catAdmin, byte[] hash, uint hashSize, uint flags, ref IntPtr prevCatInfo);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr catInfo, ref CATALOG_INFO catalogInfo, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr catAdmin, IntPtr catInfo, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr catAdmin, uint flags);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid actionId, IntPtr wvtData);
}
