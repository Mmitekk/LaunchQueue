using System.Security.Cryptography;
using System.Text;

namespace LaunchQueue;

internal sealed class MainForm : Form
{
    private QueueProfile profile = new();
    private string? profilePath;
    private bool dirty;
    private bool updating;
    private CancellationTokenSource? running;
    private readonly bool autoRun;
    private readonly bool closeAfter;
    private readonly string root;
    private readonly TextBox profileName = Theme.TextBox("Название очереди");
    private readonly NumericUpDown defaultDelay = Theme.Number(3, 3600, 1);
    private readonly NumericUpDown windowWait = Theme.Number(30, 300);
    private readonly CheckBox continueOnError = new() { Text = "Продолжать при ошибке запуска", Checked = true, AutoSize = true, Margin = new Padding(10, 7, 0, 0) };
    private readonly DataGridView grid = new();
    private readonly Label empty = new() { Text = "Ваша очередь начинается здесь\n\nДобавьте программу, выберите её монитор и задержку.\nМожно перетащить сюда файлы .exe или ярлыки.", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Muted, BackColor = Theme.Surface };
    private readonly Label summary = Theme.Label("0 программ", true);
    private readonly Label fileLabel = Theme.Label("Новая конфигурация · ещё не сохранена", true);
    private readonly Label status = Theme.Label("Готово к настройке", true);
    private readonly RichTextBox log = new() { Dock = DockStyle.Fill, BackColor = Theme.Surface, ForeColor = Theme.Muted, ReadOnly = true, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9), DetectUrls = false };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Bottom, Height = 3, Style = ProgressBarStyle.Continuous };
    private readonly Button runButton;
    private readonly Button stopButton;
    private readonly List<Control> editingControls = [];
    private readonly Dictionary<int, (string Text, Color Color)> statuses = [];
    private string lastLog = "";

    public MainForm(string? initialProfile, bool autoRun, bool closeAfter)
    {
        this.autoRun = autoRun;
        this.closeAfter = closeAfter;
        var parent = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName;
        root = parent is not null && Directory.Exists(Path.Combine(parent, "profiles")) ? parent : AppContext.BaseDirectory;
        Theme.Style(this);
        Text = "LaunchQueue — очередь запуска";
        ClientSize = new Size(1340, 850);
        MinimumSize = new Size(1180, 760);
        KeyPreview = true;
        AllowDrop = true;
        runButton = Theme.Button("Запустить очередь", async (_, _) => await StartQueueAsync(), true, 210);
        stopButton = Theme.Button("Остановить", (_, _) => running?.Cancel(), width: 125);
        stopButton.Enabled = false;
        BuildLayout();
        defaultDelay.ValueChanged += (_, _) => { if (!updating) { profile.DefaultDelaySeconds = defaultDelay.Value; MarkDirty(); RefreshRows(); } };
        windowWait.Minimum = 1;
        windowWait.ValueChanged += (_, _) => { if (!updating) { profile.WindowWaitSeconds = (int)windowWait.Value; MarkDirty(); } };
        continueOnError.CheckedChanged += (_, _) => { if (!updating) { profile.ContinueOnError = continueOnError.Checked; MarkDirty(); } };
        profileName.TextChanged += (_, _) => { if (!updating) { profile.Name = profileName.Text; MarkDirty(); } };
        DragEnter += (_, e) => { if (running is null && e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += (_, e) =>
        {
            if (running is not null || e.Data?.GetData(DataFormats.FileDrop) is not string[] paths) return;
            var items = paths.Where(File.Exists).Where(p => new[] { ".exe", ".lnk", ".bat", ".cmd", ".appref-ms" }.Contains(Path.GetExtension(p).ToLowerInvariant()))
                .Select(p => new QueueItem { Name = Path.GetFileNameWithoutExtension(p), FilePath = p }).ToList();
            profile.Items.AddRange(items);
            if (items.Count > 0) { MarkDirty(); RefreshRows(profile.Items.Count - 1); }
        };
        FormClosing += OnClosing;
        KeyDown += (_, e) =>
        {
            if (running is not null) return;
            if (e.Control && e.KeyCode == Keys.S) { SaveProfile(false); e.SuppressKeyPress = true; }
            if (e.Control && e.KeyCode == Keys.O) { OpenProfile(); e.SuppressKeyPress = true; }
            if (e.KeyCode == Keys.F5) { _ = StartQueueAsync(); e.SuppressKeyPress = true; }
        };
        if (initialProfile is not null) LoadProfile(initialProfile);
        else
        {
            var recent = Path.Combine(root, "last-profile.txt");
            try
            {
                if (File.Exists(recent))
                {
                    var path = File.ReadAllText(recent).Trim();
                    if (File.Exists(path)) LoadProfile(path);
                }
            }
            catch (Exception ex) { AppendLog($"Последний профиль не открыт: {ex.Message}", Theme.Warning); }
        }
        SyncProfile();
        Shown += async (_, _) => { if (this.autoRun) await StartQueueAsync(); };
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26, 20, 26, 20), ColumnCount = 1, RowCount = 9 };
        foreach (var height in new[] { 72, 48, 46 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        foreach (var height in new[] { 48, 90, 63, 28, 132 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        var header = new Panel { Dock = DockStyle.Fill };
        header.Controls.Add(new Label { Text = "LaunchQueue", Location = new Point(0, 0), AutoSize = true, Font = new Font("Segoe UI Semibold", 25), ForeColor = Theme.Text });
        header.Controls.Add(new Label { Text = "ВАШИ ПРОГРАММЫ. В НУЖНОМ ПОРЯДКЕ. НА СВОИХ ЭКРАНАХ.", Location = new Point(3, 48), AutoSize = true, Font = new Font("Segoe UI", 8), ForeColor = Theme.Muted });
        var monitors = Theme.Button($"Мониторы: {Screen.AllScreens.Length}", (_, _) => ShowMonitors(), width: 140);
        monitors.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        header.Controls.Add(monitors);
        header.Resize += (_, _) => monitors.Location = new Point(header.Width - monitors.Width, 12);
        layout.Controls.Add(header, 0, 0);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 3, 0, 0) };
        toolbar.Controls.Add(EditButton("Новая", (_, _) => NewProfile(), 90));
        toolbar.Controls.Add(EditButton("Открыть…", (_, _) => OpenProfile(), 110));
        toolbar.Controls.Add(EditButton("Сохранить", (_, _) => SaveProfile(false), 120));
        toolbar.Controls.Add(EditButton("Сохранить как…", (_, _) => SaveProfile(true), 155));
        toolbar.Controls.Add(EditButton("Создать ярлык…", (_, _) => CreateShortcut(), 170));
        toolbar.Controls.Add(Theme.Button("Как пользоваться", (_, _) => Help(), width: 170));
        layout.Controls.Add(toolbar, 0, 1);
        var titleRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
        titleRow.ColumnStyles.Add(new(SizeType.Absolute, 90));
        titleRow.ColumnStyles.Add(new(SizeType.Percent, 100));
        titleRow.ColumnStyles.Add(new(SizeType.Absolute, 260));
        titleRow.Controls.Add(Theme.Label("Очередь", true), 0, 0);
        titleRow.Controls.Add(profileName, 1, 0);
        summary.Dock = DockStyle.Fill;
        summary.TextAlign = ContentAlignment.MiddleRight;
        summary.Margin = new Padding(5, 0, 0, 0);
        titleRow.Controls.Add(summary, 2, 0);
        editingControls.Add(profileName);
        layout.Controls.Add(titleRow, 0, 2);
        ConfigureGrid();
        var gridPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(1) };
        gridPanel.Controls.Add(grid);
        gridPanel.Controls.Add(empty);
        empty.BringToFront();
        layout.Controls.Add(gridPanel, 0, 3);
        var rowTools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 7, 0, 0) };
        rowTools.Controls.Add(EditButton("+ Добавить программу", (_, _) => AddItem(), 215));
        rowTools.Controls.Add(EditButton("Из меню «Пуск»", (_, _) => AddInstalled(), 160));
        rowTools.Controls.Add(EditButton("Изменить", (_, _) => EditItem(), 108));
        rowTools.Controls.Add(EditButton("Выше", (_, _) => MoveItem(-1), 82));
        rowTools.Controls.Add(EditButton("Ниже", (_, _) => MoveItem(1), 82));
        rowTools.Controls.Add(EditButton("Удалить", (_, _) => DeleteItem(), 100));
        layout.Controls.Add(rowTools, 0, 4);
        var settings = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(0, 9, 0, 0) };
        settings.RowStyles.Add(new(SizeType.Absolute, 37));
        settings.RowStyles.Add(new(SizeType.Percent, 100));
        var values = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        values.Controls.Add(Theme.Label("Интервал по умолчанию, с", true));
        defaultDelay.AccessibleName = "Интервал по умолчанию в секундах";
        values.Controls.Add(defaultDelay);
        values.Controls.Add(Theme.Label("Ожидание окна, с", true));
        windowWait.AccessibleName = "Ожидание окна в секундах";
        values.Controls.Add(windowWait);
        values.Controls.Add(continueOnError);
        settings.Controls.Add(values, 0, 0);
        settings.Controls.Add(Theme.Label("Первый шаг — сразу. Закрытие окна добавляет ожидание перед следующим шагом. Двойной щелчок по строке — настройка.", true), 0, 1);
        editingControls.AddRange([defaultDelay, windowWait, continueOnError]);
        layout.Controls.Add(settings, 0, 5);
        var launchRow = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 9, 0, 0) };
        var launchButtons = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 355, WrapContents = false };
        launchButtons.Controls.Add(runButton);
        launchButtons.Controls.Add(stopButton);
        launchRow.Controls.Add(launchButtons);
        status.AutoSize = false;
        status.Dock = DockStyle.Fill;
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.Padding = new Padding(14, 0, 0, 8);
        launchRow.Controls.Add(status);
        status.BringToFront();
        launchRow.Controls.Add(progress);
        layout.Controls.Add(launchRow, 0, 6);
        var logHeading = new Panel { Dock = DockStyle.Fill };
        logHeading.Controls.Add(Theme.Label("ЖУРНАЛ ЗАПУСКА", true));
        fileLabel.AutoSize = false;
        fileLabel.Dock = DockStyle.Right;
        fileLabel.Width = 850;
        fileLabel.TextAlign = ContentAlignment.MiddleRight;
        fileLabel.AutoEllipsis = true;
        logHeading.Controls.Add(fileLabel);
        layout.Controls.Add(logHeading, 0, 7);
        layout.Controls.Add(log, 0, 8);
        Controls.Add(layout);
    }

    private Button EditButton(string text, EventHandler action, int width)
    {
        var button = Theme.Button(text, action, width: width);
        editingControls.Add(button);
        return button;
    }

    private void ConfigureGrid()
    {
        grid.Dock = DockStyle.Fill;
        grid.BackgroundColor = Theme.Surface;
        grid.BorderStyle = BorderStyle.None;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersHeight = 42;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersDefaultCellStyle = new() { BackColor = Theme.Input, ForeColor = Theme.Muted, SelectionBackColor = Theme.Input, Padding = new Padding(6, 0, 0, 0), Font = new Font("Segoe UI", 9) };
        grid.DefaultCellStyle = new() { BackColor = Theme.Surface, ForeColor = Theme.Text, SelectionBackColor = Color.FromArgb(45, 68, 76), SelectionForeColor = Theme.Text, Padding = new Padding(6, 0, 0, 0) };
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(28, 35, 47);
        grid.RowTemplate.Height = 46;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Theme.Border;
        grid.AccessibleName = "Очередь программ";
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Enabled", HeaderText = "Вкл.", FillWeight = 5, MinimumWidth = 48 });
        foreach (var column in new[] { ("Number", "№", 4f), ("Name", "Программа", 22f), ("Delay", "Интервал", 13f), ("Monitor", "Монитор", 24f), ("Mode", "Окно", 11f), ("Close", "Закрыть окно", 13f), ("Status", "Состояние", 17f) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = column.Item1, HeaderText = column.Item2, FillWeight = column.Item3, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex > 0 && running is null) EditItem(); };
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.CellValueChanged += (_, e) =>
        {
            if (updating || running is not null || e.RowIndex < 0 || e.ColumnIndex != 0) return;
            profile.Items[e.RowIndex].Enabled = grid.Rows[e.RowIndex].Cells[0].Value is true;
            statuses.Clear();
            MarkDirty();
            var selected = e.RowIndex;
            BeginInvoke(() => RefreshRows(selected));
        };
    }

    private int SelectedIndex => grid.CurrentRow?.Index ?? -1;
    private void MarkDirty() { dirty = true; UpdateTitle(); }
    private void UpdateTitle()
    {
        Text = $"{(dirty ? "* " : "")}{profile.Name} — LaunchQueue";
        fileLabel.Text = (profilePath ?? "Новая конфигурация") + (dirty ? " · есть изменения" : "");
    }
    private void UpdateSummary() => summary.Text = $"Включено: {profile.Items.Count(x => x.Enabled)} из {profile.Items.Count} · экранов: {Screen.AllScreens.Length}";

    private void SyncProfile()
    {
        updating = true;
        profileName.Text = profile.Name;
        defaultDelay.Value = profile.DefaultDelaySeconds;
        windowWait.Value = profile.WindowWaitSeconds;
        continueOnError.Checked = profile.ContinueOnError;
        updating = false;
        statuses.Clear();
        RefreshRows();
        UpdateTitle();
    }

    private void RefreshRows(int selected = -1)
    {
        if (selected < 0) selected = SelectedIndex;
        updating = true;
        grid.Rows.Clear();
        var first = profile.Items.FindIndex(i => i.Enabled);
        for (var i = 0; i < profile.Items.Count; i++)
        {
            var item = profile.Items[i];
            var interval = !item.Enabled ? "—" : i == first ? "Сразу" : $"{item.DelaySeconds ?? profile.DefaultDelaySeconds:0.#} с{(item.DelaySeconds is null ? " · общий" : "")}";
            var previous = profile.Items.Take(i).LastOrDefault(x => x.Enabled);
            if (item.Enabled && previous is { CloseWindowAfterLaunch: true })
                interval = $"{item.DelaySeconds ?? profile.DefaultDelaySeconds:0.#} с + {previous.CloseDelaySeconds:0.#} с";
            var row = grid.Rows[grid.Rows.Add(item.Enabled, i + 1, item.Name, interval, MonitorChoice.LabelFor(item.Monitor),
                item.WindowMode switch { WindowMode.Maximized => "Развернуть", WindowMode.Minimized => "Свернуть", _ => "Обычное" },
                item.CloseWindowAfterLaunch ? $"Через {item.CloseDelaySeconds:0.#} с" : "Нет",
                statuses.TryGetValue(i, out var s) ? s.Text : item.Enabled ? "Готово к запуску" : "Пропустить")];
            row.Cells[2].ToolTipText = item.FilePath + (item.Arguments.Length > 0 ? "\n" + item.Arguments : "");
            row.Cells["Delay"].ToolTipText = previous is { CloseWindowAfterLaunch: true }
                ? "Обычный интервал начинается после закрытия окна предыдущего включённого шага. Поиск окна и подтверждение закрытия могут добавить время."
                : "Минимальный интервал от команды запуска предыдущего включённого шага.";
            if (!item.Enabled) row.DefaultCellStyle.ForeColor = Theme.Muted;
            if (statuses.TryGetValue(i, out s)) row.Cells["Status"].Style.ForeColor = s.Color;
        }
        if (grid.Rows.Count > 0)
        {
            var index = Math.Clamp(selected, 0, grid.Rows.Count - 1);
            grid.CurrentCell = grid.Rows[index].Cells[2];
            grid.Rows[index].Selected = true;
        }
        empty.Visible = profile.Items.Count == 0;
        updating = false;
        UpdateSummary();
    }

    private void AddItem()
    {
        using var dialog = new ItemDialog(new QueueItem(), profile.DefaultDelaySeconds, true);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        profile.Items.Add(dialog.Item);
        statuses.Clear();
        MarkDirty();
        RefreshRows(profile.Items.Count - 1);
    }
    private void AddInstalled()
    {
        try
        {
            using var dialog = new InstalledAppsDialog();
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            profile.Items.AddRange(dialog.SelectedItems);
            statuses.Clear(); MarkDirty(); RefreshRows(profile.Items.Count - 1);
        }
        catch (Exception ex) { Error(ex.Message); }
    }
    private void EditItem()
    {
        var index = SelectedIndex;
        if (index < 0) return;
        using var dialog = new ItemDialog(profile.Items[index], profile.DefaultDelaySeconds, false);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        profile.Items[index] = dialog.Item;
        statuses.Clear(); MarkDirty(); RefreshRows(index);
    }
    private void MoveItem(int direction)
    {
        var index = SelectedIndex;
        var target = index + direction;
        if (index < 0 || target < 0 || target >= profile.Items.Count) return;
        (profile.Items[index], profile.Items[target]) = (profile.Items[target], profile.Items[index]);
        statuses.Clear(); MarkDirty(); RefreshRows(target);
    }
    private void DeleteItem()
    {
        var index = SelectedIndex;
        if (index < 0) return;
        if (MessageBox.Show(this, $"Убрать «{profile.Items[index].Name}» из очереди? Сама программа не удаляется.", "Удалить шаг", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        profile.Items.RemoveAt(index);
        statuses.Clear(); MarkDirty(); RefreshRows(index);
    }
    private bool ConfirmDiscard()
    {
        if (!dirty) return true;
        var answer = MessageBox.Show(this, "Сохранить изменения в текущей очереди?", "Несохранённая конфигурация", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return answer == DialogResult.No || answer == DialogResult.Yes && SaveProfile(false);
    }
    private void NewProfile()
    {
        if (!ConfirmDiscard()) return;
        profile = new(); profilePath = null; dirty = false;
        SyncProfile();
        status.Text = "Новая очередь";
    }
    private void OpenProfile()
    {
        if (!ConfirmDiscard()) return;
        using var dialog = new OpenFileDialog { Title = "Открыть очередь", Filter = "Профиль LaunchQueue|*.json", InitialDirectory = ProfilesDirectory() };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { LoadProfile(dialog.FileName); SyncProfile(); RememberProfile(); }
        catch (Exception ex) { Error(ex.Message); }
    }
    private void LoadProfile(string path)
    {
        var loaded = ProfileStore.Load(path);
        profile = loaded;
        profilePath = Path.GetFullPath(path);
        dirty = false;
    }
    private string ProfilesDirectory()
    {
        var directory = Path.Combine(root, "profiles");
        Directory.CreateDirectory(directory);
        return directory;
    }
    private bool SaveProfile(bool saveAs)
    {
        try
        {
            ProfileStore.Validate(profile);
            var path = profilePath;
            if (saveAs || path is null)
            {
                using var dialog = new SaveFileDialog
                {
                    Title = "Сохранить очередь", Filter = "Профиль LaunchQueue|*.json", DefaultExt = "json",
                    InitialDirectory = path is null ? ProfilesDirectory() : Path.GetDirectoryName(path),
                    FileName = path is null ? "Моя очередь.json" : Path.GetFileName(path), OverwritePrompt = true
                };
                if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                path = dialog.FileName;
            }
            ProfileStore.Save(path, profile);
            profilePath = path;
            dirty = false;
            RememberProfile();
            UpdateTitle();
            status.Text = "Конфигурация сохранена";
            return true;
        }
        catch (Exception ex) { Error(ex.Message); return false; }
    }
    private void RememberProfile()
    {
        try { File.WriteAllText(Path.Combine(root, "last-profile.txt"), profilePath); }
        catch (IOException ex) { AppendLog($"Не удалось запомнить последний профиль: {ex.Message}", Theme.Warning); }
        catch (UnauthorizedAccessException ex) { AppendLog($"Не удалось запомнить последний профиль: {ex.Message}", Theme.Warning); }
    }
    private void CreateShortcut()
    {
        if (!SaveProfile(false)) return;
        using var dialog = new SaveFileDialog
        {
            Title = "Куда сохранить ярлык запуска?", Filter = "Ярлык Windows|*.lnk", DefaultExt = "lnk",
            InitialDirectory = root, FileName = "Запустить " + Path.GetFileNameWithoutExtension(profilePath) + ".lnk", OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            ShellLinks.Create(dialog.FileName, profilePath!);
            status.Text = "Ярлык создан";
            AppendLog($"Ярлык: {dialog.FileName}. После успешного запуска окно очереди закроется; при ошибках останется журнал.", Theme.Accent);
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    private async Task StartQueueAsync()
    {
        if (running is not null) return;
        if (!profile.Items.Any(x => x.Enabled)) { Error("Добавьте и включите хотя бы одну программу."); return; }
        if ((dirty || profilePath is null) && !SaveProfile(false)) return;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(profilePath!).ToUpperInvariant())));
        using var mutex = new Mutex(false, @"Local\LaunchQueue-" + key);
        bool acquired;
        try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { Error("Эта очередь уже запускается в другом окне LaunchQueue."); return; }
        running = new CancellationTokenSource();
        SetRunning(true);
        log.Clear(); lastLog = ""; statuses.Clear(); RefreshRows();
        AppendLog($"Очередь «{profile.Name}» · {DateTime.Now:dd.MM.yyyy HH:mm:ss}", Theme.Text);
        RunResult? result = null;
        try
        {
            var runner = new QueueRunner(message =>
            {
                var color = message.Error ? Theme.Error : message.Warning ? Theme.Warning : Theme.Accent;
                if (message.ItemIndex >= 0)
                {
                    statuses[message.ItemIndex] = (message.Status, color);
                    var row = grid.Rows[message.ItemIndex];
                    row.Cells["Status"].Value = message.Status;
                    row.Cells["Status"].Style.ForeColor = color;
                }
                status.Text = message.Detail.Length > 100 ? message.Detail[..97] + "…" : message.Detail;
                if (message.Detail != lastLog) { AppendLog(message.Detail, color); lastLog = message.Detail; }
            });
            result = await runner.RunAsync(profile, running.Token);
            status.Text = result.Cancelled ? "Остановлено · дальнейшие действия отменены"
                : $"Завершено · запущено: {result.Started} · ошибок: {result.Failed} · замечаний: {result.Warnings}";
            AppendLog(status.Text, result.Failed > 0 || result.Warnings > 0 ? Theme.Warning : Theme.Accent);
        }
        catch (Exception ex) { AppendLog(ex.Message, Theme.Error); status.Text = "Не удалось выполнить очередь"; }
        finally
        {
            running.Dispose(); running = null;
            mutex.ReleaseMutex();
            SetRunning(false);
            foreach (DataGridViewRow row in grid.Rows)
                if (row.Cells["Status"].Value?.ToString() is "Ожидание окна" or "Запуск…" or "Закрытие отправлено"
                    || row.Cells["Status"].Value?.ToString()?.StartsWith("Через ") == true
                    || row.Cells["Status"].Value?.ToString()?.StartsWith("До закрытия ") == true)
                    row.Cells["Status"].Value = "Остановлено";
            SaveLog();
        }
        if (closeAfter && result is { Cancelled: false, Failed: 0, Warnings: 0 }) Close();
    }
    private void SetRunning(bool value)
    {
        foreach (var control in editingControls) control.Enabled = !value;
        grid.ReadOnly = value;
        runButton.Enabled = !value;
        stopButton.Enabled = value;
        progress.Style = value ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
    }
    private void AppendLog(string text, Color color)
    {
        log.SelectionStart = log.TextLength;
        log.SelectionColor = color;
        log.AppendText($"{DateTime.Now:HH:mm:ss}  {text}{Environment.NewLine}");
        log.ScrollToCaret();
    }
    private void SaveLog()
    {
        try
        {
            var directory = Path.Combine(root, "logs");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"queue-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log"), log.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { AppendLog($"Журнал не сохранён на диск: {ex.Message}", Theme.Warning); }
    }
    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (running is not null)
        {
            if (MessageBox.Show(this, "Остановить очередь? Дальнейшие запуски и закрытия отменятся. Уже отправленное закрытие отменить нельзя.", "Очередь выполняется", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                running.Cancel();
            e.Cancel = true;
            return;
        }
        if (!ConfirmDiscard()) e.Cancel = true;
    }
    private void ShowMonitors()
    {
        var screens = MonitorChoice.GetAll().Skip(1).Select(m => m.Label);
        MessageBox.Show(this, string.Join("\n", screens) + "\n\nНомера соответствуют системным именам DISPLAY. Если нужный монитор отключён, окно будет перенесено на основной.", "Подключённые мониторы", MessageBoxButtons.OK, MessageBoxIcon.Information);
        RefreshRows();
    }
    private void Help() => MessageBox.Show(this,
        "1. Добавьте программы через «Обзор», из меню «Пуск» или перетаскиванием файлов.\n" +
        "2. Двойным щелчком откройте шаг: задайте монитор, задержку, аргументы и режим окна.\n" +
        "3. Кнопками «Выше» и «Ниже» измените порядок. Флажок временно исключает шаг.\n" +
        "4. Сохраните JSON-профиль, затем нажмите «Запустить очередь» (F5).\n" +
        "5. «Создать ярлык» позволяет запускать именно этот профиль одним двойным щелчком.\n\n" +
        "Задержка — минимальный интервал между командами запуска, а не ожидание полной загрузки программы. Первый включённый шаг запускается сразу.\n\n" +
        "В настройке шага можно включить «Закрыть окно через». Таймер начинается после обнаружения и настройки окна. Следующий шаг ждёт закрытия и затем свой обычный интервал: например, 2 с до закрытия + 3 с интервал, плюс время поиска окна. Подтверждение закрытия может занять до 2 с.\n\n" +
        "Закрытие — обычный крестик, НЕ принудительное завершение процесса. Для работы Telegram / Discord в фоне включите в самой программе сворачивание в трей при закрытии; иначе программа может завершиться. Диалоги сохранения не подтверждаются автоматически.\n\n" +
        "Окно сначала создаётся программой и затем переносится. Программы с повышенными правами могут требовать запуск LaunchQueue от администратора. Store-приложения, игровые лаунчеры, splash-экраны и эксклюзивный полный экран могут не подчиняться переносу. Для однокопийных программ может использоваться уже открытое окно.\n\n" +
        "Остановка прекращает дальнейшие запуски, переносы и закрытия. Уже отправленную команду закрытия отменить нельзя. Журналы находятся в папке logs. Открывайте профили только из доверенных источников: они содержат команды запуска.\n\n" +
        "Ctrl+S — сохранить; Ctrl+O — открыть. Для переноса на другой компьютер копируйте всю папку проекта; пути к программам может потребоваться обновить.",
        "LaunchQueue — краткая инструкция", MessageBoxButtons.OK, MessageBoxIcon.Information);
    private void Error(string text) => MessageBox.Show(this, text, "LaunchQueue", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
