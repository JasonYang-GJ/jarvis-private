namespace ScreenGuide.Core;

public sealed record ConversationTurn(string UserText, string AssistantText)
{
    public int EstimatedTokens => ConversationHistory.EstimateTokens(UserText)
                                  + ConversationHistory.EstimateTokens(AssistantText)
                                  + 8;
}

public sealed class ConversationHistory
{
    private readonly object _gate = new();
    private readonly List<ConversationTurn> _turns = [];
    private readonly int _storageTokenLimit;

    public ConversationHistory(int storageTokenLimit)
    {
        if (storageTokenLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(storageTokenLimit));
        }

        _storageTokenLimit = storageTokenLimit;
    }

    public void AddTurn(string userText, string assistantText)
    {
        if (string.IsNullOrWhiteSpace(userText) || string.IsNullOrWhiteSpace(assistantText))
        {
            return;
        }

        lock (_gate)
        {
            _turns.Add(new ConversationTurn(userText.Trim(), assistantText.Trim()));
            TrimToLimit(_storageTokenLimit);
        }
    }

    public IReadOnlyList<ConversationTurn> GetRecentTurns(
        int requestTokenBudget,
        string currentQuestion,
        int reservedTokens)
    {
        if (requestTokenBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTokenBudget));
        }

        var remaining = Math.Max(
            0,
            requestTokenBudget - reservedTokens - EstimateTokens(currentQuestion));
        var selected = new List<ConversationTurn>();

        lock (_gate)
        {
            for (var index = _turns.Count - 1; index >= 0; index--)
            {
                var turn = _turns[index];
                if (turn.EstimatedTokens > remaining)
                {
                    break;
                }

                selected.Add(turn);
                remaining -= turn.EstimatedTokens;
            }
        }

        selected.Reverse();
        return selected;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _turns.Clear();
        }
    }

    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var cjkCharacters = 0;
        var otherCharacters = 0;
        foreach (var character in text)
        {
            if (character is >= '\u3400' and <= '\u9fff')
            {
                cjkCharacters++;
            }
            else
            {
                otherCharacters++;
            }
        }

        return (int)Math.Ceiling(cjkCharacters * 1.5D + otherCharacters / 4D);
    }

    private void TrimToLimit(int tokenLimit)
    {
        var total = _turns.Sum(turn => turn.EstimatedTokens);
        while (_turns.Count > 0 && total > tokenLimit)
        {
            total -= _turns[0].EstimatedTokens;
            _turns.RemoveAt(0);
        }
    }
}
