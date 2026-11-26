// WinVerifyTrust signature check (embedded or Windows catalog), from XenoSec.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace XenoSuite.SecAudit;

/// <summary>Authenticode verdict for a file. Valid = the signature checks out (embedded or via a Windows catalog).</summary>
sealed record Signature(bool Signed, bool Valid, string? Signer, bool Catalog, string? Problem);

/// <summary>
/// Verifies Authenticode signatures the way Windows does: the file's own signature, or (for most of Windows' own files)
/// a signed catalog listing the file's hash. Offline only: no revocation lookups, so a scan never waits on the network.
/// </summary>
static class Trust
{
    static readonly ConcurrentDictionary<(string, DateTime, long), Signature> Cache = new();   // key: lower-cased path, write time, size

    public static Signature Check(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return new(false, false, null, false, "file not found");
            return Cache.GetOrAdd((path.ToLowerInvariant(), fi.LastWriteTimeUtc, fi.Length), _ => CheckUncached(path));
        }
        catch (Exception ex) { return new(false, false, null, false, ex.Message); }
    }

    static Signature CheckUncached(string path)
    {
        int embedded = VerifyFile(path);
        if (embedded == 0) return new(true, true, EmbeddedSigner(path), false, null);
        // there IS a signature, but it doesn't verify: tampered file, expired/untrusted/revoked cert
        if (TamperOrTrust.Contains((uint)embedded)) return new(true, false, EmbeddedSigner(path), false, Describe(embedded));
        // anything else that isn't "no signature" means the check itself couldn't run (unreadable stub, odd file format):
        // that says nothing about the file, so it's reported as unchecked rather than as a bad signature
        if (embedded != TRUST_E_NOSIGNATURE && embedded != TRUST_E_SUBJECT_FORM_UNKNOWN && embedded != TRUST_E_PROVIDER_UNKNOWN)
            return new(false, false, null, false, "couldn't check: " + Describe(embedded));

        var cat = FindCatalog(path);
        if (cat != null)
        {
            int r = VerifyCatalog(path, cat.Value.catalog, cat.Value.memberTag);
            var signer = EmbeddedSigner(cat.Value.catalog);
            return r == 0 ? new(true, true, signer, true, null) : new(true, false, signer, true, Describe(r));
        }
        return new(false, false, null, false, null);
    }

    static string? EmbeddedSigner(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057
            var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            var m = System.Text.RegularExpressions.Regex.Match(cert.Subject, "CN=\"?([^\",]+)");
            return m.Success ? m.Groups[1].Value.Trim() : cert.Subject;
        }
        catch { return null; }
    }

    /// <summary>Results that mean the signature is really bad, not that the check couldn't be done.</summary>
    static readonly HashSet<uint> TamperOrTrust = new()
    {
        0x80096010, // TRUST_E_BAD_DIGEST: modified after signing
        0x80096004, // TRUST_E_CERT_SIGNATURE
        0x800B0101, // CERT_E_EXPIRED
        0x800B0109, // CERT_E_UNTRUSTEDROOT
        0x800B010A, // CERT_E_CHAINING
        0x800B010C, // CERT_E_REVOKED
        0x800B0111, // TRUST_E_EXPLICIT_DISTRUST
        0x80096019, // TRUST_E_BASIC_CONSTRAINTS
    };

    static string Describe(int hr) => (uint)hr switch
    {
        0x80096010 => "signature doesn't match the file (modified after signing)",
        0x800B0101 => "certificate expired",
        0x800B0109 => "certificate chain doesn't lead to a trusted root",
        0x800B010C => "certificate revoked",
        0x800B0111 => "publisher explicitly distrusted",
        0x80096019 => "bad certificate basic constraints",
        _ => $"signature check failed (0x{hr:X8})",
    };

    // ---------------------------------------------------------------- WinVerifyTrust
    const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
    const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);
    static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO { public int cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_CATALOG_INFO
    {
        public int cbStruct; public int dwCatalogVersion; public string pcwszCatalogFilePath; public string pcwszMemberTag;
        public string pcwszMemberFilePath; public IntPtr hMemberFile; public IntPtr pbCalculatedFileHash; public int cbCalculatedFileHash;
        public IntPtr pcCatalogContext; public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public int cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData; public int dwUIChoice;
        public int fdwRevocationChecks; public int dwUnionChoice; public IntPtr pInfo; public int dwStateAction; public IntPtr hWVTStateData;
        public IntPtr pwszURLReference; public int dwProvFlags; public int dwUIContext; public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

    const int WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1, WTD_CHOICE_CATALOG = 2,
              WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2, WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

    static int Verify(int choice, IntPtr info)
    {
        var data = new WINTRUST_DATA
        {
            cbStruct = Marshal.SizeOf<WINTRUST_DATA>(), dwUIChoice = WTD_UI_NONE, fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = choice, pInfo = info, dwStateAction = WTD_STATEACTION_VERIFY, dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
        };
        int r = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
        data.dwStateAction = WTD_STATEACTION_CLOSE;
        WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
        return r;
    }

    static int VerifyFile(string path)
    {
        var fi = new WINTRUST_FILE_INFO { cbStruct = Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var p = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try { Marshal.StructureToPtr(fi, p, false); return Verify(WTD_CHOICE_FILE, p); }
        finally { Marshal.DestroyStructure<WINTRUST_FILE_INFO>(p); Marshal.FreeHGlobal(p); }
    }

    static int VerifyCatalog(string path, string catalog, string memberTag)
    {
        var ci = new WINTRUST_CATALOG_INFO
        {
            cbStruct = Marshal.SizeOf<WINTRUST_CATALOG_INFO>(), pcwszCatalogFilePath = catalog, pcwszMemberTag = memberTag, pcwszMemberFilePath = path,
        };
        var p = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_CATALOG_INFO>());
        try { Marshal.StructureToPtr(ci, p, false); return Verify(WTD_CHOICE_CATALOG, p); }
        finally { Marshal.DestroyStructure<WINTRUST_CATALOG_INFO>(p); Marshal.FreeHGlobal(p); }
    }

    // ---------------------------------------------------------------- catalog lookup
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CATALOG_INFO { public int cbStruct; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string wszCatalogFile; }

    [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptCATAdminAcquireContext2(out IntPtr hCatAdmin, IntPtr pgSubsystem, string pwszHashAlgorithm, IntPtr pStrongHashPolicy, int dwFlags);
    [DllImport("wintrust.dll", SetLastError = true)]
    static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, int dwFlags);
    [DllImport("wintrust.dll", SetLastError = true)]
    static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr hCatAdmin, IntPtr hFile, ref int pcbHash, byte[]? pbHash, int dwFlags);
    [DllImport("wintrust.dll", SetLastError = true)]
    static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr hCatAdmin, byte[] pbHash, int cbHash, int dwFlags, IntPtr phPrevCatInfo);
    [DllImport("wintrust.dll", SetLastError = true)]
    static extern bool CryptCATCatalogInfoFromContext(IntPtr hCatInfo, ref CATALOG_INFO psCatInfo, int dwFlags);
    [DllImport("wintrust.dll", SetLastError = true)]
    static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin, IntPtr hCatInfo, int dwFlags);

    static (string catalog, string memberTag)? FindCatalog(string path)
    {
        // Windows 10+ catalogs use SHA-256; older entries are SHA-1. Try both.
        foreach (var alg in new[] { "SHA256", "SHA1" })
        {
            if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, alg, IntPtr.Zero, 0)) continue;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var handle = fs.SafeFileHandle.DangerousGetHandle();
                int size = 0;
                CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref size, null, 0);
                if (size == 0) continue;
                var hash = new byte[size];
                if (!CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref size, hash, 0)) continue;
                var ctx = CryptCATAdminEnumCatalogFromHash(admin, hash, size, 0, IntPtr.Zero);
                if (ctx == IntPtr.Zero) continue;
                try
                {
                    var info = new CATALOG_INFO { cbStruct = Marshal.SizeOf<CATALOG_INFO>() };
                    if (CryptCATCatalogInfoFromContext(ctx, ref info, 0))
                        return (info.wszCatalogFile, Convert.ToHexString(hash));
                }
                finally { CryptCATAdminReleaseCatalogContext(admin, ctx, 0); }
            }
            catch { }
            finally { CryptCATAdminReleaseContext(admin, 0); }
        }
        return null;
    }

    public static string? Sha256(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > 512L * 1024 * 1024) return null;   // don't hash huge files during a hunt
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch { return null; }
    }
}
