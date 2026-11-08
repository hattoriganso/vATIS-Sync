using System.Collections.ObjectModel;

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
public sealed class Notice(string key, NoticeKind kind, string message)
{
    /// <summary>Names the problem, so a later success can clear it and a repeat replaces it instead of stacking.</summary>
    public string Key { get; } = key;

    public NoticeKind Kind { get; } = kind;
    public string Message { get; } = message;
}

/// <summary>
/// The messages currently shown. A message stays until the user dismisses it or the thing it reported is resolved.
/// Not thread-safe: use it from the UI thread, like the collection it exposes.
/// </summary>
public sealed class NoticeBoard
{
    public ObservableCollection<Notice> Items { get; } = [];

    /// <summary>Shows a message. One with the same key is replaced where it stands, so a repeating problem never stacks.</summary>
    public Notice Post(string key, NoticeKind kind, string message)
    {
        var notice = new Notice(key, kind, message);
        var existing = Items.ToList().FindIndex(n => n.Key == key);
        if (existing >= 0) Items[existing] = notice;
        else Items.Add(notice);
        return notice;
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
