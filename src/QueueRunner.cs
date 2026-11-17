using System.Collections.Concurrent;
using System.Diagnostics;

namespace LaunchQueue;

public sealed record RunMessage(int ItemIndex, string Status, string Detail, bool Warning = false, bool Error = false);
public sealed record RunResult(int Started, int Failed, int Warnings, bool Cancelled);

public sealed class QueueRunner
{
    private readonly Action<RunMessage> report;
    public QueueRunner(Action<RunMessage> report) => this.report = report;

    public async Task<RunResult> RunAsync(QueueProfile profile, CancellationToken cancellation)
    {
        ProfileStore.Validate(profile);
        var enabled = profile.Items.Select((item, index) => (Item: item.Copy(), Index: index)).Where(x => x.Item.Enabled).ToList();
        var watchers = new List<Task<int>>();
        var processOwners = new ConcurrentDictionary<uint, int>();
        var started = 0;
        var failed = 0;
        var warnings = 0;
        var cancelled = false;
        var lastStart = Stopwatch.StartNew();
        using var watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        try
        {
            for (var position = 0; position < enabled.Count; position++)
            {
                var (item, index) = enabled[position];
                if (position > 0)
                {
                    var interval = TimeSpan.FromSeconds((double)(item.DelaySeconds ?? profile.DefaultDelaySeconds));
                    var remaining = interval - lastStart.Elapsed;
                    while (remaining > TimeSpan.Zero)
                    {
                        report(new(index, $"Через {Math.Ceiling(remaining.TotalSeconds)} с", $"Ожидание перед «{item.Name}»"));
                        await Task.Delay(remaining > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining, cancellation);
                        remaining = interval - lastStart.Elapsed;
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    var path = Environment.ExpandEnvironmentVariables(item.FilePath);
                    if (!File.Exists(path)) throw new FileNotFoundException("Файл программы не найден", path);
                    var directory = string.IsNullOrWhiteSpace(item.WorkingDirectory)
                        ? Path.GetDirectoryName(path)! : Environment.ExpandEnvironmentVariables(item.WorkingDirectory);
                    if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"Рабочая папка не найдена: {directory}");
                    var needsWindow = item.Monitor.Length > 0 || item.WindowMode != WindowMode.Normal || item.CloseWindowAfterLaunch;
                    var target = needsWindow ? ShellLinks.ResolveTarget(path) : "";
                    var before = needsWindow ? NativeWindows.VisibleWindows().Select(w => w.Handle).ToHashSet() : [];
                    report(new(index, "Запуск…", $"Запуск «{item.Name}»"));
                    lastStart.Restart();
                    using var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = item.Arguments,
                        WorkingDirectory = directory,
                        UseShellExecute = true
                    });
                    started++;
                    uint? pid = null;
                    try { if (process is not null) pid = (uint)process.Id; }
                    catch (InvalidOperationException) { }
                    if (pid.HasValue) processOwners.TryAdd(pid.Value, index);
                    if (needsWindow)
                    {
                        report(new(index, "Ожидание окна", $"«{item.Name}» запущена; ищем окно до {profile.WindowWaitSeconds} с"));
                        var windowTask = WatchWindowAsync(item, index, pid, target, before, processOwners, profile.WindowWaitSeconds, watchCancellation.Token);
                        if (item.CloseWindowAfterLaunch)
                        {
                            warnings += await windowTask;
                            lastStart.Restart();
                        }
                        else watchers.Add(windowTask);
                    }
                    else report(new(index, "Запущено", $"«{item.Name}»: команда запуска выполнена"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    report(new(index, "Ошибка", $"«{item.Name}»: {ex.Message}", Error: true));
                    if (!profile.ContinueOnError) break;
                }
            }
            if (watchers.Count > 0) warnings += (await Task.WhenAll(watchers)).Sum();
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            watchCancellation.Cancel();
            try { await Task.WhenAll(watchers); } catch (OperationCanceledException) { }
            report(new(-1, "Остановлено", "Очередь остановлена. Дальнейшие запуски, переносы и закрытия отменены; уже отправленное закрытие отменить нельзя."));
        }
        return new(started, failed, warnings, cancelled);
    }

    private async Task<int> CloseWindowAsync(QueueItem item, int index, NativeWindows.WindowInfo window, CancellationToken cancellation)
    {
        var delay = TimeSpan.FromSeconds((double)item.CloseDelaySeconds);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < delay)
        {
            var remaining = delay - timer.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            report(new(index, $"До закрытия {Math.Ceiling(remaining.TotalSeconds)} с",
                $"«{item.Name}»: закроем окно через {item.CloseDelaySeconds:0.#} с; следующий шаг пока ожидает"));
            await Task.Delay(remaining > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining, cancellation);
        }
        cancellation.ThrowIfCancellationRequested();
        if (!NativeWindows.IsOpen(window))
        {
            report(new(index, "Окно уже закрыто", $"«{item.Name}»: окно уже закрыто или скрыто, команда закрытия не отправлялась"));
            return 0;
        }
        if (!NativeWindows.RequestClose(window))
        {
            report(new(index, "Закрытие не отправлено", $"«{item.Name}»: Windows не разрешила закрытие окна. Проверьте права приложения; процесс принудительно не завершается.", Warning: true));
            return 1;
        }
        report(new(index, "Закрытие отправлено", $"«{item.Name}»: отправлена обычная команда закрытия окна (как крестик)"));
        timer.Restart();
        while (NativeWindows.IsOpen(window) && timer.Elapsed < TimeSpan.FromSeconds(2))
            await Task.Delay(100, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (NativeWindows.IsOpen(window))
        {
            report(new(index, "Окно осталось открытым", $"«{item.Name}»: окно не закрылось за 2 с. Возможно, требуется ответ в диалоге или приложение игнорирует закрытие. Принудительного завершения нет.", Warning: true));
            return 1;
        }
        report(new(index, "Окно закрыто", $"«{item.Name}»: окно закрыто или скрыто. Продолжение работы в трее зависит от настроек самой программы."));
        return 0;
    }

    private async Task<int> WatchWindowAsync(QueueItem item, int index, uint? processId, string target,
        HashSet<nint> before, ConcurrentDictionary<uint, int> processOwners, int seconds, CancellationToken cancellation)
    {
        try
        {
            var family = processId.HasValue ? new HashSet<uint> { processId.Value } : [];
            var timer = Stopwatch.StartNew();
            nint previous = 0;
            var stable = 0;
            while (timer.Elapsed.TotalSeconds < seconds)
            {
                cancellation.ThrowIfCancellationRequested();
                NativeWindows.ExpandProcessFamily(family);
                foreach (var pid in family) processOwners.TryAdd(pid, index);
                // An earlier window search must not take a later step's instance of the same executable.
                var windows = NativeWindows.VisibleWindows()
                    .Where(w => !processOwners.TryGetValue(w.ProcessId, out var owner) || owner <= index).ToList();
                var match = windows.FirstOrDefault(w => !before.Contains(w.Handle) && family.Contains(w.ProcessId))
                    ?? windows.FirstOrDefault(w => !before.Contains(w.Handle) && NativeWindows.MatchesExecutable(w.ProcessId, target));
                // Single-instance applications may reuse an existing window instead of creating a new one.
                if (match is null && timer.Elapsed.TotalSeconds >= 2)
                    match = windows.FirstOrDefault(w => NativeWindows.MatchesExecutable(w.ProcessId, target));
                if (match is not null)
                {
                    stable = match.Handle == previous ? stable + 1 : 0;
                    previous = match.Handle;
                    if (stable >= 2)
                    {
                        var screen = string.IsNullOrEmpty(item.Monitor) ? null
                            : Screen.AllScreens.FirstOrDefault(s => s.DeviceName == item.Monitor);
                        var missing = item.Monitor.Length > 0 && screen is null;
                        if (missing) screen = Screen.PrimaryScreen;
                        var moved = await NativeWindows.PlaceAsync(match.Handle, screen, item.WindowMode, cancellation);
                        var reused = before.Contains(match.Handle);
                        var warningCount = 0;
                        if (!moved)
                        {
                            report(new(index, "Окно не перенесено", $"«{item.Name}»: Windows не подтвердила перенос. Проверьте права администратора и полноэкранный режим.", Warning: true));
                            warningCount++;
                        }
                        else
                        {
                            var detail = screen is null ? "окно найдено" : $"окно на {MonitorChoice.LabelFor(screen.DeviceName)}";
                            if (missing) detail += "; выбранный монитор отключён — использован основной";
                            if (reused) detail += "; использовано уже открытое окно";
                            report(new(index, missing ? "Основной монитор" : "Готово", $"«{item.Name}»: {detail}", Warning: missing));
                            if (missing) warningCount++;
                        }
                        if (item.CloseWindowAfterLaunch)
                            warningCount += await CloseWindowAsync(item, index, match, cancellation);
                        return warningCount;
                    }
                }
                else { previous = 0; stable = 0; }
                await Task.Delay(350, cancellation);
            }
            report(new(index, "Окно не найдено", $"«{item.Name}» запущена, но подходящее окно не найдено за {seconds} с. Перенос и закрытие пропущены. Увеличьте ожидание; Store-приложения и посредники могут не поддерживаться.", Warning: true));
            return 1;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            report(new(index, "Не удалось настроить окно", $"«{item.Name}»: {ex.Message}", Warning: true));
            return 1;
        }
    }
}
