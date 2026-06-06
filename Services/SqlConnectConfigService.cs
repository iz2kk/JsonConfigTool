using System.Text.Encodings.Web;
using System.Text.Json;
using ConfigTool.Models;

namespace ConfigTool.Services;

public sealed class SqlConnectConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IAppStartupPathProvider _pathProvider;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public SqlConnectConfigService(IAppStartupPathProvider pathProvider)
    {
        _pathProvider = pathProvider;
    }

    public string ConfigDirectory => Path.Combine(_pathProvider.StartupPath, "config");
    public string ConnectFilePath => Path.Combine(ConfigDirectory, "connect.json");

    public async Task<SqlProfilesResponseDto> LoadAsync(CancellationToken cancellationToken = default)
    {
        await EnsureFileAsync(cancellationToken);
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            await using var stream = File.Open(ConnectFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var config = await JsonSerializer.DeserializeAsync<SqlConnectConfigFile>(stream, JsonOptions, cancellationToken) ?? new SqlConnectConfigFile();
            Normalize(config);
            return new SqlProfilesResponseDto
            {
                ConnectFilePath = ConnectFilePath,
                Profiles = config.Connect
            };
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<SqlActionResultDto> SaveProfileAsync(SqlConnectionProfileDto profile, CancellationToken cancellationToken = default)
    {
        await EnsureFileAsync(cancellationToken);
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var config = await LoadNoLockAsync(cancellationToken);
            NormalizeProfile(profile);
            var index = config.Connect.FindIndex(x => string.Equals(x.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                config.Connect[index] = profile;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(profile.Id))
                {
                    profile.Id = Guid.NewGuid().ToString("N");
                }

                config.Connect.Add(profile);
            }

            await SaveNoLockAsync(config, cancellationToken);
            return new SqlActionResultDto { Success = true, Message = "Đã lưu connect.json." };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return new SqlActionResultDto { Success = false, Message = ex.Message };
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<SqlActionResultDto> DeleteProfileAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureFileAsync(cancellationToken);
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var config = await LoadNoLockAsync(cancellationToken);
            var removed = config.Connect.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            await SaveNoLockAsync(config, cancellationToken);
            return new SqlActionResultDto
            {
                Success = true,
                Message = removed > 0 ? "Đã xóa cấu hình kết nối." : "Không tìm thấy cấu hình cần xóa."
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SqlActionResultDto { Success = false, Message = ex.Message };
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<SqlConnectionProfileDto> GetProfileOrThrowAsync(string id, CancellationToken cancellationToken = default)
    {
        var response = await LoadAsync(cancellationToken);
        var profile = response.Profiles.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            throw new InvalidOperationException("Không tìm thấy cấu hình kết nối SQL trong connect.json.");
        }

        NormalizeProfile(profile);
        return profile;
    }

    private async Task EnsureFileAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ConfigDirectory);
        if (File.Exists(ConnectFilePath))
        {
            return;
        }

        var sample = new SqlConnectConfigFile
        {
            Connect =
            [
                new SqlConnectionProfileDto
                {
                    Name = "Local MySQL",
                    TypeConnect = "mysql",
                    Host = "127.0.0.1",
                    Port = "3306",
                    User = "root",
                    Password = string.Empty,
                    AllowNoPassword = true,
                    Database = string.Empty
                },
                new SqlConnectionProfileDto
                {
                    Name = "Local MariaDB",
                    TypeConnect = "mariadb",
                    Host = "127.0.0.1",
                    Port = "3306",
                    User = "root",
                    Password = string.Empty,
                    AllowNoPassword = true,
                    Database = string.Empty
                },
                new SqlConnectionProfileDto
                {
                    Name = "Local SQL Server",
                    TypeConnect = "sqlserver",
                    Host = "127.0.0.1",
                    Port = "1433",
                    User = "sa",
                    Password = string.Empty,
                    AllowNoPassword = false,
                    Database = string.Empty,
                    Encrypt = false,
                    TrustServerCertificate = true
                }
            ]
        };

        await using var stream = File.Open(ConnectFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, sample, JsonOptions, cancellationToken);
    }

    private async Task<SqlConnectConfigFile> LoadNoLockAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.Open(ConnectFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var config = await JsonSerializer.DeserializeAsync<SqlConnectConfigFile>(stream, JsonOptions, cancellationToken) ?? new SqlConnectConfigFile();
        Normalize(config);
        return config;
    }

    private async Task SaveNoLockAsync(SqlConnectConfigFile config, CancellationToken cancellationToken)
    {
        Normalize(config);
        var tempPath = ConnectFilePath + ".tmp";
        await using (var stream = File.Open(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, config, JsonOptions, cancellationToken);
        }

        File.Copy(tempPath, ConnectFilePath, overwrite: true);
        File.Delete(tempPath);
    }

    private static void Normalize(SqlConnectConfigFile config)
    {
        config.Connect ??= [];
        foreach (var profile in config.Connect)
        {
            NormalizeProfile(profile);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in config.Connect)
        {
            while (!seen.Add(profile.Id))
            {
                profile.Id = Guid.NewGuid().ToString("N");
            }
        }
    }

    public static void NormalizeProfile(SqlConnectionProfileDto profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            profile.Id = Guid.NewGuid().ToString("N");
        }

        profile.TypeConnect = NormalizeType(profile.TypeConnect);
        profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? profile.TypeConnect.ToUpperInvariant() : profile.Name.Trim();
        profile.Host = string.IsNullOrWhiteSpace(profile.Host) ? "127.0.0.1" : profile.Host.Trim();
        profile.Port = string.IsNullOrWhiteSpace(profile.Port)
            ? (profile.TypeConnect is "sqlserver" ? "1433" : "3306")
            : profile.Port.Trim();
        profile.User = profile.User?.Trim() ?? string.Empty;
        profile.Database = profile.Database?.Trim();
        profile.TimeoutSeconds = Math.Clamp(profile.TimeoutSeconds <= 0 ? 15 : profile.TimeoutSeconds, 3, 120);
    }

    public static string NormalizeType(string? type)
        => type?.Trim().ToLowerInvariant() switch
        {
            "mariadb" => "mariadb",
            "maria" => "mariadb",
            "mysql" => "mysql",
            "mssql" => "sqlserver",
            "sql" => "sqlserver",
            "sqlserver" => "sqlserver",
            "sql server" => "sqlserver",
            _ => "mysql"
        };

    public static string GetDisplayType(string? type) => NormalizeType(type) switch
    {
        "mariadb" => "MariaDB",
        "sqlserver" => "SQL Server / MSSQL",
        _ => "MySQL"
    };
}
