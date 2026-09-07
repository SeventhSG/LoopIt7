using System.IO;
using System.Text.Json;
using LoopIt7.Models;

namespace LoopIt7.Services;

/// <summary>
/// Reads and writes the single settings file under %AppData%\LoopIt7. Writes go through a
/// temporary file so a crash mid save cannot leave an unreadable config behind.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _directory;
    private readonly string _path;
    private readonly object _sync = new();

    public SettingsService()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LoopIt7");
        _path = Path.Combine(_directory, "settings.json");
    }

    public string SettingsPath => _path;

    public AppSettings Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_path)) return new AppSettings();
                string json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
            catch
            {
                // A corrupt config should not stop the app from opening.
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
                File.Move(temp, _path, overwrite: true);
            }
            catch
            {
                // Settings are a convenience. Losing them is not worth an error dialog.
            }
        }
    }
}
