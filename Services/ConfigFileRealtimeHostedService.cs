using System.Collections.Concurrent;
using System.Threading.Channels;
using ConfigTool.Hubs;
using ConfigTool.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;

namespace ConfigTool.Services;

public sealed class ConfigFileRealtimeHostedService : BackgroundService
{
    private readonly ConfigToolSettingsService _settingsService;
    private readonly ConfigFolderValidator _folderValidator;
    private readonly JsonConfigRepository _repository;
    private readonly IHubContext<ConfigToolHub> _hubContext;
    private readonly SemaphoreSlim _resetLock = new(1, 1);
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly Channel<string> _changeQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    private readonly ConcurrentDictionary<string, JsonFileVersionDto> _knownVersions = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private string? _currentFolder;

    public ConfigFileRealtimeHostedService(
        ConfigToolSettingsService settingsService,
        ConfigFolderValidator folderValidator,
        JsonConfigRepository repository,
        IHubContext<ConfigToolHub> hubContext)
    {
        _settingsService = settingsService;
        _folderValidator = folderValidator;
        _repository = repository;
        _hubContext = hubContext;
    }

    public async Task ResetWatchFolderAsync(string? folderPath, CancellationToken cancellationToken = default)
    {
        await _resetLock.WaitAsync(cancellationToken);
        try
        {
            DisposeWatcher();
            _knownVersions.Clear();
            _currentFolder = null;

            var settings = await _settingsService.LoadAsync(cancellationToken);
            var folder = _folderValidator.Validate(folderPath, settings.RequiredFileNames);
            if (!folder.Exists || string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            _currentFolder = folderPath;
            await RefreshKnownVersionsAsync(folderPath, cancellationToken);

            _watcher = new FileSystemWatcher(folderPath)
            {
                Filter = "*.json",
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName
                               | NotifyFilters.LastWrite
                               | NotifyFilters.Size
                               | NotifyFilters.CreationTime
                               | NotifyFilters.Attributes,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = true
            };

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Error += OnWatcherError;
        }
        finally
        {
            _resetLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = await _settingsService.LoadAsync(stoppingToken);
        await ResetWatchFolderAsync(settings.ConfigFolderPath, stoppingToken);

        var queueTask = ProcessChangeQueueAsync(stoppingToken);
        var periodicTask = RunPeriodicScanAsync(stoppingToken);
        await Task.WhenAll(queueTask, periodicTask);
    }

    private async Task RunPeriodicScanAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var folder = _currentFolder;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                await ScanAndBroadcastAsync(folder, "scan", string.Empty, cancellationToken);
            }
        }
    }

    private async Task ProcessChangeQueueAsync(CancellationToken cancellationToken)
    {
        var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await _changeQueue.Reader.WaitToReadAsync(cancellationToken))
        {
            pending.Clear();
            while (_changeQueue.Reader.TryRead(out var fileName))
            {
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    pending.Add(fileName);
                }
            }

            await Task.Delay(250, cancellationToken);
            while (_changeQueue.Reader.TryRead(out var fileName))
            {
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    pending.Add(fileName);
                }
            }

            var folder = _currentFolder;
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            foreach (var fileName in pending)
            {
                await ScanAndBroadcastAsync(folder, "changed", fileName, cancellationToken);
            }
        }
    }

    private async Task ScanAndBroadcastAsync(string folder, string changeKind, string changedFileName, CancellationToken cancellationToken)
    {
        if (!await _scanLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var settings = await _settingsService.LoadAsync(cancellationToken);
            var validation = _folderValidator.Validate(folder, settings.RequiredFileNames);
            if (!validation.IsValid)
            {
                await _hubContext.Clients.All.SendAsync("ConfigFilesChanged", new ConfigExternalChangeDto
                {
                    FolderPath = folder,
                    FileName = changedFileName,
                    ChangeKind = "folder-invalid",
                    Folder = validation,
                    Message = validation.Message
                }, cancellationToken);
                return;
            }

            var changedFiles = await DetectChangedFilesAsync(folder, cancellationToken);
            if (changedFiles.Count == 0 && !string.Equals(changeKind, "folder", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var files = await _repository.ScanFilesAsync(folder, cancellationToken);
            var fileToReport = !string.IsNullOrWhiteSpace(changedFileName)
                ? Path.GetFileName(changedFileName)
                : changedFiles.FirstOrDefault() ?? string.Empty;

            var message = string.IsNullOrWhiteSpace(fileToReport)
                ? "Đã sync lại danh sách file JSON theo thư mục Unity config."
                : $"Unity hoặc app ngoài vừa đổi: {fileToReport}. Tool đã sync lại snapshot mới nhất để 2 bên không đấu dữ liệu.";

            await _hubContext.Clients.All.SendAsync("ConfigFilesChanged", new ConfigExternalChangeDto
            {
                FolderPath = folder,
                FileName = fileToReport,
                ChangeKind = changeKind,
                Folder = validation,
                Files = files,
                Message = message,
                ChangedAt = DateTimeOffset.Now
            }, cancellationToken);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private async Task<List<string>> DetectChangedFilesAsync(string folder, CancellationToken cancellationToken)
    {
        var latest = await _repository.GetFileVersionsAsync(folder, cancellationToken);
        var changed = new List<string>();

        foreach (var item in latest)
        {
            if (!_knownVersions.TryGetValue(item.Key, out var oldVersion) || !FileVersionMatches(oldVersion, item.Value))
            {
                changed.Add(item.Key);
            }
        }

        foreach (var oldKey in _knownVersions.Keys.ToArray())
        {
            if (!latest.ContainsKey(oldKey))
            {
                changed.Add(oldKey);
            }
        }

        _knownVersions.Clear();
        foreach (var item in latest)
        {
            _knownVersions[item.Key] = item.Value;
        }

        return changed.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task RefreshKnownVersionsAsync(string folder, CancellationToken cancellationToken)
    {
        var versions = await _repository.GetFileVersionsAsync(folder, cancellationToken);
        _knownVersions.Clear();
        foreach (var item in versions)
        {
            _knownVersions[item.Key] = item.Value;
        }
    }

    private static bool FileVersionMatches(JsonFileVersionDto left, JsonFileVersionDto right)
        => left.SizeBytes == right.SizeBytes
           && string.Equals(left.ContentHash, right.ContentHash, StringComparison.OrdinalIgnoreCase);

    private void OnFileChanged(object sender, FileSystemEventArgs args)
    {
        if (args.Name is null || !args.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _changeQueue.Writer.TryWrite(Path.GetFileName(args.Name));
    }

    private void OnFileRenamed(object sender, RenamedEventArgs args)
    {
        if (args.OldName?.EndsWith(".json", StringComparison.OrdinalIgnoreCase) == true)
        {
            _changeQueue.Writer.TryWrite(Path.GetFileName(args.OldName));
        }

        if (args.Name?.EndsWith(".json", StringComparison.OrdinalIgnoreCase) == true)
        {
            _changeQueue.Writer.TryWrite(Path.GetFileName(args.Name));
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        _changeQueue.Writer.TryWrite(string.Empty);
    }

    private void DisposeWatcher()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnFileChanged;
        _watcher.Created -= OnFileChanged;
        _watcher.Deleted -= OnFileChanged;
        _watcher.Renamed -= OnFileRenamed;
        _watcher.Error -= OnWatcherError;
        _watcher.Dispose();
        _watcher = null;
    }

    public override void Dispose()
    {
        DisposeWatcher();
        _resetLock.Dispose();
        _scanLock.Dispose();
        base.Dispose();
    }
}
