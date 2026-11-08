using System.Collections.ObjectModel;
using System.ComponentModel;

namespace VatisSync.Core;

public enum NoticeKind
{
    /// <summary>Something failed and needs the user's attention.</summary>
    Error,

    /// <summary>Something is off but the app carries on.</summary>
    Warning,

    /// <summary>A passing confirmation, such as "Added 5 airports". Shown briefly, then cleared by the UI.</summary>
    Info,
}

/// <summary>One message for the user, shown in the bar along the bottom of the window.</summary>
public sealed class Notice(string key, NoticeKind kind, string message) : INotifyPropertyChanged
{
    private string _message = message;

    /// <summary>Names the problem, so a later success can clear it and a repeat replaces it instead of stacking.</summary>
    public string Key { get; } = key;

    public NoticeKind Kind { get; } = kind;

    /// <summary>Can change while the notice is showing (a countdown), without the bar being rebuilt.</summary>
    public string Message
    {
        get => _message;
        internal set
        {
            if (_message == value) return;
            _message = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Message)));
        }
    }

    /// <summary>Set shortly before a self-clearing notice is removed, so the UI can fade it out.</summary>
    public bool IsFading
    {
        get => _isFading;
        set
        {
            if (_isFading == value) return;
            _isFading = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFading)));
        }
    }

    private bool _isFading;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// The messages currently shown. A message stays until the user dismisses it or the thing it reported is resolved.
/// Not thread-safe: use it from the UI thread, like the collection it exposes.
/// </summary>
public sealed class NoticeBoard
{
    public ObservableCollection<Notice> Items { get; } = [];

    /// <summary>
    /// Shows a message. One with the same key is replaced where it stands, so a repeating problem never stacks. If it is
    /// the same kind, the showing notice just takes the new text.
    /// </summary>
    public Notice Post(string key, NoticeKind kind, string message)
    {
        var index = Items.ToList().FindIndex(n => n.Key == key);
        if (index >= 0 && Items[index].Kind == kind)
        {
            Items[index].Message = message;
            return Items[index];
        }

        var notice = new Notice(key, kind, message);
        if (index >= 0) Items[index] = notice;
        else Items.Add(notice);
        return notice;
    }

    /// <summary>Changes the text of the message with this key, but only if it is still showing (the user may have closed it).</summary>
    public bool Update(string key, string message)
    {
        var found = Items.FirstOrDefault(n => n.Key == key);
        if (found is null) return false;
        found.Message = message;
        return true;
    }

    /// <summary>Clears the message with this key, because what it reported has been fixed. False if none was showing.</summary>
    public bool Resolve(string key)
    {
        var found = Items.FirstOrDefault(n => n.Key == key);
        return found is not null && Items.Remove(found);
    }

    /// <summary>The user closed it. False if it was already gone.</summary>
    public bool Dismiss(Notice notice) => Items.Remove(notice);
}
