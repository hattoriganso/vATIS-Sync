namespace VatisSync.Core;

/// <summary>One change waiting for the controller. It stays until they decide, or the engine withdraws it.</summary>
public sealed class ReviewItem
{
    internal ReviewItem(ApprovalRequest request) => Request = request;

    public ApprovalRequest Request { get; }

    public string Airport => Request.Airport;

    internal TaskCompletionSource<ApprovalDecision> Source { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// The changes waiting for the controller's review. The sync does not stop for them: it holds the airport that is waiting,
/// carries on with the others, and learns the answer when it comes. Nothing here shows anything: whoever listens to
/// <see cref="Changed"/> decides how the controller is told (a strip, a sound), so a change never takes the screen.
/// Safe to use from any thread.
/// </summary>
public sealed class ReviewQueue : IUpdateApprover
{
    private readonly object _gate = new();
    private readonly List<ReviewItem> _items = [];

    /// <summary>Raised, from whatever thread did it, whenever something is added or removed.</summary>
    public event Action? Changed;

    /// <summary>Raised for each new change that starts waiting.</summary>
    public event Action<ApprovalRequest>? Asked;

    /// <summary>What is waiting, oldest first.</summary>
    public IReadOnlyList<ReviewItem> Items
    {
        get { lock (_gate) return [.. _items]; }
    }

    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        var item = new ReviewItem(request);
        lock (_gate)
        {
            // An airport waits once: a newer question about it takes the place of the older one.
            foreach (var older in _items.Where(i => i.Airport == request.Airport).ToList())
            {
                _items.Remove(older);
                older.Source.TrySetCanceled();
            }
            _items.Add(item);
        }

        // The engine withdraws a question when the text moves on or the sync stops.
        ct.Register(() =>
        {
            if (Remove(item)) item.Source.TrySetCanceled(ct);
        });

        Asked?.Invoke(request);
        Changed?.Invoke();
        return item.Source.Task;
    }

    /// <summary>The controller's answer. Does nothing if the question has been withdrawn in the meantime.</summary>
    public void Decide(ReviewItem item, ApprovalDecision decision)
    {
        if (Remove(item)) item.Source.TrySetResult(decision);
    }

    private bool Remove(ReviewItem item)
    {
        bool removed;
        lock (_gate) removed = _items.Remove(item);
        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>
    /// What the strip says: the airport and its runway change when one is waiting, otherwise how many airports are.
    /// Empty when nothing is.
    /// </summary>
    public static string StripText(IReadOnlyList<ReviewItem> waiting) => waiting.Count switch
    {
        0 => "",
        1 => $"{waiting[0].Airport}: {waiting[0].Request.FlowSummary}",
        var n => $"{n} airports waiting review",
    };
}
