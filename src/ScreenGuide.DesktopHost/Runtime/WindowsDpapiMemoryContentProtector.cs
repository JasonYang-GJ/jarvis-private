using System.Security.Cryptography;
using System.Text;
using ScreenGuide.Core.Memories;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed class WindowsDpapiMemoryContentProtector : IMemoryContentProtector
{
    private const byte FormatVersion = 1;
    private const int MaximumCiphertextBytes = 64 * 1024;
    private static readonly byte[] Entropy = SHA256.HashData(
        Encoding.UTF8.GetBytes("ScreenGuide.V3.MemoryContent.CurrentUser.v1"));

    public byte[] Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            throw new ArgumentException("待保护的记忆内容不能为空。", nameof(plaintext));
        }

        byte[]? clearBytes = null;
        try
        {
            clearBytes = Encoding.UTF8.GetBytes(plaintext);
            var protectedBytes = ProtectedData.Protect(
                clearBytes,
                Entropy,
                DataProtectionScope.CurrentUser);
            if (protectedBytes.Length + 1 > MaximumCiphertextBytes)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
                throw new CryptographicException("受保护记忆内容超过安全上限。");
            }

            var envelope = new byte[protectedBytes.Length + 1];
            envelope[0] = FormatVersion;
            protectedBytes.CopyTo(envelope, 1);
            CryptographicOperations.ZeroMemory(protectedBytes);
            return envelope;
        }
        finally
        {
            if (clearBytes is not null)
            {
                CryptographicOperations.ZeroMemory(clearBytes);
            }
        }
    }

    public string Unprotect(ReadOnlySpan<byte> protectedContent)
    {
        if (protectedContent.Length < 2
            || protectedContent.Length > MaximumCiphertextBytes
            || protectedContent[0] != FormatVersion)
        {
            throw new CryptographicException("受保护记忆内容格式无效。");
        }

        byte[]? protectedBytes = null;
        byte[]? clearBytes = null;
        try
        {
            protectedBytes = protectedContent[1..].ToArray();
            clearBytes = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clearBytes);
        }
        finally
        {
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            if (clearBytes is not null)
            {
                CryptographicOperations.ZeroMemory(clearBytes);
            }
        }
    }
}
