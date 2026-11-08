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
        Fill(CurrentText, oldWords, "ErrorText", "ErrorWash");
        Fill(NewText, newWords, "SuccessText", "SuccessWash");
    }

    /// <summary>
    /// Writes the words, with each run of changed words highlighted as one phrase (the spaces between them included, the one
    /// after the last left out so the highlight ends at the text). Colors come from the palette.
    /// </summary>
    private static void Fill(System.Windows.Controls.TextBlock target, IReadOnlyList<DiffWord> words, string textKey, string washKey)
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
    private void StopAsking_Click(object sender, RoutedEventArgs e) => Answer(ApprovalDecision.ApplyAndStopAsking);
    private void Keep_Click(object sender, RoutedEventArgs e) => Answer(ApprovalDecision.KeepCurrent);

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing the window with the X is the safe answer: leave the ATIS as it is.
        if (!_answered) Decision = ApprovalDecision.KeepCurrent;
        base.OnClosing(e);
    }
}
