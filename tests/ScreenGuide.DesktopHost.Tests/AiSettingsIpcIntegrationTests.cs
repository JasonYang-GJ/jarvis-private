using System.Text;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class AiSettingsIpcIntegrationTests
{
    [Fact]
    public async Task RealPipeSwitchesOrdinaryChatRouteAndKeepsCredentialWriteOnly()
    {
        const string canaryKey = "sk-stage2-ipc-canary-must-never-leak";
        const string qwenCanaryKey = "qwen-ipc-canary-must-never-leak";
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(
            environment.Options.PipeName,
            TimeSpan.FromSeconds(3));

        try
        {
            var initial = await client.GetAiSettingsAsync();
            Assert.Equal("Codex", initial.ProgrammingAgent);
            Assert.Equal(new AiChatRouteDto("deepseek", "deepseek-v4-pro"), initial.CurrentChatRoute);
            Assert.Contains(initial.Providers, item => item.ProviderId == "codex");
            Assert.Contains(initial.Providers, item => item.ProviderId == "deepseek");
            var qwenInitial = initial.Providers.Single(item => item.ProviderId == "qwen");
            Assert.Equal("千问", qwenInitial.DisplayName);
            Assert.Equal("Missing", qwenInitial.ConfigurationState);
            Assert.Equal("qwen3.7-plus", Assert.Single(qwenInitial.Models).ModelId);

            var switched = await client.SetChatRouteAsync(
                new SetChatRouteRequestDto("deepseek", "deepseek-v4-pro"));
            Assert.Equal(new AiChatRouteDto("deepseek", "deepseek-v4-pro"), switched.CurrentChatRoute);
            Assert.Equal("Codex", switched.ProgrammingAgent);

            var credential = await client.SetProviderCredentialAsync(
                new SetProviderCredentialRequestDto("deepseek", canaryKey));
            var configured = await client.GetAiSettingsAsync();
            var deepseek = configured.Providers.Single(item => item.ProviderId == "deepseek");

            Assert.Equal("Configured", credential.ConfigurationState);
            Assert.Equal("Configured", deepseek.ConfigurationState);
            Assert.DoesNotContain(canaryKey, credential.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(canaryKey, configured.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(environment.Options.SecretsDirectory, "deepseek.bin")));
            AssertNoPlaintextCanary(environment.Options.DataDirectory, canaryKey);

            var codexHealth = await client.CheckAiProviderHealthAsync(
                new ProviderIdRequestDto("codex"));
            var afterHealth = await client.GetAiSettingsAsync();
            var codex = afterHealth.Providers.Single(item => item.ProviderId == "codex");
            Assert.Equal("Unavailable", codexHealth.State);
            Assert.True(codexHealth.IsConfigured);
            Assert.Contains("安全原因", codexHealth.SafeMessage, StringComparison.Ordinal);
            Assert.Contains("Codex 普通聊天", codexHealth.SafeMessage, StringComparison.Ordinal);
            Assert.Contains("停用", codexHealth.SafeMessage, StringComparison.Ordinal);
            Assert.Contains("编程任务不受影响", codexHealth.SafeMessage, StringComparison.Ordinal);
            Assert.DoesNotContain(canaryKey, codexHealth.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(
                environment.Options.DataDirectory,
                codexHealth.SafeMessage,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal("NotRequired", codex.ConfigurationState);
            Assert.Equal(new AiChatRouteDto("deepseek", "deepseek-v4-pro"), afterHealth.CurrentChatRoute);
            Assert.Equal("Codex", afterHealth.ProgrammingAgent);
            Assert.DoesNotContain(canaryKey, afterHealth.ToString(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(environment.Options.CodexDataDirectory));

            var deleted = await client.DeleteProviderCredentialAsync(
                new ProviderIdRequestDto("deepseek"));
            Assert.Equal("Missing", deleted.ConfigurationState);
            Assert.False(File.Exists(Path.Combine(environment.Options.SecretsDirectory, "deepseek.bin")));
            Assert.Equal(
                "Missing",
                (await client.GetAiSettingsAsync()).Providers
                .Single(item => item.ProviderId == "deepseek")
                .ConfigurationState);

            var qwenRoute = await client.SetChatRouteAsync(
                new SetChatRouteRequestDto("qwen", "qwen3.7-plus"));
            var qwenCredential = await client.SetProviderCredentialAsync(
                new SetProviderCredentialRequestDto("qwen", qwenCanaryKey));
            var qwenConfigured = await client.GetAiSettingsAsync();
            Assert.Equal(new AiChatRouteDto("qwen", "qwen3.7-plus"), qwenRoute.CurrentChatRoute);
            Assert.Equal("Codex", qwenRoute.ProgrammingAgent);
            Assert.Equal("Configured", qwenCredential.ConfigurationState);
            Assert.Equal(
                "Configured",
                qwenConfigured.Providers.Single(item => item.ProviderId == "qwen").ConfigurationState);
            Assert.Equal(
                "Missing",
                qwenConfigured.Providers.Single(item => item.ProviderId == "deepseek").ConfigurationState);
            Assert.True(File.Exists(Path.Combine(environment.Options.SecretsDirectory, "qwen.bin")));
            Assert.False(File.Exists(Path.Combine(environment.Options.SecretsDirectory, "deepseek.bin")));
            AssertNoPlaintextCanary(environment.Options.DataDirectory, qwenCanaryKey);

            var qwenDeleted = await client.DeleteProviderCredentialAsync(
                new ProviderIdRequestDto("qwen"));
            Assert.Equal("Missing", qwenDeleted.ConfigurationState);
            Assert.False(File.Exists(Path.Combine(environment.Options.SecretsDirectory, "qwen.bin")));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static void AssertNoPlaintextCanary(string rootDirectory, string canary)
    {
        var canaryBytes = Encoding.UTF8.GetBytes(canary);
        foreach (var path in Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(path);
            Assert.True(
                bytes.AsSpan().IndexOf(canaryBytes) < 0,
                $"敏感值不得明文写入运行数据文件：{Path.GetFileName(path)}");
        }
    }
}
