using System.Diagnostics;
using System.Runtime.InteropServices;
using LaunchQueue;

namespace LaunchQueue.Tests;

internal sealed class TestReport
{
    public string Status { get; set; } = "running";
    public string RunId { get; set; } = "";
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedUtc { get; set; }
    public string Runtime { get; set; } = RuntimeInformation.FrameworkDescription;
    public string OperatingSystem { get; set; } = RuntimeInformation.OSDescription;
    public string ExecutablePath { get; set; } = Environment.ProcessPath ?? "";
    public bool UserInteractive { get; set; } = Environment.UserInteractive;
    public string ReportPath { get; set; } = "";
    public string ArtifactsDirectory { get; set; } = "";
    public string? RetainedTemporaryDirectory { get; set; }
    public List<ScreenData> Screens { get; set; } = [];
    public List<TestCaseResult> Tests { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public int Passed => Tests.Count(x => x.Status == "passed");
    public int Failed => Tests.Count(x => x.Status == "failed");
    public int Skipped => Tests.Count(x => x.Status == "skipped");
}

internal sealed class TestCaseResult
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "running";
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public double DurationMilliseconds { get; set; }
    public List<string> Errors { get; set; } = [];
    public List<string> Notes { get; set; } = [];
    public Dictionary<string, object?> Evidence { get; set; } = [];
    public List<string> ProbeFiles { get; set; } = [];
    public List<TimedMessage> Messages { get; set; } = [];
}

internal sealed record TimedMessage(DateTimeOffset Utc, double ElapsedMilliseconds, RunMessage Message);

internal sealed class ProbeSpec(string outputPath, string title, QueueItem item, ProbeMode mode)
{
    internal string OutputPath { get; } = outputPath;
    internal string Title { get; } = title;
    internal QueueItem Item { get; } = item;
    internal ProbeMode Mode { get; } = mode;
    internal bool LaunchAttempted { get; set; }
}

internal sealed class SkipTestException(string message) : Exception(message) { }

internal static class Check
{
    internal static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Equal<T>(T expected, T actual, string message) =>
        True(EqualityComparer<T>.Default.Equals(expected, actual),
            $"{message}\nExpected: {expected}\nActual: {actual}");

    internal static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"{message}: expected {typeof(T).Name}.");
    }
}

internal static class TestHarness
{
    internal static async Task<int> RunAsync(string reportPath)
    {
        var runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "LaunchQueue.Tests", runId);
        var report = new TestReport
        {
            RunId = runId,
            ReportPath = reportPath,
            ArtifactsDirectory = Path.Combine(Path.GetDirectoryName(reportPath)!,
                Path.GetFileNameWithoutExtension(reportPath) + ".artifacts", runId),
            Screens = Screen.AllScreens.Select(ScreenData.From).ToList()
        };
        Directory.CreateDirectory(temporaryRoot);
        var cleanupFailed = false;
        try
        {
            JsonFiles.Write(reportPath, report);
            foreach (var (name, body) in TestChecks.All())
            {
                var result = new TestCaseResult { Name = name };
                report.Tests.Add(result);
                var timer = Stopwatch.StartNew();
                var context = new TestContext(temporaryRoot, runId, result);
                try
                {
                    await body(context);
                    result.Status = "passed";
                }
                catch (SkipTestException exception)
                {
                    result.Status = "skipped";
                    result.Notes.Add(exception.Message);
                }
                catch (Exception exception)
                {
                    result.Status = "failed";
                    result.Errors.Add(exception.ToString());
                }
                finally
                {
                    try { await context.CloseProbesAsync(); }
                    catch (Exception exception)
                    {
                        cleanupFailed = true;
                        result.Status = "failed";
                        result.Errors.Add("Probe cleanup: " + exception);
                    }
                    result.DurationMilliseconds = timer.Elapsed.TotalMilliseconds;
                }
                JsonFiles.Write(reportPath, report);
            }
        }
        catch (Exception exception)
        {
            report.Errors.Add(exception.ToString());
        }
        finally
        {
            try
            {
                // Preserve probe evidence before removing this run's private temporary files.
                Directory.CreateDirectory(report.ArtifactsDirectory);
                foreach (var source in Directory.EnumerateFiles(temporaryRoot, "*", SearchOption.AllDirectories))
                {
                    var destination = Path.Combine(report.ArtifactsDirectory, Path.GetRelativePath(temporaryRoot, source));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination, overwrite: false);
                }
                if (cleanupFailed) report.RetainedTemporaryDirectory = temporaryRoot;
                else Directory.Delete(temporaryRoot, recursive: true);
            }
            catch (Exception exception)
            {
                report.RetainedTemporaryDirectory = temporaryRoot;
                report.Errors.Add("Artifact preservation: " + exception);
            }
            report.FinishedUtc = DateTimeOffset.UtcNow;
            report.Status = report.Failed == 0 && report.Errors.Count == 0 ? "passed" : "failed";
            JsonFiles.Write(reportPath, report);
        }
        return report.Status == "passed" ? 0 : 1;
    }
}

internal sealed class TestContext
{
    private readonly string temporaryRoot;
    private readonly string runId;
    private readonly List<ProbeSpec> probes = [];
    private int queueNumber;
    internal TestCaseResult Result { get; }
    internal string DirectoryPath { get; }

    internal TestContext(string temporaryRoot, string runId, TestCaseResult result)
    {
        this.temporaryRoot = temporaryRoot;
        this.runId = runId;
        Result = result;
        DirectoryPath = Path.Combine(temporaryRoot, result.Name);
        Directory.CreateDirectory(DirectoryPath);
    }

    internal string FilePath(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(DirectoryPath, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    internal ProbeSpec NewProbe(string name, string? titleSuffix = null, ProbeMode mode = ProbeMode.Normal)
    {
        Check.True(Environment.UserInteractive, "Integration tests require an interactive Windows desktop.");
        var executable = Environment.ProcessPath;
        Check.True(executable is not null && File.Exists(executable) &&
            Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase),
            "Run LaunchQueue.Tests.exe (the apphost), not dotnet LaunchQueue.Tests.dll.");
        var path = FilePath(name + ".probe.json");
        var title = $"LaunchQueue test {runId} {Result.Name} {name} {titleSuffix ?? "Проба"}";
        var work = Path.Combine(DirectoryPath, "Рабочая папка");
        Directory.CreateDirectory(work);
        var item = new QueueItem
        {
            Name = name,
            FilePath = executable!,
            Arguments = "--probe " + ShellLinks.QuoteArgument(path) + " " + ShellLinks.QuoteArgument(title) +
                (mode == ProbeMode.Normal ? "" : " " + mode),
            WorkingDirectory = work
        };
        var probe = new ProbeSpec(path, title, item, mode);
        probes.Add(probe);
        Result.ProbeFiles.Add(Path.GetRelativePath(temporaryRoot, path));
        return probe;
    }

    internal async Task<RunResult> RunQueueAsync(QueueProfile profile,
        CancellationToken cancellation = default, Action<RunMessage>? onMessage = null, TimeSpan? deadlineOverride = null)
    {
        var number = ++queueNumber;
        var profilePath = FilePath($"profile-{number}.json");
        ProfileStore.Save(profilePath, profile);
        var loaded = ProfileStore.Load(profilePath);
        var timer = Stopwatch.StartNew();
        var deadlineLimit = deadlineOverride ?? TimeSpan.FromSeconds(7);
        using var deadline = new CancellationTokenSource(deadlineLimit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        var runner = new QueueRunner(message =>
        {
            Result.Messages.Add(new TimedMessage(DateTimeOffset.UtcNow, timer.Elapsed.TotalMilliseconds, message));
            if (message.Status == "Запуск…" && message.ItemIndex >= 0 && message.ItemIndex < loaded.Items.Count)
            {
                var item = loaded.Items[message.ItemIndex];
                foreach (var probe in probes.Where(x => x.Item.Arguments == item.Arguments))
                    probe.LaunchAttempted = true;
            }
            onMessage?.Invoke(message);
        });
        RunResult? result = null;
        try
        {
            result = await runner.RunAsync(loaded, linked.Token);
            Result.Evidence["RunResult"] = result;
            Check.True(!deadline.IsCancellationRequested || cancellation.IsCancellationRequested,
                $"Queue exceeded the {deadlineLimit.TotalSeconds:g}-second test deadline; it was cancelled for cleanup.");
            return result;
        }
        finally
        {
            JsonFiles.Write(FilePath($"queue-{number}.json"), new
            {
                Profile = Path.GetRelativePath(temporaryRoot, profilePath),
                Result = result,
                Messages = Result.Messages
            });
        }
    }

    internal async Task<ProbeLog> WaitForProbeAsync(ProbeSpec probe,
        Func<ProbeLog, bool>? predicate = null, TimeSpan? timeout = null)
    {
        ProbeLog? latest = null;
        await WaitUntilAsync(() =>
        {
            latest = JsonFiles.TryRead<ProbeLog>(probe.OutputPath);
            if (latest is null) return false;
            Check.Equal(probe.Title, latest.Title, "Probe title/argument roundtrip.");
            Check.Equal(probe.Mode, latest.Mode, "Probe mode argument roundtrip.");
            Check.True(latest.WriteError is null, "Probe snapshots must remain writable.");
            Check.True(string.Equals(Environment.ProcessPath, latest.ExecutablePath, StringComparison.OrdinalIgnoreCase),
                "Probe must use this test executable.");
            Check.Equal(probe.Item.WorkingDirectory, latest.WorkingDirectory, "Probe working directory.");
            return predicate?.Invoke(latest) ?? latest.Samples.Count > 0;
        }, timeout ?? TimeSpan.FromSeconds(4),
            $"No matching probe snapshot for {probe.Item.Name}. See {Path.GetFileName(probe.OutputPath)}.");
        return latest!;
    }

    internal static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failure)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed >= timeout) throw new TimeoutException(failure);
            await Task.Delay(50);
        }
    }

    internal void AssertNotLaunched(ProbeSpec probe)
    {
        Check.True(!probe.LaunchAttempted, $"{probe.Item.Name} must not receive a launch command.");
        Check.True(!File.Exists(probe.OutputPath), $"{probe.Item.Name} unexpectedly wrote a probe log.");
    }

    internal async Task CloseProbesAsync()
    {
        var errors = new List<Exception>();
        foreach (var probe in probes)
        {
            try
            {
                var log = JsonFiles.TryRead<ProbeLog>(probe.OutputPath);
                if (log is null && probe.LaunchAttempted)
                    log = await WaitForProbeAsync(probe, _ => true, TimeSpan.FromSeconds(2));
                if (log is null) continue;
                Check.Equal(probe.Title, log.Title, "Cleanup ownership token.");
                Check.True(log.ProcessId != Environment.ProcessId, "Never close the harness itself.");
                Process process;
                try { process = Process.GetProcessById(log.ProcessId); }
                catch (ArgumentException) { continue; } // Probe already auto-closed.
                using (process)
                {
                    if (process.HasExited) continue;
                    var actualStart = new DateTimeOffset(process.StartTime.ToUniversalTime());
                    // A PID may have been reused: do not act on a different process.
                    if (Math.Abs((actualStart - log.ProcessStartUtc).TotalMilliseconds) > 1) continue;
                    Check.True(string.Equals(process.MainModule?.FileName, Environment.ProcessPath,
                        StringComparison.OrdinalIgnoreCase), "Cleanup executable identity mismatch.");
                    var window = (nint)log.WindowHandle;
                    ProbeWindows.GetWindowThreadProcessId(window, out var windowProcessId);
                    if (window != 0 && windowProcessId == (uint)process.Id)
                    {
                        // This private message also closes our verified probes when hidden or ignoring WM_CLOSE.
                        ProbeWindows.PostMessageW(window, ProbeWindows.CleanupMessage, 0, 0);
                    }
                    try
                    {
                        // No process killing: allow the probe's own bounded auto-close as a fallback.
                        var seconds = log.Mode == ProbeMode.Normal ? 10 : 13;
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(seconds));
                    }
                    catch (TimeoutException)
                    {
                        throw new TimeoutException($"Owned probe {log.ProcessId} did not exit after graceful cleanup and its auto-close deadline.");
                    }
                    Result.Notes.Add($"Closed owned probe {probe.Item.Name} (PID {log.ProcessId}).");
                }
            }
            catch (Exception exception) { errors.Add(exception); }
        }
        if (errors.Count > 0) throw new AggregateException(errors);
    }
}
