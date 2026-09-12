namespace ScreenGuide.Voice.Windows;

public sealed record LocalVoiceModelPaths(
    string RecognitionEncoder,
    string RecognitionDecoder,
    string RecognitionJoiner,
    string RecognitionTokens)
{
    private const string RecognitionModelName =
        "sherpa-onnx-streaming-zipformer-zh-int8-2025-06-30";

    public const string ModelDirectoryEnvironmentVariable = "SCREEN_GUIDE_VOICE_MODEL_DIRECTORY";

    public static string ModelRoot =>
        Environment.GetEnvironmentVariable(ModelDirectoryEnvironmentVariable) is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScreenGuideTeacher", "models");

    public static LocalVoiceModelPaths Create(string? modelRoot = null)
    {
        var root = Path.Combine(modelRoot ?? ModelRoot, RecognitionModelName);
        return new LocalVoiceModelPaths(
            Path.Combine(root, "encoder.int8.onnx"),
            Path.Combine(root, "decoder.onnx"),
            Path.Combine(root, "joiner.int8.onnx"),
            Path.Combine(root, "tokens.txt"));
    }

    public bool IsComplete => AllFiles.All(File.Exists);

    public IReadOnlyList<string> MissingFiles =>
        AllFiles.Where(path => !File.Exists(path)).ToArray();

    private IEnumerable<string> AllFiles
    {
        get
        {
            yield return RecognitionEncoder;
            yield return RecognitionDecoder;
            yield return RecognitionJoiner;
            yield return RecognitionTokens;
        }
    }
}
