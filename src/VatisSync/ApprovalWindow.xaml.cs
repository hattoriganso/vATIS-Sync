using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using VatisSync.Core;

namespace VatisSync;

/// <summary>Shows what a refresh would change on a connected ATIS and lets the controller decide.</summary>
public partial class ApprovalWindow : Window
{
    private bool _answered;

    public ApprovalDecision Decision { get; private set; } = ApprovalDecision.Disregard;

    public ApprovalWindow(ApprovalRequest request)
    {
        InitializeComponent();
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height - 24);
        MaxHeight = Math.Min(MaxHeight, SystemParameters.WorkArea.Height - 24);
        Title = $"Approve ATIS change - {request.Airport}";
        var several = request.Changes.Count > 1;

        // A question is only ever asked for a runway change. With one station the line under the headline says how; with
        // several, the detail goes above each station's own pair of texts instead.
        Headline.Text = $"{request.Airport}: runways changed";
        var only = request.Changes[0];
        Summary.Text = several ? "" : $"From {only.CurrentFlow.Describe()} to {only.NewFlow.Describe()}.";
        Summary.Visibility = several ? Visibility.Collapsed : Visibility.Visible;
        MuteButton.Content = $"Disregard {request.Airport} for this session";

        foreach (var change in request.Changes) Sections.Children.Add(BuildSection(change, several));
    }

    /// <summary>One station's pair of texts: what vATIS has now, and what the real-world ATIS would put there.</summary>
    private static UIElement BuildSection(StationChange change, bool several)
    {
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, several ? 14 : 0) };
        if (several)
        {
            var label = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            label.Inlines.Add(new Run(change.Station + ": "));
            label.Inlines.Add(new Run(change.FlowChanged
                ? $"from {change.CurrentFlow.Describe()} to {change.NewFlow.Describe()}"
                : "the ATIS text changed") { FontWeight = FontWeights.Normal });
            section.Children.Add(label);
        }

        var (oldWords, newWords) = WordDiff.Compare(change.CurrentConditions, change.NewConditions);
        var panes = new Grid();
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = Pane("Now in vATIS", oldWords, "ErrorText", "ErrorWash", scrolls: !several);
        var right = Pane("New (from the real-world D-ATIS)", newWords, "SuccessText", "SuccessWash", scrolls: !several);
        Grid.SetColumn(right, 2);
        panes.Children.Add(left);
        panes.Children.Add(right);
        section.Children.Add(panes);
        return section;
    }

    /// <summary>A titled box of text. A lone station's box scrolls by itself; with several, the whole window scrolls instead.</summary>
    private static UIElement Pane(string title, IReadOnlyList<DiffWord> words, string textKey, string washKey, bool scrolls)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Padding = new Thickness(10, 8, 10, 8),
            FontFamily = new FontFamily("Consolas"), FontSize = 12, LineHeight = 19,
        };
        text.SetResourceReference(TextElement.ForegroundProperty, "TextStrong");
        Fill(text, words, textKey, washKey);

        UIElement content = text;
        if (scrolls)
            content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 420, Content = text,
            };
        var box = new Border
        {
            CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), ClipToBounds = true, Child = content,
        };
        box.SetResourceReference(Border.BorderBrushProperty, "CodeBorder");
        box.SetResourceReference(Border.BackgroundProperty, "CodeBackground");

        var heading = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        var pane = new DockPanel();
        DockPanel.SetDock(heading, Dock.Top);
        pane.Children.Add(heading);
        pane.Children.Add(box);
        return pane;
    }

    /// <summary>
    /// Writes the words, with each run of changed words highlighted as one phrase (the spaces between them included, the one
    /// after the last left out so the highlight ends at the text). Colors come from the palette.
    /// </summary>
    private static void Fill(TextBlock target, IReadOnlyList<DiffWord> words, string textKey, string washKey)
    {
        target.Inlines.Clear();
        for (var i = 0; i < words.Count; i++)
        {
            if (!words[i].Changed)
            {
                target.Inlines.Add(new Run(words[i].Text + " "));
                continue;
            }

            var phrase = new List<string>();
            while (i < words.Count && words[i].Changed) phrase.Add(words[i++].Text);
            i--;
            var run = new Run(string.Join(" ", phrase)) { FontWeight = FontWeights.SemiBold };
            run.SetResourceReference(TextElement.ForegroundProperty, textKey);
            run.SetResourceReference(TextElement.BackgroundProperty, washKey);
            target.Inlines.Add(run);
            target.Inlines.Add(new Run(" "));
        }
    }

    private void Answer(ApprovalDecision decision)
    {
        _answered = true;
        Decision = decision;
        Close(); // shown non-modally (it must not block the app), so no DialogResult
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => Answer(ApprovalDecision.Apply);
    private void Mute_Click(object sender, RoutedEventArgs e) => Answer(ApprovalDecision.DisregardForSession);
    private void Disregard_Click(object sender, RoutedEventArgs e) => Answer(ApprovalDecision.Disregard);

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing the window with the X is the safe answer: leave the ATIS as it is.
        if (!_answered) Decision = ApprovalDecision.Disregard;
        base.OnClosing(e);
    }
}
