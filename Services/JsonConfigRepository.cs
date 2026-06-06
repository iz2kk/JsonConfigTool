using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ConfigTool.Models;

namespace ConfigTool.Services;

public sealed class JsonConfigRepository
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<List<JsonConfigFileDto>> ScanFilesAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(folderPath))
        {
            return [];
        }

        var files = Directory.EnumerateFiles(folderPath, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var idFields = await LoadIdFieldMapAsync(folderPath, cancellationToken);
        var results = new ConcurrentBag<JsonConfigFileDto>();

        await Parallel.ForEachAsync(files, cancellationToken, async (file, ct) =>
        {
            var info = new FileInfo(file);
            var dto = new JsonConfigFileDto
            {
                FileName = info.Name,
                FullPath = info.FullName,
                SizeBytes = info.Exists ? info.Length : 0,
                LastWriteTime = info.Exists ? info.LastWriteTime : DateTime.MinValue,
                Status = "OK"
            };

            try
            {
                dto.FileVersion = await GetFileVersionAsync(file, ct);
                var root = await LoadRootAsync(file, ct);
                dto.RootKind = GetNodeKind(root);
                dto.TableCount = DiscoverTables(root, idFields, info.Name).Count;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                dto.RootKind = "invalid";
                dto.Status = ex.Message;
            }

            results.Add(dto);
        });

        return results.OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<List<JsonTableDto>> GetTablesAsync(string folderPath, string fileName, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveJsonFile(folderPath, fileName);
        var fileLock = GetFileLock(fullPath);
        await fileLock.WaitAsync(cancellationToken);
        try
        {
            var root = await LoadRootAsync(fullPath, cancellationToken);
            var idFields = await LoadIdFieldMapAsync(folderPath, cancellationToken);
            return DiscoverTables(root, idFields, Path.GetFileName(fullPath));
        }
        finally
        {
            fileLock.Release();
        }
    }

    public Task<JsonRowPageDto> QueryRowsAsync(
        string folderPath,
        string fileName,
        string tableName,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
        => QueryRowsAsync(folderPath, fileName, tableName, search, page, pageSize, null, cancellationToken);

    public async Task<JsonRowPageDto> QueryRowsAsync(
        string folderPath,
        string fileName,
        string tableName,
        string? search,
        int page,
        int pageSize,
        IReadOnlyCollection<JsonQueryFilterDto>? filters,
        CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveJsonFile(folderPath, fileName);
        var fileLock = GetFileLock(fullPath);
        await fileLock.WaitAsync(cancellationToken);
        try
        {
            var root = await LoadRootAsync(fullPath, cancellationToken);
            var idFields = await LoadIdFieldMapAsync(folderPath, cancellationToken);
            var table = DiscoverTables(root, idFields, Path.GetFileName(fullPath))
                .FirstOrDefault(x => string.Equals(x.Name, tableName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Không tìm thấy bảng {tableName} trong {fileName}.");

            var rows = BuildRows(root, table).ToList();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var keyword = search.Trim();
                rows = rows.Where(row => RowMatches(row, keyword)).ToList();
            }

            var activeFilters = filters?
                .Where(x => !string.IsNullOrWhiteSpace(x.FieldName)
                            || string.Equals(x.Operator, "empty", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(x.Operator, "not-empty", StringComparison.OrdinalIgnoreCase)
                            || !string.IsNullOrWhiteSpace(x.Value))
                .ToList() ?? [];

            if (activeFilters.Count > 0)
            {
                rows = rows.Where(row => activeFilters.All(filter => RowMatchesFilter(row, filter))).ToList();
            }

            pageSize = Math.Clamp(pageSize, 5, 200);
            var totalRows = rows.Count;
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalRows / (double)pageSize));
            page = Math.Clamp(page <= 0 ? 1 : page, 1, totalPages);

            return new JsonRowPageDto
            {
                FileName = fileName,
                TableName = tableName,
                FileVersion = await GetFileVersionAsync(fullPath, cancellationToken),
                Page = page,
                PageSize = pageSize,
                TotalRows = totalRows,
                TotalPages = totalPages,
                Fields = table.Fields,
                Rows = rows.Skip((page - 1) * pageSize).Take(pageSize).ToList()
            };
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task<JsonCrudResultDto> CreateRowAsync(string folderPath, JsonRowWriteRequest request, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveJsonFile(folderPath, request.FileName);
        var fileLock = GetFileLock(fullPath);
        await fileLock.WaitAsync(cancellationToken);
        try
        {
            var root = await LoadRootAsync(fullPath, cancellationToken);
            var idFields = await LoadIdFieldMapAsync(folderPath, cancellationToken);
            var table = DiscoverTables(root, idFields, Path.GetFileName(fullPath))
                .FirstOrDefault(x => string.Equals(x.Name, request.TableName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Không tìm thấy bảng {request.TableName}.");

            var node = GetTableNode(root, table.Name);
            if (node is JsonArray array)
            {
                if (IsArrayValueTable(table))
                {
                    var valueCell = request.Cells.FirstOrDefault(x => string.Equals(x.Name, "value", StringComparison.OrdinalIgnoreCase))
                                    ?? request.Cells.FirstOrDefault();
                    array.Add(valueCell is null ? null : ParseCell(valueCell));
                }
                else
                {
                    ValidateUniqueFieldNames(request.Cells);
                    var row = new JsonObject();
                    ApplyCellsToObject(row, request.Cells, request.DeletedFieldNames);
                    array.Add(row);
                }
            }
            else if (node is JsonObject obj)
            {
                ValidateUniqueFieldNames(request.Cells);
                ApplyCellsToObject(obj, request.Cells, request.DeletedFieldNames);
            }
            else
            {
                return new JsonCrudResultDto { Success = false, Message = "Kiểu JSON này chưa hỗ trợ thêm dữ liệu bằng form CRUD." };
            }

            await SaveRootAsync(fullPath, root, request.ExpectedFileVersion, cancellationToken);
            var message = node is JsonObject
                ? "Đã thêm/cập nhật field vào object JSON."
                : "Đã thêm dòng JSON mới.";
            return new JsonCrudResultDto { Success = true, Message = message };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException or FormatException)
        {
            return new JsonCrudResultDto { Success = false, Message = ex.Message };
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task<JsonCrudResultDto> UpdateRowAsync(string folderPath, JsonRowWriteRequest request, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveJsonFile(folderPath, request.FileName);
        var fileLock = GetFileLock(fullPath);
        await fileLock.WaitAsync(cancellationToken);
        try
        {
            var root = await LoadRootAsync(fullPath, cancellationToken);
            var idFields = await LoadIdFieldMapAsync(folderPath, cancellationToken);
            var table = DiscoverTables(root, idFields, Path.GetFileName(fullPath))
                .FirstOrDefault(x => string.Equals(x.Name, request.TableName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Không tìm thấy bảng {request.TableName}.");

            ValidateUniqueFieldNames(request.Cells);
            var tableNode = GetTableNode(root, table.Name);
            var mergeResult = new JsonMergeResult();

            if (tableNode is JsonArray array)
            {
                var targetIndex = ResolveTargetRowIndex(array, table, request.RowIndex, request.RowKey, request.IdField);
                if (targetIndex < 0 || targetIndex >= array.Count)
                {
                    return new JsonCrudResultDto
                    {
                        Success = false,
                        Message = "Dòng cần sửa không còn tồn tại. Có thể Unity vừa xóa hoặc đổi key dòng này."
                    };
                }

                if (IsArrayValueTable(table))
                {
                    ApplyArrayValueAutoMerge(array, targetIndex, request, mergeResult);
                }
                else
                {
                    if (array[targetIndex] is not JsonObject row)
                    {
                        row = new JsonObject();
                        array[targetIndex] = row;
                    }

                    ApplyCellsToObjectAutoMerge(row, request, mergeResult);
                }
            }
            else if (tableNode is JsonObject obj)
            {
                ApplyCellsToObjectAutoMerge(obj, request, mergeResult);
            }
            else
            {
                return new JsonCrudResultDto { Success = false, Message = "Kiểu JSON này chưa hỗ trợ sửa theo bảng." };
            }

            if (!mergeResult.HasAnyWrite)
            {
                return new JsonCrudResultDto
                {
                    Success = true,
                    HasConflicts = mergeResult.Conflicts.Count > 0,
                    Conflicts = mergeResult.Conflicts,
                    Message = mergeResult.Conflicts.Count > 0
                        ? "Unity vừa thay đổi cùng field nên tool giữ dữ liệu Unity, không ghi đè field xung đột."
                        : "Không có thay đổi mới từ form. Dữ liệu mới nhất từ Unity được giữ nguyên."
                };
            }

            await SaveRootAsync(fullPath, root, request.ExpectedFileVersion, cancellationToken);
            return new JsonCrudResultDto
            {
                Success = true,
                HasConflicts = mergeResult.Conflicts.Count > 0,
                Conflicts = mergeResult.Conflicts,
                Message = mergeResult.Conflicts.Count > 0
                    ? $"Đã auto-merge vào file JSON. Giữ lại {mergeResult.Conflicts.Count} field Unity vừa đổi để tránh app đấu dữ liệu."
                    : "Đã auto-merge thay đổi vào file JSON mới nhất."
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException or FormatException)
        {
            return new JsonCrudResultDto { Success = false, Message = ex.Message };
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task<JsonCrudResultDto> DeleteRowAsync(string folderPath, JsonRowDeleteRequest request, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveJsonFile(folderPath, request.FileName);
        var fileLock = GetFileLock(fullPath);
        await fileLock.WaitAsync(cancellationToken);
        try
        {
            var root = await LoadRootAsync(fullPath, cancellationToken);
            var idFields = await LoadIdFieldMapAsync(folderPath, cancellationToken);
            var table = DiscoverTables(root, idFields, Path.GetFileName(fullPath))
                .FirstOrDefault(x => string.Equals(x.Name, request.TableName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Không tìm thấy bảng {request.TableName}.");

            if (!table.CanDelete)
            {
                return new JsonCrudResultDto { Success = false, Message = "Object/root không xóa nguyên dòng; hãy bấm sửa rồi xóa field/key trong form." };
            }

            var node = GetTableNode(root, table.Name);
            if (node is not JsonArray array)
            {
                return new JsonCrudResultDto { Success = false, Message = "Bảng này không phải array nên không thể xóa dòng." };
            }

            var targetIndex = ResolveTargetRowIndex(array, table, request.RowIndex, request.RowKey, request.IdField);
            if (targetIndex < 0 || targetIndex >= array.Count)
            {
                return new JsonCrudResultDto
                {
                    Success = false,
                    Message = "Dòng cần xóa không còn tồn tại. Có thể Unity vừa xóa hoặc đổi key dòng này."
                };
            }

            if (request.OriginalCells.Count > 0 && !RowStillMatchesOriginal(array[targetIndex], request.OriginalCells))
            {
                return new JsonCrudResultDto
                {
                    Success = false,
                    HasConflicts = true,
                    Conflicts = ["Dòng này đã được Unity sửa sau khi app mở form xóa."],
                    Message = "Unity vừa sửa dòng này, tool không xóa để tránh đấu dữ liệu. Hãy tải lại rồi xóa nếu vẫn cần."
                };
            }

            array.RemoveAt(targetIndex);
            await SaveRootAsync(fullPath, root, request.ExpectedFileVersion, cancellationToken);
            return new JsonCrudResultDto { Success = true, Message = "Đã xóa dòng khỏi file JSON mới nhất." };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new JsonCrudResultDto { Success = false, Message = ex.Message };
        }
        finally
        {
            fileLock.Release();
        }
    }

    public Task<JsonCrudResultDto> DeleteRowAsync(string folderPath, string fileName, string tableName, int rowIndex, JsonFileVersionDto? expectedFileVersion = null, CancellationToken cancellationToken = default)
        => DeleteRowAsync(folderPath, new JsonRowDeleteRequest
        {
            FileName = fileName,
            TableName = tableName,
            RowIndex = rowIndex,
            ExpectedFileVersion = expectedFileVersion
        }, cancellationToken);

    private static void ApplyArrayValueAutoMerge(JsonArray array, int targetIndex, JsonRowWriteRequest request, JsonMergeResult mergeResult)
    {
        var newCell = request.Cells.FirstOrDefault(x => string.Equals(x.Name, "value", StringComparison.OrdinalIgnoreCase))
                      ?? request.Cells.FirstOrDefault();
        if (newCell is null)
        {
            return;
        }

        var originalCell = request.OriginalCells.FirstOrDefault(x => string.Equals(x.Name, "value", StringComparison.OrdinalIgnoreCase))
                           ?? request.OriginalCells.FirstOrDefault();
        if (originalCell is not null && !CellChangedFromOriginal(newCell, originalCell))
        {
            return;
        }

        var latestText = NodeToEditText(array[targetIndex]);
        var latestKind = GetNodeKind(array[targetIndex]);
        if (originalCell is not null && !CellEquivalent(latestKind, latestText, originalCell.Kind, originalCell.Value))
        {
            mergeResult.Conflicts.Add("value: Unity đã đổi value trong lúc app đang sửa nên tool giữ value của Unity.");
            return;
        }

        array[targetIndex] = ParseCell(newCell);
        mergeResult.HasAnyWrite = true;
    }

    private static void ApplyCellsToObjectAutoMerge(JsonObject obj, JsonRowWriteRequest request, JsonMergeResult mergeResult)
    {
        var originalMap = request.OriginalCells
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var deletedName in request.DeletedFieldNames.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = deletedName.Trim();
            if (!obj.TryGetPropertyValue(name, out var latestNode))
            {
                continue;
            }

            if (originalMap.TryGetValue(name, out var originalCell) && !CellEquivalent(GetNodeKind(latestNode), NodeToEditText(latestNode), originalCell.Kind, originalCell.Value))
            {
                mergeResult.Conflicts.Add($"{name}: Unity đã đổi field này nên tool không xóa.");
                continue;
            }

            obj.Remove(name);
            mergeResult.HasAnyWrite = true;
        }

        foreach (var cell in request.Cells.Where(x => !string.IsNullOrWhiteSpace(x.Name)))
        {
            var newName = cell.Name.Trim();
            var oldName = string.IsNullOrWhiteSpace(cell.OriginalName) ? newName : cell.OriginalName!.Trim();
            originalMap.TryGetValue(oldName, out var originalCell);

            if (originalCell is not null && !CellChangedFromOriginal(cell, originalCell))
            {
                continue;
            }

            if (obj.TryGetPropertyValue(oldName, out var latestNode))
            {
                if (originalCell is not null && !CellEquivalent(GetNodeKind(latestNode), NodeToEditText(latestNode), originalCell.Kind, originalCell.Value))
                {
                    mergeResult.Conflicts.Add($"{oldName}: Unity đã đổi field này trong lúc app đang sửa nên tool giữ giá trị Unity.");
                    continue;
                }
            }
            else if (originalCell is not null)
            {
                mergeResult.Conflicts.Add($"{oldName}: Unity đã xóa field này nên tool không tạo lại tự động.");
                continue;
            }
            else if (obj.ContainsKey(newName))
            {
                mergeResult.Conflicts.Add($"{newName}: Unity đã tạo field trùng tên nên tool không ghi đè.");
                continue;
            }

            if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase) && obj.ContainsKey(oldName))
            {
                obj.Remove(oldName);
            }

            obj[newName] = ParseCell(cell);
            mergeResult.HasAnyWrite = true;
        }
    }

    private static bool CellChangedFromOriginal(JsonCellDto current, JsonCellDto original)
    {
        var currentName = current.Name?.Trim() ?? string.Empty;
        var originalName = (current.OriginalName ?? original.Name)?.Trim() ?? string.Empty;
        return !string.Equals(currentName, originalName, StringComparison.OrdinalIgnoreCase)
               || !CellEquivalent(current.Kind, current.Value, original.Kind, original.Value);
    }

    private static bool CellEquivalent(string? leftKind, string? leftValue, string? rightKind, string? rightValue)
    {
        var lk = NormalizeCellKind(leftKind);
        var rk = NormalizeCellKind(rightKind);
        if (!string.Equals(lk, rk, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lv = NormalizeCellValueForCompare(lk, leftValue);
        var rv = NormalizeCellValueForCompare(rk, rightValue);
        return string.Equals(lv, rv, StringComparison.Ordinal);
    }

    private static string NormalizeCellKind(string? kind)
        => kind?.Trim().ToLowerInvariant() switch
        {
            "boolean" => "bool",
            "object" => "object",
            "array" => "array",
            "json" => "json",
            "number" => "number",
            "null" => "null",
            "bool" => "bool",
            _ => "string"
        };

    private static string NormalizeCellValueForCompare(string kind, string? value)
    {
        value ??= string.Empty;
        if (kind is "json" or "object" or "array")
        {
            try
            {
                return JsonNode.Parse(value)?.ToJsonString() ?? string.Empty;
            }
            catch
            {
                return value.Trim();
            }
        }

        return kind switch
        {
            "bool" => bool.TryParse(value, out var b) ? (b ? "true" : "false") : value.Trim().ToLowerInvariant(),
            "number" => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d.ToString(CultureInfo.InvariantCulture) : value.Trim(),
            "null" => string.Empty,
            _ => value
        };
    }

    private static int ResolveTargetRowIndex(JsonArray array, JsonTableDto table, int requestedIndex, string? requestedRowKey, string? requestedIdField)
    {
        var idField = !string.IsNullOrWhiteSpace(requestedIdField) ? requestedIdField! : table.IdField;
        if (requestedIndex >= 0 && requestedIndex < array.Count)
        {
            var keyAtIndex = GetRowKeyFromNode(array[requestedIndex], table, requestedIndex, idField);
            if (string.IsNullOrWhiteSpace(requestedRowKey) || string.Equals(keyAtIndex, requestedRowKey, StringComparison.OrdinalIgnoreCase))
            {
                return requestedIndex;
            }
        }

        if (!string.IsNullOrWhiteSpace(requestedRowKey))
        {
            for (var i = 0; i < array.Count; i++)
            {
                var key = GetRowKeyFromNode(array[i], table, i, idField);
                if (string.Equals(key, requestedRowKey, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return requestedIndex;
    }

    private static string GetRowKeyFromNode(JsonNode? node, JsonTableDto table, int index, string? idField)
    {
        if (node is JsonObject obj && !string.IsNullOrWhiteSpace(idField) && obj.TryGetPropertyValue(idField, out var keyNode))
        {
            return NodeToEditText(keyNode) ?? index.ToString(CultureInfo.InvariantCulture);
        }

        return index.ToString(CultureInfo.InvariantCulture);
    }

    private static bool RowStillMatchesOriginal(JsonNode? node, IReadOnlyCollection<JsonCellDto> originalCells)
    {
        if (originalCells.Count == 0)
        {
            return true;
        }

        if (node is not JsonObject obj)
        {
            var original = originalCells.FirstOrDefault();
            return original is null || CellEquivalent(GetNodeKind(node), NodeToEditText(node), original.Kind, original.Value);
        }

        foreach (var original in originalCells.Where(x => !string.IsNullOrWhiteSpace(x.Name)))
        {
            if (!obj.TryGetPropertyValue(original.Name.Trim(), out var latest))
            {
                return false;
            }

            if (!CellEquivalent(GetNodeKind(latest), NodeToEditText(latest), original.Kind, original.Value))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class JsonMergeResult
    {
        public bool HasAnyWrite { get; set; }
        public List<string> Conflicts { get; } = [];
    }

    private static void ApplyCellsToObject(JsonObject obj, IEnumerable<JsonCellDto> cells, IEnumerable<string>? deletedFieldNames)
    {
        RemoveRequestedFields(obj, deletedFieldNames);

        foreach (var cell in cells.Where(x => !string.IsNullOrWhiteSpace(x.Name)))
        {
            var newName = cell.Name.Trim();
            var oldName = cell.OriginalName?.Trim();
            if (!string.IsNullOrWhiteSpace(oldName) && !string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
            {
                obj.Remove(oldName);
            }

            obj[newName] = ParseCell(cell);
        }
    }

    private static void ValidateUniqueFieldNames(IEnumerable<JsonCellDto> cells)
    {
        var duplicated = cells
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicated is not null)
        {
            throw new InvalidOperationException($"Field '{duplicated.Key}' đang bị trùng trong form. Mỗi field/key chỉ được xuất hiện một lần.");
        }
    }

    private static void RemoveRequestedFields(JsonObject obj, IEnumerable<string>? fieldNames)
    {
        if (fieldNames is null)
        {
            return;
        }

        foreach (var fieldName in fieldNames.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            obj.Remove(fieldName.Trim());
        }
    }

    private static bool RowMatches(JsonRowDto row, string keyword)
    {
        return row.RowKey.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || row.RowIndex.ToString(CultureInfo.InvariantCulture).Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || row.Cells.Any(cell =>
                   cell.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                   || (cell.Value?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    private static bool RowMatchesFilter(JsonRowDto row, JsonQueryFilterDto filter)
    {
        var fieldName = filter.FieldName?.Trim() ?? string.Empty;
        var op = string.IsNullOrWhiteSpace(filter.Operator) ? "contains" : filter.Operator.Trim().ToLowerInvariant();
        var expected = filter.Value?.Trim() ?? string.Empty;
        var candidates = GetFilterCandidates(row, fieldName);

        if (op is "empty")
        {
            return candidates.Count == 0 || candidates.All(string.IsNullOrWhiteSpace);
        }

        if (op is "not-empty")
        {
            return candidates.Any(x => !string.IsNullOrWhiteSpace(x));
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        return candidates.Any(actual => ValueMatchesOperator(actual, expected, op));
    }

    private static List<string> GetFilterCandidates(JsonRowDto row, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName) || string.Equals(fieldName, "*", StringComparison.OrdinalIgnoreCase))
        {
            var values = row.Cells.Select(x => x.Value ?? string.Empty).ToList();
            values.Add(row.RowKey);
            values.Add(row.RowIndex.ToString(CultureInfo.InvariantCulture));
            return values;
        }

        if (string.Equals(fieldName, "__key", StringComparison.OrdinalIgnoreCase) || string.Equals(fieldName, "key", StringComparison.OrdinalIgnoreCase))
        {
            return [row.RowKey];
        }

        if (string.Equals(fieldName, "__rowIndex", StringComparison.OrdinalIgnoreCase) || string.Equals(fieldName, "rowIndex", StringComparison.OrdinalIgnoreCase))
        {
            return [row.RowIndex.ToString(CultureInfo.InvariantCulture)];
        }

        return row.Cells
            .Where(x => string.Equals(x.Name, fieldName, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Value ?? string.Empty)
            .ToList();
    }

    private static bool ValueMatchesOperator(string? actual, string expected, string op)
    {
        actual ??= string.Empty;
        return op switch
        {
            "equals" or "=" => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            "not-equals" or "!=" => !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            "starts" or "starts-with" => actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
            "ends" or "ends-with" => actual.EndsWith(expected, StringComparison.OrdinalIgnoreCase),
            "greater" or ">" => TryCompareNumber(actual, expected, out var cmpGreater) && cmpGreater > 0,
            "less" or "<" => TryCompareNumber(actual, expected, out var cmpLess) && cmpLess < 0,
            _ => actual.Contains(expected, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static bool TryCompareNumber(string actual, string expected, out int compare)
    {
        compare = 0;
        if (!decimal.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var actualNumber)
            || !decimal.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var expectedNumber))
        {
            return false;
        }

        compare = actualNumber.CompareTo(expectedNumber);
        return true;
    }

    private static List<JsonTableDto> DiscoverTables(JsonNode? root, IReadOnlyDictionary<string, string> idFields, string fileName)
    {
        var tables = new List<JsonTableDto>();
        if (root is null)
        {
            return tables;
        }

        if (root is JsonObject obj)
        {
            tables.Add(BuildObjectTable("__root", "Root object", "root-object", obj, idFields, fileName));

            foreach (var property in obj.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                switch (property.Value)
                {
                    case JsonArray array:
                        tables.Add(BuildArrayTable(property.Key, property.Key, array, idFields, fileName));
                        break;
                    case JsonObject childObject:
                        tables.Add(BuildObjectTable(property.Key, property.Key, "object", childObject, idFields, fileName));
                        break;
                }
            }
        }
        else if (root is JsonArray rootArray)
        {
            tables.Add(BuildArrayTable("__root_array", "Root array", rootArray, idFields, fileName));
        }

        return tables;
    }

    private static JsonTableDto BuildObjectTable(
        string name,
        string displayName,
        string kind,
        JsonObject obj,
        IReadOnlyDictionary<string, string> idFields,
        string fileName)
    {
        var fields = obj
            .Select(property => new JsonFieldDto
            {
                Name = property.Key,
                Kind = GetNodeKind(property.Value),
                IsKey = false,
                SeenCount = 1
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var idField = ResolveIdField(idFields, fileName, name, fields.Select(x => x.Name));
        foreach (var field in fields)
        {
            field.IsKey = string.Equals(field.Name, idField, StringComparison.OrdinalIgnoreCase);
        }

        return new JsonTableDto
        {
            Name = name,
            DisplayName = displayName,
            Kind = kind,
            RowCount = 1,
            CanCreate = true,
            CanDelete = false,
            IdField = idField,
            Fields = fields.OrderByDescending(x => x.IsKey).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private static JsonTableDto BuildArrayTable(
        string name,
        string displayName,
        JsonArray array,
        IReadOnlyDictionary<string, string> idFields,
        string fileName)
    {
        var fieldMap = new Dictionary<string, JsonFieldDto>(StringComparer.OrdinalIgnoreCase);
        var hasObjectRow = false;

        foreach (var item in array)
        {
            if (item is JsonObject obj)
            {
                hasObjectRow = true;
                foreach (var property in obj)
                {
                    if (!fieldMap.TryGetValue(property.Key, out var field))
                    {
                        field = new JsonFieldDto
                        {
                            Name = property.Key,
                            Kind = GetNodeKind(property.Value),
                            SeenCount = 0
                        };
                        fieldMap[property.Key] = field;
                    }

                    field.SeenCount++;
                    var currentKind = GetNodeKind(property.Value);
                    if (!string.Equals(field.Kind, currentKind, StringComparison.OrdinalIgnoreCase))
                    {
                        field.Kind = "json";
                    }
                }
            }
        }

        if (!hasObjectRow)
        {
            fieldMap["value"] = new JsonFieldDto
            {
                Name = "value",
                Kind = array.FirstOrDefault() is { } first ? GetNodeKind(first) : "string",
                SeenCount = array.Count,
                IsSynthetic = true
            };
        }

        var idField = ResolveIdField(idFields, fileName, name, fieldMap.Keys);
        foreach (var field in fieldMap.Values)
        {
            field.IsKey = string.Equals(field.Name, idField, StringComparison.OrdinalIgnoreCase);
        }

        return new JsonTableDto
        {
            Name = name,
            DisplayName = displayName,
            Kind = hasObjectRow ? "array-object" : "array-value",
            RowCount = array.Count,
            CanCreate = true,
            CanDelete = true,
            IdField = idField,
            Fields = fieldMap.Values
                .OrderByDescending(x => x.IsKey)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static IEnumerable<JsonRowDto> BuildRows(JsonNode? root, JsonTableDto table)
    {
        var node = GetTableNode(root, table.Name);
        if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                var item = array[i];
                yield return BuildRowFromArrayItem(item, table, i);
            }
        }
        else if (node is JsonObject obj)
        {
            yield return BuildRowFromObject(obj, table, 0);
        }
    }

    private static JsonRowDto BuildRowFromArrayItem(JsonNode? item, JsonTableDto table, int index)
    {
        if (item is JsonObject obj)
        {
            return BuildRowFromObject(obj, table, index);
        }

        var value = NodeToEditText(item);
        return new JsonRowDto
        {
            RowIndex = index,
            RowKey = index.ToString(CultureInfo.InvariantCulture),
            Cells =
            [
                new JsonCellDto
                {
                    Name = "value",
                    OriginalName = "value",
                    Kind = GetNodeKind(item),
                    Value = value,
                    IsKey = false
                }
            ]
        };
    }

    private static JsonRowDto BuildRowFromObject(JsonObject obj, JsonTableDto table, int index)
    {
        var orderedFields = table.Fields.Count == 0
            ? obj.Select(x => new JsonFieldDto { Name = x.Key, Kind = GetNodeKind(x.Value) }).ToList()
            : table.Fields;

        var cells = new List<JsonCellDto>();
        foreach (var field in orderedFields)
        {
            obj.TryGetPropertyValue(field.Name, out var valueNode);
            cells.Add(new JsonCellDto
            {
                Name = field.Name,
                OriginalName = field.Name,
                Kind = valueNode is null ? "null" : GetNodeKind(valueNode),
                Value = NodeToEditText(valueNode),
                IsKey = field.IsKey
            });
        }

        var key = !string.IsNullOrWhiteSpace(table.IdField)
            ? cells.FirstOrDefault(x => string.Equals(x.Name, table.IdField, StringComparison.OrdinalIgnoreCase))?.Value
            : null;

        return new JsonRowDto
        {
            RowIndex = index,
            RowKey = string.IsNullOrWhiteSpace(key) ? index.ToString(CultureInfo.InvariantCulture) : key!,
            Cells = cells
        };
    }

    private static JsonNode? GetTableNode(JsonNode? root, string tableName)
    {
        if (root is null)
        {
            return null;
        }

        if (string.Equals(tableName, "__root", StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        if (string.Equals(tableName, "__root_array", StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        if (root is JsonObject obj && obj.TryGetPropertyValue(tableName, out var node))
        {
            return node;
        }

        return null;
    }

    private static bool IsArrayValueTable(JsonTableDto table)
        => string.Equals(table.Kind, "array-value", StringComparison.OrdinalIgnoreCase);

    private static JsonNode? ParseCell(JsonCellDto cell)
    {
        var value = cell.Value ?? string.Empty;
        var kind = string.IsNullOrWhiteSpace(cell.Kind) ? "string" : cell.Kind.Trim().ToLowerInvariant();

        return kind switch
        {
            "null" => null,
            "boolean" or "bool" => bool.TryParse(value, out var boolValue)
                ? JsonValue.Create(boolValue)
                : throw new FormatException($"Field {cell.Name} phải là true/false."),
            "number" => ParseNumber(value, cell.Name),
            "json" => string.IsNullOrWhiteSpace(value) ? null : JsonNode.Parse(value),
            "object" => string.IsNullOrWhiteSpace(value) ? new JsonObject() : EnsureJsonType(JsonNode.Parse(value), "object", cell.Name),
            "array" => string.IsNullOrWhiteSpace(value) ? new JsonArray() : EnsureJsonType(JsonNode.Parse(value), "array", cell.Name),
            _ => JsonValue.Create(value)
        };
    }

    private static JsonNode? EnsureJsonType(JsonNode? node, string expectedKind, string fieldName)
    {
        if (expectedKind == "object" && node is JsonObject)
        {
            return node;
        }

        if (expectedKind == "array" && node is JsonArray)
        {
            return node;
        }

        throw new FormatException($"Field {fieldName} phải là JSON {expectedKind} hợp lệ.");
    }

    private static JsonNode ParseNumber(string value, string fieldName)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
        {
            return JsonValue.Create(longValue);
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            return JsonValue.Create(doubleValue);
        }

        throw new FormatException($"Field {fieldName} phải là số hợp lệ, dùng dấu chấm cho phần thập phân.");
    }

    private static string GetNodeKind(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        return node switch
        {
            JsonObject => "object",
            JsonArray => "array",
            JsonValue value => GetValueKind(value),
            _ => "json"
        };
    }

    private static string GetValueKind(JsonValue value)
    {
        if (value.TryGetValue<bool>(out _))
        {
            return "bool";
        }

        if (value.TryGetValue<int>(out _)
            || value.TryGetValue<long>(out _)
            || value.TryGetValue<float>(out _)
            || value.TryGetValue<double>(out _)
            || value.TryGetValue<decimal>(out _))
        {
            return "number";
        }

        return "string";
    }

    private static string? NodeToEditText(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return text;
            }

            if (value.TryGetValue<bool>(out var boolValue))
            {
                return boolValue ? "true" : "false";
            }

            return value.ToJsonString();
        }

        return node.ToJsonString(PrettyJsonOptions);
    }

    private static string ResolveIdField(IReadOnlyDictionary<string, string> idFields, string fileName, string tableName, IEnumerable<string> fieldNames)
    {
        if (idFields.TryGetValue(MakeIdKey(fileName, tableName), out var configured) && fieldNames.Contains(configured, StringComparer.OrdinalIgnoreCase))
        {
            return configured;
        }

        string[] preferred = ["id", "code", "id_odat", "name", "key", "slug"];
        return preferred.FirstOrDefault(x => fieldNames.Contains(x, StringComparer.OrdinalIgnoreCase)) ?? string.Empty;
    }

    private static async Task<IReadOnlyDictionary<string, string>> LoadIdFieldMapAsync(string folderPath, CancellationToken cancellationToken)
    {
        var configFile = Path.Combine(folderPath, "GameJsonDatabaseConfig.json");
        if (!File.Exists(configFile))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var root = await LoadRootAsync(configFile, cancellationToken);
            var databases = root?["databases"] as JsonArray;
            if (databases is null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var databaseNode in databases.OfType<JsonObject>())
            {
                var configFileName = databaseNode["fileName"]?.GetValue<string>();
                var tables = databaseNode["tables"] as JsonArray;
                if (string.IsNullOrWhiteSpace(configFileName) || tables is null)
                {
                    continue;
                }

                foreach (var tableNode in tables.OfType<JsonObject>())
                {
                    var tableName = tableNode["name"]?.GetValue<string>();
                    var idField = tableNode["idField"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(tableName) && !string.IsNullOrWhiteSpace(idField))
                    {
                        map[MakeIdKey(configFileName, tableName)] = idField;
                    }
                }
            }

            return map;
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string MakeIdKey(string fileName, string tableName)
        => Path.GetFileName(fileName).ToLowerInvariant() + "::" + tableName.ToLowerInvariant();

    private static async Task<JsonNode?> LoadRootAsync(string fullPath, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 64 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);

                return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (attempt < 6 && ex is IOException or JsonException)
            {
                // Unity có thể đang ghi dở file JSON; đợi file ổn định rồi đọc lại.
                await Task.Delay(80 * attempt, cancellationToken);
            }
        }

        await using var fallbackStream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonNode.ParseAsync(fallbackStream, cancellationToken: cancellationToken);
    }

    public async Task<JsonFileVersionDto> GetFileVersionAsync(string folderPath, string fileName, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveJsonFile(folderPath, fileName);
        return await GetFileVersionAsync(fullPath, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, JsonFileVersionDto>> GetFileVersionsAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(folderPath))
        {
            return new Dictionary<string, JsonFileVersionDto>(StringComparer.OrdinalIgnoreCase);
        }

        var files = Directory.EnumerateFiles(folderPath, "*.json", SearchOption.TopDirectoryOnly).ToArray();
        var result = new ConcurrentDictionary<string, JsonFileVersionDto>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(files, cancellationToken, async (file, ct) =>
        {
            try
            {
                var version = await GetFileVersionAsync(file, ct);
                result[version.FileName] = version;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // File may be mid-write by another app. The next watcher tick will retry.
            }
        });

        return result;
    }

    private static async Task SaveRootAsync(string fullPath, JsonNode? root, JsonFileVersionDto? expectedFileVersion, CancellationToken cancellationToken)
    {
        if (root is null)
        {
            throw new InvalidOperationException("Không thể lưu file JSON rỗng.");
        }

        var json = root.ToJsonString(PrettyJsonOptions);
        var payload = Encoding.UTF8.GetBytes(json);

        _ = expectedFileVersion; // Workflow Unity-sync: luôn load file mới nhất rồi merge từng field, không chặn cứng theo hash cũ.

        for (var attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    fullPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 64 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.WriteThrough);

                stream.SetLength(0);
                await stream.WriteAsync(payload, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                return;
            }
            catch (IOException) when (attempt < 6)
            {
                await Task.Delay(80 * attempt, cancellationToken);
            }
        }
    }

    private static async Task<JsonFileVersionDto> GetFileVersionAsync(string fullPath, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 64 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);

                return await GetFileVersionFromOpenStreamAsync(fullPath, stream, cancellationToken);
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(120 * attempt, cancellationToken);
            }
        }

        await using var fallbackStream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await GetFileVersionFromOpenStreamAsync(fullPath, fallbackStream, cancellationToken);
    }

    private static async Task<JsonFileVersionDto> GetFileVersionFromOpenStreamAsync(string fullPath, FileStream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken);
        var info = new FileInfo(fullPath);
        stream.Position = 0;
        return new JsonFileVersionDto
        {
            FileName = Path.GetFileName(fullPath),
            SizeBytes = stream.Length,
            LastWriteUtcTicks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0,
            ContentHash = Convert.ToHexString(hashBytes)
        };
    }

    private static bool FileVersionsContentEqual(JsonFileVersionDto current, JsonFileVersionDto expected)
    {
        if (!string.IsNullOrWhiteSpace(current.ContentHash) && !string.IsNullOrWhiteSpace(expected.ContentHash))
        {
            return current.SizeBytes == expected.SizeBytes
                   && string.Equals(current.ContentHash, expected.ContentHash, StringComparison.OrdinalIgnoreCase);
        }

        return current.SizeBytes == expected.SizeBytes
               && current.LastWriteUtcTicks == expected.LastWriteUtcTicks;
    }

    private sealed class ConfigFileConcurrencyException : Exception
    {
        public ConfigFileConcurrencyException(string message)
            : base(message)
        {
        }
    }


    public async Task<JsonCrudResultDto> CreateJsonFileAsync(string folderPath, JsonFileCreateRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return new JsonCrudResultDto { Success = false, Message = "Chưa chọn thư mục config hợp lệ." };
        }

        var safeName = Path.GetFileName((request.FileName ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(safeName))
        {
            return new JsonCrudResultDto { Success = false, Message = "Chưa nhập tên file JSON." };
        }

        if (!safeName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            safeName += ".json";
        }

        var rootFolder = Path.GetFullPath(folderPath);
        var fullPath = Path.GetFullPath(Path.Combine(rootFolder, safeName));
        if (!fullPath.StartsWith(rootFolder, StringComparison.OrdinalIgnoreCase))
        {
            return new JsonCrudResultDto { Success = false, Message = "Tên file JSON không hợp lệ." };
        }

        var fileLock = GetFileLock(fullPath);
        await fileLock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(fullPath) && !request.OverwriteIfExists)
            {
                return new JsonCrudResultDto { Success = false, Message = $"File {safeName} đã tồn tại. Hãy đổi tên file hoặc bật ghi đè." };
            }

            JsonNode? root;
            if (!string.IsNullOrWhiteSpace(request.JsonText))
            {
                root = JsonNode.Parse(request.JsonText);
            }
            else
            {
                root = NormalizeCellKind(request.RootKind) == "array" ? new JsonArray() : new JsonObject();
            }

            if (root is null)
            {
                root = NormalizeCellKind(request.RootKind) == "array" ? new JsonArray() : new JsonObject();
            }

            await SaveRootAsync(fullPath, root, null, cancellationToken);
            return new JsonCrudResultDto { Success = true, Message = $"Đã tạo file {safeName}." };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new JsonCrudResultDto { Success = false, Message = ex.Message };
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task<List<string>> SearchKeysAsync(string folderPath, string? fileName, string? keyword, int maxResults = 200, CancellationToken cancellationToken = default)
    {
        var results = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var files = string.IsNullOrWhiteSpace(fileName)
            ? Directory.EnumerateFiles(folderPath, "*.json", SearchOption.TopDirectoryOnly).ToArray()
            : new[] { ResolveJsonFile(folderPath, fileName) };

        var degree = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
        await Parallel.ForEachAsync(files, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = degree }, async (file, ct) =>
        {
            try
            {
                var root = await LoadRootAsync(file, ct);
                CollectKeys(root, keyword, results, maxResults);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Unity may be mid-write. Watcher/next query will retry.
            }
        });

        return results.Keys
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxResults, 10, 1000))
            .ToList();
    }

    private static void CollectKeys(JsonNode? node, string? keyword, ConcurrentDictionary<string, byte> results, int maxResults)
    {
        if (node is null || results.Count >= maxResults)
        {
            return;
        }

        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (results.Count >= maxResults)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(keyword) || property.Key.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    results.TryAdd(property.Key, 0);
                }

                CollectKeys(property.Value, keyword, results, maxResults);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (results.Count >= maxResults)
                {
                    return;
                }

                CollectKeys(item, keyword, results, maxResults);
            }
        }
    }

    private static string ResolveJsonFile(string folderPath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new InvalidOperationException("Chưa chọn thư mục config.");
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("Chưa chọn file JSON.");
        }

        var safeFileName = Path.GetFileName(fileName.Trim());
        if (!safeFileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Chỉ hỗ trợ file .json trong thư mục config đã chọn.");
        }

        var root = Path.GetFullPath(folderPath);
        var fullPath = Path.GetFullPath(Path.Combine(root, safeFileName));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Đường dẫn file không hợp lệ.");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Không tìm thấy file JSON.", safeFileName);
        }

        return fullPath;
    }

    private SemaphoreSlim GetFileLock(string fullPath)
        => _fileLocks.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
}
