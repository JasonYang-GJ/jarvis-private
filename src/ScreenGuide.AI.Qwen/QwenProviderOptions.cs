namespace ScreenGuide.AI.Qwen;

public sealed record QwenProviderOptions(
    TimeSpan RequestTimeout,
    int MaxMessageCount,
    int MaxInputCharacters,
    int MaxOutputCharacters,
    int MaxErrorBodyCharacters,
    int MaxSseEventCharacters = 262_144,
    int MaxSseResponseCharacters = 8_000_000,
    int MaxReasoningCharacters = 1_000_000)
{
    public static QwenProviderOptions Default { get; } = new(
        RequestTimeout: TimeSpan.FromSeconds(120),
        MaxMessageCount: 512,
        MaxInputCharacters: 500_000,
        MaxOutputCharacters: 1_000_000,
        MaxErrorBodyCharacters: 4_096);
}
