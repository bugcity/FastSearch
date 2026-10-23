using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using FastSearch.Core;

namespace FastSearch.Settings;

internal interface IHotKeyRegistration : IDisposable
{
    bool IsRegistered { get; }
    int RegistrationError { get; }
}

internal sealed class SettingsController : IDisposable
{
    private readonly ISettingsStore _store;
    private readonly IStartupRegistration _startup;
    private readonly Func<HotKeySetting, IHotKeyRegistration> _register;
    private IHotKeyRegistration? _hotKey;
    private readonly object _writeLock = new();
    private AppSettings _current;
    internal AppSettings Current { get => Volatile.Read(ref _current); private set => Volatile.Write(ref _current, value); }
    internal bool IsHotKeyRegistered => _hotKey?.IsRegistered == true;
    internal int RegistrationError => _hotKey?.RegistrationError ?? 0;
    internal SettingsController(ISettingsStore store, IStartupRegistration startup, Func<HotKeySetting, IHotKeyRegistration> register)
    { _store = store; _startup = startup; _register = register; _current = store.Load(); }
    internal void RegisterInitial() => _hotKey = _register(Current.Shortcut);
    internal AppSettings ReadForEditing() => Current with
    {
        // A previous installation path is still an enabled registration. Saving updates it
        // to this executable, instead of showing a misleading unchecked box after a move.
        StartWithWindows = _startup.Read().Value is string command && !string.IsNullOrWhiteSpace(command)
    };

    internal void Apply(AppSettings next)
    {
        next.Validate();
        IHotKeyRegistration? candidate = null;
        StartupEntry previous = _startup.Read();
        bool startupWritten = false;
        try
        {
            if (next.Shortcut != Current.Shortcut || !IsHotKeyRegistered)
            {
                candidate = _register(next.Shortcut);
                if (!candidate.IsRegistered)
                    throw new Win32Exception(candidate.RegistrationError, "このショートカットは登録できません。他の組み合わせを指定してください。");
            }
            StartupEntry desired = next.StartWithWindows ? _startup.EnabledEntry : new(null);
            if (desired != previous) { startupWritten = true; _startup.Write(desired); }
            lock (_writeLock)
            {
                next = next with { LastSort = Current.LastSort };
                _store.Save(next);
                Current = next;
            }
        }
        catch (Exception original)
        {
            candidate?.Dispose();
            if (startupWritten)
            {
                try { _startup.Write(previous); }
                catch (Exception rollback)
                { throw new InvalidOperationException("設定の保存と自動起動の復元に失敗しました。自動起動の登録状態を確認してください。", new AggregateException(original, rollback)); }
            }
            throw;
        }
        if (candidate is not null) { _hotKey?.Dispose(); _hotKey = candidate; }
    }
    internal Task SaveSortAsync(SortOrder sort)
    {
        if (!Enum.IsDefined(sort.Column)) throw new ArgumentException("ソート列が不正です。");
        if (Current.LastSort == sort) return Task.CompletedTask;
        Current = Current with { LastSort = sort };
        return FlushAsync();
    }
    // Each writer takes the latest state under the same lock. A late task cannot overwrite
    // a newer sort or settings change with an older captured value.
    internal Task FlushAsync() => Task.Run(() => { lock (_writeLock) _store.Save(Current); });
    public void Dispose() => _hotKey?.Dispose();
}
