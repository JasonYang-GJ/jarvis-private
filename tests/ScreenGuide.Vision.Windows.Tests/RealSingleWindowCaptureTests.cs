using System.Diagnostics;
using ScreenGuide.Vision.Abstractions;
using ScreenGuide.Vision.Windows;
using WinForms = System.Windows.Forms;

namespace ScreenGuide.Vision.Windows.Tests;

public sealed class RealSingleWindowCaptureTests
{
    [Fact]
    public async Task CapturesOnlySyntheticTestWindowWithoutWritingImageToDisk()
    {
        var ready = new TaskCompletionSource<(WinForms.Form Form, long Handle)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var form = new WinForms.Form
            {
                Text = "元枢单窗口读取自动验收",
                Width = 620,
                Height = 260,
                StartPosition = WinForms.FormStartPosition.CenterScreen
            };
            form.Controls.Add(new WinForms.Label
            {
                Text = "这是无个人数据的单窗口读取测试界面",
                AutoSize = true,
                Font = new System.Drawing.Font("Microsoft YaHei UI", 18),
                Left = 48,
                Top = 72
            });
            form.Shown += (_, _) => ready.TrySetResult((form, form.Handle.ToInt64()));
            WinForms.Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var (form, handle) = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var process = Process.GetCurrentProcess();
            var target = new WindowCaptureTarget(
                handle,
                "元枢单窗口读取自动验收",
                process.ProcessName,
                process.Id,
                new DateTimeOffset(process.StartTime.ToUniversalTime()),
                DateTimeOffset.UtcNow);
            var backend = new WindowsGraphicsCaptureBackend(
                new WindowsWindowCaptureTargetVerifier());
            var raw = await backend.CaptureAsync(target, CancellationToken.None);
            await using var frame = new CapturedWindowFrame(
                raw.PngBytes, raw.PixelWidth, raw.PixelHeight, raw.Technology);

            Assert.True(frame.PixelWidth >= 600);
            Assert.True(frame.PixelHeight >= 200);
            Assert.Equal("Windows.GraphicsCapture.SingleHwnd", frame.CaptureTechnology);
            Assert.Equal(new byte[] { 137, 80, 78, 71 }, frame.PngBytes.Span[..4].ToArray());

            var provider = new WindowsLocalWindowVisionProvider(new WindowsLocalOcrTextExtractor());
            var result = await provider.AnalyzeAsync(new WindowVisionRequest(target, frame));
            Assert.Contains("元枢单窗口读取自动验收", result.UserSummary);
            Assert.False(provider.SendsImageOffDevice);
        }
        finally
        {
            form.BeginInvoke(form.Close);
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        }
    }
}
