using SherpaOnnx;

namespace ScreenGuide.Voice.Windows;

internal static class OfflineVoiceRecognizerFactory
{
    public const int SampleRate = 16000;

    public static OnlineRecognizer Create(LocalVoiceModelPaths paths) =>
        new(CreateConfiguration(paths));

    private static OnlineRecognizerConfig CreateConfiguration(LocalVoiceModelPaths paths)
    {
        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = paths.RecognitionEncoder;
        config.ModelConfig.Transducer.Decoder = paths.RecognitionDecoder;
        config.ModelConfig.Transducer.Joiner = paths.RecognitionJoiner;
        config.ModelConfig.Tokens = paths.RecognitionTokens;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.NumThreads = 2;
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        config.EnableEndpoint = 1;
        config.Rule1MinTrailingSilence = 2.0F;
        config.Rule2MinTrailingSilence = 0.75F;
        config.Rule3MinUtteranceLength = 18.0F;
        return config;
    }
}
