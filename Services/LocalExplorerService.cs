using System.Diagnostics;
using ConfigTool.Models;

namespace ConfigTool.Services;

public interface ILocalExplorerService
{
    Task<LocalOpenResultDto> OpenFolderAsync(string? folderPath);
    Task<LocalOpenResultDto> OpenFileLocationAsync(string? filePath);
}

public sealed class LocalExplorerService : ILocalExplorerService
{
    public Task<LocalOpenResultDto> OpenFolderAsync(string? folderPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return Task.FromResult(new LocalOpenResultDto { Success = false, Message = "Thư mục không tồn tại." });
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = Quote(folderPath),
                UseShellExecute = true
            });
            return Task.FromResult(new LocalOpenResultDto { Success = true, Message = "Đã mở thư mục." });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new LocalOpenResultDto { Success = false, Message = "Không mở được Explorer: " + ex.Message });
        }
    }

    public Task<LocalOpenResultDto> OpenFileLocationAsync(string? filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return Task.FromResult(new LocalOpenResultDto { Success = false, Message = "Chưa có file để mở location." });
            }

            if (File.Exists(filePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select," + Quote(filePath),
                    UseShellExecute = true
                });
                return Task.FromResult(new LocalOpenResultDto { Success = true, Message = "Đã mở location file." });
            }

            var folder = Directory.Exists(filePath) ? filePath : Path.GetDirectoryName(filePath);
            return OpenFolderAsync(folder);
        }
        catch (Exception ex)
        {
            return Task.FromResult(new LocalOpenResultDto { Success = false, Message = "Không mở được location: " + ex.Message });
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "") + "\"";
}
