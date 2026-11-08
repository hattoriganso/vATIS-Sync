using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using VatisSync.Core;

namespace VatisSync.Controls;

/// <summary>
/// Shows D-ATIS text as a list of <see cref="DiffSegment"/>s. With <see cref="ShowChanges"/> off it reads as the plain text
/// for vATIS (words that were removed are left out). With it on, removed words stay in, struck through in red, and words that
/// were put in are highlighted green.
/// </summary>
public class DiffTextBlock : TextBlock
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<DiffSegment>), typeof(DiffTextBlock),
        new PropertyMetadata(null, (d, _) => ((DiffTextBlock)d).Rebuild()));

    public static readonly DependencyProperty ShowChangesProperty = DependencyProperty.Register(
        nameof(ShowChanges), typeof(bool), typeof(DiffTextBlock),
        new PropertyMetadata(false, (d, _) => ((DiffTextBlock)d).Rebuild()));

    public IReadOnlyList<DiffSegment>? Segments
    {
        get => (IReadOnlyList<DiffSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public bool ShowChanges
    {
        get => (bool)GetValue(ShowChangesProperty);
        set => SetValue(ShowChangesProperty, value);
    }

    private void Rebuild()
    {
        Inlines.Clear();
        var segments = Segments;
        var shown = segments?.Where(s => ShowChanges || s.Kind != DiffKind.Removed).ToList() ?? [];
        if (shown.Count == 0)
        {
            var empty = new Run(Messages.Get(Msg.SummaryNothing));
            empty.SetResourceReference(TextElement.ForegroundProperty, "TextSecondary");
            Inlines.Add(empty);
            return;
        }

        foreach (var segment in shown)
        {
            var run = new Run(segment.Text + " ");
            if (ShowChanges && segment.Kind == DiffKind.Removed)
            {
                run.SetResourceReference(TextElement.ForegroundProperty, "ErrorText");
                run.SetResourceReference(TextElement.BackgroundProperty, "ErrorWash");
                run.TextDecorations = System.Windows.TextDecorations.Strikethrough;
            }
            else if (ShowChanges && segment.Kind == DiffKind.Added)
            {
                run.SetResourceReference(TextElement.ForegroundProperty, "SuccessText");
                run.SetResourceReference(TextElement.BackgroundProperty, "SuccessWash");
                run.FontWeight = FontWeights.SemiBold;
            }
            Inlines.Add(run);
        }
    }

}
