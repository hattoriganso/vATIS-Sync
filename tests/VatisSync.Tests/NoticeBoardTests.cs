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

    [Fact]
    public void Posting_the_same_problem_again_updates_the_showing_notice_in_place()
    {
        var board = new NoticeBoard();
        var first = board.Post("vatis", NoticeKind.Warning, "retrying in 3 seconds");
        var changes = new List<string?>();
        first.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        var again = board.Post("vatis", NoticeKind.Warning, "retrying in 2 seconds");

        Assert.Same(first, again);
        Assert.Equal("retrying in 2 seconds", first.Message);
        Assert.Equal(["Message"], changes);
        Assert.Single(board.Items);
    }

    [Fact]
    public void A_different_kind_replaces_the_notice()
    {
        var board = new NoticeBoard();
        var warning = board.Post("vatis", NoticeKind.Warning, "waiting");
        var error = board.Post("vatis", NoticeKind.Error, "failed");

        Assert.NotSame(warning, error);
        Assert.Equal(NoticeKind.Error, Assert.Single(board.Items).Kind);
    }

    [Fact]
    public void Update_changes_the_text_only_while_the_notice_is_still_showing()
    {
        var board = new NoticeBoard();
        var n = board.Post("vatis", NoticeKind.Warning, "retrying in 5 seconds");

        Assert.True(board.Update("vatis", "retrying in 4 seconds"));
        Assert.Equal("retrying in 4 seconds", n.Message);

        board.Dismiss(n); // the user closed it: a later tick must not bring it back
        Assert.False(board.Update("vatis", "retrying in 3 seconds"));
        Assert.Empty(board.Items);
    }
}
