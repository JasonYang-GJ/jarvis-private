using System.Reflection;
using ScreenGuide.AI.Core;
using ScreenGuide.AI.Qwen;

namespace ScreenGuide.DesktopV01.RealAcceptanceRunner;

public sealed record R4QwenValidationBudget(
    int MaxTotalRequests,
    int MaxModelRequests,
    int OrdinaryMaxOutputTokens,
    int CancellationMaxOutputTokens,
    TimeSpan TotalTimeout,
    bool NoAutomaticRetry,
    bool NoFallback,
    bool NoResend);

public sealed record R4QwenValidationOptions(
    bool Requested,
    bool IsValid,
    string? ErrorCode,
    string? ExpectedProviderId,
    string? ExpectedModelId,
    string? ExpectedCommitSha,
    R4QwenValidationBudget? Budget)
{
    public const string ModeArgument = "--stage2-r4-qwen";
    public const string ExpectedProvider = "qwen";
    public const string ExpectedModel = "qwen3.7-plus";

    public static R4QwenValidationOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var modeCount = CountExact(arguments, ModeArgument);
        if (modeCount == 0)
        {
            return Invalid(requested: false, "real_provider_disabled");
        }

        if (modeCount != 1)
        {
            return Invalid(requested: true, "r4_real_provider_mode_repeated");
        }

        if (CountExact(arguments, "--real-provider") != 1)
        {
            return Invalid(requested: true, "r4_real_provider_approval_missing");
        }

        var provider = ReadSingle(
            arguments,
            "--expected-provider=",
            "r4_expected_provider_missing",
            "r4_expected_provider_repeated");
        if (provider.ErrorCode is not null)
        {
            return Invalid(requested: true, provider.ErrorCode);
        }

        if (!string.Equals(provider.Value, ExpectedProvider, StringComparison.Ordinal))
        {
            return Invalid(requested: true, "r4_expected_provider_invalid");
        }

        var model = ReadSingle(
            arguments,
            "--expected-model=",
            "r4_expected_model_missing",
            "r4_expected_model_repeated");
        if (model.ErrorCode is not null)
        {
            return Invalid(requested: true, model.ErrorCode);
        }

        if (!string.Equals(model.Value, ExpectedModel, StringComparison.Ordinal))
        {
            return Invalid(requested: true, "r4_expected_model_invalid");
        }

        var commit = ReadSingle(
            arguments,
            "--expected-sha=",
            "r4_expected_sha_missing",
            "r4_expected_sha_repeated");
        if (commit.ErrorCode is not null)
        {
            return Invalid(requested: true, commit.ErrorCode);
        }

        if (!IsExactCommitSha(commit.Value))
        {
            return Invalid(requested: true, "r4_expected_sha_invalid");
        }

        var requiredFlags = new[] { "--no-automatic-retry", "--no-fallback", "--no-resend" };
        if (requiredFlags.Any(flag => CountExact(arguments, flag) != 1))
        {
            return Invalid(requested: true, "r4_real_provider_budget_missing");
        }

        if (!TryReadExactInt(arguments, "--max-total-requests=", 3)
            || !TryReadExactInt(arguments, "--max-model-requests=", 2)
            || !TryReadExactInt(arguments, "--ordinary-max-output-tokens=", 32)
            || !TryReadExactInt(arguments, "--cancellation-max-output-tokens=", 256)
            || !TryReadExactInt(arguments, "--total-timeout-seconds=", 600))
        {
            return Invalid(requested: true, "r4_real_provider_budget_out_of_range");
        }

        return new R4QwenValidationOptions(
            Requested: true,
            IsValid: true,
            ErrorCode: null,
            ExpectedProvider,
            ExpectedModel,
            commit.Value,
            new R4QwenValidationBudget(
                MaxTotalRequests: 3,
                MaxModelRequests: 2,
                OrdinaryMaxOutputTokens: 32,
                CancellationMaxOutputTokens: 256,
                TimeSpan.FromSeconds(600),
                NoAutomaticRetry: true,
                NoFallback: true,
                NoResend: true));
    }

    private static R4QwenValidationOptions Invalid(bool requested, string errorCode) =>
        new(requested, IsValid: false, errorCode, null, null, null, null);

    private static int CountExact(IReadOnlyList<string> arguments, string expected) =>
        arguments.Count(argument => string.Equals(argument, expected, StringComparison.Ordinal));

    private static (string? Value, string? ErrorCode) ReadSingle(
        IReadOnlyList<string> arguments,
        string prefix,
        string missingErrorCode,
        string repeatedErrorCode)
    {
        var matches = arguments
            .Where(argument => argument.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
        {
            return (null, missingErrorCode);
        }

        if (matches.Length != 1)
        {
            return (null, repeatedErrorCode);
        }

        return (matches[0][prefix.Length..], null);
    }

    private static bool IsExactCommitSha(string? value) =>
        value is { Length: 40 }
        && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool TryReadExactInt(
        IReadOnlyList<string> arguments,
        string prefix,
        int expected)
    {
        var matches = arguments
            .Where(argument => argument.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();
        return matches.Length == 1
               && int.TryParse(matches[0].AsSpan(prefix.Length), out var actual)
               && actual == expected;
    }
}

public sealed class R4ValidationFailureException(
    string errorCode,
    string? providerDiagnosticCode = null) : Exception(errorCode)
{
    public string ErrorCode { get; } = errorCode;

    public string? ProviderDiagnosticCode { get; } = providerDiagnosticCode;
}

public sealed record R4IsolatedAiSettingsEvidence(string ProviderId, string ModelId);

public static class R4IsolatedAiSettingsMaterializer
{
    public const string MissingErrorCode = "r4_isolated_settings_missing";
    public const string InvalidErrorCode = "r4_isolated_settings_invalid";
    public const string MismatchErrorCode = "r4_expected_route_mismatch";

    public static async Task<R4IsolatedAiSettingsEvidence> MaterializeAndVerifyAsync(
        string settingsPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        if (!File.Exists(settingsPath))
        {
            using var writer = CreateStore(settingsPath);
            await writer.SaveAsync(
                    new AiSettings(new ChatModelRoute(
                        QwenChatModelProvider.ProviderId,
                        QwenChatModelProvider.DefaultModelId)),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await LoadAndVerifyAsync(settingsPath, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<R4IsolatedAiSettingsEvidence> LoadAndVerifyAsync(
        string settingsPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        if (!File.Exists(settingsPath))
        {
            throw new R4ValidationFailureException(MissingErrorCode);
        }

        AiSettings settings;
        try
        {
            using var reader = CreateStore(settingsPath);
            settings = await reader.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or IOException
                or UnauthorizedAccessException)
        {
            throw new R4ValidationFailureException(InvalidErrorCode);
        }

        var route = settings.DefaultChatRoute;
        if (!string.Equals(
                route.ProviderId,
                QwenChatModelProvider.ProviderId,
                StringComparison.Ordinal)
            || !string.Equals(
                route.ModelId,
                QwenChatModelProvider.DefaultModelId,
                StringComparison.Ordinal))
        {
            throw new R4ValidationFailureException(MismatchErrorCode);
        }

        return new R4IsolatedAiSettingsEvidence(route.ProviderId, route.ModelId);
    }

    private static FileAiSettingsStore CreateStore(string settingsPath) =>
        new(
            settingsPath,
            new AiSettings(new ChatModelRoute(
                "r4-isolated-settings-missing",
                "r4-isolated-settings-missing")));
}

public sealed record R4BuildIdentityEvidence(
    string ExpectedCommitSha,
    string? ActualCommitSha,
    string? InformationalVersion)
{
    public bool IsExactMatch => string.Equals(
        ExpectedCommitSha,
        ActualCommitSha,
        StringComparison.Ordinal);
}

public static class R4QwenBuildIdentity
{
    public const string MismatchErrorCode = "r4_build_identity_mismatch";

    public static R4BuildIdentityEvidence Read(
        string expectedCommitSha,
        Assembly? assembly = null)
    {
        assembly ??= Assembly.GetEntryAssembly() ?? typeof(R4QwenBuildIdentity).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return new R4BuildIdentityEvidence(
            expectedCommitSha,
            FindCommitSha(informationalVersion),
            SafeInformationalVersion(informationalVersion));
    }

    public static R4BuildIdentityEvidence RequireMatch(
        string expectedCommitSha,
        Assembly? assembly = null)
    {
        var evidence = Read(expectedCommitSha, assembly);
        if (!evidence.IsExactMatch)
        {
            throw new R4ValidationFailureException(MismatchErrorCode);
        }

        return evidence;
    }

    private static string? FindCommitSha(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        for (var index = 0; index <= value.Length - 40; index++)
        {
            var candidate = value.AsSpan(index, 40);
            var valid = true;
            foreach (var character in candidate)
            {
                if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                {
                    valid = false;
                    break;
                }
            }

            if (valid)
            {
                return candidate.ToString();
            }
        }

        return null;
    }

    private static string? SafeInformationalVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '+' or '-' or '_')
            ? value
            : null;
}

public static class R4QwenLaunchCommand
{
    public static string Create(string exactCommitSha)
    {
        if (exactCommitSha is not { Length: 40 }
            || exactCommitSha.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentOutOfRangeException(nameof(exactCommitSha));
        }

        return "ScreenGuide.DesktopV01.RealAcceptanceRunner.exe"
               + " --stage2-r4-qwen --real-provider"
               + " --expected-provider=qwen --expected-model=qwen3.7-plus"
               + $" --expected-sha={exactCommitSha}"
               + " --max-total-requests=3 --max-model-requests=2"
               + " --ordinary-max-output-tokens=32 --cancellation-max-output-tokens=256"
               + " --total-timeout-seconds=600"
               + " --no-automatic-retry --no-fallback --no-resend";
    }
}
