using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ScreenGuide.Core.Conversations;

namespace ScreenGuide.Agent.Codex;

public sealed class CodexConversationProvider : IConversationProvider
{
    private readonly CodexConnectorOptions _options;
    private readonly CodexCapabilityProbe _capabilityProbe;
    private readonly SemaphoreSlim _capabilityGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, ActiveConversation> _active = new();
    private CodexCapability? _capability;
    private bool _disposed;

    public CodexConversationProvider(CodexConnectorOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _capabilityProbe = new CodexCapabilityProbe(new CodexExecutableLocator(options));
    }

    public string ProviderId => "codex-conversation";

    public async Task<ConversationProviderResult> SendAsync(
        ConversationProviderRequest request,
        Func<string, int, Task>? started = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            throw new ArgumentException("对话内容不能为空。", nameof(request));
        }

        var active = new ActiveConversation(request.ExternalThreadId);
        if (!_active.TryAdd(request.ConversationId, active))
        {
            throw new InvalidOperationException("这个对话仍有一条消息正在生成回答。");
        }

        using var cancellationRegistration = cancellationToken.Register(
            static state => RequestCancellation((ActiveConversation)state!),
            active);

        Process? process = null;
        WindowsProcessJob? job = null;
        try
        {
            var capability = await EnsureCapabilityAsync(cancellationToken).ConfigureAwait(false);
            var workspace = Path.Combine(_options.DataDirectory, "conversation-workspace");
            Directory.CreateDirectory(workspace);
            var schemaPath = await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            process = CreateProcess(capability, workspace, schemaPath, request.ExternalThreadId);
            job = new WindowsProcessJob();
            if (!process.Start())
            {
                throw new InvalidOperationException("Codex 对话进程未能启动。");
            }

            try
            {
                job.Assign(process);
            }
            catch
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            lock (active.Gate)
            {
                active.Process = process;
                active.Job = job;
                active.ProcessId = process.Id;
                if (active.CancellationRequested)
                {
                    job.Terminate();
                }
            }

            if (!string.IsNullOrWhiteSpace(request.ExternalThreadId) && started is not null)
            {
                await started(request.ExternalThreadId, process.Id).ConfigureAwait(false);
                active.StartReported = true;
            }

            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await process.StandardInput.WriteAsync(
                    BuildPrompt(request.Message).AsMemory(),
                    CancellationToken.None)
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            process.StandardInput.Close();

            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)
                   is { } line)
            {
                if (!CodexJsonLineParser.TryParse(line, out var protocolEvent, out _)
                    || protocolEvent is null)
                {
                    continue;
                }

                switch (protocolEvent.Type)
                {
                    case "thread.started":
                        if (string.IsNullOrWhiteSpace(protocolEvent.ThreadId))
                        {
                            throw new InvalidDataException("Codex 对话没有返回 Thread ID。");
                        }

                        if (active.ExternalThreadId is not null
                            && !string.Equals(
                                active.ExternalThreadId,
                                protocolEvent.ThreadId,
                                StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("Codex 对话续接返回了不同的 Thread ID。");
                        }

                        active.ExternalThreadId = protocolEvent.ThreadId;
                        if (!active.StartReported && started is not null)
                        {
                            await started(protocolEvent.ThreadId, process.Id).ConfigureAwait(false);
                            active.StartReported = true;
                        }

                        break;

                    case "item.started":
                    case "item.updated":
                    case "item.completed":
                        if (!string.IsNullOrWhiteSpace(protocolEvent.AgentMessageText))
                        {
                            active.LastAgentMessage = protocolEvent.AgentMessageText;
                            active.ProviderMessageId = protocolEvent.ItemId;
                        }

                        break;

                    case "turn.failed":
                        active.AuthoritativeTerminalReceived = true;
                        active.Outcome = ConversationProviderOutcome.Failed;
                        active.FailureCode = "codex_turn_failed";
                        active.FailureMessage = Sanitize(protocolEvent.Message ?? "Codex 对话明确失败。");
                        break;

                    case "turn.completed":
                        active.AuthoritativeTerminalReceived = true;
                        active.Outcome = ConversationProviderOutcome.Succeeded;
                        break;
                }
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            var stderr = Sanitize(await stderrTask.ConfigureAwait(false));
            if (active.CancellationRequested || cancellationToken.IsCancellationRequested)
            {
                return Result(active, ConversationProviderOutcome.Cancelled, null, "cancelled", "回答已停止。");
            }

            if (!active.AuthoritativeTerminalReceived)
            {
                return Result(
                    active,
                    string.IsNullOrWhiteSpace(active.ExternalThreadId)
                        ? ConversationProviderOutcome.Failed
                        : ConversationProviderOutcome.Interrupted,
                    null,
                    "missing_terminal_event",
                    string.IsNullOrWhiteSpace(stderr)
                        ? "Codex 对话进程退出，但没有返回权威完成事件。"
                        : stderr);
            }

            if (active.Outcome == ConversationProviderOutcome.Failed)
            {
                return Result(
                    active,
                    ConversationProviderOutcome.Failed,
                    null,
                    active.FailureCode,
                    active.FailureMessage);
            }

            if (!TryParseReply(active.LastAgentMessage, out var reply, out var parseError))
            {
                return Result(
                    active,
                    ConversationProviderOutcome.Failed,
                    null,
                    "invalid_conversation_result",
                    parseError);
            }

            return Result(active, ConversationProviderOutcome.Succeeded, reply, null, null);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested || active.CancellationRequested)
        {
            return Result(
                active,
                ConversationProviderOutcome.Cancelled,
                null,
                "cancelled",
                "回答已停止。");
        }
        catch (Exception exception)
        {
            return Result(
                active,
                active.CancellationRequested
                    ? ConversationProviderOutcome.Cancelled
                    : string.IsNullOrWhiteSpace(active.ExternalThreadId)
                        ? ConversationProviderOutcome.Failed
                        : ConversationProviderOutcome.Interrupted,
                null,
                exception.GetType().Name,
                Sanitize(exception.Message));
        }
        finally
        {
            try
            {
                if (job is not null && job.HasActiveProcesses)
                {
                    job.Terminate();
                }
            }
            catch
            {
                // Job Object disposal remains the final process-tree cleanup boundary.
            }
            finally
            {
                job?.Dispose();
                process?.Dispose();
                _active.TryRemove(request.ConversationId, out _);
            }
        }
    }

    public Task CancelAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_active.TryGetValue(conversationId, out var active))
        {
            return Task.CompletedTask;
        }

        RequestCancellation(active);

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var active in _active.Values)
        {
            lock (active.Gate)
            {
                active.CancellationRequested = true;
                try
                {
                    active.Job?.Terminate();
                }
                catch
                {
                }
            }
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!_active.IsEmpty && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        _capabilityGate.Dispose();
    }

    private async Task<CodexCapability> EnsureCapabilityAsync(CancellationToken cancellationToken)
    {
        if (_capability is not null)
        {
            return _capability;
        }

        await _capabilityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _capability ??= await _capabilityProbe.ProbeAsync(cancellationToken)
                .ConfigureAwait(false);
            return _capability;
        }
        finally
        {
            _capabilityGate.Release();
        }
    }

    private Process CreateProcess(
        CodexCapability capability,
        string workspace,
        string schemaPath,
        string? externalThreadId)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = capability.ExecutablePath,
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("exec");
        if (string.IsNullOrWhiteSpace(externalThreadId))
        {
            startInfo.ArgumentList.Add("--json");
            startInfo.ArgumentList.Add("--skip-git-repo-check");
            startInfo.ArgumentList.Add("--ignore-user-config");
            startInfo.ArgumentList.Add("--ignore-rules");
            startInfo.ArgumentList.Add("--sandbox");
            startInfo.ArgumentList.Add("read-only");
            startInfo.ArgumentList.Add("--config");
            startInfo.ArgumentList.Add("approval_policy=\"never\"");
            startInfo.ArgumentList.Add("--cd");
            startInfo.ArgumentList.Add(workspace);
            startInfo.ArgumentList.Add("--output-schema");
            startInfo.ArgumentList.Add(schemaPath);
        }
        else
        {
            startInfo.ArgumentList.Add("resume");
            startInfo.ArgumentList.Add("--json");
            startInfo.ArgumentList.Add("--skip-git-repo-check");
            startInfo.ArgumentList.Add("--ignore-user-config");
            startInfo.ArgumentList.Add("--ignore-rules");
            startInfo.ArgumentList.Add("--config");
            startInfo.ArgumentList.Add("approval_policy=\"never\"");
            startInfo.ArgumentList.Add("--output-schema");
            startInfo.ArgumentList.Add(schemaPath);
            startInfo.ArgumentList.Add(externalThreadId);
        }

        if (!string.IsNullOrWhiteSpace(_options.Model))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(_options.Model);
        }

        startInfo.ArgumentList.Add("-");
        return new Process { StartInfo = startInfo };
    }

    private async Task<string> EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.DataDirectory);
        var path = Path.Combine(_options.DataDirectory, "conversation-output.schema.json");
        const string schema = """
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["reply"],
              "properties": {
                "reply": { "type": "string", "minLength": 1 }
              }
            }
            """;
        if (!File.Exists(path)
            || !string.Equals(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), schema, StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(path, schema, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
        }

        return path;
    }

    private static string BuildPrompt(string message) =>
        $$"""
        你正在元枢的“只聊天”通道中。这里没有授予任何电脑操作、项目修改、文件读取、命令执行、网页访问或外部工具调用权限。
        不要运行命令，不要访问或修改任何文件，不要调用工具，也不要声称已经执行了现实操作。
        请直接用简体中文回答用户问题；如果用户要求执行操作，请说明当前是只聊天模式，并引导用户到“新建任务”或“电脑操作”页面明确授权。
        回答要自然、准确、简洁，并严格按输出 Schema 返回 JSON。

        用户消息：
        {{message.Trim()}}
        """;

    private static bool TryParseReply(string? value, out string? reply, out string? error)
    {
        reply = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Codex 没有返回对话内容。";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            if (!document.RootElement.TryGetProperty("reply", out var element)
                || element.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(element.GetString()))
            {
                error = "Codex 对话结果缺少 reply。";
                return false;
            }

            reply = element.GetString()!.Trim();
            return true;
        }
        catch (JsonException exception)
        {
            error = $"Codex 对话结果无法解析：{Sanitize(exception.Message)}";
            return false;
        }
    }

    private static ConversationProviderResult Result(
        ActiveConversation active,
        ConversationProviderOutcome outcome,
        string? reply,
        string? failureCode,
        string? failureMessage) =>
        new(
            outcome,
            active.ExternalThreadId,
            reply,
            active.ProviderMessageId,
            active.ProcessId,
            failureCode,
            failureMessage);

    private static void RequestCancellation(ActiveConversation active)
    {
        lock (active.Gate)
        {
            active.CancellationRequested = true;
            try
            {
                active.Job?.Terminate();
            }
            catch
            {
                // Job disposal is still the final process-tree cleanup boundary.
            }
        }
    }

    private static string Sanitize(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "没有更多错误信息。" : value.ReplaceLineEndings(" ").Trim();
        return text.Length <= 500 ? text : text[..500];
    }

    private sealed class ActiveConversation(string? externalThreadId)
    {
        public object Gate { get; } = new();

        public string? ExternalThreadId { get; set; } = externalThreadId;

        public Process? Process { get; set; }

        public WindowsProcessJob? Job { get; set; }

        public int? ProcessId { get; set; }

        public bool CancellationRequested { get; set; }

        public bool StartReported { get; set; }

        public bool AuthoritativeTerminalReceived { get; set; }

        public ConversationProviderOutcome Outcome { get; set; }

        public string? LastAgentMessage { get; set; }

        public string? ProviderMessageId { get; set; }

        public string? FailureCode { get; set; }

        public string? FailureMessage { get; set; }
    }
}
