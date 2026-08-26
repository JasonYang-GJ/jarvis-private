using ScreenGuide.Agent.Codex;

namespace ScreenGuide.Agent.Codex.Tests;

public sealed class CodexOptionsBoundaryTests
{
    [Fact]
    public void ChatOptionsAreIndependentFromProgrammingOptions()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"screen-guide-codex-options-{Guid.NewGuid():N}");
        var executablePath = Path.Combine(dataDirectory, "codex.exe");
        var chatOptions = new CodexChatOptions(dataDirectory, executablePath)
        {
            Model = "shared-model",
            ChatRequestTimeout = TimeSpan.FromSeconds(30)
        };
        var programmingOptions = new CodexConnectorOptions(dataDirectory, executablePath)
        {
            Model = "shared-model",
            SandboxMode = "read-only"
        };

        Assert.NotEqual(chatOptions.GetType(), programmingOptions.GetType());
        Assert.Equal(programmingOptions.DataDirectory, chatOptions.DataDirectory);
        Assert.Equal(programmingOptions.ExecutablePath, chatOptions.ExecutablePath);
        Assert.Equal(programmingOptions.Model, chatOptions.Model);
        Assert.Null(typeof(CodexChatOptions).GetProperty(nameof(CodexConnectorOptions.SandboxMode)));
    }

    [Fact]
    public void ChatModelProviderAcceptsOnlyChatOptions()
    {
        var constructor = Assert.Single(typeof(CodexChatModelProvider).GetConstructors());
        var parameter = Assert.Single(constructor.GetParameters());

        Assert.Equal(typeof(CodexChatOptions), parameter.ParameterType);
    }

    [Fact]
    public void LegacyConversationProviderAcceptsOnlyChatOptions()
    {
        var constructor = Assert.Single(typeof(CodexConversationProvider).GetConstructors());
        var parameter = Assert.Single(constructor.GetParameters());

        Assert.Equal(typeof(CodexChatOptions), parameter.ParameterType);
    }

    [Fact]
    public void ProgrammingConnectorOptionsExcludeChatOnlySettings()
    {
        var constructor = Assert.Single(typeof(CodexConnector).GetConstructors());
        var optionsParameter = constructor.GetParameters()[0];

        Assert.Equal(typeof(CodexConnectorOptions), optionsParameter.ParameterType);
        Assert.NotNull(typeof(CodexConnectorOptions).GetProperty(nameof(CodexConnectorOptions.SandboxMode)));
        Assert.Null(typeof(CodexConnectorOptions).GetProperty(nameof(CodexChatOptions.ChatRequestTimeout)));
    }
}
