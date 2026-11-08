using System.Windows;
using VatisSync.Core;
using VatisSync.ViewModels;

namespace VatisSync.Services;

/// <summary>
/// Catches errors nothing else handled. Left alone, an uncaught error on the UI thread closes the app and, with it, any
/// ATIS syncing in progress. Instead the full details go to the error log and the user sees a message in the bar.
/// Errors that the runtime is going to terminate the app over anyway are still written to the log first.
/// </summary>
public sealed class SafetyNet(Application app, ErrorLog log, Func<MainViewModel?> viewModel)
{
    public void Install()
    {
        app.DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            Report(args.Exception, "UI thread");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            Report(args.Exception.GetBaseException(), "background task");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) log.Write(ex, "fatal, app closing");
        };
    }

    /// <summary>Writes the error to the log and tells the user. Never throws.</summary>
    public void Report(Exception error, string source)
    {
        try
        {
            var path = log.Write(error, source);
            viewModel()?.ReportUnexpectedError(error, path);
        }
        catch
        {
            // Reporting an error must never become another one.
        }
    }
}
