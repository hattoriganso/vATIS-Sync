using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using VatisSync.Core;

namespace VatisSync;

/// <summary>Shows what a refresh would change on a connected ATIS and lets the controller decide.</summary>
public partial class ApprovalWindow : Window
{
    private bool _answered;

    public ApprovalDecision Decision { get; private set; } = ApprovalDecision.KeepCurrent;

    public ApprovalWindow(ApprovalRequest request)
    {
        InitializeComponent();
        Title = $"Approve ATIS change - {request.Station}";
        Headline.Text = request.FlowChanged
            ? $"{request.Station}: the runway configuration changed"
            : $"{request.Station}: the ATIS text changed";
        Summary.Text = request.Summary;
        StopAskingButton.Content = $"Apply, and stop asking for {request.Station}";

        var (oldWords, newWords) = WordDiff.Compare(request.CurrentConditions, request.NewConditions);
        Fill(CurrentText, oldWords);
        Fill(NewText, newWords);
    }

    private static void Fill(System.Windows.Controls.TextBlock target, IReadOnlyList<DiffWord> words)
    {
        target.Inlines.Clear();
        var highlight = new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x82));
        foreach (var w in words)
        {
            var run = new Run(w.Text + " ");
            if (w.Changed)
            {
                run.Background = highlight;
                run.Foreground = Brushes.Black;
                run.FontWeight = FontWeights.SemiBold;
            }
            target.Inlines.Add(run);
        }
    }

    private void Answer(ApprovalDecision decision)
    {
        _answered = true;
        Decision = decision;
        Close(); // shown non-modally (it must not block the app), so no DialogResult
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => Answer(ApprovalDecision.Apply);
    private void StopAsking_Click(object sender, RoutedEventArgs e) => Answer(ApprovalDecision.ApplyAndStopAsking);
    private void Keep_Click(object sender, RoutedEventArgs e) => Answer(ApprovalDecision.KeepCurrent);

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing the window with the X is the safe answer: leave the ATIS as it is.
        if (!_answered) Decision = ApprovalDecision.KeepCurrent;
        base.OnClosing(e);
    }
}
