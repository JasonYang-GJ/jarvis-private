using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows;

public static class WindowsDesktopCapabilities
{
    public const string OpenApplication = "desktop.application.open";
    public const string OpenWebsite = "desktop.website.open";
    public const string OpenWebsiteSecure = "browser.open";
    public const string OpenWebsiteInApplication = "browser.open.visible-in-app";
    public const string OpenFile = "file.open";
    public const string SearchForeground = "desktop.search.submit";
    public const string DescribeForeground = "desktop.window.describe";
}

public sealed record KnownDesktopApplication(
    string Id,
    string DisplayName,
    string LaunchTarget);

public sealed record WindowsDesktopActionInput(
    string ActionKind,
    string Target,
    long? WindowHandle = null,
    string? WindowTitle = null,
    string? Argument = null);

public sealed record VisibleDesktopLaunchResult(
    int? ProcessId,
    long WindowHandle,
    string WindowTitle);

public interface IDesktopProcessLauncher
{
    int? Start(string target);

    VisibleDesktopLaunchResult OpenApplicationVisible(string applicationLaunchTarget);

    VisibleDesktopLaunchResult OpenWebsiteVisible(
        string? browserLaunchTarget,
        Uri website);
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

    public VisibleDesktopLaunchResult OpenWebsiteVisible(
        string? browserLaunchTarget,
        Uri website) =>
        VisibleBrowserWindowLauncher.Open(browserLaunchTarget, website);

    public VisibleDesktopLaunchResult OpenApplicationVisible(string applicationLaunchTarget) =>
        VisibleBrowserWindowLauncher.OpenApplication(applicationLaunchTarget);
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

public sealed class WindowsDesktopSkillAdapter(
    IDesktopProcessLauncher launcher,
    IInstalledApplicationCatalog applications,
    IReliableDesktopAutomation automation) : ISkillAdapter
{
    private readonly ConcurrentDictionary<Guid, RunState> _runs = new();

    public SkillDescriptor Descriptor { get; } = new(
        "windows.safe-launch",
        "0.2.0",
        "Windows 安全操作",
        true,
        new HashSet<string>(StringComparer.Ordinal)
        {
            WindowsDesktopCapabilities.OpenApplication,
            WindowsDesktopCapabilities.OpenWebsite,
            WindowsDesktopCapabilities.OpenWebsiteSecure,
            WindowsDesktopCapabilities.OpenWebsiteInApplication,
            WindowsDesktopCapabilities.OpenFile,
            WindowsDesktopCapabilities.SearchForeground,
            WindowsDesktopCapabilities.DescribeForeground
        });

    public IReadOnlyList<KnownDesktopApplication> Applications =>
        applications.GetApplications();

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
        var (processId, message) = Execute(request.Capability, input);
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

    private (int? ProcessId, string Message) Execute(
        string capability,
        WindowsDesktopActionInput input)
    {
        if (string.Equals(capability, WindowsDesktopCapabilities.OpenApplication, StringComparison.Ordinal))
        {
            if (!string.Equals(input.ActionKind, "OpenApplication", StringComparison.Ordinal)
                || applications.FindById(input.Target) is not { } application)
            {
                throw new UnauthorizedAccessException("这个应用不在当前允许打开的清单中。");
            }

            var launch = launcher.OpenApplicationVisible(application.LaunchTarget);
            return (launch.ProcessId,
                $"已确认“{application.DisplayName}”窗口已经显示在你眼前。");
        }

        if (string.Equals(capability, WindowsDesktopCapabilities.OpenWebsite, StringComparison.Ordinal)
            || string.Equals(capability, WindowsDesktopCapabilities.OpenWebsiteSecure, StringComparison.Ordinal))
        {
            if (!string.Equals(input.ActionKind, "OpenWebsite", StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("桌面操作类型与授权不匹配。");
            }

            var website = SafeWebsitePolicy.RequireHttps(input.Target);
            var launch = launcher.OpenWebsiteVisible(null, website);
            return (launch.ProcessId,
                $"已确认默认浏览器窗口显示在前台，并打开 {website.Host}；页面内容是否完全加载尚未验证。");
        }

        if (string.Equals(capability, WindowsDesktopCapabilities.OpenWebsiteInApplication,
                StringComparison.Ordinal))
        {
            if (!string.Equals(input.ActionKind, "OpenWebsiteInApplication", StringComparison.Ordinal)
                || applications.FindById(input.Target) is not { } browser
                || string.IsNullOrWhiteSpace(input.Argument))
            {
                throw new UnauthorizedAccessException("指定浏览器操作缺少明确的应用或网站。");
            }

            var allowedBrowser = new[]
            {
                applications.FindBrowser("Google Chrome"),
                applications.FindBrowser("Microsoft Edge")
            }.Any(item => item is not null
                          && string.Equals(item.Id, browser.Id, StringComparison.Ordinal));
            if (!allowedBrowser)
            {
                throw new UnauthorizedAccessException("当前只允许在明确识别的浏览器中打开网站。");
            }

            var website = SafeWebsitePolicy.RequireHttps(input.Argument);
            var launch = launcher.OpenWebsiteVisible(browser.LaunchTarget, website);
            return (launch.ProcessId,
                $"已确认“{browser.DisplayName}”的新窗口显示在前台，并打开 {website.Host}；页面内容是否完全加载尚未验证。");
        }

        if (string.Equals(capability, WindowsDesktopCapabilities.OpenFile, StringComparison.Ordinal))
        {
            if (!string.Equals(input.ActionKind, "OpenFile", StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("桌面操作类型与授权不匹配。");
            }

            var fullPath = Path.GetFullPath(input.Target);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("刚才选择的文件已经不存在。", fullPath);
            }

            return (launcher.Start(fullPath),
                $"Windows 已接受打开“{Path.GetFileName(fullPath)}”的请求；文件是否完成加载尚未验证。");
        }

        if (string.Equals(capability, WindowsDesktopCapabilities.SearchForeground, StringComparison.Ordinal))
        {
            if (!string.Equals(input.ActionKind, "SearchForeground", StringComparison.Ordinal)
                || input.WindowHandle is null)
            {
                throw new UnauthorizedAccessException("搜索操作缺少明确的目标窗口。");
            }

            var result = automation.Search(input.WindowHandle.Value, input.Target);
            return (null, result.Summary);
        }

        if (string.Equals(capability, WindowsDesktopCapabilities.DescribeForeground, StringComparison.Ordinal))
        {
            if (!string.Equals(input.ActionKind, "DescribeForeground", StringComparison.Ordinal)
                || input.WindowHandle is null)
            {
                throw new UnauthorizedAccessException("窗口查看缺少明确的目标窗口。");
            }

            var result = automation.Describe(input.WindowHandle.Value);
            return (null, result.Summary);
        }

        throw new UnauthorizedAccessException("这个桌面能力尚未开放。");
    }

    private sealed record RunState(
        SkillRunReference Run,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        int? ProcessId,
        string Message,
        SkillExecutionStatus Status);
}
