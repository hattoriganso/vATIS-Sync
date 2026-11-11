using System.Media;
using System.Windows;
using System.Windows.Threading;
using VatisSync.Core;

namespace VatisSync.Services;

/// <summary>
/// How the controller is told that a runway change is waiting, and how they answer. A change never opens a window by
/// itself: a small <see cref="ReviewStrip"/> appears in a corner (with a soft sound, if they asked for one), and the
/// comparison window opens only when they click Review. Later hides the strip for a few minutes; if something is still
/// waiting it comes back. Must be created and used on the UI thread.
/// </summary>
public sealed class ReviewPrompt
{
    /// <summary>How long Later hides the strip before it reminds the controller.</summary>
    public static readonly TimeSpan ReminderAfter = TimeSpan.FromMinutes(3);

    private readonly ReviewQueue _queue;
    private readonly Func<bool> _playSound;
    private readonly Func<ScreenCorner> _corner;
    private readonly Func<string> _screen;
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly Dictionary<ReviewItem, ApprovalWindow> _windows = [];
    private readonly DispatcherTimer _reminder = new() { Interval = ReminderAfter };
    private ReviewStrip? _strip;
    private bool _snoozed;

    public ReviewPrompt(ReviewQueue queue, Func<bool> playSound, Func<ScreenCorner> corner, Func<string> screen)
    {
        _screen = screen;
        _queue = queue;
        _playSound = playSound;
        _corner = corner;
        _reminder.Tick += (_, _) =>
        {
            _reminder.Stop();
            _snoozed = false;
            Refresh();
        };
        queue.Changed += () => _ui.BeginInvoke(Refresh);
    }

    /// <summary>True when something is waiting, for the tray menu.</summary>
    public bool HasWaiting => _queue.Items.Count > 0;

    /// <summary>Opens the review window for the airport (or brings it forward if it is open).</summary>
    public void Review(string airport)
    {
        if (_queue.Items.FirstOrDefault(i => i.Airport == airport) is { } item) Open(item);
    }

    /// <summary>Opens the review window for the airport that has waited longest (the strip's Review, and the tray's).</summary>
    public void ReviewNext()
    {
        var items = _queue.Items;
        if (items.FirstOrDefault(i => !_windows.ContainsKey(i)) is { } next) Open(next);
        else if (items.Count > 0) Open(items[0]);
    }

    private void Open(ReviewItem item)
    {
        if (_windows.TryGetValue(item, out var open))
        {
            open.Activate();
            return;
        }

        // Opened because the controller asked for it, so it may come to the front. Enter does nothing in it, and Esc is Disregard.
        var window = new ApprovalWindow(item.Request);
        _windows[item] = window;
        window.Closed += (_, _) =>
        {
            // Withdrawn while open (the text moved on): the window was already forgotten and the answer is moot.
            if (!_windows.TryGetValue(item, out var current) || current != window) return;
            _windows.Remove(item);
            _queue.Decide(item, window.Decision);
        };
        window.Show();
        window.Activate();
        Refresh();
    }

    private void Refresh()
    {
        var items = _queue.Items;

        // A question withdrawn while its window is open (the real-world text moved on, or the sync stopped) closes with it,
        // rather than leaving the controller to answer something that no longer applies.
        foreach (var gone in _windows.Keys.Where(k => !items.Contains(k)).ToList())
        {
            var window = _windows[gone];
            _windows.Remove(gone);
            window.Close();
        }

        if (items.Count == 0)
        {
            _snoozed = false;
            _reminder.Stop();
        }

        // The strip is for changes nobody is looking at yet.
        var waiting = items.Where(i => !_windows.ContainsKey(i)).ToList();
        if (waiting.Count == 0 || _snoozed)
        {
            HideStrip();
            return;
        }

        var text = ReviewQueue.StripText(waiting);
        if (_strip is null)
        {
            _strip = new ReviewStrip { DetailText = text, Corner = _corner(), ScreenName = _screen() };
            _strip.ReviewClicked += ReviewNext;
            _strip.LaterClicked += Later;
            _strip.Show();
            if (_playSound()) SystemSounds.Asterisk.Play();
        }
        else _strip.DetailText = text;
    }

    private void Later()
    {
        _snoozed = true;
        _reminder.Stop();
        _reminder.Start();
        HideStrip();
    }

    private void HideStrip()
    {
        if (_strip is null) return;
        var strip = _strip;
        _strip = null;
        strip.Close();
    }
}
