using VatisSync.Core;

namespace VatisSync.Tests;

public class NoticeBoardTests
{
    [Fact]
    public void A_repeated_problem_replaces_the_old_message_in_place()
    {
        var board = new NoticeBoard();
        board.Post("vatis", NoticeKind.Error, "first");
        board.Post("settings", NoticeKind.Warning, "other");
        board.Post("vatis", NoticeKind.Error, "second");

        Assert.Equal(["second", "other"], board.Items.Select(n => n.Message));
    }

    [Fact]
    public void Different_problems_stack_in_the_order_they_arrived()
    {
        var board = new NoticeBoard();
        board.Post("a", NoticeKind.Error, "A");
        board.Post("b", NoticeKind.Warning, "B");
        Assert.Equal(["A", "B"], board.Items.Select(n => n.Message));
    }

    [Fact]
    public void Resolving_clears_only_that_problem()
    {
        var board = new NoticeBoard();
        board.Post("a", NoticeKind.Error, "A");
        board.Post("b", NoticeKind.Error, "B");

        Assert.True(board.Resolve("a"));
        Assert.False(board.Resolve("a")); // nothing left to resolve
        Assert.Equal(["B"], board.Items.Select(n => n.Message));
    }

    [Fact]
    public void Dismissing_removes_the_message_and_tolerates_a_second_dismissal()
    {
        var board = new NoticeBoard();
        var n = board.Post("a", NoticeKind.Error, "A");

        Assert.True(board.Dismiss(n));
        Assert.False(board.Dismiss(n));
        Assert.Empty(board.Items);
    }

    [Fact]
    public void A_dismissed_problem_can_come_back()
    {
        var board = new NoticeBoard();
        board.Dismiss(board.Post("vatis", NoticeKind.Error, "down"));
        board.Post("vatis", NoticeKind.Error, "down again");
        Assert.Equal(["down again"], board.Items.Select(n => n.Message));
    }
}
