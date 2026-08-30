namespace ScreenGuide.Stage4.RealUsageRunner;

public sealed record VisionCandidateMatchSummary(
    string CandidateId,
    int ExpectedLength,
    int BestEditDistance);

public sealed record VisionDiagnosticSummary(
    int SampleCount,
    int CompactTextLength,
    IReadOnlyList<VisionCandidateMatchSummary> Candidates)
{
    public static VisionDiagnosticSummary Empty { get; } = new(0, 0, []);
}

public static class VisionDiagnosticAnalyzer
{
    public static VisionDiagnosticSummary Analyze(
        string analyzedText,
        IReadOnlyList<VisionDiagnosticCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(analyzedText);
        ArgumentNullException.ThrowIfNull(candidates);
        var compactText = RemoveWhitespace(analyzedText);
        var summaries = candidates
            .Select(candidate =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(candidate.Id);
                ArgumentException.ThrowIfNullOrWhiteSpace(candidate.Text);
                var expected = RemoveWhitespace(candidate.Text);
                return new VisionCandidateMatchSummary(
                    candidate.Id,
                    expected.Length,
                    CalculateBestSubstringEditDistance(expected, compactText));
            })
            .ToArray();

        return new VisionDiagnosticSummary(1, compactText.Length, summaries);
    }

    public static bool ContainsIgnoringWhitespace(string analyzedText, string expectedText)
    {
        ArgumentNullException.ThrowIfNull(analyzedText);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedText);
        return RemoveWhitespace(analyzedText)
            .Contains(RemoveWhitespace(expectedText), StringComparison.Ordinal);
    }

    private static string RemoveWhitespace(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character)));

    private static int CalculateBestSubstringEditDistance(string expected, string actual)
    {
        if (expected.Length == 0)
        {
            return 0;
        }

        if (actual.Length == 0)
        {
            return expected.Length;
        }

        var previous = new int[actual.Length + 1];
        var current = new int[actual.Length + 1];
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

        return previous.Min();
    }
}
