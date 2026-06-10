using System.Text.Json.Serialization;

namespace ConfigTool.Models;

public sealed class ThemeConfigFileDto
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "2.0";

    [JsonPropertyName("activeThemeId")]
    public string ActiveThemeId { get; set; } = "default-light";

    [JsonPropertyName("themes")]
    public List<ThemeFileItemDto> Themes { get; set; } = [];

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class ThemeFileItemDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Custom theme";

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "custom-theme.css";

    [JsonPropertyName("fileType")]
    public string FileType { get; set; } = "css";

    [JsonPropertyName("shellClass")]
    public string ShellClass { get; set; } = "theme-layout-comfortable theme-nav-pills";

    [JsonPropertyName("isBuiltIn")]
    public bool IsBuiltIn { get; set; }

    [JsonPropertyName("isScss")]
    public bool IsScss { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class ThemeConfigResponseDto
{
    public string ConfigFilePath { get; set; } = string.Empty;
    public string ThemesDirectory { get; set; } = string.Empty;
    public ThemeConfigFileDto Config { get; set; } = new();
    public ThemeFileItemDto ActiveTheme { get; set; } = new();
    public string ThemeCssText { get; set; } = string.Empty;
    public string ThemeFilePath { get; set; } = string.Empty;
    public string ShellClass { get; set; } = "theme-layout-comfortable theme-nav-pills";
}

public sealed class ThemeFileEditDto
{
    public ThemeFileItemDto Theme { get; set; } = new();
    public string Content { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
}

public sealed class ThemeActionResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public ThemeConfigFileDto? Config { get; set; }
    public ThemeFileItemDto? ActiveTheme { get; set; }
    public string ThemeCssText { get; set; } = string.Empty;
    public string ThemeFilePath { get; set; } = string.Empty;
    public string ShellClass { get; set; } = "theme-layout-comfortable theme-nav-pills";
}
