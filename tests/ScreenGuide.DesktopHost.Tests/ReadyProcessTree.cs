using System.Diagnostics;
using System.Text;

namespace ScreenGuide.DesktopHost.Tests;

/// <summary>Proves cancellation of a live parent/child instead of racing a delayed marker write.</summary>
internal sealed class ReadyProcessTree(string directory) : IDisposable
{
    private readonly string _rootReady = Path.Combine(directory, "root.ready");
    private readonly string _childReady = Path.Combine(directory, "child.ready");
    private readonly List<Process> _processes = [];

    public string Prompt => "TEST_LONG_RUNNING TEST_PROCESS_TREE_BARRIER "
        + "ROOT_READY_BASE64=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(_rootReady)) + " "
        + "CHILD_READY_BASE64=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(_childReady));

    public async Task WaitUntilReadyAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var path in new[] { _rootReady, _childReady })
        {
            while (!File.Exists(path)) await Task.Delay(20, timeout.Token);
            var process = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(path, timeout.Token)));
            _ = process.Handle;
            Assert.Equal("ScreenGuide.FakeCodexCli", process.ProcessName);
            Assert.False(process.HasExited);
            _processes.Add(process);
        }
        Assert.NotEqual(_processes[0].Id, _processes[1].Id);
    }

    public async Task AssertStoppedAsync()
    {
        Assert.Equal(2, _processes.Count);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await Task.WhenAll(_processes.Select(process => process.WaitForExitAsync(timeout.Token)));
        Assert.All(_processes, process => Assert.True(process.HasExited));
    }

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.Dispose();
        }
    }
}
