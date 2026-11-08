using System.Windows;
using VatisSync.Core;

namespace VatisSync.Services;

/// <summary>Shows an <see cref="ApprovalWindow"/> on the UI thread and waits for the controller's answer.</summary>
public sealed class WpfUpdateApprover : IUpdateApprover
{
    /// <summary>Called when a question is shown, so the app can surface itself (for example a tray notification).</summary>
    public Action<ApprovalRequest>? Asked { get; set; }

    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Application.Current.Dispatcher;

        dispatcher.BeginInvoke(() =>
        {
            ApprovalWindow window;
            try { window = new ApprovalWindow(request); }
            catch (Exception ex) { tcs.TrySetException(ex); return; }

            // If the sync is stopped while the question is open, close it.
            var registration = ct.Register(() => dispatcher.BeginInvoke(() =>
            {
                tcs.TrySetCanceled(ct);
                if (window.IsLoaded) window.Close();
            }));

            window.Closed += (_, _) =>
            {
                registration.Dispose();
                tcs.TrySetResult(window.Decision);
            };

            Asked?.Invoke(request);
            window.Show();
            window.Activate();
        });

        return tcs.Task;
    }
}
