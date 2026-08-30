using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.Vision.Windows;

/// <summary>
/// Preferred exact-HWND capture based on Windows Graphics Capture. It obtains one frame and
/// closes the session immediately; no monitor or desktop capture item is ever created.
/// </summary>
public sealed class WindowsGraphicsCaptureBackend(
    IWindowCaptureTargetVerifier identityVerifier) : IExactWindowCaptureBackend
{
    private const uint D3d11CreateDeviceBgraSupport = 0x20;
    private const int D3dDriverTypeHardware = 1;
    private const int D3dDriverTypeWarp = 5;
    private const string GraphicsCaptureItemRuntimeClass =
        "Windows.Graphics.Capture.GraphicsCaptureItem";

    public async Task<RawWindowFrame> CaptureAsync(
        WindowCaptureTarget target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        identityVerifier.Verify(target);
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new PlatformNotSupportedException("当前 Windows 版本不支持单窗口 Graphics Capture。 ");
        }

        var item = CreateItemForWindow(new IntPtr(target.WindowHandle));
        if (item.Size.Width < 2 || item.Size.Height < 2)
        {
            throw new InvalidOperationException("目标窗口尺寸无效，因此没有读取画面。 ");
        }

        using var device = CreateDirect3DDevice();
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            1,
            item.Size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        var frameReady = new TaskCompletionSource<Direct3D11CaptureFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            try
            {
                var frame = sender.TryGetNextFrame();
                if (!frameReady.TrySetResult(frame))
                {
                    frame.Dispose();
                }
            }
            catch (Exception exception)
            {
                frameReady.TrySetException(exception);
            }
        }

        pool.FrameArrived += OnFrameArrived;
        try
        {
            session.StartCapture();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            using var frame = await frameReady.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
            var bytes = await EncodePngAsync(bitmap, cancellationToken).ConfigureAwait(false);
            return new RawWindowFrame(
                bytes,
                frame.ContentSize.Width,
                frame.ContentSize.Height,
                "Windows.GraphicsCapture.SingleHwnd");
        }
        finally
        {
            pool.FrameArrived -= OnFrameArrived;
        }
    }

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr windowHandle)
    {
        var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        var interopIid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        using var factory = WinRT.ActivationFactory.Get(
            GraphicsCaptureItemRuntimeClass,
            interopIid);
        var interopPointer = factory.GetRef();
        try
        {
            var vtable = Marshal.ReadIntPtr(interopPointer);
            var methodPointer = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
            var createForWindow = Marshal.GetDelegateForFunctionPointer<CreateForWindowDelegate>(
                methodPointer);
            var result = createForWindow(interopPointer, windowHandle, ref iid, out var itemPointer);
            Marshal.ThrowExceptionForHR(result);
            try
            {
                return GraphicsCaptureItem.FromAbi(itemPointer);
            }
            finally
            {
                Marshal.Release(itemPointer);
            }
        }
        finally
        {
            Marshal.Release(interopPointer);
        }
    }

    private static IDirect3DDevice CreateDirect3DDevice()
    {
        var result = D3D11CreateDevice(
            IntPtr.Zero,
            D3dDriverTypeHardware,
            IntPtr.Zero,
            D3d11CreateDeviceBgraSupport,
            IntPtr.Zero,
            0,
            7,
            out var d3dDevice,
            out _,
            out var deviceContext);
        if (result < 0)
        {
            result = D3D11CreateDevice(
                IntPtr.Zero,
                D3dDriverTypeWarp,
                IntPtr.Zero,
                D3d11CreateDeviceBgraSupport,
                IntPtr.Zero,
                0,
                7,
                out d3dDevice,
                out _,
                out deviceContext);
        }

        Marshal.ThrowExceptionForHR(result);
        try
        {
            var dxgiDeviceIid = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            var d3dVtable = Marshal.ReadIntPtr(d3dDevice);
            var queryInterfacePointer = Marshal.ReadIntPtr(d3dVtable);
            var queryInterface = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(
                queryInterfacePointer);
            var queryResult = queryInterface(d3dDevice, ref dxgiDeviceIid, out var dxgiDevice);
            if (queryResult < 0)
            {
                throw new COMException($"ID3D11Device 未提供 IDXGIDevice，HRESULT=0x{queryResult:X8}。", queryResult);
            }
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(
                    dxgiDevice,
                    out var inspectable));
                try
                {
                    return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        finally
        {
            if (deviceContext != IntPtr.Zero) Marshal.Release(deviceContext);
            if (d3dDevice != IntPtr.Zero) Marshal.Release(d3dDevice);
        }
    }

    private static async Task<byte[]> EncodePngAsync(
        SoftwareBitmap bitmap,
        CancellationToken cancellationToken)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        cancellationToken.ThrowIfCancellationRequested();
        stream.Seek(0);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[stream.Size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForWindowDelegate(
        IntPtr thisPointer,
        IntPtr window,
        ref Guid iid,
        out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(
        IntPtr thisPointer,
        ref Guid iid,
        out IntPtr result);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelsCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);
}

public sealed class ResilientExactWindowCaptureBackend : IExactWindowCaptureBackend
{
    private readonly IExactWindowCaptureBackend _preferred;
    private readonly IExactWindowCaptureBackend _fallback;
    private readonly IWindowCaptureTargetVerifier _identityVerifier;

    public ResilientExactWindowCaptureBackend(
        WindowsGraphicsCaptureBackend preferred,
        PrintWindowCaptureBackend fallback,
        IWindowCaptureTargetVerifier identityVerifier)
        : this((IExactWindowCaptureBackend)preferred, fallback, identityVerifier)
    {
    }

    internal ResilientExactWindowCaptureBackend(
        IExactWindowCaptureBackend preferred,
        IExactWindowCaptureBackend fallback,
        IWindowCaptureTargetVerifier identityVerifier)
    {
        _preferred = preferred;
        _fallback = fallback;
        _identityVerifier = identityVerifier;
    }

    public async Task<RawWindowFrame> CaptureAsync(
        WindowCaptureTarget target,
        CancellationToken cancellationToken)
    {
        _identityVerifier.Verify(target);
        try
        {
            return await _preferred.CaptureAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not UnauthorizedAccessException
            // WGC's linked first-frame timeout also surfaces as cancellation. Only a
            // cancellation requested by our caller is authority to stop this capture.
            && (exception is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested))
        {
            _identityVerifier.Verify(target);
            return await _fallback.CaptureAsync(target, cancellationToken).ConfigureAwait(false);
        }
    }
}
