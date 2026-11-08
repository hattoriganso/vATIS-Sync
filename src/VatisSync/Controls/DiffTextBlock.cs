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

    private static readonly Brush RemovedText = Frozen(new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C)));
    private static readonly Brush RemovedBack = Frozen(new SolidColorBrush(Color.FromRgb(0xFD, 0xEC, 0xEC)));
    private static readonly Brush AddedText = Frozen(new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D)));
    private static readonly Brush AddedBack = Frozen(new SolidColorBrush(Color.FromRgb(0xE7, 0xF5, 0xEC)));

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
            Inlines.Add(new Run(Messages.Get(Msg.SummaryNothing)) { Foreground = SystemColors.GrayTextBrush });
            return;
        }

        foreach (var segment in shown)
        {
            var run = new Run(segment.Text + " ");
            if (ShowChanges && segment.Kind == DiffKind.Removed)
            {
                run.Foreground = RemovedText;
                run.Background = RemovedBack;
                run.TextDecorations = System.Windows.TextDecorations.Strikethrough;
            }
            else if (ShowChanges && segment.Kind == DiffKind.Added)
            {
                run.Foreground = AddedText;
                run.Background = AddedBack;
                run.FontWeight = FontWeights.SemiBold;
            }
            Inlines.Add(run);
        }
    }

    private static Brush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
