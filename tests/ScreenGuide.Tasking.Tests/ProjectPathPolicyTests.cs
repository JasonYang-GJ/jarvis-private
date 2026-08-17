using ScreenGuide.Core.Tasking;

namespace ScreenGuide.Tasking.Tests;

public sealed class ProjectPathPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"screen-guide-path-{Guid.NewGuid():N}");

    public ProjectPathPolicyTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
    }

    [Fact]
    public void ResolvesRelativeDirectoryWithinAuthorizedRoot()
    {
        var result = ProjectPathPolicy.ResolveWithinRoot(_root, "src");

        Assert.Equal(Path.Combine(_root, "src"), result, ignoreCase: true);
    }

    [Fact]
    public void RejectsTraversalOutsideAuthorizedRoot()
    {
        Assert.Throws<UnauthorizedAccessException>(
            () => ProjectPathPolicy.ResolveWithinRoot(_root, ".."));
    }

    [Fact]
    public void RejectsAbsoluteWorkingDirectory()
    {
        Assert.Throws<UnauthorizedAccessException>(
            () => ProjectPathPolicy.ResolveWithinRoot(_root, Path.GetTempPath()));
    }

    [Fact]
    public void RejectsMissingAuthorizationRoot()
    {
        var missing = Path.Combine(_root, "missing");

        Assert.Throws<DirectoryNotFoundException>(
            () => ProjectPathPolicy.NormalizeExistingRoot(missing));
    }

    [Fact]
    public void RejectsMissingWorkingDirectory()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => ProjectPathPolicy.ResolveWithinRoot(_root, "missing"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
