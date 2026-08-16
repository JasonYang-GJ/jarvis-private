using System.Configuration;
using System.Data;
using System.Windows;

namespace ScreenGuide.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    public static bool ShowSettingsOnStartup => Environment.GetCommandLineArgs()
        .Any(argument => string.Equals(argument, "--settings", StringComparison.OrdinalIgnoreCase));
}
