namespace ScreenGuide.FakeBrowser;

public sealed class FakeBrowserMarker;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var form = new Form
        {
            Text = "元枢模拟浏览器可见性测试",
            Width = 520,
            Height = 280,
            StartPosition = FormStartPosition.CenterScreen
        };
        using var timer = new System.Windows.Forms.Timer { Interval = 10_000 };
        timer.Tick += (_, _) => form.Close();
        form.Shown += (_, _) => timer.Start();
        Application.Run(form);
    }
}
