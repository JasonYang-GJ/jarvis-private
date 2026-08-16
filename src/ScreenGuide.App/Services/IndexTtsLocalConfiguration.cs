using System.Diagnostics;
using System.IO;

namespace ScreenGuide.App.Services;

internal sealed record IndexTtsLocalConfiguration(
    string ProjectRoot,
    string ModelRoot,
    string ReferenceAudio,
    string BridgeScript,
    Uri ServiceUri)
{
    public static IndexTtsLocalConfiguration Create()
    {
        const string projectRoot = @"D:\AI\IndexTTS2-official";
        var referenceAudio = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Jarvis",
            "IndexTTS2",
            "reference.wav");
        return new IndexTtsLocalConfiguration(
            projectRoot,
            Path.Combine(projectRoot, "checkpoints"),
            referenceAudio,
            Path.Combine(AppContext.BaseDirectory, "tools", "jarvis_indextts_server.py"),
            new Uri("http://127.0.0.1:17861/"));
    }

    public bool IsConfigured => Directory.Exists(ProjectRoot)
        && File.Exists(Path.Combine(ModelRoot, "config.yaml"))
        && File.Exists(ReferenceAudio)
        && File.Exists(BridgeScript);
}

internal sealed class IndexTtsLocalProcess : IDisposable
{
    private readonly IndexTtsLocalConfiguration _configuration;
    private Process? _process;

    public IndexTtsLocalProcess(IndexTtsLocalConfiguration configuration)
    {
        _configuration = configuration;
    }

    public bool TryStart()
    {
        if (!_configuration.IsConfigured)
        {
            return false;
        }

        if (_process is { HasExited: false })
        {
            return true;
        }

        try
        {
            var startInfo = new ProcessStartInfo("uv")
            {
                WorkingDirectory = _configuration.ProjectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("python");
            startInfo.ArgumentList.Add(_configuration.BridgeScript);
            startInfo.ArgumentList.Add("--model-root");
            startInfo.ArgumentList.Add(_configuration.ModelRoot);
            startInfo.ArgumentList.Add("--reference");
            startInfo.ArgumentList.Add(_configuration.ReferenceAudio);
            _process = Process.Start(startInfo);
            return _process is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch (InvalidOperationException)
        {
            // The service already exited.
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }
}
