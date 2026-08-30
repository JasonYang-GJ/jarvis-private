using System.Diagnostics;
using ScreenGuide.Vision.Abstractions;
using ScreenGuide.Vision.Windows;
using Windows.Graphics.Capture;
using WinForms = System.Windows.Forms;

namespace ScreenGuide.Stage4.RealUsageRunner;

internal static class VisionEvaluationRunner
{
    public static async Task<EvaluationReport> RunAsync(
        RunnerOptions options,
        CancellationToken cancellationToken)
    {
        var attempts = new List<EvaluationAttempt>();
        var visionDiagnostic = VisionDiagnosticSummary.Empty;
        using var input = new ConsoleLineInput();
        var supported = GraphicsCaptureSession.IsSupported();
        var environment = EvaluationEnvironmentFactory.Create(
            supported,
            "not_applicable");
        if (!supported)
        {
            attempts.Add(new EvaluationAttempt(
                false,
                EvaluationTerminalState.Blocked,
                TimeSpan.Zero,
                "vision.capture_unavailable"));
            return Report("blocked", cleanup: true);
        }

        SyntheticEvaluationWindow? window = null;
        var cleanupConfirmed = true;
        var stage = "completed";
        var diagnosticMode = options.Mode == "vision-diagnostic";
        try
        {
            window = await SyntheticEvaluationWindow.StartAsync(
                    VisionEvaluationContract.FormTitle,
                    diagnosticMode
                        ? VisionEvaluationContract.DiagnosticWindowText
                        : VisionEvaluationContract.FormalCanary,
                    diagnosticMode ? 760 : 680,
                    diagnosticMode ? 420 : 280,
                    cancellationToken)
                .ConfigureAwait(false);
            using var process = Process.GetCurrentProcess();
            var processStart = new DateTimeOffset(process.StartTime.ToUniversalTime());
            var consentedTarget = new WindowCaptureTarget(
                window.Handle,
                VisionEvaluationContract.FormTitle,
                process.ProcessName,
                process.Id,
                processStart,
                DateTimeOffset.UtcNow);
            Console.WriteLine("S4-R2 本机单窗口评测：只读取现在已经显示的 Runner 测试窗口。 ");
            Console.WriteLine("图像和识别文字只在内存中处理，不保存、不上传。输入 STOP 或按 Ctrl+C 可停止。 ");
            Console.WriteLine("输入 YES 表示同意本批次读取这个精确测试窗口：");
            if (!string.Equals(
                    await input.ReadLineAsync(cancellationToken),
                    "YES",
                    StringComparison.Ordinal))
            {
                attempts.Add(new EvaluationAttempt(
                    false,
                    EvaluationTerminalState.Blocked,
                    TimeSpan.Zero,
                    "evaluation.consent_missing"));
                stage = "blocked";
            }
            else
            {
                var verifier = new WindowsWindowCaptureTargetVerifier();
                var capture = Stage4VisionCaptureFactory.Create(verifier);
                var provider = new WindowsLocalWindowVisionProvider(
                    new WindowsLocalOcrTextExtractor());
                var evaluator = new VisionAttemptEvaluator(
                    capture,
                    provider);
                var diagnosticEvaluator = new VisionDiagnosticEvaluator(capture, provider);

                var attemptCount = options.Mode == "vision" ? 21 : 1;
                for (var index = 0; index < attemptCount; index++)
                {
                    var isWarmup = options.Mode == "vision" && index == 0;
                    Console.WriteLine(options.Mode == "vision-diagnostic"
                        ? "脱敏诊断样本：按 Enter 开始一次读取。"
                        : options.Mode == "vision-identity-change"
                        ? "窗口身份变化场景：按 Enter 开始一次受控取消检查。"
                        : isWarmup
                            ? "预热：按 Enter 开始读取测试窗口。"
                            : $"正式 {index}/20：按 Enter 开始读取测试窗口。");
                    var command = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (command is null
                        || string.Equals(command, "STOP", StringComparison.OrdinalIgnoreCase))
                    {
                        attempts.Add(new EvaluationAttempt(
                            isWarmup,
                            EvaluationTerminalState.Cancelled,
                            TimeSpan.Zero,
                            "evaluation.cancelled"));
                        stage = "cancelled";
                        break;
                    }

                    if (diagnosticMode)
                    {
                        var diagnosticControlled = await ManualAttemptControl.RunAsync(
                                input,
                                attemptCancellation => diagnosticEvaluator.EvaluateAsync(
                                    consentedTarget,
                                    VisionEvaluationContract.DiagnosticCandidates,
                                    attemptCancellation),
                                cancellationToken)
                            .ConfigureAwait(false);
                        var diagnosticResult = diagnosticControlled.Value.AttemptResult;
                        if (diagnosticControlled.StopRequested)
                        {
                            diagnosticResult = new VisionAttemptResult(
                                new EvaluationAttempt(
                                    false,
                                    EvaluationTerminalState.Cancelled,
                                    diagnosticResult.Attempt.EndToEndElapsed,
                                    "evaluation.cancelled",
                                    diagnosticResult.Attempt.CaptureElapsed,
                                    diagnosticResult.Attempt.AnalysisElapsed),
                                diagnosticResult.CleanupConfirmed);
                        }

                        attempts.Add(diagnosticResult.Attempt);
                        cleanupConfirmed &= diagnosticResult.CleanupConfirmed;
                        visionDiagnostic = diagnosticControlled.StopRequested
                            ? VisionDiagnosticSummary.Empty
                            : diagnosticControlled.Value.Diagnostic;
                        Console.WriteLine($"本次终态：{diagnosticResult.Attempt.State}；窗口内容未保存。 ");
                        if (diagnosticResult.Attempt.State != EvaluationTerminalState.Success)
                        {
                            stage = diagnosticResult.Attempt.State == EvaluationTerminalState.Blocked
                                ? "blocked"
                                : diagnosticResult.Attempt.State == EvaluationTerminalState.Cancelled
                                    ? "cancelled"
                                    : "failed";
                        }

                        break;
                    }

                    var controlled = await ManualAttemptControl.RunAsync(
                            input,
                            async attemptCancellation =>
                            {
                                if (options.Mode == "vision-identity-change")
                                {
                                    await window.ChangeTitleAsync(
                                            VisionEvaluationContract.ChangedFormTitle,
                                            attemptCancellation)
                                        .ConfigureAwait(false);
                                }

                                return await evaluator.EvaluateAsync(
                                        consentedTarget,
                                        VisionEvaluationContract.FormalCanary,
                                        isWarmup,
                                        attemptCancellation)
                                    .ConfigureAwait(false);
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    var result = controlled.StopRequested
                        ? new VisionAttemptResult(
                            new EvaluationAttempt(
                                isWarmup,
                                EvaluationTerminalState.Cancelled,
                                controlled.Value.Attempt.EndToEndElapsed,
                                "evaluation.cancelled",
                                controlled.Value.Attempt.CaptureElapsed,
                                controlled.Value.Attempt.AnalysisElapsed),
                            controlled.Value.CleanupConfirmed)
                        : controlled.Value;
                    attempts.Add(result.Attempt);
                    cleanupConfirmed &= result.CleanupConfirmed;
                    Console.WriteLine($"本次终态：{result.Attempt.State}；窗口内容未保存。 ");
                    if (BatchTerminationPolicy.ShouldEndBatch(result.Attempt))
                    {
                        stage = result.Attempt.State == EvaluationTerminalState.Blocked
                            ? "blocked"
                            : "cancelled";
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            attempts.Add(new EvaluationAttempt(
                false,
                EvaluationTerminalState.Cancelled,
                TimeSpan.Zero,
                "evaluation.cancelled"));
            stage = "cancelled";
        }
        catch
        {
            attempts.Add(new EvaluationAttempt(
                false,
                EvaluationTerminalState.Failure,
                TimeSpan.Zero,
                "vision.capture_failed"));
            stage = "failed";
        }
        finally
        {
            if (window is not null)
            {
                await window.DisposeAsync().ConfigureAwait(false);
                cleanupConfirmed &= window.IsStopped;
            }
        }

        if (options.Mode == "vision-identity-change"
            && attempts.Any(item => item.ErrorCode == "vision.identity_changed"))
        {
            stage = "cancelled";
        }

        return Report(stage, cleanupConfirmed);

        EvaluationReport Report(string reportStage, bool cleanup) =>
            EvaluationReport.Create(
                options.Mode,
                options.ExpectedSha,
                reportStage,
                EvaluationAggregator.Build(attempts),
                environment,
                cleanup,
                visionDiagnostic);
    }

    private sealed class SyntheticEvaluationWindow : IAsyncDisposable
    {
        private readonly Thread _thread;
        private readonly WinForms.Form _form;

        private SyntheticEvaluationWindow(Thread thread, WinForms.Form form, long handle)
        {
            _thread = thread;
            _form = form;
            Handle = handle;
        }

        public long Handle { get; }

        public bool IsStopped => !_thread.IsAlive;

        public static async Task<SyntheticEvaluationWindow> StartAsync(
            string title,
            string canary,
            int width,
            int height,
            CancellationToken cancellationToken)
        {
            var ready = new TaskCompletionSource<(WinForms.Form Form, long Handle)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    var form = new WinForms.Form
                    {
                        Text = title,
                        Width = width,
                        Height = height,
                        StartPosition = WinForms.FormStartPosition.CenterScreen,
                        TopMost = true
                    };
                    form.Controls.Add(new WinForms.Label
                    {
                        Text = canary,
                        AutoSize = true,
                        Font = new System.Drawing.Font("Microsoft YaHei UI", 18),
                        Left = 46,
                        Top = 86
                    });
                    form.Shown += (_, _) =>
                    {
                        form.Activate();
                        ready.TrySetResult((form, form.Handle.ToInt64()));
                    };
                    WinForms.Application.Run(form);
                }
                catch (Exception exception)
                {
                    ready.TrySetException(exception);
                }
            })
            {
                IsBackground = true,
                Name = "Yuanshu-S4-R2-SyntheticWindow"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            var (form, handle) = await ready.Task.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    cancellationToken)
                .ConfigureAwait(false);
            return new SyntheticEvaluationWindow(thread, form, handle);
        }

        public Task ChangeTitleAsync(string title, CancellationToken cancellationToken) =>
            InvokeAsync(() => _form.Text = title, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (!_form.IsDisposed)
            {
                await InvokeAsync(_form.Close, CancellationToken.None).ConfigureAwait(false);
            }

            await Task.Run(() => _thread.Join(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }

        private Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            var completed = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.ThrowIfCancellationRequested();
            _form.BeginInvoke(() =>
            {
                try
                {
                    action();
                    completed.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    completed.TrySetException(exception);
                }
            });
            return completed.Task.WaitAsync(cancellationToken);
        }
    }
}

internal static class Stage4VisionCaptureFactory
{
    public static IExactWindowCaptureBackend CreateBackend(
        IWindowCaptureTargetVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        return new ResilientExactWindowCaptureBackend(
            new WindowsGraphicsCaptureBackend(verifier),
            new PrintWindowCaptureBackend(verifier),
            verifier);
    }

    public static IWindowCaptureService Create(IWindowCaptureTargetVerifier verifier) =>
        new WindowsSingleWindowCaptureService(
            CreateBackend(verifier),
            new WindowsSensitiveWindowPolicy(),
            verifier);
}
