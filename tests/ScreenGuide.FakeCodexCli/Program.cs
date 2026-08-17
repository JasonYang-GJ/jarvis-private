using System.Diagnostics;
using System.Text.Json;

namespace ScreenGuide.FakeCodexCli;

public static class FakeCodexMarker;

public static class Program
{
    private const string DefaultThreadId = "11111111-1111-7111-8111-111111111111";

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--version"])
        {
            Console.WriteLine(
                AppContext.BaseDirectory.Contains("unverified-version", StringComparison.OrdinalIgnoreCase)
                    ? "codex-cli 0.148.0"
                    : "codex-cli 0.147.0");
            return 0;
        }

        if (args.Length >= 2 && args[0] == "--test-child")
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            await File.WriteAllTextAsync(args[1], "child survived");
            return 0;
        }

        var prompt = await Console.In.ReadToEndAsync();
        var isResume = args.Contains("resume", StringComparer.Ordinal);
        var threadId = isResume
            ? args.FirstOrDefault(value => Guid.TryParse(value, out _)) ?? DefaultThreadId
            : DefaultThreadId;
        Write(new { type = "thread.started", thread_id = threadId });
        Write(new { type = "turn.started" });

        if (prompt.Contains("TEST_FAILURE", StringComparison.Ordinal))
        {
            Write(new { type = "error", message = "synthetic failure" });
            Write(new { type = "turn.failed", error = new { message = "synthetic failure" } });
            return 1;
        }

        if (prompt.Contains("TEST_NO_TERMINAL", StringComparison.Ordinal))
        {
            Write(new
            {
                type = "item.started",
                item = new { id = "missing-terminal", type = "command_execution", status = "in_progress" }
            });
            return 0;
        }

        if (prompt.Contains("TEST_LONG_RUNNING", StringComparison.Ordinal))
        {
            var markerPath = ReadValue(prompt, "MARKER=");
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Fake Codex executable path is unavailable.");
            _ = Process.Start(
                new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { "--test-child", markerPath }
                });
            Write(new
            {
                type = "item.started",
                item = new { id = "long-command", type = "command_execution", status = "in_progress" }
            });
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }

        if (prompt.Contains("TEST_ACTION_REQUIRED", StringComparison.Ordinal))
        {
            WriteAgentMessage(
                "action_required",
                "需要用户补充颜色。",
                "请选择颜色。",
                ["blue", "green"]);
            Write(new { type = "turn.completed", usage = Usage() });
            return 0;
        }

        if (prompt.Contains("TEST_EVIDENCE_PASS", StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync("verified-change.txt", "agent change\n");
            WriteCommand(
                "test-pass",
                "dotnet test ScreenGuide.slnx",
                0,
                "Passed! - Failed: 0, Passed: 12, Skipped: 0, Total: 12");
            WriteAgentMessage(
                "completed",
                "claimed success",
                null,
                [],
                ["verified-change.txt"],
                [new { name = "dotnet test", status = "passed" }]);
            Write(new { type = "turn.completed", usage = Usage() });
            return 0;
        }

        if (prompt.Contains("TEST_EVIDENCE_FAIL", StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync("failed-test-change.txt", "agent change\n");
            WriteCommand(
                "test-fail",
                "dotnet test ScreenGuide.slnx",
                1,
                "Failed! - Failed: 2, Passed: 10, Skipped: 0, Total: 12");
            WriteAgentMessage(
                "completed",
                "Codex claims all work succeeded",
                null,
                [],
                ["failed-test-change.txt"],
                [new { name = "dotnet test", status = "passed" }]);
            Write(new { type = "turn.completed", usage = Usage() });
            return 0;
        }

        if (prompt.Contains("TEST_EVIDENCE_NO_TEST", StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync("untested-change.txt", "agent change\n");
            WriteAgentMessage(
                "completed",
                "changed without tests",
                null,
                [],
                ["untested-change.txt"],
                []);
            Write(new { type = "turn.completed", usage = Usage() });
            return 0;
        }

        if (prompt.Contains("TEST_EVIDENCE_NO_CHANGE", StringComparison.Ordinal))
        {
            WriteAgentMessage("completed", "no change needed", null, []);
            Write(new { type = "turn.completed", usage = Usage() });
            return 0;
        }

        if (prompt.Contains("TEST_EVIDENCE_FALSE_FILE_CLAIM", StringComparison.Ordinal))
        {
            WriteAgentMessage(
                "completed",
                "claims a file changed",
                null,
                [],
                ["not-actually-created.txt"],
                []);
            Write(new { type = "turn.completed", usage = Usage() });
            return 0;
        }

        if (prompt.Contains("TEST_EVIDENCE_MIXED", StringComparison.Ordinal))
        {
            await File.AppendAllTextAsync("mixed.txt", "agent line\n");
            WriteAgentMessage(
                "completed",
                "updated mixed file",
                null,
                [],
                ["mixed.txt"],
                []);
            Write(new { type = "turn.completed", usage = Usage() });
            return 0;
        }

        if (prompt.Contains("TEST_EVIDENCE_FILE_COUNTS", StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync("added.txt", "added\n");
            await File.AppendAllTextAsync("modified.txt", "changed\n");
            File.Delete("deleted.txt");
            WriteAgentMessage(
                "completed",
                "added modified deleted",
                null,
                [],
                ["added.txt", "modified.txt", "deleted.txt"],
                []);
            Write(new { type = "turn.completed", usage = Usage() });
            return 0;
        }

        var summary = isResume || prompt.Contains("TEST_CONTINUE", StringComparison.Ordinal)
            ? $"continued:{threadId}"
            : "completed";
        WriteAgentMessage("completed", summary, null, []);
        var completed = JsonSerializer.Serialize(new { type = "turn.completed", usage = Usage() });
        Console.WriteLine(completed);
        if (prompt.Contains("TEST_DUPLICATE", StringComparison.Ordinal))
        {
            Console.WriteLine(completed);
        }

        return 0;
    }

    private static void WriteAgentMessage(
        string outcome,
        string summary,
        string? question,
        string[] decisionOptions,
        string[]? changedFiles = null,
        object[]? tests = null)
    {
        var result = JsonSerializer.Serialize(
            new
            {
                outcome,
                summary,
                changedFiles = changedFiles ?? [],
                tests = tests ?? [],
                question,
                decisionOptions
            });
        Write(new
        {
            type = "item.completed",
            item = new
            {
                id = "agent-message",
                type = "agent_message",
                text = result
            }
        });
    }

    private static void WriteCommand(
        string id,
        string command,
        int exitCode,
        string output)
    {
        Write(new
        {
            type = "item.started",
            item = new { id, type = "command_execution", command, status = "in_progress" }
        });
        Write(new
        {
            type = "item.completed",
            item = new
            {
                id,
                type = "command_execution",
                command,
                status = exitCode == 0 ? "completed" : "failed",
                exit_code = exitCode,
                aggregated_output = output
            }
        });
    }

    private static object Usage() => new
    {
        input_tokens = 1,
        cached_input_tokens = 0,
        cache_write_input_tokens = 0,
        output_tokens = 1,
        reasoning_output_tokens = 0
    };

    private static string ReadValue(string prompt, string prefix)
    {
        var line = prompt.Split('\n').First(value => value.StartsWith(prefix, StringComparison.Ordinal));
        return line[prefix.Length..].Trim();
    }

    private static void Write(object value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value));
        Console.Out.Flush();
    }
}
