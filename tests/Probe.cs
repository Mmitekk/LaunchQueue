using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LaunchQueue.Tests;

internal static class JsonFiles
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".writing";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, Options));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    internal static T? TryRead<T>(string path) where T : class
    {
        try
        {
            // Do not block a probe's atomic replacement of its current snapshot.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }
}

internal sealed record RectangleData(int X, int Y, int Width, int Height)
{
    internal static RectangleData From(Rectangle value) =>
        new(value.X, value.Y, value.Width, value.Height);
}

internal sealed record ScreenData(string DeviceName, bool Primary,
    RectangleData Bounds, RectangleData WorkingArea)
{
    internal static ScreenData From(Screen screen) => new(screen.DeviceName, screen.Primary,
        RectangleData.From(screen.Bounds), RectangleData.From(screen.WorkingArea));
}

internal sealed record ProbeSample(DateTimeOffset Utc, double ElapsedMilliseconds,
    string Screen, RectangleData Bounds, RectangleData ScreenBounds,
    RectangleData WorkingArea, FormWindowState WindowState, bool Visible);

internal enum ProbeMode { Normal, HideOnClose, IgnoreClose }

internal static class ProbeWindows
{
    internal const int CloseMessage = 0x0010;
    internal const int CleanupMessage = 0x8000 + 73;

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
}

internal sealed class ProbeLog
{
    public int ProcessId { get; set; }
    public string ExecutablePath { get; set; } = "";
    public string Title { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public DateTimeOffset LaunchUtc { get; set; }
    public DateTimeOffset ProcessStartUtc { get; set; }
    public DateTimeOffset? ClosedUtc { get; set; }
    public ProbeMode Mode { get; set; }
    public long WindowHandle { get; set; }
    public List<DateTimeOffset> CloseRequests { get; set; } = [];
    public DateTimeOffset? HiddenUtc { get; set; }
    public DateTimeOffset? ForcedCloseUtc { get; set; }
    public string? ForcedCloseReason { get; set; }
    public string? WriteError { get; set; }
    public List<ProbeSample> Samples { get; set; } = [];
}

internal sealed class ProbeForm : Form
{
    private readonly string outputPath;
    private readonly System.Windows.Forms.Timer sampleTimer = new() { Interval = 100 };
    private readonly Stopwatch lifetime = new();
    private bool forceClose;
    internal ProbeLog Log { get; }

    internal ProbeForm(string outputPath, string title, DateTimeOffset launchUtc, ProbeMode mode = ProbeMode.Normal)
    {
        this.outputPath = outputPath;
        using var process = Process.GetCurrentProcess();
        Log = new ProbeLog
        {
            ProcessId = process.Id,
            ExecutablePath = Environment.ProcessPath!,
            Title = title,
            WorkingDirectory = Environment.CurrentDirectory,
            LaunchUtc = launchUtc,
            ProcessStartUtc = new DateTimeOffset(process.StartTime.ToUniversalTime()),
            Mode = mode
        };
        // Publish ownership before the window is shown, including on slow UI startup.
        JsonFiles.Write(outputPath, Log);

        Text = title;
        StartPosition = FormStartPosition.Manual;
        var work = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        Bounds = new Rectangle(work.Left + 24, work.Top + 24,
            Math.Min(420, work.Width - 48), Math.Min(240, work.Height - 48));
        var autoCloseSeconds = mode == ProbeMode.Normal ? 8 : 12;
        Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = $"LaunchQueue automated test probe ({mode})\r\nCloses automatically after {autoCloseSeconds} seconds."
        });
        sampleTimer.Tick += (_, _) =>
        {
            RecordSample();
            if (lifetime.Elapsed >= TimeSpan.FromSeconds(autoCloseSeconds)) ForceClose("auto-timer");
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        lifetime.Start();
        RecordSample();
        sampleTimer.Start();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == ProbeWindows.CleanupMessage)
        {
            ForceClose("verified-harness-cleanup");
            return;
        }
        if (message.Msg == ProbeWindows.CloseMessage && !forceClose)
        {
            Log.CloseRequests.Add(DateTimeOffset.UtcNow);
            Persist();
        }
        base.WndProc(ref message);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        // WinForms classifies a directly posted WM_CLOSE as TaskManagerClosing.
        if (forceClose || Log.Mode == ProbeMode.Normal ||
            e.CloseReason is not (CloseReason.UserClosing or CloseReason.TaskManagerClosing)) return;
        e.Cancel = true;
        if (Log.Mode == ProbeMode.HideOnClose)
        {
            Hide();
            Log.HiddenUtc ??= DateTimeOffset.UtcNow;
        }
        // A hidden main form must keep its message loop and sample timer alive.
        RecordSample();
    }

    private void ForceClose(string reason)
    {
        forceClose = true;
        Log.ForcedCloseUtc = DateTimeOffset.UtcNow;
        Log.ForcedCloseReason = reason;
        Close();
    }

    private void RecordSample()
    {
        Log.WindowHandle = Handle.ToInt64();
        var screen = Screen.FromHandle(Handle);
        Log.Samples.Add(new ProbeSample(DateTimeOffset.UtcNow, lifetime.Elapsed.TotalMilliseconds,
            screen.DeviceName, RectangleData.From(Bounds), RectangleData.From(screen.Bounds),
            RectangleData.From(screen.WorkingArea), WindowState, Visible));
        Persist();
    }

    private void Persist()
    {
        try
        {
            Log.WriteError = null;
            JsonFiles.Write(outputPath, Log);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.WriteError = exception.Message;
            // Keep the auto-close timer running even if a snapshot cannot be saved.
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        sampleTimer.Stop();
        Log.ClosedUtc = DateTimeOffset.UtcNow;
        Persist();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) sampleTimer.Dispose();
        base.Dispose(disposing);
    }
}
