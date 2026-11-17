namespace LaunchQueue;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(17, 21, 29);
    public static readonly Color Surface = Color.FromArgb(25, 31, 42);
    public static readonly Color Input = Color.FromArgb(33, 41, 55);
    public static readonly Color Border = Color.FromArgb(49, 60, 78);
    public static readonly Color Text = Color.FromArgb(234, 239, 247);
    public static readonly Color Muted = Color.FromArgb(153, 170, 193);
    public static readonly Color Accent = Color.FromArgb(139, 222, 190);
    public static readonly Color Warning = Color.FromArgb(247, 199, 114);
    public static readonly Color Error = Color.FromArgb(255, 149, 156);

    public static void Style(Form form)
    {
        form.Font = new Font("Segoe UI", 10);
        form.BackColor = Background;
        form.ForeColor = Text;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
    }
    public static Button Button(string text, EventHandler action, bool primary = false, int width = 140)
    {
        var button = new Button
        {
            Text = text, Width = width, Height = 38, FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Input, ForeColor = primary ? Background : Text,
            Cursor = Cursors.Hand, Margin = new Padding(0, 0, 8, 0),
            AccessibleName = text, UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(167, 236, 209) : Border;
        button.Click += action;
        return button;
    }
    public static TextBox TextBox(string name) => new()
    {
        BackColor = Input, ForeColor = Text, BorderStyle = BorderStyle.FixedSingle,
        Dock = DockStyle.Fill, AccessibleName = name, Margin = new Padding(0, 4, 0, 4)
    };
    public static NumericUpDown Number(decimal value, decimal maximum, int decimals = 0) => new()
    {
        Value = value, Minimum = 0, Maximum = maximum, DecimalPlaces = decimals,
        Increment = decimals > 0 ? 0.5m : 1m, Width = 82, BackColor = Input, ForeColor = Text,
        BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 5, 14, 0)
    };
    public static Label Label(string text, bool muted = false) => new()
    {
        Text = text, ForeColor = muted ? Muted : Text, AutoSize = true,
        Margin = new Padding(0, 8, 10, 0)
    };
    public static ComboBox Combo(string name) => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Input, ForeColor = Text,
        FlatStyle = FlatStyle.Flat, Dock = DockStyle.Fill, AccessibleName = name
    };
}
