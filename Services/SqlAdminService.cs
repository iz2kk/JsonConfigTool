using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using ConfigTool.Models;
using Microsoft.Data.SqlClient;
using MySqlConnector;

namespace ConfigTool.Services;

public sealed class SqlAdminService
{
    private readonly SqlConnectConfigService _configService;
    private readonly SemaphoreSlim _dbThrottle;

    public SqlAdminService(SqlConnectConfigService configService)
    {
        _configService = configService;
        var max = Math.Clamp(Environment.ProcessorCount * 2, 4, 32);
        _dbThrottle = new SemaphoreSlim(max, max);
    }

    public Task<SqlProfilesResponseDto> LoadProfilesAsync(CancellationToken cancellationToken = default)
        => _configService.LoadAsync(cancellationToken);

    public Task<SqlActionResultDto> SaveProfileAsync(SqlConnectionProfileDto profile, CancellationToken cancellationToken = default)
        => _configService.SaveProfileAsync(profile, cancellationToken);

    public Task<SqlActionResultDto> DeleteProfileAsync(string id, CancellationToken cancellationToken = default)
        => _configService.DeleteProfileAsync(id, cancellationToken);

    public async Task<SqlActionResultDto> TestConnectionAsync(string profileId, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(profileId, cancellationToken);
            return await TestConnectionAsync(profile, cancellationToken);
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlActionResultDto
            {
                Success = false,
                Message = ToFriendlyDatabaseError(ex, $"Test kết nối {profileId}")
            };
        }
    }

    public async Task<SqlActionResultDto> TestConnectionAsync(SqlConnectionProfileDto profile, CancellationToken cancellationToken = default)
    {
        try
        {
            SqlConnectConfigService.NormalizeProfile(profile);
            await using var connection = CreateConnection(profile, databaseName: profile.Database);
            await OpenThrottledAsync(connection, cancellationToken);
            return new SqlActionResultDto { Success = true, Message = $"Kết nối thành công: {profile.Name}" };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlActionResultDto
            {
                Success = false,
                Message = ToFriendlyDatabaseError(ex, $"Test kết nối {profile.Name}")
            };
        }
    }

    public async Task<SqlServerInfoDto> GetServerInfoAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var profile = await _configService.GetProfileOrThrowAsync(profileId, cancellationToken);
        await using var connection = CreateConnection(profile, profile.Database);
        await OpenThrottledAsync(connection, cancellationToken);
        var isSqlServer = IsSqlServer(profile);
        var info = new SqlServerInfoDto
        {
            Provider = SqlConnectConfigService.NormalizeType(profile.TypeConnect),
            CurrentDatabase = string.IsNullOrWhiteSpace(profile.Database) ? string.Empty : profile.Database,
            Status = "online"
        };

        if (isSqlServer)
        {
            var version = await ReadRowsAsync(connection, "SELECT CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(128)) AS version, CAST(SERVERPROPERTY('Collation') AS NVARCHAR(128)) AS collation", [], 1, cancellationToken);
            if (version.Rows.Count > 0)
            {
                info.Version = Value(version.Rows[0], "version");
                info.Collation = Value(version.Rows[0], "collation");
            }
            var count = await ReadRowsAsync(connection, "SELECT COUNT(1) AS database_count FROM sys.databases WHERE state = 0", [], 1, cancellationToken);
            info.DatabaseCount = int.TryParse(count.Rows.FirstOrDefault()?.Values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 0;
            info.Facts.Add(new("Provider", "SQL Server"));
            info.Facts.Add(new("Collation", info.Collation));
        }
        else
        {
            // MySQL/MariaDB không có system variable @@uptime trên nhiều bản (đúng là status variable Uptime).
            // Tách uptime sang SHOW GLOBAL STATUS để tránh lỗi Unknown system variable 'uptime' làm chết dashboard.
            var version = await ReadRowsAsync(connection, "SELECT VERSION() AS version, @@character_set_server AS charset, @@collation_server AS collation", [], 1, cancellationToken);
            if (version.Rows.Count > 0)
            {
                info.Version = Value(version.Rows[0], "version");
                info.Charset = Value(version.Rows[0], "charset");
                info.Collation = Value(version.Rows[0], "collation");
                info.Uptime = await ReadMySqlOrMariaDbUptimeAsync(connection, cancellationToken);
            }
            var count = await ReadRowsAsync(connection, "SELECT COUNT(1) AS database_count FROM INFORMATION_SCHEMA.SCHEMATA", [], 1, cancellationToken);
            info.DatabaseCount = int.TryParse(count.Rows.FirstOrDefault()?.Values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 0;
            info.Facts.Add(new("Provider", SqlConnectConfigService.NormalizeType(profile.TypeConnect) == "mariadb" ? "MariaDB" : "MySQL"));
            info.Facts.Add(new("Charset", info.Charset));
            info.Facts.Add(new("Collation", info.Collation));
            info.Facts.Add(new("Uptime", info.Uptime));
        }

        return info;
    }

    public async Task<List<SqlDatabaseDto>> ListDatabasesAsync(string profileId, string? search = null, CancellationToken cancellationToken = default)
    {
        var profile = await _configService.GetProfileOrThrowAsync(profileId, cancellationToken);
        await using var connection = CreateConnection(profile);
        await OpenThrottledAsync(connection, cancellationToken);

        var isSqlServer = IsSqlServer(profile);
        var parameters = new List<DbParameter>();
        string query;
        if (isSqlServer)
        {
            query = "SELECT name AS SCHEMA_NAME, CAST(DATABASEPROPERTYEX(name, 'Collation') AS NVARCHAR(128)) AS DEFAULT_COLLATION_NAME, '' AS DEFAULT_CHARACTER_SET_NAME FROM sys.databases WHERE state = 0";
            if (!string.IsNullOrWhiteSpace(search))
            {
                query += " AND name LIKE @search";
                parameters.Add(CreateParameter(connection, "@search", "%" + search.Trim() + "%"));
            }
            query += " ORDER BY name";
        }
        else
        {
            query = "SELECT SCHEMA_NAME, DEFAULT_CHARACTER_SET_NAME, DEFAULT_COLLATION_NAME FROM INFORMATION_SCHEMA.SCHEMATA";
            if (!string.IsNullOrWhiteSpace(search))
            {
                query += " WHERE SCHEMA_NAME LIKE @search";
                parameters.Add(CreateParameter(connection, "@search", "%" + search.Trim() + "%"));
            }
            query += " ORDER BY SCHEMA_NAME";
        }

        var rows = await ReadRowsAsync(connection, query, parameters, 5000, cancellationToken);
        return rows.Rows
            .Select(row => new SqlDatabaseDto
            {
                Name = Value(row, "SCHEMA_NAME"),
                Charset = Value(row, "DEFAULT_CHARACTER_SET_NAME"),
                Collation = Value(row, "DEFAULT_COLLATION_NAME")
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<List<SqlTableDto>> ListTablesAsync(string profileId, string databaseName, string? search, CancellationToken cancellationToken = default)
    {
        var profile = await _configService.GetProfileOrThrowAsync(profileId, cancellationToken);
        await using var connection = CreateConnection(profile, databaseName);
        await OpenThrottledAsync(connection, cancellationToken);
        var isSqlServer = IsSqlServer(profile);
        var parameters = new List<DbParameter>();
        string query;
        if (isSqlServer)
        {
            query = """
SELECT t.TABLE_SCHEMA, t.TABLE_NAME, t.TABLE_TYPE,
       CAST(p.rows AS BIGINT) AS TABLE_ROWS,
       '' AS ENGINE,
       '' AS TABLE_COLLATION
FROM INFORMATION_SCHEMA.TABLES t
OUTER APPLY (
    SELECT SUM(CASE WHEN ps.index_id IN (0,1) THEN ps.row_count ELSE 0 END) AS rows
    FROM sys.dm_db_partition_stats ps
    WHERE ps.object_id = OBJECT_ID(QUOTENAME(t.TABLE_SCHEMA) + '.' + QUOTENAME(t.TABLE_NAME))
) p
WHERE t.TABLE_TYPE IN ('BASE TABLE','VIEW')
""";
            if (!string.IsNullOrWhiteSpace(search))
            {
                query += " AND (t.TABLE_NAME LIKE @search OR t.TABLE_SCHEMA LIKE @search)";
                parameters.Add(CreateParameter(connection, "@search", "%" + search.Trim() + "%"));
            }

            query += " ORDER BY t.TABLE_SCHEMA, t.TABLE_NAME";
        }
        else
        {
            query = """
SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE, TABLE_ROWS, ENGINE, TABLE_COLLATION
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_SCHEMA = @db
""";
            parameters.Add(CreateParameter(connection, "@db", databaseName));
            if (!string.IsNullOrWhiteSpace(search))
            {
                query += " AND TABLE_NAME LIKE @search";
                parameters.Add(CreateParameter(connection, "@search", "%" + search.Trim() + "%"));
            }

            query += " ORDER BY TABLE_NAME";
        }

        var result = await ReadRowsAsync(connection, query, parameters, 5000, cancellationToken);
        return result.Rows.Select(row => new SqlTableDto
        {
            Schema = Value(row, "TABLE_SCHEMA"),
            Name = Value(row, "TABLE_NAME"),
            FullName = isSqlServer ? $"{Value(row, "TABLE_SCHEMA")}.{Value(row, "TABLE_NAME")}" : Value(row, "TABLE_NAME"),
            Type = Value(row, "TABLE_TYPE"),
            RowCount = long.TryParse(Value(row, "TABLE_ROWS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rows) ? rows : null,
            Engine = Value(row, "ENGINE"),
            Collation = Value(row, "TABLE_COLLATION")
        }).ToList();
    }

    public async Task<List<SqlColumnDto>> ListColumnsAsync(string profileId, string databaseName, string schema, string tableName, CancellationToken cancellationToken = default)
    {
        var profile = await _configService.GetProfileOrThrowAsync(profileId, cancellationToken);
        await using var connection = CreateConnection(profile, databaseName);
        await OpenThrottledAsync(connection, cancellationToken);
        var isSqlServer = IsSqlServer(profile);
        var parameters = new List<DbParameter>();
        string query;
        if (isSqlServer)
        {
            query = """
SELECT c.COLUMN_NAME,
       c.DATA_TYPE,
       COALESCE(CAST(c.CHARACTER_MAXIMUM_LENGTH AS NVARCHAR(32)), CAST(c.NUMERIC_PRECISION AS NVARCHAR(32)), '') AS LENGTH_VALUE,
       c.COLUMN_DEFAULT,
       c.IS_NULLABLE,
       c.ORDINAL_POSITION,
       CASE WHEN tc.CONSTRAINT_TYPE = 'PRIMARY KEY' THEN 1 ELSE 0 END AS IS_PRIMARY,
       '' AS EXTRA,
       COALESCE(CONVERT(NVARCHAR(512), ep.value), '') AS COLUMN_COMMENT
FROM INFORMATION_SCHEMA.COLUMNS c
LEFT JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
  ON c.TABLE_SCHEMA = k.TABLE_SCHEMA AND c.TABLE_NAME = k.TABLE_NAME AND c.COLUMN_NAME = k.COLUMN_NAME
LEFT JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
  ON k.CONSTRAINT_NAME = tc.CONSTRAINT_NAME AND k.TABLE_SCHEMA = tc.TABLE_SCHEMA AND k.TABLE_NAME = tc.TABLE_NAME AND tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
LEFT JOIN sys.extended_properties ep
  ON ep.major_id = OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME))
 AND ep.minor_id = COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)), c.COLUMN_NAME, 'ColumnId')
 AND ep.name = 'MS_Description'
WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @table
ORDER BY c.ORDINAL_POSITION
""";
            parameters.Add(CreateParameter(connection, "@schema", string.IsNullOrWhiteSpace(schema) ? "dbo" : schema));
            parameters.Add(CreateParameter(connection, "@table", tableName));
        }
        else
        {
            query = """
SELECT COLUMN_NAME, DATA_TYPE, COLUMN_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE,
       COLUMN_DEFAULT, IS_NULLABLE, ORDINAL_POSITION,
       CASE WHEN COLUMN_KEY = 'PRI' THEN 1 ELSE 0 END AS IS_PRIMARY,
       EXTRA, COLUMN_COMMENT
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @table
ORDER BY ORDINAL_POSITION
""";
            parameters.Add(CreateParameter(connection, "@db", databaseName));
            parameters.Add(CreateParameter(connection, "@table", tableName));
        }

        var result = await ReadRowsAsync(connection, query, parameters, 5000, cancellationToken);
        return result.Rows.Select(row =>
        {
            var length = Value(row, "LENGTH_VALUE");
            if (string.IsNullOrWhiteSpace(length))
            {
                var max = Value(row, "CHARACTER_MAXIMUM_LENGTH");
                var precision = Value(row, "NUMERIC_PRECISION");
                var scale = Value(row, "NUMERIC_SCALE");
                length = !string.IsNullOrWhiteSpace(max) ? max : (!string.IsNullOrWhiteSpace(precision) && !string.IsNullOrWhiteSpace(scale) ? $"{precision},{scale}" : precision);
            }
            return new SqlColumnDto
            {
                Name = Value(row, "COLUMN_NAME"),
                DataType = Value(row, "DATA_TYPE"),
                FullDataType = string.IsNullOrWhiteSpace(Value(row, "COLUMN_TYPE")) ? Value(row, "DATA_TYPE") : Value(row, "COLUMN_TYPE"),
                Length = length,
                DefaultValue = Value(row, "COLUMN_DEFAULT"),
                Extra = Value(row, "EXTRA"),
                Comment = Value(row, "COLUMN_COMMENT"),
                IsNullable = string.Equals(Value(row, "IS_NULLABLE"), "YES", StringComparison.OrdinalIgnoreCase),
                IsPrimaryKey = Value(row, "IS_PRIMARY") is "1" or "True" or "true",
                Ordinal = int.TryParse(Value(row, "ORDINAL_POSITION"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal) ? ordinal : 0
            };
        }).ToList();
    }

    public async Task<List<SqlIndexDto>> ListIndexesAsync(string profileId, string databaseName, string schema, string tableName, string? search, CancellationToken cancellationToken = default)
    {
        var profile = await _configService.GetProfileOrThrowAsync(profileId, cancellationToken);
        await using var connection = CreateConnection(profile, databaseName);
        await OpenThrottledAsync(connection, cancellationToken);
        var isSqlServer = IsSqlServer(profile);
        var parameters = new List<DbParameter>();
        string query;
        if (isSqlServer)
        {
            query = """
SELECT i.name AS INDEX_NAME,
       SCHEMA_NAME(t.schema_id) AS TABLE_SCHEMA,
       t.name AS TABLE_NAME,
       c.name AS COLUMN_NAME,
       ic.key_ordinal AS SEQ_IN_INDEX,
       i.is_primary_key AS IS_PRIMARY,
       i.is_unique AS IS_UNIQUE,
       i.type_desc AS INDEX_TYPE
FROM sys.indexes i
JOIN sys.tables t ON i.object_id = t.object_id
JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
WHERE i.name IS NOT NULL AND SCHEMA_NAME(t.schema_id) = @schema AND t.name = @table
""";
            parameters.Add(CreateParameter(connection, "@schema", string.IsNullOrWhiteSpace(schema) ? "dbo" : schema));
            parameters.Add(CreateParameter(connection, "@table", tableName));
            if (!string.IsNullOrWhiteSpace(search))
            {
                query += " AND (i.name LIKE @search OR c.name LIKE @search)";
                parameters.Add(CreateParameter(connection, "@search", "%" + search.Trim() + "%"));
            }
            query += " ORDER BY i.name, ic.key_ordinal";
        }
        else
        {
            query = """
SELECT INDEX_NAME, TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, SEQ_IN_INDEX,
       CASE WHEN INDEX_NAME = 'PRIMARY' THEN 1 ELSE 0 END AS IS_PRIMARY,
       CASE WHEN NON_UNIQUE = 0 THEN 1 ELSE 0 END AS IS_UNIQUE,
       INDEX_TYPE
FROM INFORMATION_SCHEMA.STATISTICS
WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @table
""";
            parameters.Add(CreateParameter(connection, "@db", databaseName));
            parameters.Add(CreateParameter(connection, "@table", tableName));
            if (!string.IsNullOrWhiteSpace(search))
            {
                query += " AND (INDEX_NAME LIKE @search OR COLUMN_NAME LIKE @search)";
                parameters.Add(CreateParameter(connection, "@search", "%" + search.Trim() + "%"));
            }
            query += " ORDER BY INDEX_NAME, SEQ_IN_INDEX";
        }

        var rows = await ReadRowsAsync(connection, query, parameters, 10000, cancellationToken);
        return rows.Rows
            .GroupBy(row => Value(row, "INDEX_NAME"), StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .Select(g =>
            {
                var first = g.First();
                var typeValue = Value(first, "INDEX_TYPE");
                var isPrimary = Value(first, "IS_PRIMARY") is "1" or "True" or "true";
                var isUnique = Value(first, "IS_UNIQUE") is "1" or "True" or "true";
                return new SqlIndexDto
                {
                    Name = g.Key,
                    Schema = Value(first, "TABLE_SCHEMA"),
                    TableName = Value(first, "TABLE_NAME"),
                    Columns = g.Select(x => Value(x, "COLUMN_NAME")).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    IsPrimary = isPrimary,
                    IsUnique = isUnique,
                    IsFullText = typeValue.Contains("FULLTEXT", StringComparison.OrdinalIgnoreCase),
                    IsSpatial = typeValue.Contains("SPATIAL", StringComparison.OrdinalIgnoreCase),
                    Type = isPrimary ? "PRIMARY" : isUnique ? "UNIQUE" : typeValue.Contains("FULLTEXT", StringComparison.OrdinalIgnoreCase) ? "FULLTEXT" : typeValue.Contains("SPATIAL", StringComparison.OrdinalIgnoreCase) ? "SPATIAL" : "INDEX"
                };
            })
            .OrderByDescending(x => x.IsPrimary)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<SqlActionResultDto> AddIndexAsync(SqlIndexCrudRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            await using var connection = CreateConnection(profile, request.DatabaseName);
            await OpenThrottledAsync(connection, cancellationToken);
            var sql = BuildCreateIndexSql(profile, request);
            var affected = await ExecuteNonQueryAsync(connection, sql, [], cancellationToken);
            return new SqlActionResultDto { Success = true, Message = $"Đã tạo key/index {request.Name}. Affected: {affected}." };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlActionResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không tạo được key/index") };
        }
    }

    public async Task<SqlActionResultDto> UpdateIndexAsync(SqlIndexCrudRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var drop = await DropIndexAsync(request, cancellationToken);
            if (!drop.Success)
            {
                return drop;
            }
            return await AddIndexAsync(request, cancellationToken);
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlActionResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không sửa được key/index") };
        }
    }

    public async Task<SqlActionResultDto> DropIndexAsync(SqlIndexCrudRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            await using var connection = CreateConnection(profile, request.DatabaseName);
            await OpenThrottledAsync(connection, cancellationToken);
            var isSqlServer = IsSqlServer(profile);
            ValidateIdentifier(request.TableName, "Tên bảng");
            var name = string.IsNullOrWhiteSpace(request.OriginalName) ? request.Name : request.OriginalName;
            ValidateIdentifier(name, "Tên index/key");
            var type = NormalizeIndexType(request.IndexType);
            string sql;
            if (type == "PRIMARY")
            {
                sql = isSqlServer
                    ? $"ALTER TABLE {BuildTableName(profile, request.DatabaseName, request.Schema, request.TableName)} DROP CONSTRAINT {QuoteIdentifier(name, true)}"
                    : $"ALTER TABLE {BuildTableName(profile, request.DatabaseName, request.Schema, request.TableName)} DROP PRIMARY KEY";
            }
            else
            {
                sql = isSqlServer
                    ? $"DROP INDEX {QuoteIdentifier(name, true)} ON {BuildTableName(profile, request.DatabaseName, request.Schema, request.TableName)}"
                    : $"DROP INDEX {QuoteIdentifier(name, false)} ON {BuildTableName(profile, request.DatabaseName, request.Schema, request.TableName)}";
            }

            var affected = await ExecuteNonQueryAsync(connection, sql, [], cancellationToken);
            return new SqlActionResultDto { Success = true, Message = $"Đã xóa key/index {name}. Affected: {affected}." };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlActionResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không xóa được key/index") };
        }
    }

    public async Task<SqlActionResultDto> TruncateTableAsync(SqlTableCrudRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            await using var connection = CreateConnection(profile, request.DatabaseName);
            await OpenThrottledAsync(connection, cancellationToken);
            var sql = $"TRUNCATE TABLE {BuildTableName(profile, request.DatabaseName, request.Schema, request.TableName)}";
            var affected = await ExecuteNonQueryAsync(connection, sql, [], cancellationToken);
            return new SqlActionResultDto { Success = true, Message = $"Đã empty/truncate bảng {request.TableName}. Affected: {affected}." };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlActionResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không truncate được bảng") };
        }
    }

    public async Task<SqlActionResultDto> CopyTableAsync(SqlTableCrudRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            var isSqlServer = IsSqlServer(profile);
            ValidateIdentifier(request.TableName, "Tên bảng nguồn");
            ValidateIdentifier(request.NewTableName, "Tên bảng copy");
            await using var connection = CreateConnection(profile, request.DatabaseName);
            await OpenThrottledAsync(connection, cancellationToken);
            var source = BuildTableName(profile, request.DatabaseName, request.Schema, request.TableName);
            var target = BuildTableName(profile, request.DatabaseName, request.Schema, request.NewTableName);
            var sql = isSqlServer
                ? $"SELECT TOP 0 * INTO {target} FROM {source}"
                : $"CREATE TABLE {target} LIKE {source}";
            var affected = await ExecuteNonQueryAsync(connection, sql, [], cancellationToken);
            return new SqlActionResultDto { Success = true, Message = $"Đã duplicate cấu trúc bảng {request.TableName} thành {request.NewTableName}. Affected: {affected}." };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlActionResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không copy được bảng") };
        }
    }

    public async Task<List<SqlForeignKeyDto>> ListForeignKeysAsync(string profileId, string databaseName, string schema, string tableName, string? search, CancellationToken cancellationToken = default)
    {
        var profile = await _configService.GetProfileOrThrowAsync(profileId, cancellationToken);
        await using var connection = CreateConnection(profile, databaseName);
        await OpenThrottledAsync(connection, cancellationToken);
        var isSqlServer = IsSqlServer(profile);
        var parameters = new List<DbParameter>();
        string query;
        if (isSqlServer)
        {
            query = """
SELECT fk.name AS CONSTRAINT_NAME,
       SCHEMA_NAME(tp.schema_id) AS TABLE_SCHEMA,
       tp.name AS TABLE_NAME,
       cp.name AS COLUMN_NAME,
       SCHEMA_NAME(tr.schema_id) AS REFERENCED_TABLE_SCHEMA,
       tr.name AS REFERENCED_TABLE_NAME,
       cr.name AS REFERENCED_COLUMN_NAME,
       fk.update_referential_action_desc AS UPDATE_RULE,
       fk.delete_referential_action_desc AS DELETE_RULE
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
JOIN sys.tables tp ON fkc.parent_object_id = tp.object_id
JOIN sys.columns cp ON fkc.parent_object_id = cp.object_id AND fkc.parent_column_id = cp.column_id
JOIN sys.tables tr ON fkc.referenced_object_id = tr.object_id
JOIN sys.columns cr ON fkc.referenced_object_id = cr.object_id AND fkc.referenced_column_id = cr.column_id
WHERE SCHEMA_NAME(tp.schema_id) = @schema AND tp.name = @table
""";
            parameters.Add(CreateParameter(connection, "@schema", string.IsNullOrWhiteSpace(schema) ? "dbo" : schema));
            parameters.Add(CreateParameter(connection, "@table", tableName));
            if (!string.IsNullOrWhiteSpace(search))
            {
                query += " AND (fk.name LIKE @search OR cp.name LIKE @search OR tr.name LIKE @search OR cr.name LIKE @search)";
                parameters.Add(CreateParameter(connection, "@search", "%" + search.Trim() + "%"));
            }

            query += " ORDER BY fk.name, cp.column_id";
        }
        else
        {
            query = """
SELECT k.CONSTRAINT_NAME,
       k.TABLE_SCHEMA,
       k.TABLE_NAME,
       k.COLUMN_NAME,
       k.REFERENCED_TABLE_SCHEMA,
       k.REFERENCED_TABLE_NAME,
       k.REFERENCED_COLUMN_NAME,
       rc.UPDATE_RULE,
       rc.DELETE_RULE
FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
LEFT JOIN INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc
  ON rc.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA AND rc.CONSTRAINT_NAME = k.CONSTRAINT_NAME
WHERE k.TABLE_SCHEMA = @db AND k.TABLE_NAME = @table AND k.REFERENCED_TABLE_NAME IS NOT NULL
""";
            parameters.Add(CreateParameter(connection, "@db", databaseName));
            parameters.Add(CreateParameter(connection, "@table", tableName));
            if (!string.IsNullOrWhiteSpace(search))
            {
                query += " AND (k.CONSTRAINT_NAME LIKE @search OR k.COLUMN_NAME LIKE @search OR k.REFERENCED_TABLE_NAME LIKE @search OR k.REFERENCED_COLUMN_NAME LIKE @search)";
                parameters.Add(CreateParameter(connection, "@search", "%" + search.Trim() + "%"));
            }

            query += " ORDER BY k.CONSTRAINT_NAME, k.ORDINAL_POSITION";
        }

        var result = await ReadRowsAsync(connection, query, parameters, 5000, cancellationToken);
        return result.Rows.Select(row => new SqlForeignKeyDto
        {
            ConstraintName = Value(row, "CONSTRAINT_NAME"),
            Schema = Value(row, "TABLE_SCHEMA"),
            TableName = Value(row, "TABLE_NAME"),
            ColumnName = Value(row, "COLUMN_NAME"),
            ReferencedSchema = Value(row, "REFERENCED_TABLE_SCHEMA"),
            ReferencedTableName = Value(row, "REFERENCED_TABLE_NAME"),
            ReferencedColumnName = Value(row, "REFERENCED_COLUMN_NAME"),
            UpdateRule = Value(row, "UPDATE_RULE"),
            DeleteRule = Value(row, "DELETE_RULE")
        }).ToList();
    }

    public async Task<SqlActionResultDto> AddForeignKeyAsync(SqlForeignKeyCrudRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            await using var connection = CreateConnection(profile, request.DatabaseName);
            await OpenThrottledAsync(connection, cancellationToken);
            var isSqlServer = IsSqlServer(profile);
            ValidateIdentifier(request.TableName, "Tên bảng");
            ValidateIdentifier(request.ColumnName, "Cột khóa ngoại");
            ValidateIdentifier(request.ReferencedTableName, "Bảng tham chiếu");
            ValidateIdentifier(request.ReferencedColumnName, "Cột tham chiếu");
            var constraint = string.IsNullOrWhiteSpace(request.ConstraintName)
                ? $"fk_{request.TableName}_{request.ColumnName}_{request.ReferencedTableName}_{request.ReferencedColumnName}"
                : request.ConstraintName.Trim();
            ValidateIdentifier(constraint, "Tên khóa ngoại");
            var table = BuildTableName(profile, request.DatabaseName, request.Schema, request.TableName);
            var referencedSchema = string.IsNullOrWhiteSpace(request.ReferencedSchema) ? request.Schema : request.ReferencedSchema;
            var refTable = BuildTableName(profile, request.DatabaseName, referencedSchema ?? string.Empty, request.ReferencedTableName);
            var updateRule = SafeForeignKeyRule(request.UpdateRule);
            var deleteRule = SafeForeignKeyRule(request.DeleteRule);
            var sql = new StringBuilder();
            sql.Append("ALTER TABLE ").Append(table)
                .Append(" ADD CONSTRAINT ").Append(QuoteIdentifier(constraint, isSqlServer))
                .Append(" FOREIGN KEY (").Append(QuoteIdentifier(request.ColumnName, isSqlServer)).Append(")")
                .Append(" REFERENCES ").Append(refTable).Append(" (").Append(QuoteIdentifier(request.ReferencedColumnName, isSqlServer)).Append(")");
            if (!string.IsNullOrWhiteSpace(updateRule)) sql.Append(" ON UPDATE ").Append(updateRule);
            if (!string.IsNullOrWhiteSpace(deleteRule)) sql.Append(" ON DELETE ").Append(deleteRule);
            var affected = await ExecuteNonQueryAsync(connection, sql.ToString(), [], cancellationToken);
            return new SqlActionResultDto { Success = true, Message = $"Đã thêm khóa ngoại {constraint}. Affected: {affected}." };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlActionResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không thêm được khóa ngoại") };
        }
    }

    public async Task<SqlActionResultDto> DropForeignKeyAsync(SqlForeignKeyCrudRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            await using var connection = CreateConnection(profile, request.DatabaseName);
            await OpenThrottledAsync(connection, cancellationToken);
            var isSqlServer = IsSqlServer(profile);
            ValidateIdentifier(request.TableName, "Tên bảng");
            var constraintName = string.IsNullOrWhiteSpace(request.OriginalConstraintName) ? request.ConstraintName : request.OriginalConstraintName;
            ValidateIdentifier(constraintName, "Tên khóa ngoại");
            var table = BuildTableName(profile, request.DatabaseName, request.Schema, request.TableName);
            var sql = isSqlServer
                ? $"ALTER TABLE {table} DROP CONSTRAINT {QuoteIdentifier(constraintName, true)}"
                : $"ALTER TABLE {table} DROP FOREIGN KEY {QuoteIdentifier(constraintName, false)}";
            var affected = await ExecuteNonQueryAsync(connection, sql, [], cancellationToken);
            return new SqlActionResultDto { Success = true, Message = $"Đã xóa khóa ngoại {constraintName}. Affected: {affected}." };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlActionResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không xóa được khóa ngoại") };
        }
    }

    public async Task<SqlActionResultDto> UpdateForeignKeyAsync(SqlForeignKeyCrudRequest request, CancellationToken cancellationToken = default)
    {
        var oldName = string.IsNullOrWhiteSpace(request.OriginalConstraintName) ? request.ConstraintName : request.OriginalConstraintName;
        var drop = await DropForeignKeyAsync(new SqlForeignKeyCrudRequest
        {
            ProfileId = request.ProfileId,
            DatabaseName = request.DatabaseName,
            Schema = request.Schema,
            TableName = request.TableName,
            ConstraintName = oldName,
            OriginalConstraintName = oldName
        }, cancellationToken);
        if (!drop.Success)
        {
            return drop;
        }

        var add = await AddForeignKeyAsync(request, cancellationToken);
        return add.Success
            ? new SqlActionResultDto { Success = true, Message = $"Đã cập nhật khóa ngoại {oldName} → {request.ConstraintName}." }
            : add;
    }

    public async Task<SqlRowPageDto> QueryRecordsAsync(SqlRecordQueryRequest request, CancellationToken cancellationToken = default)
    {
        request.PageSize = Math.Clamp(request.PageSize <= 0 ? 20 : request.PageSize, 5, 200);
        request.Page = Math.Max(1, request.Page);
        var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
        var columns = await ListColumnsAsync(request.ProfileId, request.DatabaseName, request.Schema, request.TableName, cancellationToken);
        await using var connection = CreateConnection(profile, request.DatabaseName);
        await OpenThrottledAsync(connection, cancellationToken);
        var isSqlServer = IsSqlServer(profile);
        var parameters = new List<DbParameter>();
        var where = BuildSearchWhere(connection, columns, request.Search, isSqlServer, parameters);
        var tableSql = BuildTableName(profile, request.DatabaseName, request.Schema, request.TableName);
        var countSql = $"SELECT COUNT(1) AS total_count FROM {tableSql}{where}";
        var countResult = await ReadRowsAsync(connection, countSql, parameters.Select(CloneParameter).ToList(), 1, cancellationToken);
        var total = 0;
        if (countResult.Rows.Count > 0 && int.TryParse(countResult.Rows[0].Values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCount))
        {
            total = parsedCount;
        }

        var offset = (request.Page - 1) * request.PageSize;
        string query;
        if (isSqlServer)
        {
            query = $"SELECT * FROM {tableSql}{where} ORDER BY (SELECT NULL) OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY";
        }
        else
        {
            query = $"SELECT * FROM {tableSql}{where} LIMIT @limit OFFSET @offset";
        }

        parameters.Add(CreateParameter(connection, "@limit", request.PageSize));
        parameters.Add(CreateParameter(connection, "@offset", offset));
        var data = await ReadRowsAsync(connection, query, parameters, request.PageSize, cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)request.PageSize));
        return new SqlRowPageDto
        {
            DatabaseName = request.DatabaseName,
            Schema = request.Schema,
            TableName = request.TableName,
            Page = Math.Clamp(request.Page, 1, totalPages),
            PageSize = request.PageSize,
            TotalRows = total,
            TotalPages = totalPages,
            Columns = columns,
            Rows = data.Rows
        };
    }

    public async Task<SqlQueryResultDto> ExecuteQueryAsync(SqlQueryRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.QueryText))
        {
            return new SqlQueryResultDto { Success = false, Message = "Chưa nhập lệnh query." };
        }

        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            await using var connection = CreateConnection(profile, request.DatabaseName);
            await OpenThrottledAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = request.QueryText;
            command.CommandTimeout = Math.Clamp(profile.TimeoutSeconds, 3, 120);

            if (LooksLikeReaderQuery(request.QueryText))
            {
                var result = await ReadRowsAsync(command, Math.Clamp(request.MaxRows <= 0 ? 500 : request.MaxRows, 1, 5000), cancellationToken);
                result.Success = true;
                result.Message = $"Query trả về {result.Rows.Count} dòng.";
                return result;
            }

            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            return new SqlQueryResultDto
            {
                Success = true,
                AffectedRows = affected,
                Message = $"Đã thực thi query. Affected rows: {affected}."
            };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlQueryResultDto
            {
                Success = false,
                Message = ToFriendlyDatabaseError(ex, "Không chạy được query")
            };
        }
    }


    public async Task<SqlActionResultDto> CreateDatabaseAsync(SqlDatabaseCrudRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            var isSqlServer = IsSqlServer(profile);
            ValidateIdentifier(request.DatabaseName, "Tên database");
            await using var connection = CreateConnection(profile);
            await OpenThrottledAsync(connection, cancellationToken);
            var sql = isSqlServer
                ? $"CREATE DATABASE {QuoteIdentifier(request.DatabaseName, true)}"
                : $"CREATE DATABASE IF NOT EXISTS {QuoteIdentifier(request.DatabaseName, false)} CHARACTER SET {SafeSqlWord(request.Charset, "utf8mb4")} COLLATE {SafeSqlWord(request.Collation, "utf8mb4_unicode_ci")}";
            var affected = await ExecuteNonQueryAsync(connection, sql, [], cancellationToken);
            return new SqlActionResultDto { Success = true, Message = $"Đã tạo database {request.DatabaseName}. Affected: {affected}." };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlActionResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không tạo được database") };
        }
    }

    public async Task<SqlActionResultDto> RenameDatabaseAsync(SqlDatabaseCrudRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            ValidateIdentifier(request.DatabaseName, "Database cũ");
            ValidateIdentifier(request.NewDatabaseName, "Database mới");
            if (!IsSqlServer(profile))
            {
                return new SqlActionResultDto
                {
                    Success = false,
                    Message = "MySQL/MariaDB không có lệnh RENAME DATABASE an toàn. Hãy export database cũ, tạo database mới rồi import lại để tránh mất dữ liệu."
                };
            }

            await using var connection = CreateConnection(profile, "master");
            await OpenThrottledAsync(connection, cancellationToken);
            var sql = $"ALTER DATABASE {QuoteIdentifier(request.DatabaseName, true)} MODIFY NAME = {QuoteIdentifier(request.NewDatabaseName, true)}";
            var affected = await ExecuteNonQueryAsync(connection, sql, [], cancellationToken);
            return new SqlActionResultDto { Success = true, Message = $"Đã đổi tên database thành {request.NewDatabaseName}. Affected: {affected}." };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlActionResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không đổi tên được database") };
        }
    }

    public async Task<SqlActionResultDto> DropDatabaseAsync(SqlDatabaseCrudRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            ValidateIdentifier(request.DatabaseName, "Tên database");
            var isSqlServer = IsSqlServer(profile);
            await using var connection = CreateConnection(profile, isSqlServer ? "master" : null);
            await OpenThrottledAsync(connection, cancellationToken);
            var sql = $"DROP DATABASE {(isSqlServer ? string.Empty : "IF EXISTS ")}{QuoteIdentifier(request.DatabaseName, isSqlServer)}";
            var affected = await ExecuteNonQueryAsync(connection, sql, [], cancellationToken);
            return new SqlActionResultDto { Success = true, Message = $"Đã xóa database {request.DatabaseName}. Affected: {affected}." };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlActionResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không xóa được database") };
        }
    }

    public async Task<SqlActionResultDto> CreateTableAsync(SqlTableCrudRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            var isSqlServer = IsSqlServer(profile);
            ValidateIdentifier(request.DatabaseName, "Tên database");
            ValidateIdentifier(request.TableName, "Tên bảng");
            if (request.Columns.Count == 0)
            {
                request.Columns.Add(new SqlColumnEditDto { Name = "id", DataType = isSqlServer ? "int" : "int", IsNullable = false, IsPrimaryKey = true, AutoIncrement = true });
            }

            await using var connection = CreateConnection(profile, request.DatabaseName);
            await OpenThrottledAsync(connection, cancellationToken);
            var tableName = BuildTableName(profile, request.DatabaseName, request.Schema, request.TableName);
            var columns = request.Columns.Where(c => !string.IsNullOrWhiteSpace(c.Name)).ToList();
            foreach (var column in columns)
            {
                ValidateIdentifier(column.Name, "Tên cột");
            }

            var columnSql = columns.Select(c => BuildColumnDefinition(c, isSqlServer, includePrimaryInline: columns.Count(x => x.IsPrimaryKey) <= 1)).ToList();
            var primaryColumns = columns.Where(c => c.IsPrimaryKey).Select(c => QuoteIdentifier(c.Name, isSqlServer)).ToList();
            if (primaryColumns.Count > 1)
            {
                columnSql.Add($"PRIMARY KEY ({string.Join(", ", primaryColumns)})");
            }

            var ifNotExists = request.IfNotExists && !isSqlServer ? "IF NOT EXISTS " : string.Empty;
            var suffix = isSqlServer ? string.Empty : $" ENGINE={SafeSqlWord(request.Engine, "InnoDB")} DEFAULT CHARSET={SafeSqlWord(request.Charset, "utf8mb4")}";
            var sql = $"CREATE TABLE {ifNotExists}{tableName} (\n  {string.Join(",\n  ", columnSql)}\n){suffix}";
            var affected = await ExecuteNonQueryAsync(connection, sql, [], cancellationToken);
            return new SqlActionResultDto { Success = true, Message = $"Đã tạo bảng {request.TableName}. Affected: {affected}." };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlActionResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không tạo được bảng") };
        }
    }

    public async Task<SqlActionResultDto> RenameTableAsync(SqlTableCrudRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            var isSqlServer = IsSqlServer(profile);
            ValidateIdentifier(request.TableName, "Tên bảng cũ");
            ValidateIdentifier(request.NewTableName, "Tên bảng mới");
            await using var connection = CreateConnection(profile, request.DatabaseName);
            await OpenThrottledAsync(connection, cancellationToken);
            var sql = isSqlServer
                ? $"EXEC sp_rename '{EscapeSingleQuotedSql((string.IsNullOrWhiteSpace(request.Schema) ? "dbo" : request.Schema) + "." + request.TableName)}', '{EscapeSingleQuotedSql(request.NewTableName)}'"
                : $"RENAME TABLE {BuildTableName(profile, request.DatabaseName, request.Schema, request.TableName)} TO {BuildTableName(profile, request.DatabaseName, request.Schema, request.NewTableName)}";
            var affected = await ExecuteNonQueryAsync(connection, sql, [], cancellationToken);
            return new SqlActionResultDto { Success = true, Message = $"Đã đổi tên bảng thành {request.NewTableName}. Affected: {affected}." };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlActionResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không đổi tên được bảng") };
        }
    }

    public async Task<SqlActionResultDto> DropTableAsync(SqlTableCrudRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            await using var connection = CreateConnection(profile, request.DatabaseName);
            await OpenThrottledAsync(connection, cancellationToken);
            var isSqlServer = IsSqlServer(profile);
            var sql = $"DROP TABLE {(isSqlServer ? string.Empty : "IF EXISTS ")}{BuildTableName(profile, request.DatabaseName, request.Schema, request.TableName)}";
            var affected = await ExecuteNonQueryAsync(connection, sql, [], cancellationToken);
            return new SqlActionResultDto { Success = true, Message = $"Đã xóa bảng {request.TableName}. Affected: {affected}." };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlActionResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không xóa được bảng") };
        }
    }

    public async Task<SqlActionResultDto> AddColumnAsync(SqlColumnCrudRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            await using var connection = CreateConnection(profile, request.DatabaseName);
            await OpenThrottledAsync(connection, cancellationToken);
            var isSqlServer = IsSqlServer(profile);
            ValidateIdentifier(request.Column.Name, "Tên cột");
            var sql = $"ALTER TABLE {BuildTableName(profile, request.DatabaseName, request.Schema, request.TableName)} ADD {BuildColumnDefinition(request.Column, isSqlServer, includePrimaryInline: false)}{BuildColumnPositionSql(request.Column, isSqlServer)}";
            var affected = await ExecuteNonQueryAsync(connection, sql, [], cancellationToken);
            return new SqlActionResultDto { Success = true, Message = $"Đã thêm cột {request.Column.Name}. Affected: {affected}." };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlActionResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không thêm được cột") };
        }
    }

    public async Task<SqlActionResultDto> UpdateColumnAsync(SqlColumnCrudRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            await using var connection = CreateConnection(profile, request.DatabaseName);
            await OpenThrottledAsync(connection, cancellationToken);
            var isSqlServer = IsSqlServer(profile);
            ValidateIdentifier(request.Column.OriginalName, "Tên cột cũ");
            ValidateIdentifier(request.Column.Name, "Tên cột mới");
            var table = BuildTableName(profile, request.DatabaseName, request.Schema, request.TableName);
            string sql;
            if (isSqlServer)
            {
                var statements = new List<string>();
                if (!string.Equals(request.Column.OriginalName, request.Column.Name, StringComparison.OrdinalIgnoreCase))
                {
                    var schema = string.IsNullOrWhiteSpace(request.Schema) ? "dbo" : request.Schema;
                    statements.Add($"EXEC sp_rename '{EscapeSingleQuotedSql(schema + "." + request.TableName + "." + request.Column.OriginalName)}', '{EscapeSingleQuotedSql(request.Column.Name)}', 'COLUMN'");
                }

                statements.Add($"ALTER TABLE {table} ALTER COLUMN {QuoteIdentifier(request.Column.Name, true)} {BuildColumnType(request.Column, true)} {(request.Column.IsNullable ? "NULL" : "NOT NULL")}");
                sql = string.Join(";\n", statements);
            }
            else
            {
                sql = $"ALTER TABLE {table} CHANGE COLUMN {QuoteIdentifier(request.Column.OriginalName, false)} {BuildColumnDefinition(request.Column, false, includePrimaryInline: false)}{BuildColumnPositionSql(request.Column, false)}";
            }

            var affected = await ExecuteScriptStatementsAsync(connection, SplitSqlScript(sql), cancellationToken);
            return new SqlActionResultDto { Success = true, Message = $"Đã cập nhật cột {request.Column.Name}. Affected: {affected}." };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlActionResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không cập nhật được cột") };
        }
    }

    public async Task<SqlActionResultDto> DropColumnAsync(SqlColumnCrudRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            await using var connection = CreateConnection(profile, request.DatabaseName);
            await OpenThrottledAsync(connection, cancellationToken);
            var isSqlServer = IsSqlServer(profile);
            ValidateIdentifier(request.Column.Name, "Tên cột");
            var sql = $"ALTER TABLE {BuildTableName(profile, request.DatabaseName, request.Schema, request.TableName)} DROP COLUMN {QuoteIdentifier(request.Column.Name, isSqlServer)}";
            var affected = await ExecuteNonQueryAsync(connection, sql, [], cancellationToken);
            return new SqlActionResultDto { Success = true, Message = $"Đã xóa cột {request.Column.Name}. Affected: {affected}." };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlActionResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không xóa được cột") };
        }
    }

    public async Task<SqlActionResultDto> InsertRecordAsync(SqlRecordWriteRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            var columns = await ListColumnsAsync(request.ProfileId, request.DatabaseName, request.Schema, request.TableName, cancellationToken);
            await using var connection = CreateConnection(profile, request.DatabaseName);
            await OpenThrottledAsync(connection, cancellationToken);
            var isSqlServer = IsSqlServer(profile);
            var table = BuildTableName(profile, request.DatabaseName, request.Schema, request.TableName);
            var writable = columns.Where(c => request.Values.ContainsKey(c.Name)).ToList();
            if (writable.Count == 0)
            {
                return new SqlActionResultDto { Success = false, Message = "Không có cột nào để insert." };
            }

            var parameters = new List<DbParameter>();
            var names = new List<string>();
            var values = new List<string>();
            for (var i = 0; i < writable.Count; i++)
            {
                var column = writable[i];
                names.Add(QuoteIdentifier(column.Name, isSqlServer));
                var parameterName = "@p" + i.ToString(CultureInfo.InvariantCulture);
                values.Add(parameterName);
                parameters.Add(CreateParameter(connection, parameterName, NormalizeSqlRecordValue(column, request.Values[column.Name], isSqlServer)));
            }

            var sql = $"INSERT INTO {table} ({string.Join(", ", names)}) VALUES ({string.Join(", ", values)})";
            var affected = await ExecuteNonQueryAsync(connection, sql, parameters, cancellationToken);
            return new SqlActionResultDto { Success = true, Message = $"Đã insert {affected} record." };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlActionResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không insert được record") };
        }
    }

    public async Task<SqlActionResultDto> UpdateRecordAsync(SqlRecordWriteRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            var columns = await ListColumnsAsync(request.ProfileId, request.DatabaseName, request.Schema, request.TableName, cancellationToken);
            await using var connection = CreateConnection(profile, request.DatabaseName);
            await OpenThrottledAsync(connection, cancellationToken);
            var isSqlServer = IsSqlServer(profile);
            var table = BuildTableName(profile, request.DatabaseName, request.Schema, request.TableName);
            var parameters = new List<DbParameter>();
            var assignments = new List<string>();
            var writable = columns.Where(c => request.Values.ContainsKey(c.Name)).ToList();
            for (var i = 0; i < writable.Count; i++)
            {
                var column = writable[i];
                var parameterName = "@set" + i.ToString(CultureInfo.InvariantCulture);
                assignments.Add($"{QuoteIdentifier(column.Name, isSqlServer)} = {parameterName}");
                parameters.Add(CreateParameter(connection, parameterName, NormalizeSqlRecordValue(column, request.Values[column.Name], isSqlServer)));
            }

            if (assignments.Count == 0)
            {
                return new SqlActionResultDto { Success = false, Message = "Không có cột nào để update." };
            }

            var where = BuildRecordWhere(connection, columns, request.OriginalValues, request.KeyColumns, isSqlServer, parameters);
            if (string.IsNullOrWhiteSpace(where))
            {
                return new SqlActionResultDto { Success = false, Message = "Không tạo được điều kiện WHERE an toàn cho record này." };
            }

            var sql = $"UPDATE {table} SET {string.Join(", ", assignments)} WHERE {where}";
            var affected = await ExecuteNonQueryAsync(connection, sql, parameters, cancellationToken);
            return new SqlActionResultDto { Success = true, Message = $"Đã update {affected} record." };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlActionResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không update được record") };
        }
    }

    public async Task<SqlActionResultDto> DeleteRecordAsync(SqlRecordDeleteRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            var columns = await ListColumnsAsync(request.ProfileId, request.DatabaseName, request.Schema, request.TableName, cancellationToken);
            await using var connection = CreateConnection(profile, request.DatabaseName);
            await OpenThrottledAsync(connection, cancellationToken);
            var isSqlServer = IsSqlServer(profile);
            var parameters = new List<DbParameter>();
            var where = BuildRecordWhere(connection, columns, request.OriginalValues, request.KeyColumns, isSqlServer, parameters);
            if (string.IsNullOrWhiteSpace(where))
            {
                return new SqlActionResultDto { Success = false, Message = "Không tạo được điều kiện WHERE an toàn để xóa record." };
            }

            var sql = $"DELETE FROM {BuildTableName(profile, request.DatabaseName, request.Schema, request.TableName)} WHERE {where}";
            var affected = await ExecuteNonQueryAsync(connection, sql, parameters, cancellationToken);
            return new SqlActionResultDto { Success = true, Message = $"Đã xóa {affected} record." };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlActionResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không xóa được record") };
        }
    }

    public async Task<SqlQueryResultDto> ImportSqlAsync(SqlImportRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ScriptText))
        {
            return new SqlQueryResultDto { Success = false, Message = "Chưa nhập SQL để import." };
        }

        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            var isSqlServer = IsSqlServer(profile);
            await using var connection = CreateConnection(profile, request.DatabaseName);
            await OpenThrottledAsync(connection, cancellationToken);
            var statements = SplitSqlScript(request.ScriptText);
            if (request.DisableForeignKeyChecks && !isSqlServer)
            {
                statements.Insert(0, "SET FOREIGN_KEY_CHECKS=0");
                statements.Add("SET FOREIGN_KEY_CHECKS=1");
            }
            else if (request.DisableForeignKeyChecks && isSqlServer)
            {
                // SQL Server disable FK checks needs per-table ALTER. Giữ import an toàn và không sinh lệnh toàn cục nguy hiểm.
            }

            var importResult = await ExecuteScriptStatementsAsync(connection, statements, request.ContinueOnError, cancellationToken);
            return new SqlQueryResultDto
            {
                Success = importResult.FailedCount == 0 || request.ContinueOnError,
                AffectedRows = importResult.AffectedRows,
                Message = importResult.FailedCount == 0
                    ? $"Import xong {importResult.TotalStatements} lệnh. Affected rows: {importResult.AffectedRows}."
                    : $"Import chạy {importResult.TotalStatements} lệnh, lỗi {importResult.FailedCount} lệnh. Affected rows: {importResult.AffectedRows}. {string.Join(" | ", importResult.Errors.Take(3))}"
            };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlQueryResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không import được SQL") };
        }
    }

    public async Task<SqlQueryResultDto> ExportSqlAsync(SqlExportRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            var isSqlServer = IsSqlServer(profile);
            var scope = NormalizeExportScope(request.Scope, request.TableName);
            if (string.IsNullOrWhiteSpace(request.DatabaseName))
            {
                return new SqlQueryResultDto { Success = false, Message = "Chưa chọn database để export." };
            }
            if ((scope == "table" || scope == "selectedRows") && string.IsNullOrWhiteSpace(request.TableName))
            {
                return new SqlQueryResultDto { Success = false, Message = "Chưa chọn bảng để export." };
            }
            if (scope == "selectedRows" && request.SelectedRows.Count == 0)
            {
                return new SqlQueryResultDto { Success = false, Message = "Chưa tick record nào để export selected rows." };
            }

            var allObjects = await ListTablesAsync(request.ProfileId, request.DatabaseName, null, cancellationToken);
            List<SqlTableDto> exportObjects;
            if (scope == "database")
            {
                exportObjects = allObjects;
            }
            else
            {
                var selected = allObjects.FirstOrDefault(x => string.Equals(x.Name, request.TableName, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(request.Schema) || string.Equals(x.Schema, request.Schema, StringComparison.OrdinalIgnoreCase)))
                    ?? new SqlTableDto { Schema = request.Schema, Name = request.TableName, FullName = request.TableName, Type = "BASE TABLE" };
                exportObjects = [selected];
            }

            var baseTables = exportObjects.Where(IsBaseTableLike).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var views = exportObjects.Where(IsViewLike).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var sb = new StringBuilder();
            AppendExportHeader(sb, request, scope);

            await using var connection = CreateConnection(profile, request.DatabaseName);
            await OpenThrottledAsync(connection, cancellationToken);

            if (!isSqlServer)
            {
                sb.AppendLine("SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS;");
                sb.AppendLine("SET FOREIGN_KEY_CHECKS=0;");
                sb.AppendLine($"CREATE DATABASE IF NOT EXISTS {QuoteIdentifier(request.DatabaseName, false)};");
                sb.AppendLine($"USE {QuoteIdentifier(request.DatabaseName, false)};");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"USE {QuoteIdentifier(request.DatabaseName, true)};");
                sb.AppendLine("GO");
                sb.AppendLine();
            }

            if (request.IncludeDrop && request.IncludeStructure && scope != "selectedRows")
            {
                AppendDropStatements(sb, profile, request.DatabaseName, views, objectKind: "view");
                AppendDropStatements(sb, profile, request.DatabaseName, baseTables, objectKind: "table");
            }

            var exportedRows = 0;
            foreach (var table in baseTables)
            {
                var columns = await ListColumnsAsync(request.ProfileId, request.DatabaseName, table.Schema, table.Name, cancellationToken);
                if (request.IncludeStructure)
                {
                    sb.AppendLine($"-- --------------------------------------------------------");
                    sb.AppendLine($"-- Structure for table {table.Name}");
                    if (!isSqlServer && request.IncludeIndexes && request.IncludeForeignKeys)
                    {
                        var showCreate = await TryReadMySqlShowCreateObjectAsync(connection, request.DatabaseName, table.Schema, table.Name, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(showCreate))
                        {
                            sb.AppendLine(showCreate.TrimEnd(';'));
                            sb.AppendLine(";");
                            sb.AppendLine();
                        }
                        else
                        {
                            await AppendGenericCreateTableAsync(sb, profile, request, table, columns, includeDropAlreadyHandled: true, cancellationToken);
                        }
                    }
                    else
                    {
                        await AppendGenericCreateTableAsync(sb, profile, request, table, columns, includeDropAlreadyHandled: true, cancellationToken);
                    }
                }

                if (request.IncludeData)
                {
                    IReadOnlyList<Dictionary<string, string?>> rows;
                    if (scope == "selectedRows")
                    {
                        rows = request.SelectedRows;
                    }
                    else
                    {
                        var page = await QueryRecordsAsync(new SqlRecordQueryRequest
                        {
                            ProfileId = request.ProfileId,
                            DatabaseName = request.DatabaseName,
                            Schema = table.Schema,
                            TableName = table.Name,
                            Page = 1,
                            PageSize = Math.Clamp(request.MaxRows <= 0 ? 1000 : request.MaxRows, 1, 5000)
                        }, cancellationToken);
                        rows = page.Rows;
                    }

                    exportedRows += rows.Count;
                    AppendDataStatements(sb, profile, request, table, columns, rows, isSqlServer);
                }
            }

            if (request.IncludeStructure && request.IncludeViews && scope != "selectedRows")
            {
                foreach (var view in views)
                {
                    sb.AppendLine("-- --------------------------------------------------------");
                    sb.AppendLine($"-- Structure for view {view.Name}");
                    if (!isSqlServer)
                    {
                        var showCreate = await TryReadMySqlShowCreateObjectAsync(connection, request.DatabaseName, view.Schema, view.Name, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(showCreate))
                        {
                            sb.AppendLine(showCreate.TrimEnd(';'));
                            sb.AppendLine(";");
                            sb.AppendLine();
                        }
                    }
                    else
                    {
                        sb.AppendLine($"-- SQL Server view export chưa dựng full CREATE VIEW trong bản SQL-13. View: {view.FullName}");
                        sb.AppendLine();
                    }
                }
            }

            if (!isSqlServer && request.IncludeStructure && scope == "database")
            {
                if (request.IncludeTriggers) await AppendMySqlTriggersAsync(sb, connection, request.DatabaseName, request.IncludeDrop, cancellationToken);
                if (request.IncludeRoutines) await AppendMySqlRoutinesAsync(sb, connection, request.DatabaseName, request.IncludeDrop, cancellationToken);
                if (request.IncludeEvents) await AppendMySqlEventsAsync(sb, connection, request.DatabaseName, request.IncludeDrop, cancellationToken);
            }

            if (!isSqlServer)
            {
                sb.AppendLine("SET FOREIGN_KEY_CHECKS=@OLD_FOREIGN_KEY_CHECKS;");
            }

            var objectCount = baseTables.Count + views.Count;
            var message = scope == "selectedRows"
                ? $"Đã tạo script export {exportedRows} selected rows từ bảng {request.TableName}."
                : $"Đã tạo script export {objectCount} object ({baseTables.Count} bảng, {views.Count} view), data rows: {exportedRows}.";
            return new SqlQueryResultDto
            {
                Success = true,
                Message = message,
                Rows = [new Dictionary<string, string?> { ["script"] = sb.ToString() }],
                Columns = [new SqlColumnDto { Name = "script", DataType = "sql", Ordinal = 1 }]
            };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlQueryResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không export được SQL") };
        }
    }

    private static string NormalizeExportScope(string? scope, string? tableName)
    {
        var value = string.IsNullOrWhiteSpace(scope) ? string.Empty : scope.Trim();
        if (value.Equals("selectedRows", StringComparison.OrdinalIgnoreCase) || value.Equals("rows", StringComparison.OrdinalIgnoreCase)) return "selectedRows";
        if (value.Equals("table", StringComparison.OrdinalIgnoreCase)) return "table";
        if (value.Equals("database", StringComparison.OrdinalIgnoreCase) || value.Equals("db", StringComparison.OrdinalIgnoreCase)) return "database";
        return string.IsNullOrWhiteSpace(tableName) ? "database" : "table";
    }

    private static void AppendExportHeader(StringBuilder sb, SqlExportRequest request, string scope)
    {
        sb.AppendLine("-- ConfigTool SQL export");
        sb.AppendLine("-- SQL-13 Import/Export phpMyAdmin parity patch");
        sb.AppendLine($"-- Database: {request.DatabaseName}");
        sb.AppendLine($"-- Scope: {scope}");
        if (!string.IsNullOrWhiteSpace(request.TableName)) sb.AppendLine($"-- Table: {request.TableName}");
        sb.AppendLine($"-- At: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();
    }

    private static bool IsViewLike(SqlTableDto table)
        => table.Type?.Contains("VIEW", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsBaseTableLike(SqlTableDto table)
        => string.IsNullOrWhiteSpace(table.Type)
            || table.Type.Contains("BASE TABLE", StringComparison.OrdinalIgnoreCase)
            || table.Type.Equals("USER_TABLE", StringComparison.OrdinalIgnoreCase);

    private static void AppendDropStatements(StringBuilder sb, SqlConnectionProfileDto profile, string databaseName, List<SqlTableDto> objects, string objectKind)
    {
        if (objects.Count == 0) return;
        var isSqlServer = IsSqlServer(profile);
        foreach (var obj in objects.OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = BuildTableName(profile, databaseName, obj.Schema, obj.Name);
            if (objectKind == "view")
            {
                sb.AppendLine(isSqlServer ? $"DROP VIEW IF EXISTS {name};" : $"DROP VIEW IF EXISTS {name};");
            }
            else
            {
                sb.AppendLine($"DROP TABLE {(isSqlServer ? "IF EXISTS " : "IF EXISTS ")}{name};");
            }
        }
        sb.AppendLine();
    }

    private async Task AppendGenericCreateTableAsync(StringBuilder sb, SqlConnectionProfileDto profile, SqlExportRequest request, SqlTableDto table, List<SqlColumnDto> columns, bool includeDropAlreadyHandled, CancellationToken cancellationToken)
    {
        var isSqlServer = IsSqlServer(profile);
        if (request.IncludeDrop && !includeDropAlreadyHandled)
        {
            sb.AppendLine($"DROP TABLE {(isSqlServer ? "IF EXISTS " : "IF EXISTS ")}{BuildTableName(profile, request.DatabaseName, table.Schema, table.Name)};");
        }

        var columnDefs = columns.Select(c => BuildColumnDefinition(new SqlColumnEditDto
        {
            Name = c.Name,
            DataType = string.IsNullOrWhiteSpace(c.FullDataType) ? c.DataType : c.FullDataType,
            Length = c.Length,
            IsNullable = c.IsNullable,
            IsPrimaryKey = c.IsPrimaryKey,
            DefaultValue = c.DefaultValue,
            Extra = c.Extra,
            Comment = c.Comment
        }, isSqlServer, includePrimaryInline: columns.Count(x => x.IsPrimaryKey) <= 1)).ToList();
        var primaryColumns = columns.Where(c => c.IsPrimaryKey).Select(c => QuoteIdentifier(c.Name, isSqlServer)).ToList();
        if (primaryColumns.Count > 1)
        {
            columnDefs.Add($"PRIMARY KEY ({string.Join(", ", primaryColumns)})");
        }
        sb.AppendLine($"CREATE TABLE {BuildTableName(profile, request.DatabaseName, table.Schema, table.Name)} (\n  {string.Join(",\n  ", columnDefs)}\n);");
        sb.AppendLine();

        if (request.IncludeIndexes)
        {
            var indexes = await ListIndexesAsync(request.ProfileId, request.DatabaseName, table.Schema, table.Name, null, cancellationToken);
            foreach (var index in indexes.Where(x => !x.IsPrimary))
            {
                sb.AppendLine(BuildCreateIndexSql(profile, new SqlIndexCrudRequest
                {
                    ProfileId = request.ProfileId,
                    DatabaseName = request.DatabaseName,
                    Schema = table.Schema,
                    TableName = table.Name,
                    Name = index.Name,
                    IndexType = index.IsUnique ? "UNIQUE" : index.IsFullText ? "FULLTEXT" : index.IsSpatial ? "SPATIAL" : "INDEX",
                    Columns = index.Columns
                }) + ";");
            }
            if (indexes.Any(x => !x.IsPrimary)) sb.AppendLine();
        }

        if (request.IncludeForeignKeys)
        {
            var foreignKeys = await ListForeignKeysAsync(request.ProfileId, request.DatabaseName, table.Schema, table.Name, null, cancellationToken);
            foreach (var fk in foreignKeys)
            {
                sb.AppendLine(BuildForeignKeyCreateSql(profile, request.DatabaseName, fk) + ";");
            }
            if (foreignKeys.Count > 0) sb.AppendLine();
        }
    }

    private static void AppendDataStatements(StringBuilder sb, SqlConnectionProfileDto profile, SqlExportRequest request, SqlTableDto table, List<SqlColumnDto> columns, IReadOnlyList<Dictionary<string, string?>> rows, bool isSqlServer)
    {
        sb.AppendLine("-- --------------------------------------------------------");
        sb.AppendLine($"-- Data for {table.Name}: {rows.Count} rows");
        if (rows.Count == 0 || columns.Count == 0)
        {
            sb.AppendLine();
            return;
        }

        var names = columns.Select(c => QuoteIdentifier(c.Name, isSqlServer)).ToList();
        var tableName = BuildTableName(profile, request.DatabaseName, table.Schema, table.Name);
        if (request.BatchInsert)
        {
            const int chunkSize = 100;
            for (var i = 0; i < rows.Count; i += chunkSize)
            {
                var chunk = rows.Skip(i).Take(chunkSize).ToList();
                sb.AppendLine($"INSERT INTO {tableName} ({string.Join(", ", names)}) VALUES");
                for (var index = 0; index < chunk.Count; index++)
                {
                    var row = chunk[index];
                    var values = columns.Select(c => ToSqlLiteral(row.TryGetValue(c.Name, out var value) ? value : null)).ToList();
                    sb.Append("  (").Append(string.Join(", ", values)).Append(')');
                    sb.AppendLine(index == chunk.Count - 1 ? ";" : ",");
                }
            }
        }
        else
        {
            foreach (var row in rows)
            {
                var values = columns.Select(c => ToSqlLiteral(row.TryGetValue(c.Name, out var value) ? value : null)).ToList();
                sb.AppendLine($"INSERT INTO {tableName} ({string.Join(", ", names)}) VALUES ({string.Join(", ", values)});");
            }
        }
        sb.AppendLine();
    }

    private static async Task<string> TryReadMySqlShowCreateObjectAsync(DbConnection connection, string databaseName, string schema, string objectName, CancellationToken cancellationToken)
    {
        try
        {
            var fullName = $"{QuoteIdentifier(databaseName, false)}.{QuoteIdentifier(objectName, false)}";
            var result = await ReadRowsAsync(connection, $"SHOW CREATE TABLE {fullName}", [], 1, cancellationToken);
            var row = result.Rows.FirstOrDefault();
            if (row is null) return string.Empty;
            foreach (var key in new[] { "Create Table", "Create View", "Create Table `" + objectName + "`" })
            {
                if (row.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value!;
            }
            return row.Values.Skip(1).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task AppendMySqlTriggersAsync(StringBuilder sb, DbConnection connection, string databaseName, bool includeDrop, CancellationToken cancellationToken)
    {
        var rows = await SafeReadRowsAsync(connection, "SELECT TRIGGER_NAME FROM INFORMATION_SCHEMA.TRIGGERS WHERE TRIGGER_SCHEMA = @db ORDER BY EVENT_OBJECT_TABLE, TRIGGER_NAME", databaseName, cancellationToken);
        if (rows.Count == 0) return;
        sb.AppendLine("-- --------------------------------------------------------");
        sb.AppendLine("-- Triggers");
        foreach (var row in rows)
        {
            var name = Value(row, "TRIGGER_NAME");
            var create = await ReadMySqlShowCreateNamedObjectAsync(connection, databaseName, name, "TRIGGER", "SQL Original Statement", cancellationToken);
            if (string.IsNullOrWhiteSpace(create)) continue;
            if (includeDrop) sb.AppendLine($"DROP TRIGGER IF EXISTS {QuoteIdentifier(databaseName, false)}.{QuoteIdentifier(name, false)};");
            AppendDelimitedStatement(sb, create);
        }
    }

    private static async Task AppendMySqlRoutinesAsync(StringBuilder sb, DbConnection connection, string databaseName, bool includeDrop, CancellationToken cancellationToken)
    {
        var rows = await SafeReadRowsAsync(connection, "SELECT ROUTINE_NAME, ROUTINE_TYPE FROM INFORMATION_SCHEMA.ROUTINES WHERE ROUTINE_SCHEMA = @db ORDER BY ROUTINE_TYPE, ROUTINE_NAME", databaseName, cancellationToken);
        if (rows.Count == 0) return;
        sb.AppendLine("-- --------------------------------------------------------");
        sb.AppendLine("-- Routines / procedures / functions");
        foreach (var row in rows)
        {
            var name = Value(row, "ROUTINE_NAME");
            var type = Value(row, "ROUTINE_TYPE").Equals("FUNCTION", StringComparison.OrdinalIgnoreCase) ? "FUNCTION" : "PROCEDURE";
            var createColumn = type == "FUNCTION" ? "Create Function" : "Create Procedure";
            var create = await ReadMySqlShowCreateNamedObjectAsync(connection, databaseName, name, type, createColumn, cancellationToken);
            if (string.IsNullOrWhiteSpace(create)) continue;
            if (includeDrop) sb.AppendLine($"DROP {type} IF EXISTS {QuoteIdentifier(databaseName, false)}.{QuoteIdentifier(name, false)};");
            AppendDelimitedStatement(sb, create);
        }
    }

    private static async Task AppendMySqlEventsAsync(StringBuilder sb, DbConnection connection, string databaseName, bool includeDrop, CancellationToken cancellationToken)
    {
        var rows = await SafeReadRowsAsync(connection, "SELECT EVENT_NAME FROM INFORMATION_SCHEMA.EVENTS WHERE EVENT_SCHEMA = @db ORDER BY EVENT_NAME", databaseName, cancellationToken);
        if (rows.Count == 0) return;
        sb.AppendLine("-- --------------------------------------------------------");
        sb.AppendLine("-- Events");
        foreach (var row in rows)
        {
            var name = Value(row, "EVENT_NAME");
            var create = await ReadMySqlShowCreateNamedObjectAsync(connection, databaseName, name, "EVENT", "Create Event", cancellationToken);
            if (string.IsNullOrWhiteSpace(create)) continue;
            if (includeDrop) sb.AppendLine($"DROP EVENT IF EXISTS {QuoteIdentifier(databaseName, false)}.{QuoteIdentifier(name, false)};");
            AppendDelimitedStatement(sb, create);
        }
    }

    private static async Task<List<Dictionary<string, string?>>> SafeReadRowsAsync(DbConnection connection, string sql, string databaseName, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = new List<DbParameter> { CreateParameter(connection, "@db", databaseName) };
            var result = await ReadRowsAsync(connection, sql, parameters, 5000, cancellationToken);
            return result.Rows;
        }
        catch
        {
            return [];
        }
    }

    private static async Task<string> ReadMySqlShowCreateNamedObjectAsync(DbConnection connection, string databaseName, string name, string kind, string preferredColumn, CancellationToken cancellationToken)
    {
        try
        {
            var fullName = $"{QuoteIdentifier(databaseName, false)}.{QuoteIdentifier(name, false)}";
            var result = await ReadRowsAsync(connection, $"SHOW CREATE {kind} {fullName}", [], 1, cancellationToken);
            var row = result.Rows.FirstOrDefault();
            if (row is null) return string.Empty;
            if (row.TryGetValue(preferredColumn, out var preferred) && !string.IsNullOrWhiteSpace(preferred)) return preferred!;
            return row.Values.Skip(1).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void AppendDelimitedStatement(StringBuilder sb, string sql)
    {
        var statement = sql.Trim().TrimEnd(';');
        if (string.IsNullOrWhiteSpace(statement)) return;
        sb.AppendLine("DELIMITER ;;");
        sb.AppendLine(statement + ";;");
        sb.AppendLine("DELIMITER ;");
        sb.AppendLine();
    }


    public async Task<SqlMaintenanceResultDto> RunMaintenanceAsync(SqlMaintenanceRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            await using var connection = CreateConnection(profile, request.DatabaseName);
            await OpenThrottledAsync(connection, cancellationToken);
            var isSqlServer = IsSqlServer(profile);
            var action = NormalizeMaintenanceAction(request.Action);
            if (string.IsNullOrWhiteSpace(request.TableName))
            {
                return new SqlMaintenanceResultDto { Success = false, Message = "Chưa chọn bảng để chạy maintenance." };
            }

            if ((action == "truncate" || action == "repair") && !request.ConfirmDangerous)
            {
                return new SqlMaintenanceResultDto { Success = false, Message = "Thao tác này có thể ảnh hưởng dữ liệu. Hãy bật xác nhận nguy hiểm trước khi chạy." };
            }

            var table = BuildTableName(profile, request.DatabaseName, request.Schema, request.TableName);
            string sql;
            var readerQuery = true;
            if (isSqlServer)
            {
                sql = action switch
                {
                    "analyze" or "optimize" => $"UPDATE STATISTICS {table}",
                    "check" => $"DBCC CHECKTABLE ('{EscapeSingleQuotedSql(string.IsNullOrWhiteSpace(request.Schema) ? request.TableName : request.Schema + "." + request.TableName)}') WITH NO_INFOMSGS",
                    "truncate" => $"TRUNCATE TABLE {table}",
                    "checksum" => $"SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) AS checksum_value FROM {table}",
                    "repair" => string.Empty,
                    _ => string.Empty
                };
                readerQuery = action == "check" || action == "checksum";
            }
            else
            {
                sql = action switch
                {
                    "analyze" => $"ANALYZE TABLE {table}",
                    "check" => $"CHECK TABLE {table}",
                    "optimize" => $"OPTIMIZE TABLE {table}",
                    "repair" => $"REPAIR TABLE {table}",
                    "checksum" => $"CHECKSUM TABLE {table}",
                    "truncate" => $"TRUNCATE TABLE {table}",
                    _ => string.Empty
                };
                readerQuery = action != "truncate";
            }

            if (string.IsNullOrWhiteSpace(sql))
            {
                return new SqlMaintenanceResultDto { Success = false, Message = $"Provider hiện tại chưa hỗ trợ maintenance action: {action}." };
            }

            if (readerQuery)
            {
                var rows = await ReadRowsAsync(connection, sql, [], 500, cancellationToken);
                return new SqlMaintenanceResultDto
                {
                    Success = true,
                    Message = $"Đã chạy {action} cho {request.TableName}.",
                    Columns = rows.Columns,
                    Rows = rows.Rows
                };
            }

            var affected = await ExecuteNonQueryAsync(connection, sql, [], cancellationToken);
            return new SqlMaintenanceResultDto
            {
                Success = true,
                Message = $"Đã chạy {action} cho {request.TableName}. Affected: {affected}."
            };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlMaintenanceResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không chạy được maintenance") };
        }
    }

    public async Task<List<SqlProcessDto>> ListProcessesAsync(string profileId, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(profileId, cancellationToken);
            await using var connection = CreateConnection(profile, profile.Database);
            await OpenThrottledAsync(connection, cancellationToken);
            var isSqlServer = IsSqlServer(profile);
            var rows = isSqlServer
                ? await ReadRowsAsync(connection, "SELECT CAST(session_id AS NVARCHAR(32)) AS Id, login_name AS [User], host_name AS Host, DB_NAME(database_id) AS [Database], status AS Command, CAST(cpu_time AS NVARCHAR(32)) AS [Time], status AS State, COALESCE(program_name, '') AS Info FROM sys.dm_exec_sessions WHERE is_user_process = 1 ORDER BY session_id", [], 500, cancellationToken)
                : await ReadRowsAsync(connection, "SHOW FULL PROCESSLIST", [], 500, cancellationToken);

            return rows.Rows.Select(row => new SqlProcessDto
            {
                Id = FirstValue(row, "Id", "ID", "session_id"),
                User = FirstValue(row, "User", "login_name"),
                Host = FirstValue(row, "Host", "host_name"),
                Database = FirstValue(row, "db", "Db", "Database"),
                Command = FirstValue(row, "Command", "status"),
                Time = FirstValue(row, "Time", "cpu_time"),
                State = FirstValue(row, "State", "status"),
                Info = FirstValue(row, "Info", "program_name")
            }).Where(x => !string.IsNullOrWhiteSpace(x.Id)).ToList();
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return [new SqlProcessDto { Id = "error", Info = ToFriendlyDatabaseError(ex, "Không tải được process list") }];
        }
    }

    public async Task<SqlActionResultDto> KillProcessAsync(SqlKillProcessRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.ProcessId) || !request.ProcessId.All(char.IsDigit))
            {
                return new SqlActionResultDto { Success = false, Message = "Process id không hợp lệ." };
            }

            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            await using var connection = CreateConnection(profile, profile.Database);
            await OpenThrottledAsync(connection, cancellationToken);
            var sql = IsSqlServer(profile) ? $"KILL {request.ProcessId}" : $"KILL {request.ProcessId}";
            await ExecuteNonQueryAsync(connection, sql, [], cancellationToken);
            return new SqlActionResultDto { Success = true, Message = $"Đã kill process {request.ProcessId}." };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlActionResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không kill được process") };
        }
    }

    public async Task<List<SqlVariableDto>> ListVariablesAsync(string profileId, string? search, bool includeStatus, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _configService.GetProfileOrThrowAsync(profileId, cancellationToken);
            await using var connection = CreateConnection(profile, profile.Database);
            await OpenThrottledAsync(connection, cancellationToken);
            var isSqlServer = IsSqlServer(profile);
            var all = new List<SqlVariableDto>();
            if (isSqlServer)
            {
                var cfg = await ReadRowsAsync(connection, "SELECT name, CAST(value_in_use AS NVARCHAR(512)) AS value FROM sys.configurations ORDER BY name", [], 1000, cancellationToken);
                all.AddRange(cfg.Rows.Select(row => new SqlVariableDto { Name = Value(row, "name"), Value = Value(row, "value"), Group = "variable" }));
                if (includeStatus)
                {
                    var status = await ReadRowsAsync(connection, "SELECT TOP 300 counter_name AS name, CAST(cntr_value AS NVARCHAR(512)) AS value FROM sys.dm_os_performance_counters ORDER BY counter_name", [], 300, cancellationToken);
                    all.AddRange(status.Rows.Select(row => new SqlVariableDto { Name = Value(row, "name"), Value = Value(row, "value"), Group = "status" }));
                }
            }
            else
            {
                var vars = await ReadRowsAsync(connection, "SHOW VARIABLES", [], 2000, cancellationToken);
                all.AddRange(vars.Rows.Select(row => new SqlVariableDto { Name = FirstValue(row, "Variable_name", "Name"), Value = FirstValue(row, "Value"), Group = "variable" }));
                if (includeStatus)
                {
                    var status = await ReadRowsAsync(connection, "SHOW GLOBAL STATUS", [], 2000, cancellationToken);
                    all.AddRange(status.Rows.Select(row => new SqlVariableDto { Name = FirstValue(row, "Variable_name", "Name"), Value = FirstValue(row, "Value"), Group = "status" }));
                }
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                all = all.Where(x => x.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) || x.Value.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            }

            return all.Take(1000).OrderBy(x => x.Group).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return [new SqlVariableDto { Name = "error", Value = ToFriendlyDatabaseError(ex, "Không tải được variables/status"), Group = "error" }];
        }
    }

    public async Task<SqlDesignerDto> GetDesignerAsync(SqlDesignerRequest request, CancellationToken cancellationToken = default)
    {
        var designer = new SqlDesignerDto { DatabaseName = request.DatabaseName };
        try
        {
            if (string.IsNullOrWhiteSpace(request.ProfileId) || string.IsNullOrWhiteSpace(request.DatabaseName))
            {
                return designer;
            }

            var tables = await ListTablesAsync(request.ProfileId, request.DatabaseName, request.Search, cancellationToken);
            tables = tables.Where(x => string.Equals(x.Type, "BASE TABLE", StringComparison.OrdinalIgnoreCase) || x.Type.Contains("TABLE", StringComparison.OrdinalIgnoreCase)).Take(80).ToList();
            var index = 0;
            foreach (var table in tables)
            {
                var columns = await ListColumnsAsync(request.ProfileId, request.DatabaseName, table.Schema, table.Name, cancellationToken);
                designer.Tables.Add(new SqlDesignerTableDto
                {
                    Schema = table.Schema,
                    Name = table.Name,
                    FullName = table.FullName,
                    RowCount = table.RowCount,
                    X = 24 + (index % 4) * 330,
                    Y = 24 + (index / 4) * 260,
                    Columns = columns.Take(16).ToList()
                });
                index++;
            }

            var profile = await _configService.GetProfileOrThrowAsync(request.ProfileId, cancellationToken);
            await using var connection = CreateConnection(profile, request.DatabaseName);
            await OpenThrottledAsync(connection, cancellationToken);
            var rows = IsSqlServer(profile)
                ? await ReadRowsAsync(connection, """
SELECT fk.name AS ConstraintName,
       SCHEMA_NAME(tp.schema_id) AS FromSchema,
       tp.name AS FromTable,
       cp.name AS FromColumn,
       SCHEMA_NAME(tr.schema_id) AS ToSchema,
       tr.name AS ToTable,
       cr.name AS ToColumn,
       fk.update_referential_action_desc AS UpdateRule,
       fk.delete_referential_action_desc AS DeleteRule
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
JOIN sys.tables tp ON fkc.parent_object_id = tp.object_id
JOIN sys.columns cp ON fkc.parent_object_id = cp.object_id AND fkc.parent_column_id = cp.column_id
JOIN sys.tables tr ON fkc.referenced_object_id = tr.object_id
JOIN sys.columns cr ON fkc.referenced_object_id = cr.object_id AND fkc.referenced_column_id = cr.column_id
ORDER BY FromSchema, FromTable, ConstraintName
""", [], 2000, cancellationToken)
                : await ReadRowsAsync(connection, """
SELECT kcu.CONSTRAINT_NAME AS ConstraintName,
       kcu.TABLE_SCHEMA AS FromSchema,
       kcu.TABLE_NAME AS FromTable,
       kcu.COLUMN_NAME AS FromColumn,
       kcu.REFERENCED_TABLE_SCHEMA AS ToSchema,
       kcu.REFERENCED_TABLE_NAME AS ToTable,
       kcu.REFERENCED_COLUMN_NAME AS ToColumn,
       rc.UPDATE_RULE AS UpdateRule,
       rc.DELETE_RULE AS DeleteRule
FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
LEFT JOIN INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc
       ON rc.CONSTRAINT_SCHEMA = kcu.CONSTRAINT_SCHEMA AND rc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
WHERE kcu.TABLE_SCHEMA = @db AND kcu.REFERENCED_TABLE_NAME IS NOT NULL
ORDER BY kcu.TABLE_NAME, kcu.CONSTRAINT_NAME, kcu.ORDINAL_POSITION
""", [CreateParameter(connection, "@db", request.DatabaseName)], 2000, cancellationToken);

            var visible = designer.Tables.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows.Rows)
            {
                var fromTable = Value(row, "FromTable");
                var toTable = Value(row, "ToTable");
                if (!visible.Contains(fromTable) || !visible.Contains(toTable))
                {
                    continue;
                }

                designer.Relations.Add(new SqlDesignerRelationDto
                {
                    ConstraintName = Value(row, "ConstraintName"),
                    FromSchema = Value(row, "FromSchema"),
                    FromTable = fromTable,
                    FromColumn = Value(row, "FromColumn"),
                    ToSchema = Value(row, "ToSchema"),
                    ToTable = toTable,
                    ToColumn = Value(row, "ToColumn"),
                    UpdateRule = Value(row, "UpdateRule"),
                    DeleteRule = Value(row, "DeleteRule")
                });
            }

            return designer;
        }
        catch
        {
            return designer;
        }
    }

    public async Task<List<SqlQueryTemplateDto>> BuildQueryTemplatesAsync(string profileId, string databaseName, string schema, string tableName, CancellationToken cancellationToken = default)
    {
        var profile = await _configService.GetProfileOrThrowAsync(profileId, cancellationToken);
        var isSqlServer = IsSqlServer(profile);
        List<SqlColumnDto> columns = string.IsNullOrWhiteSpace(tableName)
            ? []
            : await ListColumnsAsync(profileId, databaseName, schema, tableName, cancellationToken);
        var table = string.IsNullOrWhiteSpace(tableName) ? "table_name" : BuildTableName(profile, databaseName, schema, tableName);
        var selectLimit = isSqlServer ? $"SELECT TOP 100 * FROM {table};" : $"SELECT * FROM {table} LIMIT 100;";
        List<string> colNames = columns.Count == 0 ? ["column1", "column2"] : columns.Select(x => x.Name).ToList();
        var quoted = colNames.Select(c => QuoteIdentifier(c, isSqlServer)).ToList();
        var firstKey = columns.FirstOrDefault(x => x.IsPrimaryKey)?.Name ?? colNames.FirstOrDefault() ?? "id";
        return
        [
            new SqlQueryTemplateDto { Title = "SELECT 100 dòng", Description = "Xem nhanh dữ liệu bảng đang chọn", Icon = "fa-table-list", QueryText = selectLimit },
            new SqlQueryTemplateDto { Title = "COUNT", Description = "Đếm record", Icon = "fa-calculator", QueryText = $"SELECT COUNT(1) AS total FROM {table};" },
            new SqlQueryTemplateDto { Title = "INSERT mẫu", Description = "Tạo record mới", Icon = "fa-plus", QueryText = $"INSERT INTO {table} ({string.Join(", ", quoted)}) VALUES ({string.Join(", ", colNames.Select((_, i) => "@v" + i.ToString(CultureInfo.InvariantCulture)))});" },
            new SqlQueryTemplateDto { Title = "UPDATE mẫu", Description = "Cập nhật theo key", Icon = "fa-pen-to-square", QueryText = $"UPDATE {table} SET {QuoteIdentifier(colNames.Last(), isSqlServer)} = @value WHERE {QuoteIdentifier(firstKey, isSqlServer)} = @id;" },
            new SqlQueryTemplateDto { Title = "DELETE mẫu", Description = "Xóa theo key", Icon = "fa-trash-can", QueryText = $"DELETE FROM {table} WHERE {QuoteIdentifier(firstKey, isSqlServer)} = @id;" },
            new SqlQueryTemplateDto { Title = "Thêm cột", Description = "DDL thêm cột nhanh", Icon = "fa-columns-3", QueryText = $"ALTER TABLE {table} ADD {QuoteIdentifier("new_column", isSqlServer)} {(isSqlServer ? "NVARCHAR(255) NULL" : "VARCHAR(255) NULL")};" },
            new SqlQueryTemplateDto { Title = "Thêm index", Description = "Tạo index/key nhanh", Icon = "fa-key", QueryText = $"CREATE INDEX {(isSqlServer ? "[idx_name]" : "`idx_name`")} ON {table} ({QuoteIdentifier(firstKey, isSqlServer)});" },
            new SqlQueryTemplateDto { Title = "Thêm khóa ngoại", Description = "Tạo constraint FK nhanh", Icon = "fa-link", QueryText = $"ALTER TABLE {table} ADD CONSTRAINT {(isSqlServer ? "[fk_name]" : "`fk_name`")} FOREIGN KEY ({QuoteIdentifier(firstKey, isSqlServer)}) REFERENCES {(isSqlServer ? "[dbo].[ref_table]" : "`ref_table`")} ({QuoteIdentifier("id", isSqlServer)}) ON UPDATE NO ACTION ON DELETE NO ACTION;" },
            new SqlQueryTemplateDto { Title = "Xóa toàn bộ record", Description = "Dọn dữ liệu bảng đang chọn", Icon = "fa-broom", QueryText = $"DELETE FROM {table};" }
        ];
    }

    private static string BuildForeignKeyCreateSql(SqlConnectionProfileDto profile, string databaseName, SqlForeignKeyDto fk)
    {
        var isSqlServer = IsSqlServer(profile);
        var table = BuildTableName(profile, databaseName, fk.Schema, fk.TableName);
        var refTable = BuildTableName(profile, databaseName, fk.ReferencedSchema, fk.ReferencedTableName);
        var updateRule = SafeForeignKeyRule(fk.UpdateRule);
        var deleteRule = SafeForeignKeyRule(fk.DeleteRule);
        var sql = new StringBuilder();
        sql.Append("ALTER TABLE ").Append(table)
            .Append(" ADD CONSTRAINT ").Append(QuoteIdentifier(fk.ConstraintName, isSqlServer))
            .Append(" FOREIGN KEY (").Append(QuoteIdentifier(fk.ColumnName, isSqlServer)).Append(")")
            .Append(" REFERENCES ").Append(refTable).Append(" (").Append(QuoteIdentifier(fk.ReferencedColumnName, isSqlServer)).Append(")");
        if (!string.IsNullOrWhiteSpace(updateRule)) sql.Append(" ON UPDATE ").Append(updateRule);
        if (!string.IsNullOrWhiteSpace(deleteRule)) sql.Append(" ON DELETE ").Append(deleteRule);
        return sql.ToString();
    }

    private static string BuildCreateIndexSql(SqlConnectionProfileDto profile, SqlIndexCrudRequest request)
    {
        var isSqlServer = IsSqlServer(profile);
        ValidateIdentifier(request.TableName, "Tên bảng");
        var type = NormalizeIndexType(request.IndexType);
        var columns = request.Columns.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (columns.Count == 0)
        {
            throw new ArgumentException("Key/index phải có ít nhất một cột.");
        }
        foreach (var column in columns)
        {
            ValidateIdentifier(column, "Tên cột index");
        }

        var tableName = BuildTableName(profile, request.DatabaseName, request.Schema, request.TableName);
        var quotedColumns = string.Join(", ", columns.Select(c => QuoteIdentifier(c, isSqlServer)));
        if (type == "PRIMARY")
        {
            var name = string.IsNullOrWhiteSpace(request.Name) ? $"PK_{request.TableName}" : request.Name;
            ValidateIdentifier(name, "Tên primary key");
            return isSqlServer
                ? $"ALTER TABLE {tableName} ADD CONSTRAINT {QuoteIdentifier(name, true)} PRIMARY KEY ({quotedColumns})"
                : $"ALTER TABLE {tableName} ADD PRIMARY KEY ({quotedColumns})";
        }

        var indexName = string.IsNullOrWhiteSpace(request.Name) ? $"idx_{request.TableName}_{columns[0]}" : request.Name;
        ValidateIdentifier(indexName, "Tên index");
        var prefix = type switch
        {
            "UNIQUE" => "CREATE UNIQUE INDEX",
            "FULLTEXT" when !isSqlServer => "CREATE FULLTEXT INDEX",
            "SPATIAL" when !isSqlServer => "CREATE SPATIAL INDEX",
            _ => "CREATE INDEX"
        };
        return $"{prefix} {QuoteIdentifier(indexName, isSqlServer)} ON {tableName} ({quotedColumns})";
    }


    private static string NormalizeMaintenanceAction(string? action)
    {
        var value = string.IsNullOrWhiteSpace(action) ? "check" : action.Trim().ToLowerInvariant();
        return value switch
        {
            "analyze" or "analyse" => "analyze",
            "check" => "check",
            "optimize" or "optimise" => "optimize",
            "repair" => "repair",
            "checksum" => "checksum",
            "truncate" or "empty" => "truncate",
            _ => "check"
        };
    }

    private static string FirstValue(Dictionary<string, string?> row, params string[] names)
    {
        foreach (var name in names)
        {
            if (row.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value!;
            }
        }

        return string.Empty;
    }

    private static string NormalizeIndexType(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "INDEX" : value.Trim().Replace('-', '_').Replace(' ', '_').ToUpperInvariant();
        return text switch
        {
            "PRIMARY" or "PRIMARY_KEY" or "PK" => "PRIMARY",
            "UNIQUE" or "UNIQUE_KEY" => "UNIQUE",
            "FULLTEXT" => "FULLTEXT",
            "SPATIAL" => "SPATIAL",
            _ => "INDEX"
        };
    }

    public static bool IsSoftSqlException(Exception ex)
    {
        ex = UnwrapException(ex);
        return ex is DbException
            or InvalidOperationException
            or TimeoutException
            or ArgumentException
            or IOException;
    }

    public static string ToFriendlyDatabaseError(Exception ex, string? action = null)
    {
        var root = UnwrapException(ex);
        var prefix = string.IsNullOrWhiteSpace(action) ? "Không thực hiện được thao tác SQL" : action.Trim();
        var detail = string.IsNullOrWhiteSpace(root.Message) ? root.GetType().Name : root.Message;

        if (root is MySqlException mysql)
        {
            return mysql.Number switch
            {
                1045 => $"{prefix}: MySQL/MariaDB từ chối đăng nhập. Kiểm tra lại user, password, quyền đăng nhập từ host này và mục Allow no password. Chi tiết: {detail}",
                1044 => $"{prefix}: user không có quyền truy cập database được chọn. Chi tiết: {detail}",
                1049 => $"{prefix}: database không tồn tại hoặc user chưa có quyền thấy database đó. Chi tiết: {detail}",
                2002 or 2003 or 2005 => $"{prefix}: không kết nối được MySQL/MariaDB. Kiểm tra host, port, server đang chạy và firewall. Chi tiết: {detail}",
                2013 or 2055 => $"{prefix}: kết nối MySQL/MariaDB bị ngắt giữa chừng. Hãy thử lại hoặc tăng timeout. Chi tiết: {detail}",
                _ => $"{prefix}: lỗi MySQL/MariaDB. Chi tiết: {detail}"
            };
        }

        if (root is SqlException sql)
        {
            return sql.Number switch
            {
                18456 => $"{prefix}: SQL Server từ chối đăng nhập. Kiểm tra user/password, quyền server/database và chế độ SQL Authentication. Chi tiết: {detail}",
                4060 => $"{prefix}: database SQL Server không mở được hoặc user chưa có quyền. Chi tiết: {detail}",
                53 or -1 => $"{prefix}: không kết nối được SQL Server. Kiểm tra host, port, instance, firewall và server đang chạy. Chi tiết: {detail}",
                _ => $"{prefix}: lỗi SQL Server. Chi tiết: {detail}"
            };
        }

        if (root is TimeoutException)
        {
            return $"{prefix}: quá thời gian chờ kết nối/query. Kiểm tra server SQL hoặc tăng timeout. Chi tiết: {detail}";
        }

        if (root is InvalidOperationException or ArgumentException)
        {
            return $"{prefix}: cấu hình kết nối chưa hợp lệ. Chi tiết: {detail}";
        }

        return $"{prefix}: {detail}";
    }

    private static Exception UnwrapException(Exception ex)
    {
        while (ex.InnerException is not null && ex is not MySqlException && ex is not SqlException)
        {
            ex = ex.InnerException;
        }

        return ex;
    }


    private static async Task<string> ReadMySqlOrMariaDbUptimeAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var status = await ReadRowsAsync(connection, "SHOW GLOBAL STATUS LIKE 'Uptime'", [], 1, cancellationToken);
            if (status.Rows.Count == 0)
            {
                return string.Empty;
            }

            var row = status.Rows[0];
            var raw = Value(row, "Value");
            if (string.IsNullOrWhiteSpace(raw))
            {
                raw = row.Values.Skip(1).FirstOrDefault() ?? row.Values.FirstOrDefault() ?? string.Empty;
            }

            if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                return string.Empty;
            }

            var ts = TimeSpan.FromSeconds(seconds);
            return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        }
        catch
        {
            // Uptime chỉ là thông tin phụ. Không để dashboard server fail vì quyền/status variable không đọc được.
            return string.Empty;
        }
    }

    private static async Task<int> ExecuteNonQueryAsync(DbConnection connection, string sql, List<DbParameter> parameters, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ExecuteScriptStatementsAsync(DbConnection connection, List<string> statements, CancellationToken cancellationToken)
    {
        var result = await ExecuteScriptStatementsAsync(connection, statements, false, cancellationToken);
        return result.AffectedRows;
    }

    private sealed class SqlImportExecutionResult
    {
        public int TotalStatements { get; set; }
        public int AffectedRows { get; set; }
        public int FailedCount { get; set; }
        public List<string> Errors { get; } = [];
    }

    private static async Task<SqlImportExecutionResult> ExecuteScriptStatementsAsync(DbConnection connection, List<string> statements, bool continueOnError, CancellationToken cancellationToken)
    {
        var result = new SqlImportExecutionResult();
        foreach (var statement in statements.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            result.TotalStatements++;
            try
            {
                result.AffectedRows += await ExecuteNonQueryAsync(connection, statement, [], cancellationToken);
            }
            catch (Exception ex) when (continueOnError && IsSoftSqlException(ex))
            {
                result.FailedCount++;
                var preview = statement.Length > 80 ? statement[..80] + "..." : statement;
                result.Errors.Add($"Lệnh {result.TotalStatements}: {preview} => {ToFriendlyDatabaseError(ex, "Import")}");
            }
        }

        return result;
    }

    private static List<string> SplitSqlScript(string script)
    {
        script = script.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var statements = new List<string>();
        var sb = new StringBuilder();
        var delimiter = ";";
        var inSingle = false;
        var inDouble = false;
        var inLineComment = false;
        var inBlockComment = false;
        var atLineStart = true;

        for (var i = 0; i < script.Length; i++)
        {
            if (atLineStart)
            {
                var lineEnd = script.IndexOf('\n', i);
                if (lineEnd < 0) lineEnd = script.Length;
                var line = script[i..lineEnd].Trim();
                if (line.Equals("GO", StringComparison.OrdinalIgnoreCase))
                {
                    FlushStatement(statements, sb);
                    i = lineEnd;
                    atLineStart = true;
                    continue;
                }
                if (line.StartsWith("DELIMITER ", StringComparison.OrdinalIgnoreCase))
                {
                    FlushStatement(statements, sb);
                    delimiter = line[10..].Trim();
                    if (string.IsNullOrWhiteSpace(delimiter)) delimiter = ";";
                    i = lineEnd;
                    atLineStart = true;
                    continue;
                }
            }

            var ch = script[i];
            var next = i + 1 < script.Length ? script[i + 1] : '\0';

            if (inLineComment)
            {
                sb.Append(ch);
                if (ch == '\n')
                {
                    inLineComment = false;
                    atLineStart = true;
                }
                continue;
            }

            if (inBlockComment)
            {
                sb.Append(ch);
                if (ch == '*' && next == '/')
                {
                    sb.Append(next);
                    i++;
                    inBlockComment = false;
                    atLineStart = false;
                }
                continue;
            }

            if (!inSingle && !inDouble && ch == '-' && next == '-')
            {
                inLineComment = true;
                sb.Append(ch).Append(next);
                i++;
                atLineStart = false;
                continue;
            }
            if (!inSingle && !inDouble && ch == '/' && next == '*')
            {
                inBlockComment = true;
                sb.Append(ch).Append(next);
                i++;
                atLineStart = false;
                continue;
            }

            if (ch == '\'' && !inDouble)
            {
                sb.Append(ch);
                if (next == '\'' || next == '\\')
                {
                    if (next == '\\' && i + 2 < script.Length)
                    {
                        sb.Append(next).Append(script[i + 2]);
                        i += 2;
                    }
                    else
                    {
                        sb.Append(next);
                        i++;
                    }
                }
                else
                {
                    inSingle = !inSingle;
                }
                atLineStart = false;
                continue;
            }
            if (ch == '"' && !inSingle)
            {
                sb.Append(ch);
                if (next == '"' || next == '\\')
                {
                    if (next == '\\' && i + 2 < script.Length)
                    {
                        sb.Append(next).Append(script[i + 2]);
                        i += 2;
                    }
                    else
                    {
                        sb.Append(next);
                        i++;
                    }
                }
                else
                {
                    inDouble = !inDouble;
                }
                atLineStart = false;
                continue;
            }

            if (!inSingle && !inDouble && !string.IsNullOrEmpty(delimiter) && MatchesAt(script, i, delimiter))
            {
                FlushStatement(statements, sb);
                i += delimiter.Length - 1;
                atLineStart = false;
                continue;
            }

            sb.Append(ch);
            atLineStart = ch == '\n';
        }

        FlushStatement(statements, sb);
        return statements;

    }

    private static bool MatchesAt(string text, int index, string value)
        => index >= 0 && index + value.Length <= text.Length && string.Compare(text, index, value, 0, value.Length, StringComparison.Ordinal) == 0;

    private static void FlushStatement(List<string> statements, StringBuilder sb)
    {
        var sql = sb.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(sql))
        {
            statements.Add(sql);
        }
        sb.Clear();
    }

    private static string BuildColumnPositionSql(SqlColumnEditDto column, bool isSqlServer)
    {
        if (isSqlServer)
        {
            return string.Empty;
        }

        var mode = string.IsNullOrWhiteSpace(column.PositionMode) ? "end" : column.PositionMode.Trim().ToLowerInvariant();
        if (mode == "first")
        {
            return " FIRST";
        }

        if (mode == "after" && !string.IsNullOrWhiteSpace(column.AfterColumn))
        {
            ValidateIdentifier(column.AfterColumn, "Cột đứng trước");
            return " AFTER " + QuoteIdentifier(column.AfterColumn, false);
        }

        return string.Empty;
    }

    private static string BuildColumnDefinition(SqlColumnEditDto column, bool isSqlServer, bool includePrimaryInline)
    {
        var sql = new StringBuilder();
        sql.Append(QuoteIdentifier(column.Name, isSqlServer));
        sql.Append(' ');
        sql.Append(BuildColumnType(column, isSqlServer));
        if (column.AutoIncrement)
        {
            sql.Append(isSqlServer ? " IDENTITY(1,1)" : " AUTO_INCREMENT");
        }
        sql.Append(column.IsNullable ? " NULL" : " NOT NULL");
        if (!string.IsNullOrWhiteSpace(column.DefaultValue))
        {
            sql.Append(" DEFAULT ");
            sql.Append(ToDefaultSql(column.DefaultValue));
        }
        if (includePrimaryInline && column.IsPrimaryKey)
        {
            sql.Append(" PRIMARY KEY");
        }

        return sql.ToString();
    }

    private static string BuildColumnType(SqlColumnEditDto column, bool isSqlServer)
    {
        var type = string.IsNullOrWhiteSpace(column.DataType)
            ? (isSqlServer ? "nvarchar" : "varchar")
            : SafeSqlType(column.DataType);
        var upper = type.ToUpperInvariant();
        if (type.Contains('('))
        {
            return type;
        }

        var length = SafeLength(column.Length);
        if ((upper.Contains("CHAR", StringComparison.Ordinal) || upper is "VARCHAR" or "NVARCHAR" or "VARBINARY") && string.IsNullOrWhiteSpace(length))
        {
            length = isSqlServer && upper.Contains("TEXT", StringComparison.Ordinal) ? string.Empty : "255";
        }

        if (upper is "ENUM" or "SET")
        {
            var enumSetValues = SafeEnumSetValues(column.Length);
            return string.IsNullOrWhiteSpace(enumSetValues) ? type : $"{type}({enumSetValues})";
        }

        if (!string.IsNullOrWhiteSpace(length) && (upper.Contains("CHAR", StringComparison.Ordinal) || upper is "VARCHAR" or "NVARCHAR" or "VARBINARY" or "BINARY" or "DECIMAL" or "NUMERIC" or "FLOAT" or "DOUBLE" or "TIME" or "DATETIME2" or "DATETIMEOFFSET"))
        {
            return $"{type}({length})";
        }

        if (isSqlServer && upper == "TEXT")
        {
            return "NVARCHAR(MAX)";
        }

        return type;
    }

    private static string BuildRecordWhere(DbConnection connection, List<SqlColumnDto> columns, Dictionary<string, string?> originalValues, List<string> keyColumns, bool isSqlServer, List<DbParameter> parameters)
    {
        var keys = keyColumns.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (keys.Count == 0)
        {
            keys = columns.Where(x => x.IsPrimaryKey).Select(x => x.Name).ToList();
        }
        if (keys.Count == 0)
        {
            keys = originalValues.Keys.Where(x => columns.Any(c => string.Equals(c.Name, x, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        var parts = new List<string>();
        var index = 0;
        foreach (var key in keys)
        {
            var column = columns.FirstOrDefault(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase));
            if (column is null || !originalValues.TryGetValue(column.Name, out var value))
            {
                continue;
            }

            if (value is null)
            {
                parts.Add($"{QuoteIdentifier(column.Name, isSqlServer)} IS NULL");
            }
            else
            {
                var parameterName = "@where" + index.ToString(CultureInfo.InvariantCulture);
                parts.Add($"{QuoteIdentifier(column.Name, isSqlServer)} = {parameterName}");
                parameters.Add(CreateParameter(connection, parameterName, NormalizeSqlRecordValue(column, value, isSqlServer)));
                index++;
            }
        }

        return string.Join(" AND ", parts);
    }

    private static object? NormalizeSqlValue(string? value)
    {
        if (value is null)
        {
            return DBNull.Value;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "<NULL>", StringComparison.OrdinalIgnoreCase) || string.Equals(trimmed, "NULL", StringComparison.OrdinalIgnoreCase))
        {
            return DBNull.Value;
        }

        return trimmed;
    }

    private static object? NormalizeSqlRecordValue(SqlColumnDto column, string? value, bool isSqlServer)
    {
        var normalized = NormalizeSqlValue(value);
        if (normalized is DBNull || normalized is null)
        {
            return normalized ?? DBNull.Value;
        }

        var text = Convert.ToString(normalized, CultureInfo.InvariantCulture)?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return text ?? string.Empty;
        }

        var dataType = GetSqlDataTypeName(column);
        if (IsSqlServerRowVersion(dataType, isSqlServer))
        {
            return text;
        }

        if (IsSqlDateType(dataType) && TryNormalizeSqlDate(text, out var dateText))
        {
            return dateText;
        }

        if (IsSqlTimeType(dataType) && TryNormalizeSqlTime(text, out var timeText))
        {
            return timeText;
        }

        if (IsSqlDateTimeOffsetType(dataType) && TryNormalizeSqlDateTimeOffset(text, isSqlServer, out var dateTimeOffsetText))
        {
            return dateTimeOffsetText;
        }

        if (IsSqlDateTimeType(dataType, isSqlServer) && TryNormalizeSqlDateTime(text, dataType, isSqlServer, out var dateTimeText))
        {
            return dateTimeText;
        }

        return text;
    }

    private static string GetSqlDataTypeName(SqlColumnDto column)
    {
        var dataType = string.IsNullOrWhiteSpace(column.DataType) ? column.FullDataType : column.DataType;
        dataType = (dataType ?? string.Empty).Trim().ToLowerInvariant();
        var paren = dataType.IndexOf('(', StringComparison.Ordinal);
        if (paren > 0)
        {
            dataType = dataType[..paren];
        }

        return dataType.Trim();
    }

    private static bool IsSqlServerRowVersion(string dataType, bool isSqlServer)
        => isSqlServer && (dataType is "timestamp" or "rowversion");

    private static bool IsSqlDateType(string dataType)
        => dataType is "date";

    private static bool IsSqlTimeType(string dataType)
        => dataType is "time";

    private static bool IsSqlDateTimeOffsetType(string dataType)
        => dataType is "datetimeoffset" or "timestamptz";

    private static bool IsSqlDateTimeType(string dataType, bool isSqlServer)
    {
        if (IsSqlServerRowVersion(dataType, isSqlServer))
        {
            return false;
        }

        return dataType is "datetime" or "datetime2" or "smalldatetime" or "timestamp"
            || dataType.Contains("datetime", StringComparison.Ordinal)
            || dataType.StartsWith("timestamp", StringComparison.Ordinal);
    }

    private static bool TryNormalizeSqlDate(string text, out string formatted)
    {
        formatted = string.Empty;
        if (!TryParseSqlDateTime(text, out var dateTime))
        {
            return false;
        }

        formatted = dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryNormalizeSqlTime(string text, out string formatted)
    {
        formatted = string.Empty;
        var normalized = text.Trim();
        if (TimeSpan.TryParse(normalized, CultureInfo.InvariantCulture, out var timeSpan)
            || TimeSpan.TryParse(normalized, CultureInfo.GetCultureInfo("vi-VN"), out timeSpan))
        {
            formatted = FormatSqlTime(timeSpan);
            return true;
        }

        if (DateTime.TryParse(normalized.Replace('T', ' '), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dateTime)
            || DateTime.TryParse(normalized.Replace('T', ' '), CultureInfo.GetCultureInfo("vi-VN"), DateTimeStyles.AllowWhiteSpaces, out dateTime))
        {
            formatted = dateTime.ToString(dateTime.Millisecond == 0 && dateTime.Ticks % TimeSpan.TicksPerSecond == 0 ? "HH:mm:ss" : "HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
            return true;
        }

        return false;
    }

    private static bool TryNormalizeSqlDateTime(string text, string dataType, bool isSqlServer, out string formatted)
    {
        formatted = string.Empty;
        if (!TryParseSqlDateTime(text, out var dateTime))
        {
            return false;
        }

        formatted = FormatSqlDateTime(dateTime, dataType, isSqlServer);
        return true;
    }

    private static bool TryNormalizeSqlDateTimeOffset(string text, bool isSqlServer, out string formatted)
    {
        formatted = string.Empty;
        var normalized = text.Trim().Replace('T', ' ');
        var formats = new[]
        {
            "yyyy-MM-dd HH:mm:ss zzz",
            "yyyy-MM-dd HH:mm:ss.FFFFFFF zzz",
            "yyyy-MM-ddTHH:mm:sszzz",
            "yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz",
            "yyyy-MM-dd HH:mm:ssK",
            "yyyy-MM-dd HH:mm:ss.FFFFFFFK",
            "o"
        };

        if (!DateTimeOffset.TryParseExact(normalized, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dto)
            && !DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out dto)
            && !DateTimeOffset.TryParse(normalized, CultureInfo.GetCultureInfo("vi-VN"), DateTimeStyles.AllowWhiteSpaces, out dto))
        {
            if (!TryParseSqlDateTime(normalized, out var dateTime))
            {
                return false;
            }

            dto = new DateTimeOffset(dateTime, TimeSpan.Zero);
        }

        var baseText = FormatSqlDateTime(dto.DateTime, "datetimeoffset", isSqlServer);
        formatted = baseText + " " + dto.ToString("zzz", CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryParseSqlDateTime(string text, out DateTime dateTime)
    {
        var normalized = text.Trim().Replace('T', ' ');
        var formats = new[]
        {
            "yyyy-MM-dd",
            "yyyy/MM/dd",
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss.FFFFFFF",
            "yyyy/MM/dd HH:mm",
            "yyyy/MM/dd HH:mm:ss",
            "yyyy/MM/dd HH:mm:ss.FFFFFFF",
            "dd/MM/yyyy",
            "d/M/yyyy",
            "dd/MM/yyyy HH:mm",
            "d/M/yyyy H:mm",
            "dd/MM/yyyy HH:mm:ss",
            "d/M/yyyy H:mm:ss",
            "MM/dd/yyyy",
            "M/d/yyyy",
            "MM/dd/yyyy HH:mm",
            "M/d/yyyy H:mm",
            "MM/dd/yyyy HH:mm:ss",
            "M/d/yyyy H:mm:ss"
        };

        return DateTime.TryParseExact(normalized, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out dateTime)
            || DateTime.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out dateTime)
            || DateTime.TryParse(normalized, CultureInfo.GetCultureInfo("vi-VN"), DateTimeStyles.AllowWhiteSpaces, out dateTime);
    }

    private static string FormatSqlDateTime(DateTime dateTime, string dataType, bool isSqlServer)
    {
        if (dataType == "smalldatetime")
        {
            return dateTime.ToString("yyyy-MM-dd HH:mm:00", CultureInfo.InvariantCulture);
        }

        var baseText = dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var fractionTicks = dateTime.Ticks % TimeSpan.TicksPerSecond;
        if (fractionTicks == 0)
        {
            return baseText;
        }

        var maxDigits = isSqlServer
            ? dataType is "datetime" ? 3 : 7
            : 6;
        var fraction = fractionTicks.ToString("D7", CultureInfo.InvariantCulture)[..maxDigits].TrimEnd('0');
        return string.IsNullOrEmpty(fraction) ? baseText : baseText + "." + fraction;
    }

    private static string FormatSqlTime(TimeSpan timeSpan)
    {
        var baseText = timeSpan.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        var fractionTicks = timeSpan.Ticks % TimeSpan.TicksPerSecond;
        if (fractionTicks == 0)
        {
            return baseText;
        }

        var fraction = fractionTicks.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
        return string.IsNullOrEmpty(fraction) ? baseText : baseText + "." + fraction;
    }

    private static string ToDefaultSql(string value)
    {
        var trimmed = value.Trim();
        if (string.Equals(trimmed, "NULL", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(")", StringComparison.Ordinal)
            || decimal.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
        {
            return trimmed;
        }

        return ToSqlLiteral(trimmed);
    }

    private static string ToSqlLiteral(string? value)
    {
        if (value is null)
        {
            return "NULL";
        }

        return "'" + EscapeSingleQuotedSql(value) + "'";
    }

    private static string EscapeSingleQuotedSql(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static string SafeSqlWord(string? value, string fallback)
    {
        value = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return value.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-') ? value : fallback;
    }

    private static string SafeSqlType(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            return "varchar";
        }

        var allowed = value.Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '(' or ')' or ',' or ' ').ToArray();
        var safe = new string(allowed).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "varchar" : safe;
    }

    private static string SafeForeignKeyRule(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "NO ACTION";
        }

        var normalized = value.Trim().Replace('_', ' ').Replace('-', ' ').ToUpperInvariant();
        return normalized switch
        {
            "CASCADE" => "CASCADE",
            "SET NULL" => "SET NULL",
            "SET DEFAULT" => "SET DEFAULT",
            "RESTRICT" => "RESTRICT",
            "NO ACTION" => "NO ACTION",
            _ => "NO ACTION"
        };
    }

    private static string SafeLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var safe = new string(value.Trim().Where(ch => char.IsDigit(ch) || ch == ',').ToArray());
        return safe.Length > 0 ? safe : string.Empty;
    }

    private static string SafeEnumSetValues(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var safe = new string(value.Trim().Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == ' ' || ch == ',' || ch == '\'' || ch == '"').ToArray()).Trim();
        return safe.Length > 0 ? safe : string.Empty;
    }

    private static void ValidateIdentifier(string? identifier, string label)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException(label + " không được để trống.");
        }

        foreach (var ch in identifier.Trim())
        {
            if (!char.IsLetterOrDigit(ch) && ch is not '_' and not '$' and not '-')
            {
                throw new ArgumentException(label + " chỉ nên gồm chữ, số, _, -, $. Giá trị hiện tại: " + identifier);
            }
        }
    }

    private async Task OpenThrottledAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await _dbThrottle.WaitAsync(cancellationToken);
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        finally
        {
            _dbThrottle.Release();
        }
    }

    private static DbConnection CreateConnection(SqlConnectionProfileDto profile, string? databaseName = null)
    {
        if (!profile.AllowNoPassword && string.IsNullOrEmpty(profile.Password))
        {
            throw new InvalidOperationException("Kết nối này không cho phép password rỗng.");
        }

        if (IsSqlServer(profile))
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = string.IsNullOrWhiteSpace(profile.Port) || profile.Port == "1433" ? profile.Host : profile.Host + "," + profile.Port,
                UserID = profile.User,
                Password = profile.Password,
                InitialCatalog = string.IsNullOrWhiteSpace(databaseName) ? (profile.Database ?? "master") : databaseName,
                ConnectTimeout = Math.Clamp(profile.TimeoutSeconds <= 0 ? 15 : profile.TimeoutSeconds, 3, 120)
            };
            builder["Encrypt"] = profile.Encrypt;
            builder["TrustServerCertificate"] = profile.TrustServerCertificate;
            return new SqlConnection(builder.ConnectionString);
        }

        var mysqlBuilder = new MySqlConnectionStringBuilder
        {
            Server = profile.Host,
            UserID = profile.User,
            Password = profile.Password,
            Database = string.IsNullOrWhiteSpace(databaseName) ? profile.Database : databaseName,
            ConnectionTimeout = (uint)Math.Clamp(profile.TimeoutSeconds <= 0 ? 15 : profile.TimeoutSeconds, 3, 120),
            AllowUserVariables = true,
            TreatTinyAsBoolean = false,
            ConvertZeroDateTime = true,
            AllowLoadLocalInfile = true
        };
        if (uint.TryParse(profile.Port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0)
        {
            mysqlBuilder.Port = port;
        }

        return new MySqlConnection(mysqlBuilder.ConnectionString);
    }

    private static bool IsSqlServer(SqlConnectionProfileDto profile)
        => SqlConnectConfigService.NormalizeType(profile.TypeConnect) == "sqlserver";

    private static string BuildSearchWhere(DbConnection connection, List<SqlColumnDto> columns, string? search, bool isSqlServer, List<DbParameter> parameters)
    {
        if (string.IsNullOrWhiteSpace(search) || columns.Count == 0)
        {
            return string.Empty;
        }

        parameters.Add(CreateParameter(connection, "@search", "%" + search.Trim() + "%"));
        var expressions = columns.Select(column => isSqlServer
            ? $"CAST({QuoteIdentifier(column.Name, isSqlServer)} AS NVARCHAR(MAX)) LIKE @search"
            : $"CAST({QuoteIdentifier(column.Name, isSqlServer)} AS CHAR) LIKE @search");
        return " WHERE " + string.Join(" OR ", expressions);
    }

    private static string BuildTableName(SqlConnectionProfileDto profile, string databaseName, string schema, string tableName)
    {
        var isSqlServer = IsSqlServer(profile);
        if (isSqlServer)
        {
            return $"{QuoteIdentifier(string.IsNullOrWhiteSpace(schema) ? "dbo" : schema, true)}.{QuoteIdentifier(tableName, true)}";
        }

        return $"{QuoteIdentifier(databaseName, false)}.{QuoteIdentifier(tableName, false)}";
    }

    private static string QuoteIdentifier(string identifier, bool isSqlServer)
    {
        identifier = identifier.Trim();
        return isSqlServer
            ? "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]"
            : "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";
    }

    private static async Task<SqlQueryResultDto> ReadRowsAsync(DbConnection connection, string query, List<DbParameter> parameters, int maxRows, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        return await ReadRowsAsync(command, maxRows, cancellationToken);
    }

    private static async Task<SqlQueryResultDto> ReadRowsAsync(DbCommand command, int maxRows, CancellationToken cancellationToken)
    {
        var result = new SqlQueryResultDto();
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            result.Columns.Add(new SqlColumnDto
            {
                Name = reader.GetName(i),
                DataType = reader.GetDataTypeName(i),
                Ordinal = i + 1
            });
        }

        var rowCount = 0;
        while (await reader.ReadAsync(cancellationToken) && rowCount < maxRows)
        {
            var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                row[name] = await ReadValueAsStringAsync(reader, i, cancellationToken);
            }

            result.Rows.Add(row);
            rowCount++;
        }

        return result;
    }

    private static async Task<string?> ReadValueAsStringAsync(DbDataReader reader, int ordinal, CancellationToken cancellationToken)
    {
        if (await reader.IsDBNullAsync(ordinal, cancellationToken))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        if (value is byte[] bytes)
        {
            return Convert.ToBase64String(bytes);
        }

        if (value is DateTime dateTime)
        {
            var dataTypeName = reader.GetDataTypeName(ordinal).ToLowerInvariant();
            if (dataTypeName == "date")
            {
                return dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            if (dateTime.Ticks % TimeSpan.TicksPerSecond == 0)
            {
                return dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }

            return dateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        }

#if NET6_0_OR_GREATER
        if (value is DateOnly dateOnly)
        {
            return dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (value is TimeOnly timeOnly)
        {
            return timeOnly.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }
#endif

        if (value is TimeSpan timeSpan)
        {
            return timeSpan.ToString(timeSpan.Milliseconds == 0 ? @"hh\:mm\:ss" : @"hh\:mm\:ss\.FFFFFF", CultureInfo.InvariantCulture);
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static DbParameter CreateParameter(DbConnection connection, string name, object? value)
    {
        var parameter = connection.CreateCommand().CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        return parameter;
    }

    private static DbParameter CloneParameter(DbParameter source)
    {
        DbParameter clone = source switch
        {
            SqlParameter => new SqlParameter(),
            MySqlParameter => new MySqlParameter(),
            _ => throw new InvalidOperationException("Không hỗ trợ clone parameter này.")
        };
        clone.ParameterName = source.ParameterName;
        clone.Value = source.Value;
        clone.DbType = source.DbType;
        return clone;
    }

    private static string Value(Dictionary<string, string?> row, string name)
        => row.TryGetValue(name, out var value) ? value ?? string.Empty : string.Empty;

    private static bool LooksLikeReaderQuery(string sql)
    {
        var text = sql.TrimStart();
        var upper = text.Length > 32 ? text[..32].ToUpperInvariant() : text.ToUpperInvariant();
        return upper.StartsWith("SELECT", StringComparison.Ordinal)
               || upper.StartsWith("SHOW", StringComparison.Ordinal)
               || upper.StartsWith("WITH", StringComparison.Ordinal)
               || upper.StartsWith("DESCRIBE", StringComparison.Ordinal)
               || upper.StartsWith("DESC", StringComparison.Ordinal);
    }
}
