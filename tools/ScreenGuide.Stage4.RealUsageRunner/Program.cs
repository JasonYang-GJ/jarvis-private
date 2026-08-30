namespace ScreenGuide.Stage4.RealUsageRunner;

public static class Program
{
    private const string ResultPrefix = "YUANSHU_S4_R2_RESULT ";

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        RunnerOptions options;
        try
        {
            options = RunnerOptions.Parse(args);
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine(
                "用法：--mode voice|voice-diagnostic|vision|vision-identity-change --expected-sha <40位小写SHA>");
            return 2;
        }

        if (!BuildIdentityVerifier.MatchesCurrentExecutable(options.ExpectedSha))
        {
            var blocked = EvaluationReport.Create(
                options.Mode,
                options.ExpectedSha,
                "blocked",
                EvaluationAggregator.Build(
                [
                    new EvaluationAttempt(
                        false,
                        EvaluationTerminalState.Blocked,
                        TimeSpan.Zero,
                        "evaluation.build_identity_mismatch")
                ]),
                EvaluationEnvironmentFactory.Create(false, "unknown"),
                cleanupConfirmed: true);
            Console.WriteLine(ResultPrefix + SafeEvaluationReportWriter.Serialize(blocked));
            return 1;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            EvaluationReport report;
            try
            {
                report = options.Mode is "voice" or "voice-diagnostic"
                    ? await VoiceEvaluationRunner.RunAsync(options, cancellation.Token)
                        .ConfigureAwait(false)
                    : await VisionEvaluationRunner.RunAsync(options, cancellation.Token)
                        .ConfigureAwait(false);
            }
            catch
            {
                var aggregate = EvaluationAggregator.Build(
                [
                    new EvaluationAttempt(
                        false,
                        EvaluationTerminalState.Failure,
                        TimeSpan.Zero,
                        "evaluation.unexpected")
                ]);
                report = EvaluationReport.Create(
                    options.Mode,
                    options.ExpectedSha,
                    "failed",
                    aggregate,
                    EvaluationEnvironmentFactory.Create(false, "unknown"),
                    cleanupConfirmed: false);
            }

            Console.WriteLine(ResultPrefix + SafeEvaluationReportWriter.Serialize(report));
            return report.Stage == "completed" ? 0 : 1;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }
}
