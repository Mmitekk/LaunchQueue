namespace LaunchQueue;

internal sealed class ItemDialog : Form
{
    private readonly TextBox name = Theme.TextBox("Название программы");
    private readonly TextBox path = Theme.TextBox("Путь к программе");
    private readonly TextBox arguments = Theme.TextBox("Аргументы запуска");
    private readonly TextBox workingDirectory = Theme.TextBox("Рабочая папка");
    private readonly ComboBox monitor = Theme.Combo("Монитор");
    private readonly ComboBox mode = Theme.Combo("Режим окна");
    private readonly NumericUpDown delay = Theme.Number(3, 3600, 1);
    private readonly CheckBox customDelay = new() { Text = "Своя задержка перед запуском", AutoSize = true };
    private readonly CheckBox enabled = new() { Text = "Включить в очередь", Checked = true, AutoSize = true };
    private readonly CheckBox closeWindow = new() { Text = "Закрыть окно через", AutoSize = true, Margin = new Padding(0, 7, 10, 0) };
    private readonly NumericUpDown closeDelay = Theme.Number(2, 3600, 1);
    public QueueItem Item { get; private set; }

    public ItemDialog(QueueItem item, decimal defaultDelay, bool isNew)
    {
        Item = item.Copy();
        Theme.Style(this);
        Text = isNew ? "Добавить программу" : "Настройка программы";
        ClientSize = new Size(760, 670);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 2, RowCount = 12
        };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 145));
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (var i = 0; i < 10; i++) layout.RowStyles.Add(new(SizeType.Absolute, i == 0 ? 52 : 42));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 44));
        var heading = new Label { Text = isNew ? "Новый шаг очереди" : "Параметры запуска", AutoSize = true, Font = new Font(Font, FontStyle.Bold), ForeColor = Theme.Accent };
        layout.Controls.Add(heading, 0, 0);
        layout.SetColumnSpan(heading, 2);
        AddRow(layout, "Название", name, 1);
        var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        pathRow.ColumnStyles.Add(new(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new(SizeType.Absolute, 90));
        pathRow.Controls.Add(path, 0, 0);
        var browse = Theme.Button("Обзор…", (_, _) => Browse(), width: 82);
        browse.Height = 29;
        browse.Margin = new Padding(8, 2, 0, 0);
        pathRow.Controls.Add(browse, 1, 0);
        AddRow(layout, "Программа / ярлык", pathRow, 2);
        AddRow(layout, "Аргументы", arguments, 3);
        AddRow(layout, "Рабочая папка", workingDirectory, 4);
        AddRow(layout, "Монитор", monitor, 5);
        AddRow(layout, "Окно", mode, 6);
        var delayRow = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
        customDelay.Margin = new Padding(0, 7, 10, 0);
        delayRow.Controls.Add(customDelay);
        delay.Width = 73;
        delayRow.Controls.Add(delay);
        delayRow.Controls.Add(Theme.Label("с", true));
        AddRow(layout, "Интервал", delayRow, 7);
        var closeRow = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
        closeRow.Controls.Add(closeWindow);
        closeDelay.AccessibleName = "Задержка закрытия окна в секундах";
        closeRow.Controls.Add(closeDelay);
        closeRow.Controls.Add(Theme.Label("с после появления окна", true));
        AddRow(layout, "После запуска", closeRow, 8);
        layout.Controls.Add(enabled, 1, 9);
        var note = new Label
        {
            Text = "Закрытие работает как крестик, а не завершение процесса. Для Telegram / Discord включите в самой программе сворачивание в трей при закрытии — иначе она может завершиться.\n\nПри включённом закрытии следующий шаг ждёт появления окна и его закрытия, затем обычный интервал. Первый шаг запускается сразу. Аргументы надёжнее задавать для .exe, а не .lnk.",
            Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = new Font("Segoe UI", 9), Padding = new Padding(0, 4, 0, 0)
        };
        layout.Controls.Add(note, 0, 10);
        layout.SetColumnSpan(note, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = Theme.Button(isNew ? "Добавить" : "Применить", (_, _) => Commit(), true);
        var cancel = Theme.Button("Отмена", (_, _) => { DialogResult = DialogResult.Cancel; Close(); }, width: 100);
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 11);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
        name.Text = item.Name;
        path.Text = item.FilePath;
        arguments.Text = item.Arguments;
        workingDirectory.Text = item.WorkingDirectory;
        var monitors = MonitorChoice.GetAll();
        if (!monitors.Any(m => m.Id == item.Monitor)) monitors.Add(new(item.Monitor, MonitorChoice.LabelFor(item.Monitor)));
        monitor.DataSource = monitors;
        monitor.SelectedItem = monitors.First(m => m.Id == item.Monitor);
        mode.Items.AddRange(["Обычное", "На весь экран (развернуть)", "Свёрнутое"]);
        mode.SelectedIndex = (int)item.WindowMode;
        delay.Value = item.DelaySeconds ?? defaultDelay;
        customDelay.Checked = item.DelaySeconds.HasValue;
        delay.Enabled = customDelay.Checked;
        customDelay.CheckedChanged += (_, _) => delay.Enabled = customDelay.Checked;
        enabled.Checked = item.Enabled;
        closeWindow.Checked = item.CloseWindowAfterLaunch;
        closeDelay.Value = item.CloseDelaySeconds;
        closeDelay.Enabled = closeWindow.Checked;
        closeWindow.CheckedChanged += (_, _) => closeDelay.Enabled = closeWindow.Checked;
        path.Leave += (_, _) => SuggestName();
    }

    private static void AddRow(TableLayoutPanel layout, string label, Control control, int row)
    {
        layout.Controls.Add(Theme.Label(label, true), 0, row);
        layout.Controls.Add(control, 1, row);
    }

    private void Browse()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Выберите установленную программу или её ярлык",
            Filter = "Программы и ярлыки|*.exe;*.lnk;*.appref-ms;*.bat;*.cmd|Все файлы|*.*",
            DereferenceLinks = false, CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        path.Text = dialog.FileName;
        SuggestName();
    }

    private void SuggestName()
    {
        if (name.Text.Trim().Length == 0 && path.Text.Trim().Length > 0)
            name.Text = Path.GetFileNameWithoutExtension(path.Text.Trim().Trim('"'));
    }

    private void Commit()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path.Text)) throw new InvalidDataException("Укажите путь к программе или ярлыку.");
            var file = ProfileStore.ResolvePath(path.Text, AppContext.BaseDirectory);
            if (!File.Exists(file)) throw new InvalidDataException("Файл не найден. Выберите существующую программу или ярлык.");
            var directory = string.IsNullOrWhiteSpace(workingDirectory.Text) ? ""
                : ProfileStore.ResolvePath(workingDirectory.Text, Path.GetDirectoryName(file)!);
            if (directory.Length > 0 && !Directory.Exists(directory)) throw new InvalidDataException("Рабочая папка не найдена.");
            Item = new QueueItem
            {
                Name = string.IsNullOrWhiteSpace(name.Text) ? Path.GetFileNameWithoutExtension(file) : name.Text.Trim(),
                FilePath = file, Arguments = arguments.Text, WorkingDirectory = directory,
                Enabled = enabled.Checked, Monitor = ((MonitorChoice)monitor.SelectedItem!).Id,
                WindowMode = (WindowMode)mode.SelectedIndex,
                DelaySeconds = customDelay.Checked ? delay.Value : null,
                CloseWindowAfterLaunch = closeWindow.Checked,
                CloseDelaySeconds = closeDelay.Value
            };
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        { MessageBox.Show(this, ex.Message, "Проверьте параметры", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}

internal sealed class InstalledAppsDialog : Form
{
    private readonly TextBox search = Theme.TextBox("Поиск программы");
    private readonly ListBox list = new() { Dock = DockStyle.Fill, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended };
    private readonly List<AppLink> apps = [];
    private sealed record AppLink(string Name, string Path) { public override string ToString() => Name; }
    public List<QueueItem> SelectedItems { get; } = [];

    public InstalledAppsDialog()
    {
        Theme.Style(this);
        Text = "Программы из меню «Пуск» и рабочего стола";
        ClientSize = new Size(650, 550);
        MinimumSize = new Size(530, 400);
        StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), RowCount = 4, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.Absolute, 38));
        layout.RowStyles.Add(new(SizeType.Absolute, 38));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 52));
        layout.Controls.Add(Theme.Label("Найдите программу · Ctrl / Shift — выбрать несколько", true), 0, 0);
        search.PlaceholderText = "Поиск по названию…";
        layout.Controls.Add(search, 0, 1);
        layout.Controls.Add(list, 0, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 12, 0, 0) };
        var add = Theme.Button("Добавить выбранные", (_, _) => SelectApps(), true, 200);
        buttons.Controls.Add(add);
        buttons.Controls.Add(Theme.Button("Отмена", (_, _) => Close(), width: 100));
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        AcceptButton = add;
        foreach (var folder in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
        {
            var root = Environment.GetFolderPath(folder);
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                if (Path.GetExtension(file).Equals(".lnk", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(file).Equals(".appref-ms", StringComparison.OrdinalIgnoreCase))
                    apps.Add(new(Path.GetFileNameWithoutExtension(file), file));
        }
        apps.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        Filter();
        search.TextChanged += (_, _) => Filter();
        list.DoubleClick += (_, _) => SelectApps();
    }
    private void Filter()
    {
        list.BeginUpdate();
        list.Items.Clear();
        list.Items.AddRange(apps.Where(a => a.Name.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).Cast<object>().ToArray());
        list.EndUpdate();
    }
    private void SelectApps()
    {
        foreach (AppLink link in list.SelectedItems) SelectedItems.Add(new() { Name = link.Name, FilePath = link.Path });
        if (SelectedItems.Count == 0) return;
        DialogResult = DialogResult.OK;
        Close();
    }
}
