using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FastSearch.Native;
using FastSearch.Settings;
using FastSearch.Themes;

internal static class SettingsUiProbe
{
    internal static void Run()
    {
        var shortcut = new HotKeySetting(HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift, 0x78);
        using (var registered = new GlobalHotKey(() => { }, shortcut))
        {
            if (!registered.IsRegistered) throw new Exception("Custom test hotkey could not register: " + registered.RegistrationError);
            using var conflict = new GlobalHotKey(() => { }, shortcut);
            if (conflict.IsRegistered) throw new Exception("Custom hotkey conflict was not detected");
        }
        using (var again = new GlobalHotKey(() => { }, shortcut))
            if (!again.IsRegistered) throw new Exception("Custom hotkey was not released");
        Console.WriteLine("SETTINGS HOTKEY: custom modifiers/key, conflict detection and disposal passed");

        int saves = 0;
        bool fail = true;
        var initial = new AppSettings { Shortcut = shortcut, LastSort = new(FastSearch.Core.SortColumn.Folder, true) };
        AppSettings? saved = null;
        Exception? failure = null;
        var dialog = new FastSearch.SettingsWindow(initial, settings =>
        {
            saves++;
            if (fail) throw new System.ComponentModel.Win32Exception(1409, "競合するショートカットです。");
            saved = settings;
        }) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowInTaskbar = false };
        dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                var shortcutBox = (TextBox)dialog.FindName("ShortcutBox");
                var startup = (CheckBox)dialog.FindName("StartupBox");
                var resident = (CheckBox)dialog.FindName("ResidentBox");
                var save = (Button)dialog.FindName("SaveButton");
                var error = (TextBlock)dialog.FindName("ErrorText");
                var themeBox = (ComboBox)dialog.FindName("ThemeBox");
                if (!Equals(themeBox.SelectedValue, AppTheme.LightGray)) throw new Exception("Settings UI did not restore the theme");
                themeBox.SelectedValue = AppTheme.Charcoal;
                if (ThemeManager.Current != AppTheme.LightGray) throw new Exception("Selecting a theme applied it before saving");
                if (!shortcutBox.IsReadOnly || shortcutBox.Text != shortcut.DisplayText || startup.IsChecked == true || resident.IsChecked != true)
                    throw new Exception("Settings UI did not load existing values");
                startup.IsChecked = true;
                resident.IsChecked = false;
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!dialog.IsVisible || saves != 1 || !error.Text.Contains("競合")) throw new Exception("Conflict did not keep the settings dialog open");
                ((Button)dialog.FindName("ResetShortcutButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (shortcutBox.Text != HotKeySetting.Default.DisplayText || error.Text.Length != 0)
                    throw new Exception("Reset did not restore the default shortcut");
                dialog.UpdateLayout();
                var preview = new RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth), (int)Math.Ceiling(dialog.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                preview.Render(dialog);
                string previewPath = Path.GetFullPath("artifacts/Settings-preview.png");
                Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
                using (var file = File.Create(previewPath))
                {
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(preview)); encoder.Save(file);
                }
                fail = false;
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception ex) { failure = ex; dialog.Close(); }
        }));
        bool? result = dialog.ShowDialog();
        if (failure is not null) throw failure;
        if (result != true || saved?.StartWithWindows != true || saved.KeepRunningInTray || saved.Shortcut != HotKeySetting.Default || saves != 2)
            throw new Exception("Settings dialog did not save the edited settings and close");
        if (saved.LastSort != initial.LastSort) throw new Exception("Settings dialog discarded the saved sort");
        if (saved.Theme != AppTheme.Charcoal) throw new Exception("Settings dialog discarded the selected theme");

        var canceled = new FastSearch.SettingsWindow(initial, _ => throw new Exception("Canceled settings were saved"))
            { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowInTaskbar = false };
        canceled.Loaded += (_, _) => canceled.Dispatcher.BeginInvoke(() => canceled.Close());
        if (canceled.ShowDialog() == true) throw new Exception("Canceled dialog returned success");
        Console.WriteLine("SETTINGS UI: loaded values, save validation, inline conflict, reset, startup checkbox, cancel and preview passed");
    }
}
