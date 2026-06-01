using ConfigTool.Models;
using ConfigTool.Services;
using Microsoft.AspNetCore.SignalR;

namespace ConfigTool.Hubs;

public sealed class ConfigToolHub : Hub
{
    private readonly ConfigToolSettingsService _settingsService;
    private readonly ConfigFolderValidator _folderValidator;
    private readonly JsonConfigRepository _repository;
    private readonly ConfigFileRealtimeHostedService _fileWatcher;
    private readonly SqlAdminService _sqlAdminService;

    public ConfigToolHub(
        ConfigToolSettingsService settingsService,
        ConfigFolderValidator folderValidator,
        JsonConfigRepository repository,
        ConfigFileRealtimeHostedService fileWatcher,
        SqlAdminService sqlAdminService)
    {
        _settingsService = settingsService;
        _folderValidator = folderValidator;
        _repository = repository;
        _fileWatcher = fileWatcher;
        _sqlAdminService = sqlAdminService;
    }

    public async Task<ConfigBootstrapDto> BootstrapAsync()
    {
        var cancellationToken = Context.ConnectionAborted;
        var settings = await _settingsService.LoadAsync(cancellationToken);
        var folder = _folderValidator.Validate(settings.ConfigFolderPath, settings.RequiredFileNames);
        var files = folder.IsValid
            ? await _repository.ScanFilesAsync(settings.ConfigFolderPath!, cancellationToken)
            : [];

        return new ConfigBootstrapDto
        {
            Settings = settings,
            SettingsFilePath = _settingsService.SettingsFilePath,
            Folder = folder,
            Files = files,
            Message = folder.Message
        };
    }

    public async Task<ConfigBootstrapDto> SetConfigFolderAsync(string folderPath)
    {
        var cancellationToken = Context.ConnectionAborted;
        var settings = await _settingsService.LoadAsync(cancellationToken);
        settings.ConfigFolderPath = folderPath;
        settings.LastFolderSelectedAt = DateTimeOffset.Now;

        var folder = _folderValidator.Validate(settings.ConfigFolderPath, settings.RequiredFileNames);
        if (folder.Exists)
        {
            await _settingsService.SaveAsync(settings, cancellationToken);
        }

        var files = folder.IsValid
            ? await _repository.ScanFilesAsync(settings.ConfigFolderPath!, cancellationToken)
            : [];

        await _fileWatcher.ResetWatchFolderAsync(folder.IsValid ? settings.ConfigFolderPath : null, cancellationToken);

        var result = new ConfigBootstrapDto
        {
            Settings = settings,
            SettingsFilePath = _settingsService.SettingsFilePath,
            Folder = folder,
            Files = files,
            Message = folder.Message
        };

        await Clients.Caller.SendAsync("FolderChanged", result, cancellationToken);
        return result;
    }

    public async Task<List<JsonConfigFileDto>> GetFilesAsync()
    {
        var cancellationToken = Context.ConnectionAborted;
        var folder = await GetValidFolderOrThrowAsync(cancellationToken);
        return await _repository.ScanFilesAsync(folder, cancellationToken);
    }

    public async Task<List<JsonTableDto>> GetTablesAsync(string fileName)
    {
        var cancellationToken = Context.ConnectionAborted;
        var folder = await GetValidFolderOrThrowAsync(cancellationToken);
        return await _repository.GetTablesAsync(folder, fileName, cancellationToken);
    }

    public async Task<JsonRowPageDto> QueryRowsAsync(string fileName, string tableName, string? search, int page, int pageSize)
    {
        var cancellationToken = Context.ConnectionAborted;
        var folder = await GetValidFolderOrThrowAsync(cancellationToken);
        return await _repository.QueryRowsAsync(folder, fileName, tableName, search, page, pageSize, cancellationToken);
    }

    public async Task<JsonRowPageDto> QueryRowsByFormAsync(JsonRowsQueryRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var folder = await GetValidFolderOrThrowAsync(cancellationToken);
        return await _repository.QueryRowsAsync(
            folder,
            request.FileName,
            request.TableName,
            request.Search,
            request.Page,
            request.PageSize,
            request.Filters,
            cancellationToken);
    }

    public async Task<JsonCrudResultDto> CreateRowAsync(JsonRowWriteRequest request, string? search, int page, int pageSize)
    {
        var cancellationToken = Context.ConnectionAborted;
        var folder = await GetValidFolderOrThrowAsync(cancellationToken);
        var result = await _repository.CreateRowAsync(folder, request, cancellationToken);
        if (result.Success)
        {
            result.Page = await _repository.QueryRowsAsync(folder, request.FileName, request.TableName, search, page, pageSize, cancellationToken);
            await Clients.All.SendAsync("ConfigChanged", request.FileName, request.TableName, result.Message, cancellationToken);
        }

        return result;
    }

    public async Task<JsonCrudResultDto> UpdateRowAsync(JsonRowWriteRequest request, string? search, int page, int pageSize)
    {
        var cancellationToken = Context.ConnectionAborted;
        var folder = await GetValidFolderOrThrowAsync(cancellationToken);
        var result = await _repository.UpdateRowAsync(folder, request, cancellationToken);
        if (result.Success)
        {
            result.Page = await _repository.QueryRowsAsync(folder, request.FileName, request.TableName, search, page, pageSize, cancellationToken);
            await Clients.All.SendAsync("ConfigChanged", request.FileName, request.TableName, result.Message, cancellationToken);
        }

        return result;
    }

    public async Task<JsonCrudResultDto> DeleteRowAsync(JsonRowDeleteRequest request, string? search, int page, int pageSize)
    {
        var cancellationToken = Context.ConnectionAborted;
        var folder = await GetValidFolderOrThrowAsync(cancellationToken);
        var result = await _repository.DeleteRowAsync(folder, request, cancellationToken);
        if (result.Success)
        {
            result.Page = await _repository.QueryRowsAsync(folder, request.FileName, request.TableName, search, page, pageSize, cancellationToken);
            await Clients.All.SendAsync("ConfigChanged", request.FileName, request.TableName, result.Message, cancellationToken);
        }

        return result;
    }


    public async Task<List<string>> SearchKeysAsync(string? fileName, string? keyword, int maxResults = 200)
    {
        var cancellationToken = Context.ConnectionAborted;
        var folder = await GetValidFolderOrThrowAsync(cancellationToken);
        return await _repository.SearchKeysAsync(folder, fileName, keyword, maxResults, cancellationToken);
    }

    public async Task<JsonCrudResultDto> CreateJsonFileAsync(JsonFileCreateRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var settings = await _settingsService.LoadAsync(cancellationToken);
        var folder = settings.ConfigFolderPath;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            throw new HubException("Chưa chọn thư mục config hợp lệ để tạo file JSON.");
        }

        var result = await _repository.CreateJsonFileAsync(folder, request, cancellationToken);
        if (result.Success)
        {
            var files = await _repository.ScanFilesAsync(folder, cancellationToken);
            await Clients.All.SendAsync("ConfigFilesChanged", new ConfigExternalChangeDto
            {
                FolderPath = folder,
                FileName = request.FileName,
                ChangeKind = "created",
                Folder = _folderValidator.Validate(folder, settings.RequiredFileNames),
                Files = files,
                Message = result.Message,
                ChangedAt = DateTimeOffset.Now
            }, cancellationToken);
        }

        return result;
    }


    public async Task<SqlProfilesResponseDto> GetSqlProfilesAsync()
    {
        var cancellationToken = Context.ConnectionAborted;
        try
        {
            return await _sqlAdminService.LoadProfilesAsync(cancellationToken);
        }
        catch (Exception ex) when (SqlAdminService.IsSoftSqlException(ex))
        {
            await SendSqlSoftErrorAsync(SqlAdminService.ToFriendlyDatabaseError(ex, "Không đọc được connect.json"));
            return new SqlProfilesResponseDto();
        }
    }

    public async Task<SqlActionResultDto> SaveSqlProfileAsync(SqlConnectionProfileDto profile)
    {
        var cancellationToken = Context.ConnectionAborted;
        try
        {
            var result = await _sqlAdminService.SaveProfileAsync(profile, cancellationToken);
            if (result.Success)
            {
                await Clients.All.SendAsync("SqlProfilesChanged", result.Message, cancellationToken);
            }

            return result;
        }
        catch (Exception ex) when (SqlAdminService.IsSoftSqlException(ex))
        {
            return new SqlActionResultDto
            {
                Success = false,
                Message = SqlAdminService.ToFriendlyDatabaseError(ex, "Không lưu được cấu hình SQL")
            };
        }
    }

    public async Task<SqlActionResultDto> DeleteSqlProfileAsync(string id)
    {
        var cancellationToken = Context.ConnectionAborted;
        try
        {
            var result = await _sqlAdminService.DeleteProfileAsync(id, cancellationToken);
            if (result.Success)
            {
                await Clients.All.SendAsync("SqlProfilesChanged", result.Message, cancellationToken);
            }

            return result;
        }
        catch (Exception ex) when (SqlAdminService.IsSoftSqlException(ex))
        {
            return new SqlActionResultDto
            {
                Success = false,
                Message = SqlAdminService.ToFriendlyDatabaseError(ex, "Không xóa được cấu hình SQL")
            };
        }
    }

    public Task<SqlActionResultDto> TestSqlConnectionAsync(string profileId)
    {
        var cancellationToken = Context.ConnectionAborted;
        return _sqlAdminService.TestConnectionAsync(profileId, cancellationToken);
    }

    public Task<SqlActionResultDto> TestSqlConnectionDraftAsync(SqlConnectionProfileDto profile)
    {
        var cancellationToken = Context.ConnectionAborted;
        return _sqlAdminService.TestConnectionAsync(profile, cancellationToken);
    }

    public async Task<List<SqlDatabaseDto>> GetSqlDatabasesAsync(string profileId)
    {
        var cancellationToken = Context.ConnectionAborted;
        try
        {
            return await _sqlAdminService.ListDatabasesAsync(profileId, cancellationToken);
        }
        catch (Exception ex) when (SqlAdminService.IsSoftSqlException(ex))
        {
            await SendSqlSoftErrorAsync(SqlAdminService.ToFriendlyDatabaseError(ex, "Không tải được danh sách database"));
            return [];
        }
    }

    public async Task<List<SqlTableDto>> GetSqlTablesAsync(string profileId, string databaseName, string? search)
    {
        var cancellationToken = Context.ConnectionAborted;
        try
        {
            return await _sqlAdminService.ListTablesAsync(profileId, databaseName, search, cancellationToken);
        }
        catch (Exception ex) when (SqlAdminService.IsSoftSqlException(ex))
        {
            await SendSqlSoftErrorAsync(SqlAdminService.ToFriendlyDatabaseError(ex, "Không tải được danh sách bảng"));
            return [];
        }
    }

    public async Task<List<SqlColumnDto>> GetSqlColumnsAsync(string profileId, string databaseName, string schema, string tableName)
    {
        var cancellationToken = Context.ConnectionAborted;
        try
        {
            return await _sqlAdminService.ListColumnsAsync(profileId, databaseName, schema, tableName, cancellationToken);
        }
        catch (Exception ex) when (SqlAdminService.IsSoftSqlException(ex))
        {
            await SendSqlSoftErrorAsync(SqlAdminService.ToFriendlyDatabaseError(ex, "Không tải được danh sách cột"));
            return [];
        }
    }

    public async Task<List<SqlForeignKeyDto>> GetSqlForeignKeysAsync(string profileId, string databaseName, string schema, string tableName, string? search)
    {
        var cancellationToken = Context.ConnectionAborted;
        try
        {
            return await _sqlAdminService.ListForeignKeysAsync(profileId, databaseName, schema, tableName, search, cancellationToken);
        }
        catch (Exception ex) when (SqlAdminService.IsSoftSqlException(ex))
        {
            await SendSqlSoftErrorAsync(SqlAdminService.ToFriendlyDatabaseError(ex, "Không tải được khóa ngoại"));
            return [];
        }
    }

    public async Task<SqlRowPageDto> QuerySqlRecordsAsync(SqlRecordQueryRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        try
        {
            return await _sqlAdminService.QueryRecordsAsync(request, cancellationToken);
        }
        catch (Exception ex) when (SqlAdminService.IsSoftSqlException(ex))
        {
            await SendSqlSoftErrorAsync(SqlAdminService.ToFriendlyDatabaseError(ex, "Không tải được record"));
            return new SqlRowPageDto
            {
                DatabaseName = request.DatabaseName,
                Schema = request.Schema,
                TableName = request.TableName,
                Page = Math.Max(1, request.Page),
                PageSize = Math.Clamp(request.PageSize <= 0 ? 20 : request.PageSize, 5, 200),
                TotalPages = 1,
                TotalRows = 0,
                Columns = [],
                Rows = []
            };
        }
    }

    public async Task<SqlQueryResultDto> ExecuteSqlQueryAsync(SqlQueryRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        try
        {
            var result = await _sqlAdminService.ExecuteQueryAsync(request, cancellationToken);
            if (!result.Success && !string.IsNullOrWhiteSpace(result.Message))
            {
                await SendSqlSoftErrorAsync(result.Message);
            }

            return result;
        }
        catch (Exception ex) when (SqlAdminService.IsSoftSqlException(ex))
        {
            var message = SqlAdminService.ToFriendlyDatabaseError(ex, "Không chạy được query");
            await SendSqlSoftErrorAsync(message);
            return new SqlQueryResultDto
            {
                Success = false,
                Message = message
            };
        }
    }


    public async Task<SqlActionResultDto> CreateSqlDatabaseAsync(SqlDatabaseCrudRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.CreateDatabaseAsync(request, cancellationToken);
        await NotifySqlCrudResultAsync(result, cancellationToken);
        return result;
    }

    public async Task<SqlActionResultDto> RenameSqlDatabaseAsync(SqlDatabaseCrudRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.RenameDatabaseAsync(request, cancellationToken);
        await NotifySqlCrudResultAsync(result, cancellationToken);
        return result;
    }

    public async Task<SqlActionResultDto> DropSqlDatabaseAsync(SqlDatabaseCrudRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.DropDatabaseAsync(request, cancellationToken);
        await NotifySqlCrudResultAsync(result, cancellationToken);
        return result;
    }

    public async Task<SqlActionResultDto> CreateSqlTableAsync(SqlTableCrudRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.CreateTableAsync(request, cancellationToken);
        await NotifySqlCrudResultAsync(result, cancellationToken);
        return result;
    }

    public async Task<SqlActionResultDto> RenameSqlTableAsync(SqlTableCrudRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.RenameTableAsync(request, cancellationToken);
        await NotifySqlCrudResultAsync(result, cancellationToken);
        return result;
    }

    public async Task<SqlActionResultDto> DropSqlTableAsync(SqlTableCrudRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.DropTableAsync(request, cancellationToken);
        await NotifySqlCrudResultAsync(result, cancellationToken);
        return result;
    }

    public async Task<SqlActionResultDto> AddSqlColumnAsync(SqlColumnCrudRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.AddColumnAsync(request, cancellationToken);
        await NotifySqlCrudResultAsync(result, cancellationToken);
        return result;
    }

    public async Task<SqlActionResultDto> UpdateSqlColumnAsync(SqlColumnCrudRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.UpdateColumnAsync(request, cancellationToken);
        await NotifySqlCrudResultAsync(result, cancellationToken);
        return result;
    }

    public async Task<SqlActionResultDto> DropSqlColumnAsync(SqlColumnCrudRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.DropColumnAsync(request, cancellationToken);
        await NotifySqlCrudResultAsync(result, cancellationToken);
        return result;
    }

    public async Task<SqlActionResultDto> AddSqlForeignKeyAsync(SqlForeignKeyCrudRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.AddForeignKeyAsync(request, cancellationToken);
        await NotifySqlCrudResultAsync(result, cancellationToken);
        return result;
    }

    public async Task<SqlActionResultDto> UpdateSqlForeignKeyAsync(SqlForeignKeyCrudRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.UpdateForeignKeyAsync(request, cancellationToken);
        await NotifySqlCrudResultAsync(result, cancellationToken);
        return result;
    }

    public async Task<SqlActionResultDto> DropSqlForeignKeyAsync(SqlForeignKeyCrudRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.DropForeignKeyAsync(request, cancellationToken);
        await NotifySqlCrudResultAsync(result, cancellationToken);
        return result;
    }

    public async Task<SqlActionResultDto> InsertSqlRecordAsync(SqlRecordWriteRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.InsertRecordAsync(request, cancellationToken);
        await NotifySqlCrudResultAsync(result, cancellationToken);
        return result;
    }

    public async Task<SqlActionResultDto> UpdateSqlRecordAsync(SqlRecordWriteRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.UpdateRecordAsync(request, cancellationToken);
        await NotifySqlCrudResultAsync(result, cancellationToken);
        return result;
    }

    public async Task<SqlActionResultDto> DeleteSqlRecordAsync(SqlRecordDeleteRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.DeleteRecordAsync(request, cancellationToken);
        await NotifySqlCrudResultAsync(result, cancellationToken);
        return result;
    }

    public async Task<SqlQueryResultDto> ImportSqlScriptAsync(SqlImportRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.ImportSqlAsync(request, cancellationToken);
        if (!result.Success)
        {
            await SendSqlSoftErrorAsync(result.Message);
        }
        else
        {
            await Clients.All.SendAsync("SqlSchemaChanged", result.Message, cancellationToken);
        }

        return result;
    }

    public async Task<SqlQueryResultDto> ExportSqlScriptAsync(SqlExportRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.ExportSqlAsync(request, cancellationToken);
        if (!result.Success)
        {
            await SendSqlSoftErrorAsync(result.Message);
        }

        return result;
    }

    public async Task<List<SqlQueryTemplateDto>> GetSqlQueryTemplatesAsync(string profileId, string databaseName, string schema, string tableName)
    {
        var cancellationToken = Context.ConnectionAborted;
        try
        {
            return await _sqlAdminService.BuildQueryTemplatesAsync(profileId, databaseName, schema, tableName, cancellationToken);
        }
        catch (Exception ex) when (SqlAdminService.IsSoftSqlException(ex))
        {
            await SendSqlSoftErrorAsync(SqlAdminService.ToFriendlyDatabaseError(ex, "Không tạo được query mẫu"));
            return [];
        }
    }

    private async Task NotifySqlCrudResultAsync(SqlActionResultDto result, CancellationToken cancellationToken)
    {
        if (result.Success)
        {
            await Clients.All.SendAsync("SqlSchemaChanged", result.Message, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(result.Message))
        {
            await SendSqlSoftErrorAsync(result.Message);
        }
    }

    private async Task SendSqlSoftErrorAsync(string message)
    {
        try
        {
            await Clients.Caller.SendAsync("SqlSoftError", message, CancellationToken.None);
        }
        catch
        {
            // Không để lỗi báo lỗi làm hỏng luồng SignalR chính.
        }
    }

    private async Task<string> GetValidFolderOrThrowAsync(CancellationToken cancellationToken)
    {
        var settings = await _settingsService.LoadAsync(cancellationToken);
        var folder = _folderValidator.Validate(settings.ConfigFolderPath, settings.RequiredFileNames);
        if (!folder.IsValid || string.IsNullOrWhiteSpace(settings.ConfigFolderPath))
        {
            throw new HubException(folder.Message);
        }

        return settings.ConfigFolderPath;
    }
}
