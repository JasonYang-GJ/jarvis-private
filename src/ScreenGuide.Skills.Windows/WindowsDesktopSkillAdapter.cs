using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows;

public static class WindowsDesktopCapabilities
{
    public const string OpenApplication = "desktop.application.open";
    public const string OpenWebsite = "desktop.website.open";
}

public sealed record KnownDesktopApplication(
    string Id,
    string DisplayName,
    string LaunchTarget);

public sealed record WindowsDesktopActionInput(string ActionKind, string Target);

public interface IDesktopProcessLauncher
{
    int? Start(string target);
}

public sealed class DesktopProcessLauncher : IDesktopProcessLauncher
{
    public int? Start(string target)
    {
        var process = Process.Start(new ProcessStartInfo(target)
        {
            UseShellExecute = true
        });
        return process?.Id;
    }
}

public static class SafeWebsitePolicy
{
    public static Uri RequireHttps(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048)
        {
            throw new ArgumentException("请输入有效的网站地址。", nameof(value));
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            throw new ArgumentException("当前只允许打开不含账号信息的 https 网站。", nameof(value));
        }

        return uri;
    }

    public static string ForAudit(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri.AbsoluteUri;
    }
}

public sealed class WindowsDesktopSkillAdapter(IDesktopProcessLauncher launcher) : ISkillAdapter
{
    private static readonly IReadOnlyDictionary<string, KnownDesktopApplication> ApplicationMap =
        new Dictionary<string, KnownDesktopApplication>(StringComparer.Ordinal)
        {
            ["file-explorer"] = new("file-explorer", "文件资源管理器", "explorer.exe"),
            ["notepad"] = new("notepad", "记事本", "notepad.exe"),
            ["calculator"] = new("calculator", "计算器", "calc.exe"),
            ["paint"] = new("paint", "画图", "mspaint.exe"),
            ["windows-settings"] = new("windows-settings", "Windows 设置", "ms-settings:")
        };

    private readonly ConcurrentDictionary<Guid, RunState> _runs = new();

    public SkillDescriptor Descriptor { get; } = new(
        "windows.safe-launch",
        "0.2.0",
        "Windows 安全启动",
        true,
        new HashSet<string>(StringComparer.Ordinal)
        {
            WindowsDesktopCapabilities.OpenApplication,
            WindowsDesktopCapabilities.OpenWebsite
        });

    public IReadOnlyList<KnownDesktopApplication> Applications =>
        ApplicationMap.Values.OrderBy(item => item.DisplayName, StringComparer.CurrentCulture).ToArray();

    public Task<SkillStartResult> StartAsync(
        SkillInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(request.SkillId, Descriptor.Id, StringComparison.Ordinal)
            || !Descriptor.Capabilities.Contains(request.Capability))
        {
            throw new UnauthorizedAccessException("请求的桌面能力与安全启动技能不匹配。");
        }

        var input = JsonSerializer.Deserialize<WindowsDesktopActionInput>(request.InputJson)
            ?? throw new InvalidDataException("桌面操作内容无效。");
        var now = DateTimeOffset.UtcNow;
        var (launchTarget, message) = ResolveTarget(request.Capability, input);
        var processId = launcher.Start(launchTarget);
        var run = new SkillRunReference(request.TaskId, request.InvocationId, null, request.AttemptId);
        _runs[request.InvocationId] = new RunState(
            run,
            now,
            now,
            processId,
            message,
            SkillExecutionStatus.Succeeded);
        return Task.FromResult(new SkillStartResult(
            run,
            SkillExecutionStatus.Succeeded,
            now,
            Descriptor.Version,
            processId));
    }

    public Task<SkillStatusSnapshot> GetStatusAsync(
        SkillRunReference run,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_runs.TryGetValue(run.InvocationId, out var state)
            ? new SkillStatusSnapshot(run, state.Status, DateTimeOffset.UtcNow, state.Message)
            : new SkillStatusSnapshot(run, SkillExecutionStatus.Unknown, DateTimeOffset.UtcNow));
    }

    public async IAsyncEnumerable<SkillEvent> GetEventsAsync(
        SkillRunReference run,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_runs.TryGetValue(run.InvocationId, out var state))
        {
            yield break;
        }

        if (afterSequence < 1)
        {
            yield return new SkillEvent(
                1,
                SkillEventKind.Started,
                state.StartedAtUtc,
                "Windows 已接受本次启动请求。",
                Status: SkillExecutionStatus.Running,
                ExternalEventId: $"{run.InvocationId:N}:started");
        }

        if (afterSequence < 2)
        {
            yield return new SkillEvent(
                2,
                SkillEventKind.Completed,
                state.CompletedAtUtc,
                state.Message,
                Status: state.Status,
                ExternalEventId: $"{run.InvocationId:N}:completed");
        }

        await Task.CompletedTask;
    }

    public Task CancelAsync(SkillRunReference run, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_runs.TryGetValue(run.InvocationId, out var state)
            && state.Status is SkillExecutionStatus.Starting or SkillExecutionStatus.Running)
        {
            _runs[run.InvocationId] = state with
            {
                Status = SkillExecutionStatus.Cancelled,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Message = "启动请求已取消。"
            };
        }

        return Task.CompletedTask;
    }

    public Task<SkillStartResult> RespondAsync(
        SkillDecisionResponse response,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("桌面启动操作不会请求后续回答。");

    public Task<SkillFinalResult?> GetFinalResultAsync(
        SkillRunReference run,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_runs.TryGetValue(run.InvocationId, out var state))
        {
            return Task.FromResult<SkillFinalResult?>(null);
        }

        return Task.FromResult<SkillFinalResult?>(new SkillFinalResult(
            run,
            state.Status == SkillExecutionStatus.Succeeded,
            state.CompletedAtUtc,
            state.Message));
    }

    private static (string LaunchTarget, string Message) ResolveTarget(
        string capability,
        WindowsDesktopActionInput input)
    {
        if (string.Equals(capability, WindowsDesktopCapabilities.OpenApplication, StringComparison.Ordinal))
        {
            if (!string.Equals(input.ActionKind, "OpenApplication", StringComparison.Ordinal)
                || !ApplicationMap.TryGetValue(input.Target, out var application))
            {
                throw new UnauthorizedAccessException("这个应用不在当前允许打开的清单中。");
            }

            return (application.LaunchTarget, $"已请求 Windows 打开{application.DisplayName}。");
        }

        if (!string.Equals(input.ActionKind, "OpenWebsite", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("桌面操作类型与授权不匹配。");
        }

        var website = SafeWebsitePolicy.RequireHttps(input.Target);
        return (website.AbsoluteUri, $"已请求浏览器打开 {website.Host}。");
    }

    private sealed record RunState(
        SkillRunReference Run,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        int? ProcessId,
        string Message,
        SkillExecutionStatus Status);
}
