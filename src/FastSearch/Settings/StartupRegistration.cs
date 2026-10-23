using System;
using System.IO;
using Microsoft.Win32;

namespace FastSearch.Settings;

internal sealed record StartupEntry(object? Value, RegistryValueKind Kind = RegistryValueKind.String);
internal interface IStartupRegistration
{
    StartupEntry Read();
    void Write(StartupEntry entry);
    StartupEntry EnabledEntry { get; }
}

internal sealed class StartupRegistration : IStartupRegistration
{
    private const string Subkey = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "FastSearch";
    public StartupEntry EnabledEntry { get; }
    public StartupRegistration(string executable)
    {
        string path = Path.GetFullPath(executable);
        if (path.Contains('"')) throw new ArgumentException("実行ファイルのパスが不正です。");
        string command = $"\"{path}\" --background";
        EnabledEntry = new(command);
    }
    public StartupEntry Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Subkey);
        object? value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return new(value, value is null ? RegistryValueKind.String : key!.GetValueKind(ValueName));
    }
    public void Write(StartupEntry entry)
    {
        if (entry.Value is string command && command.Length > 260)
            throw new ArgumentException("自動起動の登録には、より短い配置パスを使用してください。");
        if (entry.Value is null)
        {
            using var key = Registry.CurrentUser.OpenSubKey(Subkey, true);
            key?.DeleteValue(ValueName, false);
        }
        else
        {
            using var key = Registry.CurrentUser.CreateSubKey(Subkey, true);
            key.SetValue(ValueName, entry.Value, entry.Kind);
        }
    }
}
