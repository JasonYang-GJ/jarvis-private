using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenGuide.Agent.Codex;

internal sealed record CodexProtocolEvent(
    string Type,
    string EventId,
    string? ThreadId = null,
    string? ItemId = null,
    string? ItemType = null,
    string? ItemStatus = null,
    int? ExitCode = null,
    string? Message = null,
    string? AgentMessageText = null,
    string? Command = null,
    CodexTestCounts? TestCounts = null);

internal sealed record CodexTestCounts(int Total, int Passed, int Failed, int Skipped);

internal static class CodexJsonLineParser
{
    public static bool TryParse(
        string line,
        out CodexProtocolEvent? protocolEvent,
        out string? error)
    {
        protocolEvent = null;
        error = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                error = "Codex JSONL 事件缺少 type。";
                return false;
            }

            var type = typeElement.GetString()!;
            string? threadId = null;
            string? itemId = null;
            string? itemType = null;
            string? itemStatus = null;
            int? exitCode = null;
            string? message = null;
            string? agentMessageText = null;
            string? command = null;
            CodexTestCounts? testCounts = null;

            if (root.TryGetProperty("thread_id", out var threadElement)
                && threadElement.ValueKind == JsonValueKind.String)
            {
                threadId = threadElement.GetString();
            }

            if (root.TryGetProperty("message", out var messageElement)
                && messageElement.ValueKind == JsonValueKind.String)
            {
                message = messageElement.GetString();
            }

            if (root.TryGetProperty("error", out var errorElement)
                && errorElement.ValueKind == JsonValueKind.Object
                && errorElement.TryGetProperty("message", out var errorMessage)
                && errorMessage.ValueKind == JsonValueKind.String)
            {
                message = errorMessage.GetString();
            }

            if (root.TryGetProperty("item", out var item)
                && item.ValueKind == JsonValueKind.Object)
            {
                itemId = GetString(item, "id");
                itemType = GetString(item, "type");
                itemStatus = GetString(item, "status");
                command = GetString(item, "command");
                if (item.TryGetProperty("exit_code", out var exitElement)
                    && exitElement.ValueKind == JsonValueKind.Number
                    && exitElement.TryGetInt32(out var parsedExitCode))
                {
                    exitCode = parsedExitCode;
                }

                if (string.Equals(itemType, "agent_message", StringComparison.Ordinal)
                    && item.TryGetProperty("text", out var textElement)
                    && textElement.ValueKind == JsonValueKind.String)
                {
                    agentMessageText = textElement.GetString();
                }

                if (string.Equals(itemType, "error", StringComparison.Ordinal))
                {
                    message = GetString(item, "message") ?? message;
                }

                if (item.TryGetProperty("aggregated_output", out var outputElement)
                    && outputElement.ValueKind == JsonValueKind.String)
                {
                    testCounts = ParseTestCounts(outputElement.GetString());
                }
            }

            protocolEvent = new CodexProtocolEvent(
                type,
                ComputeEventId(line),
                threadId,
                itemId,
                itemType,
                itemStatus,
                exitCode,
                message,
                agentMessageText,
                command,
                testCounts);
            return true;
        }
        catch (JsonException exception)
        {
            error = $"Codex JSONL 无法解析：{exception.Message}";
            return false;
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string ComputeEventId(string line) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line)));

    private static CodexTestCounts? ParseTestCounts(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var dotnet = Regex.Match(
            output,
            @"(?is)(?:Failed|失败)\s*:\s*(?<failed>\d+).*?(?:Passed|通过)\s*:\s*(?<passed>\d+).*?(?:Skipped|已跳过)\s*:\s*(?<skipped>\d+).*?(?:Total|总计)\s*:\s*(?<total>\d+)");
        if (dotnet.Success)
        {
            return Counts(dotnet);
        }

        var jest = Regex.Match(
            output,
            @"(?im)^\s*Tests:\s*(?:(?<failed>\d+)\s+failed,?\s*)?(?:(?<passed>\d+)\s+passed,?\s*)?(?:(?<skipped>\d+)\s+skipped,?\s*)?(?<total>\d+)\s+total");
        if (jest.Success)
        {
            return Counts(jest);
        }

        var passed = SumMatches(output, @"(?i)(\d+)\s+passed");
        var failed = SumMatches(output, @"(?i)(\d+)\s+failed");
        var skipped = SumMatches(output, @"(?i)(\d+)\s+(?:skipped|deselected)");
        if (passed + failed + skipped > 0)
        {
            return new CodexTestCounts(passed + failed + skipped, passed, failed, skipped);
        }

        return null;
    }

    private static CodexTestCounts Counts(Match match)
    {
        var failed = ReadGroup(match, "failed");
        var passed = ReadGroup(match, "passed");
        var skipped = ReadGroup(match, "skipped");
        var total = ReadGroup(match, "total");
        return new CodexTestCounts(total, passed, failed, skipped);
    }

    private static int ReadGroup(Match match, string name) =>
        match.Groups[name].Success && int.TryParse(match.Groups[name].Value, out var value)
            ? value
            : 0;

    private static int SumMatches(string output, string pattern) =>
        Regex.Matches(output, pattern)
            .Select(match => int.Parse(match.Groups[1].Value))
            .Sum();
}

internal sealed record CodexTurnResult(
    string Outcome,
    string Summary,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<CodexTestResult> Tests,
    string? Question,
    IReadOnlyList<string> DecisionOptions,
    string Json);

internal sealed record CodexTestResult(string Name, string Status);

internal static class CodexTurnResultParser
{
    public static bool TryParse(
        string? json,
        out CodexTurnResult? result,
        out string? error)
    {
        result = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Codex 没有返回结构化最终结果。";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var outcome = RequireString(root, "outcome");
            var summary = RequireString(root, "summary");
            if (outcome is not ("completed" or "action_required"))
            {
                error = $"Codex 返回了未知 outcome：{outcome}";
                return false;
            }

            var changedFiles = ReadStringArray(root, "changedFiles");
            var decisionOptions = ReadStringArray(root, "decisionOptions");
            var tests = ReadTests(root);
            var question = root.TryGetProperty("question", out var questionElement)
                && questionElement.ValueKind == JsonValueKind.String
                    ? questionElement.GetString()
                    : null;
            if (outcome == "action_required" && string.IsNullOrWhiteSpace(question))
            {
                error = "action_required 必须包含非空 question。";
                return false;
            }

            result = new CodexTurnResult(
                outcome,
                summary,
                changedFiles,
                tests,
                question,
                decisionOptions,
                json);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            error = $"Codex 结构化结果无效：{exception.Message}";
            return false;
        }
    }

    private static string RequireString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new InvalidDataException($"缺少非空字段 {name}。");
        }

        return element.GetString()!;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"缺少数组字段 {name}。");
        }

        return element.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw new InvalidDataException($"{name} 包含非字符串值。"))
            .ToArray();
    }

    private static IReadOnlyList<CodexTestResult> ReadTests(JsonElement root)
    {
        if (!root.TryGetProperty("tests", out var element)
            || element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("缺少数组字段 tests。");
        }

        var tests = new List<CodexTestResult>();
        foreach (var item in element.EnumerateArray())
        {
            tests.Add(new CodexTestResult(
                RequireString(item, "name"),
                RequireString(item, "status")));
        }

        return tests;
    }
}
