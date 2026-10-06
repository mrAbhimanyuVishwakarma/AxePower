using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace AxePower.Core.Services.Update;

public sealed class AuthenticodeVerifyResult
{
    public bool IsValid { get; }
    public string? FailureReason { get; }
    public string? Subject { get; }
    public string? Thumbprint { get; }

    private AuthenticodeVerifyResult(bool isValid, string? failureReason, string? subject, string? thumbprint)
    {
        IsValid = isValid;
        FailureReason = failureReason;
        Subject = subject;
        Thumbprint = thumbprint;
    }

    public static AuthenticodeVerifyResult Success(string subject, string? thumbprint = null) =>
        new(true, null, subject, thumbprint);

    public static AuthenticodeVerifyResult Failure(string reason) =>
        new(false, reason, null, null);
}

public interface IAuthenticodeVerifier
{
    AuthenticodeVerifyResult Verify(string filePath, string expectedPublisher);
}

/// <summary>
/// Authenticode verifier for Windows that verifies cryptographic integrity,
/// root trust via WinVerifyTrust, and publisher identity.
/// </summary>
public sealed class WindowsAuthenticodeVerifier : IAuthenticodeVerifier
{
    private readonly bool _requireTrustedRoot;

    public WindowsAuthenticodeVerifier(bool requireTrustedRoot = true)
    {
        _requireTrustedRoot = requireTrustedRoot;
    }

    public AuthenticodeVerifyResult Verify(string filePath, string expectedPublisher)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return AuthenticodeVerifyResult.Failure($"Target file does not exist: '{filePath}'");
        }

        // 1. Verify that the file contains an Authenticode signature and read the certificate
        X509Certificate2? cert = null;
        try
        {
#pragma warning disable SYSLIB0057 // X509Certificate.CreateFromSignedFile
            using var rawCert = X509Certificate.CreateFromSignedFile(filePath);
            cert = new X509Certificate2(rawCert);
#pragma warning restore SYSLIB0057
        }
        catch (Exception ex)
        {
            // Fails closed with the reason, which the update flow logs and shows.
            return AuthenticodeVerifyResult.Failure($"File is not Authenticode signed or signature structure is corrupt: {ex.Message}");
        }

        if (cert == null)
        {
            return AuthenticodeVerifyResult.Failure("Unable to read Authenticode certificate from file.");
        }

        var subject = cert.Subject;
        var thumbprint = cert.Thumbprint;

        // 2. Verify publisher identity
        if (!string.IsNullOrWhiteSpace(expectedPublisher))
        {
            if (subject.IndexOf(expectedPublisher, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return AuthenticodeVerifyResult.Failure(
                    $"Signer identity '{subject}' does not match expected publisher '{expectedPublisher}'.");
            }
        }

        // 3. On Windows, verify cryptographic integrity and trust chain via WinVerifyTrust
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var winTrustResult = VerifyWinTrust(filePath);
            if (winTrustResult != 0)
            {
                // 0x800B0100 = TRUST_E_NOSIGNATURE
                // 0x80096010 = TRUST_E_BAD_DIGEST (file tampered with)
                // 0x800B0109 = CERT_E_UNTRUSTEDROOT
                if (winTrustResult == 0x800B0109 && !_requireTrustedRoot)
                {
                    // In testing or self-signed mode where root trust is relaxed but digest must still be valid
                    return AuthenticodeVerifyResult.Success(subject, thumbprint);
                }

                var hexError = $"0x{winTrustResult:X8}";
                string description = winTrustResult switch
                {
                    0x800B0100 => "No signature found or file format not supported.",
                    0x80096010 => "Tampering detected: file contents do not match digital signature digest.",
                    0x800B0109 => "The certificate chain processed, but terminated in a root certificate not trusted by trust provider.",
                    0x800B0101 => "The certificate is expired or not yet valid.",
                    _ => $"WinVerifyTrust failed with error code {hexError}."
                };

                return AuthenticodeVerifyResult.Failure($"Authenticode trust validation failed ({hexError}): {description}");
            }
        }

        return AuthenticodeVerifyResult.Success(subject, thumbprint);
    }

    private static uint VerifyWinTrust(string filePath)
    {
        var wvtData = new WINTRUST_DATA();
        var fileInfo = new WINTRUST_FILE_INFO();

        try
        {
            fileInfo.cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>();
            fileInfo.pcwszFilePath = filePath;
            fileInfo.hFile = IntPtr.Zero;
            fileInfo.pgKnownSubject = IntPtr.Zero;

            var pFileInfo = Marshal.AllocHGlobal((int)fileInfo.cbStruct);
            Marshal.StructureToPtr(fileInfo, pFileInfo, false);

            wvtData.cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>();
            wvtData.pPolicyCallbackData = IntPtr.Zero;
            wvtData.pSIPClientData = IntPtr.Zero;
            wvtData.dwUIChoice = 2; // WTD_UI_NONE
            wvtData.fdwRevocationChecks = 0; // WTD_REVOKE_NONE
            wvtData.dwUnionChoice = 1; // WTD_CHOICE_FILE
            wvtData.pFile = pFileInfo;
            wvtData.dwStateAction = 0; // WTD_STATEACTION_IGNORE
            wvtData.hWVTStateData = IntPtr.Zero;
            wvtData.pwszURLReference = null!;
            wvtData.dwProvFlags = 0x00000040 | 0x00000080; // WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE
            wvtData.dwUIContext = 0;

            var pWvtData = Marshal.AllocHGlobal((int)wvtData.cbStruct);
            Marshal.StructureToPtr(wvtData, pWvtData, false);

            var actionGuid = new Guid("{00AAC56B-CD44-11d0-8CC2-00C04FC295EE}"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            var result = WinVerifyTrust(IntPtr.Zero, actionGuid, pWvtData);

            Marshal.FreeHGlobal(pFileInfo);
            Marshal.FreeHGlobal(pWvtData);

            return result;
        }
        catch (Exception)
        {
            // Fails closed: any failure to ask WinVerifyTrust counts as "not trusted".
            return 0x80004005; // E_FAIL
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern uint WinVerifyTrust(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID,
        IntPtr pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
    }
}
