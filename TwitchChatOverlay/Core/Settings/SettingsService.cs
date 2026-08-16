using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace TwitchChatOverlay.Core.Settings;

/// <summary>
/// Owns the on-disk settings file. Missing fields/sections in an older config simply
/// keep the property initializer defaults from <see cref="AppSettings"/> — System.Text.Json
/// only assigns properties that are actually present in the JSON.
/// </summary>
public sealed class SettingsService
{
    private readonly ILogger<SettingsService> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions;

    public string SettingsDirectory { get; }
    public string SettingsFilePath { get; }

    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? SettingsChanged;

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        SettingsDirectory = Path.Combine(appData, "TwitchChatOverlay");
        Directory.CreateDirectory(SettingsDirectory);
        SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                Current = new AppSettings();
                await WriteToDiskAsync(ct);
                return;
            }

            try
            {
                await using var stream = File.OpenRead(SettingsFilePath);
                var loaded = await JsonSerializer.DeserializeAsync<AppSettings>(stream, _jsonOptions, ct);
                Current = loaded ?? new AppSettings();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось прочитать settings.json по пути {Path}, используются значения по умолчанию", SettingsFilePath);
                Current = new AppSettings();
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>Swaps in a whole new settings object (reset / import) and persists it.
    /// Subscribers get the usual SettingsChanged, so the running app re-reads everything.</summary>
    public async Task ReplaceAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            Current = settings;
            await WriteToDiskAsync(ct);
        }
        finally
        {
            _fileLock.Release();
        }

        SettingsChanged?.Invoke(this, Current);
    }

    public async Task ExportAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, Current, _jsonOptions, ct);
    }

    /// <summary>Reads a settings file exported earlier. Throws on malformed JSON so the UI can
    /// report it instead of silently wiping the user's configuration.</summary>
    public async Task ImportAsync(string path, CancellationToken ct = default)
    {
        AppSettings imported;
        await using (var stream = File.OpenRead(path))
        {
            imported = await JsonSerializer.DeserializeAsync<AppSettings>(stream, _jsonOptions, ct)
                       ?? throw new InvalidDataException("Файл не содержит настроек.");
        }

        await ReplaceAsync(imported, ct);
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            await WriteToDiskAsync(ct);
        }
        finally
        {
            _fileLock.Release();
        }

        SettingsChanged?.Invoke(this, Current);
    }

    private async Task WriteToDiskAsync(CancellationToken ct)
    {
        var tempPath = SettingsFilePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, Current, _jsonOptions, ct);
        }
        File.Move(tempPath, SettingsFilePath, overwrite: true);
    }
}
