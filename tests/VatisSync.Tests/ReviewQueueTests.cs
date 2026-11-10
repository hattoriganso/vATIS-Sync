using VatisSync.Core;

namespace VatisSync.Tests;

public class ReviewQueueTests
{
    private static ApprovalRequest Request(string airport, string from = "26", string to = "8", bool flow = true) =>
        new(airport, [new StationChange(airport, "old", "new", flow, new FlowSignature([from], []), new FlowSignature([to], []))]);

    [Fact]
    public async Task A_question_waits_until_the_controller_decides()
    {
        var queue = new ReviewQueue();
        var answer = queue.RequestAsync(Request("KPHX"), CancellationToken.None);

        Assert.False(answer.IsCompleted);
        var item = Assert.Single(queue.Items);
        Assert.Equal("KPHX", item.Airport);

        queue.Decide(item, ApprovalDecision.Apply);
        Assert.Equal(ApprovalDecision.Apply, await answer);
        Assert.Empty(queue.Items);
    }

    [Fact]
    public async Task A_withdrawn_question_leaves_the_queue_and_cancels_the_answer()
    {
        var queue = new ReviewQueue();
        using var cts = new CancellationTokenSource();
        var answer = queue.RequestAsync(Request("KPHX"), cts.Token);
        var item = Assert.Single(queue.Items);

        cts.Cancel();

        Assert.Empty(queue.Items);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => answer);
        queue.Decide(item, ApprovalDecision.Apply); // a late click on a withdrawn question does nothing
        Assert.True(answer.IsCanceled);
    }

    [Fact]
    public async Task An_airport_waits_once_a_newer_question_replaces_the_older()
    {
        var queue = new ReviewQueue();
        var first = queue.RequestAsync(Request("KPHX", to: "8"), CancellationToken.None);
        var second = queue.RequestAsync(Request("KPHX", to: "9"), CancellationToken.None);

        Assert.Equal("9", Assert.Single(queue.Items).Request.Changes[0].NewFlow.Runways[0]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(second.IsCompleted);
    }

    [Fact]
    public void Questions_are_listed_oldest_first_and_every_change_is_announced()
    {
        var queue = new ReviewQueue();
        var changed = 0;
        var asked = new List<string>();
        queue.Changed += () => changed++;
        queue.Asked += r => asked.Add(r.Airport);

        _ = queue.RequestAsync(Request("KPHX"), CancellationToken.None);
        _ = queue.RequestAsync(Request("KLAX"), CancellationToken.None);
        queue.Decide(queue.Items[0], ApprovalDecision.Disregard);

        Assert.Equal(["KLAX"], queue.Items.Select(i => i.Airport));
        Assert.Equal(["KPHX", "KLAX"], asked);
        Assert.Equal(3, changed); // two added, one answered
    }

    [Fact]
    public void The_strip_names_the_airport_when_one_waits_and_counts_when_several_do()
    {
        var queue = new ReviewQueue();
        Assert.Equal("", ReviewQueue.StripText(queue.Items));

        _ = queue.RequestAsync(Request("KPHX"), CancellationToken.None);
        Assert.Equal("KPHX: Runways 26 → 8.", ReviewQueue.StripText(queue.Items));

        _ = queue.RequestAsync(Request("KLAX"), CancellationToken.None);
        Assert.Equal("2 airports waiting review", ReviewQueue.StripText(queue.Items));
    }

    [Fact]
    public void The_one_line_summary_is_the_runway_change_even_when_another_station_only_changes_its_text()
    {
        var request = new ApprovalRequest("KPHX",
        [
            new StationChange("KPHX_D", "a", "b", false, new FlowSignature(["26L"], []), new FlowSignature(["26L"], [])),
            new StationChange("KPHX_A", "a", "b", true, new FlowSignature(["26"], []), new FlowSignature(["8"], [])),
        ]);

        Assert.Equal("Runways 26 → 8.", request.FlowSummary);
    }
}
