using System.Windows.Forms;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>How install and uninstall talk to the user: a terminal, or dialogs when started from Explorer or Settings.</summary>
internal interface IInstallUi
{
    /// <summary>Asks before changing the PC. A terminal does not ask: typing the command was the request.</summary>
    bool Confirm(string heading, string text, string action);

    /// <summary>Runs <paramref name="work"/> while showing its progress lines.</summary>
    Task<T> RunAsync<T>(string heading, Func<IProgress<string>, Task<T>> work);

    void Report(string heading, string text, bool succeeded);
}

internal sealed class ConsoleInstallUi : IInstallUi
{
    public bool Confirm(string heading, string text, string action) => true;

    public async Task<T> RunAsync<T>(string heading, Func<IProgress<string>, Task<T>> work)
    {
        Console.WriteLine();
        Console.WriteLine(heading);
        return await work(new SynchronousProgress(line => Console.WriteLine($"  {line}")));
    }

    public void Report(string heading, string text, bool succeeded)
    {
        var writer = succeeded ? Console.Out : Console.Error;
        writer.WriteLine(heading);
        if (text.Length > 0)
        {
            writer.WriteLine(text);
        }
    }

    /// <summary>Reports on the calling thread, so lines print in order.</summary>
    private sealed class SynchronousProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}

/// <summary>Task dialogs, each shown on its own STA thread.</summary>
internal sealed class DialogInstallUi : IInstallUi
{
    private const string Caption = "HyperHarbor";

    public bool Confirm(string heading, string text, string action) => OnStaThread(() =>
    {
        var accept = new TaskDialogButton(action) { ShowShieldIcon = true };
        var page = new TaskDialogPage
        {
            Caption = Caption,
            Heading = heading,
            Text = text,
            Icon = TaskDialogIcon.Information,
            Buttons = { accept, TaskDialogButton.Cancel },
            DefaultButton = accept,
        };
        return TaskDialog.ShowDialog(page) == accept;
    });

    public Task<T> RunAsync<T>(string heading, Func<IProgress<string>, Task<T>> work) => Task.FromResult(OnStaThread(() =>
    {
        var page = new TaskDialogPage
        {
            Caption = Caption,
            Heading = heading,
            Text = "Starting…",
            ProgressBar = new TaskDialogProgressBar(TaskDialogProgressBarState.Marquee),
            AllowCancel = false,
            Buttons = { new TaskDialogButton("Close") { Enabled = false } },
        };

        Task<T>? running = null;
        page.Created += (_, _) =>
        {
            // Created on the dialog's thread, so the progress callbacks update the page there.
            var progress = new Progress<string>(line => page.Text = line);
            running = Task.Run(() => work(progress));
            running.ContinueWith(_ => page.BoundDialog?.Close(), TaskScheduler.FromCurrentSynchronizationContext());
        };
        TaskDialog.ShowDialog(page);
        return running!.GetAwaiter().GetResult();
    }));

    public void Report(string heading, string text, bool succeeded) => OnStaThread(() =>
        TaskDialog.ShowDialog(new TaskDialogPage
        {
            Caption = Caption,
            Heading = heading,
            Text = text,
            Icon = succeeded ? TaskDialogIcon.ShieldSuccessGreenBar : TaskDialogIcon.Error,
            Buttons = { TaskDialogButton.OK },
        }));

    private static T OnStaThread<T>(Func<T> show)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                result = show();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result;
    }
}
