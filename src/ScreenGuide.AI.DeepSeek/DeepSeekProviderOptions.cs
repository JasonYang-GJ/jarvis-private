namespace ScreenGuide.AI.DeepSeek;

public sealed record DeepSeekProviderOptions(
    TimeSpan RequestTimeout,
    int MaxMessageCount,
    int MaxInputCharacters,
    int MaxOutputCharacters,
    int MaxErrorBodyCharacters,
    int MaxSseEventCharacters = 262_144,
    int MaxSseResponseCharacters = 8_000_000)
{
    public static DeepSeekProviderOptions Default { get; } = new(
        RequestTimeout: TimeSpan.FromSeconds(120),
        MaxMessageCount: 512,
        MaxInputCharacters: 500_000,
        MaxOutputCharacters: 1_000_000,
        MaxErrorBodyCharacters: 4_096,
        MaxSseEventCharacters: 262_144,
        MaxSseResponseCharacters: 8_000_000);
}
