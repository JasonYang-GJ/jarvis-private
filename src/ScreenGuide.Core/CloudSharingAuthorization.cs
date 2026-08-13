namespace ScreenGuide.Core;

public sealed class CloudSharingAuthorization
{
    public string? AuthorizedWindowTitle { get; private set; }

    public bool IsGranted => AuthorizedWindowTitle is not null;

    public void GrantForWindow(string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            throw new ArgumentException("必须先选择一个窗口才能授权云端分析。", nameof(windowTitle));
        }

        AuthorizedWindowTitle = windowTitle.Trim();
    }

    public bool CanShareFrameFrom(string windowTitle) =>
        AuthorizedWindowTitle is not null
        && string.Equals(AuthorizedWindowTitle, windowTitle.Trim(), StringComparison.Ordinal);

    public void Revoke() => AuthorizedWindowTitle = null;
}
