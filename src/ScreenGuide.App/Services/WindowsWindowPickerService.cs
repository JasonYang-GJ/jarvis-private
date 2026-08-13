using Windows.Graphics.Capture;

namespace ScreenGuide.App.Services;

internal sealed class WindowsWindowPickerService : IDisposable
{
    private GraphicsCaptureItem? _selectedItem;

    public event EventHandler? SelectedTargetClosed;

    public bool IsSupported => GraphicsCaptureSession.IsSupported();

    public async Task<WindowSelectionResult?> PickWindowAsync(IntPtr ownerWindowHandle)
    {
        if (ownerWindowHandle == IntPtr.Zero)
        {
            throw new ArgumentException("应用窗口句柄无效。", nameof(ownerWindowHandle));
        }

        if (!IsSupported)
        {
            throw new NotSupportedException("当前 Windows 设备不支持系统窗口选择器。");
        }

        var picker = new GraphicsCapturePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerWindowHandle);

        var selectedItem = await picker.PickSingleItemAsync();
        if (selectedItem is null)
        {
            return null;
        }

        ReplaceSelection(selectedItem);

        return new WindowSelectionResult(
            selectedItem.DisplayName,
            selectedItem.Size.Width,
            selectedItem.Size.Height);
    }

    public void ClearSelection()
    {
        if (_selectedItem is null)
        {
            return;
        }

        _selectedItem.Closed -= SelectedItem_Closed;
        _selectedItem = null;
    }

    public void Dispose()
    {
        ClearSelection();
    }

    private void ReplaceSelection(GraphicsCaptureItem selectedItem)
    {
        ClearSelection();
        _selectedItem = selectedItem;
        _selectedItem.Closed += SelectedItem_Closed;
    }

    private void SelectedItem_Closed(GraphicsCaptureItem sender, object args)
    {
        ClearSelection();
        SelectedTargetClosed?.Invoke(this, EventArgs.Empty);
    }
}
