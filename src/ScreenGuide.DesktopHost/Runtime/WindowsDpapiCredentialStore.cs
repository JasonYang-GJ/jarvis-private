using System.Security.Cryptography;
using System.Text;
using ScreenGuide.AI.Core;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed class ProviderCredentialStoreException : Exception
{
    public ProviderCredentialStoreException(string code, string message)
        : base(message)
    {
        Code = string.IsNullOrWhiteSpace(code)
            ? throw new ArgumentException("错误代码不能为空。", nameof(code))
            : code;
    }

    public string Code { get; }
}

public sealed class WindowsDpapiCredentialStore : IProviderCredentialStore
{
    private const int MaximumSecretCharacters = 8 * 1024;
    private const long MaximumCiphertextBytes = 64 * 1024;
    private readonly string _rootDirectory;

    public WindowsDpapiCredentialStore(
        string rootDirectory,
        TimeProvider timeProvider)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("密钥目录不能为空。", nameof(rootDirectory));
        }

        _rootDirectory = Path.GetFullPath(rootDirectory.Trim());
        ArgumentNullException.ThrowIfNull(timeProvider);
    }

    public Task<ProviderCredentialStatus> GetStatusAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedProviderId = NormalizeProviderId(providerId);
        var path = GetCredentialPath(normalizedProviderId);

        if (!File.Exists(path))
        {
            return Task.FromResult(new ProviderCredentialStatus(
                normalizedProviderId,
                ProviderCredentialState.Missing,
                null));
        }

        DateTimeOffset updatedAt;
        try
        {
            updatedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw CreateSafeFailure("credential_status_failed", "无法读取该模型服务的密钥状态。");
        }

        return Task.FromResult(new ProviderCredentialStatus(
            normalizedProviderId,
            ProviderCredentialState.Configured,
            updatedAt));
    }

    public async Task SetAsync(
        string providerId,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default)
    {
        var normalizedProviderId = NormalizeProviderId(providerId);
        if (secret.IsEmpty || secret.Length > MaximumSecretCharacters)
        {
            throw new ArgumentException("密钥不能为空且长度不能超过 8192 个字符。", nameof(secret));
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_rootDirectory);

        byte[]? plaintext = null;
        byte[]? ciphertext = null;
        var tempPath = Path.Combine(
            _rootDirectory,
            $".{normalizedProviderId}.{Guid.NewGuid():N}.tmp");

        try
        {
            plaintext = GC.AllocateUninitializedArray<byte>(
                Encoding.UTF8.GetByteCount(secret.Span));
            Encoding.UTF8.GetBytes(secret.Span, plaintext.AsSpan());
            ciphertext = ProtectedData.Protect(
                plaintext,
                GetProviderEntropy(normalizedProviderId),
                DataProtectionScope.CurrentUser);

            if (ciphertext.LongLength > MaximumCiphertextBytes)
            {
                throw CreateSafeFailure("credential_ciphertext_too_large", "加密后的密钥数据超过安全上限。");
            }

            await File.WriteAllBytesAsync(tempPath, ciphertext, cancellationToken);
            File.Move(tempPath, GetCredentialPath(normalizedProviderId), overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderCredentialStoreException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw CreateSafeFailure("credential_protect_failed", "无法安全保存该模型服务的密钥。");
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            if (ciphertext is not null)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }

            TryDeleteTemporaryFile(tempPath);
        }
    }

    public async ValueTask<IProviderCredentialLease?> OpenLeaseAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var normalizedProviderId = NormalizeProviderId(providerId);
        var path = GetCredentialPath(normalizedProviderId);
        if (!File.Exists(path))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        byte[]? ciphertext = null;
        byte[]? plaintext = null;
        char[]? characters = null;

        try
        {
            var fileLength = new FileInfo(path).Length;
            if (fileLength <= 0 || fileLength > MaximumCiphertextBytes)
            {
                throw CreateSafeFailure("credential_unprotect_failed", "已保存的密钥无法读取，请重新设置。");
            }

            ciphertext = await File.ReadAllBytesAsync(path, cancellationToken);
            plaintext = ProtectedData.Unprotect(
                ciphertext,
                GetProviderEntropy(normalizedProviderId),
                DataProtectionScope.CurrentUser);

            if (plaintext.Length == 0 || plaintext.Length > Encoding.UTF8.GetMaxByteCount(MaximumSecretCharacters))
            {
                throw CreateSafeFailure("credential_unprotect_failed", "已保存的密钥无法读取，请重新设置。");
            }

            characters = Encoding.UTF8.GetChars(plaintext);
            if (characters.Length == 0 || characters.Length > MaximumSecretCharacters)
            {
                throw CreateSafeFailure("credential_unprotect_failed", "已保存的密钥无法读取，请重新设置。");
            }

            var lease = new CredentialLease(characters);
            characters = null;
            return lease;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderCredentialStoreException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is CryptographicException or IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw CreateSafeFailure("credential_unprotect_failed", "已保存的密钥无法读取，请重新设置。");
        }
        finally
        {
            if (ciphertext is not null)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }

            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            if (characters is not null)
            {
                characters.AsSpan().Clear();
            }
        }
    }

    public Task<bool> DeleteAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedProviderId = NormalizeProviderId(providerId);
        var path = GetCredentialPath(normalizedProviderId);

        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        try
        {
            File.Delete(path);
            return Task.FromResult(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw CreateSafeFailure("credential_delete_failed", "无法删除该模型服务的密钥。");
        }
    }

    private string GetCredentialPath(string normalizedProviderId) =>
        Path.Combine(_rootDirectory, $"{normalizedProviderId}.bin");

    private static byte[] GetProviderEntropy(string normalizedProviderId) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"ScreenGuide.V2.Stage2.Credential.{normalizedProviderId}"));

    private static string NormalizeProviderId(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException("Provider ID 不能为空。", nameof(providerId));
        }

        var normalized = providerId.Trim().ToLowerInvariant();
        if (!string.Equals(providerId, providerId.Trim(), StringComparison.Ordinal)
            || normalized.Length > 64
            || normalized[0] == '.'
            || normalized[^1] == '.'
            || normalized.Any(character =>
                !((character >= 'a' && character <= 'z')
                    || (character >= '0' && character <= '9')
                    || character is '-' or '_' or '.')))
        {
            throw new ArgumentException("Provider ID 格式无效。", nameof(providerId));
        }

        return normalized;
    }

    private static ProviderCredentialStoreException CreateSafeFailure(
        string code,
        string message) => new(code, message);

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A random, encrypted temporary file contains no plaintext. Startup maintenance
            // may remove it later; hiding the primary credential-store failure is worse.
        }
    }

    private sealed class CredentialLease(char[] secret) : IProviderCredentialLease
    {
        private char[]? _secret = secret ?? throw new ArgumentNullException(nameof(secret));

        public ReadOnlyMemory<char> Secret =>
            _secret is { } value
                ? value
                : throw new ObjectDisposedException(nameof(CredentialLease));

        public void Dispose()
        {
            var secretToClear = Interlocked.Exchange(ref _secret, null);
            if (secretToClear is not null)
            {
                secretToClear.AsSpan().Clear();
            }
        }
    }
}
