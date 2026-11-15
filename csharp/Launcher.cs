using System.Diagnostics;

namespace DiskSpaceMap;

/// <summary>Opens shells and the system file manager for a folder, on Windows, macOS and Linux.</summary>
internal static class Launcher
{
    public sealed record Shell(string Id, string Name);

    private static readonly string[] LinuxTerminals = ["x-terminal-emulator", "gnome-terminal", "konsole", "xfce4-terminal", "kitty", "alacritty", "xterm"];

    public static string OsName => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "mac" : "linux";

    /// <summary>The menu text for "show this in the system file manager".</summary>
    public static string FileManagerLabel => OperatingSystem.IsWindows() ? "Open in Explorer" : OperatingSystem.IsMacOS() ? "Reveal in Finder" : "Open in file manager";

    /// <summary>The shells that can actually be launched on this machine.</summary>
    public static IReadOnlyList<Shell> AvailableShells()
    {
        var shells = new List<Shell>();
        if (OperatingSystem.IsWindows())
        {
            shells.Add(new Shell("cmd", "Command Prompt"));
            shells.Add(new Shell("powershell", "PowerShell"));
            if (FindGitBash() is not null) shells.Add(new Shell("gitbash", "Git Bash"));
        }
        else if (OperatingSystem.IsMacOS()) shells.Add(new Shell("terminal", "Terminal"));
        else if (FindLinuxTerminal() is not null) shells.Add(new Shell("terminal", "Terminal"));
        return shells;
    }

    /// <summary>Starts the named shell in the folder. Returns an error message, or null on success.</summary>
    public static string? LaunchShell(string shellId, string folder)
    {
        if (!Directory.Exists(folder)) return "That folder no longer exists.";
        if (!AvailableShells().Any(s => s.Id == shellId)) return "That shell is not available.";

        ProcessStartInfo? start = shellId switch
        {
            "cmd" => new ProcessStartInfo("cmd.exe") { UseShellExecute = true, WorkingDirectory = folder },
            "powershell" => new ProcessStartInfo(FindOnPath("pwsh.exe") ?? "powershell.exe", "-NoExit") { UseShellExecute = true, WorkingDirectory = folder },
            "gitbash" => FindGitBash() is { } gitBash ? new ProcessStartInfo(gitBash) { UseShellExecute = true, WorkingDirectory = folder } : null,
            "terminal" when OperatingSystem.IsMacOS() => new ProcessStartInfo("open") { ArgumentList = { "-a", "Terminal", folder } },
            "terminal" => FindLinuxTerminal() is { } terminal ? new ProcessStartInfo(terminal) { WorkingDirectory = folder } : null,
            _ => null,
        };
        return Start(start, "Could not start the shell.");
    }

    /// <summary>Shows the file or folder in Explorer / Finder / the default file manager.</summary>
    public static string? Reveal(string path)
    {
        bool isFile = File.Exists(path);
        if (!isFile && !Directory.Exists(path)) return "That item no longer exists.";

        ProcessStartInfo start;
        if (OperatingSystem.IsWindows())
            start = isFile ? new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") : new ProcessStartInfo(path) { UseShellExecute = true };
        else if (OperatingSystem.IsMacOS())
            start = new ProcessStartInfo("open") { ArgumentList = { isFile ? "-R" : path } }.WithArgumentIf(isFile, path);
        else
            start = new ProcessStartInfo("xdg-open") { ArgumentList = { isFile ? Path.GetDirectoryName(path) ?? path : path } };
        return Start(start, "Could not open the file manager.");
    }

    private static ProcessStartInfo WithArgumentIf(this ProcessStartInfo info, bool condition, string argument)
    {
        if (condition) info.ArgumentList.Add(argument);
        return info;
    }

    private static string? Start(ProcessStartInfo? start, string failure)
    {
        if (start is null) return failure;
        try
        {
            using Process? process = Process.Start(start);
            return process is null ? failure : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return failure; }
    }

    private static string? FindLinuxTerminal() => LinuxTerminals.Select(FindOnPath).FirstOrDefault(p => p is not null);

    private static string? FindGitBash()
    {
        if (FindOnPath("git.exe") is { } git && Path.GetDirectoryName(git) is { } bin && Path.GetDirectoryName(bin) is { } installDir)
        {
            string candidate = Path.Combine(installDir, "git-bash.exe");
            if (File.Exists(candidate)) return candidate;
        }
        foreach (string folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") })
        {
            if (string.IsNullOrEmpty(folder)) continue;
            string candidate = Path.Combine(folder, "Git", "git-bash.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? FindOnPath(string executable)
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(directory.Trim('"'), executable);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}
