using System.Diagnostics;

namespace ScreenGuide.Agent.Codex;

internal sealed record CodexCapability(string ExecutablePath, string Version);

internal sealed class CodexCapabilityProbe(CodexExecutableLocator locator)
{
    public async Task<CodexCapability> ProbeAsync(CancellationToken cancellationToken)
    {
        var executable = locator.Resolve();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--version");
        if (!process.Start())
        {
            throw new InvalidOperationException("Codex CLI 版本检查无法启动。");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = (await standardOutput.ConfigureAwait(false)).Trim();
        var error = (await standardError.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Codex CLI 版本检查失败，退出码 {process.ExitCode}：{Sanitize(error)}");
        }

        const string prefix = "codex-cli ";
        if (!output.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Codex CLI 版本输出无法识别：{Sanitize(output)}。");
        }

        var compatibility = CodexVersionCompatibility.Evaluate(output[prefix.Length..]);
        if (!compatibility.IsVerified)
        {
            throw new CodexVersionCompatibilityException(
                compatibility.DetectedVersion,
                compatibility.Decision);
        }

        return new CodexCapability(executable, compatibility.DetectedVersion);
    }

    private static string Sanitize(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "无错误详情"
            : value.Length <= 300 ? value : value[..300];
}
