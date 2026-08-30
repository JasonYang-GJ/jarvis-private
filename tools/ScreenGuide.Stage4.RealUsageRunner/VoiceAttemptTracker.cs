using System.Diagnostics;
using System.Text;

namespace ScreenGuide.Stage4.RealUsageRunner;

public static class VoiceTextNormalizer
{
    public const string Version = "voice-text-normalization.v1";

    public static string Normalize(string? value)
    {
        var builder = new StringBuilder();
        foreach (var character in value ?? string.Empty)
        {
            if (!char.IsWhiteSpace(character) && !char.IsPunctuation(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }
}

public sealed class VoiceAttemptTracker
{
    private readonly string _expectedNormalized;
    private readonly bool _isWarmup;
    private readonly long _attemptStarted;
    private long? _speechStarted;
    private long? _firstFinalAt;
    private int _finalCount;
    private bool _firstFinalMatches;
    private VoiceTextMatchDiagnostic? _voiceTextMatch;
    private bool _faulted;
    private bool _closed;

    public VoiceAttemptTracker(string expectedText, bool isWarmup, long attemptStartedTimestamp)
    {
        _expectedNormalized = VoiceTextNormalizer.Normalize(expectedText);
        if (_expectedNormalized.Length == 0)
        {
            throw new ArgumentException("固定评测短句不能为空。", nameof(expectedText));
        }

        _isWarmup = isWarmup;
        _attemptStarted = attemptStartedTimestamp;
    }

    public void OnPartial(string? text, long timestamp)
    {
        if (_closed || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _speechStarted ??= timestamp;
    }

    public void OnFinal(string? text, long timestamp)
    {
        if (_closed || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _speechStarted ??= timestamp;
        _finalCount++;
        if (_finalCount == 1)
        {
            _firstFinalAt = timestamp;
            var actualNormalized = VoiceTextNormalizer.Normalize(text);
            var editDistance = CalculateEditDistance(_expectedNormalized, actualNormalized);
            _voiceTextMatch = new VoiceTextMatchDiagnostic(
                _expectedNormalized.Length,
                actualNormalized.Length,
                editDistance);
            _firstFinalMatches = editDistance == 0;
        }
    }

    public void OnListenerFaulted()
    {
        if (!_closed)
        {
            _faulted = true;
        }
    }

    public EvaluationAttempt Complete(
        long completedTimestamp,
        bool timedOut = false,
        bool cancelled = false)
    {
        if (_closed)
        {
            throw new InvalidOperationException("本次语音评测已经结束。 ");
        }

        _closed = true;
        var endTimestamp = _firstFinalAt ?? completedTimestamp;
        var startTimestamp = _speechStarted ?? _attemptStarted;
        if (endTimestamp < startTimestamp)
        {
            throw new ArgumentOutOfRangeException(nameof(completedTimestamp));
        }

        var elapsed = Stopwatch.GetElapsedTime(startTimestamp, endTimestamp);
        if (cancelled)
        {
            return new EvaluationAttempt(
                _isWarmup,
                EvaluationTerminalState.Cancelled,
                elapsed,
                "evaluation.cancelled",
                VoiceTextMatch: _voiceTextMatch);
        }

        if (_faulted)
        {
            return new EvaluationAttempt(
                _isWarmup,
                EvaluationTerminalState.Failure,
                elapsed,
                "voice.listener_faulted",
                VoiceTextMatch: _voiceTextMatch);
        }

        if (_finalCount > 1)
        {
            return new EvaluationAttempt(
                _isWarmup,
                EvaluationTerminalState.Failure,
                elapsed,
                "voice.multiple_final",
                VoiceTextMatch: _voiceTextMatch);
        }

        if (_finalCount == 1)
        {
            return new EvaluationAttempt(
                _isWarmup,
                _firstFinalMatches ? EvaluationTerminalState.Success : EvaluationTerminalState.Failure,
                elapsed,
                _firstFinalMatches ? null : "voice.text_mismatch",
                VoiceTextMatch: _voiceTextMatch);
        }

        return new EvaluationAttempt(
            _isWarmup,
            EvaluationTerminalState.Failure,
            elapsed,
            timedOut && _speechStarted is not null ? "voice.timeout" : "voice.no_final");
    }

    private static int CalculateEditDistance(string expected, string actual)
    {
        var previous = new int[actual.Length + 1];
        var current = new int[actual.Length + 1];
        for (var column = 0; column <= actual.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= expected.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= actual.Length; column++)
            {
                var substitutionCost = expected[row - 1] == actual[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[actual.Length];
    }
}
