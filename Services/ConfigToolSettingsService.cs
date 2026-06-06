using System.Text.Encodings.Web;
using System.Text.Json;
using ConfigTool.Models;

namespace ConfigTool.Services;

public sealed class ConfigToolSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IAppStartupPathProvider _paths;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ConfigToolSettings? _cache;

    public ConfigToolSettingsService(IAppStartupPathProvider paths)
    {
        _paths = paths;
    }

    public string SettingsFilePath => _paths.SettingsFilePath;

    public async Task<ConfigToolSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cache is not null)
            {
                return Clone(_cache);
            }

            Directory.CreateDirectory(_paths.ConfigDirectory);

            if (!File.Exists(_paths.SettingsFilePath))
            {
                _cache = new ConfigToolSettings
                {
                    RequiredFileNames = ConfigToolDefaults.RequiredFileNames
                };
                await SaveCoreAsync(_cache, cancellationToken);
                return Clone(_cache);
            }

            await using var stream = File.OpenRead(_paths.SettingsFilePath);
            _cache = await JsonSerializer.DeserializeAsync<ConfigToolSettings>(stream, JsonOptions, cancellationToken)
                     ?? new ConfigToolSettings();
            _cache.RequiredFileNames = NormalizeRequiredFiles(_cache.RequiredFileNames);
            return Clone(_cache);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(ConfigToolSettings settings, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            settings.RequiredFileNames = NormalizeRequiredFiles(settings.RequiredFileNames);
            _cache = Clone(settings);
            await SaveCoreAsync(_cache, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SaveCoreAsync(ConfigToolSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        var tempFile = _paths.SettingsFilePath + ".tmp";
        await using (var stream = File.Create(tempFile))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
        }

        File.Copy(tempFile, _paths.SettingsFilePath, overwrite: true);
        File.Delete(tempFile);
    }

    private static string[] NormalizeRequiredFiles(IEnumerable<string>? values)
    {
        var items = values?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return items is { Length: > 0 } ? items : ConfigToolDefaults.RequiredFileNames;
    }

    private static ConfigToolSettings Clone(ConfigToolSettings settings) => new()
    {
        ConfigFolderPath = settings.ConfigFolderPath,
        SignalRPort = settings.SignalRPort,
        LastFolderSelectedAt = settings.LastFolderSelectedAt,
        RequiredFileNames = NormalizeRequiredFiles(settings.RequiredFileNames)
    };
}
