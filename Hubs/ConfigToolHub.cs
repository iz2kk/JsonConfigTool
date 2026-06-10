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
    private readonly DynamicDnsUpdateService _dynamicDnsService;
    private readonly GitAdminService _gitAdminService;
    private readonly ConfigToolOperationCancellationService _operationCancellationService;

    public ConfigToolHub(
        ConfigToolSettingsService settingsService,
        ConfigFolderValidator folderValidator,
        JsonConfigRepository repository,
        ConfigFileRealtimeHostedService fileWatcher,
        SqlAdminService sqlAdminService,
        DynamicDnsUpdateService dynamicDnsService,
        GitAdminService gitAdminService,
        ConfigToolOperationCancellationService operationCancellationService)
    {
        _settingsService = settingsService;
        _folderValidator = folderValidator;
        _repository = repository;
        _fileWatcher = fileWatcher;
        _sqlAdminService = sqlAdminService;
        _dynamicDnsService = dynamicDnsService;
        _gitAdminService = gitAdminService;
        _operationCancellationService = operationCancellationService;
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

    public async Task<SqlServerInfoDto> GetSqlServerInfoAsync(string profileId)
    {
        var cancellationToken = Context.ConnectionAborted;
        try
        {
            return await _sqlAdminService.GetServerInfoAsync(profileId, cancellationToken);
        }
        catch (Exception ex) when (SqlAdminService.IsSoftSqlException(ex))
        {
            await SendSqlSoftErrorAsync(SqlAdminService.ToFriendlyDatabaseError(ex, "Không tải được thông tin server"));
            return new SqlServerInfoDto { Status = "offline" };
        }
    }

    public async Task<List<SqlDatabaseDto>> GetSqlDatabasesAsync(string profileId, string? search)
    {
        var cancellationToken = Context.ConnectionAborted;
        try
        {
            return await _sqlAdminService.ListDatabasesAsync(profileId, search, cancellationToken);
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

    public async Task<List<SqlIndexDto>> GetSqlIndexesAsync(string profileId, string databaseName, string schema, string tableName, string? search)
    {
        var cancellationToken = Context.ConnectionAborted;
        try
        {
            return await _sqlAdminService.ListIndexesAsync(profileId, databaseName, schema, tableName, search, cancellationToken);
        }
        catch (Exception ex) when (SqlAdminService.IsSoftSqlException(ex))
        {
            await SendSqlSoftErrorAsync(SqlAdminService.ToFriendlyDatabaseError(ex, "Không tải được key/index"));
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

    public async Task<SqlActionResultDto> AddSqlIndexAsync(SqlIndexCrudRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.AddIndexAsync(request, cancellationToken);
        await NotifySqlCrudResultAsync(result, cancellationToken);
        return result;
    }

    public async Task<SqlActionResultDto> UpdateSqlIndexAsync(SqlIndexCrudRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.UpdateIndexAsync(request, cancellationToken);
        await NotifySqlCrudResultAsync(result, cancellationToken);
        return result;
    }

    public async Task<SqlActionResultDto> DropSqlIndexAsync(SqlIndexCrudRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.DropIndexAsync(request, cancellationToken);
        await NotifySqlCrudResultAsync(result, cancellationToken);
        return result;
    }

    public async Task<SqlActionResultDto> TruncateSqlTableAsync(SqlTableCrudRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.TruncateTableAsync(request, cancellationToken);
        await NotifySqlCrudResultAsync(result, cancellationToken);
        return result;
    }

    public async Task<SqlActionResultDto> CopySqlTableAsync(SqlTableCrudRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.CopyTableAsync(request, cancellationToken);
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




    public async Task<SqlMaintenanceResultDto> RunSqlMaintenanceAsync(SqlMaintenanceRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.RunMaintenanceAsync(request, cancellationToken);
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

    public async Task<List<SqlProcessDto>> GetSqlProcessesAsync(string profileId)
    {
        var cancellationToken = Context.ConnectionAborted;
        try
        {
            return await _sqlAdminService.ListProcessesAsync(profileId, cancellationToken);
        }
        catch (Exception ex) when (SqlAdminService.IsSoftSqlException(ex))
        {
            await SendSqlSoftErrorAsync(SqlAdminService.ToFriendlyDatabaseError(ex, "Không tải được process list"));
            return [];
        }
    }

    public async Task<SqlActionResultDto> KillSqlProcessAsync(SqlKillProcessRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _sqlAdminService.KillProcessAsync(request, cancellationToken);
        await NotifySqlCrudResultAsync(result, cancellationToken);
        return result;
    }

    public async Task<List<SqlVariableDto>> GetSqlVariablesAsync(string profileId, string? search, bool includeStatus)
    {
        var cancellationToken = Context.ConnectionAborted;
        try
        {
            return await _sqlAdminService.ListVariablesAsync(profileId, search, includeStatus, cancellationToken);
        }
        catch (Exception ex) when (SqlAdminService.IsSoftSqlException(ex))
        {
            await SendSqlSoftErrorAsync(SqlAdminService.ToFriendlyDatabaseError(ex, "Không tải được variables/status"));
            return [];
        }
    }

    public async Task<SqlDesignerDto> GetSqlDesignerAsync(SqlDesignerRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        try
        {
            return await _sqlAdminService.GetDesignerAsync(request, cancellationToken);
        }
        catch (Exception ex) when (SqlAdminService.IsSoftSqlException(ex))
        {
            await SendSqlSoftErrorAsync(SqlAdminService.ToFriendlyDatabaseError(ex, "Không tải được designer relation"));
            return new SqlDesignerDto { DatabaseName = request.DatabaseName };
        }
    }




    public async Task<GitActionResultDto> CancelGitTabActionsAsync(string tabKey)
    {
        var scope = BuildGitScope(tabKey);
        var count = _operationCancellationService.CancelByPrefix(Context.ConnectionId, scope, "Người dùng đã hủy hành động trong tab Git: " + tabKey);
        var message = count > 0 ? $"Đã gửi lệnh hủy {count} hành động trong tab {tabKey}." : $"Không có hành động đang chạy trong tab {tabKey}.";
        await SendGitConsoleAsync(count > 0 ? "warn" : "info", "cancel", message, Context.ConnectionAborted);
        return new GitActionResultDto { Success = true, Message = message };
    }

    public async Task<GitActionResultDto> CancelAllGitActionsAsync()
    {
        var count = _operationCancellationService.CancelByPrefix(Context.ConnectionId, "git", "Người dùng đã hủy tất cả hành động Git.");
        var message = count > 0 ? $"Đã gửi lệnh hủy {count} hành động Git." : "Không có hành động Git nào đang chạy.";
        await SendGitConsoleAsync(count > 0 ? "warn" : "info", "cancel", message, Context.ConnectionAborted);
        return new GitActionResultDto { Success = true, Message = message };
    }

    private async Task<T> RunGitTabOperationAsync<T>(string tabKey, Func<CancellationToken, Task<T>> action)
    {
        var sendToken = Context.ConnectionAborted;
        using var operation = _operationCancellationService.Begin(Context.ConnectionId, BuildGitScope(tabKey), sendToken);
        try
        {
            return await action(operation.Token);
        }
        catch (OperationCanceledException)
        {
            await SendGitConsoleAsync("warn", "cancel", operation.CancelReason ?? "Đã hủy hành động Git.", sendToken);
            throw;
        }
    }

    private async Task<GitActionResultDto> RunGitActionOperationAsync(string tabKey, Func<CancellationToken, Task<GitActionResultDto>> action)
    {
        var sendToken = Context.ConnectionAborted;
        using var operation = _operationCancellationService.Begin(Context.ConnectionId, BuildGitScope(tabKey), sendToken);
        try
        {
            return await action(operation.Token);
        }
        catch (OperationCanceledException)
        {
            var message = operation.CancelReason ?? "Đã hủy hành động Git.";
            await SendGitConsoleAsync("warn", "cancel", message, sendToken);
            return new GitActionResultDto { Success = false, Message = message };
        }
    }

    private static string BuildGitScope(string? tabKey)
        => "git:" + (string.IsNullOrWhiteSpace(tabKey) ? "commands" : tabKey.Trim().ToLowerInvariant());

    public Task<GitConfigResponseDto> GetGitConfigAsync()
    {
        var cancellationToken = Context.ConnectionAborted;
        return _gitAdminService.LoadConfigAsync(cancellationToken);
    }

    public async Task<GitActionResultDto> SaveGitAccountAsync(GitAccountDto account)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _gitAdminService.SaveAccountAsync(account, cancellationToken);
        if (result.Success)
        {
            await Clients.All.SendAsync("GitConfigChanged", result.Message, cancellationToken);
        }
        else
        {
            await SendGitConsoleAsync("error", "config", result.Message, cancellationToken);
        }

        return result;
    }

    public async Task<GitActionResultDto> DeleteGitAccountAsync(string accountId)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _gitAdminService.DeleteAccountAsync(accountId, cancellationToken);
        if (result.Success)
        {
            await Clients.All.SendAsync("GitConfigChanged", result.Message, cancellationToken);
        }
        else
        {
            await SendGitConsoleAsync("error", "config", result.Message, cancellationToken);
        }

        return result;
    }

    public async Task<GitCreatedRepositoryDto> TestGitAccountAsync(string accountId)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _gitAdminService.TestAccountAsync(accountId, cancellationToken);
        await SendGitConsoleAsync(result.Success ? "success" : "error", "account", result.Message, cancellationToken);
        return result;
    }

    public async Task<GitOAuthResultDto> StartGitOAuthAsync(GitOAuthRequestDto request)
    {
        return await RunGitTabOperationAsync("account", async cancellationToken =>
        {
            var sendToken = Context.ConnectionAborted;
            var result = await _gitAdminService.StartOAuthLoginAsync(request, entry => Clients.Caller.SendAsync("GitConsoleEntry", entry, sendToken), cancellationToken);
            await SendGitConsoleAsync(result.Success ? "success" : "error", "oauth", result.Message, sendToken);
            if (result.Success)
            {
                await Clients.All.SendAsync("GitConfigChanged", result.Message, sendToken);
            }

            return result;
        });
    }


    public async Task<GitRepositoryListResponseDto> ListGitRepositoriesAsync(GitRepositoryListRequestDto request)
    {
        return await RunGitTabOperationAsync("repositories", cancellationToken =>
            _gitAdminService.ListRepositoriesAsync(request, entry => Clients.Caller.SendAsync("GitConsoleEntry", entry, Context.ConnectionAborted), cancellationToken));
    }

    public async Task<GitBatchAccountActionResponseDto> TestGitAccountsAsync(GitBatchAccountActionRequestDto request)
    {
        return await RunGitTabOperationAsync("account", cancellationToken =>
            _gitAdminService.TestAccountsAsync(request, entry => Clients.Caller.SendAsync("GitConsoleEntry", entry, Context.ConnectionAborted), cancellationToken));
    }

    public async Task<GitRepositoryListResponseDto> ListGitRepositoriesForAccountsAsync(GitBatchRepositoryListRequestDto request)
    {
        return await RunGitTabOperationAsync("repositories", cancellationToken =>
            _gitAdminService.ListRepositoriesForAccountsAsync(request, entry => Clients.Caller.SendAsync("GitConsoleEntry", entry, Context.ConnectionAborted), cancellationToken));
    }

    public async Task<GitBatchAccountActionResponseDto> CreateGitRepositoryForAccountsAsync(GitBatchCreateRepositoryRequestDto request)
    {
        return await RunGitTabOperationAsync("clone", cancellationToken =>
            _gitAdminService.CreateRepositoryForAccountsAsync(request, entry => Clients.Caller.SendAsync("GitConsoleEntry", entry, Context.ConnectionAborted), cancellationToken));
    }

    public async Task<GitTrafficStatsDto> GetGitRepositoryTrafficAsync(GitTrafficRequestDto request)
    {
        return await RunGitTabOperationAsync("traffic", cancellationToken =>
            _gitAdminService.GetRepositoryTrafficAsync(request, entry => Clients.Caller.SendAsync("GitConsoleEntry", entry, Context.ConnectionAborted), cancellationToken));
    }

    public async Task<GitCreatedRepositoryDto> CreateGitRepositoryAsync(GitCreateRepositoryRequestDto request)
    {
        return await RunGitTabOperationAsync("clone", async cancellationToken =>
            await _gitAdminService.CreateRepositoryAsync(request, entry => Clients.Caller.SendAsync("GitConsoleEntry", entry, Context.ConnectionAborted), cancellationToken));
    }

    public async Task<GitActionResultDto> SaveGitProjectFolderAsync(string folderPath)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _gitAdminService.SaveLastProjectFolderAsync(folderPath, cancellationToken);
        if (result.Success)
        {
            await Clients.All.SendAsync("GitConfigChanged", result.Message, cancellationToken);
        }

        return result;
    }

    public async Task<GitActionResultDto> SaveGitCloneFolderAsync(string folderPath)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _gitAdminService.SaveLastCloneFolderAsync(folderPath, cancellationToken);
        if (result.Success)
        {
            await Clients.All.SendAsync("GitConfigChanged", result.Message, cancellationToken);
        }

        return result;
    }

    public Task<GitOAuth2ConfigDto> GetGitOAuth2ConfigAsync()
    {
        var cancellationToken = Context.ConnectionAborted;
        return _gitAdminService.LoadOAuth2ConfigAsync(cancellationToken);
    }

    public async Task<GitOAuth2ConfigDto> SaveGitOAuth2ConfigAsync(GitOAuth2ConfigDto settings)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _gitAdminService.SaveOAuth2ConfigAsync(settings, cancellationToken);
        await SendGitConsoleAsync("success", "oauth", $"Đã lưu OAuth2.json callback port {result.CallbackPort}.", cancellationToken);
        return result;
    }

    public Task<GitWorkspaceResponseDto> GetGitWorkspacesAsync()
    {
        var cancellationToken = Context.ConnectionAborted;
        return _gitAdminService.LoadWorkspacesAsync(cancellationToken);
    }

    public async Task<GitActionResultDto> SaveGitWorkspaceAsync(GitWorkspaceDto workspace)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _gitAdminService.SaveWorkspaceAsync(workspace, cancellationToken);
        if (result.Success)
        {
            await Clients.All.SendAsync("GitConfigChanged", result.Message, cancellationToken);
        }
        return result;
    }

    public async Task<GitActionResultDto> DeleteGitWorkspaceAsync(string workspaceId)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _gitAdminService.DeleteWorkspaceAsync(workspaceId, cancellationToken);
        if (result.Success)
        {
            await Clients.All.SendAsync("GitConfigChanged", result.Message, cancellationToken);
        }
        return result;
    }

    public async Task<GitBatchAccountActionResponseDto> RunGitWorkspaceActionsAsync(GitBatchWorkspaceActionRequestDto request)
    {
        return await RunGitTabOperationAsync("workspaces", cancellationToken =>
            _gitAdminService.RunWorkspaceActionsAsync(request, entry => Clients.Caller.SendAsync("GitConsoleEntry", entry, Context.ConnectionAborted), cancellationToken));
    }

    public Task<GitRepositoryStatusDto> GetGitRepositoryStatusAsync(string folderPath)
    {
        var cancellationToken = Context.ConnectionAborted;
        return _gitAdminService.GetRepositoryStatusAsync(folderPath, cancellationToken);
    }

    public async Task<GitFileExplorerDto> GetGitFileExplorerAsync(string folderPath, string? search, int page = 1, int pageSize = 50, bool includeCommitInfo = true)
    {
        return await RunGitTabOperationAsync("explorer", cancellationToken => _gitAdminService.GetFileExplorerAsync(folderPath, search, page, pageSize, includeCommitInfo, cancellationToken));
    }

    public async Task<GitFileExplorerDto> GetGitFileExplorerFolderAsync(string folderPath, string? currentRelativePath, string? search, int page = 1, int pageSize = 50, bool includeCommitInfo = true)
    {
        return await RunGitTabOperationAsync("explorer", cancellationToken => _gitAdminService.GetFileExplorerFolderAsync(folderPath, currentRelativePath, search, page, pageSize, includeCommitInfo, cancellationToken));
    }

    public async Task<GitFilePreviewDto> GetGitLocalFilePreviewAsync(string folderPath, string relativePath)
    {
        return await RunGitTabOperationAsync("explorer", cancellationToken => _gitAdminService.GetLocalFilePreviewAsync(folderPath, relativePath, cancellationToken));
    }

    public async Task<GitFilePreviewDto> GetGitRemoteFilePreviewAsync(string folderPath, string? remoteName, string? branchName, string relativePath)
    {
        return await RunGitTabOperationAsync("explorer", cancellationToken => _gitAdminService.GetRemoteFilePreviewAsync(folderPath, remoteName, branchName, relativePath, cancellationToken));
    }

    public async Task<GitActionResultDto> UpdateGitIgnoreRuleAsync(GitIgnoreRuleRequestDto request)
    {
        return await RunGitActionOperationAsync("explorer", async cancellationToken =>
        {
            var result = await _gitAdminService.UpdateGitIgnoreRuleAsync(request, cancellationToken);
            await SendGitConsoleAsync(result.Success ? "success" : "error", "gitignore", result.Message, Context.ConnectionAborted);
            return result;
        });
    }

    public async Task<GitRemoteExplorerDto> GetGitRemoteExplorerAsync(string folderPath, string? remoteName, string? branchName, string? search, int page = 1, int pageSize = 50, bool includeCommitInfo = true, string? currentRelativePath = null)
    {
        return await RunGitTabOperationAsync("explorer", cancellationToken => _gitAdminService.GetRemoteExplorerAsync(folderPath, remoteName, branchName, currentRelativePath, search, page, pageSize, includeCommitInfo, cancellationToken));
    }


    public async Task<GitProjectConfigSnapshotDto> GetGitProjectConfigFilesAsync(string folderPath)
    {
        return await RunGitTabOperationAsync("config", cancellationToken => _gitAdminService.GetProjectConfigFilesAsync(folderPath, cancellationToken));
    }

    public async Task<string> LoadGitIgnoreAsync(string folderPath)
    {
        return await RunGitTabOperationAsync("ignore", cancellationToken => _gitAdminService.LoadGitIgnoreAsync(folderPath, cancellationToken));
    }

    public async Task<GitActionResultDto> SaveGitIgnoreAsync(string folderPath, string content)
    {
        return await RunGitActionOperationAsync("ignore", async cancellationToken =>
        {
            var result = await _gitAdminService.SaveGitIgnoreAsync(folderPath, content, cancellationToken);
            await SendGitConsoleAsync(result.Success ? "success" : "error", "gitignore", result.Message, Context.ConnectionAborted);
            return result;
        });
    }

    public async Task<GitActionResultDto> GitInitAsync(string folderPath)
    {
        return await RunGitActionOperationAsync("commands", cancellationToken =>
            _gitAdminService.InitRepositoryAsync(folderPath, entry => Clients.Caller.SendAsync("GitConsoleEntry", entry, Context.ConnectionAborted), cancellationToken));
    }

    public async Task<GitActionResultDto> AddGitRemoteAsync(GitRemoteRequestDto request)
    {
        return await RunGitActionOperationAsync("commands", cancellationToken =>
            _gitAdminService.AddRemoteAsync(request, entry => Clients.Caller.SendAsync("GitConsoleEntry", entry, Context.ConnectionAborted), cancellationToken));
    }

    public async Task<GitActionResultDto> AddGitFilesAsync(GitAddRequestDto request)
    {
        return await RunGitActionOperationAsync("commands", cancellationToken =>
            _gitAdminService.AddFilesAsync(request, entry => Clients.Caller.SendAsync("GitConsoleEntry", entry, Context.ConnectionAborted), cancellationToken));
    }

    public async Task<GitActionResultDto> CommitGitAsync(GitCommitRequestDto request)
    {
        return await RunGitActionOperationAsync("commands", cancellationToken =>
            _gitAdminService.CommitAsync(request, entry => Clients.Caller.SendAsync("GitConsoleEntry", entry, Context.ConnectionAborted), cancellationToken));
    }

    public async Task<GitActionResultDto> PushGitAsync(GitPushPullRequestDto request)
    {
        return await RunGitActionOperationAsync("commands", cancellationToken =>
            _gitAdminService.PushAsync(request, entry => Clients.Caller.SendAsync("GitConsoleEntry", entry, Context.ConnectionAborted), cancellationToken));
    }

    public async Task<GitActionResultDto> PullGitAsync(GitPushPullRequestDto request)
    {
        return await RunGitActionOperationAsync("commands", cancellationToken =>
            _gitAdminService.PullAsync(request, entry => Clients.Caller.SendAsync("GitConsoleEntry", entry, Context.ConnectionAborted), cancellationToken));
    }

    public async Task<GitActionResultDto> CreateGitBranchAsync(GitBranchRequestDto request)
    {
        return await RunGitActionOperationAsync("commands", cancellationToken =>
            _gitAdminService.CreateBranchAsync(request, entry => Clients.Caller.SendAsync("GitConsoleEntry", entry, Context.ConnectionAborted), cancellationToken));
    }

    public async Task<GitActionResultDto> CheckoutGitBranchAsync(GitBranchRequestDto request)
    {
        return await RunGitActionOperationAsync("commands", cancellationToken =>
            _gitAdminService.CheckoutBranchAsync(request, entry => Clients.Caller.SendAsync("GitConsoleEntry", entry, Context.ConnectionAborted), cancellationToken));
    }

    public async Task<GitActionResultDto> CloneGitAsync(GitCloneRequestDto request)
    {
        return await RunGitActionOperationAsync("clone", cancellationToken =>
            _gitAdminService.CloneAsync(request, entry => Clients.Caller.SendAsync("GitConsoleEntry", entry, Context.ConnectionAborted), cancellationToken));
    }

    public async Task<List<GitRepositorySearchResultDto>> FindGitRepositoriesAsync(string rootFolderPath, string? keyword, int maxResults = 100)
    {
        return await RunGitTabOperationAsync("scan", cancellationToken =>
            _gitAdminService.FindGitRepositoriesAsync(rootFolderPath, keyword, maxResults, entry => Clients.Caller.SendAsync("GitConsoleEntry", entry, Context.ConnectionAborted), cancellationToken));
    }

    public async Task<DynamicDnsResponseDto> GetDynamicDnsAsync()
    {
        var cancellationToken = Context.ConnectionAborted;
        return await _dynamicDnsService.LoadAsync(cancellationToken);
    }

    public async Task<SqlActionResultDto> SaveDynamicDnsAccountAsync(DynamicDnsAccountDto account)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _dynamicDnsService.SaveAccountAsync(account, cancellationToken);
        await NotifyDynamicDnsChangedAsync(result, cancellationToken);
        return result;
    }

    public async Task<SqlActionResultDto> DeleteDynamicDnsAccountAsync(string accountId)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _dynamicDnsService.DeleteAccountAsync(accountId, cancellationToken);
        await NotifyDynamicDnsChangedAsync(result, cancellationToken);
        return result;
    }

    public async Task<SqlActionResultDto> SaveDynamicDnsDomainAsync(DynamicDnsDomainRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _dynamicDnsService.SaveDomainAsync(request.AccountId, request.Domain, cancellationToken);
        await NotifyDynamicDnsChangedAsync(result, cancellationToken);
        return result;
    }

    public async Task<SqlActionResultDto> DeleteDynamicDnsDomainAsync(string accountId, string domainId)
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _dynamicDnsService.DeleteDomainAsync(accountId, domainId, cancellationToken);
        await NotifyDynamicDnsChangedAsync(result, cancellationToken);
        return result;
    }

    public async Task<SqlActionResultDto> GetPublicIpAsync()
    {
        var cancellationToken = Context.ConnectionAborted;
        var result = await _dynamicDnsService.GetAndSavePublicIpAsync(cancellationToken);
        if (result.Success)
        {
            await NotifyDynamicDnsChangedAsync(new SqlActionResultDto
            {
                Success = true,
                Message = "Đã lấy IP public: " + result.Message
            }, cancellationToken);
        }
        else
        {
            await SendDynamicDnsSoftErrorAsync(result.Message);
        }

        return result;
    }

    public async Task<DynamicDnsBulkUpdateResultDto> UpdateDynamicDnsAsync(DynamicDnsUpdateRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        try
        {
            var result = await _dynamicDnsService.UpdateAsync(request, cancellationToken);
            if (result.Success)
            {
                await Clients.All.SendAsync("DynamicDnsChanged", result.Snapshot ?? await _dynamicDnsService.LoadAsync(cancellationToken), result.Message, cancellationToken);
            }
            else
            {
                await SendDynamicDnsSoftErrorAsync(result.Message);
            }

            return result;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            var message = "Không update được Dynamic DNS: " + ex.Message;
            await SendDynamicDnsSoftErrorAsync(message);
            return new DynamicDnsBulkUpdateResultDto
            {
                Success = false,
                Message = message,
                Snapshot = await _dynamicDnsService.LoadAsync(CancellationToken.None)
            };
        }
    }

    private async Task NotifyDynamicDnsChangedAsync(SqlActionResultDto result, CancellationToken cancellationToken)
    {
        if (result.Success)
        {
            await Clients.All.SendAsync("DynamicDnsChanged", await _dynamicDnsService.LoadAsync(cancellationToken), result.Message, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(result.Message))
        {
            await SendDynamicDnsSoftErrorAsync(result.Message);
        }
    }

    private async Task SendDynamicDnsSoftErrorAsync(string message)
    {
        try
        {
            await Clients.Caller.SendAsync("DynamicDnsSoftError", message, CancellationToken.None);
        }
        catch
        {
            // Không để lỗi báo lỗi làm hỏng luồng SignalR chính.
        }
    }



    private Task SendGitConsoleAsync(string level, string scope, string message, CancellationToken cancellationToken)
    {
        return Clients.Caller.SendAsync("GitConsoleEntry", new GitConsoleEntryDto
        {
            Level = level,
            Scope = scope,
            Message = message
        }, cancellationToken);
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
