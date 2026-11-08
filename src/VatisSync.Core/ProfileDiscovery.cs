namespace VatisSync.Core;

public enum DiscoveryPhase
{
    /// <summary>Not started, or stopped.</summary>
    Idle,

    /// <summary>Asking vATIS for its profile right now.</summary>
    Searching,

    /// <summary>vATIS could not be read; counting down to the next try.</summary>
    Waiting,

    /// <summary>vATIS answered. No further tries until asked.</summary>
    Found,
}

/// <param name="Remaining">While <see cref="DiscoveryPhase.Waiting"/>, the time left before the next try.</param>
/// <param name="Problem">While waiting, what went wrong with the last try.</param>
/// <param name="Failures">Consecutive failed tries so far in this episode (0 once vATIS answers).</param>
public sealed record DiscoveryState(
    DiscoveryPhase Phase, TimeSpan Remaining = default, string? Problem = null, int Failures = 0);

/// <summary>
/// Finds out which airports are in the vATIS profile the user has open, without being asked and without giving up.
/// It reads the profile (read-only), and if vATIS cannot be reached it waits, counting down, and tries again, over and
/// over. When vATIS answers it reports the airports and goes quiet until <see cref="RetryNow"/>.
/// </summary>
public sealed class ProfileDiscovery(
    Func<CancellationToken, Task<IReadOnlyList<VatisStation>>> source,
    TimeSpan? retryDelay = null,
    TimeSpan? tick = null,
    TimeSpan? attemptTimeout = null)
{
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(15);

    private readonly TimeSpan _retryDelay = retryDelay ?? DefaultRetryDelay;
    private readonly TimeSpan _tick = tick ?? TimeSpan.FromSeconds(1);
    private readonly TimeSpan _attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim _retry = new(0, 1);
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _run;
    private int _failures;

    public DiscoveryState State { get; private set; } = new(DiscoveryPhase.Idle);

    /// <summary>Raised (from a background thread) each time the state changes, including every tick of the countdown.</summary>
    public event Action<DiscoveryState>? StateChanged;

    /// <summary>Raised (from a background thread) each time vATIS answers: the airports (ICAO, alphabetical) that have a D-ATIS station.</summary>
    public event Action<IReadOnlyList<string>>? AirportsFound;

    public bool IsRunning => _run is { IsCompleted: false };

    /// <summary>Begins trying. Does nothing if already running.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _run = Task.Run(() => RunAsync(ct));
        }
    }

    /// <summary>
    /// Tries right now. While waiting it ends the countdown, so a failure starts a full new one. After vATIS has answered
    /// it reads the profile again. While a try is already in flight it has no effect.
    /// </summary>
    public void RetryNow()
    {
        try { _retry.Release(); }
        catch (SemaphoreFullException) { }
    }

    public async Task StopAsync()
    {
        Task? run;
        lock (_gate)
        {
            _cts?.Cancel();
            run = _run;
        }
        if (run is not null)
        {
            try { await run.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        Set(new DiscoveryState(DiscoveryPhase.Idle));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (await TryOnceAsync(ct).ConfigureAwait(false))
                {
                    await _retry.WaitAsync(ct).ConfigureAwait(false); // quiet until the user asks again
                    continue;
                }

                var problem = State.Problem;
                for (var left = _retryDelay; left > TimeSpan.Zero; left -= _tick)
                {
                    Set(new DiscoveryState(DiscoveryPhase.Waiting, left, problem, _failures));
                    if (await DelayOrRetryAsync(left < _tick ? left : _tick, ct).ConfigureAwait(false)) break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> TryOnceAsync(CancellationToken ct)
    {
        while (_retry.Wait(0)) { } // a button press from before this try is already satisfied by it
        Set(new DiscoveryState(DiscoveryPhase.Searching, Failures: _failures));

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(_attemptTimeout);
        try
        {
            var airports = ProfileAirports.From(await source(attempt.Token).ConfigureAwait(false));
            _failures = 0;
            Set(new DiscoveryState(DiscoveryPhase.Found));
            try { AirportsFound?.Invoke(airports); } catch { /* a listener's bug must not stop the loop */ }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _failures++;
            var problem = ex is VatisUnavailableException or OperationCanceledException
                ? "Could not reach vATIS."
                : ex.Message;
            Set(new DiscoveryState(DiscoveryPhase.Searching, Problem: problem, Failures: _failures));
            return false;
        }
    }

    /// <summary>Waits for <paramref name="delay"/>; returns true if <see cref="RetryNow"/> cut it short.</summary>
    private async Task<bool> DelayOrRetryAsync(TimeSpan delay, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var wait = Task.Delay(delay, cts.Token);
        var press = _retry.WaitAsync(cts.Token);
        var first = await Task.WhenAny(wait, press).ConfigureAwait(false);
        cts.Cancel();
        try { await Task.WhenAll(wait, press).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        ct.ThrowIfCancellationRequested();
        return first == press && press.IsCompletedSuccessfully;
    }

    private void Set(DiscoveryState state)
    {
        State = state;
        try { StateChanged?.Invoke(state); } catch { /* same: never let a listener end the loop */ }
    }
}
