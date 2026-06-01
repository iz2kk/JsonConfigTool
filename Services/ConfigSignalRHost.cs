using System.Net;
using System.Net.Sockets;
using ConfigTool.Hubs;
using ConfigTool.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ConfigTool.Services;

public sealed class ConfigSignalRHost : IAsyncDisposable
{
    private readonly ConfigToolSettingsService _settingsService;
    private readonly ConfigFolderValidator _folderValidator;
    private readonly JsonConfigRepository _repository;
    private readonly SqlConnectConfigService _sqlConnectConfigService;
    private readonly SqlAdminService _sqlAdminService;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private WebApplication? _app;

    public ConfigSignalRHost(
        ConfigToolSettingsService settingsService,
        ConfigFolderValidator folderValidator,
        JsonConfigRepository repository,
        SqlConnectConfigService sqlConnectConfigService,
        SqlAdminService sqlAdminService)
    {
        _settingsService = settingsService;
        _folderValidator = folderValidator;
        _repository = repository;
        _sqlConnectConfigService = sqlConnectConfigService;
        _sqlAdminService = sqlAdminService;
    }

    public int Port { get; private set; } = 59177;
    public string HubUrl => $"http://127.0.0.1:{Port}/config-hub";

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            return;
        }

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_app is not null)
            {
                return;
            }

            var settings = await _settingsService.LoadAsync(cancellationToken);
            Port = FindAvailablePort(settings.SignalRPort <= 0 ? 59177 : settings.SignalRPort);
            if (Port != settings.SignalRPort)
            {
                settings.SignalRPort = Port;
                await _settingsService.SaveAsync(settings, cancellationToken);
            }

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(MauiProgram).Assembly.FullName,
                ContentRootPath = AppContext.BaseDirectory
            });

            builder.WebHost.UseUrls($"http://127.0.0.1:{Port}");
            builder.Services.AddSignalR(options =>
            {
                options.EnableDetailedErrors = true;
                options.MaximumReceiveMessageSize = 1024 * 1024 * 8;
                options.StreamBufferCapacity = 32;
            });

            builder.Services.AddSingleton(_settingsService);
            builder.Services.AddSingleton(_folderValidator);
            builder.Services.AddSingleton(_repository);
            builder.Services.AddSingleton(_sqlConnectConfigService);
            builder.Services.AddSingleton(_sqlAdminService);
            builder.Services.AddSingleton<ConfigFileRealtimeHostedService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<ConfigFileRealtimeHostedService>());

            _app = builder.Build();
            _app.MapHub<ConfigToolHub>("/config-hub");
            await _app.StartAsync(cancellationToken);
        }
        finally
        {
            _startLock.Release();
        }
    }

    private static int FindAvailablePort(int preferredPort)
    {
        for (var port = preferredPort; port < preferredPort + 50; port++)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return port;
            }
            catch (SocketException)
            {
                // Try next port.
            }
        }

        using var randomListener = new TcpListener(IPAddress.Loopback, 0);
        randomListener.Start();
        return ((IPEndPoint)randomListener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null)
        {
            return;
        }

        await _app.StopAsync(TimeSpan.FromSeconds(2));
        await _app.DisposeAsync();
        _app = null;
    }
}
