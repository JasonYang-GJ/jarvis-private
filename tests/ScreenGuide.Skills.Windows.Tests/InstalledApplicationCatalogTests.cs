namespace ScreenGuide.Skills.Windows.Tests;

public sealed class InstalledApplicationCatalogTests
{
    [Fact]
    public void SelectUniqueApplication_PrefersSingleRegisteredExecutableOverShortcut()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sg-app-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "sample.exe");
        File.WriteAllBytes(executable, []);
        try
        {
            var selected = InstalledApplicationCatalog.SelectUniqueApplication(
            [
                new KnownDesktopApplication("installed-exe", "示例应用", executable),
                new KnownDesktopApplication("installed-link", "示例应用", Path.Combine(root, "示例应用.lnk"))
            ]);

            Assert.NotNull(selected);
            Assert.Equal(executable, selected.LaunchTarget);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SelectUniqueApplication_RejectsTwoDifferentRegisteredExecutables()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sg-app-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var first = Path.Combine(root, "first.exe");
        var second = Path.Combine(root, "second.exe");
        File.WriteAllBytes(first, []);
        File.WriteAllBytes(second, []);
        try
        {
            var selected = InstalledApplicationCatalog.SelectUniqueApplication(
            [
                new KnownDesktopApplication("installed-first", "同名应用", first),
                new KnownDesktopApplication("installed-second", "同名应用", second)
            ]);

            Assert.Null(selected);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
