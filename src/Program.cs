namespace LaunchQueue;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "LaunchQueue — ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        try
        {
            string? profile = null;
            var run = false;
            var closeAfter = false;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--run": run = true; break;
                    case "--close-after": closeAfter = true; break;
                    case "--profile":
                        if (++i >= args.Length) throw new ArgumentException("После --profile укажите путь к JSON-профилю.");
                        profile = Path.GetFullPath(args[i]);
                        break;
                    default: throw new ArgumentException($"Неизвестный параметр: {args[i]}");
                }
            }
            if (run && profile is null) throw new ArgumentException("Для --run требуется --profile с явным путём к профилю.");
            Application.Run(new MainForm(profile, run, closeAfter));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "LaunchQueue — не удалось открыть", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
