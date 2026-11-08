using System.Collections.Concurrent;
using VatisSync.Core;

namespace VatisSync.Tests;

public class ProfileDiscoveryTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(40);

    private static VatisStation Station(string name) => new("id-" + name, name, "Combined", ["D-ATIS"]);

    /// <summary>A vATIS that is down until told otherwise, counting how often it was asked.</summary>
    private sealed class FakeVatis
    {
        public volatile bool Up;
        public int Calls;
        public Task<IReadOnlyList<VatisStation>> Ask(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (!Up) throw new VatisUnavailableException("down");
            return Task.FromResult<IReadOnlyList<VatisStation>>([Station("KSFO"), Station("KOAK")]);
        }
    }

    private sealed class Rig
    {
        public FakeVatis Vatis = new();
        public ProfileDiscovery Discovery;
        public ConcurrentQueue<DiscoveryState> States = new();
        public ConcurrentQueue<IReadOnlyList<string>> Found = new();

        public Rig(TimeSpan? delay = null, TimeSpan? tick = null)
        {
            Discovery = new ProfileDiscovery(Vatis.Ask, delay ?? Delay, tick ?? Tick, TimeSpan.FromSeconds(2));
            Discovery.StateChanged += States.Enqueue;
            Discovery.AirportsFound += Found.Enqueue;
        }
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Succeeds_on_the_first_try_without_waiting()
    {
        var r = new Rig();
        r.Vatis.Up = true;

        r.Discovery.Start();
        await WaitFor(() => r.Discovery.State.Phase == DiscoveryPhase.Found, "found");
        await r.Discovery.StopAsync();

        Assert.Equal(["KOAK", "KSFO"], r.Found.Single());
        Assert.DoesNotContain(r.States, s => s.Phase == DiscoveryPhase.Waiting);
        Assert.Equal(1, r.Vatis.Calls);
    }

    [Fact]
    public async Task When_vatis_is_closed_it_counts_down_then_retries_until_vatis_opens()
    {
        var r = new Rig();

        r.Discovery.Start();
        await WaitFor(() => r.Discovery.State.Phase == DiscoveryPhase.Waiting, "the first countdown");
        Assert.Equal(Messages.Get(Msg.DiscoveryProblem), r.Discovery.State.Problem);
        await WaitFor(() => r.Vatis.Calls >= 3, "automatic retries"); // two full countdowns went by

        r.Vatis.Up = true;
        await WaitFor(() => r.Discovery.State.Phase == DiscoveryPhase.Found, "found once vATIS opened");
        await r.Discovery.StopAsync();

        // The countdown ran down in steps and was restarted in full after each failed retry.
        var waits = r.States.Where(s => s.Phase == DiscoveryPhase.Waiting).Select(s => s.Remaining).ToList();
        Assert.Equal(Delay, waits[0]);
        Assert.Contains(waits, w => w < Delay);
        Assert.True(waits.Count(w => w == Delay) >= 2, "each failure should restart the counter at the full delay");
        Assert.Single(r.Found);
    }

    [Fact]
    public async Task Failures_are_counted_and_reset_once_vatis_answers()
    {
        var r = new Rig();
        r.Discovery.Start();
        await WaitFor(() => r.Vatis.Calls >= 3, "a few failures");
        Assert.True(r.Discovery.State.Failures >= 2);

        r.Vatis.Up = true;
        await WaitFor(() => r.Discovery.State.Phase == DiscoveryPhase.Found, "found");
        await r.Discovery.StopAsync();
        Assert.Contains(r.States, s => s.Phase == DiscoveryPhase.Found && s.Failures == 0);
    }

    [Fact]
    public async Task Retry_now_ends_the_countdown_and_tries_immediately()
    {
        var r = new Rig(delay: TimeSpan.FromSeconds(30), tick: TimeSpan.FromMilliseconds(50)); // would wait far too long
        r.Discovery.Start();
        await WaitFor(() => r.Discovery.State.Phase == DiscoveryPhase.Waiting, "waiting");
        Assert.Equal(1, r.Vatis.Calls);

        r.Vatis.Up = true;
        r.Discovery.RetryNow();
        await WaitFor(() => r.Discovery.State.Phase == DiscoveryPhase.Found, "an immediate retry");
        await r.Discovery.StopAsync();

        Assert.Equal(2, r.Vatis.Calls);
    }

    [Fact]
    public async Task A_manual_retry_that_fails_starts_a_full_new_countdown()
    {
        var r = new Rig(delay: TimeSpan.FromSeconds(30), tick: TimeSpan.FromMilliseconds(50));
        r.Discovery.Start();
        await WaitFor(() => r.Discovery.State.Phase == DiscoveryPhase.Waiting, "waiting");
        await Task.Delay(300); // let the countdown run down a little
        Assert.True(r.Discovery.State.Remaining < TimeSpan.FromSeconds(30));

        r.Discovery.RetryNow();
        await WaitFor(() => r.Vatis.Calls == 2, "the manual retry");
        await WaitFor(() => r.Discovery.State is { Phase: DiscoveryPhase.Waiting } s && s.Remaining == TimeSpan.FromSeconds(30), "the counter to restart");
        await r.Discovery.StopAsync();
    }

    [Fact]
    public async Task After_success_it_stays_quiet_until_asked_and_then_reads_the_profile_again()
    {
        var r = new Rig();
        r.Vatis.Up = true;
        r.Discovery.Start();
        await WaitFor(() => r.Discovery.State.Phase == DiscoveryPhase.Found, "found");

        await Task.Delay(400); // several would-be countdowns
        Assert.Equal(1, r.Vatis.Calls);

        r.Discovery.RetryNow();
        await WaitFor(() => r.Found.Count == 2, "a second read");
        await r.Discovery.StopAsync();
        Assert.Equal(2, r.Vatis.Calls);
    }

    [Fact]
    public async Task An_unexpected_error_is_a_failure_too_and_keeps_retrying()
    {
        var calls = 0;
        var d = new ProfileDiscovery(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidCastException("odd reply");
            return Task.FromResult<IReadOnlyList<VatisStation>>([Station("KSFO")]);
        }, Delay, Tick);

        d.Start();
        await WaitFor(() => d.State.Phase == DiscoveryPhase.Waiting, "waiting after the odd error");
        Assert.Equal("odd reply", d.State.Problem);
        await WaitFor(() => d.State.Phase == DiscoveryPhase.Found, "recovering");
        await d.StopAsync();
    }

    [Fact]
    public async Task A_listener_that_throws_does_not_stop_the_loop()
    {
        var r = new Rig();
        r.Discovery.AirportsFound += _ => throw new InvalidOperationException("listener bug");
        r.Vatis.Up = true;
        r.Discovery.Start();
        await WaitFor(() => r.Discovery.State.Phase == DiscoveryPhase.Found, "found");

        r.Discovery.RetryNow();
        await WaitFor(() => r.Vatis.Calls == 2, "still responding to retries");
        await r.Discovery.StopAsync();
    }

    [Fact]
    public async Task Stop_ends_the_loop_and_a_second_start_is_ignored_while_running()
    {
        var r = new Rig();
        r.Discovery.Start();
        r.Discovery.Start();
        await WaitFor(() => r.Discovery.State.Phase == DiscoveryPhase.Waiting, "waiting");

        await r.Discovery.StopAsync();
        var calls = r.Vatis.Calls;
        await Task.Delay(400);

        Assert.False(r.Discovery.IsRunning);
        Assert.Equal(DiscoveryPhase.Idle, r.Discovery.State.Phase);
        Assert.Equal(calls, r.Vatis.Calls);
    }

    [Fact]
    public async Task Stations_without_the_preset_are_reported_once_per_read_and_never_as_empty()
    {
        var reads = 0;
        var d = new ProfileDiscovery(_ =>
        {
            var n = Interlocked.Increment(ref reads);
            return Task.FromResult<IReadOnlyList<VatisStation>>(n == 1
                ? [Station("KSFO"), new VatisStation("id-new", "KJFK", "Combined", [])]
                : [Station("KSFO"), Station("KJFK")]); // the user added the preset
        }, Delay, Tick);
        var skipped = new ConcurrentQueue<IReadOnlyList<string>>();
        var found = new ConcurrentQueue<IReadOnlyList<string>>();
        d.StationsWithoutPreset += skipped.Enqueue;
        d.AirportsFound += found.Enqueue;

        d.Start();
        await WaitFor(() => found.Count == 1, "the first read");
        Assert.Equal(["KJFK"], skipped.Single());
        Assert.Equal(["KSFO"], found.Single()); // KJFK is not offered as an airport

        d.RetryNow();
        await WaitFor(() => found.Count == 2, "the second read");
        await d.StopAsync();

        Assert.Single(skipped); // nothing skipped the second time, so no second report
        Assert.Equal(["KJFK", "KSFO"], found.Last());
    }
}
