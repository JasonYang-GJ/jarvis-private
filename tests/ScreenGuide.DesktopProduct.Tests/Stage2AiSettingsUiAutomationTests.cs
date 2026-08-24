using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ScreenGuide.AI.Core;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.FakeCodexCli;

namespace ScreenGuide.DesktopProduct.Tests;

public sealed class Stage2AiSettingsUiAutomationTests
{
    private const string FakeKey = "ds-fake-stage2-ui-canary-never-send";

    [Fact]
    [Trait("Category", "DesktopAcceptance")]
    public async Task ActualReleaseClientKeepsStage2RoutingCredentialsAndSessionVisible()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"screen-guide-stage2-ui-{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(testRoot, "user-data");
        Directory.CreateDirectory(dataRoot);
        await WriteCompletedOnboardingSettingsAsync(dataRoot);

        var codex = RecordingChatProvider.Codex();
        var deepSeek = RecordingChatProvider.DeepSeek();
        Process? clientProcess = null;
        IHost? host = null;
        try
        {
            var binaries = LocateReleaseBinaries();
            var pipeName = $"ScreenGuide.Stage2UiAcceptance.{Guid.NewGuid():N}";
            var options = new DesktopHostOptions(dataRoot, binaries.FakeCodexPath, pipeName);
            host = DesktopHostFactory.Build(
                [],
                options,
                services =>
                {
                    services.RemoveAll<IChatModelProvider>();
                    services.AddSingleton<IChatModelProvider>(codex);
                    services.AddSingleton<IChatModelProvider>(deepSeek);
                    services.RemoveAll<ISemanticIntentSuggester>();
                    services.AddSingleton<ISemanticIntentSuggester, NoSemanticIntentSuggester>();
                });
            await host.StartAsync();

            clientProcess = StartClient(binaries, dataRoot, pipeName);
            var api = new DesktopApiClient(pipeName, TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => api.PingAsync(), TimeSpan.FromSeconds(20));
            var clientWindow = await WaitForMainWindowAsync(
                clientProcess,
                dataRoot,
                TimeSpan.FromSeconds(15));
            var automationRoot = AutomationElement.FromHandle(clientWindow)
                ?? throw new InvalidOperationException("无法连接实际 DesktopClient 的 WPF 自动化树。");

            Invoke(await WaitForElementByNameAsync(
                automationRoot,
                "设置",
                ControlType.Button,
                TimeSpan.FromSeconds(10)));
            var providerCombo = await WaitForElementByAutomationIdAsync(
                automationRoot,
                "AiChatProvider",
                TimeSpan.FromSeconds(10));
            _ = await WaitForElementByNameAsync(
                automationRoot,
                "由下面选择的 Provider 和 Model 负责。",
                ControlType.Text,
                TimeSpan.FromSeconds(10));
            _ = await WaitForElementByNameAsync(
                automationRoot,
                "编程任务仍由 Codex 负责。",
                ControlType.Text,
                TimeSpan.FromSeconds(10));

            var modelCombo = await WaitForElementByAutomationIdAsync(
                automationRoot,
                "AiChatModel",
                TimeSpan.FromSeconds(10));
            Assert.Equal("Codex", SelectedItemName(providerCombo));
            Assert.Equal("Codex Chat", SelectedItemName(modelCombo));

            Invoke(await WaitForElementByNameAsync(
                automationRoot,
                "首页",
                ControlType.Button,
                TimeSpan.FromSeconds(10)));
            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "NewTopic",
                TimeSpan.FromSeconds(10)));
            var session = await WaitForSessionAsync(
                api,
                snapshot => snapshot is { Title: "新话题" },
                TimeSpan.FromSeconds(10));

            const string firstQuestion = "请记住阶段二第一轮代号是青石。";
            var firstTurn = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
                firstQuestion,
                "Text",
                $"stage2-ui-codex-{Guid.NewGuid():N}",
                session.SessionId));
            var firstCompleted = await WaitForTurnPhaseAsync(
                api,
                firstTurn.TurnId,
                "Completed",
                TimeSpan.FromSeconds(10));
            var codexRequest = Assert.Single(codex.Requests);
            Assert.Equal(firstTurn.TurnId, codexRequest.TurnId);
            Assert.Equal("codex-default", codexRequest.ModelId);
            Assert.Collection(
                codexRequest.Messages,
                message =>
                {
                    Assert.Equal(ChatMessageRole.User, message.Role);
                    Assert.Equal(firstQuestion, message.Content);
                });
            var firstReply = Assert.Single(
                firstCompleted.Messages,
                message => string.Equals(message.Role, "Assistant", StringComparison.Ordinal));
            _ = await WaitForElementByNameAsync(
                automationRoot,
                firstReply.Content,
                ControlType.Text,
                TimeSpan.FromSeconds(10));

            Invoke(await WaitForElementByNameAsync(
                automationRoot,
                "设置",
                ControlType.Button,
                TimeSpan.FromSeconds(10)));
            providerCombo = await WaitForElementByAutomationIdAsync(
                automationRoot,
                "AiChatProvider",
                TimeSpan.FromSeconds(10));
            modelCombo = await WaitForElementByAutomationIdAsync(
                automationRoot,
                "AiChatModel",
                TimeSpan.FromSeconds(10));
            await SelectComboBoxItemAsync(
                providerCombo,
                clientProcess.Id,
                "DeepSeek",
                TimeSpan.FromSeconds(10));
            await WaitUntilAsync(
                () => Task.FromResult(SelectedItemName(modelCombo) == "DeepSeek V4 Flash"),
                TimeSpan.FromSeconds(10));
            await SelectComboBoxItemAsync(
                modelCombo,
                clientProcess.Id,
                "DeepSeek V4 Pro",
                TimeSpan.FromSeconds(10));

            var destination = await WaitForElementByAutomationIdAsync(
                automationRoot,
                "AiDataDestination",
                TimeSpan.FromSeconds(10));
            Assert.Contains("https://api.deepseek.com", destination.Current.Name, StringComparison.Ordinal);
            Assert.Contains("离开本机", destination.Current.Name, StringComparison.Ordinal);

            var password = await WaitForElementByAutomationIdAsync(
                automationRoot,
                "AiCredentialPassword",
                TimeSpan.FromSeconds(10));
            Assert.True(password.Current.IsPassword);
            Assert.Equal("Provider 密钥，只能设置，不能查看", password.Current.Name);
            Assert.True(
                password.TryGetCurrentPattern(ValuePattern.Pattern, out var rawPasswordValue),
                "Provider 密钥输入框必须允许 UI Automation 安全写入。 ");
            ((ValuePattern)rawPasswordValue).SetValue(FakeKey);
            AssertNoAutomationNameContains(automationRoot, FakeKey);
            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "SaveAiCredential",
                TimeSpan.FromSeconds(10)));
            await WaitForAutomationTextAsync(
                automationRoot,
                "AiCredentialStatus",
                "已配置",
                TimeSpan.FromSeconds(10));
            Assert.True(password.Current.IsPassword);
            AssertNoAutomationNameContains(automationRoot, FakeKey);
            AssertNoPlaintextCanary(dataRoot, FakeKey);

            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "CheckAiProviderHealth",
                TimeSpan.FromSeconds(10)));
            await WaitForAutomationTextAsync(
                automationRoot,
                "AiProviderHealthStatus",
                "连接状态：正常",
                TimeSpan.FromSeconds(10));
            await WaitForAutomationTextAsync(
                automationRoot,
                "AiProviderHealthStatus",
                "DeepSeek Fake Provider 连接正常",
                TimeSpan.FromSeconds(10));

            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "SaveAiChatRoute",
                TimeSpan.FromSeconds(10)));
            await WaitUntilAsync(
                async () =>
                {
                    var settings = await api.GetAiSettingsAsync();
                    return settings.CurrentChatRoute is
                    {
                        ProviderId: "deepseek",
                        ModelId: "deepseek-v4-pro"
                    };
                },
                TimeSpan.FromSeconds(10));

            Invoke(await WaitForElementByNameAsync(
                automationRoot,
                "首页",
                ControlType.Button,
                TimeSpan.FromSeconds(10)));
            var continuedSession = await WaitForSessionAsync(
                api,
                snapshot => snapshot?.SessionId == session.SessionId,
                TimeSpan.FromSeconds(10));
            Assert.Equal(session.ConversationId, continuedSession.ConversationId);

            const string secondQuestion = "刚才那个代号是什么？请只引用它。";
            var secondTurn = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
                secondQuestion,
                "Text",
                $"stage2-ui-deepseek-{Guid.NewGuid():N}",
                session.SessionId));
            var secondCompleted = await WaitForTurnPhaseAsync(
                api,
                secondTurn.TurnId,
                "Completed",
                TimeSpan.FromSeconds(10));
            var deepSeekRequest = Assert.Single(deepSeek.Requests);
            Assert.Equal(secondTurn.TurnId, deepSeekRequest.TurnId);
            Assert.Equal("deepseek-v4-pro", deepSeekRequest.ModelId);
            Assert.Collection(
                deepSeekRequest.Messages,
                message => AssertChatMessage(message, ChatMessageRole.User, firstQuestion),
                message => AssertChatMessage(message, ChatMessageRole.Assistant, firstReply.Content),
                message => AssertChatMessage(message, ChatMessageRole.User, secondQuestion));
            var secondReply = secondCompleted.Messages.Last(message =>
                string.Equals(message.Role, "Assistant", StringComparison.Ordinal));
            _ = await WaitForElementByNameAsync(
                automationRoot,
                secondReply.Content,
                ControlType.Text,
                TimeSpan.FromSeconds(10));

            const string cancellationQuestion = "STAGE2_BLOCK_UNTIL_CANCEL";
            var cancelTurn = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
                cancellationQuestion,
                "Text",
                $"stage2-ui-cancel-{Guid.NewGuid():N}",
                session.SessionId));
            var providerTurnId = await deepSeek.BlockingTurnStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(10));
            Assert.Equal(cancelTurn.TurnId, providerTurnId);
            await WaitForTurnPhaseAsync(
                api,
                cancelTurn.TurnId,
                "Responding",
                TimeSpan.FromSeconds(10));
            var stop = await WaitForEnabledOnscreenElementAsync(
                automationRoot,
                "StopSession",
                TimeSpan.FromSeconds(10));
            Invoke(stop);
            Assert.Equal(
                providerTurnId,
                await deepSeek.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            var cancelled = await WaitForTurnPhaseAsync(
                api,
                cancelTurn.TurnId,
                "Cancelled",
                TimeSpan.FromSeconds(10));
            await WaitForAutomationTextAsync(
                automationRoot,
                "SessionStatus",
                "已取消",
                TimeSpan.FromSeconds(10));
            await Task.Delay(500);
            var afterCancellation = await api.GetCurrentSessionAsync();
            Assert.NotNull(afterCancellation);
            Assert.Equal(cancelled.SessionId, afterCancellation.SessionId);
            Assert.Equal(5, afterCancellation.Messages.Count);
            Assert.Equal("User", afterCancellation.Messages[^1].Role);
            Assert.Equal(cancellationQuestion, afterCancellation.Messages[^1].Content);
            Assert.DoesNotContain(
                afterCancellation.Messages,
                message => message.Content.Contains("迟到", StringComparison.Ordinal));

            Invoke(await WaitForElementByNameAsync(
                automationRoot,
                "设置",
                ControlType.Button,
                TimeSpan.FromSeconds(10)));
            await WaitForAutomationTextAsync(
                automationRoot,
                "AiCredentialStatus",
                "已配置",
                TimeSpan.FromSeconds(10));
            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "DeleteAiCredential",
                TimeSpan.FromSeconds(10)));
            var deleteDialog = await WaitForTopLevelAutomationWindowAsync(
                clientProcess.Id,
                "删除 Provider 密钥",
                TimeSpan.FromSeconds(10));
            AssertAutomationTreeContains(deleteDialog, "DeepSeek");
            AssertAutomationTreeContains(deleteDialog, "无法使用普通聊天");
            Invoke(await WaitForElementByAutomationIdAsync(
                deleteDialog,
                "7",
                TimeSpan.FromSeconds(10)));
            await WaitForTopLevelWindowToCloseAsync(
                clientProcess.Id,
                "删除 Provider 密钥",
                TimeSpan.FromSeconds(10));
            await WaitForAutomationTextAsync(
                automationRoot,
                "AiCredentialStatus",
                "已配置",
                TimeSpan.FromSeconds(10));

            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "DeleteAiCredential",
                TimeSpan.FromSeconds(10)));
            deleteDialog = await WaitForTopLevelAutomationWindowAsync(
                clientProcess.Id,
                "删除 Provider 密钥",
                TimeSpan.FromSeconds(10));
            Invoke(await WaitForElementByAutomationIdAsync(
                deleteDialog,
                "6",
                TimeSpan.FromSeconds(10)));
            await WaitForAutomationTextAsync(
                automationRoot,
                "AiCredentialStatus",
                "未配置",
                TimeSpan.FromSeconds(10));
            await WaitUntilAsync(
                async () =>
                {
                    var settings = await api.GetAiSettingsAsync();
                    return settings.Providers.Single(provider =>
                            string.Equals(provider.ProviderId, "deepseek", StringComparison.OrdinalIgnoreCase))
                        .ConfigurationState == "Missing";
                },
                TimeSpan.FromSeconds(10));
            AssertNoPlaintextCanary(dataRoot, FakeKey);
        }
        finally
        {
            await StopProcessAsync(clientProcess, entireProcessTree: false);
            if (host is not null)
            {
                await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
                host.Dispose();
            }

            await DeleteDirectoryWithRetryAsync(testRoot);
        }
    }

    private static async Task WriteCompletedOnboardingSettingsAsync(string dataRoot) =>
        await File.WriteAllTextAsync(
            Path.Combine(dataRoot, "client-settings.json"),
            JsonSerializer.Serialize(
                new
                {
                    startWithWindows = false,
                    runInBackground = false,
                    closeToTray = false,
                    notificationsEnabled = false,
                    onboardingCompleted = true,
                    notificationStateInitialized = true,
                    deliveredNotificationKeys = Array.Empty<string>()
                },
                new JsonSerializerOptions { WriteIndented = true }));

    private static Process StartClient(Binaries binaries, string dataRoot, string pipeName)
    {
        var info = new ProcessStartInfo
        {
            FileName = binaries.ClientPath,
            WorkingDirectory = Path.GetDirectoryName(binaries.ClientPath)!,
            UseShellExecute = false
        };
        info.Environment[DesktopHostOptions.DataDirectoryEnvironmentVariable] = dataRoot;
        info.Environment[DesktopHostOptions.PipeNameEnvironmentVariable] = pipeName;
        info.Environment["SCREEN_GUIDE_DESKTOP_HOST_PATH"] = binaries.HostPath;
        info.Environment[DesktopHostOptions.CodexExecutableEnvironmentVariable] = binaries.FakeCodexPath;
        info.ArgumentList.Add("--show");
        return Process.Start(info)
               ?? throw new InvalidOperationException("实际 Release DesktopClient 未启动。");
    }

    private static async Task<IntPtr> WaitForMainWindowAsync(
        Process process,
        string dataRoot,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            process.Refresh();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"DesktopClient 已退出，退出码 {process.ExitCode}；{ReadStartupError(dataRoot)}");
            }

            var window = FindTopLevelWindow(process.Id, "元枢");
            if (window != IntPtr.Zero)
            {
                return window;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"实际 DesktopClient 主窗口未出现；{ReadStartupError(dataRoot)}");
    }

    private static async Task<AutomationElement> WaitForElementByAutomationIdAsync(
        AutomationElement root,
        string automationId,
        TimeSpan timeout) =>
        await WaitForElementAsync(
            root,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId),
            timeout);

    private static async Task<AutomationElement> WaitForElementByNameAsync(
        AutomationElement root,
        string name,
        ControlType controlType,
        TimeSpan timeout) =>
        await WaitForElementAsync(
            root,
            new AndCondition(
                new PropertyCondition(AutomationElement.NameProperty, name),
                new PropertyCondition(AutomationElement.ControlTypeProperty, controlType)),
            timeout);

    private static async Task WaitForAutomationTextAsync(
        AutomationElement root,
        string automationId,
        string expectedText,
        TimeSpan timeout) =>
        await WaitUntilAsync(
            () =>
            {
                try
                {
                    var element = root.FindFirst(
                        TreeScope.Descendants,
                        new PropertyCondition(
                            AutomationElement.AutomationIdProperty,
                            automationId));
                    return Task.FromResult(
                        element?.Current.Name.Contains(expectedText, StringComparison.Ordinal)
                        == true);
                }
                catch (Exception exception) when (
                    exception is ElementNotAvailableException or COMException)
                {
                    return Task.FromResult(false);
                }
            },
            timeout);

    private static async Task<AutomationElement> WaitForEnabledOnscreenElementAsync(
        AutomationElement root,
        string automationId,
        TimeSpan timeout)
    {
        AutomationElement? match = null;
        await WaitUntilAsync(
            () =>
            {
                try
                {
                    match = root.FindFirst(
                        TreeScope.Descendants,
                        new PropertyCondition(
                            AutomationElement.AutomationIdProperty,
                            automationId));
                    return Task.FromResult(match is not null
                                           && match.Current.IsEnabled
                                           && !match.Current.IsOffscreen);
                }
                catch (Exception exception) when (
                    exception is ElementNotAvailableException or COMException)
                {
                    return Task.FromResult(false);
                }
            },
            timeout);
        return match!;
    }

    private static async Task<AutomationElement> WaitForElementAsync(
        AutomationElement root,
        Condition condition,
        TimeSpan timeout)
    {
        AutomationElement? match = null;
        await WaitUntilAsync(
            () =>
            {
                try
                {
                    match = root.FindFirst(TreeScope.Descendants, condition);
                    return Task.FromResult(match is not null);
                }
                catch (Exception exception) when (exception is ElementNotAvailableException or COMException)
                {
                    return Task.FromResult(false);
                }
            },
            timeout);
        return match!;
    }

    private static async Task SelectComboBoxItemAsync(
        AutomationElement comboBox,
        int processId,
        string itemName,
        TimeSpan timeout)
    {
        Assert.True(
            comboBox.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var rawExpand),
            $"ComboBox {comboBox.Current.AutomationId} 不支持展开。");
        var expand = (ExpandCollapsePattern)rawExpand;
        expand.Expand();
        try
        {
            var (item, selection) = await WaitForSelectableListItemAsync(
                new AndCondition(
                    new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
                    new PropertyCondition(AutomationElement.NameProperty, itemName),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)),
                timeout);
            selection.Select();

            await WaitUntilAsync(
                () => Task.FromResult(SelectedItemName(comboBox) == itemName),
                timeout);
        }
        finally
        {
            try
            {
                expand.Collapse();
            }
            catch (ElementNotAvailableException)
            {
            }
        }
    }

    private static async Task<(AutomationElement Item, SelectionItemPattern Selection)>
        WaitForSelectableListItemAsync(
            Condition condition,
            TimeSpan timeout)
    {
        AutomationElement? match = null;
        SelectionItemPattern? selection = null;
        await WaitUntilAsync(
            () =>
            {
                try
                {
                    var candidates = AutomationElement.RootElement.FindAll(
                        TreeScope.Descendants,
                        condition);
                    for (var index = 0; index < candidates.Count; index++)
                    {
                        var candidate = candidates[index];
                        if (!candidate.TryGetCurrentPattern(
                                SelectionItemPattern.Pattern,
                                out var rawSelection))
                        {
                            continue;
                        }

                        match = candidate;
                        selection = (SelectionItemPattern)rawSelection;
                        return Task.FromResult(true);
                    }

                    return Task.FromResult(false);
                }
                catch (Exception exception) when (
                    exception is ElementNotAvailableException or COMException)
                {
                    return Task.FromResult(false);
                }
            },
            timeout);

        return (match!, selection!);
    }

    private static string? SelectedItemName(AutomationElement comboBox)
    {
        try
        {
            if (!comboBox.TryGetCurrentPattern(SelectionPattern.Pattern, out var rawSelection))
            {
                return null;
            }

            return ((SelectionPattern)rawSelection).Current.GetSelection().SingleOrDefault()?.Current.Name;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or COMException)
        {
            return null;
        }
    }

    private static async Task<SessionSnapshotDto> WaitForSessionAsync(
        IDesktopApiClient api,
        Func<SessionSnapshotDto?, bool> condition,
        TimeSpan timeout)
    {
        SessionSnapshotDto? snapshot = null;
        await WaitUntilAsync(
            async () =>
            {
                snapshot = await api.GetCurrentSessionAsync();
                return condition(snapshot);
            },
            timeout);
        return snapshot!;
    }

    private static async Task<SessionSnapshotDto> WaitForTurnPhaseAsync(
        IDesktopApiClient api,
        Guid turnId,
        string expectedPhase,
        TimeSpan timeout) =>
        await WaitForSessionAsync(
            api,
            snapshot => snapshot?.Turns.Any(turn =>
                turn.Id == turnId
                && string.Equals(turn.Phase, expectedPhase, StringComparison.Ordinal)) == true,
            timeout);

    private static async Task<AutomationElement> WaitForTopLevelAutomationWindowAsync(
        int processId,
        string title,
        TimeSpan timeout)
    {
        AutomationElement? window = null;
        await WaitUntilAsync(
            () =>
            {
                var handle = FindTopLevelWindow(processId, title);
                window = handle == IntPtr.Zero ? null : AutomationElement.FromHandle(handle);
                return Task.FromResult(window is not null);
            },
            timeout);
        return window!;
    }

    private static async Task WaitForTopLevelWindowToCloseAsync(
        int processId,
        string title,
        TimeSpan timeout) =>
        await WaitUntilAsync(
            () => Task.FromResult(FindTopLevelWindow(processId, title) == IntPtr.Zero),
            timeout);

    private static void AssertNoAutomationNameContains(AutomationElement root, string secret)
    {
        var elements = root.FindAll(TreeScope.Subtree, Condition.TrueCondition);
        foreach (AutomationElement element in elements)
        {
            Assert.DoesNotContain(secret, element.Current.Name, StringComparison.Ordinal);
        }
    }

    private static void AssertAutomationTreeContains(AutomationElement root, string expectedText)
    {
        var elements = root.FindAll(TreeScope.Subtree, Condition.TrueCondition);
        Assert.Contains(
            elements.Cast<AutomationElement>(),
            element => element.Current.Name.Contains(expectedText, StringComparison.Ordinal));
    }

    private static void AssertNoPlaintextCanary(string dataRoot, string canary)
    {
        var needle = Encoding.UTF8.GetBytes(canary);
        foreach (var file in Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories))
        {
            using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Assert.False(
                StreamContains(stream, needle),
                $"隔离数据目录不应明文保存 Fake Key：{file}");
        }
    }

    private static bool StreamContains(Stream stream, byte[] needle)
    {
        var buffer = new byte[8 * 1024 + needle.Length];
        var preserved = 0;
        while (true)
        {
            var read = stream.Read(buffer, preserved, 8 * 1024);
            if (read == 0)
            {
                return false;
            }

            var available = preserved + read;
            if (buffer.AsSpan(0, available).IndexOf(needle) >= 0)
            {
                return true;
            }

            preserved = Math.Min(needle.Length - 1, available);
            buffer.AsSpan(available - preserved, preserved).CopyTo(buffer);
        }
    }

    private static void AssertChatMessage(
        ChatMessage actual,
        ChatMessageRole expectedRole,
        string expectedContent)
    {
        Assert.Equal(expectedRole, actual.Role);
        Assert.Equal(expectedContent, actual.Content);
    }

    private static void Invoke(AutomationElement element)
    {
        Assert.True(element.Current.IsEnabled, $"控件 {element.Current.Name} 当前不可用。");
        Assert.True(
            element.TryGetCurrentPattern(InvokePattern.Pattern, out var rawPattern),
            $"控件 {element.Current.Name} 不支持 InvokePattern。");
        ((InvokePattern)rawPattern).Invoke();
    }

    private static async Task SelectComboBoxItemWithKeyboardAsync(
        AutomationElement comboBox,
        AutomationElement item)
    {
        var position = item.GetCurrentPropertyValue(
            AutomationElement.PositionInSetProperty,
            ignoreDefaultValue: true);
        var oneBasedPosition = position is int reportedPosition && reportedPosition > 0
            ? reportedPosition
            : PositionAmongListItemSiblings(item) + 1;
        Assert.True(oneBasedPosition > 0, $"无法确定 {item.Current.Name} 的下拉列表位置。");

        comboBox.SetFocus();
        await Task.Delay(75);
        SendVirtualKey(0x24); // Home
        for (var index = 1; index < oneBasedPosition; index++)
        {
            await Task.Delay(50);
            SendVirtualKey(0x28); // Down
        }

        await Task.Delay(75);
        SendVirtualKey(0x0D); // Enter
        await Task.Delay(150);
    }

    private static int PositionAmongListItemSiblings(AutomationElement item)
    {
        var parent = TreeWalker.ControlViewWalker.GetParent(item);
        if (parent is null)
        {
            return -1;
        }

        var siblings = parent.FindAll(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        var targetRuntimeId = item.GetRuntimeId();
        for (var index = 0; index < siblings.Count; index++)
        {
            if (siblings[index].GetRuntimeId().SequenceEqual(targetRuntimeId))
            {
                return index;
            }
        }

        return -1;
    }

    private static void SendVirtualKey(byte virtualKey)
    {
        KeyboardEvent(virtualKey, 0, 0, UIntPtr.Zero);
        KeyboardEvent(virtualKey, 0, 0x0002, UIntPtr.Zero);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or DesktopApiException
                    or ElementNotAvailableException
                    or COMException)
            {
                lastError = exception;
            }

            await Task.Delay(150);
        }

        throw new TimeoutException(
            lastError is null
                ? "Stage 2 真实桌面验收条件超时。"
                : $"Stage 2 真实桌面验收条件超时：{lastError.Message}");
    }

    private static IntPtr FindTopLevelWindow(int processId, string expectedTitle)
    {
        var match = IntPtr.Zero;
        EnumWindows(
            (window, ignored) =>
            {
                _ = GetWindowThreadProcessId(window, out var ownerProcessId);
                if (ownerProcessId != processId || !IsWindowVisible(window))
                {
                    return true;
                }

                var length = GetWindowTextLength(window);
                var title = new StringBuilder(Math.Max(1, length + 1));
                _ = GetWindowText(window, title, title.Capacity);
                if (!string.Equals(title.ToString(), expectedTitle, StringComparison.Ordinal))
                {
                    return true;
                }

                match = window;
                return false;
            },
            IntPtr.Zero);
        return match;
    }

    private static string ReadStartupError(string dataRoot)
    {
        var errorPath = Path.Combine(dataRoot, "state", "client-startup-error.json");
        var stagePath = Path.Combine(dataRoot, "state", "client-startup-stage.txt");
        return $"stage={(File.Exists(stagePath) ? File.ReadAllText(stagePath) : "none")}; "
               + $"error={(File.Exists(errorPath) ? File.ReadAllText(errorPath) : "none")}";
    }

    private static async Task StopProcessAsync(Process? process, bool entireProcessTree)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path)
    {
        var normalized = Path.GetFullPath(path);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        Assert.StartsWith(temporaryRoot, normalized, StringComparison.OrdinalIgnoreCase);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                if (Directory.Exists(normalized))
                {
                    Directory.Delete(normalized, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException
                    && attempt < 7)
            {
                await Task.Delay(250);
            }
        }
    }

    private static Binaries LocateReleaseBinaries()
    {
        var repository = FindRepositoryRoot();
        const string framework = "net10.0-windows10.0.19041.0";
        var client = Path.Combine(
            repository,
            "src",
            "ScreenGuide.DesktopClient",
            "bin",
            "Release",
            framework,
            "ScreenGuide.DesktopClient.exe");
        var host = Path.Combine(
            repository,
            "src",
            "ScreenGuide.DesktopHost",
            "bin",
            "Release",
            framework,
            "ScreenGuide.DesktopHost.exe");
        var fakeCodex = Path.ChangeExtension(typeof(FakeCodexMarker).Assembly.Location, ".exe");
        Assert.True(File.Exists(client), $"缺少实际 Release DesktopClient：{client}");
        Assert.True(File.Exists(host), $"缺少实际 Release DesktopHost：{host}");
        Assert.True(File.Exists(fakeCodex), $"缺少 FakeCodex：{fakeCodex}");
        return new Binaries(client, host, fakeCodex);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "ScreenGuide.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("没有找到 ScreenGuide 仓库根目录。");
    }

    private sealed record Binaries(string ClientPath, string HostPath, string FakeCodexPath);

    private sealed class NoSemanticIntentSuggester : ISemanticIntentSuggester
    {
        public Task<SemanticIntentSuggestion?> SuggestAsync(
            Guid sessionTurnId,
            FrozenChatModelRoute frozenRoute,
            string text,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SemanticIntentSuggestion?>(null);
    }

    private sealed class RecordingChatProvider : IChatModelProvider
    {
        private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();

        private RecordingChatProvider(ChatProviderDescriptor descriptor) => Descriptor = descriptor;

        public ChatProviderDescriptor Descriptor { get; }

        public ConcurrentQueue<ObservedRequest> Requests { get; } = new();

        public TaskCompletionSource<Guid> BlockingTurnStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<Guid> CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static RecordingChatProvider Codex() => new(new ChatProviderDescriptor(
            "codex",
            "Codex",
            "OpenAI Codex cloud（Fake acceptance provider）",
            SendsDataOffDevice: true,
            [new ChatModelDescriptor(
                "codex-default",
                "Codex Chat",
                ChatModelCapabilities.Streaming | ChatModelCapabilities.JsonObjectOutput)],
            ChatProviderCredentialKind.None,
            ChatProviderWorkloads.OrdinaryChat | ChatProviderWorkloads.ProgrammingAgent));

        public static RecordingChatProvider DeepSeek() => new(new ChatProviderDescriptor(
            "deepseek",
            "DeepSeek",
            "https://api.deepseek.com",
            SendsDataOffDevice: true,
            [
                new ChatModelDescriptor(
                    "deepseek-v4-flash",
                    "DeepSeek V4 Flash",
                    ChatModelCapabilities.Streaming | ChatModelCapabilities.JsonObjectOutput),
                new ChatModelDescriptor(
                    "deepseek-v4-pro",
                    "DeepSeek V4 Pro",
                    ChatModelCapabilities.Streaming | ChatModelCapabilities.JsonObjectOutput)
            ],
            ChatProviderCredentialKind.ApiKey));

        public Task<ChatProviderHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatProviderHealth(
                Descriptor.ProviderId,
                ChatProviderHealthState.Healthy,
                IsConfigured: true,
                $"{Descriptor.DisplayName} Fake Provider 连接正常。",
                DateTimeOffset.UtcNow));

        public async Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            using var local = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                local.Token);
            if (!_active.TryAdd(request.TurnId, local))
            {
                throw new InvalidOperationException("Turn already active in fake provider.");
            }

            try
            {
                Requests.Enqueue(new ObservedRequest(
                    request.TurnId,
                    Descriptor.ProviderId,
                    request.ModelId,
                    request.Messages.ToArray()));
                var latest = request.Messages.Last().Content;
                if (string.Equals(
                        latest,
                        "STAGE2_BLOCK_UNTIL_CANCEL",
                        StringComparison.Ordinal))
                {
                    BlockingTurnStarted.TrySetResult(request.TurnId);
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested)
                    {
                        CancellationObserved.TrySetResult(request.TurnId);
                        throw new ChatModelException(
                            Descriptor.ProviderId,
                            request.ModelId,
                            new ChatModelError(
                                ChatModelErrorKind.Cancelled,
                                "fake_cancelled",
                                "回答已停止。",
                                IsRetryable: false));
                    }
                }

                var text = $"{Descriptor.DisplayName}/{request.ModelId} 已回答：{latest}";
                return new ChatModelResponse(
                    text,
                    ChatFinishReason.Stop,
                    new ChatModelUsage(10, 5, 15),
                    new ChatProviderMetadata(
                        Descriptor.ProviderId,
                        request.ModelId,
                        $"fake-{Guid.NewGuid():N}",
                        Descriptor.DataDestination));
            }
            finally
            {
                _active.TryRemove(request.TurnId, out _);
            }
        }

        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_active.TryGetValue(turnId, out var active))
            {
                active.Cancel();
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            foreach (var active in _active.Values)
            {
                active.Cancel();
            }

            _active.Clear();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record ObservedRequest(
        Guid TurnId,
        string ProviderId,
        string ModelId,
        IReadOnlyList<ChatMessage> Messages);

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void KeyboardEvent(
        byte virtualKey,
        byte scanCode,
        uint flags,
        UIntPtr extraInfo);
}
