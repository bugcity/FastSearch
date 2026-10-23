using System;
using System.ComponentModel;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Input;
using System.Diagnostics;
using System.Security.Principal;
using FastSearch.Settings;
using FastSearch.Themes;

namespace FastSearch;

public partial class SettingsWindow : Window
{
    private readonly Action<AppSettings> _save;
    private HotKeySetting _shortcut;
    private readonly AppSettings _initialSettings;
    internal SettingsWindow(AppSettings settings, Action<AppSettings> save)
    {
        _save = save;
        _initialSettings = settings;
        _shortcut = settings.Shortcut;
        ThemeManager.Ensure();
        InitializeComponent();
        ThemeManager.Attach(this);
        ThemeBox.SelectedValue = settings.Theme;
        ShortcutBox.Text = _shortcut.DisplayText;
        StartupBox.IsChecked = settings.StartWithWindows;
        ResidentBox.IsChecked = settings.KeepRunningInTray;
    }
    private void ShortcutBox_KeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if ((key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift) ||
            (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)) return;
        e.Handled = true;
        if (e.IsRepeat || key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed) return;
        var modifiers = HotKeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= HotKeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= HotKeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= HotKeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= HotKeyModifiers.Windows;
        var candidate = new HotKeySetting(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        try
        {
            candidate.Validate();
            _shortcut = candidate;
            ShortcutBox.Text = candidate.DisplayText;
            ErrorText.Text = "";
        }
        catch (ArgumentException ex) { ErrorText.Text = ex.Message; }
    }
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _shortcut = HotKeySetting.Default;
        ShortcutBox.Text = _shortcut.DisplayText;
        ErrorText.Text = "";
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _save(_initialSettings with { Shortcut = _shortcut, StartWithWindows = StartupBox.IsChecked == true,
                KeepRunningInTray = ResidentBox.IsChecked == true,
                Theme = ThemeBox.SelectedValue is AppTheme theme ? theme : _initialSettings.Theme });
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or
            ArgumentException or InvalidOperationException or Win32Exception)
        { ErrorText.Text = ex.Message; }
    }
    private async void Service_Click(object sender, RoutedEventArgs e)
    {
        ServiceButton.IsEnabled = false;
        try
        {
            string script = Path.Combine(AppContext.BaseDirectory, "Setup-Service.ps1");
            if (!File.Exists(script)) throw new FileNotFoundException("Setup-Service.ps1 がありません。配布フォルダー一式を展開してください。");
            string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(shell) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            foreach (string argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Mode", "Install", "-OwnerSid", WindowsIdentity.GetCurrent().User!.Value })
                start.ArgumentList.Add(argument);
            ErrorText.Text = "サービスをインストールしています…";
            using var process = Process.Start(start) ?? throw new IOException("インストーラーを起動できません。");
            await process.WaitForExitAsync();
            ErrorText.Text = process.ExitCode == 0 ? "サービスを開始しました。検索画面は自動的に接続します。" :
                "インストールに失敗しました。%PROGRAMDATA%\\FastSearch\\setup.log を確認してください。";
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or InvalidOperationException)
        { ErrorText.Text = ex is Win32Exception { NativeErrorCode: 1223 } ? "インストールをキャンセルしました。" : ex.Message; }
        finally { ServiceButton.IsEnabled = true; }
    }
}
