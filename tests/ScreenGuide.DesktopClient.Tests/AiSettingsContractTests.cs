using System.IO.Pipes;
using System.Text.Json;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Tests;

public sealed class AiSettingsContractTests
{
    [Fact]
    public void ProtocolV9PreservesStableAiSettingsMethods()
    {
        Assert.Equal(10, DesktopProtocolVersion.Current);
        Assert.Equal("ai.settings.get", DesktopApiMethods.GetAiSettings);
        Assert.Equal("ai.chat-route.set", DesktopApiMethods.SetChatRoute);
        Assert.Equal("ai.credentials.set", DesktopApiMethods.SetProviderCredential);
        Assert.Equal("ai.credentials.delete", DesktopApiMethods.DeleteProviderCredential);
        Assert.Equal("ai.provider.health", DesktopApiMethods.CheckAiProviderHealth);
    }

    [Fact]
    public void AiSettingsContractDisclosesRoutingAndNeverReturnsCredentialValue()
    {
        var provider = new AiProviderSettingsDto(
            "deepseek",
            "DeepSeek",
            "DeepSeek API（中国境内云服务）",
            SendsDataOffDevice: true,
            ConfigurationState: "Configured",
            new AiProviderHealthDto(
                "deepseek",
                "Healthy",
                IsConfigured: true,
                "连接正常。",
                DateTimeOffset.Parse("2026-08-24T10:00:00+08:00")),
            [new AiModelSettingsDto("deepseek-v4-pro", "DeepSeek V4 Pro", ["Streaming"])]);
        var settings = new AiSettingsDto(
            [provider],
            new AiChatRouteDto("deepseek", "deepseek-v4-pro"),
            ProgrammingAgent: "Codex");

        Assert.Equal("deepseek", settings.CurrentChatRoute.ProviderId);
        Assert.Equal("deepseek-v4-pro", settings.CurrentChatRoute.ModelId);
        Assert.Equal("Codex", settings.ProgrammingAgent);
        Assert.True(Assert.Single(settings.Providers).SendsDataOffDevice);
        Assert.Equal("DeepSeek API（中国境内云服务）", provider.DataDestination);
        Assert.Equal("Configured", provider.ConfigurationState);
        Assert.Equal("Healthy", provider.Health.State);
        Assert.Equal("连接正常。", provider.Health.SafeMessage);
        Assert.Contains("Streaming", Assert.Single(provider.Models).Capabilities);

        var responseTypes = new[]
        {
            typeof(AiSettingsDto),
            typeof(AiProviderSettingsDto),
            typeof(AiProviderCredentialStatusDto),
            typeof(AiProviderHealthDto)
        };
        Assert.DoesNotContain(
            responseTypes.SelectMany(type => type.GetProperties()),
            property => property.Name is "Secret" or "ApiKey" or "CredentialValue");
    }

    [Fact]
    public void AiSettingsWritesCredentialWithoutAddingProgrammingRouteInput()
    {
        var route = new SetChatRouteRequestDto("deepseek", "deepseek-v4-pro");
        var credential = new SetProviderCredentialRequestDto("deepseek", "fake-test-key");
        var provider = new ProviderIdRequestDto("deepseek");

        Assert.Equal("deepseek", route.ProviderId);
        Assert.Equal("deepseek-v4-pro", route.ModelId);
        Assert.Equal(
            ["ProviderId", "ModelId"],
            typeof(SetChatRouteRequestDto).GetProperties().Select(item => item.Name));
        Assert.Equal("fake-test-key", credential.Secret);
        Assert.Equal("deepseek", provider.ProviderId);
        Assert.DoesNotContain("fake-test-key", credential.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", credential.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DesktopApiClientGetsAiSettingsThroughProtocol()
    {
        var expected = Settings();
        var pipeName = $"ScreenGuide.AiSettings.Tests.{Guid.NewGuid():N}";
        var server = ServeOnceAsync(pipeName, request =>
        {
            Assert.Equal(DesktopApiMethods.GetAiSettings, request.Method);
            return new DesktopApiResponse(
                request.RequestId,
                true,
                DesktopProtocolJson.ToElement(expected));
        });
        var client = new DesktopApiClient(pipeName, TimeSpan.FromSeconds(5));

        var actual = await client.GetAiSettingsAsync();
        await server;

        Assert.Equal(expected.CurrentChatRoute, actual.CurrentChatRoute);
        Assert.Equal("Codex", actual.ProgrammingAgent);
    }

    [Fact]
    public async Task DesktopApiClientSetsOnlyNextChatRouteThroughProtocol()
    {
        var expected = Settings();
        var pipeName = $"ScreenGuide.AiSettings.Tests.{Guid.NewGuid():N}";
        var server = ServeOnceAsync(pipeName, request =>
        {
            Assert.Equal(DesktopApiMethods.SetChatRoute, request.Method);
            var payload = request.Payload.Deserialize<SetChatRouteRequestDto>(
                DesktopProtocolJson.Options);
            Assert.Equal("deepseek", payload!.ProviderId);
            Assert.Equal("deepseek-v4-pro", payload.ModelId);
            Assert.DoesNotContain("Codex", request.Payload.GetRawText(), StringComparison.Ordinal);
            return new DesktopApiResponse(
                request.RequestId,
                true,
                DesktopProtocolJson.ToElement(expected));
        });
        var client = new DesktopApiClient(pipeName, TimeSpan.FromSeconds(5));

        var actual = await client.SetChatRouteAsync(
            new SetChatRouteRequestDto("deepseek", "deepseek-v4-pro"));
        await server;

        Assert.Equal(expected.CurrentChatRoute, actual.CurrentChatRoute);
    }

    [Fact]
    public async Task DesktopApiClientSetsCredentialWithoutReadingItBack()
    {
        const string fakeKey = "sk-fake-protocol-canary-123456";
        var pipeName = $"ScreenGuide.AiSettings.Tests.{Guid.NewGuid():N}";
        var server = ServeOnceAsync(pipeName, request =>
        {
            Assert.Equal(DesktopApiMethods.SetProviderCredential, request.Method);
            var payload = request.Payload.Deserialize<SetProviderCredentialRequestDto>(
                DesktopProtocolJson.Options);
            Assert.Equal("deepseek", payload!.ProviderId);
            Assert.Equal(fakeKey, payload.Secret);
            var response = new AiProviderCredentialStatusDto(
                "deepseek",
                "Configured",
                "密钥已安全保存。");
            Assert.DoesNotContain(
                fakeKey,
                JsonSerializer.Serialize(response, DesktopProtocolJson.Options),
                StringComparison.Ordinal);
            return new DesktopApiResponse(
                request.RequestId,
                true,
                DesktopProtocolJson.ToElement(response));
        });
        var client = new DesktopApiClient(pipeName, TimeSpan.FromSeconds(5));

        var actual = await client.SetProviderCredentialAsync(
            new SetProviderCredentialRequestDto("deepseek", fakeKey));
        await server;

        Assert.Equal("Configured", actual.ConfigurationState);
        Assert.Equal("密钥已安全保存。", actual.SafeMessage);
    }

    [Fact]
    public async Task DesktopApiClientDeletesCredentialThroughProtocol()
    {
        var pipeName = $"ScreenGuide.AiSettings.Tests.{Guid.NewGuid():N}";
        var server = ServeOnceAsync(pipeName, request =>
        {
            Assert.Equal(DesktopApiMethods.DeleteProviderCredential, request.Method);
            var payload = request.Payload.Deserialize<ProviderIdRequestDto>(
                DesktopProtocolJson.Options);
            Assert.Equal("deepseek", payload!.ProviderId);
            return new DesktopApiResponse(
                request.RequestId,
                true,
                DesktopProtocolJson.ToElement(new AiProviderCredentialStatusDto(
                    "deepseek",
                    "Missing",
                    "密钥已删除。")));
        });
        var client = new DesktopApiClient(pipeName, TimeSpan.FromSeconds(5));

        var actual = await client.DeleteProviderCredentialAsync(
            new ProviderIdRequestDto("deepseek"));
        await server;

        Assert.Equal("Missing", actual.ConfigurationState);
        Assert.Equal("密钥已删除。", actual.SafeMessage);
    }

    [Fact]
    public async Task DesktopApiClientChecksProviderHealthThroughProtocol()
    {
        var checkedAt = DateTimeOffset.Parse("2026-08-24T10:30:00+08:00");
        var pipeName = $"ScreenGuide.AiSettings.Tests.{Guid.NewGuid():N}";
        var server = ServeOnceAsync(pipeName, request =>
        {
            Assert.Equal(DesktopApiMethods.CheckAiProviderHealth, request.Method);
            var payload = request.Payload.Deserialize<ProviderIdRequestDto>(
                DesktopProtocolJson.Options);
            Assert.Equal("deepseek", payload!.ProviderId);
            return new DesktopApiResponse(
                request.RequestId,
                true,
                DesktopProtocolJson.ToElement(new AiProviderHealthDto(
                    "deepseek",
                    "Healthy",
                    IsConfigured: true,
                    "连接正常。",
                    checkedAt)));
        });
        var client = new DesktopApiClient(pipeName, TimeSpan.FromSeconds(5));

        var actual = await client.CheckAiProviderHealthAsync(
            new ProviderIdRequestDto("deepseek"));
        await server;

        Assert.Equal("Healthy", actual.State);
        Assert.Equal("连接正常。", actual.SafeMessage);
        Assert.Equal(checkedAt, actual.CheckedAtUtc);
    }

    private static AiSettingsDto Settings() => new(
        [new AiProviderSettingsDto(
            "deepseek",
            "DeepSeek",
            "DeepSeek API（中国境内云服务）",
            SendsDataOffDevice: true,
            ConfigurationState: "Configured",
            new AiProviderHealthDto(
                "deepseek",
                "Healthy",
                IsConfigured: true,
                "连接正常。"),
            [new AiModelSettingsDto("deepseek-v4-pro", "DeepSeek V4 Pro", ["Streaming"])])],
        new AiChatRouteDto("deepseek", "deepseek-v4-pro"),
        "Codex");

    private static async Task ServeOnceAsync(
        string pipeName,
        Func<DesktopApiRequest, DesktopApiResponse> responseFactory)
    {
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync();
        var request = await DesktopIpcFraming.ReadAsync<DesktopApiRequest>(server);
        await DesktopIpcFraming.WriteAsync(server, responseFactory(request));
    }
}
