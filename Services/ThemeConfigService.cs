using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ConfigTool.Models;

namespace ConfigTool.Services;

public sealed class ThemeConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly IAppStartupPathProvider _startupPathProvider;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ThemeConfigFileDto? _cache;

    public ThemeConfigService(IAppStartupPathProvider startupPathProvider)
    {
        _startupPathProvider = startupPathProvider;
    }

    public string ConfigFilePath => Path.Combine(_startupPathProvider.ConfigDirectory, "themeconfig.json");
    public string ThemesDirectory => Path.Combine(_startupPathProvider.StartupPath, "themes");

    public async Task<ThemeConfigResponseDto> LoadResponseAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureBuiltInThemeFilesAsync(cancellationToken);
            var config = await ReadCoreUnlockedAsync(cancellationToken);
            NormalizeConfig(config);
            await WriteCoreAsync(config, cancellationToken);
            _cache = Clone(config);
            var active = GetActiveTheme(config);
            var css = await ReadThemeCssForApplyAsync(active, cancellationToken);
            return new ThemeConfigResponseDto
            {
                ConfigFilePath = ConfigFilePath,
                ThemesDirectory = ThemesDirectory,
                Config = Clone(config),
                ActiveTheme = Clone(active),
                ThemeCssText = css,
                ThemeFilePath = GetThemeFilePath(active.FileName),
                ShellClass = NormalizeShellClass(active.ShellClass)
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ThemeFileEditDto> GetThemeForEditAsync(string themeId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureBuiltInThemeFilesAsync(cancellationToken);
            var config = await ReadCoreUnlockedAsync(cancellationToken);
            var theme = config.Themes.FirstOrDefault(x => string.Equals(x.Id, themeId, StringComparison.OrdinalIgnoreCase)) ?? GetActiveTheme(config);
            var content = await ReadThemeFileAsync(theme.FileName, cancellationToken);
            return new ThemeFileEditDto
            {
                Theme = Clone(theme),
                Content = content,
                FilePath = GetThemeFilePath(theme.FileName)
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ThemeActionResultDto> SaveThemeFileAsync(ThemeFileItemDto theme, string content, bool setActive, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureBuiltInThemeFilesAsync(cancellationToken);
            var config = await ReadCoreUnlockedAsync(cancellationToken);
            NormalizeTheme(theme);

            var existing = config.Themes.FirstOrDefault(x => string.Equals(x.Id, theme.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                theme.CreatedAt = DateTimeOffset.Now;
                theme.UpdatedAt = DateTimeOffset.Now;
                config.Themes.Add(Clone(theme));
            }
            else
            {
                if (existing.IsBuiltIn)
                {
                    return Fail("Theme built-in chỉ nên duplicate rồi sửa bản custom, không ghi đè file gốc.");
                }

                var oldPath = GetThemeFilePath(existing.FileName);
                var newPath = GetThemeFilePath(theme.FileName);
                existing.Name = theme.Name;
                existing.Description = theme.Description;
                existing.FileName = theme.FileName;
                existing.FileType = theme.FileType;
                existing.IsScss = theme.IsScss;
                existing.ShellClass = NormalizeShellClass(theme.ShellClass);
                existing.UpdatedAt = DateTimeOffset.Now;

                if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath))
                {
                    try { File.Delete(oldPath); } catch { }
                }
            }

            await WriteThemeFileAsync(theme.FileName, content, cancellationToken);
            if (setActive)
            {
                config.ActiveThemeId = theme.Id;
            }

            NormalizeConfig(config);
            config.UpdatedAt = DateTimeOffset.Now;
            await WriteCoreAsync(config, cancellationToken);
            _cache = Clone(config);
            var active = GetActiveTheme(config);
            return await OkAsync("Đã lưu theme file.", config, active, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail("Không lưu được theme file: " + ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ThemeActionResultDto> ImportThemeFileAsync(string name, string fileName, string content, bool setActive, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureBuiltInThemeFilesAsync(cancellationToken);
            var config = await ReadCoreUnlockedAsync(cancellationToken);
            var safeFileName = MakeSafeThemeFileName(fileName);
            var id = Path.GetFileNameWithoutExtension(safeFileName).ToLowerInvariant().Replace(" ", "-");
            id = Regex.Replace(id, "[^a-z0-9_-]+", "-").Trim('-');
            if (string.IsNullOrWhiteSpace(id)) id = "theme-" + Guid.NewGuid().ToString("N")[..8];
            var originalId = id;
            var suffix = 2;
            while (config.Themes.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                id = originalId + "-" + suffix++;
            }

            var theme = new ThemeFileItemDto
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(safeFileName) : name.Trim(),
                Description = "Imported theme file",
                FileName = safeFileName,
                FileType = Path.GetExtension(safeFileName).TrimStart('.').ToLowerInvariant(),
                IsScss = string.Equals(Path.GetExtension(safeFileName), ".scss", StringComparison.OrdinalIgnoreCase),
                IsBuiltIn = false,
                ShellClass = "theme-layout-comfortable theme-nav-pills",
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now
            };

            config.Themes.Add(theme);
            await WriteThemeFileAsync(theme.FileName, content, cancellationToken);
            if (setActive)
            {
                config.ActiveThemeId = theme.Id;
            }

            NormalizeConfig(config);
            config.UpdatedAt = DateTimeOffset.Now;
            await WriteCoreAsync(config, cancellationToken);
            _cache = Clone(config);
            var active = GetActiveTheme(config);
            return await OkAsync("Đã import theme file.", config, active, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail("Không import được theme file: " + ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ThemeActionResultDto> SetActiveAsync(string themeId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureBuiltInThemeFilesAsync(cancellationToken);
            var config = await ReadCoreUnlockedAsync(cancellationToken);
            if (!config.Themes.Any(x => string.Equals(x.Id, themeId, StringComparison.OrdinalIgnoreCase)))
            {
                return Fail("Không tìm thấy theme để áp dụng.");
            }

            config.ActiveThemeId = themeId;
            config.UpdatedAt = DateTimeOffset.Now;
            await WriteCoreAsync(config, cancellationToken);
            _cache = Clone(config);
            var active = GetActiveTheme(config);
            return await OkAsync("Đã áp dụng theme: " + active.Name, config, active, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail("Không áp dụng được theme: " + ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ThemeActionResultDto> DuplicateThemeAsync(string themeId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureBuiltInThemeFilesAsync(cancellationToken);
            var config = await ReadCoreUnlockedAsync(cancellationToken);
            var source = config.Themes.FirstOrDefault(x => string.Equals(x.Id, themeId, StringComparison.OrdinalIgnoreCase));
            if (source is null)
            {
                return Fail("Không tìm thấy theme để nhân bản.");
            }

            var content = await ReadThemeFileAsync(source.FileName, cancellationToken);
            var duplicateId = source.Id + "-copy";
            var suffix = 2;
            while (config.Themes.Any(x => string.Equals(x.Id, duplicateId, StringComparison.OrdinalIgnoreCase)))
            {
                duplicateId = source.Id + "-copy-" + suffix++;
            }

            var ext = source.IsScss ? ".scss" : ".css";
            var fileName = MakeSafeThemeFileName(duplicateId + ext);
            var duplicate = Clone(source);
            duplicate.Id = duplicateId;
            duplicate.Name = source.Name + " Copy";
            duplicate.Description = "Duplicated from " + source.Name;
            duplicate.FileName = fileName;
            duplicate.FileType = ext.TrimStart('.');
            duplicate.IsBuiltIn = false;
            duplicate.CreatedAt = DateTimeOffset.Now;
            duplicate.UpdatedAt = DateTimeOffset.Now;
            config.Themes.Add(duplicate);
            await WriteThemeFileAsync(fileName, content, cancellationToken);
            config.UpdatedAt = DateTimeOffset.Now;
            await WriteCoreAsync(config, cancellationToken);
            _cache = Clone(config);
            var active = GetActiveTheme(config);
            return await OkAsync("Đã nhân bản theme thành file CSS/SCSS riêng.", config, active, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail("Không nhân bản được theme: " + ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ThemeActionResultDto> DeleteThemeAsync(string themeId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureBuiltInThemeFilesAsync(cancellationToken);
            var config = await ReadCoreUnlockedAsync(cancellationToken);
            var theme = config.Themes.FirstOrDefault(x => string.Equals(x.Id, themeId, StringComparison.OrdinalIgnoreCase));
            if (theme is null)
            {
                return Fail("Không tìm thấy theme để xóa.");
            }

            if (theme.IsBuiltIn)
            {
                return Fail("Theme built-in không xóa trực tiếp. Hãy duplicate rồi sửa bản custom.");
            }

            config.Themes.Remove(theme);
            var path = GetThemeFilePath(theme.FileName);
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch { }
            }

            if (string.Equals(config.ActiveThemeId, themeId, StringComparison.OrdinalIgnoreCase))
            {
                config.ActiveThemeId = config.Themes.FirstOrDefault()?.Id ?? "default-light";
            }

            NormalizeConfig(config);
            config.UpdatedAt = DateTimeOffset.Now;
            await WriteCoreAsync(config, cancellationToken);
            _cache = Clone(config);
            var active = GetActiveTheme(config);
            return await OkAsync("Đã xóa theme custom và file theme.", config, active, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail("Không xóa được theme: " + ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> ExportConfigJsonAsync(CancellationToken cancellationToken = default)
    {
        var response = await LoadResponseAsync(cancellationToken);
        return JsonSerializer.Serialize(response.Config, JsonOptions);
    }

    public async Task<string> ExportThemeFileAsync(string themeId, CancellationToken cancellationToken = default)
    {
        var edit = await GetThemeForEditAsync(themeId, cancellationToken);
        return edit.Content;
    }

    public async Task<string> ExportCurrentAppCssBundleAsync(CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder();
        builder.AppendLine("/* ConfigTool generated default theme bundle. */");
        foreach (var path in FindCurrentAppCssFiles())
        {
            if (!File.Exists(path)) continue;
            builder.AppendLine();
            builder.AppendLine("/* ===== " + Path.GetFileName(path) + " ===== */");
            builder.AppendLine(await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken));
        }
        if (builder.Length < 100)
        {
            builder.AppendLine(BuiltInThemeFiles.First(x => x.Id == "default-light").Content);
        }
        return builder.ToString();
    }

    public ThemeFileItemDto GetActiveTheme(ThemeConfigFileDto config)
    {
        NormalizeConfig(config);
        return Clone(config.Themes.FirstOrDefault(x => string.Equals(x.Id, config.ActiveThemeId, StringComparison.OrdinalIgnoreCase)) ?? config.Themes[0]);
    }

    public string GetThemeFilePath(string fileName)
    {
        Directory.CreateDirectory(ThemesDirectory);
        return Path.Combine(ThemesDirectory, MakeSafeThemeFileName(fileName));
    }

    private async Task<ThemeConfigFileDto> ReadCoreUnlockedAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return Clone(_cache);
        }

        Directory.CreateDirectory(_startupPathProvider.ConfigDirectory);
        Directory.CreateDirectory(ThemesDirectory);
        if (!File.Exists(ConfigFilePath))
        {
            var defaults = BuildDefaultConfig();
            await WriteCoreAsync(defaults, cancellationToken);
            _cache = Clone(defaults);
            return defaults;
        }

        try
        {
            var json = await File.ReadAllTextAsync(ConfigFilePath, Encoding.UTF8, cancellationToken);
            var config = JsonSerializer.Deserialize<ThemeConfigFileDto>(json, JsonOptions) ?? BuildDefaultConfig();
            NormalizeConfig(config);
            _cache = Clone(config);
            return config;
        }
        catch
        {
            var fallback = BuildDefaultConfig();
            await WriteCoreAsync(fallback, cancellationToken);
            _cache = Clone(fallback);
            return fallback;
        }
    }

    private async Task WriteCoreAsync(ThemeConfigFileDto config, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_startupPathProvider.ConfigDirectory);
        Directory.CreateDirectory(ThemesDirectory);
        NormalizeConfig(config);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        var temp = ConfigFilePath + ".tmp";
        await File.WriteAllTextAsync(temp, json, Encoding.UTF8, cancellationToken);
        if (File.Exists(ConfigFilePath))
        {
            File.Delete(ConfigFilePath);
        }
        File.Move(temp, ConfigFilePath);
    }

    private async Task<string> ReadThemeCssForApplyAsync(ThemeFileItemDto theme, CancellationToken cancellationToken)
    {
        var text = await ReadThemeFileAsync(theme.FileName, cancellationToken);
        if (theme.IsScss || string.Equals(theme.FileType, "scss", StringComparison.OrdinalIgnoreCase))
        {
            return "/* SCSS theme file is injected as plain CSS by ConfigTool. Keep runtime-safe CSS at top level. */\n" + text;
        }

        return text;
    }

    private async Task<string> ReadThemeFileAsync(string fileName, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ThemesDirectory);
        var path = GetThemeFilePath(fileName);
        if (!File.Exists(path))
        {
            await EnsureBuiltInThemeFilesAsync(cancellationToken);
        }

        return File.Exists(path) ? await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken) : string.Empty;
    }

    private async Task WriteThemeFileAsync(string fileName, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ThemesDirectory);
        var safeFile = MakeSafeThemeFileName(fileName);
        var path = GetThemeFilePath(safeFile);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content ?? string.Empty, Encoding.UTF8, cancellationToken);
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }

    private async Task EnsureBuiltInThemeFilesAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ThemesDirectory);
        foreach (var builtIn in BuiltInThemeFiles)
        {
            var path = GetThemeFilePath(builtIn.FileName);
            var content = builtIn.Id == "configtool-current-default"
                ? await ExportCurrentAppCssBundleAsync(cancellationToken)
                : await LoadShippedThemeContentAsync(builtIn, cancellationToken);
            if (!File.Exists(path) || !string.Equals(await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken), content, StringComparison.Ordinal))
            {
                await File.WriteAllTextAsync(path, content, Encoding.UTF8, cancellationToken);
            }
        }
    }

    private static ThemeConfigFileDto BuildDefaultConfig()
    {
        return new ThemeConfigFileDto
        {
            Version = "2.0",
            ActiveThemeId = "configtool-current-default",
            Themes = BuiltInThemeFiles.Select(x => new ThemeFileItemDto
            {
                Id = x.Id,
                Name = x.Name,
                Description = x.Description,
                FileName = x.FileName,
                FileType = Path.GetExtension(x.FileName).TrimStart('.').ToLowerInvariant(),
                IsScss = string.Equals(Path.GetExtension(x.FileName), ".scss", StringComparison.OrdinalIgnoreCase),
                ShellClass = x.ShellClass,
                IsBuiltIn = true,
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now
            }).ToList(),
            UpdatedAt = DateTimeOffset.Now
        };
    }

    private static void NormalizeConfig(ThemeConfigFileDto config)
    {
        config.Version = string.IsNullOrWhiteSpace(config.Version) ? "2.0" : config.Version;
        if (config.Themes.Count == 0)
        {
            config.Themes = BuildDefaultConfig().Themes;
        }
        else
        {
            foreach (var builtIn in BuildDefaultConfig().Themes.Where(x => x.IsBuiltIn))
            {
                if (config.Themes.All(x => !string.Equals(x.Id, builtIn.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    config.Themes.Add(builtIn);
                }
            }
        }

        foreach (var theme in config.Themes)
        {
            NormalizeTheme(theme);
        }

        if (string.IsNullOrWhiteSpace(config.ActiveThemeId) || config.Themes.All(x => !string.Equals(x.Id, config.ActiveThemeId, StringComparison.OrdinalIgnoreCase)))
        {
            config.ActiveThemeId = config.Themes.First().Id;
        }
    }

    private static void NormalizeTheme(ThemeFileItemDto theme)
    {
        theme.Id = string.IsNullOrWhiteSpace(theme.Id) ? Guid.NewGuid().ToString("N") : theme.Id.Trim();
        theme.Name = string.IsNullOrWhiteSpace(theme.Name) ? "Custom theme" : theme.Name.Trim();
        theme.FileName = MakeSafeThemeFileName(theme.FileName);
        var ext = Path.GetExtension(theme.FileName).ToLowerInvariant();
        if (ext is not ".css" and not ".scss")
        {
            theme.FileName = Path.GetFileNameWithoutExtension(theme.FileName) + ".css";
            ext = ".css";
        }
        theme.FileType = ext.TrimStart('.');
        theme.IsScss = string.Equals(ext, ".scss", StringComparison.OrdinalIgnoreCase);
        theme.ShellClass = NormalizeShellClass(theme.ShellClass);
    }

    private static string NormalizeShellClass(string? shellClass)
    {
        if (string.IsNullOrWhiteSpace(shellClass))
        {
            return "theme-layout-comfortable theme-nav-pills";
        }

        return string.Join(' ', shellClass.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.StartsWith("theme-", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string MakeSafeThemeFileName(string? fileName)
    {
        var safe = Path.GetFileName(string.IsNullOrWhiteSpace(fileName) ? "custom-theme.css" : fileName.Trim());
        safe = Regex.Replace(safe, "[^a-zA-Z0-9_.-]+", "-");
        if (string.IsNullOrWhiteSpace(safe)) safe = "custom-theme.css";
        var ext = Path.GetExtension(safe).ToLowerInvariant();
        if (ext is not ".css" and not ".scss") safe += ".css";
        return safe;
    }

    private async Task<ThemeActionResultDto> OkAsync(string message, ThemeConfigFileDto config, ThemeFileItemDto active, CancellationToken cancellationToken)
    {
        return new ThemeActionResultDto
        {
            Success = true,
            Message = message,
            Config = Clone(config),
            ActiveTheme = Clone(active),
            ThemeCssText = await ReadThemeCssForApplyAsync(active, cancellationToken),
            ThemeFilePath = GetThemeFilePath(active.FileName),
            ShellClass = NormalizeShellClass(active.ShellClass)
        };
    }

    private static ThemeActionResultDto Fail(string message) => new()
    {
        Success = false,
        Message = message
    };

    private static ThemeConfigFileDto Clone(ThemeConfigFileDto value)
    {
        return JsonSerializer.Deserialize<ThemeConfigFileDto>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions) ?? new ThemeConfigFileDto();
    }

    private static ThemeFileItemDto Clone(ThemeFileItemDto value)
    {
        return JsonSerializer.Deserialize<ThemeFileItemDto>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions) ?? new ThemeFileItemDto();
    }

    private async Task<string> LoadShippedThemeContentAsync(BuiltInThemeFile builtIn, CancellationToken cancellationToken)
    {
        var candidates = new[]
        {
            Path.Combine(_startupPathProvider.StartupPath, "themes", builtIn.FileName),
            Path.Combine(AppContext.BaseDirectory, "themes", builtIn.FileName),
            Path.Combine(Directory.GetCurrentDirectory(), "themes", builtIn.FileName)
        };
        foreach (var path in candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }
        return builtIn.Content;
    }

    private IEnumerable<string> FindCurrentAppCssFiles()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            _startupPathProvider.StartupPath,
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory()
        };

        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var directCandidates = new[]
            {
                Path.Combine(root, "wwwroot", "app.css"),
                Path.Combine(root, "app.css"),
                Path.Combine(root, "ConfigTool.styles.css")
            };

            foreach (var path in directCandidates.Where(File.Exists))
            {
                if (seen.Add(path))
                {
                    yield return path;
                }
            }

            foreach (var file in SafeEnumerateCssFiles(Path.Combine(root, "Components")))
            {
                if (seen.Add(file))
                {
                    yield return file;
                }
            }

            foreach (var file in SafeEnumerateCssFiles(Path.Combine(root, "wwwroot")))
            {
                if (seen.Add(file))
                {
                    yield return file;
                }
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateCssFiles(string folder)
    {
        if (!Directory.Exists(folder))
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path).Equals(".css", StringComparison.OrdinalIgnoreCase) ||
                               Path.GetExtension(path).Equals(".scss", StringComparison.OrdinalIgnoreCase))
                .Where(path => !Path.GetFileName(path).Contains("configtool-current-default", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            yield return file;
        }
    }

    private sealed record BuiltInThemeFile(string Id, string Name, string Description, string FileName, string ShellClass, string Content);

    private static readonly BuiltInThemeFile[] BuiltInThemeFiles =
    [
        new(
            "configtool-current-default",
            "ConfigTool Current Default",
            "Gom toàn bộ CSS hiện tại app đang dùng thành 1 file theme mặc định.",
            "configtool-current-default.css",
            "theme-layout-comfortable theme-nav-pills",
            """
            /* Fallback: runtime sẽ tự gom wwwroot/app.css + ConfigTool.styles.css vào file này. */
            :root { color-scheme: light; }
            """),
        new(
            "default-light",
            "Default Light",
            "CSS theme sáng mặc định. themeconfig.json chỉ lưu metadata, file này mới chứa style thật.",
            "default-light.css",
            "theme-layout-comfortable theme-nav-pills",
            """
            :root {
                color-scheme: light;
                --ct-bg: #f4f7fb;
                --ct-card: #ffffff;
                --ct-surface-soft: #f8fafc;
                --ct-border: rgba(15, 23, 42, .09);
                --ct-text: #14213d;
                --ct-muted: #64748b;
                --ct-primary: #4263eb;
                --ct-primary-dark: #2f49c7;
                --ct-green: #12b886;
                --ct-red: #f03e3e;
                --ct-orange: #f59f00;
                --ct-sidebar: #0f172a;
                --ct-sidebar-soft: rgba(255, 255, 255, .075);
                --ct-radius: 22px;
                --ct-panel-padding: 16px;
                --ct-main-max-width: none;
                --ct-font-scale: 1;
                --ct-shadow-strength: .08;
            }
            .ct-shell { font-size: calc(1rem * var(--ct-font-scale)); }
            .ct-topbar { background: rgba(255,255,255,.9); color: var(--ct-text); }
            .ct-main { max-width: var(--ct-main-max-width); }
            .ct-panel, .card { border-radius: var(--ct-radius) !important; }
            """),
        new(
            "dark-slate",
            "Dark Slate",
            "CSS theme tối hiện đại.",
            "dark-slate.css",
            "theme-layout-comfortable theme-nav-pills",
            """
            :root {
                color-scheme: dark;
                --ct-bg: #07111f;
                --ct-card: #101b2d;
                --ct-surface-soft: #162338;
                --ct-border: rgba(148, 163, 184, .2);
                --ct-text: #e5edf8;
                --ct-muted: #94a3b8;
                --ct-primary: #38bdf8;
                --ct-primary-dark: #0284c7;
                --ct-green: #34d399;
                --ct-red: #fb7185;
                --ct-orange: #fbbf24;
                --ct-sidebar: #020617;
                --ct-sidebar-soft: rgba(56, 189, 248, .12);
                --ct-radius: 20px;
                --ct-panel-padding: 16px;
                --ct-main-max-width: none;
                --ct-font-scale: 1;
                --ct-shadow-strength: .18;
            }
            body, .ct-shell { background: radial-gradient(circle at top left, rgba(56,189,248,.14), transparent 36%), var(--ct-bg); color: var(--ct-text); }
            .card, .ct-panel, .modal-content, .form-control, .form-select, .list-group-item { background: var(--ct-card) !important; color: var(--ct-text) !important; border-color: var(--ct-border) !important; }
            .ct-topbar { background: rgba(2, 6, 23, .9); color: var(--ct-text); }
            .ct-main { max-width: var(--ct-main-max-width); }
            .text-muted, .ct-muted { color: var(--ct-muted) !important; }
            """),
        new(
            "clean-blue",
            "Clean Blue",
            "Theme sáng xanh dương, dễ đọc, bố cục gọn.",
            "clean-blue.css",
            "theme-layout-compact theme-nav-pills",
            """
            :root {
                color-scheme: light;
                --ct-bg: #eef6ff;
                --ct-card: #ffffff;
                --ct-surface-soft: #f7fbff;
                --ct-border: rgba(37, 99, 235, .16);
                --ct-text: #0f2747;
                --ct-muted: #5b708f;
                --ct-primary: #2563eb;
                --ct-primary-dark: #1d4ed8;
                --ct-green: #059669;
                --ct-red: #dc2626;
                --ct-orange: #d97706;
                --ct-radius: 18px;
                --ct-main-max-width: 1760px;
                --ct-font-scale: .98;
            }
            body, .ct-shell { background: linear-gradient(135deg, #eef6ff 0%, #f9fbff 52%, #e0f2fe 100%) !important; color: var(--ct-text) !important; }
            .ct-topbar, .card, .ct-panel, .ct-mini-box, .modal-content { background: rgba(255,255,255,.92) !important; color: var(--ct-text) !important; border-color: var(--ct-border) !important; }
            .btn-primary, .nav-pills .nav-link.active, .ct-nav-pill.active { background: var(--ct-primary) !important; border-color: var(--ct-primary) !important; color: #fff !important; }
            .form-control, .form-select, .input-group-text { background: #fff !important; color: var(--ct-text) !important; border-color: rgba(37, 99, 235, .22) !important; }
            """),
        new(
            "emerald-day",
            "Emerald Day",
            "Theme sáng xanh lá, dịu mắt cho thao tác lâu.",
            "emerald-day.css",
            "theme-layout-compact theme-nav-pills",
            """
            :root {
                color-scheme: light;
                --ct-bg: #f0fdf4;
                --ct-card: #ffffff;
                --ct-surface-soft: #ecfdf5;
                --ct-border: rgba(5, 150, 105, .18);
                --ct-text: #123026;
                --ct-muted: #527064;
                --ct-primary: #059669;
                --ct-primary-dark: #047857;
                --ct-green: #10b981;
                --ct-red: #e11d48;
                --ct-orange: #f59e0b;
                --ct-radius: 18px;
                --ct-main-max-width: 1760px;
            }
            body, .ct-shell { background: radial-gradient(circle at top left, rgba(16,185,129,.18), transparent 34rem), #f0fdf4 !important; color: var(--ct-text) !important; }
            .ct-topbar, .card, .ct-panel, .ct-mini-box, .modal-content { background: rgba(255,255,255,.94) !important; color: var(--ct-text) !important; border-color: var(--ct-border) !important; }
            .btn-primary, .nav-pills .nav-link.active, .ct-nav-pill.active { background: var(--ct-primary) !important; border-color: var(--ct-primary) !important; color: #fff !important; }
            .form-control, .form-select, .input-group-text { background: #fff !important; color: var(--ct-text) !important; border-color: rgba(5,150,105,.22) !important; }
            """),
        new(
            "violet-glass",
            "Violet Glass",
            "Theme sáng tím xanh, glassmorphism nhẹ.",
            "violet-glass.css",
            "theme-layout-wide theme-nav-pills",
            """
            :root {
                color-scheme: light;
                --ct-bg: #f5f3ff;
                --ct-card: #ffffff;
                --ct-surface-soft: #faf5ff;
                --ct-border: rgba(124, 58, 237, .18);
                --ct-text: #2a1749;
                --ct-muted: #73638d;
                --ct-primary: #7c3aed;
                --ct-primary-dark: #6d28d9;
                --ct-green: #0d9488;
                --ct-red: #e11d48;
                --ct-orange: #ea580c;
                --ct-radius: 24px;
                --ct-main-max-width: 1840px;
                --ct-font-scale: 1;
            }
            body, .ct-shell { background: linear-gradient(135deg, #f5f3ff 0%, #eff6ff 45%, #fff7ed 100%) !important; color: var(--ct-text) !important; }
            .ct-topbar, .card, .ct-panel, .ct-mini-box, .modal-content { background: rgba(255,255,255,.82) !important; backdrop-filter: blur(18px); color: var(--ct-text) !important; border-color: var(--ct-border) !important; }
            .btn-primary, .nav-pills .nav-link.active, .ct-nav-pill.active { background: linear-gradient(135deg, #7c3aed, #2563eb) !important; border-color: transparent !important; color: #fff !important; }
            .form-control, .form-select, .input-group-text { background: rgba(255,255,255,.9) !important; color: var(--ct-text) !important; border-color: rgba(124,58,237,.24) !important; }
            """),
        new(
            "candy-modern-scss",
            "Candy Modern SCSS",
            "Ví dụ theme SCSS. ConfigTool lưu file .scss thật; runtime inject phần CSS-safe trong file.",
            "candy-modern.scss",
            "theme-layout-wide theme-nav-pills",
            """
            $primary: #ec4899;
            $mint: #14b8a6;

            :root {
                color-scheme: light;
                --ct-bg: #fff7fb;
                --ct-card: #ffffff;
                --ct-surface-soft: #fff0f7;
                --ct-border: rgba(236, 72, 153, .18);
                --ct-text: #35142d;
                --ct-muted: #8b5f7d;
                --ct-primary: #ec4899;
                --ct-primary-dark: #be185d;
                --ct-green: #14b8a6;
                --ct-red: #f43f5e;
                --ct-orange: #f59e0b;
                --ct-sidebar: #4a1238;
                --ct-sidebar-soft: rgba(255, 255, 255, .13);
                --ct-radius: 26px;
                --ct-panel-padding: 18px;
                --ct-main-max-width: 1680px;
                --ct-font-scale: 1.02;
                --ct-shadow-strength: .12;
            }

            .ct-shell { background: linear-gradient(135deg, #fff7fb 0%, #ecfeff 100%); color: var(--ct-text); }
            .ct-topbar { background: rgba(255, 255, 255, .86); backdrop-filter: blur(18px); }
            .ct-main { max-width: var(--ct-main-max-width); }
            .ct-panel, .card { border-radius: var(--ct-radius) !important; border-color: var(--ct-border) !important; }
            """)
    ];
}
