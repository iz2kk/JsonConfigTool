using ConfigTool.Hubs;
using ConfigTool.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;

namespace ConfigTool.Services;

public sealed class DynamicDnsAutoUpdateHostedService : BackgroundService
{
    private readonly DynamicDnsConfigService _configService;
    private readonly DynamicDnsUpdateService _updateService;
    private readonly IHubContext<ConfigToolHub> _hubContext;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public DynamicDnsAutoUpdateHostedService(
        DynamicDnsConfigService configService,
        DynamicDnsUpdateService updateService,
        IHubContext<ConfigToolHub> hubContext)
    {
        _configService = configService;
        _updateService = updateService;
        _hubContext = hubContext;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunIfDueAsync(stoppingToken);
            }
            catch
            {
                // Auto update không được làm chết SignalR host.
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunIfDueAsync(CancellationToken cancellationToken)
    {
        if (!await _runLock.WaitAsync(0, cancellationToken)) return;
        try
        {
            var config = await _configService.LoadAsync(cancellationToken);
            var settings = config.AutoUpdate;
            if (settings is null || !settings.Enabled) return;

            var interval = TimeSpan.FromMinutes(Math.Clamp(settings.IntervalMinutes <= 0 ? 10 : settings.IntervalMinutes, 1, 1440));
            if (settings.LastRunAt is not null && DateTimeOffset.Now - settings.LastRunAt.Value < interval) return;

            var ipResult = await _updateService.GetPublicIpAsync(cancellationToken);
            if (!ipResult.Success)
            {
                await SaveAndNotifyAsync("ip-error", ipResult.Message, null, cancellationToken);
                return;
            }

            var ip = ipResult.Message.Trim();
            if (settings.UpdateOnlyWhenIpChanged && string.Equals(config.PublicIp.LastIp, ip, StringComparison.OrdinalIgnoreCase))
            {
                await SaveAndNotifyAsync("skipped", "IP public chưa đổi: " + ip, ip, cancellationToken);
                return;
            }

            if (settings.ScanBeforeUpdate)
            {
                foreach (var provider in config.Accounts.Where(x => x.Enabled).Select(x => DynamicDnsConfigService.NormalizeProvider(x.Provider)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    await _updateService.ScanAsync(new DynamicDnsScanRequest { Provider = provider, OnlyEnabled = true, SaveToConfig = true }, cancellationToken);
                }
            }

            var latest = await _configService.LoadAsync(cancellationToken);
            var providers = latest.Accounts.Where(x => x.Enabled).Select(x => DynamicDnsConfigService.NormalizeProvider(x.Provider)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var success = 0;
            var total = 0;
            var messages = new List<string>();
            foreach (var provider in providers)
            {
                var update = await _updateService.UpdateAsync(new DynamicDnsUpdateRequest { Provider = provider, Ip = ip, OnlyEnabled = true }, cancellationToken);
                success += update.SuccessCount;
                total += update.Total;
                messages.Add(update.Message);
            }

            var message = total == 0 ? "Auto Update không có record bật sẵn." : $"Auto Update xong {success}/{total} record. {string.Join(" | ", messages.Take(3))}";
            await SaveAndNotifyAsync(success > 0 ? "updated" : "failed", message, ip, cancellationToken);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task SaveAndNotifyAsync(string status, string message, string? ip, CancellationToken cancellationToken)
    {
        await _configService.SaveAutoUpdateRuntimeAsync(status, message, ip, cancellationToken);
        var snapshot = await _configService.LoadResponseAsync(cancellationToken);
        await _hubContext.Clients.All.SendAsync("DynamicDnsChanged", snapshot, message, cancellationToken);
    }
}
