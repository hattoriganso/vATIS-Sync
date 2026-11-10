using VatisSync.Core;

namespace VatisSync.Tests;

public class NoticeBoardTests
{
    [Fact]
    public void A_notice_tells_the_screen_when_it_starts_fading()
    {
        var notice = new NoticeBoard().Post("done", NoticeKind.Info, "Added 1 airport.");
        var changed = new List<string?>();
        notice.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        notice.IsFading = true;
        notice.IsFading = true;

        Assert.True(notice.IsFading);
        Assert.Equal([nameof(Notice.IsFading)], changed);
    }

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

    [Fact]
    public void The_banner_shown_is_an_error_before_a_warning_and_the_newest_of_those()
    {
        var board = new NoticeBoard();
        Assert.Null(board.TopBanner);

        var rules = board.Post("rules", NoticeKind.Warning, "rules file unreadable");
        var settings = board.Post("settings", NoticeKind.Warning, "settings unreadable");
        Assert.Same(settings, board.TopBanner); // the newest warning

        var vatis = board.Post("vatis", NoticeKind.Error, "vATIS is not responding");
        board.Post("airports", NoticeKind.Warning, "airports folder unreadable");
        Assert.Same(vatis, board.TopBanner); // an error outranks a newer warning
        Assert.Equal(4, board.Banners.Count());

        // As each is dismissed or resolved, the next comes up.
        board.Resolve("vatis");
        Assert.Equal("airports", board.TopBanner!.Key);
        board.Dismiss(board.TopBanner);
        Assert.Same(settings, board.TopBanner);
        board.Dismiss(settings);
        Assert.Same(rules, board.TopBanner);
    }

    [Fact]
    public void A_confirmation_is_never_the_banner()
    {
        var board = new NoticeBoard();
        board.Post("done", NoticeKind.Info, "Added 1 airport");
        Assert.Null(board.TopBanner);
        Assert.Empty(board.Banners);
    }
}
