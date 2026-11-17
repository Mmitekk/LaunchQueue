namespace LaunchQueue.Tests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var launchUtc = DateTimeOffset.UtcNow;
        try
        {
            ApplicationConfiguration.Initialize();
            if (args.Length is 3 or 4 && args[0] == "--probe")
            {
                var mode = args.Length == 3 ? ProbeMode.Normal : Enum.Parse<ProbeMode>(args[3], ignoreCase: true);
                if (!Enum.IsDefined(mode)) return 1;
                using var probe = new ProbeForm(Path.GetFullPath(args[1]), args[2], launchUtc, mode);
                Application.Run(probe);
                return probe.Log.WriteError is null ? 0 : 1;
            }

            if (args is ["--test", var report])
            {
                using var host = new TestHost(Path.GetFullPath(report));
                Application.Run(host);
                return host.ExitCode;
            }
        }
        catch (Exception exception)
        {
            if (args is ["--test", var report])
            {
                try
                {
                    JsonFiles.Write(Path.GetFullPath(report), new
                    {
                        Status = "failed",
                        StartedUtc = launchUtc,
                        FinishedUtc = DateTimeOffset.UtcNow,
                        FatalError = exception.ToString()
                    });
                }
                catch { /* An unwritable report destination is still a failing exit. */ }
            }
        }
        return 1;
    }
}

internal sealed class TestHost : Form
{
    private readonly string reportPath;
    public int ExitCode { get; private set; } = 1;

    public TestHost(string reportPath)
    {
        this.reportPath = reportPath;
        ShowInTaskbar = false;
        Opacity = 0;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Size = new Size(1, 1);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Hide();
        try
        {
            // Keep the STA synchronization context alive during asynchronous window checks.
            ExitCode = await TestHarness.RunAsync(reportPath);
        }
        catch (Exception exception)
        {
            try
            {
                JsonFiles.Write(reportPath, new
                {
                    Status = "failed",
                    FinishedUtc = DateTimeOffset.UtcNow,
                    FatalError = exception.ToString()
                });
            }
            catch { }
            ExitCode = 1;
        }
        finally
        {
            Close();
        }
    }
}
