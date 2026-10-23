using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using FastSearch.Core;

namespace FastSearch.Settings;

[Flags]
public enum HotKeyModifiers : uint { None = 0, Alt = 1, Control = 2, Shift = 4, Windows = 8 }

public sealed record HotKeySetting(HotKeyModifiers Modifiers, uint VirtualKey)
{
    public static HotKeySetting Default
    {
        get
        {
            short translated = VkKeyScanW('/');
            if (translated == -1) return new(HotKeyModifiers.Windows, 0xBF);
            int shift = (translated >> 8) & 0xFF;
            var modifiers = HotKeyModifiers.Windows;
            if ((shift & 1) != 0) modifiers |= HotKeyModifiers.Shift;
            if ((shift & 2) != 0) modifiers |= HotKeyModifiers.Control;
            if ((shift & 4) != 0) modifiers |= HotKeyModifiers.Alt;
            return new(modifiers, (uint)(translated & 0xFF));
        }
    }

    public void Validate()
    {
        if ((Modifiers & ~(HotKeyModifiers)15) != 0 ||
            (Modifiers & (HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Windows)) == 0)
            throw new ArgumentException("Ctrl・Alt・Winのいずれかを含む組み合わせを指定してください。");
        if (VirtualKey < 8 || VirtualKey > 254 || VirtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or
            >= 0xA0 and <= 0xA5 or 0xE5 or 0xE7 || KeyInterop.KeyFromVirtualKey((int)VirtualKey) == Key.None)
            throw new ArgumentException("修飾キー以外のキーも指定してください。");
        if (Modifiers == HotKeyModifiers.Control && (VirtualKey is >= 0x31 and <= 0x34 or >= 0x61 and <= 0x64))
            throw new ArgumentException("Ctrl + 1〜4 は列ソートで使用します。他の組み合わせを指定してください。");
    }

    public string DisplayText
    {
        get
        {
            var parts = new List<string>(5);
            if (Modifiers.HasFlag(HotKeyModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(HotKeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(HotKeyModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(HotKeyModifiers.Windows)) parts.Add("Win");
            string key = VirtualKey switch
            {
                0xBF => "/", 0xBD => "-", 0xBB => "=", 0xBC => ",", 0xBE => ".",
                0xDC => "\\", 0xDB => "[", 0xDD => "]", 0xBA => ";", 0xDE => "'",
                >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)VirtualKey).ToString(),
                _ => new KeyConverter().ConvertToString(KeyInterop.KeyFromVirtualKey((int)VirtualKey)) ?? VirtualKey.ToString()
            };
            parts.Add(key);
            return string.Join(" + ", parts);
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char character);
}

public enum AppTheme { LightGray, Charcoal }

public sealed record AppSettings
{
    public int Version { get; init; } = 1;
    public HotKeySetting Shortcut { get; init; } = HotKeySetting.Default;
    public bool StartWithWindows { get; init; }
    public bool KeepRunningInTray { get; init; } = true;
    public SortOrder LastSort { get; init; } = new(SortColumn.Name);
    public AppTheme Theme { get; init; } = AppTheme.LightGray;
    public bool ShouldStartHidden(bool backgroundRequested) => KeepRunningInTray && backgroundRequested;
    public void Validate()
    {
        if (Version != 1 || Shortcut is null) throw new ArgumentException("未対応または不正な設定です。");
        Shortcut.Validate();
        if (!Enum.IsDefined(LastSort.Column)) throw new ArgumentException("ソート列が不正です。");
        if (!Enum.IsDefined(Theme)) throw new ArgumentException("配色が不正です。");
    }
}
