using System;
using System.IO;
using System.Text.Json;

namespace FastSearch.Settings;

internal interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

internal sealed class SettingsStore(string path) : ISettingsStore
{
    public AppSettings Load()
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 64 * 1024) throw new InvalidDataException("設定ファイルが大きすぎます。");
            var settings = JsonSerializer.Deserialize<AppSettings>(stream) ?? throw new JsonException("空の設定です。");
            settings.Validate();
            return settings;
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Core.IndexManager.Log(ex);
            return new();
        }
    }
    public void Save(AppSettings settings)
    {
        settings.Validate();
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(true);
            }
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
