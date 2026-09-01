using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed record PointerRegionOcrResult(
    Guid AnchorId,
    string Text,
    int CharacterCount,
    int LineCount,
    int RegionWidth,
    int RegionHeight,
    string RegionSource,
    string DiagnosticCode);

/// <summary>
/// Owns short-lived, one-use pointer anchors and regional pixels. It never writes image or OCR
/// content to persistence, logs, evidence, conversation history, or a model provider.
/// </summary>
public sealed class PointerRegionUnderstandingService : IDisposable
{
    private const int MaximumPreparedAnchors = 32;
    private const int MaximumReturnedCharacters = 700;
    private readonly IPointerDesktopProbe _probe;
    private readonly IPointerRegionCaptureService _capture;
    private readonly ILocalOcrTextExtractor _ocr;
    private readonly TimeProvider _timeProvider;
    private readonly Guid _appRunId = Guid.NewGuid();
    private readonly ConcurrentDictionary<Guid, PointerAnchor> _prepared = new();
    private readonly ConcurrentDictionary<Guid, ActiveOperation> _active = new();
    private int _disposed;

    public PointerRegionUnderstandingService(
        IPointerDesktopProbe probe,
        IPointerRegionCaptureService capture,
        ILocalOcrTextExtractor ocr,
        TimeProvider timeProvider)
    {
        _probe = probe;
        _capture = capture;
        _ocr = ocr;
        _timeProvider = timeProvider;
    }

    public PointerAnchor Prepare(bool confirmed, string authorizationSource)
    {
        ThrowIfDisposed();
        if (!confirmed
            || !string.Equals(
                authorizationSource,
                "VisibleConfirmation",
                StringComparison.Ordinal))
        {
            throw new PointerRegionException(
                PointerRegionErrorCodes.ConsentRequired,
                "需要你在可见界面中明确同意这一次本机区域读取。 ");
        }

        RemoveExpired();
        if (_prepared.Count >= MaximumPreparedAnchors)
        {
            throw new PointerRegionException(
                PointerRegionErrorCodes.AnchorUnavailable,
                "待处理的指针确认过多，请取消旧操作后重试。 ");
        }

        var now = _timeProvider.GetUtcNow();
        var anchor = PointerAnchorPolicy.Create(_probe.CaptureCurrent(), _appRunId, now);
        if (!_prepared.TryAdd(anchor.AnchorId, anchor))
        {
            throw new PointerRegionException(
                PointerRegionErrorCodes.AnchorUnavailable,
                "未能创建一次性指针确认，请重试。 ");
        }

        return anchor;
    }

    public async Task<PointerRegionOcrResult> ReadAsync(
        Guid anchorId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_prepared.TryRemove(anchorId, out var anchor) || anchor.AppRunId != _appRunId)
        {
            throw new PointerRegionException(
                PointerRegionErrorCodes.AnchorUnavailable,
                "这次指针确认不存在、已使用或已失效。 ");
        }

        var operation = new ActiveOperation(cancellationToken);
        if (!_active.TryAdd(anchorId, operation))
        {
            operation.Dispose();
            throw new PointerRegionException(
                PointerRegionErrorCodes.AnchorUnavailable,
                "这次指针区域读取已经在进行中。 ");
        }

        try
        {
            var beforeCapture = _probe.ObserveAt(anchor.PhysicalScreenX, anchor.PhysicalScreenY);
            PointerAnchorPolicy.RequireCurrent(anchor, beforeCapture, _timeProvider.GetUtcNow());
            var selection = PointerRegionSelector.Select(
                anchor.Window.Bounds,
                anchor.PhysicalScreenX,
                anchor.PhysicalScreenY,
                beforeCapture.TrustworthyElementBounds);

            CapturedPointerRegion region;
            try
            {
                region = await _capture.CaptureAsync(
                        anchor.Window.Target,
                        anchor.Window.Bounds,
                        selection.Bounds,
                        operation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (PointerRegionException)
            {
                throw;
            }
            catch
            {
                throw new PointerRegionException(
                    PointerRegionErrorCodes.CaptureFailed,
                    "本机未能安全读取目标区域，未保留画面。 ");
            }
            operation.Attach(region);
            operation.Token.ThrowIfCancellationRequested();

            var beforeOcr = _probe.ObserveAt(anchor.PhysicalScreenX, anchor.PhysicalScreenY);
            PointerAnchorPolicy.RequireCurrent(anchor, beforeOcr, _timeProvider.GetUtcNow());
            string rawText;
            try
            {
                rawText = await _ocr.ExtractAsync(region.PngBytes, operation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                throw new PointerRegionException(
                    PointerRegionErrorCodes.OcrFailed,
                    "本机文字识别未能完成，区域画面已清理。 ");
            }
            operation.Token.ThrowIfCancellationRequested();

            var afterOcr = _probe.ObserveAt(anchor.PhysicalScreenX, anchor.PhysicalScreenY);
            PointerAnchorPolicy.RequireCurrent(anchor, afterOcr, _timeProvider.GetUtcNow());
            var compact = Compact(rawText);
            if (!operation.TryComplete())
            {
                throw new OperationCanceledException(operation.Token);
            }
            return new PointerRegionOcrResult(
                anchor.AnchorId,
                compact,
                compact.Length,
                CountLines(compact),
                region.PixelWidth,
                region.PixelHeight,
                selection.Source,
                compact.Length == 0 ? "local_ocr_no_text" : "local_ocr_text_detected");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PointerRegionException(
                PointerRegionErrorCodes.Cancelled,
                "这次本机指针区域读取已取消，画面已清理。 ");
        }
        finally
        {
            _active.TryRemove(anchorId, out _);
            operation.Dispose();
        }
    }

    public bool Cancel(Guid anchorId)
    {
        var removed = _prepared.TryRemove(anchorId, out _);
        if (_active.TryGetValue(anchorId, out var operation))
        {
            removed = operation.TryCancelAndClear() || removed;
        }

        return removed;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _prepared.Clear();
        foreach (var operation in _active.Values)
        {
            operation.TryCancelAndClear();
        }

        _active.Clear();
    }

    private static string Compact(string? value)
    {
        var normalized = Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");
        return normalized.Length <= MaximumReturnedCharacters
            ? normalized
            : normalized[..MaximumReturnedCharacters];
    }

    private static int CountLines(string value) => string.IsNullOrEmpty(value) ? 0 : 1;

    private void RemoveExpired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var item in _prepared)
        {
            if (now < item.Value.CapturedAtUtc
                || now - item.Value.CapturedAtUtc >= PointerAnchorPolicy.MaximumAge)
            {
                _prepared.TryRemove(item.Key, out _);
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref _disposed) != 0,
        this);

    private sealed class ActiveOperation : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private CapturedPointerRegion? _region;
        private int _terminalState;
        private int _disposed;

        public ActiveOperation(CancellationToken cancellationToken)
        {
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _cancellationRegistration = _cancellation.Token.Register(
                () => Interlocked.CompareExchange(ref _terminalState, 2, 0));
        }

        public CancellationToken Token => _cancellation.Token;

        public void Attach(CapturedPointerRegion region)
        {
            ArgumentNullException.ThrowIfNull(region);
            if (Interlocked.CompareExchange(ref _region, region, null) is not null)
            {
                region.Dispose();
                throw new InvalidOperationException("指针区域缓冲区状态无效。 ");
            }

            if (Volatile.Read(ref _terminalState) != 0 || _cancellation.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _region, null)?.Dispose();
                _cancellation.Token.ThrowIfCancellationRequested();
                throw new OperationCanceledException(_cancellation.Token);
            }
        }

        public bool TryComplete() => Interlocked.CompareExchange(ref _terminalState, 1, 0) == 0;

        public bool TryCancelAndClear()
        {
            var accepted = Interlocked.CompareExchange(ref _terminalState, 2, 0) == 0;
            if (accepted)
            {
                try
                {
                    _cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // A simultaneous completion already disposed the same one-use operation.
                }
                Interlocked.Exchange(ref _region, null)?.Dispose();
            }

            return accepted;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _terminalState, 2, 0) == 0)
            {
                try
                {
                    _cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // A simultaneous cancellation already disposed the source.
                }
            }
            Interlocked.Exchange(ref _region, null)?.Dispose();
            _cancellationRegistration.Dispose();
            _cancellation.Dispose();
        }
    }
}
