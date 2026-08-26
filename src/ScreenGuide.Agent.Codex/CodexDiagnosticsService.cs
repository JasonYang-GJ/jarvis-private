using System.Diagnostics;

namespace ScreenGuide.Agent.Codex;

public sealed record CodexDiagnosticsResult(
    bool IsInstalled,
    bool IsCompatible,
    string? Version,
    string Message);

public sealed class CodexDiagnosticsService(CodexConnectorOptions options)
{
    public async Task<CodexDiagnosticsResult> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        string executable;
        try
        {
            executable = new CodexExecutableLocator(options.ExecutablePath).Resolve();
        }
        catch (FileNotFoundException)
        {
            return new CodexDiagnosticsResult(
                false,
                false,
                null,
                "未检测到 Codex CLI。请先安装并登录 Codex。");
        }

        try
        {
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
                return new CodexDiagnosticsResult(true, false, null, "Codex 版本检查无法启动。");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = (await outputTask.ConfigureAwait(false)).Trim();
            var error = (await errorTask.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0)
            {
                return new CodexDiagnosticsResult(
                    true,
                    false,
                    null,
                    $"Codex 版本检查失败：{Sanitize(error)}");
            }

            const string prefix = "codex-cli ";
            if (!output.StartsWith(prefix, StringComparison.Ordinal))
            {
                return new CodexDiagnosticsResult(
                    true,
                    false,
                    null,
                    "Codex 返回了无法识别的版本信息。");
            }

            var compatibility = CodexVersionCompatibility.Evaluate(output[prefix.Length..]);
            return new CodexDiagnosticsResult(
                true,
                compatibility.IsVerified,
                compatibility.DetectedVersion,
                compatibility.Decision);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new CodexDiagnosticsResult(
                true,
                false,
                null,
                $"Codex 状态检查失败：{exception.GetType().Name}");
        }
    }

    private static string Sanitize(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "没有错误详情"
            : value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 160 ? normalized : normalized[..160];
    }
}
