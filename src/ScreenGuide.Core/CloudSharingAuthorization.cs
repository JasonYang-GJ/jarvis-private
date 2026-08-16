namespace ScreenGuide.Core;

public sealed class CloudSharingAuthorization
{
    public bool IsGranted { get; private set; }

    public void GrantForForegroundWindowSession() => IsGranted = true;

    public bool CanShareForegroundFrame() => IsGranted;

    public void Revoke() => IsGranted = false;
}
