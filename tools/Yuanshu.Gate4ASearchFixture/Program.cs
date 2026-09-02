using System.Drawing;

namespace Yuanshu.Gate4ASearchFixture;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new SearchFixtureForm());
    }
}

public static class SubmissionReceipt
{
    public static string Format(int submitCount, int textLength) =>
        $"SubmitCount={submitCount};TextLength={textLength}";
}

internal sealed class SearchFixtureForm : Form
{
    internal const string ReadyTitle = "Yuanshu Gate4A Compiled Fixture Ready";
    internal const string SearchAutomationId = "Gate4ASearchBox";
    internal const string SearchAccessibleName = "搜索框";

    private readonly TextBox _searchBox;
    private readonly Label _status;
    private int _submitCount;

    internal SearchFixtureForm()
    {
        Text = "Yuanshu Gate4A Compiled Fixture Starting";
        ClientSize = new Size(560, 170);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;

        var instruction = new Label
        {
            Name = "Gate4AInstruction",
            Text = "Controlled offline search fixture",
            Location = new Point(24, 20),
            Size = new Size(500, 24)
        };
        _searchBox = new TextBox
        {
            Name = SearchAutomationId,
            AccessibleName = SearchAccessibleName,
            AccessibleDescription = "Controlled local search input",
            Location = new Point(24, 54),
            Size = new Size(500, 30),
            ReadOnly = false,
            UseSystemPasswordChar = false,
            TabIndex = 0
        };
        _status = new Label
        {
            Name = "Gate4AStatus",
            AccessibleName = "提交状态",
            Text = SubmissionReceipt.Format(0, 0),
            Location = new Point(24, 100),
            Size = new Size(500, 24)
        };
        _searchBox.KeyDown += OnSearchKeyDown;
        Controls.Add(instruction);
        Controls.Add(_searchBox);
        Controls.Add(_status);
        ActiveControl = _searchBox;
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        _ = _searchBox.Handle;
        Text = ReadyTitle;
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode != Keys.Enter)
        {
            return;
        }

        _submitCount++;
        var receipt = SubmissionReceipt.Format(_submitCount, _searchBox.TextLength);
        _status.Text = receipt;
        Text = $"Yuanshu Gate4A Compiled Fixture Submitted;{receipt}";
        eventArgs.SuppressKeyPress = true;
        eventArgs.Handled = true;
    }
}
