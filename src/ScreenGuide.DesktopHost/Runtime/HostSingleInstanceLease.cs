using System.Security.Cryptography;
using System.Text;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed class HostSingleInstanceLease : IDisposable
{
    private readonly Mutex _mutex;

    private HostSingleInstanceLease(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static HostSingleInstanceLease? TryAcquire(string? scope = null)
    {
        var resolvedScope = string.IsNullOrWhiteSpace(scope)
            ? Environment.GetEnvironmentVariable(DesktopHostOptions.PipeNameEnvironmentVariable)
            : scope;
        var component = string.IsNullOrWhiteSpace(resolvedScope)
            ? "DesktopHost"
            : $"DesktopHost.{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resolvedScope.Trim())))[..16]}";
        var mutex = new Mutex(
            initiallyOwned: false,
            DesktopIpcEndpoint.CurrentUserMutexName(component));
        try
        {
            if (!mutex.WaitOne(TimeSpan.Zero))
            {
                mutex.Dispose();
                return null;
            }
        }
        catch (AbandonedMutexException)
        {
        }

        return new HostSingleInstanceLease(mutex);
    }

    public void Dispose()
    {
        // A Mutex is thread-affine. Host startup and shutdown can resume on different
        // pool threads, so closing the owned handle is safer than ReleaseMutex here.
        _mutex.Dispose();
    }
}
