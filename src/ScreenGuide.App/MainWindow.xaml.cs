using System.Windows;
using System.Windows.Media;

namespace ScreenGuide.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void SelectWindowButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "当前已经完成安全界面，下一步才会接入 Windows 的窗口选择器。\n\n这次操作没有读取或上传你的屏幕。",
            "尚未开始观察",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StatusLight.Fill = new SolidColorBrush(Color.FromRgb(169, 178, 194));
        StatusTitle.Text = "尚未选择窗口";
        StatusDescription.Text = "只有你主动选择后，软件才能读取那个窗口。";
        StopButton.IsEnabled = false;
    }
}
