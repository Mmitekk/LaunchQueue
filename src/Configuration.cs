using System.Text.Json;
using System.Text.Json.Serialization;

namespace LaunchQueue;

public enum WindowMode { Normal, Maximized, Minimized }

public sealed class QueueItem
{
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public decimal? DelaySeconds { get; set; }
    public string Monitor { get; set; } = "";
    public WindowMode WindowMode { get; set; }
    public bool CloseWindowAfterLaunch { get; set; }
    public decimal CloseDelaySeconds { get; set; } = 2;
    public QueueItem Copy() => (QueueItem)MemberwiseClone();
}

public sealed class QueueProfile
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Моя очередь";
    public decimal DefaultDelaySeconds { get; set; } = 3;
    public int WindowWaitSeconds { get; set; } = 30;
    public bool ContinueOnError { get; set; } = true;
    public List<QueueItem> Items { get; set; } = [];
}

public static class ProfileStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static QueueProfile Load(string path)
    {
        var profile = JsonSerializer.Deserialize<QueueProfile>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Файл не содержит профиль очереди.");
        Validate(profile);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach (var item in profile.Items)
        {
            item.FilePath = ResolvePath(item.FilePath, directory);
            if (!string.IsNullOrWhiteSpace(item.WorkingDirectory))
                item.WorkingDirectory = ResolvePath(item.WorkingDirectory, directory);
        }
        return profile;
    }

    public static void Save(string path, QueueProfile profile)
    {
        Validate(profile);
        var fullPath = Path.GetFullPath(path);
        var temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(profile, JsonOptions));
            File.Move(temp, fullPath, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static string ResolvePath(string path, string directory) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')), directory);

    public static void Validate(QueueProfile profile)
    {
        if (profile.Version != 1) throw new InvalidDataException("Неизвестная версия профиля.");
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new InvalidDataException("Укажите название очереди.");
        if (profile.DefaultDelaySeconds < 0 || profile.DefaultDelaySeconds > 3600)
            throw new InvalidDataException("Задержка должна быть от 0 до 3600 секунд.");
        if (profile.WindowWaitSeconds < 1 || profile.WindowWaitSeconds > 300)
            throw new InvalidDataException("Ожидание окна должно быть от 1 до 300 секунд.");
        if (profile.Items is null) throw new InvalidDataException("В профиле отсутствует список программ.");
        foreach (var item in profile.Items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.FilePath))
                throw new InvalidDataException("У одной из программ не указан путь.");
            if (item.Name is null || item.Arguments is null || item.WorkingDirectory is null || item.Monitor is null)
                throw new InvalidDataException("Некорректные поля программы в профиле.");
            if (item.DelaySeconds is < 0 or > 3600)
                throw new InvalidDataException("Задержка программы должна быть от 0 до 3600 секунд.");
            if (item.CloseDelaySeconds is < 0 or > 3600)
                throw new InvalidDataException("Задержка закрытия окна должна быть от 0 до 3600 секунд.");
            if (!Enum.IsDefined(item.WindowMode)) throw new InvalidDataException("Неизвестный режим окна.");
            if (item.FilePath.IndexOfAny(['\r', '\n', '\0']) >= 0 || item.Arguments.Contains('\0'))
                throw new InvalidDataException("Путь или аргументы содержат недопустимые символы.");
        }
    }
}

public sealed record MonitorChoice(string Id, string Label)
{
    public override string ToString() => Label;
    public static List<MonitorChoice> GetAll()
    {
        var result = new List<MonitorChoice> { new("", "Как решит программа") };
        foreach (var screen in Screen.AllScreens)
        {
            var number = screen.DeviceName.Replace(@"\\.\DISPLAY", "");
            result.Add(new(screen.DeviceName,
                $"Монитор {number} · {screen.Bounds.Width} × {screen.Bounds.Height}{(screen.Primary ? " · основной" : "")}"));
        }
        return result;
    }
    public static string LabelFor(string id) => GetAll().FirstOrDefault(x => x.Id == id)?.Label ?? $"Отключён: {id}";
}
