using System.Text;
using ScreenGuide.AI.Core;
using ScreenGuide.DesktopHost.Runtime;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class WindowsDpapiCredentialStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "screen-guide-dpapi-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RoundTrip_persists_only_encrypted_bytes_for_current_user()
    {
        var store = new WindowsDpapiCredentialStore(_root, TimeProvider.System);
        var secret = "sk-stage2-plaintext-must-not-leak".ToCharArray();

        try
        {
            await store.SetAsync("deepseek", secret);

            var status = await store.GetStatusAsync("deepseek");
            Assert.Equal(ProviderCredentialState.Configured, status.State);
            Assert.NotNull(status.UpdatedAtUtc);

            using var lease = await store.OpenLeaseAsync("deepseek");
            Assert.NotNull(lease);
            Assert.True(secret.AsSpan().SequenceEqual(lease!.Secret.Span));

            var encryptedPath = Assert.Single(Directory.EnumerateFiles(_root, "*.bin"));
            var persisted = await File.ReadAllBytesAsync(encryptedPath);
            Assert.False(persisted.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) >= 0);
            Assert.DoesNotContain(new string(secret), Convert.ToBase64String(persisted), StringComparison.Ordinal);
        }
        finally
        {
            secret.AsSpan().Clear();
        }
    }

    [Fact]
    public async Task Replace_and_delete_never_expose_the_previous_secret()
    {
        var store = new WindowsDpapiCredentialStore(_root, TimeProvider.System);
        var first = "sk-first-secret".ToCharArray();
        var replacement = "sk-replacement-secret".ToCharArray();

        try
        {
            await store.SetAsync("deepseek", first);
            await store.SetAsync("deepseek", replacement);

            using (var lease = await store.OpenLeaseAsync("deepseek"))
            {
                Assert.NotNull(lease);
                Assert.True(replacement.AsSpan().SequenceEqual(lease!.Secret.Span));
                Assert.False(first.AsSpan().SequenceEqual(lease.Secret.Span));
            }

            Assert.True(await store.DeleteAsync("deepseek"));
            Assert.False(await store.DeleteAsync("deepseek"));
            Assert.Null(await store.OpenLeaseAsync("deepseek"));
            Assert.Equal(
                ProviderCredentialState.Missing,
                (await store.GetStatusAsync("deepseek")).State);
            Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
        }
        finally
        {
            first.AsSpan().Clear();
            replacement.AsSpan().Clear();
        }
    }

    [Fact]
    public async Task Corrupt_ciphertext_fails_closed_without_echoing_file_contents()
    {
        Directory.CreateDirectory(_root);
        var corrupt = Encoding.UTF8.GetBytes("sk-corrupt-secret-must-not-appear");
        await File.WriteAllBytesAsync(Path.Combine(_root, "deepseek.bin"), corrupt);
        var store = new WindowsDpapiCredentialStore(_root, TimeProvider.System);

        var exception = await Assert.ThrowsAsync<ProviderCredentialStoreException>(async () =>
        {
            using var ignored = await store.OpenLeaseAsync("deepseek");
        });

        Assert.Equal("credential_unprotect_failed", exception.Code);
        Assert.DoesNotContain("sk-corrupt", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../deepseek")]
    [InlineData("deepseek\\other")]
    [InlineData("deepseek/other")]
    public async Task Invalid_provider_id_cannot_escape_the_secret_directory(string providerId)
    {
        var store = new WindowsDpapiCredentialStore(_root, TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.GetStatusAsync(providerId));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
