namespace ConfigTool.Services;

public interface IAppStartupPathProvider
{
    string StartupPath { get; }
    string ConfigDirectory { get; }
    string SettingsFilePath { get; }
}

public sealed class AppStartupPathProvider : IAppStartupPathProvider
{
    public string StartupPath { get; } = AppContext.BaseDirectory;
    public string ConfigDirectory => Path.Combine(StartupPath, "config");
    public string SettingsFilePath => Path.Combine(ConfigDirectory, "cauhinh.json");
}
