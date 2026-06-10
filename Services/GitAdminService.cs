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
    public string OAuth2ConfigFilePath => Path.Combine(_pathProvider.ConfigDirectory, "OAuth2.json");

    public async Task<GitConfigResponseDto> LoadConfigAsync(CancellationToken cancellationToken)
    {
        var config = await ReadConfigAsync(cancellationToken);
        return new GitConfigResponseDto
        {
            ConfigFilePath = GitConfigFilePath,
            Config = config
        };
    }

    public async Task<GitOAuth2ConfigDto> LoadOAuth2ConfigAsync(CancellationToken cancellationToken)
    {
        await _configLock.WaitAsync(cancellationToken);
        try
        {
            return await ReadOAuth2ConfigCoreAsync(cancellationToken);
        }
        finally
        {
            _configLock.Release();
        }
    }

    public async Task<GitOAuth2ConfigDto> SaveOAuth2ConfigAsync(GitOAuth2ConfigDto settings, CancellationToken cancellationToken)
    {
        await _configLock.WaitAsync(cancellationToken);
        try
        {
            settings.CallbackPort = NormalizeOAuthRedirectPort(settings.CallbackPort);
            settings.CallbackPath = string.IsNullOrWhiteSpace(settings.CallbackPath) ? "/callback" : settings.CallbackPath.Trim();
            if (!settings.CallbackPath.StartsWith('/')) settings.CallbackPath = "/" + settings.CallbackPath;
            settings.UpdatedAt = DateTimeOffset.Now;
            await WriteOAuth2ConfigCoreAsync(settings, cancellationToken);
            return settings;
        }
        finally
        {
            _configLock.Release();
        }
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


    public async Task<GitRepositoryListResponseDto> ListRepositoriesAsync(GitRepositoryListRequestDto request, Func<GitConsoleEntryDto, Task>? logAsync, CancellationToken cancellationToken)
    {
        var config = await ReadConfigAsync(cancellationToken);
        var account = config.Accounts.FirstOrDefault(x => string.Equals(x.Id, request.AccountId, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            return new GitRepositoryListResponseDto { Success = false, Message = "Chưa chọn GitHub/GitLab account." };
        }

        var pageSize = Math.Clamp(request.PageSize <= 0 ? 50 : request.PageSize, 10, 100);
        var search = (request.Search ?? string.Empty).Trim();
        await WriteLogAsync(logAsync, "info", "api", $"Đang lấy repository list từ {account.Provider}: {account.Name}...");

        try
        {
            using var client = CreateClient(account);
            var items = new List<GitProviderRepositoryDto>();
            if (account.Provider.Equals("gitlab", StringComparison.OrdinalIgnoreCase))
            {
                for (var page = 1; page <= 5; page++)
                {
                    var path = $"/projects?membership=true&simple=true&order_by=last_activity_at&sort=desc&per_page={pageSize}&page={page}";
                    if (!string.IsNullOrWhiteSpace(search))
                    {
                        path += "&search=" + Uri.EscapeDataString(search);
                    }

                    using var response = await client.GetAsync(BuildApiUrl(account, path), cancellationToken);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        return new GitRepositoryListResponseDto
                        {
                            Success = false,
                            Provider = account.Provider,
                            AccountId = account.Id,
                            Message = $"Lấy repository GitLab lỗi HTTP {(int)response.StatusCode}: {TrimForUi(body, 500)}"
                        };
                    }

                    var parsed = ParseGitLabRepositories(body, account.Provider);
                    items.AddRange(parsed);
                    if (parsed.Count < pageSize)
                    {
                        break;
                    }
                }
            }
            else
            {
                for (var page = 1; page <= 5; page++)
                {
                    var path = $"/user/repos?affiliation=owner,collaborator,organization_member&sort=updated&direction=desc&per_page={pageSize}&page={page}";
                    using var response = await client.GetAsync(BuildApiUrl(account, path), cancellationToken);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        return new GitRepositoryListResponseDto
                        {
                            Success = false,
                            Provider = account.Provider,
                            AccountId = account.Id,
                            Message = $"Lấy repository GitHub lỗi HTTP {(int)response.StatusCode}: {TrimForUi(body, 500)}"
                        };
                    }

                    var parsed = ParseGitHubRepositories(body, account.Provider);
                    items.AddRange(parsed);
                    if (parsed.Count < pageSize)
                    {
                        break;
                    }
                }

                if (!string.IsNullOrWhiteSpace(search))
                {
                    items = items.Where(x =>
                            x.FullName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            x.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            x.Description.Contains(search, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }
            }

            items = items
                .GroupBy(x => x.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .OrderByDescending(x => x.UpdatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(x => x.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var item in items)
            {
                item.AccountId = account.Id;
                item.AccountName = account.Name;
                item.Provider = account.Provider;
            }

            await WriteLogAsync(logAsync, "success", "api", $"Đã lấy {items.Count} repository từ {account.Provider}.");
            return new GitRepositoryListResponseDto
            {
                Success = true,
                Message = $"Đã lấy {items.Count} repository.",
                Provider = account.Provider,
                AccountId = account.Id,
                Items = items,
                TotalCount = items.Count,
                IsTruncated = items.Count >= pageSize * 5
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            await WriteLogAsync(logAsync, "error", "api", "Không lấy được repository list: " + ex.Message);
            return new GitRepositoryListResponseDto
            {
                Success = false,
                Provider = account.Provider,
                AccountId = account.Id,
                Message = "Không lấy được repository list: " + ex.Message
            };
        }
    }


    public async Task<GitBatchAccountActionResponseDto> TestAccountsAsync(GitBatchAccountActionRequestDto request, Func<GitConsoleEntryDto, Task>? logAsync, CancellationToken cancellationToken)
    {
        var accountIds = NormalizeAccountIds(request.AccountIds);
        if (accountIds.Count == 0)
        {
            return new GitBatchAccountActionResponseDto { Success = false, Message = "Chưa tick account nào để chạy hàng loạt." };
        }

        var config = await ReadConfigAsync(cancellationToken);
        var accounts = config.Accounts.Where(x => accountIds.Contains(x.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (accounts.Count == 0)
        {
            return new GitBatchAccountActionResponseDto { Success = false, Message = "Không tìm thấy account đã tick trong gitconfig.json." };
        }

        var response = new GitBatchAccountActionResponseDto();
        await RunForAccountsAsync(accounts, request.MaxParallel, async account =>
        {
            var result = await TestAccountAsync(account.Id, cancellationToken);
            await WriteLogAsync(logAsync, result.Success ? "success" : "error", "batch", $"[{account.Provider}] {account.Name}: {result.Message}");
            lock (response.Items)
            {
                response.Items.Add(new GitBatchAccountActionItemDto
                {
                    AccountId = account.Id,
                    AccountName = account.Name,
                    Provider = account.Provider,
                    Success = result.Success,
                    Message = result.Message
                });
            }
        }, cancellationToken);

        response.Success = response.Items.Count > 0 && response.Items.All(x => x.Success);
        response.Message = $"Đã test {response.Items.Count} account: {response.SuccessCount} OK, {response.ErrorCount} lỗi.";
        return response;
    }

    public async Task<GitRepositoryListResponseDto> ListRepositoriesForAccountsAsync(GitBatchRepositoryListRequestDto request, Func<GitConsoleEntryDto, Task>? logAsync, CancellationToken cancellationToken)
    {
        var accountIds = NormalizeAccountIds(request.AccountIds);
        if (accountIds.Count == 0)
        {
            return new GitRepositoryListResponseDto { Success = false, Message = "Chưa tick account nào để lấy repository list." };
        }

        var config = await ReadConfigAsync(cancellationToken);
        var accounts = config.Accounts.Where(x => accountIds.Contains(x.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (accounts.Count == 0)
        {
            return new GitRepositoryListResponseDto { Success = false, Message = "Không tìm thấy account đã tick trong gitconfig.json." };
        }

        var allItems = new List<GitProviderRepositoryDto>();
        var messages = new List<string>();
        var hadError = false;
        await RunForAccountsAsync(accounts, request.MaxParallel, async account =>
        {
            var result = await ListRepositoriesAsync(new GitRepositoryListRequestDto
            {
                AccountId = account.Id,
                Search = request.Search,
                PageSize = request.PageSize <= 0 ? 50 : request.PageSize
            }, logAsync, cancellationToken);

            lock (allItems)
            {
                if (result.Success)
                {
                    foreach (var item in result.Items)
                    {
                        item.AccountId = account.Id;
                        item.AccountName = account.Name;
                        item.Provider = account.Provider;
                        allItems.Add(item);
                    }
                }
                else
                {
                    hadError = true;
                }

                messages.Add($"{account.Provider}/{account.Name}: {result.Message}");
            }
        }, cancellationToken);

        allItems = allItems
            .OrderBy(x => x.AccountName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(x => x.UpdatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(x => x.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new GitRepositoryListResponseDto
        {
            Success = allItems.Count > 0 || !hadError,
            Message = $"Đã lấy {allItems.Count} repository từ {accounts.Count} account." + (hadError ? " Một số account bị lỗi, xem Console log." : string.Empty),
            Provider = accounts.Count == 1 ? accounts[0].Provider : "multi",
            AccountId = string.Join(",", accounts.Select(x => x.Id)),
            Items = allItems,
            TotalCount = allItems.Count,
            IsTruncated = allItems.Count >= Math.Clamp(request.PageSize <= 0 ? 50 : request.PageSize, 10, 100) * 5 * Math.Max(1, accounts.Count)
        };
    }

    public async Task<GitBatchAccountActionResponseDto> CreateRepositoryForAccountsAsync(GitBatchCreateRepositoryRequestDto request, Func<GitConsoleEntryDto, Task>? logAsync, CancellationToken cancellationToken)
    {
        var accountIds = NormalizeAccountIds(request.AccountIds);
        if (accountIds.Count == 0)
        {
            return new GitBatchAccountActionResponseDto { Success = false, Message = "Chưa tick account nào để tạo repository hàng loạt." };
        }

        var config = await ReadConfigAsync(cancellationToken);
        var accounts = config.Accounts.Where(x => accountIds.Contains(x.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (accounts.Count == 0)
        {
            return new GitBatchAccountActionResponseDto { Success = false, Message = "Không tìm thấy account đã tick trong gitconfig.json." };
        }

        var response = new GitBatchAccountActionResponseDto();
        await RunForAccountsAsync(accounts, request.MaxParallel, async account =>
        {
            var result = await CreateRepositoryAsync(new GitCreateRepositoryRequestDto
            {
                AccountId = account.Id,
                Name = request.Name,
                Description = request.Description,
                Visibility = request.Visibility,
                InitializeReadme = request.InitializeReadme
            }, logAsync, cancellationToken);

            lock (response.Items)
            {
                response.Items.Add(new GitBatchAccountActionItemDto
                {
                    AccountId = account.Id,
                    AccountName = account.Name,
                    Provider = account.Provider,
                    Success = result.Success,
                    Message = result.Message + (string.IsNullOrWhiteSpace(result.CloneUrl) ? string.Empty : " " + result.CloneUrl)
                });
            }
        }, cancellationToken);

        response.Success = response.Items.Count > 0 && response.Items.All(x => x.Success);
        response.Message = $"Tạo repository hàng loạt: {response.SuccessCount} OK, {response.ErrorCount} lỗi.";
        return response;
    }

    public async Task<GitTrafficStatsDto> GetRepositoryTrafficAsync(GitTrafficRequestDto request, Func<GitConsoleEntryDto, Task>? logAsync, CancellationToken cancellationToken)
    {
        var config = await ReadConfigAsync(cancellationToken);
        var account = config.Accounts.FirstOrDefault(x => string.Equals(x.Id, request.AccountId, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            return new GitTrafficStatsDto { Success = false, Message = "Chưa chọn GitHub/GitLab account." };
        }

        var fullName = (request.RepositoryFullName ?? string.Empty).Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return new GitTrafficStatsDto { Success = false, Provider = account.Provider, Message = "Chưa chọn repository để lấy traffic." };
        }

        if (account.Provider.Equals("gitlab", StringComparison.OrdinalIgnoreCase))
        {
            await WriteLogAsync(logAsync, "warn", "api", "GitLab API hiện không có traffic views/clones tương đương GitHub cho mọi tài khoản. Chỉ GitHub traffic được vẽ biểu đồ ở patch này.");
            return new GitTrafficStatsDto
            {
                Success = false,
                Provider = account.Provider,
                RepositoryFullName = fullName,
                Message = "GitLab traffic views/clones chưa hỗ trợ đồng nhất. Hãy dùng GitHub repo để lấy views/clones hoặc mở GitLab analytics riêng."
            };
        }

        try
        {
            using var client = CreateClient(account);
            await WriteLogAsync(logAsync, "info", "api", "Đang lấy traffic views/clones cho " + fullName + "...");
            var stats = new GitTrafficStatsDto
            {
                Success = true,
                Provider = account.Provider,
                RepositoryFullName = fullName,
                Message = "Đã lấy traffic repository."
            };

            var views = await GetGitHubTrafficMetricAsync(client, account, fullName, "views", "Visitors / Views", cancellationToken);
            var clones = await GetGitHubTrafficMetricAsync(client, account, fullName, "clones", "Git clones", cancellationToken);
            if (views is not null)
            {
                stats.Metrics.Add(views);
            }
            if (clones is not null)
            {
                stats.Metrics.Add(clones);
            }

            if (stats.Metrics.Count == 0)
            {
                stats.Success = false;
                stats.Message = "Không lấy được traffic. Token cần quyền xem repository traffic hoặc repository không có dữ liệu traffic.";
            }

            await WriteLogAsync(logAsync, stats.Success ? "success" : "warn", "api", stats.Message);
            return stats;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            await WriteLogAsync(logAsync, "error", "api", "Không lấy được traffic: " + ex.Message);
            return new GitTrafficStatsDto
            {
                Success = false,
                Provider = account.Provider,
                RepositoryFullName = fullName,
                Message = "Không lấy được traffic: " + ex.Message
            };
        }
    }

    public async Task<GitWorkspaceResponseDto> LoadWorkspacesAsync(CancellationToken cancellationToken)
    {
        var config = await ReadConfigAsync(cancellationToken);
        NormalizeWorkspaces(config);
        return new GitWorkspaceResponseDto
        {
            ConfigFilePath = GitConfigFilePath,
            Items = config.Workspaces.OrderBy(x => x.AccountName).ThenBy(x => x.RepositoryFullName).ThenBy(x => x.BranchName).ToList()
        };
    }

    public async Task<GitActionResultDto> SaveWorkspaceAsync(GitWorkspaceDto workspace, CancellationToken cancellationToken)
    {
        await _configLock.WaitAsync(cancellationToken);
        try
        {
            var config = await ReadConfigCoreAsync(cancellationToken);
            NormalizeWorkspace(workspace);
            var account = config.Accounts.FirstOrDefault(x => string.Equals(x.Id, workspace.AccountId, StringComparison.OrdinalIgnoreCase));
            if (account is not null)
            {
                workspace.AccountName = account.Name;
                workspace.Provider = account.Provider;
            }

            var existing = config.Workspaces.FirstOrDefault(x => string.Equals(x.Id, workspace.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                workspace.Id = string.IsNullOrWhiteSpace(workspace.Id) ? Guid.NewGuid().ToString("N") : workspace.Id;
                config.Workspaces.Add(workspace);
            }
            else
            {
                existing.AccountId = workspace.AccountId;
                existing.AccountName = workspace.AccountName;
                existing.Provider = workspace.Provider;
                existing.RepositoryFullName = workspace.RepositoryFullName;
                existing.RepositoryId = workspace.RepositoryId;
                existing.CloneUrl = workspace.CloneUrl;
                existing.HtmlUrl = workspace.HtmlUrl;
                existing.RemoteName = workspace.RemoteName;
                existing.BranchName = workspace.BranchName;
                existing.FolderPath = workspace.FolderPath;
                existing.UpdatedAt = DateTimeOffset.Now;
            }

            NormalizeWorkspaces(config);
            await WriteConfigCoreAsync(config, cancellationToken);
            return Ok("Đã lưu cấu hình account/repo/branch/folder vào gitconfig.json.");
        }
        finally
        {
            _configLock.Release();
        }
    }

    public async Task<GitActionResultDto> DeleteWorkspaceAsync(string workspaceId, CancellationToken cancellationToken)
    {
        await _configLock.WaitAsync(cancellationToken);
        try
        {
            var config = await ReadConfigCoreAsync(cancellationToken);
            var removed = config.Workspaces.RemoveAll(x => string.Equals(x.Id, workspaceId, StringComparison.OrdinalIgnoreCase));
            await WriteConfigCoreAsync(config, cancellationToken);
            return removed > 0 ? Ok("Đã xóa workspace Git.") : Fail("Không tìm thấy workspace cần xóa.");
        }
        finally
        {
            _configLock.Release();
        }
    }

    public async Task<GitBatchAccountActionResponseDto> RunWorkspaceActionsAsync(GitBatchWorkspaceActionRequestDto request, Func<GitConsoleEntryDto, Task>? logAsync, CancellationToken cancellationToken)
    {
        var ids = request.WorkspaceIds?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        if (ids.Count == 0)
        {
            return new GitBatchAccountActionResponseDto { Success = false, Message = "Chưa chọn workspace account/repo/branch nào." };
        }

        var config = await ReadConfigAsync(cancellationToken);
        NormalizeWorkspaces(config);
        var workspaces = config.Workspaces.Where(x => ids.Contains(x.Id)).ToList();
        var response = new GitBatchAccountActionResponseDto();
        var action = (request.Action ?? "status").Trim().ToLowerInvariant();
        await RunForWorkspacesAsync(workspaces, request.MaxParallel, async workspace =>
        {
            var item = new GitBatchAccountActionItemDto
            {
                AccountId = workspace.AccountId,
                AccountName = string.IsNullOrWhiteSpace(workspace.AccountName) ? workspace.RepositoryFullName : $"{workspace.AccountName} / {workspace.RepositoryFullName}",
                Provider = workspace.Provider,
            };
            try
            {
                GitActionResultDto result;
                if (action == "clone")
                {
                    if (string.IsNullOrWhiteSpace(workspace.CloneUrl) || string.IsNullOrWhiteSpace(workspace.FolderPath))
                    {
                        result = Fail("Workspace thiếu CloneUrl hoặc FolderPath.");
                    }
                    else
                    {
                        var parent = Directory.GetParent(workspace.FolderPath)?.FullName ?? workspace.FolderPath;
                        var target = Path.GetFileName(workspace.FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        result = await CloneAsync(new GitCloneRequestDto
                        {
                            ParentFolderPath = parent,
                            RepositoryUrl = workspace.CloneUrl,
                            TargetFolderName = target
                        }, logAsync, cancellationToken);
                    }
                }
                else if (action == "pull")
                {
                    result = await PullAsync(new GitPushPullRequestDto
                    {
                        FolderPath = workspace.FolderPath,
                        RemoteName = string.IsNullOrWhiteSpace(workspace.RemoteName) ? "origin" : workspace.RemoteName,
                        BranchName = workspace.BranchName,
                    }, logAsync, cancellationToken);
                }
                else if (action == "push")
                {
                    result = await PushAsync(new GitPushPullRequestDto
                    {
                        FolderPath = workspace.FolderPath,
                        RemoteName = string.IsNullOrWhiteSpace(workspace.RemoteName) ? "origin" : workspace.RemoteName,
                        BranchName = workspace.BranchName,
                        SetUpstream = true
                    }, logAsync, cancellationToken);
                }
                else
                {
                    var status = await GetRepositoryStatusAsync(workspace.FolderPath, cancellationToken);
                    result = new GitActionResultDto
                    {
                        Success = status.FolderExists && status.IsGitRepository,
                        Message = status.IsGitRepository ? $"OK: {workspace.RepositoryFullName} [{status.CurrentBranch}] changes={status.Changes.Count}" : "Folder chưa phải Git repository.",
                        Repository = status
                    };
                }

                item.Success = result.Success;
                item.Message = result.Message;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                item.Success = false;
                item.Message = ex.Message;
            }

            lock (response.Items)
            {
                response.Items.Add(item);
            }
        }, cancellationToken);

        response.Success = response.Items.Count > 0 && response.Items.All(x => x.Success);
        response.Message = $"Multi workspace {action}: {response.SuccessCount} OK, {response.ErrorCount} lỗi.";
        return response;
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
        => await GetFileExplorerAsync(folderPath, search, 1, 50, true, cancellationToken);

    public async Task<GitFileExplorerDto> GetFileExplorerAsync(string folderPath, string? search, int page, int pageSize, bool includeCommitInfo, CancellationToken cancellationToken)
        => await GetFileExplorerFolderAsync(folderPath, string.Empty, search, page, pageSize, includeCommitInfo, cancellationToken);

    public async Task<GitFileExplorerDto> GetFileExplorerFolderAsync(string folderPath, string? currentRelativePath, string? search, int page, int pageSize, bool includeCommitInfo, CancellationToken cancellationToken)
    {
        var folder = NormalizeFolder(folderPath);
        var normalizedPageSize = NormalizePageSize(pageSize);
        var normalizedPage = Math.Max(1, page);
        var current = NormalizeRelativePath(currentRelativePath);
        var currentFullPath = ResolveSafePath(folder, current);
        var normalizedSearch = (search ?? string.Empty).Trim();

        var result = new GitFileExplorerDto
        {
            FolderPath = folder,
            CurrentRelativePath = current,
            ParentRelativePath = GetParentRelativePath(current),
            CanGoParent = !string.IsNullOrWhiteSpace(current),
            Breadcrumbs = BuildBreadcrumbs(current),
            Search = normalizedSearch,
            Page = normalizedPage,
            PageSize = normalizedPageSize,
            TotalPages = 1
        };

        if (!Directory.Exists(folder) || !Directory.Exists(currentFullPath))
        {
            return result;
        }

        var statusMap = await BuildStatusMapAsync(folder, true, cancellationToken);
        var candidates = new List<GitLocalFileDto>();
        var comparison = StringComparison.OrdinalIgnoreCase;
        var ignored = 0;

        var entries = string.IsNullOrWhiteSpace(normalizedSearch)
            ? Directory.EnumerateFileSystemEntries(currentFullPath, "*", SearchOption.TopDirectoryOnly)
            : Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories);

        foreach (var entry in entries)
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
            }

            var isDirectory = Directory.Exists(entry);
            var fileInfo = isDirectory ? null : new FileInfo(entry);
            var dirInfo = isDirectory ? new DirectoryInfo(entry) : null;
            candidates.Add(new GitLocalFileDto
            {
                Name = Path.GetFileName(entry),
                RelativePath = relative,
                FullPath = entry,
                IsDirectory = isDirectory,
                GitStatus = status,
                IsIgnored = isIgnored,
                IsTracked = IsTrackedStatus(status),
                CanStage = !isIgnored,
                SizeBytes = fileInfo?.Length ?? 0,
                LastWriteTime = isDirectory ? new DateTimeOffset(dirInfo!.LastWriteTime) : new DateTimeOffset(fileInfo!.LastWriteTime)
            });
        }

        var ordered = candidates
            .OrderByDescending(x => x.IsDirectory)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var total = ordered.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)normalizedPageSize));
        normalizedPage = Math.Clamp(normalizedPage, 1, totalPages);
        var pageItems = ordered.Skip((normalizedPage - 1) * normalizedPageSize).Take(normalizedPageSize).ToList();
        if (includeCommitInfo && Directory.Exists(Path.Combine(folder, ".git")))
        {
            await EnrichLocalCommitInfoAsync(folder, pageItems, cancellationToken);
        }

        result.Items = pageItems;
        result.TotalCount = total;
        result.IgnoredCount = ignored;
        result.IsTruncated = false;
        result.Page = normalizedPage;
        result.PageSize = normalizedPageSize;
        result.TotalPages = totalPages;
        return result;
    }

    public async Task<GitRemoteExplorerDto> GetRemoteExplorerAsync(string folderPath, string? remoteName, string? branchName, string? search, CancellationToken cancellationToken)
        => await GetRemoteExplorerAsync(folderPath, remoteName, branchName, string.Empty, search, 1, 50, true, cancellationToken);

    public async Task<GitRemoteExplorerDto> GetRemoteExplorerAsync(string folderPath, string? remoteName, string? branchName, string? search, int page, int pageSize, bool includeCommitInfo, CancellationToken cancellationToken)
        => await GetRemoteExplorerAsync(folderPath, remoteName, branchName, string.Empty, search, page, pageSize, includeCommitInfo, cancellationToken);

    public async Task<GitRemoteExplorerDto> GetRemoteExplorerAsync(string folderPath, string? remoteName, string? branchName, string? currentRelativePath, string? search, int page, int pageSize, bool includeCommitInfo, CancellationToken cancellationToken)
    {
        var folder = NormalizeFolder(folderPath);
        var normalizedPageSize = NormalizePageSize(pageSize);
        var normalizedPage = Math.Max(1, page);
        var remote = string.IsNullOrWhiteSpace(remoteName) ? "origin" : remoteName.Trim();
        var branch = string.IsNullOrWhiteSpace(branchName) ? await GetCurrentBranchNameAsync(folder, cancellationToken) : branchName.Trim();
        var current = NormalizeRelativePath(currentRelativePath);
        var normalizedSearch = (search ?? string.Empty).Trim();
        var result = new GitRemoteExplorerDto
        {
            FolderPath = folder,
            RemoteName = remote,
            BranchName = branch,
            CurrentRelativePath = current,
            ParentRelativePath = GetParentRelativePath(current),
            CanGoParent = !string.IsNullOrWhiteSpace(current),
            Breadcrumbs = BuildBreadcrumbs(current),
            Search = normalizedSearch,
            Page = normalizedPage,
            PageSize = normalizedPageSize,
            TotalPages = 1
        };

        if (string.IsNullOrWhiteSpace(branch))
        {
            return result;
        }

        var sourceRef = remote.Equals("HEAD", StringComparison.OrdinalIgnoreCase) ? "HEAD" : $"{remote}/{branch}";
        result.SourceRef = sourceRef;

        var recursive = !string.IsNullOrWhiteSpace(normalizedSearch);
        var objectSpec = BuildGitTreeObjectSpec(sourceRef, current);
        var treeArgs = recursive
            ? "ls-tree -r --long " + QuoteArg(objectSpec)
            : "ls-tree --long " + QuoteArg(objectSpec);
        var tree = await RunGitUnlockedAsync(folder, treeArgs, null, cancellationToken);
        if (!tree.Success && !sourceRef.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            sourceRef = branch;
            result.SourceRef = sourceRef;
            objectSpec = BuildGitTreeObjectSpec(sourceRef, current);
            treeArgs = recursive
                ? "ls-tree -r --long " + QuoteArg(objectSpec)
                : "ls-tree --long " + QuoteArg(objectSpec);
            tree = await RunGitUnlockedAsync(folder, treeArgs, null, cancellationToken);
        }

        if (!tree.Success)
        {
            return result;
        }

        var comparison = StringComparison.OrdinalIgnoreCase;
        var all = ParseLsTreeLong(tree.Output, current)
            .Where(item => string.IsNullOrWhiteSpace(normalizedSearch) || item.Path.IndexOf(normalizedSearch, comparison) >= 0 || item.Name.IndexOf(normalizedSearch, comparison) >= 0)
            .OrderByDescending(x => string.Equals(x.ObjectType, "tree", StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var total = all.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)normalizedPageSize));
        normalizedPage = Math.Clamp(normalizedPage, 1, totalPages);
        var pageItems = all.Skip((normalizedPage - 1) * normalizedPageSize).Take(normalizedPageSize).ToList();
        if (includeCommitInfo)
        {
            await EnrichRemoteCommitInfoAsync(folder, sourceRef, pageItems, cancellationToken);
        }

        var remoteUrl = await GetRemoteUrlAsync(folder, remote, cancellationToken);
        foreach (var item in pageItems.Where(x => string.Equals(x.ObjectType, "blob", StringComparison.OrdinalIgnoreCase)))
        {
            item.RawUrl = BuildRawFileUrl(remoteUrl, branch, item.Path);
        }

        result.Items = pageItems;
        result.TotalCount = total;
        result.DirectoryCount = all.Count(x => string.Equals(x.ObjectType, "tree", StringComparison.OrdinalIgnoreCase));
        result.FileCount = all.Count(x => string.Equals(x.ObjectType, "blob", StringComparison.OrdinalIgnoreCase));
        result.IsTruncated = false;
        result.Page = normalizedPage;
        result.PageSize = normalizedPageSize;
        result.TotalPages = totalPages;
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

    public async Task<GitActionResultDto> UpdateGitIgnoreRuleAsync(GitIgnoreRuleRequestDto request, CancellationToken cancellationToken)
    {
        var folder = NormalizeFolder(request.FolderPath);
        if (!Directory.Exists(folder))
        {
            return Fail("Thư mục dự án không tồn tại.");
        }

        var relative = NormalizeRelativePath(request.RelativePath);
        if (string.IsNullOrWhiteSpace(relative))
        {
            return Fail("Path cần thêm/bỏ .gitignore không hợp lệ.");
        }

        var gate = GetRepoLock(folder);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var gitIgnorePath = Path.Combine(folder, ".gitignore");
            var existing = File.Exists(gitIgnorePath)
                ? await File.ReadAllTextAsync(gitIgnorePath, Encoding.UTF8, cancellationToken)
                : string.Empty;

            var pattern = BuildGitIgnorePattern(relative, request.IsDirectory);
            var lines = existing.Split(['\r', '\n'], StringSplitOptions.None).ToList();
            var before = lines.Count;
            lines = lines.Where(line => !IsSameGitIgnorePattern(line, pattern, relative)).ToList();

            if (request.AddToIgnore)
            {
                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines.Last()))
                {
                    lines.Add(string.Empty);
                }
                lines.Add(pattern);
                lines.Add(string.Empty);
            }

            var content = string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
            await File.WriteAllTextAsync(gitIgnorePath, content, new UTF8Encoding(false), cancellationToken);
            var action = request.AddToIgnore ? "Đã thêm vào .gitignore" : "Đã loại bỏ khỏi .gitignore";
            return Ok($"{action}: {pattern}", await GetRepositoryStatusAsync(folder, cancellationToken));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail("Không cập nhật được .gitignore: " + ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GitFilePreviewDto> GetLocalFilePreviewAsync(string folderPath, string relativePath, CancellationToken cancellationToken)
    {
        var folder = NormalizeFolder(folderPath);
        var relative = NormalizeRelativePath(relativePath);
        var fullPath = ResolveSafePath(folder, relative);
        var preview = new GitFilePreviewDto
        {
            FolderPath = folder,
            RelativePath = relative,
            DisplayName = Path.GetFileName(relative),
            RawUrl = new Uri(fullPath).AbsoluteUri,
            EmbedUrl = new Uri(fullPath).AbsoluteUri
        };

        if (!File.Exists(fullPath))
        {
            preview.Success = false;
            preview.Message = Directory.Exists(fullPath) ? "Đây là thư mục, không preview raw text." : "File không tồn tại.";
            preview.IsDirectory = Directory.Exists(fullPath);
            return preview;
        }

        var info = new FileInfo(fullPath);
        preview.SizeBytes = info.Length;
        preview.Language = GuessLanguage(relative);
        var read = await ReadTextPreviewAsync(fullPath, 512 * 1024, cancellationToken);
        preview.Content = read.Text;
        preview.IsTruncated = read.Truncated;
        preview.Success = true;
        preview.Message = read.Truncated ? "Đã preview 512KB đầu file." : "Đã đọc file raw.";

        if (Directory.Exists(Path.Combine(folder, ".git")))
        {
            var item = new GitLocalFileDto { RelativePath = relative };
            await EnrichLocalCommitInfoAsync(folder, [item], cancellationToken);
            preview.LastCommitHash = item.LastCommitHash;
            preview.LastCommitMessage = item.LastCommitMessage;
            preview.LastCommitAt = item.LastCommitAt;
        }

        return preview;
    }

    public async Task<GitFilePreviewDto> GetRemoteFilePreviewAsync(string folderPath, string? remoteName, string? branchName, string relativePath, CancellationToken cancellationToken)
    {
        var folder = NormalizeFolder(folderPath);
        var remote = string.IsNullOrWhiteSpace(remoteName) ? "origin" : remoteName.Trim();
        var branch = string.IsNullOrWhiteSpace(branchName) ? await GetCurrentBranchNameAsync(folder, cancellationToken) : branchName.Trim();
        var relative = NormalizeRelativePath(relativePath);
        var sourceRef = remote.Equals("HEAD", StringComparison.OrdinalIgnoreCase) ? "HEAD" : $"{remote}/{branch}";

        var preview = new GitFilePreviewDto
        {
            FolderPath = folder,
            RelativePath = relative,
            DisplayName = Path.GetFileName(relative),
            IsRemote = true,
            Language = GuessLanguage(relative)
        };

        var remoteUrl = await GetRemoteUrlAsync(folder, remote, cancellationToken);
        preview.RawUrl = BuildRawFileUrl(remoteUrl, branch, relative);
        preview.EmbedUrl = preview.RawUrl;

        var show = await RunGitUnlockedAsync(folder, "show " + QuoteArg(sourceRef + ":" + relative), null, cancellationToken);
        if (!show.Success && !sourceRef.Equals(branch, StringComparison.OrdinalIgnoreCase))
        {
            sourceRef = branch;
            show = await RunGitUnlockedAsync(folder, "show " + QuoteArg(sourceRef + ":" + relative), null, cancellationToken);
        }

        if (!show.Success)
        {
            preview.Success = false;
            preview.Message = string.IsNullOrWhiteSpace(show.Output) ? "Không đọc được raw remote file." : show.Output.Trim();
            return preview;
        }

        preview.Success = true;
        preview.Content = LimitText(show.Output, 512 * 1024, out var truncated);
        preview.IsTruncated = truncated;
        preview.SizeBytes = Encoding.UTF8.GetByteCount(show.Output);
        preview.Message = truncated ? "Đã preview 512KB đầu raw remote file." : "Đã đọc raw remote file.";

        var item = new GitRemoteFileDto { Path = relative };
        await EnrichRemoteCommitInfoAsync(folder, sourceRef, [item], cancellationToken);
        preview.LastCommitHash = item.LastCommitHash;
        preview.LastCommitMessage = item.LastCommitMessage;
        preview.LastCommitAt = item.LastCommitAt;
        return preview;
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



    private static List<string> NormalizeAccountIds(IEnumerable<string>? accountIds)
    {
        return accountIds?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
    }

    private static async Task RunForAccountsAsync(IEnumerable<GitAccountDto> accounts, int maxParallel, Func<GitAccountDto, Task> action, CancellationToken cancellationToken)
    {
        var parallel = Math.Clamp(maxParallel <= 0 ? 4 : maxParallel, 1, 8);
        using var throttler = new SemaphoreSlim(parallel, parallel);
        var tasks = accounts.Select(async account =>
        {
            await throttler.WaitAsync(cancellationToken);
            try
            {
                await action(account);
            }
            finally
            {
                throttler.Release();
            }
        });

        await Task.WhenAll(tasks);
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
        var config = await JsonSerializer.DeserializeAsync<GitConfigFileDto>(stream, JsonOptions, cancellationToken) ?? new GitConfigFileDto();
        NormalizeWorkspaces(config);
        return config;
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

    private static List<GitRemoteFileDto> ParseLsTreeLong(string output, string? baseRelativePath = null)
    {
        var basePath = NormalizeRelativePath(baseRelativePath);
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
            var path = CombineRemotePath(basePath, line[(tabIndex + 1)..].Trim());
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

    private static string BuildGitTreeObjectSpec(string sourceRef, string? currentRelativePath)
    {
        var current = NormalizeRelativePath(currentRelativePath);
        return string.IsNullOrWhiteSpace(current) ? sourceRef : sourceRef + ":" + current;
    }

    private static string CombineRemotePath(string? baseRelativePath, string? nameOrPath)
    {
        var current = NormalizeRelativePath(baseRelativePath);
        var child = (nameOrPath ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(child))
        {
            return current;
        }

        return string.IsNullOrWhiteSpace(current) ? child : current + "/" + child;
    }

    private static string NormalizeRelativePath(string? value)
    {
        var path = (value ?? string.Empty).Trim().Trim('"').Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return string.Empty;
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Any(x => x == "..") || parts.Any(x => x.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            return string.Empty;
        }

        return string.Join('/', parts);
    }

    private static string ResolveSafePath(string folder, string? relativePath)
    {
        var root = Path.GetFullPath(folder);
        var normalized = NormalizeRelativePath(relativePath);
        var full = string.IsNullOrWhiteSpace(normalized)
            ? root
            : Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSlash = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Path nằm ngoài thư mục project.");
        }

        return full;
    }

    private static string GetParentRelativePath(string? currentRelativePath)
    {
        var current = NormalizeRelativePath(currentRelativePath);
        if (string.IsNullOrWhiteSpace(current))
        {
            return string.Empty;
        }

        var index = current.LastIndexOf('/');
        return index < 0 ? string.Empty : current[..index];
    }

    private static List<GitExplorerBreadcrumbDto> BuildBreadcrumbs(string? currentRelativePath)
    {
        var current = NormalizeRelativePath(currentRelativePath);
        var result = new List<GitExplorerBreadcrumbDto>
        {
            new() { Name = "Project", RelativePath = string.Empty }
        };

        if (string.IsNullOrWhiteSpace(current))
        {
            return result;
        }

        var parts = current.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var path = string.Empty;
        foreach (var part in parts)
        {
            path = string.IsNullOrWhiteSpace(path) ? part : path + "/" + part;
            result.Add(new GitExplorerBreadcrumbDto { Name = part, RelativePath = path });
        }

        return result;
    }

    private async Task<string> GetRemoteUrlAsync(string folder, string remoteName, CancellationToken cancellationToken)
    {
        var remote = string.IsNullOrWhiteSpace(remoteName) ? "origin" : remoteName.Trim();
        var result = await RunGitUnlockedAsync(folder, "remote get-url " + QuoteArg(remote), null, cancellationToken);
        return result.Success ? result.Output.Trim() : string.Empty;
    }

    private static string BuildRawFileUrl(string remoteUrl, string branch, string path)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl) || string.IsNullOrWhiteSpace(branch) || string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = remoteUrl.Trim();
        if (normalized.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "https://github.com/" + normalized["git@github.com:".Length..];
        }
        else if (normalized.StartsWith("git@gitlab.com:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "https://gitlab.com/" + normalized["git@gitlab.com:".Length..];
        }

        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return string.Empty;
        }

        var encodedBranch = EscapePathPart(branch);
        var encodedPath = string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(EscapePathPart));
        var ownerRepo = string.Join('/', segments.Select(Uri.UnescapeDataString));

        if (uri.Host.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://raw.githubusercontent.com/{ownerRepo}/{encodedBranch}/{encodedPath}";
        }

        if (uri.Host.Contains("gitlab", StringComparison.OrdinalIgnoreCase))
        {
            return $"{uri.Scheme}://{uri.Host}/{ownerRepo}/-/raw/{encodedBranch}/{encodedPath}";
        }

        return string.Empty;
    }

    private static string EscapePathPart(string value) => Uri.EscapeDataString(value ?? string.Empty).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);

    private static string GuessLanguage(string relativePath)
    {
        var ext = Path.GetExtension(relativePath).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "cs" => "csharp",
            "razor" => "razor",
            "js" => "javascript",
            "ts" => "typescript",
            "json" => "json",
            "css" => "css",
            "scss" => "scss",
            "html" or "htm" => "html",
            "xml" => "xml",
            "yml" or "yaml" => "yaml",
            "md" => "markdown",
            "sql" => "sql",
            "php" => "php",
            "py" => "python",
            "java" => "java",
            _ => ext
        };
    }

    private static string LimitText(string text, int maxChars, out bool truncated)
    {
        text ??= string.Empty;
        truncated = text.Length > maxChars;
        return truncated ? text[..maxChars] + $"\n\n--- Nội dung bị cắt để tránh lag UI. Chỉ hiển thị {maxChars:N0} ký tự đầu. ---" : text;
    }

    private static string BuildGitIgnorePattern(string relativePath, bool isDirectory)
    {
        var normalized = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return isDirectory && !normalized.EndsWith("/", StringComparison.Ordinal) ? normalized + "/" : normalized;
    }

    private static bool IsSameGitIgnorePattern(string line, string pattern, string relativePath)
    {
        var value = (line ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('#'))
        {
            return false;
        }

        value = value.TrimStart('/');
        var cleanPattern = (pattern ?? string.Empty).Trim().TrimStart('/');
        var cleanRelative = NormalizeRelativePath(relativePath);
        return string.Equals(value.TrimEnd('/'), cleanPattern.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value.TrimEnd('/'), cleanRelative.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
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

    private static int NormalizePageSize(int pageSize) => Math.Clamp(pageSize <= 0 ? 50 : pageSize, 10, 200);

    private async Task EnrichLocalCommitInfoAsync(string folder, IEnumerable<GitLocalFileDto> items, CancellationToken cancellationToken)
    {
        foreach (var item in items.Where(x => !string.IsNullOrWhiteSpace(x.RelativePath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var commit = await GetLastCommitForPathAsync(folder, "HEAD", item.RelativePath, cancellationToken);
            item.LastCommitHash = commit.Hash;
            item.LastCommitMessage = commit.Message;
            item.LastCommitAt = commit.At;
        }
    }

    private async Task EnrichRemoteCommitInfoAsync(string folder, string sourceRef, IEnumerable<GitRemoteFileDto> items, CancellationToken cancellationToken)
    {
        foreach (var item in items.Where(x => !string.IsNullOrWhiteSpace(x.Path)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var commit = await GetLastCommitForPathAsync(folder, sourceRef, item.Path, cancellationToken);
            item.LastCommitHash = commit.Hash;
            item.LastCommitMessage = commit.Message;
            item.LastCommitAt = commit.At;
        }
    }

    private async Task<(string Hash, DateTimeOffset? At, string Message)> GetLastCommitForPathAsync(string folder, string refName, string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(Path.Combine(folder, ".git")))
        {
            return (string.Empty, null, string.Empty);
        }

        var args = "log -1 --format=%h%x09%ci%x09%s " + QuoteArg(refName) + " -- " + QuoteArg(path.Replace('\\', '/'));
        var result = await RunGitUnlockedAsync(folder, args, null, cancellationToken);
        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
        {
            return (string.Empty, null, string.Empty);
        }

        var line = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var parts = line.Split('\t', 3);
        if (parts.Length < 3)
        {
            return (string.Empty, null, line.Trim());
        }

        DateTimeOffset? at = DateTimeOffset.TryParse(parts[1], out var parsed) ? parsed : null;
        return (parts[0], at, parts[2]);
    }

    private async Task<GitOAuth2ConfigDto> ReadOAuth2ConfigCoreAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_pathProvider.ConfigDirectory);
        if (!File.Exists(OAuth2ConfigFilePath))
        {
            var fresh = new GitOAuth2ConfigDto();
            await WriteOAuth2ConfigCoreAsync(fresh, cancellationToken);
            return fresh;
        }

        try
        {
            await using var stream = File.Open(OAuth2ConfigFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var settings = await JsonSerializer.DeserializeAsync<GitOAuth2ConfigDto>(stream, JsonOptions, cancellationToken) ?? new GitOAuth2ConfigDto();
            settings.CallbackPort = NormalizeOAuthRedirectPort(settings.CallbackPort);
            settings.CallbackPath = string.IsNullOrWhiteSpace(settings.CallbackPath) ? "/callback" : settings.CallbackPath.Trim();
            if (!settings.CallbackPath.StartsWith('/')) settings.CallbackPath = "/" + settings.CallbackPath;
            return settings;
        }
        catch
        {
            var fresh = new GitOAuth2ConfigDto();
            await WriteOAuth2ConfigCoreAsync(fresh, cancellationToken);
            return fresh;
        }
    }

    private async Task WriteOAuth2ConfigCoreAsync(GitOAuth2ConfigDto settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_pathProvider.ConfigDirectory);
        settings.CallbackPort = NormalizeOAuthRedirectPort(settings.CallbackPort);
        settings.UpdatedAt = DateTimeOffset.Now;
        var tempPath = OAuth2ConfigFilePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
        }
        if (File.Exists(OAuth2ConfigFilePath)) File.Delete(OAuth2ConfigFilePath);
        File.Move(tempPath, OAuth2ConfigFilePath);
    }

    private static void NormalizeWorkspace(GitWorkspaceDto workspace)
    {
        workspace.Id = string.IsNullOrWhiteSpace(workspace.Id) ? Guid.NewGuid().ToString("N") : workspace.Id.Trim();
        workspace.AccountId = workspace.AccountId?.Trim() ?? string.Empty;
        workspace.AccountName = workspace.AccountName?.Trim() ?? string.Empty;
        workspace.Provider = NormalizeProvider(workspace.Provider);
        workspace.RepositoryFullName = workspace.RepositoryFullName?.Trim().Trim('/') ?? string.Empty;
        workspace.RepositoryId = workspace.RepositoryId?.Trim() ?? string.Empty;
        workspace.CloneUrl = workspace.CloneUrl?.Trim() ?? string.Empty;
        workspace.HtmlUrl = workspace.HtmlUrl?.Trim() ?? string.Empty;
        workspace.RemoteName = string.IsNullOrWhiteSpace(workspace.RemoteName) ? "origin" : workspace.RemoteName.Trim();
        workspace.BranchName = workspace.BranchName?.Trim() ?? string.Empty;
        workspace.FolderPath = NormalizeNullable(workspace.FolderPath) ?? string.Empty;
        workspace.UpdatedAt = DateTimeOffset.Now;
    }

    private static void NormalizeWorkspaces(GitConfigFileDto config)
    {
        config.Workspaces ??= [];
        foreach (var workspace in config.Workspaces)
        {
            NormalizeWorkspace(workspace);
        }
    }

    private async Task RunForWorkspacesAsync(IReadOnlyList<GitWorkspaceDto> workspaces, int maxParallel, Func<GitWorkspaceDto, Task> action, CancellationToken cancellationToken)
    {
        using var semaphore = new SemaphoreSlim(Math.Clamp(maxParallel <= 0 ? 3 : maxParallel, 1, 6), Math.Clamp(maxParallel <= 0 ? 3 : maxParallel, 1, 6));
        var tasks = workspaces.Select(async workspace =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try { await action(workspace); }
            finally { semaphore.Release(); }
        });
        await Task.WhenAll(tasks);
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


    private static List<GitProviderRepositoryDto> ParseGitHubRepositories(string json, string provider)
    {
        var result = new List<GitProviderRepositoryDto>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var owner = item.TryGetProperty("owner", out var ownerElement) ? TryGetString(ownerElement, "login") : string.Empty;
            result.Add(new GitProviderRepositoryDto
            {
                Provider = provider,
                Id = TryGetRawString(item, "id"),
                Name = TryGetString(item, "name"),
                FullName = TryGetString(item, "full_name"),
                OwnerName = owner,
                Description = TryGetString(item, "description"),
                Visibility = TryGetString(item, "visibility"),
                IsPrivate = TryGetBool(item, "private"),
                DefaultBranch = TryGetString(item, "default_branch"),
                CloneUrl = TryGetString(item, "clone_url"),
                SshUrl = TryGetString(item, "ssh_url"),
                HtmlUrl = TryGetString(item, "html_url"),
                StarCount = TryGetLong(item, "stargazers_count"),
                ForkCount = TryGetLong(item, "forks_count"),
                OpenIssueCount = TryGetLong(item, "open_issues_count"),
                UpdatedAt = TryGetDateTimeOffset(item, "updated_at")
            });
        }

        return result.Where(x => !string.IsNullOrWhiteSpace(x.FullName)).ToList();
    }

    private static List<GitProviderRepositoryDto> ParseGitLabRepositories(string json, string provider)
    {
        var result = new List<GitProviderRepositoryDto>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var namespaceName = item.TryGetProperty("namespace", out var ns) ? TryGetString(ns, "full_path") : string.Empty;
            var visibility = TryGetString(item, "visibility");
            result.Add(new GitProviderRepositoryDto
            {
                Provider = provider,
                Id = TryGetRawString(item, "id"),
                Name = TryGetString(item, "name"),
                FullName = TryGetString(item, "path_with_namespace"),
                OwnerName = namespaceName,
                Description = TryGetString(item, "description"),
                Visibility = visibility,
                IsPrivate = !visibility.Equals("public", StringComparison.OrdinalIgnoreCase),
                DefaultBranch = TryGetString(item, "default_branch"),
                CloneUrl = TryGetString(item, "http_url_to_repo"),
                SshUrl = TryGetString(item, "ssh_url_to_repo"),
                HtmlUrl = TryGetString(item, "web_url"),
                StarCount = TryGetLong(item, "star_count"),
                ForkCount = TryGetLong(item, "forks_count"),
                OpenIssueCount = TryGetLong(item, "open_issues_count"),
                UpdatedAt = TryGetDateTimeOffset(item, "last_activity_at") ?? TryGetDateTimeOffset(item, "updated_at")
            });
        }

        return result.Where(x => !string.IsNullOrWhiteSpace(x.FullName)).ToList();
    }

    private static async Task<GitTrafficMetricDto?> GetGitHubTrafficMetricAsync(HttpClient client, GitAccountDto account, string fullName, string kind, string title, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(BuildApiUrl(account, $"/repos/{fullName}/traffic/{kind}"), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var metric = new GitTrafficMetricDto
        {
            Kind = kind,
            Title = title,
            Count = TryGetLong(root, "count"),
            Uniques = TryGetLong(root, "uniques")
        };

        var arrayName = kind.Equals("clones", StringComparison.OrdinalIgnoreCase) ? "clones" : "views";
        if (root.TryGetProperty(arrayName, out var points) && points.ValueKind == JsonValueKind.Array)
        {
            foreach (var point in points.EnumerateArray())
            {
                metric.Points.Add(new GitTrafficPointDto
                {
                    Timestamp = TryGetDateTimeOffset(point, "timestamp") ?? DateTimeOffset.MinValue,
                    Count = TryGetLong(point, "count"),
                    Uniques = TryGetLong(point, "uniques")
                });
            }
        }

        metric.Points = metric.Points.OrderBy(x => x.Timestamp).ToList();
        return metric;
    }

    private static string TryGetRawString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }

    private static long TryGetLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var number) => number,
            _ => 0
        };
    }

    private static bool TryGetBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => false
        };
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

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
