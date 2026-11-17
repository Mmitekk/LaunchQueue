using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using LaunchQueue;

namespace LaunchQueue.Tests;

internal static class TestChecks
{
    internal static IEnumerable<(string Name, Func<TestContext, Task> Body)> All()
    {
        yield return ("configuration-roundtrip", ConfigurationRoundtripAsync);
        yield return ("configuration-invalid", InvalidConfigurationAsync);
        yield return ("configuration-relative-paths", RelativePathsAsync);
        yield return ("quote-argument", QuoteArgumentAsync);
        yield return ("sequence-default-disabled", DefaultSequenceAsync);
        yield return ("sequence-delay-override", OverrideSequenceAsync);
        yield return ("disabled-only", DisabledOnlyAsync);
        yield return ("monitor-DISPLAY1", context => MonitorAsync(context, @"\\.\DISPLAY1"));
        yield return ("monitor-DISPLAY2", context => MonitorAsync(context, @"\\.\DISPLAY2"));
        yield return ("window-maximized", MaximizedAsync);
        yield return ("window-minimized", MinimizedAsync);
        yield return ("window-timeout-does-not-block", WindowTimeoutAsync);
        yield return ("missing-file-continue", context => MissingFileAsync(context, true));
        yield return ("missing-file-stop", context => MissingFileAsync(context, false));
        yield return ("stop-during-delay", CancellationAsync);
        yield return ("disconnected-monitor-fallback", DisconnectedMonitorAsync);
        yield return ("close-configuration-defaults-copy", CloseConfigurationAsync);
        yield return ("close-last-window", context => CloseLastAsync(context, 0.35m));
        yield return ("close-zero-delay", context => CloseLastAsync(context, 0));
        yield return ("close-tray-hidden-alive", CloseTrayAsync);
        yield return ("close-disabled-visible", CloseDisabledAsync);
        yield return ("close-next-default-interval", context => CloseIntervalAsync(context, false));
        yield return ("close-next-override-interval", context => CloseIntervalAsync(context, true));
        yield return ("close-cancel-countdown", CloseCancellationAsync);
        yield return ("close-ignored-warning", CloseIgnoredAsync);
        yield return ("close-missing-window", CloseMissingWindowAsync);
        yield return ("close-placement-warning", ClosePlacementWarningAsync);
    }

    private static Task ConfigurationRoundtripAsync(TestContext context)
    {
        var path = context.FilePath("Очередь с кириллицей.json");
        var profile = new QueueProfile
        {
            Name = "Утренняя очередь — Тест",
            DefaultDelaySeconds = 0.7m,
            WindowWaitSeconds = 17,
            ContinueOnError = false,
            Items =
            [
                new QueueItem
                {
                    Name = "Редактор документов",
                    FilePath = context.FilePath("Программы с пробелами/Редактор.exe"),
                    Arguments = "--имя \"Мария Иванова\" --папка \"C:\\Данные с пробелами\\\\\"",
                    WorkingDirectory = Path.Combine(context.DirectoryPath, "Мои документы"),
                    DelaySeconds = 0.725m,
                    CloseWindowAfterLaunch = true,
                    CloseDelaySeconds = 0.375m,
                    Monitor = @"\\.\DISPLAY2",
                    WindowMode = WindowMode.Maximized
                },
                new QueueItem
                {
                    Name = "Отключённая программа",
                    FilePath = context.FilePath("Утилиты/Помощник.exe"),
                    Arguments = "--сообщение Привет",
                    Enabled = false,
                    DelaySeconds = null,
                    CloseWindowAfterLaunch = false,
                    CloseDelaySeconds = 0,
                    WindowMode = WindowMode.Minimized
                }
            ]
        };
        ProfileStore.Save(path, profile);
        var loaded = ProfileStore.Load(path);
        Check.Equal(JsonSerializer.Serialize(profile, ProfileStore.JsonOptions),
            JsonSerializer.Serialize(loaded, ProfileStore.JsonOptions), "Every profile field must roundtrip.");
        var text = File.ReadAllText(path);
        Check.True(text.Contains("Редактор документов", StringComparison.Ordinal), "JSON should preserve readable Cyrillic.");
        Check.True(text.Contains("\"Maximized\"", StringComparison.Ordinal), "Window modes should be serialized by name.");
        profile.Name = "Очередь после повторного сохранения";
        profile.Items[0].Arguments += " --повтор";
        ProfileStore.Save(path, profile);
        Check.Equal(JsonSerializer.Serialize(profile, ProfileStore.JsonOptions),
            JsonSerializer.Serialize(ProfileStore.Load(path), ProfileStore.JsonOptions), "Saving must replace an existing profile.");
        Check.True(!Directory.EnumerateFiles(context.DirectoryPath, "*.tmp").Any(), "Save must not leave temporary files.");
        context.Result.Evidence["Profile"] = Path.GetFileName(path);
        return Task.CompletedTask;
    }

    private static Task InvalidConfigurationAsync(TestContext context)
    {
        QueueProfile Valid() => new()
        {
            Name = "Validation fixture",
            Items = [new QueueItem { FilePath = context.FilePath("never-launched.exe") }]
        };
        var invalid = new (string Name, Action<QueueProfile> Mutate)[]
        {
            ("version-zero", p => p.Version = 0),
            ("version-newer", p => p.Version = 2),
            ("name-empty", p => p.Name = " "),
            ("name-null", p => p.Name = null!),
            ("default-delay-negative", p => p.DefaultDelaySeconds = -0.001m),
            ("default-delay-too-large", p => p.DefaultDelaySeconds = 3600.001m),
            ("window-wait-zero", p => p.WindowWaitSeconds = 0),
            ("window-wait-too-large", p => p.WindowWaitSeconds = 301),
            ("items-null", p => p.Items = null!),
            ("item-null", p => p.Items[0] = null!),
            ("path-blank", p => p.Items[0].FilePath = " "),
            ("path-null", p => p.Items[0].FilePath = null!),
            ("item-name-null", p => p.Items[0].Name = null!),
            ("arguments-null", p => p.Items[0].Arguments = null!),
            ("working-directory-null", p => p.Items[0].WorkingDirectory = null!),
            ("monitor-null", p => p.Items[0].Monitor = null!),
            ("item-delay-negative", p => p.Items[0].DelaySeconds = -0.001m),
            ("item-delay-too-large", p => p.Items[0].DelaySeconds = 3600.001m),
            ("close-delay-negative-disabled", p => p.Items[0].CloseDelaySeconds = -0.001m),
            ("close-delay-too-large-disabled", p => p.Items[0].CloseDelaySeconds = 3600.001m),
            ("close-delay-negative-enabled", p =>
            {
                p.Items[0].CloseWindowAfterLaunch = true;
                p.Items[0].CloseDelaySeconds = -0.001m;
            }),
            ("close-delay-too-large-enabled", p =>
            {
                p.Items[0].CloseWindowAfterLaunch = true;
                p.Items[0].CloseDelaySeconds = 3600.001m;
            }),
            ("window-mode-unknown", p => p.Items[0].WindowMode = (WindowMode)999),
            ("path-newline", p => p.Items[0].FilePath += "\n"),
            ("path-carriage-return", p => p.Items[0].FilePath += "\r"),
            ("path-null-character", p => p.Items[0].FilePath += "\0"),
            ("arguments-null-character", p => p.Items[0].Arguments = "a\0b")
        };
        var guardPath = context.FilePath("valid-save-guard.json");
        ProfileStore.Save(guardPath, Valid());
        var original = File.ReadAllText(guardPath);
        foreach (var (name, mutate) in invalid)
        {
            var profile = Valid();
            mutate(profile);
            Check.Throws<InvalidDataException>(() => ProfileStore.Validate(profile), name + ": Validate");
            Check.Throws<InvalidDataException>(() => ProfileStore.Save(guardPath, profile), name + ": Save");
            Check.Equal(original, File.ReadAllText(guardPath), name + ": invalid save must not damage the existing file");
            var path = context.FilePath("invalid/" + name + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(profile, ProfileStore.JsonOptions));
            Check.Throws<InvalidDataException>(() => ProfileStore.Load(path), name + ": Load");
        }
        foreach (var (name, json) in new[]
        {
            ("malformed", "{\"Version\":"),
            ("wrong-type", "{\"DefaultDelaySeconds\":\"not a number\"}"),
            ("unknown-mode", "{\"Items\":[{\"FilePath\":\"probe.exe\",\"WindowMode\":\"Unknown\"}]}")
        })
        {
            var path = context.FilePath("invalid/" + name + ".json");
            File.WriteAllText(path, json);
            Check.Throws<JsonException>(() => ProfileStore.Load(path), name);
        }
        var nullPath = context.FilePath("invalid/null-root.json");
        File.WriteAllText(nullPath, "null");
        Check.Throws<InvalidDataException>(() => ProfileStore.Load(nullPath), "Null profile root");

        var boundaries = Valid();
        boundaries.DefaultDelaySeconds = 0;
        boundaries.WindowWaitSeconds = 1;
        boundaries.Items[0].DelaySeconds = 0;
        boundaries.Items[0].CloseDelaySeconds = 0;
        ProfileStore.Validate(boundaries);
        boundaries.DefaultDelaySeconds = 3600;
        boundaries.WindowWaitSeconds = 300;
        boundaries.Items[0].DelaySeconds = 3600;
        boundaries.Items[0].CloseWindowAfterLaunch = true;
        boundaries.Items[0].CloseDelaySeconds = 3600;
        ProfileStore.Validate(boundaries);
        context.Result.Evidence["RejectedValidationCases"] = invalid.Select(x => x.Name).ToArray();
        context.Result.Evidence["RejectedJsonCases"] = new[] { "malformed", "wrong-type", "unknown-mode", "null-root" };
        return Task.CompletedTask;
    }

    private static Task RelativePathsAsync(TestContext context)
    {
        var path = context.FilePath("profiles/Относительные пути.json");
        var directory = Path.GetDirectoryName(path)!;
        var profile = new QueueProfile
        {
            Items =
            [
                new QueueItem
                {
                    FilePath = "  \"..\\Программы с пробелами\\проба.exe\"  ",
                    WorkingDirectory = @".\временная\..\Рабочая папка",
                    Arguments = "--относительный .\\файл.txt"
                },
                new QueueItem { FilePath = @".\утилита.exe" }
            ]
        };
        ProfileStore.Save(path, profile);
        var loaded = ProfileStore.Load(path);
        var expected = Path.GetFullPath(Path.Combine(directory, "..", "Программы с пробелами", "проба.exe"));
        Check.Equal(expected, loaded.Items[0].FilePath, "Executable path is relative to the profile, not the harness CWD.");
        Check.Equal(Path.Combine(directory, "Рабочая папка"), loaded.Items[0].WorkingDirectory, "Working directory normalization.");
        Check.Equal(Path.Combine(directory, "утилита.exe"), loaded.Items[1].FilePath, "Dot-relative executable.");
        Check.Equal("", loaded.Items[1].WorkingDirectory, "Empty working directory remains empty.");
        Check.Equal(profile.Items[0].Arguments, loaded.Items[0].Arguments, "Arguments must not undergo path resolution.");
        Check.Equal(expected, ProfileStore.ResolvePath("  \"" + expected + "\"  ", directory), "Quoted absolute path.");
        var temp = Environment.GetEnvironmentVariable("TEMP");
        Check.True(!string.IsNullOrWhiteSpace(temp), "Windows TEMP must exist for the environment-expansion fixture.");
        Check.Equal(Path.GetFullPath(Path.Combine(temp!, "Кириллица", "probe.exe")),
            ProfileStore.ResolvePath(@"%TEMP%\Кириллица\probe.exe", directory), "Environment variable expansion.");
        context.Result.Evidence["ResolvedExecutable"] = loaded.Items[0].FilePath;
        context.Result.Evidence["ResolvedWorkingDirectory"] = loaded.Items[0].WorkingDirectory;
        return Task.CompletedTask;
    }

    private static Task QuoteArgumentAsync(TestContext context)
    {
        var cases = new List<(string Input, string Expected)>
        {
            ("", "\"\""),
            ("plain", "\"plain\""),
            ("two words", "\"two words\""),
            ("Кириллица и пробелы", "\"Кириллица и пробелы\""),
            ("a\"b", "\"a\\\"b\""),
            (@"C:\with spaces\", "\"C:\\with spaces\\\\\""),
            ("tab\tvalue", "\"tab\tvalue\"")
        };
        for (var slashCount = 0; slashCount <= 4; slashCount++)
        {
            cases.Add(("prefix" + new string('\\', slashCount) + "\"suffix",
                "\"prefix" + new string('\\', slashCount * 2 + 1) + "\"suffix\""));
            cases.Add(("folder with spaces" + new string('\\', slashCount),
                "\"folder with spaces" + new string('\\', slashCount * 2) + "\""));
        }
        foreach (var (input, expected) in cases)
        {
            var quoted = ShellLinks.QuoteArgument(input);
            Check.Equal(expected, quoted, "QuoteArgument exact escaping.");
            // Give argv[0] its own simple token: Windows parses that token differently.
            var pointer = CommandLineToArgvW("probe.exe " + quoted, out var count);
            if (pointer == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                Check.Equal(2, count, "Quoted value must remain exactly one argument.");
                var parsed = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, IntPtr.Size));
                Check.Equal(input, parsed, "Windows argument parser roundtrip.");
            }
            finally { LocalFree(pointer); }
        }
        context.Result.Evidence["ArgumentCases"] = cases.Select(x => new { x.Input, x.Expected }).ToArray();
        return Task.CompletedTask;
    }

    private static QueueProfile Profile(params QueueItem[] items) => new()
    {
        Name = "Isolated automated probe queue",
        DefaultDelaySeconds = 0,
        WindowWaitSeconds = 5,
        Items = items.ToList()
    };

    private static async Task DefaultSequenceAsync(TestContext context)
    {
        var first = context.NewProbe("first");
        var disabled = context.NewProbe("disabled");
        disabled.Item.Enabled = false;
        disabled.Item.DelaySeconds = 30;
        disabled.Item.Monitor = @"\\.\DISPLAY99999";
        var second = context.NewProbe("second");
        var third = context.NewProbe("third");
        var profile = Profile(
            new QueueItem { FilePath = context.FilePath("disabled-missing.exe"), Enabled = false, DelaySeconds = 30 },
            first.Item, disabled.Item, second.Item, third.Item);
        profile.DefaultDelaySeconds = 0.7m;
        var result = await context.RunQueueAsync(profile);
        Check.Equal(new RunResult(3, 0, 0, false), result, "Default-delay queue summary.");
        var logs = await Task.WhenAll(new[] { first, second, third }
            .Select(probe => context.WaitForProbeAsync(probe, log => log.Samples.Count >= 2)));
        AssertIntervals(context, [1, 3, 4], logs, 0.7);
        context.AssertNotLaunched(disabled);
        Check.True(context.Result.Messages.All(x => x.Message.ItemIndex != 0 && x.Message.ItemIndex != 2),
            "Disabled entries must not emit launch, delay, error, or placement messages.");
    }

    private static async Task OverrideSequenceAsync(TestContext context)
    {
        var first = context.NewProbe("first", "Кавычки \"значение\" C:\\Папка с пробелами\\");
        var second = context.NewProbe("second");
        first.Item.DelaySeconds = 30; // The first enabled entry must not wait at all.
        second.Item.DelaySeconds = 0.7m;
        var profile = Profile(first.Item, second.Item);
        profile.DefaultDelaySeconds = 30; // An ignored override must fail the bounded queue deadline.
        var result = await context.RunQueueAsync(profile);
        Check.Equal(new RunResult(2, 0, 0, false), result, "Override-delay queue summary.");
        var logs = await Task.WhenAll(new[] { first, second }
            .Select(probe => context.WaitForProbeAsync(probe, log => log.Samples.Count >= 2)));
        AssertIntervals(context, [0, 1], logs, 0.7);
    }

    private static void AssertIntervals(TestContext context, int[] indices, ProbeLog[] logs, double expectedSeconds)
    {
        var launches = context.Result.Messages.Where(x => x.Message.Status == "Запуск…").ToArray();
        Check.Equal(indices.Length, launches.Length, "Launch message count.");
        for (var index = 0; index < indices.Length; index++)
            Check.Equal(indices[index], launches[index].Message.ItemIndex, "Enabled entries must launch in queue order.");
        var observations = new List<object>();
        for (var index = 1; index < logs.Length; index++)
        {
            var commandSeconds = (launches[index].ElapsedMilliseconds - launches[index - 1].ElapsedMilliseconds) / 1000;
            // OS process creation timestamps avoid attributing variable .NET startup/JIT time to the queue delay.
            var processSeconds = (logs[index].ProcessStartUtc - logs[index - 1].ProcessStartUtc).TotalSeconds;
            var entrypointSeconds = (logs[index].LaunchUtc - logs[index - 1].LaunchUtc).TotalSeconds;
            observations.Add(new { ExpectedSeconds = expectedSeconds, CommandSeconds = commandSeconds,
                ProcessSeconds = processSeconds, EntrypointSeconds = entrypointSeconds });
            context.Result.Evidence["LaunchIntervals"] = observations;
            Check.True(commandSeconds >= expectedSeconds - 0.07 && commandSeconds <= expectedSeconds + 2.5,
                $"Launch command interval {commandSeconds:F3}s is not approximately {expectedSeconds:F3}s.");
            Check.True(processSeconds >= expectedSeconds - 0.18 && processSeconds <= expectedSeconds + 2.5,
                $"Probe process launch interval {processSeconds:F3}s is not approximately {expectedSeconds:F3}s.");
            Check.True(entrypointSeconds > 0, "Probe launch UTCs must preserve launch order.");
        }
    }

    private static async Task DisabledOnlyAsync(TestContext context)
    {
        var profile = Profile(new QueueItem { FilePath = context.FilePath("not-present.exe"), Enabled = false });
        profile.DefaultDelaySeconds = 30;
        Check.Equal(new RunResult(0, 0, 0, false), await context.RunQueueAsync(profile), "All-disabled queue summary.");
        Check.Equal(0, context.Result.Messages.Count, "All-disabled queue must not attempt work.");
    }

    private static async Task MonitorAsync(TestContext context, string monitorId)
    {
        var screen = Screen.AllScreens.FirstOrDefault(x => x.DeviceName == monitorId);
        if (screen is null) throw new SkipTestException($"{monitorId} is not connected; no display settings were changed.");
        var probe = context.NewProbe("placed");
        probe.Item.Monitor = monitorId;
        var result = await context.RunQueueAsync(Profile(probe.Item));
        Check.Equal(new RunResult(1, 0, 0, false), result, "Connected monitor placement summary.");
        var log = await context.WaitForProbeAsync(probe,
            value => Stable(value, sample => NormalOnScreen(sample, screen)));
        Check.True(context.Result.Messages.Any(x => x.Message.Status == "Готово" && !x.Message.Warning),
            "Connected-monitor placement should report success.");
        context.Result.Evidence["FinalPlacement"] = log.Samples[^1];
    }

    private static async Task MaximizedAsync(TestContext context)
    {
        var screen = Screen.AllScreens.FirstOrDefault(x => x.DeviceName == @"\\.\DISPLAY2")
            ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var probe = context.NewProbe("maximized");
        probe.Item.Monitor = screen.DeviceName;
        probe.Item.WindowMode = WindowMode.Maximized;
        Check.Equal(new RunResult(1, 0, 0, false), await context.RunQueueAsync(Profile(probe.Item)),
            "Maximized window summary.");
        var log = await context.WaitForProbeAsync(probe, value => Stable(value, sample =>
            sample.Visible && sample.Screen == screen.DeviceName && sample.WindowState == FormWindowState.Maximized &&
            sample.Bounds.Width >= screen.WorkingArea.Width - 32 &&
            sample.Bounds.Height >= screen.WorkingArea.Height - 32 &&
            Math.Abs(sample.Bounds.X - screen.WorkingArea.X) <= 32 &&
            Math.Abs(sample.Bounds.Y - screen.WorkingArea.Y) <= 32));
        context.Result.Evidence["FinalPlacement"] = log.Samples[^1];
    }

    private static async Task MinimizedAsync(TestContext context)
    {
        var probe = context.NewProbe("minimized");
        probe.Item.Monitor = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).DeviceName;
        probe.Item.WindowMode = WindowMode.Minimized;
        Check.Equal(new RunResult(1, 0, 0, false), await context.RunQueueAsync(Profile(probe.Item)),
            "Minimized window summary.");
        var log = await context.WaitForProbeAsync(probe,
            value => Stable(value, sample => sample.WindowState == FormWindowState.Minimized));
        context.Result.Evidence["FinalPlacement"] = log.Samples[^1];
    }

    private static async Task WindowTimeoutAsync(TestContext context)
    {
        var next = context.NewProbe("after-windowless");
        var profile = Profile(new QueueItem
        {
            Name = "Windowless test process",
            FilePath = Environment.ProcessPath!,
            Arguments = "--no-window",
            Monitor = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).DeviceName
        }, next.Item);
        profile.WindowWaitSeconds = 2;
        profile.DefaultDelaySeconds = 0.2m;
        Check.Equal(new RunResult(2, 0, 1, false), await context.RunQueueAsync(profile),
            "Window timeout should warn without blocking the next launch.");
        var launched = context.Result.Messages.Single(x => x.Message.ItemIndex == 1 && x.Message.Status == "Запуск…");
        var timedOut = context.Result.Messages.Single(x => x.Message.ItemIndex == 0 && x.Message.Status == "Окно не найдено");
        Check.True(launched.ElapsedMilliseconds < timedOut.ElapsedMilliseconds,
            "The next program must launch before the first window search times out.");
        await context.WaitForProbeAsync(next);
        context.Result.Evidence["NextLaunchMilliseconds"] = launched.ElapsedMilliseconds;
        context.Result.Evidence["WindowTimeoutMilliseconds"] = timedOut.ElapsedMilliseconds;
    }

    private static bool Stable(ProbeLog log, Func<ProbeSample, bool> predicate) =>
        log.Samples.Count >= 2 && log.Samples.TakeLast(2).All(predicate);

    private static bool NormalOnScreen(ProbeSample sample, Screen screen)
    {
        var bounds = sample.Bounds;
        var work = screen.WorkingArea;
        return sample.Visible && sample.Screen == screen.DeviceName && sample.WindowState == FormWindowState.Normal &&
            bounds.Width >= 80 && bounds.Height >= 50 &&
            bounds.X >= work.Left - 8 && bounds.Y >= work.Top - 8 &&
            bounds.X + bounds.Width <= work.Right + 8 && bounds.Y + bounds.Height <= work.Bottom + 8 &&
            Math.Abs(bounds.X + bounds.Width / 2.0 - (work.Left + work.Width / 2.0)) <= 16 &&
            Math.Abs(bounds.Y + bounds.Height / 2.0 - (work.Top + work.Height / 2.0)) <= 16;
    }

    private static async Task MissingFileAsync(TestContext context, bool continueOnError)
    {
        var before = context.NewProbe("before-error");
        var after = context.NewProbe("after-error");
        var profile = Profile(before.Item,
            new QueueItem { Name = "Deliberately missing", FilePath = context.FilePath("does-not-exist.exe") }, after.Item);
        profile.ContinueOnError = continueOnError;
        var result = await context.RunQueueAsync(profile);
        Check.Equal(new RunResult(continueOnError ? 2 : 1, 1, 0, false), result, "Missing-file queue summary.");
        Check.Equal(1, context.Result.Messages.Count(x => x.Message.Error && x.Message.ItemIndex == 1),
            "Missing file must emit exactly one item error.");
        var beforeLog = await context.WaitForProbeAsync(before);
        Check.True(beforeLog.ClosedUtc is null, "An error must not close an already launched application.");
        if (continueOnError) await context.WaitForProbeAsync(after);
        else context.AssertNotLaunched(after);
    }

    private static async Task CancellationAsync(TestContext context)
    {
        var before = context.NewProbe("before-stop");
        before.Item.Monitor = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).DeviceName;
        var after = context.NewProbe("after-stop");
        var profile = Profile(before.Item, after.Item);
        profile.DefaultDelaySeconds = 30;
        using var cancellation = new CancellationTokenSource();
        var delayObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = context.RunQueueAsync(profile, cancellation.Token, message =>
        {
            if (message.ItemIndex == 1 && message.Status.StartsWith("Через ", StringComparison.Ordinal))
                delayObserved.TrySetResult(true);
        });
        try
        {
            await delayObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await context.WaitForProbeAsync(before);
            var stopUtc = DateTimeOffset.UtcNow;
            var timer = Stopwatch.StartNew();
            cancellation.Cancel();
            var result = await running.WaitAsync(TimeSpan.FromSeconds(2));
            context.Result.Evidence["StopLatencyMilliseconds"] = timer.Elapsed.TotalMilliseconds;
            Check.Equal(new RunResult(1, 0, 0, true), result, "Cancellation during delay summary.");
            Check.True(timer.Elapsed < TimeSpan.FromSeconds(2), "Cancellation must interrupt the delay promptly.");
            Check.True(context.Result.Messages.Any(x => x.Message.ItemIndex == -1 && x.Message.Status == "Остановлено"),
                "Cancellation must report the stopped queue.");
            context.AssertNotLaunched(after);
            var survivor = await context.WaitForProbeAsync(before,
                log => log.ClosedUtc is null && log.Samples.Any(sample => sample.Utc >= stopUtc && sample.Visible));
            context.Result.Evidence["SurvivingProbeAfterStop"] = survivor.Samples[^1];
        }
        finally
        {
            cancellation.Cancel();
            // Settle the runner before inspecting/closing its probes, including assertion failures.
            await running;
        }
    }

    private static async Task DisconnectedMonitorAsync(TestContext context)
    {
        var primary = Screen.PrimaryScreen ?? throw new InvalidOperationException("No primary monitor is available.");
        var monitorId = @"\\.\DISPLAY99999";
        while (Screen.AllScreens.Any(x => x.DeviceName == monitorId)) monitorId += "9";
        var probe = context.NewProbe("fallback");
        probe.Item.Monitor = monitorId;
        var result = await context.RunQueueAsync(Profile(probe.Item));
        Check.Equal(new RunResult(1, 0, 1, false), result, "Disconnected-monitor fallback summary.");
        Check.True(context.Result.Messages.Any(x => x.Message.ItemIndex == 0 && x.Message.Warning &&
            !x.Message.Error && x.Message.Status == "Основной монитор" &&
            x.Message.Detail.Contains("отключён", StringComparison.Ordinal) &&
            x.Message.Detail.Contains("основной", StringComparison.Ordinal)),
            "Disconnected monitor must produce a specific fallback warning, not a window-not-found warning.");
        var log = await context.WaitForProbeAsync(probe,
            value => Stable(value, sample => NormalOnScreen(sample, primary)));
        context.Result.Evidence["RequestedMonitor"] = monitorId;
        context.Result.Evidence["FinalPlacement"] = log.Samples[^1];
    }

    private static Task CloseConfigurationAsync(TestContext context)
    {
        var defaults = new QueueItem();
        Check.Equal(false, defaults.CloseWindowAfterLaunch, "New items must leave windows open by default.");
        Check.Equal(2m, defaults.CloseDelaySeconds, "New items have a two-second close delay.");
        var legacyPath = context.FilePath("legacy-without-close-fields.json");
        File.WriteAllText(legacyPath, """
            {"Version":1,"Name":"Legacy profile","Items":[{"Name":"Legacy item","FilePath":"never-launched.exe"}]}
            """);
        var legacy = ProfileStore.Load(legacyPath);
        Check.Equal(false, legacy.Items[0].CloseWindowAfterLaunch, "Legacy JSON must not opt into closing windows.");
        Check.Equal(2m, legacy.Items[0].CloseDelaySeconds, "Legacy JSON receives the default close delay.");
        var savedPath = context.FilePath("legacy-resaved.json");
        ProfileStore.Save(savedPath, legacy);
        var reloaded = ProfileStore.Load(savedPath);
        Check.Equal(false, reloaded.Items[0].CloseWindowAfterLaunch, "Saving a legacy profile keeps closing disabled.");
        Check.Equal(2m, reloaded.Items[0].CloseDelaySeconds, "Saving a legacy profile preserves its close delay.");

        var original = legacy.Items[0];
        original.CloseWindowAfterLaunch = true;
        original.CloseDelaySeconds = 0.375m;
        var copy = original.Copy();
        Check.True(!ReferenceEquals(original, copy), "Copy must produce a separate queue item.");
        Check.Equal(JsonSerializer.Serialize(original, ProfileStore.JsonOptions),
            JsonSerializer.Serialize(copy, ProfileStore.JsonOptions), "Copy preserves all fields, including close settings.");
        copy.CloseWindowAfterLaunch = false;
        copy.CloseDelaySeconds = 1.25m;
        Check.Equal(true, original.CloseWindowAfterLaunch, "Editing a copy must not change the original close flag.");
        Check.Equal(0.375m, original.CloseDelaySeconds, "Editing a copy must not change the original close delay.");
        context.Result.Evidence["LegacyDefaults"] = reloaded.Items[0];
        return Task.CompletedTask;
    }

    private static async Task CloseLastAsync(TestContext context, decimal delay)
    {
        var probe = context.NewProbe("last-to-close");
        probe.Item.CloseWindowAfterLaunch = true;
        probe.Item.CloseDelaySeconds = delay;
        Check.Equal(new RunResult(1, 0, 0, false), await context.RunQueueAsync(Profile(probe.Item)),
            "The last window must close successfully without a following item.");
        var returnedUtc = DateTimeOffset.UtcNow;
        context.Result.Evidence["RunReturnedUtc"] = returnedUtc;
        var log = await context.WaitForProbeAsync(probe, value => value.ClosedUtc.HasValue);
        AssertOrdinaryClose(log);
        Check.True(log.ClosedUtc <= returnedUtc, "RunAsync must not return before the last window closes.");
        Check.True(!ProbeWindows.IsWindowVisible((nint)log.WindowHandle), "The last window must no longer be visible.");
        AssertCloseCountdown(context, probe, log, 0);
        if (delay == 0)
        {
            var ready = context.Result.Messages.Last(x => x.Message.ItemIndex == 0 && x.Message.Status == "Готово");
            var seconds = (log.CloseRequests[0] - ready.Utc).TotalSeconds;
            context.Result.Evidence["ZeroDelayCloseSecondsAfterReady"] = seconds;
            Check.True(seconds >= 0 && seconds < 1, "Zero close delay must not wait for the default two seconds.");
        }
        context.Result.Evidence["ClosedProbe"] = log;
    }

    private static async Task CloseTrayAsync(TestContext context)
    {
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var probe = context.NewProbe("tray-like", mode: ProbeMode.HideOnClose);
        probe.Item.CloseWindowAfterLaunch = true;
        probe.Item.CloseDelaySeconds = 0.35m;
        probe.Item.Monitor = screen.DeviceName;
        Check.Equal(new RunResult(1, 0, 0, false), await context.RunQueueAsync(Profile(probe.Item)),
            "A tray-like window hiding after ordinary close is successful, not a warning.");
        var returnedUtc = DateTimeOffset.UtcNow;
        var log = await AssertHiddenAliveAsync(context, probe, returnedUtc);
        AssertCloseCountdown(context, probe, log, 0);
        Check.True(log.Samples.Any(sample => sample.Utc < log.CloseRequests[0] && NormalOnScreen(sample, screen)),
            "Requested placement must complete before the tray-like window is closed.");
    }

    private static async Task CloseDisabledAsync(TestContext context)
    {
        var probe = context.NewProbe("leave-visible", mode: ProbeMode.HideOnClose);
        probe.Item.CloseWindowAfterLaunch = false;
        probe.Item.CloseDelaySeconds = 0.15m;
        probe.Item.Monitor = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).DeviceName;
        var next = context.NewProbe("next-with-close-disabled");
        var profile = Profile(probe.Item, next.Item);
        profile.DefaultDelaySeconds = 0.3m;
        Check.Equal(new RunResult(2, 0, 0, false), await context.RunQueueAsync(profile),
            "Disabled closing preserves ordinary non-blocking window watching.");
        var returnedUtc = DateTimeOffset.UtcNow;
        var logs = await Task.WhenAll(new[] { probe, next }
            .Select(item => context.WaitForProbeAsync(item, value => value.Samples.Count >= 2)));
        AssertIntervals(context, [0, 1], logs, 0.3);
        var nextLaunch = context.Result.Messages.Single(x => x.Message.ItemIndex == 1 && x.Message.Status == "Запуск…");
        var ready = context.Result.Messages.Single(x => x.Message.ItemIndex == 0 && x.Message.Status == "Готово");
        Check.True(nextLaunch.Utc < ready.Utc, "Disabled closing must not serialize the next launch behind window placement.");
        var survivor = await context.WaitForProbeAsync(probe,
            value => value.Samples.Any(sample => sample.Utc >= returnedUtc.AddSeconds(0.3) && sample.Visible));
        Check.Equal(0, survivor.CloseRequests.Count, "Disabled closing must not send WM_CLOSE.");
        Check.True(survivor.HiddenUtc is null, "Disabled closing must not hide the window.");
        Check.True(!context.Result.Messages.Any(x => x.Message.Status.StartsWith("До закрытия ", StringComparison.Ordinal)),
            "Disabled closing must not report a close countdown.");
        AssertAlive(survivor, visible: true);
        context.Result.Evidence["VisibleAfterDisabledCloseDelay"] = survivor;
    }

    private static async Task CloseIntervalAsync(TestContext context, bool useOverride)
    {
        var first = context.NewProbe("close-before-next", mode: ProbeMode.HideOnClose);
        first.Item.CloseWindowAfterLaunch = true;
        first.Item.CloseDelaySeconds = 0.55m;
        first.Item.DelaySeconds = 30; // Still ignored for the first enabled entry.
        var next = context.NewProbe("after-close");
        var interval = useOverride ? 0.35 : 0.45;
        next.Item.DelaySeconds = useOverride ? (decimal)interval : null;
        var profile = Profile(first.Item, next.Item);
        profile.DefaultDelaySeconds = useOverride ? 30 : (decimal)interval;
        Check.Equal(new RunResult(2, 0, 0, false), await context.RunQueueAsync(profile),
            "Close-before-next queue summary.");
        var returnedUtc = DateTimeOffset.UtcNow;
        var firstLog = await AssertHiddenAliveAsync(context, first, returnedUtc);
        var nextLog = await context.WaitForProbeAsync(next);
        AssertCloseCountdown(context, first, firstLog, 0);
        var launches = context.Result.Messages.Where(x => x.Message.Status == "Запуск…").ToArray();
        Check.Equal(2, launches.Length, "Exactly two probes must launch.");
        Check.Equal(0, launches[0].Message.ItemIndex, "First launch order.");
        Check.Equal(1, launches[1].Message.ItemIndex, "Next launch order.");
        var closeUtc = firstLog.HiddenUtc!.Value;
        AssertIntervalAfterEvent(context, "IntervalAfterClose", closeUtc, launches[1], interval);
        var processSeconds = (nextLog.ProcessStartUtc - closeUtc).TotalSeconds;
        Check.True(processSeconds >= interval - 0.18 && processSeconds <= interval + 2.5,
            $"The next process must start about {interval:F2}s after closing, not after the previous launch: {processSeconds:F3}s.");
        var countdown = context.Result.Messages.First(x => x.Message.ItemIndex == 0 &&
            x.Message.Status.StartsWith("До закрытия ", StringComparison.Ordinal));
        var combinedSeconds = (launches[1].Utc - countdown.Utc).TotalSeconds;
        Check.True(combinedSeconds >= (double)first.Item.CloseDelaySeconds + interval - 0.07,
            "The close delay and the next ordinary interval must add, not overlap.");
        var nextDelay = context.Result.Messages.First(x => x.Message.ItemIndex == 1 &&
            x.Message.Status.StartsWith("Через ", StringComparison.Ordinal));
        Check.True(nextDelay.Utc >= closeUtc, "The next interval countdown must begin only after the window hides.");
        context.Result.Evidence["CloseAndNextTiming"] = new
        {
            CloseUtc = closeUtc, NextProcessStartUtc = nextLog.ProcessStartUtc,
            ProcessSeconds = processSeconds, CombinedSeconds = combinedSeconds, UseOverride = useOverride
        };
    }

    private static async Task CloseCancellationAsync(TestContext context)
    {
        var current = context.NewProbe("cancel-close-countdown", mode: ProbeMode.HideOnClose);
        current.Item.CloseWindowAfterLaunch = true;
        current.Item.CloseDelaySeconds = 3.2m;
        var next = context.NewProbe("must-not-launch-after-cancel");
        using var cancellation = new CancellationTokenSource();
        var countdownObserved = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = context.RunQueueAsync(Profile(current.Item, next.Item), cancellation.Token, message =>
        {
            if (message.ItemIndex == 0 && message.Status.StartsWith("До закрытия ", StringComparison.Ordinal))
                countdownObserved.TrySetResult(DateTimeOffset.UtcNow);
        });
        try
        {
            var countdownUtc = await countdownObserved.Task.WaitAsync(TimeSpan.FromSeconds(4));
            var countdown = context.Result.Messages.First(x => x.Message.Status.StartsWith("До закрытия ", StringComparison.Ordinal));
            Check.Equal("До закрытия 4 с", countdown.Message.Status, "A fractional long countdown rounds upward.");
            Check.True(countdown.Message.Detail.Contains(current.Item.Name, StringComparison.Ordinal),
                "Close countdown detail must identify the current application.");
            await context.WaitForProbeAsync(current, log => log.Samples.Any(sample => sample.Visible));
            var timer = Stopwatch.StartNew();
            cancellation.Cancel();
            Check.Equal(new RunResult(1, 0, 0, true), await running.WaitAsync(TimeSpan.FromSeconds(2)),
                "Cancellation during close countdown must stop without closing or launching the next item.");
            context.Result.Evidence["CloseCancellationLatencyMilliseconds"] = timer.Elapsed.TotalMilliseconds;
            Check.True(timer.Elapsed < TimeSpan.FromSeconds(2), "Close countdown cancellation must be prompt.");
            Check.True(context.Result.Messages.Any(x => x.Message.ItemIndex == -1 && x.Message.Status == "Остановлено"),
                "Close countdown cancellation must report a stopped queue.");
            context.AssertNotLaunched(next);
            // Observe beyond the abandoned close deadline to catch a detached countdown sending a late WM_CLOSE.
            var observeUntil = countdownUtc.AddSeconds((double)current.Item.CloseDelaySeconds + 0.3);
            var survivor = await context.WaitForProbeAsync(current,
                log => log.Samples.Any(sample => sample.Utc >= observeUntil && sample.Visible), TimeSpan.FromSeconds(4));
            Check.Equal(0, survivor.CloseRequests.Count, "Cancellation must not send even a delayed WM_CLOSE.");
            Check.True(survivor.HiddenUtc is null, "Cancelled closing must leave the current window visible.");
            AssertAlive(survivor, visible: true);
            context.AssertNotLaunched(next);
            context.Result.Evidence["SurvivorBeyondCancelledDeadline"] = survivor;
        }
        finally
        {
            cancellation.Cancel();
            await running;
        }
    }

    private static async Task CloseIgnoredAsync(TestContext context)
    {
        var probe = context.NewProbe("ignore-close", mode: ProbeMode.IgnoreClose);
        probe.Item.CloseWindowAfterLaunch = true;
        probe.Item.CloseDelaySeconds = 0.25m;
        var next = context.NewProbe("after-ignored-close");
        var profile = Profile(probe.Item, next.Item);
        profile.DefaultDelaySeconds = 0.25m;
        profile.ContinueOnError = false; // A close warning is not a launch failure.
        // Allow the five-second window search plus the mandated two-second close observation.
        Check.Equal(new RunResult(2, 0, 1, false),
            await context.RunQueueAsync(profile, deadlineOverride: TimeSpan.FromSeconds(9)),
            "An ignored ordinary close must warn and continue, without killing the process.");
        var returnedUtc = DateTimeOffset.UtcNow;
        var log = await context.WaitForProbeAsync(probe,
            value => value.Samples.Any(sample => sample.Utc >= returnedUtc && sample.Visible));
        AssertOrdinaryClose(log);
        AssertCloseCountdown(context, probe, log, 0);
        AssertAlive(log, visible: true);
        Check.True(log.HiddenUtc is null, "The ignore-close probe must remain visible.");
        var warning = context.Result.Messages.Single(x => x.Message.ItemIndex == 0 && x.Message.Warning);
        Check.True(!warning.Message.Error && warning.Message.Status != "Окно не найдено" &&
            (warning.Message.Status + warning.Message.Detail).Contains("закры", StringComparison.OrdinalIgnoreCase),
            "The warning must describe the unsuccessful close, not a missing window or launch failure.");
        var observedSeconds = (warning.Utc - log.CloseRequests[0]).TotalSeconds;
        context.Result.Evidence["IgnoredCloseObservationSeconds"] = observedSeconds;
        Check.True(observedSeconds >= 1.7 && observedSeconds <= 3,
            $"An ignored close should be observed for about two seconds, not indefinitely: {observedSeconds:F3}s.");
        var nextLaunch = context.Result.Messages.Single(x => x.Message.ItemIndex == 1 && x.Message.Status == "Запуск…");
        AssertIntervalAfterEvent(context, "IntervalAfterIgnoredCloseWarning", warning.Utc, nextLaunch, 0.25);
        await context.WaitForProbeAsync(next);
        context.Result.Evidence["IgnoredCloseStillAlive"] = log;
    }

    private static async Task CloseMissingWindowAsync(TestContext context)
    {
        var next = context.NewProbe("after-missing-close-window");
        var profile = Profile(new QueueItem
        {
            Name = "Windowless close fixture",
            FilePath = Environment.ProcessPath!,
            Arguments = "--no-window",
            CloseWindowAfterLaunch = true,
            CloseDelaySeconds = 30 // No window means no close countdown; the normal interval still applies.
        }, next.Item);
        profile.WindowWaitSeconds = 1;
        profile.DefaultDelaySeconds = 0.35m;
        profile.ContinueOnError = false;
        Check.Equal(new RunResult(2, 0, 1, false), await context.RunQueueAsync(profile),
            "A missing close target must warn and proceed within the normal bounded deadline.");
        var warning = context.Result.Messages.Single(x => x.Message.ItemIndex == 0 && x.Message.Warning);
        Check.Equal("Окно не найдено", warning.Message.Status, "Missing close target keeps the existing window warning.");
        Check.True(!warning.Message.Error, "A missing close target is not a launch error.");
        Check.True(!context.Result.Messages.Any(x => x.Message.ItemIndex == 0 &&
            x.Message.Status.StartsWith("До закрытия ", StringComparison.Ordinal)),
            "A missing window must not start a close countdown.");
        var nextLaunch = context.Result.Messages.Single(x => x.Message.ItemIndex == 1 && x.Message.Status == "Запуск…");
        AssertIntervalAfterEvent(context, "IntervalAfterMissingWindow", warning.Utc, nextLaunch, 0.35);
        var log = await context.WaitForProbeAsync(next);
        Check.Equal(0, log.CloseRequests.Count, "A failed search must not close the next item's window.");
        AssertAlive(log, visible: true);
    }

    private static async Task ClosePlacementWarningAsync(TestContext context)
    {
        var primary = Screen.PrimaryScreen ?? throw new InvalidOperationException("No primary monitor is available.");
        var monitorId = @"\\.\DISPLAY99999";
        while (Screen.AllScreens.Any(x => x.DeviceName == monitorId)) monitorId += "9";
        var probe = context.NewProbe("close-with-placement-warning", mode: ProbeMode.HideOnClose);
        probe.Item.Monitor = monitorId;
        probe.Item.CloseWindowAfterLaunch = true;
        probe.Item.CloseDelaySeconds = 0.25m;
        Check.Equal(new RunResult(1, 0, 1, false), await context.RunQueueAsync(Profile(probe.Item)),
            "A placement warning must not prevent the ordinary close attempt.");
        var returnedUtc = DateTimeOffset.UtcNow;
        var log = await AssertHiddenAliveAsync(context, probe, returnedUtc);
        AssertCloseCountdown(context, probe, log, 0);
        var warning = context.Result.Messages.Single(x => x.Message.ItemIndex == 0 && x.Message.Warning);
        Check.Equal("Основной монитор", warning.Message.Status, "The warning must be the disconnected-monitor fallback.");
        Check.True(!warning.Message.Error && warning.Utc <= log.CloseRequests[0], "The close must follow the placement warning.");
        Check.True(log.Samples.Any(sample => sample.Utc < log.CloseRequests[0] && NormalOnScreen(sample, primary)),
            "The fallback placement must still be applied before closing.");
    }

    private static void AssertOrdinaryClose(ProbeLog log)
    {
        Check.Equal(1, log.CloseRequests.Count, "The probe must receive exactly one ordinary WM_CLOSE.");
        Check.True(log.ForcedCloseUtc is null && log.ForcedCloseReason is null,
            "Only the runner's ordinary close, not cleanup or the safety timer, may satisfy a close assertion.");
    }

    private static async Task<ProbeLog> AssertHiddenAliveAsync(TestContext context, ProbeSpec probe, DateTimeOffset returnedUtc)
    {
        var log = await context.WaitForProbeAsync(probe, value => value.HiddenUtc.HasValue &&
            value.Samples.Count(sample => sample.Utc >= returnedUtc && !sample.Visible) >= 2);
        AssertOrdinaryClose(log);
        Check.True(log.HiddenUtc <= returnedUtc, "RunAsync must await disappearance/hide, including for the last item.");
        Check.True(log.Samples.Any(sample => sample.Visible && sample.Utc < log.CloseRequests[0]),
            "The tray-like probe must have been visible before ordinary close.");
        AssertAlive(log, visible: false);
        context.Result.Evidence["RunReturnedUtc"] = returnedUtc;
        context.Result.Evidence["HiddenButAliveProbe"] = log;
        return log;
    }

    private static void AssertAlive(ProbeLog log, bool visible)
    {
        Check.True(log.ProcessId != Environment.ProcessId, "The live process must be a probe, not the harness.");
        using var process = Process.GetProcessById(log.ProcessId);
        Check.True(!process.HasExited, "Ordinary window closing must not terminate a tray-like or ignoring process.");
        Check.True(Math.Abs((new DateTimeOffset(process.StartTime.ToUniversalTime()) - log.ProcessStartUtc).TotalMilliseconds) <= 1,
            "The surviving PID must still belong to the original probe.");
        Check.True(string.Equals(process.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase),
            "The surviving process must be this test executable.");
        Check.True(log.ClosedUtc is null && log.ForcedCloseUtc is null, "The surviving probe must not have exited or used forced cleanup.");
        var window = (nint)log.WindowHandle;
        Check.True(ProbeWindows.IsWindow(window), "The surviving probe must retain its own window handle.");
        ProbeWindows.GetWindowThreadProcessId(window, out var processId);
        Check.Equal((uint)log.ProcessId, processId, "The retained window must still belong to the verified probe.");
        Check.Equal(visible, ProbeWindows.IsWindowVisible(window), "Native window visibility must agree with the probe samples.");
    }

    private static void AssertCloseCountdown(TestContext context, ProbeSpec probe, ProbeLog log, int index)
    {
        const string prefix = "До закрытия ";
        var messages = context.Result.Messages.Where(x => x.Message.ItemIndex == index &&
            x.Message.Status.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        var maximum = (int)decimal.Ceiling(probe.Item.CloseDelaySeconds);
        if (maximum > 0)
        {
            Check.True(messages.Length > 0, "Closing with a positive delay must report its countdown.");
            Check.Equal($"{prefix}{maximum} с", messages[0].Message.Status, "Close countdown must round fractional seconds upward.");
            var waitSeconds = (log.CloseRequests[0] - messages[0].Utc).TotalSeconds;
            context.Result.Evidence["CloseCountdownSeconds"] = waitSeconds;
            Check.True(waitSeconds >= (double)probe.Item.CloseDelaySeconds - 0.07 &&
                waitSeconds <= (double)probe.Item.CloseDelaySeconds + 2.5,
                $"WM_CLOSE must follow the full configured close delay: {waitSeconds:F3}s.");
        }
        foreach (var message in messages)
        {
            Check.True(message.Message.Status.EndsWith(" с", StringComparison.Ordinal) &&
                int.TryParse(message.Message.Status[prefix.Length..^2], out var seconds) && seconds >= 0 && seconds <= maximum,
                "Close countdown must use an integer ceiling in the specified status format.");
            Check.True(message.Message.Detail.Contains(probe.Item.Name, StringComparison.Ordinal),
                "Every close countdown detail must identify the application.");
            Check.True(!message.Message.Error && !message.Message.Warning, "A close countdown is not an error or warning.");
            Check.True(message.Utc <= log.CloseRequests[0], "Countdown messages must precede WM_CLOSE.");
        }
    }

    private static void AssertIntervalAfterEvent(TestContext context, string evidenceKey,
        DateTimeOffset eventUtc, TimedMessage nextLaunch, double expectedSeconds)
    {
        var seconds = (nextLaunch.Utc - eventUtc).TotalSeconds;
        context.Result.Evidence[evidenceKey] = new { EventUtc = eventUtc, NextLaunchUtc = nextLaunch.Utc, Seconds = seconds, ExpectedSeconds = expectedSeconds };
        Check.True(seconds >= expectedSeconds - 0.07 && seconds <= expectedSeconds + 2.5,
            $"Next launch interval {seconds:F3}s must be approximately {expectedSeconds:F3}s after the close outcome.");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
