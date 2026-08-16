using ScreenGuide.Core;

namespace ScreenGuide.Core.Tests;

public sealed class ConversationHistoryTests
{
    [Fact]
    public void NewHistory_IsEmpty()
    {
        var history = new ConversationHistory(1_000);

        Assert.Empty(history.GetRecentTurns(900, "现在的问题", 100));
    }

    [Fact]
    public void RecentTurns_AreReturnedInConversationOrder()
    {
        var history = new ConversationHistory(10_000);
        history.AddTurn("第一个问题", "第一个回答");
        history.AddTurn("第二个问题", "第二个回答");

        var turns = history.GetRecentTurns(9_000, "第三个问题", 100);

        Assert.Equal(2, turns.Count);
        Assert.Equal("第一个问题", turns[0].UserText);
        Assert.Equal("第二个问题", turns[1].UserText);
    }

    [Fact]
    public void TokenBudget_DropsOldestTurnsFirst()
    {
        var history = new ConversationHistory(10_000);
        history.AddTurn(new string('旧', 120), new string('答', 120));
        history.AddTurn("最近的问题", "最近的回答");

        var turns = history.GetRecentTurns(180, "新问题", 100);

        Assert.Single(turns);
        Assert.Equal("最近的问题", turns[0].UserText);
    }

    [Fact]
    public void Clear_RemovesAllTurns()
    {
        var history = new ConversationHistory(1_000);
        history.AddTurn("问题", "回答");

        history.Clear();

        Assert.Empty(history.GetRecentTurns(900, "新问题", 100));
    }
}
