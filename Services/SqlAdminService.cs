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

    public async Task<List<SqlDatabaseDto>> ListDatabasesAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var profile = await _configService.GetProfileOrThrowAsync(profileId, cancellationToken);
        await using var connection = CreateConnection(profile);
        await OpenThrottledAsync(connection, cancellationToken);

        var isSqlServer = IsSqlServer(profile);
        const string sqlServerQuery = "SELECT name FROM sys.databases WHERE state = 0 ORDER BY name";
        const string mysqlQuery = "SHOW DATABASES";
        var rows = await ReadRowsAsync(connection, isSqlServer ? sqlServerQuery : mysqlQuery, [], 5000, cancellationToken);
        return rows.Rows
            .Select(row => row.Values.FirstOrDefault() ?? string.Empty)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(x => new SqlDatabaseDto { Name = x })
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
SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_TYPE IN ('BASE TABLE','VIEW')
""";
            if (!string.IsNullOrWhiteSpace(search))
            {
                query += " AND (TABLE_NAME LIKE @search OR TABLE_SCHEMA LIKE @search)";
                parameters.Add(CreateParameter(connection, "@search", "%" + search.Trim() + "%"));
            }

            query += " ORDER BY TABLE_SCHEMA, TABLE_NAME";
        }
        else
        {
            query = """
SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE
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
            Type = Value(row, "TABLE_TYPE")
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
SELECT c.COLUMN_NAME, c.DATA_TYPE, c.IS_NULLABLE, c.ORDINAL_POSITION,
       CASE WHEN k.COLUMN_NAME IS NULL THEN 0 ELSE 1 END AS IS_PRIMARY
FROM INFORMATION_SCHEMA.COLUMNS c
LEFT JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
  ON c.TABLE_SCHEMA = k.TABLE_SCHEMA AND c.TABLE_NAME = k.TABLE_NAME AND c.COLUMN_NAME = k.COLUMN_NAME
LEFT JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
  ON k.CONSTRAINT_NAME = tc.CONSTRAINT_NAME AND k.TABLE_SCHEMA = tc.TABLE_SCHEMA AND k.TABLE_NAME = tc.TABLE_NAME AND tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @table
ORDER BY c.ORDINAL_POSITION
""";
            parameters.Add(CreateParameter(connection, "@schema", string.IsNullOrWhiteSpace(schema) ? "dbo" : schema));
            parameters.Add(CreateParameter(connection, "@table", tableName));
        }
        else
        {
            query = """
SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, ORDINAL_POSITION,
       CASE WHEN COLUMN_KEY = 'PRI' THEN 1 ELSE 0 END AS IS_PRIMARY
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @table
ORDER BY ORDINAL_POSITION
""";
            parameters.Add(CreateParameter(connection, "@db", databaseName));
            parameters.Add(CreateParameter(connection, "@table", tableName));
        }

        var result = await ReadRowsAsync(connection, query, parameters, 5000, cancellationToken);
        return result.Rows.Select(row => new SqlColumnDto
        {
            Name = Value(row, "COLUMN_NAME"),
            DataType = Value(row, "DATA_TYPE"),
            IsNullable = string.Equals(Value(row, "IS_NULLABLE"), "YES", StringComparison.OrdinalIgnoreCase),
            IsPrimaryKey = Value(row, "IS_PRIMARY") is "1" or "True" or "true",
            Ordinal = int.TryParse(Value(row, "ORDINAL_POSITION"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal) ? ordinal : 0
        }).ToList();
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
            var sql = $"ALTER TABLE {BuildTableName(profile, request.DatabaseName, request.Schema, request.TableName)} ADD {BuildColumnDefinition(request.Column, isSqlServer, includePrimaryInline: false)}";
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
                sql = $"ALTER TABLE {table} CHANGE COLUMN {QuoteIdentifier(request.Column.OriginalName, false)} {BuildColumnDefinition(request.Column, false, includePrimaryInline: false)}";
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
                parameters.Add(CreateParameter(connection, parameterName, NormalizeSqlValue(request.Values[column.Name])));
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
                parameters.Add(CreateParameter(connection, parameterName, NormalizeSqlValue(request.Values[column.Name])));
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
            await using var connection = CreateConnection(profile, request.DatabaseName);
            await OpenThrottledAsync(connection, cancellationToken);
            var statements = SplitSqlScript(request.ScriptText);
            var affected = await ExecuteScriptStatementsAsync(connection, statements, cancellationToken);
            return new SqlQueryResultDto { Success = true, AffectedRows = affected, Message = $"Import xong {statements.Count} lệnh. Affected rows: {affected}." };
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
            List<SqlTableDto> tables = string.IsNullOrWhiteSpace(request.TableName)
                ? await ListTablesAsync(request.ProfileId, request.DatabaseName, null, cancellationToken)
                : [new SqlTableDto { Schema = request.Schema, Name = request.TableName, FullName = request.TableName, Type = "BASE TABLE" }];
            var sb = new StringBuilder();
            sb.AppendLine("-- Export generated by ConfigTool");
            sb.AppendLine($"-- Database: {request.DatabaseName}");
            sb.AppendLine($"-- At: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            sb.AppendLine();
            if (!isSqlServer)
            {
                sb.AppendLine($"CREATE DATABASE IF NOT EXISTS {QuoteIdentifier(request.DatabaseName, false)};");
                sb.AppendLine($"USE {QuoteIdentifier(request.DatabaseName, false)};");
                sb.AppendLine();
            }

            foreach (var table in tables.Where(t => string.Equals(t.Type, "BASE TABLE", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(t.Type)))
            {
                var columns = await ListColumnsAsync(request.ProfileId, request.DatabaseName, table.Schema, table.Name, cancellationToken);
                if (request.IncludeStructure)
                {
                    sb.AppendLine($"-- Structure for {table.Name}");
                    sb.AppendLine($"DROP TABLE {(isSqlServer ? string.Empty : "IF EXISTS ")}{BuildTableName(profile, request.DatabaseName, table.Schema, table.Name)};");
                    var columnDefs = columns.Select(c => BuildColumnDefinition(new SqlColumnEditDto
                    {
                        Name = c.Name,
                        DataType = c.DataType,
                        IsNullable = c.IsNullable,
                        IsPrimaryKey = c.IsPrimaryKey
                    }, isSqlServer, includePrimaryInline: columns.Count(x => x.IsPrimaryKey) <= 1)).ToList();
                    var primaryColumns = columns.Where(c => c.IsPrimaryKey).Select(c => QuoteIdentifier(c.Name, isSqlServer)).ToList();
                    if (primaryColumns.Count > 1)
                    {
                        columnDefs.Add($"PRIMARY KEY ({string.Join(", ", primaryColumns)})");
                    }
                    sb.AppendLine($"CREATE TABLE {BuildTableName(profile, request.DatabaseName, table.Schema, table.Name)} (\n  {string.Join(",\n  ", columnDefs)}\n);");
                    sb.AppendLine();
                }

                if (request.IncludeData)
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
                    sb.AppendLine($"-- Data for {table.Name}");
                    foreach (var row in page.Rows)
                    {
                        var names = columns.Select(c => QuoteIdentifier(c.Name, isSqlServer)).ToList();
                        var values = columns.Select(c => ToSqlLiteral(row.TryGetValue(c.Name, out var value) ? value : null)).ToList();
                        sb.AppendLine($"INSERT INTO {BuildTableName(profile, request.DatabaseName, table.Schema, table.Name)} ({string.Join(", ", names)}) VALUES ({string.Join(", ", values)});");
                    }
                    sb.AppendLine();
                }
            }

            return new SqlQueryResultDto
            {
                Success = true,
                Message = $"Đã tạo script export {tables.Count} bảng.",
                Rows = [new Dictionary<string, string?> { ["script"] = sb.ToString() }],
                Columns = [new SqlColumnDto { Name = "script", DataType = "sql", Ordinal = 1 }]
            };
        }
        catch (Exception ex) when (IsSoftSqlException(ex))
        {
            return new SqlQueryResultDto { Success = false, Message = ToFriendlyDatabaseError(ex, "Không export được SQL") };
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
            new SqlQueryTemplateDto { Title = "Thêm khóa ngoại", Description = "Tạo constraint FK nhanh", Icon = "fa-link", QueryText = $"ALTER TABLE {table} ADD CONSTRAINT {(isSqlServer ? "[fk_name]" : "`fk_name`")} FOREIGN KEY ({QuoteIdentifier(firstKey, isSqlServer)}) REFERENCES {(isSqlServer ? "[dbo].[ref_table]" : "`ref_table`")} ({QuoteIdentifier("id", isSqlServer)}) ON UPDATE NO ACTION ON DELETE NO ACTION;" },
            new SqlQueryTemplateDto { Title = "Xóa toàn bộ record", Description = "Dọn dữ liệu bảng đang chọn", Icon = "fa-broom", QueryText = $"DELETE FROM {table};" }
        ];
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
        var affected = 0;
        foreach (var statement in statements.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            affected += await ExecuteNonQueryAsync(connection, statement, [], cancellationToken);
        }

        return affected;
    }

    private static List<string> SplitSqlScript(string script)
    {
        script = script.Replace("\r\nGO\r\n", ";\n", StringComparison.OrdinalIgnoreCase)
            .Replace("\nGO\n", ";\n", StringComparison.OrdinalIgnoreCase);
        var statements = new List<string>();
        var sb = new StringBuilder();
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < script.Length; i++)
        {
            var ch = script[i];
            if (ch == '\'' && !inDouble)
            {
                inSingle = !inSingle;
            }
            else if (ch == '"' && !inSingle)
            {
                inDouble = !inDouble;
            }

            if (ch == ';' && !inSingle && !inDouble)
            {
                var sql = sb.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(sql))
                {
                    statements.Add(sql);
                }

                sb.Clear();
            }
            else
            {
                sb.Append(ch);
            }
        }

        var last = sb.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(last))
        {
            statements.Add(last);
        }

        return statements;
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
                parameters.Add(CreateParameter(connection, parameterName, NormalizeSqlValue(value)));
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

        return value;
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
