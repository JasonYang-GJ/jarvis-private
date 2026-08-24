using ScreenGuide.AI.Core;
using Xunit;

namespace ScreenGuide.AI.Core.Tests;

public sealed class FileAiSettingsStoreTests
{
    [Fact]
    public async Task UsesExplicitDefaultAndAtomicallyPersistsAWholeRoute()
    {
        var root = Path.Combine(Path.GetTempPath(), $"screen-guide-ai-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "ai-settings.json");
        var defaultSettings = Settings("provider-default", "model-default");
        try
        {
            var store = new FileAiSettingsStore(path, defaultSettings);
            var initial = await store.LoadAsync();

            var routeA = Settings("provider-a", "model-a");
            var routeB = Settings("provider-b", "model-b");
            var writes = Enumerable.Range(0, 20)
                .Select(index => store.SaveAsync(index % 2 == 0 ? routeA : routeB));
            await Task.WhenAll(writes);
            var reloaded = await new FileAiSettingsStore(path, defaultSettings).LoadAsync();

            Assert.Equal(defaultSettings, initial);
            Assert.Contains(reloaded, new[] { routeA, routeB });
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static AiSettings Settings(string providerId, string modelId) =>
        new(new ChatModelRoute(providerId, modelId));
}
