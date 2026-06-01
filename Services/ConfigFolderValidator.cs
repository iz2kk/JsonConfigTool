using ConfigTool.Models;

namespace ConfigTool.Services;

public sealed class ConfigFolderValidator
{
    public FolderValidationResult Validate(string? folderPath, IEnumerable<string>? requiredFileNames = null)
    {
        var sample = (requiredFileNames?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToArray()
                      ?? ConfigToolDefaults.RequiredFileNames);

        var result = new FolderValidationResult
        {
            FolderPath = folderPath
        };

        if (string.IsNullOrWhiteSpace(folderPath))
        {
            result.Exists = false;
            result.IsValid = false;
            result.MissingFiles = sample.ToList();
            result.Message = "Chưa chọn thư mục config.";
            return result;
        }

        if (!Directory.Exists(folderPath))
        {
            result.Exists = false;
            result.IsValid = false;
            result.MissingFiles = sample.ToList();
            result.Message = "Thư mục không tồn tại hoặc không còn truy cập được.";
            return result;
        }

        result.Exists = true;
        var files = Directory.EnumerateFiles(folderPath, "*.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        result.FoundFiles = files;
        result.MissingFiles = sample
            .Where(req => !files.Contains(req, StringComparer.OrdinalIgnoreCase))
            .ToList();

        // Workflow mới: folder hợp lệ khi có ít nhất 1 file .json.
        // Bộ file mẫu trong Configs.zip chỉ dùng để gợi ý, không còn chặn việc load custom config của Unity.
        result.IsValid = files.Count > 0;
        result.Message = result.IsValid
            ? $"Đã quét {files.Count} file JSON trong thư mục. Tool sẽ đọc tất cả file .json, không giới hạn bộ mẫu."
            : "Không tìm thấy file .json nào trong thư mục đã chọn.";

        return result;
    }
}
