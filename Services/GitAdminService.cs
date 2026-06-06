using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConfigTool.Models;

namespace ConfigTool.Services;

public sealed class GitAdminService
{
    private readonly IAppStartupPathProvider _pathProvider;
    private readonly SemaphoreSlim _configLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _repoLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public GitAdminService(IAppStartupPathProvider pathProvider)
    {
        _pathProvider = pathProvider;
    }

    public string GitConfigFilePath => Path.Combine(_pathProvider.ConfigDirectory, "gitconfig.json");

    public async Task<GitConfigResponseDto> LoadConfigAsync(CancellationToken cancellationToken)
    {
        var config = await ReadConfigAsync(cancellationToken);
        return new GitConfigResponseDto
        {
            ConfigFilePath = GitConfigFilePath,
            Config = config
        };
    }

    public async Task<GitActionResultDto> SaveAccountAsync(GitAccountDto account, CancellationToken cancellationToken)
    {
        await _configLock.WaitAsync(cancellationToken);
        try
        {
            var config = await ReadConfigCoreAsync(cancellationToken);
            account.Provider = NormalizeProvider(account.Provider);
            account.ApiBaseUrl = NormalizeApiBaseUrl(account.Provider, account.ApiBaseUrl);
            account.Name = string.IsNullOrWhiteSpace(account.Name) ? BuildDefaultAccountName(account) : account.Name.Trim();
            account.Username = NormalizeNullable(account.Username);
            account.Email = NormalizeNullable(account.Email);
            account.GitBaseUrl = NormalizeNullable(account.GitBaseUrl);
            account.Token = account.Token?.Trim() ?? string.Empty;
            account.TokenSource = NormalizeTokenSource(account.TokenSource);
            account.TokenType = NormalizeNullable(account.TokenType) ?? string.Empty;
            account.RefreshToken = NormalizeNullable(account.RefreshToken);
            account.OAuthClientId = NormalizeNullable(account.OAuthClientId);
            account.OAuthClientSecret = NormalizeNullable(account.OAuthClientSecret);
            account.OAuthScopes = NormalizeOAuthScopes(account.Provider, account.OAuthScopes);
            account.OAuthRedirectPort = NormalizeOAuthRedirectPort(account.OAuthRedirectPort);
            account.UpdatedAt = DateTimeOffset.Now;

            if (string.IsNullOrWhiteSpace(account.Id))
            {
                account.Id = Guid.NewGuid().ToString("N");
                account.CreatedAt = DateTimeOffset.Now;
            }

            var existing = config.Accounts.FindIndex(x => string.Equals(x.Id, account.Id, StringComparison.OrdinalIgnoreCase));
            if (account.IsDefault)
            {
                foreach (var item in config.Accounts)
                {
                    item.IsDefault = false;
                }
            }
            else if (config.Accounts.Count == 0)
            {
                account.IsDefault = true;
            }

            if (existing >= 0)
            {
                config.Accounts[existing] = account;
            }
            else
            {
                config.Accounts.Add(account);
            }

            if (config.Accounts.Count > 0 && !config.Accounts.Any(x => x.IsDefault))
            {
                config.Accounts[0].IsDefault = true;
            }

            await WriteConfigCoreAsync(config, cancellationToken);
            return Ok("Đã lưu Git account vào gitconfig.json.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail("Không lưu được Git account: " + ex.Message);
        }
        finally
        {
            _configLock.Release();
        }
    }

    public async Task<GitActionResultDto> DeleteAccountAsync(string accountId, CancellationToken cancellationToken)
    {
        await _configLock.WaitAsync(cancellationToken);
        try
        {
            var config = await ReadConfigCoreAsync(cancellationToken);
            var removed = config.Accounts.RemoveAll(x => string.Equals(x.Id, accountId, StringComparison.OrdinalIgnoreCase));
            if (config.Accounts.Count > 0 && !config.Accounts.Any(x => x.IsDefault))
            {
                config.Accounts[0].IsDefault = true;
            }

            await WriteConfigCoreAsync(config, cancellationToken);
            return removed > 0 ? Ok("Đã xóa Git account.") : Fail("Không tìm thấy Git account để xóa.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail("Không xóa được Git account: " + ex.Message);
        }
        finally
        {
            _configLock.Release();
        }
    }

    public async Task<GitOAuthResultDto> StartOAuthLoginAsync(GitOAuthRequestDto request, Func<GitConsoleEntryDto, Task>? logAsync, CancellationToken cancellationToken)
    {
        var draft = request.Account ?? new GitAccountDto();
        draft.Provider = NormalizeProvider(draft.Provider);
        draft.ApiBaseUrl = NormalizeApiBaseUrl(draft.Provider, draft.ApiBaseUrl);
        draft.OAuthClientId = NormalizeNullable(draft.OAuthClientId);
        draft.OAuthClientSecret = NormalizeNullable(draft.OAuthClientSecret);
        draft.OAuthScopes = NormalizeOAuthScopes(draft.Provider, draft.OAuthScopes);
        draft.OAuthRedirectPort = NormalizeOAuthRedirectPort(draft.OAuthRedirectPort);
        draft.Name = string.IsNullOrWhiteSpace(draft.Name) ? BuildDefaultAccountName(draft) : draft.Name.Trim();
        draft.Username = NormalizeNullable(draft.Username);
        draft.Email = NormalizeNullable(draft.Email);

        if (string.IsNullOrWhiteSpace(draft.OAuthClientId))
        {
            return new GitOAuthResultDto
            {
                Success = false,
                Message = "Chưa nhập OAuth Client ID. Tạo OAuth App trên GitHub/GitLab rồi nhập Client ID vào form."
            };
        }

        var timeoutSeconds = Math.Clamp(request.TimeoutSeconds <= 0 ? 300 : request.TimeoutSeconds, 60, 900);
        var redirectUri = BuildLocalOAuthRedirectUri(draft.OAuthRedirectPort);
        var state = CreateRandomUrlSafe(32);
        var verifier = CreateCodeVerifier();
        var challenge = CreateCodeChallenge(verifier);
        var authUrl = BuildOAuthAuthorizeUrl(draft, redirectUri, state, challenge);

        await WriteLogAsync(logAsync, "info", "oauth", $"Mở trình duyệt OAuth {draft.Provider}. Redirect URI: {redirectUri}");
        try
        {
            OpenBrowser(authUrl);
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logAsync, "error", "oauth", "Không mở được trình duyệt: " + ex.Message);
            return new GitOAuthResultDto
            {
                Success = false,
                Message = "Không mở được trình duyệt OAuth: " + ex.Message,
                AuthorizationUrl = authUrl
            };
        }

        GitOAuthCallbackData callback;
        try
        {
            callback = await WaitForOAuthCallbackAsync(draft.OAuthRedirectPort, state, TimeSpan.FromSeconds(timeoutSeconds), logAsync, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            await WriteLogAsync(logAsync, "error", "oauth", "OAuth callback lỗi: " + ex.Message);
            return new GitOAuthResultDto
            {
                Success = false,
                Message = "Không nhận được OAuth callback. Kiểm tra Callback URL trong OAuth App có đúng " + redirectUri + " không. Chi tiết: " + ex.Message,
                AuthorizationUrl = authUrl
            };
        }

        if (!string.IsNullOrWhiteSpace(callback.Error))
        {
            var message = "OAuth bị từ chối hoặc lỗi: " + callback.Error;
            if (!string.IsNullOrWhiteSpace(callback.ErrorDescription))
            {
                message += " - " + callback.ErrorDescription;
            }

            await WriteLogAsync(logAsync, "error", "oauth", message);
            return new GitOAuthResultDto { Success = false, Message = message, AuthorizationUrl = authUrl };
        }

        if (string.IsNullOrWhiteSpace(callback.Code))
        {
            return new GitOAuthResultDto { Success = false, Message = "OAuth callback không có code.", AuthorizationUrl = authUrl };
        }

        GitOAuthTokenData token;
        try
        {
            token = await ExchangeOAuthCodeAsync(draft, callback.Code, redirectUri, verifier, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            await WriteLogAsync(logAsync, "error", "oauth", "Đổi OAuth code lấy token lỗi: " + ex.Message);
            return new GitOAuthResultDto
            {
                Success = false,
                Message = "Đổi OAuth code lấy token lỗi: " + ex.Message,
                AuthorizationUrl = authUrl
            };
        }

        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            return new GitOAuthResultDto { Success = false, Message = "OAuth token rỗng, không thể lưu account.", AuthorizationUrl = authUrl };
        }

        draft.Token = token.AccessToken.Trim();
        draft.TokenType = string.IsNullOrWhiteSpace(token.TokenType) ? "Bearer" : token.TokenType.Trim();
        draft.TokenSource = "oauth";
        draft.RefreshToken = NormalizeNullable(token.RefreshToken);
        draft.LastOAuthAt = DateTimeOffset.Now;
        draft.TokenExpiresAt = token.ExpiresInSeconds > 0 ? DateTimeOffset.Now.AddSeconds(token.ExpiresInSeconds) : null;

        await PopulateOAuthProfileAsync(draft, cancellationToken);
        var save = await SaveAccountAsync(draft, cancellationToken);
        await WriteLogAsync(logAsync, save.Success ? "success" : "error", "oauth", save.Message);

        return new GitOAuthResultDto
        {
            Success = save.Success,
            Message = save.Success ? $"OAuth {draft.Provider} OK. Đã tự lấy token và lưu vào gitconfig.json cho {draft.Name}." : save.Message,
            AuthorizationUrl = authUrl,
            Account = draft
        };
    }

    public async Task<GitCreatedRepositoryDto> TestAccountAsync(string accountId, CancellationToken cancellationToken)
    {
        var config = await ReadConfigAsync(cancellationToken);
        var account = config.Accounts.FirstOrDefault(x => string.Equals(x.Id, accountId, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            return new GitCreatedRepositoryDto { Success = false, Message = "Không tìm thấy account." };
        }

        try
        {
            using var client = CreateClient(account);
            using var response = account.Provider.Equals("gitlab", StringComparison.OrdinalIgnoreCase)
                ? await client.GetAsync(BuildApiUrl(account, "/user"), cancellationToken)
                : await client.GetAsync(BuildApiUrl(account, "/user"), cancellationToken);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new GitCreatedRepositoryDto
                {
                    Success = false,
                    Message = $"Đăng nhập {account.Provider} lỗi HTTP {(int)response.StatusCode}: {TrimForUi(body, 500)}"
                };
            }

            return new GitCreatedRepositoryDto
            {
                Success = true,
                Message = $"Đăng nhập {account.Provider} OK: {account.Name}"
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new GitCreatedRepositoryDto { Success = false, Message = "Không test được account: " + ex.Message };
        }
    }

    public async Task<GitCreatedRepositoryDto> CreateRepositoryAsync(GitCreateRepositoryRequestDto request, Func<GitConsoleEntryDto, Task>? logAsync, CancellationToken cancellationToken)
    {
        var config = await ReadConfigAsync(cancellationToken);
        var account = config.Accounts.FirstOrDefault(x => string.Equals(x.Id, request.AccountId, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            return new GitCreatedRepositoryDto { Success = false, Message = "Chưa chọn GitHub/GitLab account." };
        }

        var name = SanitizeRepoName(request.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return new GitCreatedRepositoryDto { Success = false, Message = "Tên repository không hợp lệ." };
        }

        await WriteLogAsync(logAsync, "info", "api", $"Tạo repository {account.Provider}/{name}...");
        try
        {
            using var client = CreateClient(account);
            HttpResponseMessage response;
            if (account.Provider.Equals("gitlab", StringComparison.OrdinalIgnoreCase))
            {
                var payload = new
                {
                    name,
                    path = name,
                    description = request.Description ?? string.Empty,
                    visibility = NormalizeVisibility(request.Visibility),
                    initialize_with_readme = request.InitializeReadme
                };
                response = await client.PostAsync(BuildApiUrl(account, "/projects"), ToJsonContent(payload), cancellationToken);
            }
            else
            {
                var payload = new
                {
                    name,
                    description = request.Description ?? string.Empty,
                    @private = !NormalizeVisibility(request.Visibility).Equals("public", StringComparison.OrdinalIgnoreCase),
                    auto_init = request.InitializeReadme
                };
                response = await client.PostAsync(BuildApiUrl(account, "/user/repos"), ToJsonContent(payload), cancellationToken);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    await WriteLogAsync(logAsync, "error", "api", $"Tạo repository lỗi HTTP {(int)response.StatusCode}: {TrimForUi(body, 800)}");
                    return new GitCreatedRepositoryDto
                    {
                        Success = false,
                        Message = $"Tạo repository lỗi HTTP {(int)response.StatusCode}: {TrimForUi(body, 500)}"
                    };
                }

                var result = ParseCreatedRepository(body, account.Provider);
                result.Success = true;
                result.Message = "Đã tạo repository mới.";
                await WriteLogAsync(logAsync, "success", "api", "Đã tạo repository: " + (string.IsNullOrWhiteSpace(result.HtmlUrl) ? result.CloneUrl : result.HtmlUrl));
                return result;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            await WriteLogAsync(logAsync, "error", "api", "Không tạo được repository: " + ex.Message);
            return new GitCreatedRepositoryDto { Success = false, Message = "Không tạo được repository: " + ex.Message };
        }
    }

    public async Task<GitActionResultDto> SaveLastProjectFolderAsync(string? folderPath, CancellationToken cancellationToken)
    {
        await _configLock.WaitAsync(cancellationToken);
        try
        {
            var config = await ReadConfigCoreAsync(cancellationToken);
            config.LastProjectFolder = NormalizeNullable(folderPath);
            await WriteConfigCoreAsync(config, cancellationToken);
            return Ok("Đã lưu thư mục dự án Git.");
        }
        finally
        {
            _configLock.Release();
        }
    }

    public async Task<GitActionResultDto> SaveLastCloneFolderAsync(string? folderPath, CancellationToken cancellationToken)
    {
        await _configLock.WaitAsync(cancellationToken);
        try
        {
            var config = await ReadConfigCoreAsync(cancellationToken);
            config.LastCloneFolder = NormalizeNullable(folderPath);
            await WriteConfigCoreAsync(config, cancellationToken);
            return Ok("Đã lưu thư mục clone Git.");
        }
        finally
        {
            _configLock.Release();
        }
    }

    public async Task<GitRepositoryStatusDto> GetRepositoryStatusAsync(string folderPath, CancellationToken cancellationToken)
    {
        var folder = NormalizeFolder(folderPath);
        var result = new GitRepositoryStatusDto
        {
            FolderPath = folder,
            FolderExists = Directory.Exists(folder),
            IsGitRepository = Directory.Exists(Path.Combine(folder, ".git"))
        };

        if (!result.FolderExists || !result.IsGitRepository)
        {
            return result;
        }

        var status = await RunGitUnlockedAsync(folder, "status --porcelain=v1 -b", null, cancellationToken);
        result.Changes = ParseGitStatus(status.Output);
        result.HasChanges = result.Changes.Count > 0;
        result.CurrentBranch = ParseCurrentBranch(status.Output);

        var branches = await RunGitUnlockedAsync(folder, "branch --all --no-color", null, cancellationToken);
        result.Branches = ParseBranches(branches.Output);
        if (string.IsNullOrWhiteSpace(result.CurrentBranch))
        {
            result.CurrentBranch = result.Branches.FirstOrDefault(x => x.IsCurrent)?.Name ?? string.Empty;
        }

        var remotes = await RunGitUnlockedAsync(folder, "remote -v", null, cancellationToken);
        result.Remotes = ParseRemotes(remotes.Output);

        var lastCommit = await RunGitUnlockedAsync(folder, "log -1 --pretty=format:%h %ad %s --date=short", null, cancellationToken);
        result.LastCommit = lastCommit.Success ? lastCommit.Output.Trim() : string.Empty;
        return result;
    }

    public async Task<GitFileExplorerDto> GetFileExplorerAsync(string folderPath, string? search, CancellationToken cancellationToken)
    {
        var folder = NormalizeFolder(folderPath);
        var statusMap = await BuildStatusMapAsync(folder, true, cancellationToken);
        var items = new List<GitLocalFileDto>();
        if (!Directory.Exists(folder))
        {
            return new GitFileExplorerDto { FolderPath = folder, Search = search ?? string.Empty };
        }

        var normalizedSearch = (search ?? string.Empty).Trim();
        var comparison = StringComparison.OrdinalIgnoreCase;
        var total = 0;
        var ignored = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(folder, entry).Replace('\\', '/');
            if (relative.Equals(".git", comparison) || relative.StartsWith(".git/", comparison))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(normalizedSearch) && relative.IndexOf(normalizedSearch, comparison) < 0)
            {
                continue;
            }

            var status = ResolveGitStatusForPath(statusMap, relative);
            var isIgnored = string.Equals(status, "!!", StringComparison.Ordinal);
            if (isIgnored)
            {
                ignored++;
                continue;
            }

            total++;
            if (items.Count >= 700)
            {
                continue;
            }

            var isDirectory = Directory.Exists(entry);
            var fileInfo = isDirectory ? null : new FileInfo(entry);
            var dirInfo = isDirectory ? new DirectoryInfo(entry) : null;
            items.Add(new GitLocalFileDto
            {
                Name = Path.GetFileName(entry),
                RelativePath = relative,
                FullPath = entry,
                IsDirectory = isDirectory,
                GitStatus = status,
                IsIgnored = false,
                IsTracked = IsTrackedStatus(status),
                CanStage = true,
                SizeBytes = fileInfo?.Length ?? 0,
                LastWriteTime = isDirectory ? new DateTimeOffset(dirInfo!.LastWriteTime) : new DateTimeOffset(fileInfo!.LastWriteTime)
            });
        }

        return new GitFileExplorerDto
        {
            FolderPath = folder,
            Search = normalizedSearch,
            Items = items
                .OrderByDescending(x => x.IsDirectory)
                .ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            TotalCount = total,
            IgnoredCount = ignored,
            IsTruncated = total > items.Count
        };
    }

    public async Task<GitRemoteExplorerDto> GetRemoteExplorerAsync(string folderPath, string? remoteName, string? branchName, string? search, CancellationToken cancellationToken)
    {
        var folder = NormalizeFolder(folderPath);
        var remote = string.IsNullOrWhiteSpace(remoteName) ? "origin" : remoteName.Trim();
        var branch = string.IsNullOrWhiteSpace(branchName) ? await GetCurrentBranchNameAsync(folder, cancellationToken) : branchName.Trim();
        var normalizedSearch = (search ?? string.Empty).Trim();
        var result = new GitRemoteExplorerDto
        {
            FolderPath = folder,
            RemoteName = remote,
            BranchName = branch,
            Search = normalizedSearch
        };

        if (string.IsNullOrWhiteSpace(branch))
        {
            return result;
        }

        var sourceRef = remote.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
            ? "HEAD"
            : $"{remote}/{branch}";
        result.SourceRef = sourceRef;

        var tree = await RunGitUnlockedAsync(folder, "ls-tree -r --long " + QuoteArg(sourceRef), null, cancellationToken);
        if (!tree.Success && !sourceRef.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            sourceRef = branch;
            result.SourceRef = sourceRef;
            tree = await RunGitUnlockedAsync(folder, "ls-tree -r --long " + QuoteArg(sourceRef), null, cancellationToken);
        }

        if (!tree.Success)
        {
            return result;
        }

        var comparison = StringComparison.OrdinalIgnoreCase;
        var total = 0;
        foreach (var item in ParseLsTreeLong(tree.Output))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(normalizedSearch) && item.Path.IndexOf(normalizedSearch, comparison) < 0)
            {
                continue;
            }

            total++;
            if (result.Items.Count >= 700)
            {
                continue;
            }

            result.Items.Add(item);
        }

        result.TotalCount = total;
        result.IsTruncated = total > result.Items.Count;
        result.Items = result.Items.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
        return result;
    }

    public async Task<GitProjectConfigSnapshotDto> GetProjectConfigFilesAsync(string folderPath, CancellationToken cancellationToken)
    {
        var folder = NormalizeFolder(folderPath);
        var snapshot = new GitProjectConfigSnapshotDto { FolderPath = folder };
        if (!Directory.Exists(folder))
        {
            return snapshot;
        }

        const int maxFiles = 220;
        const int maxBytesPerFile = 256 * 1024;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<GitProjectConfigFileDto>();

        async Task AddCandidateAsync(string fullPath, string source, string? displayName = null, string? relativePathOverride = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (files.Count >= maxFiles || string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                return;
            }

            var normalized = Path.GetFullPath(fullPath);
            if (!seen.Add(normalized))
            {
                return;
            }

            var info = new FileInfo(normalized);
            var relativePath = relativePathOverride;
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                relativePath = normalized.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                    ? Path.GetRelativePath(folder, normalized).Replace('\\', '/')
                    : normalized;
            }

            try
            {
                var content = await ReadTextPreviewAsync(normalized, maxBytesPerFile, cancellationToken);
                files.Add(new GitProjectConfigFileDto
                {
                    Source = source,
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(normalized) : displayName,
                    RelativePath = relativePath,
                    FullPath = normalized,
                    Content = content.Text,
                    SizeBytes = info.Length,
                    IsTruncated = content.Truncated,
                    LastWriteTime = new DateTimeOffset(info.LastWriteTime)
                });
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                files.Add(new GitProjectConfigFileDto
                {
                    Source = source,
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(normalized) : displayName,
                    RelativePath = relativePath,
                    FullPath = normalized,
                    Content = "Không đọc được file cấu hình: " + ex.Message,
                    SizeBytes = info.Exists ? info.Length : 0,
                    IsTruncated = false,
                    LastWriteTime = info.Exists ? new DateTimeOffset(info.LastWriteTime) : DateTimeOffset.Now
                });
            }
        }

        await AddCandidateAsync(Path.Combine(folder, ".gitignore"), "root");
        await AddCandidateAsync(Path.Combine(folder, ".gitattributes"), "root");
        await AddCandidateAsync(Path.Combine(folder, ".gitmodules"), "root");

        var gitPath = Path.Combine(folder, ".git");
        if (File.Exists(gitPath))
        {
            await AddCandidateAsync(gitPath, ".git", ".git file", ".git");
            var resolvedGitDir = await TryResolveGitDirFileAsync(gitPath, folder, cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolvedGitDir) && Directory.Exists(resolvedGitDir))
            {
                await AddKnownGitDirectoryFilesAsync(resolvedGitDir, ".git", ".git/resolved", AddCandidateAsync, cancellationToken);
            }
        }
        else if (Directory.Exists(gitPath))
        {
            await AddKnownGitDirectoryFilesAsync(gitPath, ".git", ".git", AddCandidateAsync, cancellationToken);
        }

        var githubFolder = Path.Combine(folder, ".github");
        if (Directory.Exists(githubFolder))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(githubFolder, "*", SearchOption.AllDirectories)
                             .OrderBy(x => Path.GetRelativePath(folder, x), StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (files.Count >= maxFiles)
                    {
                        break;
                    }

                    if (!IsLikelyTextGitHubConfig(file))
                    {
                        continue;
                    }

                    await AddCandidateAsync(file, ".github");
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                files.Add(new GitProjectConfigFileDto
                {
                    Source = ".github",
                    DisplayName = ".github scan error",
                    RelativePath = ".github",
                    FullPath = githubFolder,
                    Content = "Không scan được .github: " + ex.Message,
                    LastWriteTime = DateTimeOffset.Now
                });
            }
        }

        snapshot.TotalCount = files.Count;
        snapshot.IsTruncated = files.Count >= maxFiles;
        snapshot.Files = files
            .OrderBy(x => x.Source == ".git" ? 0 : x.Source == "root" ? 1 : 2)
            .ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return snapshot;
    }

    public async Task<string> LoadGitIgnoreAsync(string folderPath, CancellationToken cancellationToken)
    {
        var file = Path.Combine(NormalizeFolder(folderPath), ".gitignore");
        if (!File.Exists(file))
        {
            return string.Empty;
        }

        return await File.ReadAllTextAsync(file, Encoding.UTF8, cancellationToken);
    }

    public async Task<GitActionResultDto> SaveGitIgnoreAsync(string folderPath, string content, CancellationToken cancellationToken)
    {
        var folder = NormalizeFolder(folderPath);
        if (!Directory.Exists(folder))
        {
            return Fail("Thư mục dự án không tồn tại.");
        }

        var gate = GetRepoLock(folder);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var file = Path.Combine(folder, ".gitignore");
            await File.WriteAllTextAsync(file, content ?? string.Empty, new UTF8Encoding(false), cancellationToken);
            return Ok("Đã lưu .gitignore.", await GetRepositoryStatusAsync(folder, cancellationToken));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail("Không lưu được .gitignore: " + ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GitActionResultDto> InitRepositoryAsync(string folderPath, Func<GitConsoleEntryDto, Task>? logAsync, CancellationToken cancellationToken)
    {
        var folder = NormalizeFolder(folderPath);
        if (!Directory.Exists(folder))
        {
            return Fail("Thư mục dự án không tồn tại.");
        }

        var gate = GetRepoLock(folder);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var result = await RunGitUnlockedAsync(folder, "init", logAsync, cancellationToken);
            result.Repository = await GetRepositoryStatusAsync(folder, cancellationToken);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GitActionResultDto> AddRemoteAsync(GitRemoteRequestDto request, Func<GitConsoleEntryDto, Task>? logAsync, CancellationToken cancellationToken)
    {
        var folder = NormalizeFolder(request.FolderPath);
        var remoteName = SanitizeRemoteName(request.RemoteName);
        if (string.IsNullOrWhiteSpace(remoteName) || string.IsNullOrWhiteSpace(request.RemoteUrl))
        {
            return Fail("Remote name/url không hợp lệ.");
        }

        var gate = GetRepoLock(folder);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await RunGitUnlockedAsync(folder, "remote", null, cancellationToken);
            var exists = existing.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Any(x => string.Equals(x.Trim(), remoteName, StringComparison.OrdinalIgnoreCase));
            var args = exists && request.SetUrlIfExists
                ? $"remote set-url {QuoteArg(remoteName)} {QuoteArg(request.RemoteUrl)}"
                : $"remote add {QuoteArg(remoteName)} {QuoteArg(request.RemoteUrl)}";
            var result = await RunGitUnlockedAsync(folder, args, logAsync, cancellationToken);
            result.Repository = await GetRepositoryStatusAsync(folder, cancellationToken);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GitActionResultDto> AddFilesAsync(GitAddRequestDto request, Func<GitConsoleEntryDto, Task>? logAsync, CancellationToken cancellationToken)
    {
        var folder = NormalizeFolder(request.FolderPath);
        var gate = GetRepoLock(folder);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var paths = NormalizeGitPaths(request.Paths);
            string args;
            if (request.StageAll || paths.Count == 0)
            {
                args = "add -A --";
                await WriteLogAsync(logAsync, "info", "git", "Stage all: git add -A --, Git tự bỏ qua file trong .gitignore.");
            }
            else
            {
                var stageable = await FilterStageablePathsAsync(folder, paths, logAsync, cancellationToken);
                if (stageable.Count == 0)
                {
                    await WriteLogAsync(logAsync, "warn", "git", "Không có file hợp lệ để git add sau khi lọc .gitignore.");
                    return new GitActionResultDto
                    {
                        Success = false,
                        Message = "Không có file hợp lệ để git add. File bị .gitignore hoặc path không hợp lệ.",
                        Repository = await GetRepositoryStatusAsync(folder, cancellationToken)
                    };
                }

                args = "add -- " + string.Join(' ', stageable.Select(QuoteArg));
                await WriteLogAsync(logAsync, "info", "git", $"Stage selected: {stageable.Count} path hợp lệ sau khi lọc .gitignore.");
            }

            var result = await RunGitUnlockedAsync(folder, args, logAsync, cancellationToken);
            if (result.Success)
            {
                var staged = await RunGitUnlockedAsync(folder, "diff --cached --name-status --", null, cancellationToken);
                var stagedText = staged.Output.Trim();
                if (string.IsNullOrWhiteSpace(stagedText))
                {
                    await WriteLogAsync(logAsync, "warn", "git", "git add chạy xong nhưng chưa có staged changes để commit. Có thể file đang clean hoặc bị ignore.");
                    result.Message = "git add chạy xong nhưng chưa có staged changes để commit.";
                }
                else
                {
                    await WriteLogAsync(logAsync, "success", "git", "Staged changes:");
                    foreach (var line in stagedText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Take(120))
                    {
                        await WriteLogAsync(logAsync, "out", "git", line);
                    }
                }
            }

            result.Repository = await GetRepositoryStatusAsync(folder, cancellationToken);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GitActionResultDto> CommitAsync(GitCommitRequestDto request, Func<GitConsoleEntryDto, Task>? logAsync, CancellationToken cancellationToken)
    {
        var folder = NormalizeFolder(request.FolderPath);
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return Fail("Commit message không được trống.");
        }

        var gate = GetRepoLock(folder);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var paths = NormalizeGitPaths(request.Paths);
            if (request.StageAll)
            {
                await WriteLogAsync(logAsync, "info", "git", "Stage all: git add -A --, vẫn tuân theo .gitignore.");
                var addResult = await RunGitUnlockedAsync(folder, "add -A --", logAsync, cancellationToken);
                if (!addResult.Success)
                {
                    addResult.Repository = await GetRepositoryStatusAsync(folder, cancellationToken);
                    return addResult;
                }
            }
            else if (paths.Count > 0)
            {
                var stageable = await FilterStageablePathsAsync(folder, paths, logAsync, cancellationToken);
                if (stageable.Count == 0)
                {
                    return new GitActionResultDto
                    {
                        Success = false,
                        Message = "Không có file hợp lệ để stage trước commit. File có thể bị .gitignore hoặc path không hợp lệ.",
                        Repository = await GetRepositoryStatusAsync(folder, cancellationToken)
                    };
                }

                await WriteLogAsync(logAsync, "info", "git", $"Stage selected: {stageable.Count} file/folder, vẫn tuân theo .gitignore.");
                var addResult = await RunGitUnlockedAsync(folder, "add -- " + string.Join(' ', stageable.Select(QuoteArg)), logAsync, cancellationToken);
                if (!addResult.Success)
                {
                    addResult.Repository = await GetRepositoryStatusAsync(folder, cancellationToken);
                    return addResult;
                }
            }
            else
            {
                await WriteLogAsync(logAsync, "info", "git", "Không stage thêm file; commit dùng các thay đổi đã git add trước đó.");
            }

            var result = await RunGitUnlockedAsync(folder, "commit -m " + QuoteArg(request.Message), logAsync, cancellationToken);
            result.Repository = await GetRepositoryStatusAsync(folder, cancellationToken);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GitActionResultDto> PushAsync(GitPushPullRequestDto request, Func<GitConsoleEntryDto, Task>? logAsync, CancellationToken cancellationToken)
    {
        var folder = NormalizeFolder(request.FolderPath);
        var gate = GetRepoLock(folder);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var branch = string.IsNullOrWhiteSpace(request.BranchName)
                ? await GetCurrentBranchNameAsync(folder, cancellationToken)
                : request.BranchName.Trim();
            var remote = string.IsNullOrWhiteSpace(request.RemoteName) ? "origin" : request.RemoteName.Trim();
            var args = request.SetUpstream
                ? $"push --progress --verbose -u {QuoteArg(remote)} {QuoteArg(branch)}"
                : $"push --progress --verbose {QuoteArg(remote)} {QuoteArg(branch)}";
            var result = await RunGitUnlockedAsync(folder, args, logAsync, cancellationToken);
            result.Repository = await GetRepositoryStatusAsync(folder, cancellationToken);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GitActionResultDto> PullAsync(GitPushPullRequestDto request, Func<GitConsoleEntryDto, Task>? logAsync, CancellationToken cancellationToken)
    {
        var folder = NormalizeFolder(request.FolderPath);
        var gate = GetRepoLock(folder);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var remote = string.IsNullOrWhiteSpace(request.RemoteName) ? "origin" : request.RemoteName.Trim();
            var branch = string.IsNullOrWhiteSpace(request.BranchName)
                ? await GetCurrentBranchNameAsync(folder, cancellationToken)
                : request.BranchName.Trim();
            var args = request.PullRebase
                ? $"pull --progress --rebase {QuoteArg(remote)} {QuoteArg(branch)}"
                : $"pull --progress {QuoteArg(remote)} {QuoteArg(branch)}";
            var result = await RunGitUnlockedAsync(folder, args, logAsync, cancellationToken);
            result.Repository = await GetRepositoryStatusAsync(folder, cancellationToken);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GitActionResultDto> CreateBranchAsync(GitBranchRequestDto request, Func<GitConsoleEntryDto, Task>? logAsync, CancellationToken cancellationToken)
    {
        var folder = NormalizeFolder(request.FolderPath);
        var branch = SanitizeBranchName(request.BranchName);
        if (string.IsNullOrWhiteSpace(branch))
        {
            return Fail("Tên branch không hợp lệ.");
        }

        var gate = GetRepoLock(folder);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var args = request.CheckoutAfterCreate ? "checkout -b " + QuoteArg(branch) : "branch " + QuoteArg(branch);
            var result = await RunGitUnlockedAsync(folder, args, logAsync, cancellationToken);
            result.Repository = await GetRepositoryStatusAsync(folder, cancellationToken);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GitActionResultDto> CheckoutBranchAsync(GitBranchRequestDto request, Func<GitConsoleEntryDto, Task>? logAsync, CancellationToken cancellationToken)
    {
        var folder = NormalizeFolder(request.FolderPath);
        var branch = SanitizeBranchName(request.BranchName);
        if (string.IsNullOrWhiteSpace(branch))
        {
            return Fail("Tên branch không hợp lệ.");
        }

        var gate = GetRepoLock(folder);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var result = await RunGitUnlockedAsync(folder, "checkout " + QuoteArg(branch), logAsync, cancellationToken);
            result.Repository = await GetRepositoryStatusAsync(folder, cancellationToken);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GitActionResultDto> CloneAsync(GitCloneRequestDto request, Func<GitConsoleEntryDto, Task>? logAsync, CancellationToken cancellationToken)
    {
        var parent = NormalizeFolder(request.ParentFolderPath);
        if (!Directory.Exists(parent))
        {
            return Fail("Thư mục đích clone không tồn tại.");
        }

        if (string.IsNullOrWhiteSpace(request.RepositoryUrl))
        {
            return Fail("Repository URL không được trống.");
        }

        var targetFolderName = NormalizeNullable(request.TargetFolderName);
        var args = "clone --progress --verbose " + QuoteArg(request.RepositoryUrl) + (string.IsNullOrWhiteSpace(targetFolderName) ? string.Empty : " " + QuoteArg(targetFolderName));
        var gateKey = Path.GetFullPath(parent);
        var gate = GetRepoLock(gateKey);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var result = await RunGitUnlockedAsync(parent, args, logAsync, cancellationToken);
            var target = ResolveCloneTarget(parent, request.RepositoryUrl, targetFolderName);
            result.Repository = Directory.Exists(target) ? await GetRepositoryStatusAsync(target, cancellationToken) : null;
            await SaveLastCloneFolderAsync(parent, cancellationToken);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<List<GitRepositorySearchResultDto>> FindGitRepositoriesAsync(string rootFolderPath, string? keyword, int maxResults, Func<GitConsoleEntryDto, Task>? logAsync, CancellationToken cancellationToken)
    {
        var root = NormalizeFolder(rootFolderPath);
        var results = new List<GitRepositorySearchResultDto>();
        if (!Directory.Exists(root))
        {
            return results;
        }

        maxResults = Math.Clamp(maxResults <= 0 ? 100 : maxResults, 1, 300);
        var search = (keyword ?? string.Empty).Trim();
        await WriteLogAsync(logAsync, "info", "scan", "Đang tìm thư mục đã git init trong: " + root);
        await Task.Run(() => ScanGitRepositories(root, search, maxResults, results, cancellationToken), cancellationToken);
        await WriteLogAsync(logAsync, "success", "scan", $"Tìm thấy {results.Count} repository.");
        return results.OrderBy(x => x.FolderPath, StringComparer.OrdinalIgnoreCase).ToList();
    }


    private static async Task AddKnownGitDirectoryFilesAsync(
        string gitDirectory,
        string source,
        string relativePrefix,
        Func<string, string, string?, string?, Task> addCandidateAsync,
        CancellationToken cancellationToken)
    {
        string[] knownFiles =
        [
            "config",
            "HEAD",
            "packed-refs",
            "FETCH_HEAD",
            "ORIG_HEAD",
            "COMMIT_EDITMSG",
            "MERGE_MSG",
            "MERGE_HEAD",
            "info/exclude",
            "logs/HEAD",
            "rebase-merge/git-rebase-todo",
            "rebase-merge/done",
            "rebase-apply/patch"
        ];

        foreach (var relative in knownFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await addCandidateAsync(
                Path.Combine(gitDirectory, relative.Replace('/', Path.DirectorySeparatorChar)),
                source,
                Path.GetFileName(relative),
                relativePrefix.TrimEnd('/') + "/" + relative);
        }
    }

    private static async Task<string?> TryResolveGitDirFileAsync(string gitFilePath, string projectFolder, CancellationToken cancellationToken)
    {
        try
        {
            var content = await File.ReadAllTextAsync(gitFilePath, Encoding.UTF8, cancellationToken);
            var line = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            const string prefix = "gitdir:";
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var value = line[prefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(projectFolder, value));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsLikelyTextGitHubConfig(string filePath)
    {
        var name = Path.GetFileName(filePath);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (name.Equals("CODEOWNERS", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PULL_REQUEST_TEMPLATE", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("ISSUE_TEMPLATE", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ext is ".yml" or ".yaml" or ".md" or ".txt" or ".json" or ".toml" or ".ini" or ".cfg" or ".properties";
    }

    private static async Task<(string Text, bool Truncated)> ReadTextPreviewAsync(string filePath, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var take = (int)Math.Min(Math.Max(maxBytes, 1024), length);
        var buffer = new byte[take];
        var read = await stream.ReadAsync(buffer.AsMemory(0, take), cancellationToken);
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        if (text.IndexOf('\0') >= 0)
        {
            text = text.Replace("\0", "␀");
        }

        var truncated = length > read;
        if (truncated)
        {
            text += $"\n\n--- Nội dung bị cắt để tránh lag UI. File lớn {length:N0} bytes, chỉ đọc {read:N0} bytes đầu. ---";
        }

        return (text, truncated);
    }

    private async Task<GitConfigFileDto> ReadConfigAsync(CancellationToken cancellationToken)
    {
        await _configLock.WaitAsync(cancellationToken);
        try
        {
            return await ReadConfigCoreAsync(cancellationToken);
        }
        finally
        {
            _configLock.Release();
        }
    }

    private async Task<GitConfigFileDto> ReadConfigCoreAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_pathProvider.ConfigDirectory);
        if (!File.Exists(GitConfigFilePath))
        {
            var fresh = new GitConfigFileDto();
            await WriteConfigCoreAsync(fresh, cancellationToken);
            return fresh;
        }

        await using var stream = File.Open(GitConfigFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await JsonSerializer.DeserializeAsync<GitConfigFileDto>(stream, JsonOptions, cancellationToken) ?? new GitConfigFileDto();
    }

    private async Task WriteConfigCoreAsync(GitConfigFileDto config, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_pathProvider.ConfigDirectory);
        var tempPath = GitConfigFilePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, config, JsonOptions, cancellationToken);
        }

        if (File.Exists(GitConfigFilePath))
        {
            File.Delete(GitConfigFilePath);
        }

        File.Move(tempPath, GitConfigFilePath);
    }

    private async Task<GitActionResultDto> RunGitUnlockedAsync(string workingDirectory, string arguments, Func<GitConsoleEntryDto, Task>? logAsync, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return Fail("Working directory không tồn tại: " + workingDirectory);
        }

        await WriteLogAsync(logAsync, "command", "git", "git " + MaskToken(arguments));
        await WriteLogAsync(logAsync, "info", "git", "cwd: " + workingDirectory);
        try
        {
            using var process = new Process();
            var output = new StringBuilder();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            process.StartInfo.Environment["GIT_FLUSH"] = "1";
            process.EnableRaisingEvents = true;

            if (!process.Start())
            {
                return Fail("Không start được git process.");
            }

            var stdoutTask = PumpGitStreamAsync(process.StandardOutput, output, "out", logAsync, cancellationToken);
            var stderrTask = PumpGitStreamAsync(process.StandardError, output, "err", logAsync, cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch (OperationCanceledException)
            {
                await TryKillGitProcessAsync(process, logAsync);
                try
                {
                    await Task.WhenAll(stdoutTask, stderrTask);
                }
                catch (OperationCanceledException)
                {
                    // Streams can be interrupted while the process is being killed.
                }

                var canceledText = output.ToString();
                await WriteLogAsync(logAsync, "warn", "git", "Đã hủy git command và đã cố gắng kill process.");
                return new GitActionResultDto
                {
                    Success = false,
                    ExitCode = process.HasExited ? process.ExitCode : -1,
                    Message = "Đã hủy git command.",
                    Output = canceledText
                };
            }

            var text = output.ToString();
            var success = process.ExitCode == 0;
            await WriteLogAsync(logAsync, success ? "success" : "error", "git", $"git exit code {process.ExitCode}");
            return new GitActionResultDto
            {
                Success = success,
                ExitCode = process.ExitCode,
                Message = success ? "Git command chạy xong." : BuildGitFailureMessage(text),
                Output = text
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logAsync, "error", "git", "Không chạy được git CLI: " + ex.Message);
            return Fail("Không chạy được git CLI. Kiểm tra máy đã cài Git và git có trong PATH chưa. Chi tiết: " + ex.Message);
        }
    }


    private static async Task TryKillGitProcessAsync(Process process, Func<GitConsoleEntryDto, Task>? logAsync)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await WriteLogAsync(logAsync, "warn", "git", "Đang kill tiến trình git đang chạy...");
            }
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logAsync, "error", "git", "Không kill được tiến trình git: " + ex.Message);
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static async Task PumpGitStreamAsync(StreamReader reader, StringBuilder output, string level, Func<GitConsoleEntryDto, Task>? logAsync, CancellationToken cancellationToken)
    {
        var buffer = new char[512];
        var line = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            for (var i = 0; i < read; i++)
            {
                var ch = buffer[i];
                if (ch == '\r' || ch == '\n')
                {
                    await FlushGitLogLineAsync(line, output, level, logAsync);
                    continue;
                }

                line.Append(ch);
                if (line.Length >= 2048)
                {
                    await FlushGitLogLineAsync(line, output, level, logAsync);
                }
            }
        }

        await FlushGitLogLineAsync(line, output, level, logAsync);
    }

    private static async Task FlushGitLogLineAsync(StringBuilder line, StringBuilder output, string level, Func<GitConsoleEntryDto, Task>? logAsync)
    {
        if (line.Length == 0)
        {
            return;
        }

        var text = line.ToString();
        line.Clear();
        lock (output)
        {
            output.AppendLine(text);
        }

        await WriteLogAsync(logAsync, level, "git", text);
    }

    private static string BuildGitFailureMessage(string output)
    {
        if (output.Contains("HTTP 408", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("unexpected disconnect", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("RPC failed", StringComparison.OrdinalIgnoreCase))
        {
            return "Git command lỗi do remote/network timeout hoặc disconnect. Xem console log; có thể thử pull trước, push lại, kiểm tra mạng/token/remote hoặc repo/file quá lớn.";
        }

        if (output.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("could not read Username", StringComparison.OrdinalIgnoreCase))
        {
            return "Git command lỗi xác thực. Kiểm tra token/credential helper hoặc remote URL.";
        }

        return "Git command lỗi. Xem console log để biết chi tiết.";
    }

    private SemaphoreSlim GetRepoLock(string folderPath)
    {
        var key = Path.GetFullPath(folderPath ?? string.Empty);
        return _repoLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    }

    private async Task<Dictionary<string, string>> BuildStatusMapAsync(string folder, bool includeIgnored, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(Path.Combine(folder, ".git")))
        {
            return result;
        }

        var args = includeIgnored
            ? "status --porcelain=v1 --ignored=matching -uall"
            : "status --porcelain=v1 -uall";
        var status = await RunGitUnlockedAsync(folder, args, null, cancellationToken);
        foreach (var item in ParseGitStatus(status.Output))
        {
            if (!string.IsNullOrWhiteSpace(item.Path))
            {
                result[item.Path] = item.Status;
            }
        }

        return result;
    }

    private static string ResolveGitStatusForPath(IReadOnlyDictionary<string, string> statusMap, string relativePath)
    {
        if (statusMap.TryGetValue(relativePath, out var status))
        {
            return status;
        }

        foreach (var pair in statusMap)
        {
            if (string.Equals(pair.Value, "!!", StringComparison.Ordinal) &&
                (relativePath.StartsWith(pair.Key.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase) ||
                 pair.Key.StartsWith(relativePath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)))
            {
                return "!!";
            }
        }

        return string.Empty;
    }

    private static bool IsTrackedStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return true;
        }

        return !string.Equals(status, "??", StringComparison.Ordinal) &&
               !string.Equals(status, "!!", StringComparison.Ordinal);
    }



    private async Task<List<string>> FilterStageablePathsAsync(string folder, List<string> paths, Func<GitConsoleEntryDto, Task>? logAsync, CancellationToken cancellationToken)
    {
        var statusMap = await BuildStatusMapAsync(folder, true, cancellationToken);
        var result = new List<string>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = ResolveGitStatusForPath(statusMap, path);
            if (string.Equals(status, "!!", StringComparison.Ordinal))
            {
                await WriteLogAsync(logAsync, "warn", "git", "Bỏ qua vì bị .gitignore: " + path);
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), folder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                await WriteLogAsync(logAsync, "warn", "git", "Bỏ qua path ngoài project: " + path);
                continue;
            }

            result.Add(path);
        }

        return result;
    }

    private static List<GitRemoteFileDto> ParseLsTreeLong(string output)
    {
        var result = new List<GitRemoteFileDto>();
        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var tabIndex = line.IndexOf('\t');
            if (tabIndex < 0)
            {
                continue;
            }

            var meta = line[..tabIndex].Trim();
            var path = line[(tabIndex + 1)..].Trim();
            var parts = meta.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            long size = 0;
            if (parts.Length >= 4 && !string.Equals(parts[3], "-", StringComparison.Ordinal))
            {
                long.TryParse(parts[3], out size);
            }

            result.Add(new GitRemoteFileDto
            {
                Name = Path.GetFileName(path),
                Path = path.Replace('\\', '/'),
                Mode = parts[0],
                ObjectType = parts[1],
                ObjectId = parts[2],
                SizeBytes = size
            });
        }

        return result;
    }

    private static List<string> NormalizeGitPaths(IEnumerable<string>? paths)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (paths is null)
        {
            return result;
        }

        foreach (var raw in paths)
        {
            var path = (raw ?? string.Empty).Trim().Trim('"').Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            {
                continue;
            }

            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(x => x == "..") || path.Equals(".git", StringComparison.OrdinalIgnoreCase) || path.StartsWith(".git/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            path = string.Join('/', parts);
            if (seen.Add(path))
            {
                result.Add(path);
            }
        }

        return result;
    }

    private async Task<string> GetCurrentBranchNameAsync(string folder, CancellationToken cancellationToken)
    {
        var branch = await RunGitUnlockedAsync(folder, "branch --show-current", null, cancellationToken);
        return branch.Output.Trim();
    }

    private static List<GitFileStatusDto> ParseGitStatus(string output)
    {
        var result = new List<GitFileStatusDto>();
        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.StartsWith("##", StringComparison.Ordinal))
            {
                continue;
            }

            if (raw.Length < 4)
            {
                continue;
            }

            var status = raw[..2].Trim();
            var path = raw[3..].Trim();
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                path = path[(arrow + 4)..].Trim();
            }

            result.Add(new GitFileStatusDto
            {
                Status = string.IsNullOrWhiteSpace(status) ? "modified" : status,
                Path = path.Replace('\\', '/')
            });
        }

        return result;
    }

    private static string ParseCurrentBranch(string statusOutput)
    {
        var first = statusOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(x => x.StartsWith("##", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(first))
        {
            return string.Empty;
        }

        var branch = first[2..].Trim();
        var dot = branch.IndexOf("...", StringComparison.Ordinal);
        if (dot >= 0)
        {
            branch = branch[..dot];
        }

        return branch.Trim();
    }

    private static List<GitBranchDto> ParseBranches(string output)
    {
        var result = new List<GitBranchDto>();
        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd();
            var isCurrent = line.TrimStart().StartsWith('*');
            var name = line.Trim().TrimStart('*').Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Contains(" -> ", StringComparison.Ordinal))
            {
                continue;
            }

            var isRemote = name.StartsWith("remotes/", StringComparison.OrdinalIgnoreCase);
            result.Add(new GitBranchDto
            {
                Name = name,
                IsCurrent = isCurrent,
                IsRemote = isRemote
            });
        }

        return result;
    }

    private static List<GitRemoteDto> ParseRemotes(string output)
    {
        var map = new Dictionary<string, GitRemoteDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            var name = parts[0].Trim();
            var url = parts[1].Trim();
            var kind = parts[2].Trim('(', ')');
            if (!map.TryGetValue(name, out var remote))
            {
                remote = new GitRemoteDto { Name = name };
                map[name] = remote;
            }

            if (kind.Equals("push", StringComparison.OrdinalIgnoreCase))
            {
                remote.PushUrl = url;
            }
            else
            {
                remote.FetchUrl = url;
            }
        }

        return map.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ScanGitRepositories(string root, string keyword, int maxResults, List<GitRepositorySearchResultDto> results, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0 && results.Count < maxResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            try
            {
                if (Directory.Exists(Path.Combine(current, ".git")))
                {
                    if (string.IsNullOrWhiteSpace(keyword) || current.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        results.Add(new GitRepositorySearchResultDto
                        {
                            FolderPath = current,
                            Name = Path.GetFileName(current),
                            LastWriteTime = new DateTimeOffset(Directory.GetLastWriteTime(current))
                        });
                    }

                    continue;
                }

                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    var name = Path.GetFileName(child);
                    if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals(".vs", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    pending.Push(child);
                }
            }
            catch
            {
                // Ignore folders without permission; scanner must not crash the app.
            }
        }
    }

    private static HttpClient CreateClient(GitAccountDto account)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ConfigTool-GitAdmin/1.0");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(account.Token))
        {
            if (account.Provider.Equals("gitlab", StringComparison.OrdinalIgnoreCase))
            {
                if (IsOAuthToken(account))
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.Token);
                }
                else
                {
                    client.DefaultRequestHeaders.Add("PRIVATE-TOKEN", account.Token);
                }
            }
            else
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.Token);
                client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            }
        }

        return client;
    }

    private static bool IsOAuthToken(GitAccountDto account)
        => account.TokenSource.Equals("oauth", StringComparison.OrdinalIgnoreCase) || account.TokenType.Equals("Bearer", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTokenSource(string? source)
    {
        source = (source ?? "manual").Trim().ToLowerInvariant();
        return source is "oauth" or "manual" ? source : "manual";
    }

    private static string NormalizeOAuthScopes(string provider, string? scopes)
    {
        var value = (scopes ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(value))
        {
            return string.Join(' ', value.Split([' ', ',', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
        }

        return provider.Equals("gitlab", StringComparison.OrdinalIgnoreCase)
            ? "api read_user write_repository"
            : "repo read:user user:email";
    }

    private static int NormalizeOAuthRedirectPort(int port)
        => port is >= 1024 and <= 65535 ? port : 53682;

    private static string BuildLocalOAuthRedirectUri(int port)
        => $"http://127.0.0.1:{NormalizeOAuthRedirectPort(port)}/callback";

    private static string BuildOAuthAuthorizeUrl(GitAccountDto account, string redirectUri, string state, string codeChallenge)
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = account.OAuthClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = NormalizeOAuthScopes(account.Provider, account.OAuthScopes),
            ["state"] = state
        };

        if (account.Provider.Equals("gitlab", StringComparison.OrdinalIgnoreCase))
        {
            query["code_challenge"] = codeChallenge;
            query["code_challenge_method"] = "S256";
            return AddQuery(BuildGitLabWebBaseUrl(account).TrimEnd('/') + "/oauth/authorize", query);
        }

        return AddQuery("https://github.com/login/oauth/authorize", query);
    }

    private static string BuildGitLabWebBaseUrl(GitAccountDto account)
    {
        var raw = (account.ApiBaseUrl ?? "https://gitlab.com").Trim();
        raw = raw.Replace("/api/v4", string.Empty, StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        return string.IsNullOrWhiteSpace(raw) ? "https://gitlab.com" : raw;
    }

    private static string AddQuery(string baseUrl, IReadOnlyDictionary<string, string?> query)
    {
        var builder = new StringBuilder(baseUrl);
        builder.Append(baseUrl.Contains("?", StringComparison.Ordinal) ? '&' : '?');
        var first = true;
        foreach (var item in query)
        {
            if (string.IsNullOrWhiteSpace(item.Value))
            {
                continue;
            }

            if (!first)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(item.Key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(item.Value));
            first = false;
        }

        return builder.ToString();
    }

    private static async Task<GitOAuthCallbackData> WaitForOAuthCallbackAsync(int port, string expectedState, TimeSpan timeout, Func<GitConsoleEntryDto, Task>? logAsync, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var listener = new TcpListener(IPAddress.Loopback, NormalizeOAuthRedirectPort(port));
        try
        {
            listener.Start(1);
            await WriteLogAsync(logAsync, "info", "oauth", $"Đang chờ OAuth callback tại {BuildLocalOAuthRedirectUri(port)} ...");
            using var client = await listener.AcceptTcpClientAsync(linkedCts.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(linkedCts.Token) ?? string.Empty;
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(linkedCts.Token)))
            {
                // Drain headers.
            }

            var callback = ParseOAuthRequestLine(requestLine, expectedState);
            await WriteOAuthBrowserResponseAsync(stream, callback.Success, callback.Success
                ? "ConfigTool đã nhận OAuth token. Bạn có thể quay lại app."
                : callback.ErrorDescription ?? callback.Error ?? "OAuth callback lỗi.", linkedCts.Token);
            return callback;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static GitOAuthCallbackData ParseOAuthRequestLine(string requestLine, string expectedState)
    {
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return new GitOAuthCallbackData(false, string.Empty, "bad_request", "OAuth callback request không hợp lệ.");
        }

        var target = parts[1];
        var uri = new Uri("http://127.0.0.1" + target, UriKind.Absolute);
        var query = ParseQuery(uri.Query);
        query.TryGetValue("state", out var actualState);
        if (!string.Equals(expectedState, actualState, StringComparison.Ordinal))
        {
            return new GitOAuthCallbackData(false, string.Empty, "invalid_state", "OAuth state không khớp, đã chặn để tránh callback giả.");
        }

        query.TryGetValue("error", out var error);
        query.TryGetValue("error_description", out var errorDescription);
        query.TryGetValue("code", out var code);
        return new GitOAuthCallbackData(string.IsNullOrWhiteSpace(error) && !string.IsNullOrWhiteSpace(code), code ?? string.Empty, error ?? string.Empty, errorDescription ?? string.Empty);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        query = query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            var key = index >= 0 ? pair[..index] : pair;
            var value = index >= 0 ? pair[(index + 1)..] : string.Empty;
            result[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return result;
    }

    private static async Task WriteOAuthBrowserResponseAsync(Stream stream, bool success, string message, CancellationToken cancellationToken)
    {
        var safe = WebUtility.HtmlEncode(message);
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>ConfigTool Git OAuth</title>" +
                   "<style>body{font-family:Segoe UI,Arial,sans-serif;background:#f8fafc;color:#0f172a;padding:40px}.box{max-width:680px;margin:auto;background:white;border-radius:18px;padding:28px;box-shadow:0 20px 60px rgba(15,23,42,.12)}.ok{color:#16a34a}.err{color:#dc2626}</style></head>" +
                   $"<body><div class=\"box\"><h1 class=\"{(success ? "ok" : "err")}\">{(success ? "OAuth thành công" : "OAuth lỗi")}</h1><p>{safe}</p><p>Cửa sổ này có thể đóng.</p></div></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<GitOAuthTokenData> ExchangeOAuthCodeAsync(GitAccountDto account, string code, string redirectUri, string verifier, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ConfigTool-GitAdmin/1.0");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var values = new Dictionary<string, string>
        {
            ["client_id"] = account.OAuthClientId ?? string.Empty,
            ["code"] = code,
            ["redirect_uri"] = redirectUri
        };

        string tokenUrl;
        if (account.Provider.Equals("gitlab", StringComparison.OrdinalIgnoreCase))
        {
            tokenUrl = BuildGitLabWebBaseUrl(account).TrimEnd('/') + "/oauth/token";
            values["grant_type"] = "authorization_code";
            values["code_verifier"] = verifier;
            if (!string.IsNullOrWhiteSpace(account.OAuthClientSecret))
            {
                values["client_secret"] = account.OAuthClientSecret!;
            }
        }
        else
        {
            tokenUrl = "https://github.com/login/oauth/access_token";
            if (!string.IsNullOrWhiteSpace(account.OAuthClientSecret))
            {
                values["client_secret"] = account.OAuthClientSecret!;
            }
        }

        using var response = await client.PostAsync(tokenUrl, new FormUrlEncodedContent(values), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {TrimForUi(body, 600)}");
        }

        return ParseOAuthTokenResponse(body);
    }

    private static GitOAuthTokenData ParseOAuthTokenResponse(string body)
    {
        if (body.TrimStart().StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return new GitOAuthTokenData(
                TryGetString(root, "access_token"),
                TryGetString(root, "token_type"),
                TryGetString(root, "refresh_token"),
                TryGetInt(root, "expires_in"));
        }

        var query = ParseQuery(body);
        query.TryGetValue("access_token", out var accessToken);
        query.TryGetValue("token_type", out var tokenType);
        query.TryGetValue("refresh_token", out var refreshToken);
        var expires = query.TryGetValue("expires_in", out var expiresText) && int.TryParse(expiresText, out var parsed) ? parsed : 0;
        return new GitOAuthTokenData(accessToken ?? string.Empty, tokenType ?? string.Empty, refreshToken ?? string.Empty, expires);
    }

    private static int TryGetInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number,
            _ => 0
        };
    }

    private static async Task PopulateOAuthProfileAsync(GitAccountDto account, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(account);
            using var response = await client.GetAsync(BuildApiUrl(account, "/user"), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            account.Username = NormalizeNullable(TryGetString(root, account.Provider.Equals("gitlab", StringComparison.OrdinalIgnoreCase) ? "username" : "login")) ?? account.Username;
            account.Email = NormalizeNullable(TryGetString(root, "email")) ?? account.Email;
            var profileName = NormalizeNullable(TryGetString(root, "name"));
            if (!string.IsNullOrWhiteSpace(profileName) && (string.IsNullOrWhiteSpace(account.Name) || account.Name.Equals("GitHub account", StringComparison.OrdinalIgnoreCase) || account.Name.Equals("GitLab account", StringComparison.OrdinalIgnoreCase)))
            {
                account.Name = profileName;
            }
            else if (!string.IsNullOrWhiteSpace(account.Username) && (string.IsNullOrWhiteSpace(account.Name) || account.Name.Contains("account", StringComparison.OrdinalIgnoreCase)))
            {
                account.Name = account.Provider + " - " + account.Username;
            }
        }
        catch
        {
            // Profile enrichment is best-effort. The token has already been obtained.
        }
    }

    private static void OpenBrowser(string url)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        };
        process.Start();
    }

    private static string CreateCodeVerifier() => CreateRandomUrlSafe(64);

    private static string CreateCodeChallenge(string verifier)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(bytes);
    }

    private static string CreateRandomUrlSafe(int bytes)
    {
        var buffer = RandomNumberGenerator.GetBytes(bytes);
        return Base64UrlEncode(buffer);
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static StringContent ToJsonContent(object payload)
        => new(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

    private static GitCreatedRepositoryDto ParseCreatedRepository(string json, string provider)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string clone = string.Empty;
        string html = string.Empty;
        if (provider.Equals("gitlab", StringComparison.OrdinalIgnoreCase))
        {
            clone = TryGetString(root, "http_url_to_repo");
            html = TryGetString(root, "web_url");
        }
        else
        {
            clone = TryGetString(root, "clone_url");
            html = TryGetString(root, "html_url");
        }

        return new GitCreatedRepositoryDto
        {
            CloneUrl = clone,
            HtmlUrl = html
        };
    }

    private static string TryGetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static string BuildApiUrl(GitAccountDto account, string path)
    {
        var baseUrl = NormalizeApiBaseUrl(account.Provider, account.ApiBaseUrl).TrimEnd('/');
        if (account.Provider.Equals("gitlab", StringComparison.OrdinalIgnoreCase) && !baseUrl.EndsWith("/api/v4", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl += "/api/v4";
        }

        return baseUrl + "/" + path.TrimStart('/');
    }

    private static string NormalizeProvider(string? provider)
    {
        var value = (provider ?? "github").Trim().ToLowerInvariant();
        return value == "gitlab" ? "gitlab" : "github";
    }

    private static string NormalizeApiBaseUrl(string provider, string? baseUrl)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            return baseUrl.Trim().TrimEnd('/');
        }

        return provider.Equals("gitlab", StringComparison.OrdinalIgnoreCase)
            ? "https://gitlab.com/api/v4"
            : "https://api.github.com";
    }

    private static string NormalizeVisibility(string? visibility)
    {
        var value = (visibility ?? "private").Trim().ToLowerInvariant();
        return value == "public" ? "public" : "private";
    }

    private static string BuildDefaultAccountName(GitAccountDto account)
    {
        var user = string.IsNullOrWhiteSpace(account.Username) ? "account" : account.Username.Trim();
        return $"{account.Provider} - {user}";
    }

    private static string SanitizeRepoName(string? value)
        => new((value ?? string.Empty).Trim().Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.').ToArray());

    private static string SanitizeRemoteName(string? value)
        => new((value ?? "origin").Trim().Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.').ToArray());

    private static string SanitizeBranchName(string? value)
        => (value ?? string.Empty).Trim().Replace(" ", "-");

    private static string NormalizeFolder(string? folderPath)
        => string.IsNullOrWhiteSpace(folderPath) ? string.Empty : Path.GetFullPath(folderPath.Trim());

    private static string? NormalizeNullable(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string QuoteArg(string? value)
    {
        value ??= string.Empty;
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string MaskToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return text.Replace("ghp_", "ghp_***", StringComparison.OrdinalIgnoreCase)
            .Replace("glpat-", "glpat-***", StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimForUi(string text, int max)
    {
        text = text?.Trim() ?? string.Empty;
        return text.Length <= max ? text : text[..max] + "...";
    }

    private static string ResolveCloneTarget(string parent, string repositoryUrl, string? targetFolderName)
    {
        if (!string.IsNullOrWhiteSpace(targetFolderName))
        {
            return Path.Combine(parent, targetFolderName.Trim());
        }

        var url = repositoryUrl.Trim().TrimEnd('/');
        var last = url.Split(['/', ':'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "repo";
        if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            last = last[..^4];
        }

        return Path.Combine(parent, last);
    }

    private static GitActionResultDto Ok(string message, GitRepositoryStatusDto? repository = null)
        => new() { Success = true, Message = message, Repository = repository };

    private static GitActionResultDto Fail(string message)
        => new() { Success = false, Message = message, ExitCode = -1 };

    private static async Task WriteLogAsync(Func<GitConsoleEntryDto, Task>? logAsync, string level, string scope, string message)
    {
        if (logAsync is null || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        try
        {
            await logAsync(new GitConsoleEntryDto
            {
                Level = level,
                Scope = scope,
                Message = message
            });
        }
        catch
        {
            // Console streaming must never crash git command execution.
        }
    }
    private sealed record GitOAuthCallbackData(bool Success, string Code, string Error, string ErrorDescription);

    private sealed record GitOAuthTokenData(string AccessToken, string TokenType, string RefreshToken, int ExpiresInSeconds);

}
