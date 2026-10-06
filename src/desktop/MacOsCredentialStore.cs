using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace NubeZero.Desktop;

internal static class MacOsCredentialStore
{
    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ItemNotFound = -25300;
    private static readonly byte[] ServiceName = Encoding.UTF8.GetBytes("com.esmesolutions.nubezero");
    private static readonly byte[] AccountName = Encoding.UTF8.GetBytes("last-login");

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychainOrArray,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        out uint passwordLength,
        out IntPtr passwordData,
        out IntPtr itemReference);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainAddGenericPassword(
        IntPtr keychain,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        uint passwordLength,
        byte[] passwordData,
        out IntPtr itemReference);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemDelete(IntPtr itemReference);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);

    [DllImport(CoreFoundationFramework)]
    private static extern void CFRelease(IntPtr value);

    internal static SavedMacCredentials? Load()
    {
        int status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)ServiceName.Length,
            ServiceName,
            (uint)AccountName.Length,
            AccountName,
            out uint passwordLength,
            out IntPtr passwordData,
            out IntPtr itemReference);

        if (status == ItemNotFound) return null;
        ThrowIfError(status);

        try
        {
            var data = new byte[checked((int)passwordLength)];
            Marshal.Copy(passwordData, data, 0, data.Length);
            return JsonSerializer.Deserialize<SavedMacCredentials>(data);
        }
        finally
        {
            if (passwordData != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
            if (itemReference != IntPtr.Zero) CFRelease(itemReference);
        }
    }

    internal static void Save(string serverUrl, string username, string password, string certificateFingerprint)
    {
        Delete();

        byte[] data = JsonSerializer.SerializeToUtf8Bytes(new SavedMacCredentials
        {
            ServerUrl = serverUrl,
            Username = username,
            Password = password,
            CertificateFingerprint = certificateFingerprint
        });

        int status = SecKeychainAddGenericPassword(
            IntPtr.Zero,
            (uint)ServiceName.Length,
            ServiceName,
            (uint)AccountName.Length,
            AccountName,
            (uint)data.Length,
            data,
            out IntPtr itemReference);

        try
        {
            ThrowIfError(status);
        }
        finally
        {
            if (itemReference != IntPtr.Zero) CFRelease(itemReference);
        }
    }

    internal static void Delete()
    {
        int status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)ServiceName.Length,
            ServiceName,
            (uint)AccountName.Length,
            AccountName,
            out _,
            out _,
            out IntPtr itemReference);

        if (status == ItemNotFound) return;
        ThrowIfError(status);

        try
        {
            ThrowIfError(SecKeychainItemDelete(itemReference));
        }
        finally
        {
            if (itemReference != IntPtr.Zero) CFRelease(itemReference);
        }
    }

    private static void ThrowIfError(int status)
    {
        if (status != 0)
            throw new InvalidOperationException($"Keychain devolvió el código {status}.");
    }
}

internal sealed class SavedMacCredentials
{
    public string ServerUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string CertificateFingerprint { get; set; } = string.Empty;
}