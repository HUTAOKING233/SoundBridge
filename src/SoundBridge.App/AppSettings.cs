using System;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace SoundBridge.App;

internal sealed class AppSettings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupEntry = "SoundBridge";
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoundBridge", "auto-wake.txt");
    private readonly string? _startupCommand = Environment.ProcessPath is { } path
        && !string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase)
        ? $"\"{path}\""
        : null;

    public bool AutoWakeEnabled { get; private set; }
    public bool StartWithWindows { get; private set; }

    public AppSettings()
    {
        try
        {
            if (File.Exists(_path) && bool.TryParse(File.ReadAllText(_path), out bool enabled))
                AutoWakeEnabled = enabled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log("Could not read automatic audio recovery preference: " + ex.Message);
        }

        try
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey);
            StartWithWindows = run?.GetValue(StartupEntry) is string command
                && string.Equals(command, _startupCommand, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            App.Log("Could not read Windows startup preference: " + ex.Message);
        }
    }

    public void SetStartWithWindows(bool enabled)
    {
        if (enabled)
        {
            string command = _startupCommand
                ?? throw new InvalidOperationException("请从 SoundBridge.App.exe 启动声桥后重试。");
            if (command.Length > 260)
                throw new InvalidOperationException("程序路径过长，请将声桥移到较短路径后重试。");

            using RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey);
            run.SetValue(StartupEntry, command, RegistryValueKind.String);
        }
        else
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.DeleteValue(StartupEntry, throwOnMissingValue: false);
        }

        StartWithWindows = enabled;
    }

    public void SetAutoWake(bool enabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, enabled.ToString());
        AutoWakeEnabled = enabled;
    }
}
