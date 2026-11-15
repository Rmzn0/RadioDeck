using System.IO;
using System.Text.Json;
using RadioDeck.Models;

namespace RadioDeck.Services;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _sync = new();

    public ConfigStore(string appName = "RadioDeck", string? directory = null)
    {
        var dir = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), appName);
        Directory.CreateDirectory(dir);
        DirectoryOnDisk = dir;
        _path = Path.Combine(dir, "config.json");
    }

    public string PathOnDisk => _path;
    public string DirectoryOnDisk { get; }

    /// <summary>Set when the stored file could not be read and was set aside.</summary>
    public string? LoadWarning { get; private set; }

    public AppConfig Load()
    {
        lock (_sync)
        {
            LoadWarning = null;
            if (!File.Exists(_path))
            {
                var initial = AppConfig.CreateDefault();
                Save(initial);
                return initial;
            }

            AppConfig? cfg;
            try
            {
                cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_path), ReadOptions);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // Never silently overwrite a file the operator may want to repair by hand.
                var aside = Path.Combine(DirectoryOnDisk, $"config.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                try { File.Copy(_path, aside, overwrite: true); } catch { aside = "(copy failed)"; }
                LoadWarning = Localization.Loc.T("Main_ConfigUnreadable", ex.Message, Path.GetFileName(aside));
                cfg = null;
            }

            var storedVersion = cfg?.ConfigVersion ?? AppConfig.CurrentVersion;
            cfg ??= AppConfig.CreateDefault();
            if (ConfigMigrations.Normalize(cfg) && LoadWarning is null)
            {
                if (storedVersion < AppConfig.CurrentVersion)
                    KeepPreviousVersion(storedVersion);
                Save(cfg);
            }
            return cfg;
        }
    }

    /// <summary>
    /// Keeps the file of an older program version before it is converted, so the
    /// older program can be used again by copying it back. Written only once.
    /// </summary>
    private void KeepPreviousVersion(int storedVersion)
    {
        try
        {
            var backup = Path.Combine(DirectoryOnDisk, $"config.v{Math.Max(1, storedVersion)}-backup.json");
            if (!File.Exists(backup))
                File.Copy(_path, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The conversion itself loses no addresses; a missing backup must not stop the program.
        }
    }

    public void Save(AppConfig config)
    {
        lock (_sync)
        {
            var json = JsonSerializer.Serialize(config, WriteOptions);

            // Write to a temporary file first so an interrupted save cannot truncate the config.
            var tmp = Path.Combine(DirectoryOnDisk, $"config.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(tmp, json);
                if (File.Exists(_path))
                {
                    try
                    {
                        File.Replace(tmp, _path, destinationBackupFileName: null);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        File.Copy(tmp, _path, overwrite: true);
                    }
                }
                else
                {
                    File.Move(tmp, _path);
                }
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
    }
}
