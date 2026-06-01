using System.Text.Json.Serialization;

namespace ConfigTool.Models;

public sealed class ConfigToolSettings
{
    public string? ConfigFolderPath { get; set; }
    public int SignalRPort { get; set; } = 59177;
    public DateTimeOffset? LastFolderSelectedAt { get; set; }
    public string[] RequiredFileNames { get; set; } = ConfigToolDefaults.RequiredFileNames;
}

public static class ConfigToolDefaults
{
    public static readonly string[] RequiredFileNames =
    [
        "GameCoreConfig.json",
        "GameJsonDatabaseConfig.json",
        "PlantConfig.json",
        "PlantingConfig.json",
        "tilemaps.json"
    ];
}

public sealed class FolderValidationResult
{
    public string? FolderPath { get; set; }
    public bool Exists { get; set; }
    public bool IsValid { get; set; }
    public List<string> FoundFiles { get; set; } = [];
    public List<string> MissingFiles { get; set; } = [];
    public string Message { get; set; } = string.Empty;
}

public sealed class ConfigBootstrapDto
{
    public ConfigToolSettings Settings { get; set; } = new();
    public string SettingsFilePath { get; set; } = string.Empty;
    public FolderValidationResult Folder { get; set; } = new();
    public List<JsonConfigFileDto> Files { get; set; } = [];
    public string Message { get; set; } = string.Empty;
}

public sealed class JsonConfigFileDto
{
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTimeOffset LastWriteTime { get; set; }
    public JsonFileVersionDto? FileVersion { get; set; }
    public int TableCount { get; set; }
    public string RootKind { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

public sealed class JsonTableDto
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int RowCount { get; set; }
    public bool CanCreate { get; set; }
    public bool CanDelete { get; set; }
    public string IdField { get; set; } = string.Empty;
    public List<JsonFieldDto> Fields { get; set; } = [];
}

public sealed class JsonFieldDto
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "string";
    public bool IsKey { get; set; }
    public bool IsSynthetic { get; set; }
    public int SeenCount { get; set; }
}

public sealed class JsonFileVersionDto
{
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public string ContentHash { get; set; } = string.Empty;

    [JsonIgnore]
    public DateTimeOffset LastWriteUtc => new(LastWriteUtcTicks, TimeSpan.Zero);
}

public sealed class ConfigExternalChangeDto
{
    public string ChangeId { get; set; } = Guid.NewGuid().ToString("N");
    public string FolderPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ChangeKind { get; set; } = "changed";
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.Now;
    public FolderValidationResult Folder { get; set; } = new();
    public List<JsonConfigFileDto> Files { get; set; } = [];
}

public sealed class JsonRowsQueryRequest
{
    public string FileName { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public List<JsonQueryFilterDto> Filters { get; set; } = [];
}

public sealed class JsonQueryFilterDto
{
    public string FieldName { get; set; } = string.Empty;
    public string Operator { get; set; } = "contains";
    public string? Value { get; set; }
}

public sealed class JsonRowPageDto
{
    public string FileName { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public JsonFileVersionDto? FileVersion { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public int TotalRows { get; set; }
    public int TotalPages { get; set; }
    public List<JsonFieldDto> Fields { get; set; } = [];
    public List<JsonRowDto> Rows { get; set; } = [];
}

public sealed class JsonRowDto
{
    public int RowIndex { get; set; }
    public string RowKey { get; set; } = string.Empty;
    public List<JsonCellDto> Cells { get; set; } = [];
}

public sealed class JsonCellDto
{
    public string Name { get; set; } = string.Empty;
    public string? OriginalName { get; set; }
    public string Kind { get; set; } = "string";
    public string? Value { get; set; }
    public bool IsKey { get; set; }
}

public sealed class JsonRowWriteRequest
{
    public string FileName { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public int RowIndex { get; set; } = -1;

    // Giữ lại để tương thích client cũ, nhưng workflow Unity-sync không dùng để chặn lưu cứng nữa.
    public JsonFileVersionDto? ExpectedFileVersion { get; set; }

    public string RowKey { get; set; } = string.Empty;
    public string IdField { get; set; } = string.Empty;
    public bool AutoMergeWithLatest { get; set; } = true;
    public List<JsonCellDto> OriginalCells { get; set; } = [];
    public List<JsonCellDto> Cells { get; set; } = [];
    public List<string> DeletedFieldNames { get; set; } = [];
}

public sealed class JsonRowDeleteRequest
{
    public string FileName { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public int RowIndex { get; set; } = -1;

    // Giữ lại để tương thích client cũ, nhưng workflow Unity-sync không dùng để chặn lưu cứng nữa.
    public JsonFileVersionDto? ExpectedFileVersion { get; set; }

    public string RowKey { get; set; } = string.Empty;
    public string IdField { get; set; } = string.Empty;
    public List<JsonCellDto> OriginalCells { get; set; } = [];
}

public sealed class JsonCrudResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool HasConflicts { get; set; }
    public List<string> Conflicts { get; set; } = [];
    public JsonRowPageDto? Page { get; set; }
}

public sealed class JsonFileCreateRequest
{
    public string FileName { get; set; } = string.Empty;
    public string RootKind { get; set; } = "object";
    public string? JsonText { get; set; }
    public bool OverwriteIfExists { get; set; }
}

public sealed class SqlConnectConfigFile
{
    [JsonPropertyName("connect")]
    public List<SqlConnectionProfileDto> Connect { get; set; } = [];
}

public sealed class SqlConnectionProfileDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Local SQL";

    [JsonPropertyName("typeconect")]
    public string TypeConnect { get; set; } = "mysql";

    [JsonPropertyName("host")]
    public string Host { get; set; } = "127.0.0.1";

    [JsonPropertyName("port")]
    public string Port { get; set; } = "3306";

    [JsonPropertyName("user")]
    public string User { get; set; } = "root";

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("allownopassword")]
    public bool AllowNoPassword { get; set; } = true;

    [JsonPropertyName("database")]
    public string? Database { get; set; }

    [JsonPropertyName("encrypt")]
    public bool Encrypt { get; set; }

    [JsonPropertyName("trustservercertificate")]
    public bool TrustServerCertificate { get; set; } = true;

    [JsonPropertyName("timeoutseconds")]
    public int TimeoutSeconds { get; set; } = 15;
}

public sealed class SqlProfilesResponseDto
{
    public string ConnectFilePath { get; set; } = string.Empty;
    public List<SqlConnectionProfileDto> Profiles { get; set; } = [];
}

public sealed class SqlActionResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class SqlDatabaseDto
{
    public string Name { get; set; } = string.Empty;
}

public sealed class SqlTableDto
{
    public string Schema { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Type { get; set; } = "BASE TABLE";
}

public sealed class SqlColumnDto
{
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool IsNullable { get; set; }
    public bool IsPrimaryKey { get; set; }
    public int Ordinal { get; set; }
}

public sealed class SqlRecordQueryRequest
{
    public string ProfileId { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string Schema { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public string Search { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public sealed class SqlQueryRequest
{
    public string ProfileId { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string QueryText { get; set; } = string.Empty;
    public int MaxRows { get; set; } = 500;
}

public sealed class SqlRowPageDto
{
    public string DatabaseName { get; set; } = string.Empty;
    public string Schema { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public int TotalRows { get; set; }
    public int TotalPages { get; set; } = 1;
    public List<SqlColumnDto> Columns { get; set; } = [];
    public List<Dictionary<string, string?>> Rows { get; set; } = [];
}

public sealed class SqlQueryResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int AffectedRows { get; set; }
    public List<SqlColumnDto> Columns { get; set; } = [];
    public List<Dictionary<string, string?>> Rows { get; set; } = [];
}

public sealed class SqlDatabaseCrudRequest
{
    public string ProfileId { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string NewDatabaseName { get; set; } = string.Empty;
    public string Charset { get; set; } = "utf8mb4";
    public string Collation { get; set; } = "utf8mb4_unicode_ci";
}

public sealed class SqlColumnEditDto
{
    public string OriginalName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = "varchar";
    public string Length { get; set; } = "255";
    public bool IsNullable { get; set; } = true;
    public bool IsPrimaryKey { get; set; }
    public bool AutoIncrement { get; set; }
    public string DefaultValue { get; set; } = string.Empty;
    public string Extra { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public int Ordinal { get; set; }
}

public sealed class SqlTableCrudRequest
{
    public string ProfileId { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string Schema { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public string NewTableName { get; set; } = string.Empty;
    public bool IfNotExists { get; set; } = true;
    public string Engine { get; set; } = "InnoDB";
    public string Charset { get; set; } = "utf8mb4";
    public List<SqlColumnEditDto> Columns { get; set; } = [];
}

public sealed class SqlColumnCrudRequest
{
    public string ProfileId { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string Schema { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public SqlColumnEditDto Column { get; set; } = new();
}

public sealed class SqlRecordWriteRequest
{
    public string ProfileId { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string Schema { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public Dictionary<string, string?> OriginalValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> KeyColumns { get; set; } = [];
}

public sealed class SqlRecordDeleteRequest
{
    public string ProfileId { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string Schema { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public Dictionary<string, string?> OriginalValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> KeyColumns { get; set; } = [];
}

public sealed class SqlExportRequest
{
    public string ProfileId { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string Schema { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public bool IncludeStructure { get; set; } = true;
    public bool IncludeData { get; set; } = true;
    public int MaxRows { get; set; } = 1000;
}

public sealed class SqlImportRequest
{
    public string ProfileId { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string ScriptText { get; set; } = string.Empty;
}

public sealed class SqlQueryTemplateDto
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string QueryText { get; set; } = string.Empty;
    public string Icon { get; set; } = "fa-code";
}


public sealed class SqlForeignKeyDto
{
    public string ConstraintName { get; set; } = string.Empty;
    public string Schema { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public string ColumnName { get; set; } = string.Empty;
    public string ReferencedSchema { get; set; } = string.Empty;
    public string ReferencedTableName { get; set; } = string.Empty;
    public string ReferencedColumnName { get; set; } = string.Empty;
    public string UpdateRule { get; set; } = "NO ACTION";
    public string DeleteRule { get; set; } = "NO ACTION";
}

public sealed class SqlForeignKeyCrudRequest
{
    public string ProfileId { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string Schema { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public string ConstraintName { get; set; } = string.Empty;
    public string OriginalConstraintName { get; set; } = string.Empty;
    public string ColumnName { get; set; } = string.Empty;
    public string ReferencedSchema { get; set; } = string.Empty;
    public string ReferencedTableName { get; set; } = string.Empty;
    public string ReferencedColumnName { get; set; } = string.Empty;
    public string UpdateRule { get; set; } = "NO ACTION";
    public string DeleteRule { get; set; } = "NO ACTION";
}
