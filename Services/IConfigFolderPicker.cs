namespace ConfigTool.Services;

public interface IConfigFolderPicker
{
    Task<string?> PickFolderAsync(CancellationToken cancellationToken = default);
    Task<string?> PickFolderAsync(string? title, string? okButtonLabel = null, CancellationToken cancellationToken = default);
}

public sealed class UnsupportedConfigFolderPicker : IConfigFolderPicker
{
    public Task<string?> PickFolderAsync(CancellationToken cancellationToken = default)
        => PickFolderAsync(null, null, cancellationToken);

    public Task<string?> PickFolderAsync(string? title, string? okButtonLabel = null, CancellationToken cancellationToken = default)
        => Task.FromException<string?>(new PlatformNotSupportedException("ConfigTool hiện cấu hình cho Windows folder dialog."));
}
