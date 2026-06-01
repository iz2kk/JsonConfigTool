namespace ConfigTool.Services;

public interface IConfigFolderPicker
{
    Task<string?> PickFolderAsync(CancellationToken cancellationToken = default);
}

public sealed class UnsupportedConfigFolderPicker : IConfigFolderPicker
{
    public Task<string?> PickFolderAsync(CancellationToken cancellationToken = default)
        => Task.FromException<string?>(new PlatformNotSupportedException("ConfigTool hiện cấu hình cho Windows folder dialog."));
}
