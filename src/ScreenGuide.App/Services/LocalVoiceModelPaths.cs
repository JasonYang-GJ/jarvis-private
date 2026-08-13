using System.IO;

namespace ScreenGuide.App.Services;

internal sealed record LocalVoiceModelPaths(
    string KeywordEncoder,
    string KeywordDecoder,
    string KeywordJoiner,
    string KeywordTokens,
    string RecognitionEncoder,
    string RecognitionDecoder,
    string RecognitionJoiner,
    string RecognitionTokens)
{
    private const string KeywordModelName = "sherpa-onnx-kws-zipformer-zh-en-3M-2025-12-20";
    private const string RecognitionModelName = "sherpa-onnx-streaming-zipformer-zh-int8-2025-06-30";

    public static string ModelRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScreenGuideTeacher",
        "models");

    public static string WakeKeywordFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScreenGuideTeacher",
        "config",
        "wake-keywords.txt");

    public static LocalVoiceModelPaths Create()
    {
        var keywordRoot = Path.Combine(ModelRoot, KeywordModelName);
        var recognitionRoot = Path.Combine(ModelRoot, RecognitionModelName);

        return new LocalVoiceModelPaths(
            Path.Combine(keywordRoot, "encoder-epoch-13-avg-2-chunk-16-left-64.int8.onnx"),
            Path.Combine(keywordRoot, "decoder-epoch-13-avg-2-chunk-16-left-64.onnx"),
            Path.Combine(keywordRoot, "joiner-epoch-13-avg-2-chunk-16-left-64.int8.onnx"),
            Path.Combine(keywordRoot, "tokens.txt"),
            Path.Combine(recognitionRoot, "encoder.int8.onnx"),
            Path.Combine(recognitionRoot, "decoder.onnx"),
            Path.Combine(recognitionRoot, "joiner.int8.onnx"),
            Path.Combine(recognitionRoot, "tokens.txt"));
    }

    public bool IsComplete => AllFiles.All(File.Exists);

    public IReadOnlyList<string> MissingFiles => AllFiles.Where(path => !File.Exists(path)).ToArray();

    private IEnumerable<string> AllFiles
    {
        get
        {
            yield return KeywordEncoder;
            yield return KeywordDecoder;
            yield return KeywordJoiner;
            yield return KeywordTokens;
            yield return RecognitionEncoder;
            yield return RecognitionDecoder;
            yield return RecognitionJoiner;
            yield return RecognitionTokens;
        }
    }
}
