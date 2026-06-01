using ConfigTool.Services;
using Microsoft.Maui.LifecycleEvents;
#if WINDOWS
using ConfigTool.Platforms.Windows;
#endif

namespace ConfigTool
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();

#if WINDOWS
            builder.ConfigureLifecycleEvents(events =>
            {
                events.AddWindows(windows =>
                {
                    windows.OnWindowCreated(WindowsWindowSizer.ApplyStartupBounds);
                });
            });
#endif

            builder.Services.AddSingleton<IAppStartupPathProvider, AppStartupPathProvider>();
            builder.Services.AddSingleton<ConfigToolSettingsService>();
            builder.Services.AddSingleton<ConfigFolderValidator>();
            builder.Services.AddSingleton<JsonConfigRepository>();
            builder.Services.AddSingleton<SqlConnectConfigService>();
            builder.Services.AddSingleton<SqlAdminService>();
            builder.Services.AddSingleton<ConfigSignalRHost>();

#if WINDOWS
            builder.Services.AddSingleton<IConfigFolderPicker, WindowsConfigFolderPicker>();
#else
            builder.Services.AddSingleton<IConfigFolderPicker, UnsupportedConfigFolderPicker>();
#endif

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
#endif

            return builder.Build();
        }
    }
}
