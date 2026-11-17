using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LaunchQueue;

public static class ShellLinks
{
    public static string ResolveTarget(string file)
    {
        if (!Path.GetExtension(file).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return file;
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            shortcut = ((dynamic)shell!).CreateShortcut(file);
            string target = ((dynamic)shortcut).TargetPath;
            return Environment.ExpandEnvironmentVariables(target);
        }
        catch (COMException) { return ""; }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }

    public static void Create(string file, string profile)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            shortcut = ((dynamic)shell!).CreateShortcut(file);
            dynamic link = shortcut;
            link.TargetPath = Environment.ProcessPath!;
            link.Arguments = $"--profile {QuoteArgument(Path.GetFullPath(profile))} --run --close-after";
            link.WorkingDirectory = AppContext.BaseDirectory;
            link.Description = "Запустить сохранённую очередь LaunchQueue";
            link.IconLocation = Environment.ProcessPath + ",0";
            link.Save();
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }

    public static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            result.Append(c);
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}

internal static class NativeWindows
{
    private delegate bool EnumWindowsProc(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rectangle);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint DefaultHeap;
        public uint ModuleId, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    internal sealed record WindowInfo(nint Handle, uint ProcessId);

    public static List<WindowInfo> VisibleWindows()
    {
        var result = new List<WindowInfo>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindow(window, 4) != 0) return true;
            if (DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (!GetWindowRect(window, out var rect) || rect.Right - rect.Left < 80 || rect.Bottom - rect.Top < 50) return true;
            GetWindowThreadProcessId(window, out var pid);
            if (pid != Environment.ProcessId) result.Add(new(window, pid));
            return true;
        }, 0);
        return result;
    }

    public static void ExpandProcessFamily(HashSet<uint> family)
    {
        if (family.Count == 0) return;
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == -1) return;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = "" };
            var pairs = new List<(uint Id, uint Parent)>();
            if (Process32FirstW(snapshot, ref entry))
                do { pairs.Add((entry.ProcessId, entry.ParentProcessId)); } while (Process32NextW(snapshot, ref entry));
            bool changed;
            do
            {
                changed = false;
                foreach (var pair in pairs)
                    if (family.Contains(pair.Parent)) changed |= family.Add(pair.Id);
            } while (changed);
        }
        finally { CloseHandle(snapshot); }
    }

    public static bool MatchesExecutable(uint pid, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        { return false; }
    }

    public static bool IsOpen(WindowInfo window)
    {
        GetWindowThreadProcessId(window.Handle, out var pid);
        return pid == window.ProcessId && IsWindowVisible(window.Handle);
    }

    public static bool RequestClose(WindowInfo window)
    {
        // Recheck ownership because the handle may have been reused during the delay.
        return IsOpen(window) && PostMessageW(window.Handle, 0x0010, 0, 0);
    }

    public static async Task<bool> PlaceAsync(nint handle, Screen? screen, WindowMode mode, CancellationToken cancellation)
    {
        if (screen is not null || mode != WindowMode.Normal)
        {
            ShowWindowAsync(handle, 9);
            await Task.Delay(120, cancellation);
        }
        if (screen is not null)
        {
            if (!GetWindowRect(handle, out var rect)) return false;
            var area = screen.WorkingArea;
            var width = Math.Min(Math.Max(rect.Right - rect.Left, 160), area.Width);
            var height = Math.Min(Math.Max(rect.Bottom - rect.Top, 100), area.Height);
            var x = area.Left + (area.Width - width) / 2;
            var y = area.Top + (area.Height - height) / 2;
            if (!SetWindowPos(handle, 0, x, y, width, height, 0x0014)) return false;
        }
        if (mode == WindowMode.Maximized) ShowWindowAsync(handle, 3);
        if (mode == WindowMode.Minimized) ShowWindowAsync(handle, 6);
        await Task.Delay(180, cancellation);
        if (screen is null || IsIconic(handle)) return true;
        return Screen.FromHandle(handle).DeviceName == screen.DeviceName;
    }
}
