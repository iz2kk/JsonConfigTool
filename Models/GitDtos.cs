using System.Text.Json.Serialization;

namespace ConfigTool.Models;

public sealed class GitConfigFileDto
{
    [JsonPropertyName("accounts")]
    public List<GitAccountDto> Accounts { get; set; } = [];

    [JsonPropertyName("lastProjectFolder")]
    public string? LastProjectFolder { get; set; }

    [JsonPropertyName("lastCloneFolder")]
    public string? LastCloneFolder { get; set; }

    [JsonPropertyName("workspaces")]
    public List<GitWorkspaceDto> Workspaces { get; set; } = [];
}

public sealed class GitAccountDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "github";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Git account";

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("tokenSource")]
    public string TokenSource { get; set; } = "manual";

    [JsonPropertyName("tokenType")]
    public string TokenType { get; set; } = string.Empty;

    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("tokenExpiresAt")]
    public DateTimeOffset? TokenExpiresAt { get; set; }

    [JsonPropertyName("lastOAuthAt")]
    public DateTimeOffset? LastOAuthAt { get; set; }

    [JsonPropertyName("oauthClientId")]
    public string? OAuthClientId { get; set; }

    [JsonPropertyName("oauthClientSecret")]
    public string? OAuthClientSecret { get; set; }

    [JsonPropertyName("oauthScopes")]
    public string? OAuthScopes { get; set; }

    [JsonPropertyName("oauthRedirectPort")]
    public int OAuthRedirectPort { get; set; } = 53682;

    [JsonPropertyName("apiBaseUrl")]
    public string ApiBaseUrl { get; set; } = "https://api.github.com";

    [JsonPropertyName("gitBaseUrl")]
    public string? GitBaseUrl { get; set; }

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class GitConfigResponseDto
{
    public string ConfigFilePath { get; set; } = string.Empty;
    public GitConfigFileDto Config { get; set; } = new();
}

public sealed class GitActionResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int ExitCode { get; set; }
    public string Output { get; set; } = string.Empty;
    public GitRepositoryStatusDto? Repository { get; set; }
}

public sealed class GitRepositoryStatusDto
{
    public string FolderPath { get; set; } = string.Empty;
    public bool FolderExists { get; set; }
    public bool IsGitRepository { get; set; }
    public string CurrentBranch { get; set; } = string.Empty;
    public bool HasChanges { get; set; }
    public string LastCommit { get; set; } = string.Empty;
    public List<GitRemoteDto> Remotes { get; set; } = [];
    public List<GitBranchDto> Branches { get; set; } = [];
    public List<GitFileStatusDto> Changes { get; set; } = [];
}

public sealed class GitRemoteDto
{
    public string Name { get; set; } = string.Empty;
    public string FetchUrl { get; set; } = string.Empty;
    public string PushUrl { get; set; } = string.Empty;
}

public sealed class GitBranchDto
{
    public string Name { get; set; } = string.Empty;
    public bool IsCurrent { get; set; }
    public bool IsRemote { get; set; }
}

public sealed class GitFileStatusDto
{
    public string Path { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset LastWriteTime { get; set; }
    public string LastCommitHash { get; set; } = string.Empty;
    public string LastCommitMessage { get; set; } = string.Empty;
    public DateTimeOffset? LastCommitAt { get; set; }
}

public sealed class GitLocalFileDto
{
    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public string GitStatus { get; set; } = string.Empty;
    public bool IsIgnored { get; set; }
    public bool IsTracked { get; set; }
    public bool CanStage { get; set; } = true;
    public long SizeBytes { get; set; }
    public DateTimeOffset LastWriteTime { get; set; }
    public string LastCommitHash { get; set; } = string.Empty;
    public string LastCommitMessage { get; set; } = string.Empty;
    public DateTimeOffset? LastCommitAt { get; set; }
}

public sealed class GitExplorerBreadcrumbDto
{
    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
}

public sealed class GitFileExplorerDto
{
    public string FolderPath { get; set; } = string.Empty;
    public string CurrentRelativePath { get; set; } = string.Empty;
    public string ParentRelativePath { get; set; } = string.Empty;
    public bool CanGoParent { get; set; }
    public List<GitExplorerBreadcrumbDto> Breadcrumbs { get; set; } = [];
    public string Search { get; set; } = string.Empty;
    public List<GitLocalFileDto> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int IgnoredCount { get; set; }
    public bool IsTruncated { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public int TotalPages { get; set; } = 1;
}

public sealed class GitRemoteFileDto
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string ObjectType { get; set; } = "blob";
    public string Mode { get; set; } = string.Empty;
    public string ObjectId { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string LastCommitHash { get; set; } = string.Empty;
    public string LastCommitMessage { get; set; } = string.Empty;
    public DateTimeOffset? LastCommitAt { get; set; }
    public string RawUrl { get; set; } = string.Empty;
}

public sealed class GitRemoteExplorerDto
{
    public string FolderPath { get; set; } = string.Empty;
    public string RemoteName { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public string CurrentRelativePath { get; set; } = string.Empty;
    public string ParentRelativePath { get; set; } = string.Empty;
    public bool CanGoParent { get; set; }
    public List<GitExplorerBreadcrumbDto> Breadcrumbs { get; set; } = [];
    public string Search { get; set; } = string.Empty;
    public List<GitRemoteFileDto> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int DirectoryCount { get; set; }
    public int FileCount { get; set; }
    public bool IsTruncated { get; set; }
    public string SourceRef { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public int TotalPages { get; set; } = 1;
}

public sealed class GitFilePreviewDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsRemote { get; set; }
    public bool IsDirectory { get; set; }
    public string Content { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public bool IsTruncated { get; set; }
    public string RawUrl { get; set; } = string.Empty;
    public string EmbedUrl { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string LastCommitHash { get; set; } = string.Empty;
    public string LastCommitMessage { get; set; } = string.Empty;
    public DateTimeOffset? LastCommitAt { get; set; }
}

public sealed class GitIgnoreRuleRequestDto
{
    public string FolderPath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public bool AddToIgnore { get; set; } = true;
    public bool IsDirectory { get; set; }
}

public sealed class GitOAuth2ConfigDto
{
    [JsonPropertyName("callbackPort")]
    public int CallbackPort { get; set; } = 53682;

    [JsonPropertyName("callbackPath")]
    public string CallbackPath { get; set; } = "/callback";

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class GitWorkspaceDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("accountId")]
    public string AccountId { get; set; } = string.Empty;

    [JsonPropertyName("accountName")]
    public string AccountName { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("repositoryFullName")]
    public string RepositoryFullName { get; set; } = string.Empty;

    [JsonPropertyName("repositoryId")]
    public string RepositoryId { get; set; } = string.Empty;

    [JsonPropertyName("cloneUrl")]
    public string CloneUrl { get; set; } = string.Empty;

    [JsonPropertyName("htmlUrl")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("remoteName")]
    public string RemoteName { get; set; } = "origin";

    [JsonPropertyName("branchName")]
    public string BranchName { get; set; } = string.Empty;

    [JsonPropertyName("folderPath")]
    public string FolderPath { get; set; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class GitWorkspaceResponseDto
{
    public List<GitWorkspaceDto> Items { get; set; } = [];
    public string ConfigFilePath { get; set; } = string.Empty;
}

public sealed class GitWorkspaceSaveRequestDto
{
    public GitWorkspaceDto Workspace { get; set; } = new();
}

public sealed class GitBatchWorkspaceActionRequestDto
{
    public List<string> WorkspaceIds { get; set; } = [];
    public string Action { get; set; } = "status";
    public int MaxParallel { get; set; } = 3;
}


public sealed class GitProjectConfigSnapshotDto
{
    public string FolderPath { get; set; } = string.Empty;
    public List<GitProjectConfigFileDto> Files { get; set; } = [];
    public int TotalCount { get; set; }
    public bool IsTruncated { get; set; }
}

public sealed class GitProjectConfigFileDto
{
    public string Source { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public bool IsTruncated { get; set; }
    public DateTimeOffset LastWriteTime { get; set; }
}

public sealed class GitConsoleEntryDto
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;
    public string Level { get; set; } = "info";
    public string Scope { get; set; } = "git";
    public string Message { get; set; } = string.Empty;
}

public sealed class GitRepositorySearchResultDto
{
    public string FolderPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset LastWriteTime { get; set; }
}

public sealed class GitRemoteRequestDto
{
    public string FolderPath { get; set; } = string.Empty;
    public string RemoteName { get; set; } = "origin";
    public string RemoteUrl { get; set; } = string.Empty;
    public bool SetUrlIfExists { get; set; } = true;
}


public sealed class GitAddRequestDto
{
    public string FolderPath { get; set; } = string.Empty;
    public bool StageAll { get; set; } = true;
    public List<string> Paths { get; set; } = [];
}

public sealed class GitCommitRequestDto
{
    public string FolderPath { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool StageAll { get; set; } = true;
    public List<string> Paths { get; set; } = [];
}

public sealed class GitPushPullRequestDto
{
    public string FolderPath { get; set; } = string.Empty;
    public string RemoteName { get; set; } = "origin";
    public string BranchName { get; set; } = string.Empty;
    public bool SetUpstream { get; set; } = true;
    public bool PullRebase { get; set; }
}

public sealed class GitBranchRequestDto
{
    public string FolderPath { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public bool CheckoutAfterCreate { get; set; } = true;
}

public sealed class GitCloneRequestDto
{
    public string ParentFolderPath { get; set; } = string.Empty;
    public string RepositoryUrl { get; set; } = string.Empty;
    public string? TargetFolderName { get; set; }
}


public sealed class GitOAuthRequestDto
{
    public GitAccountDto Account { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 300;
}

public sealed class GitOAuthResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string AuthorizationUrl { get; set; } = string.Empty;
    public GitAccountDto? Account { get; set; }
}

public sealed class GitCreateRepositoryRequestDto
{
    public string AccountId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Visibility { get; set; } = "private";
    public bool InitializeReadme { get; set; }
}

public sealed class GitCreatedRepositoryDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string CloneUrl { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
}


public sealed class GitRepositoryListRequestDto
{
    public string AccountId { get; set; } = string.Empty;
    public string Search { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public sealed class GitRepositoryListResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public List<GitProviderRepositoryDto> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public bool IsTruncated { get; set; }
}

public sealed class GitProviderRepositoryDto
{
    public string Provider { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Visibility { get; set; } = string.Empty;
    public bool IsPrivate { get; set; }
    public string DefaultBranch { get; set; } = string.Empty;
    public string CloneUrl { get; set; } = string.Empty;
    public string SshUrl { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
    public long StarCount { get; set; }
    public long ForkCount { get; set; }
    public long OpenIssueCount { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class GitTrafficRequestDto
{
    public string AccountId { get; set; } = string.Empty;
    public string RepositoryFullName { get; set; } = string.Empty;
    public string RepositoryId { get; set; } = string.Empty;
}

public sealed class GitTrafficStatsDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string RepositoryFullName { get; set; } = string.Empty;
    public List<GitTrafficMetricDto> Metrics { get; set; } = [];
}

public sealed class GitTrafficMetricDto
{
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public long Count { get; set; }
    public long Uniques { get; set; }
    public List<GitTrafficPointDto> Points { get; set; } = [];
}

public sealed class GitTrafficPointDto
{
    public DateTimeOffset Timestamp { get; set; }
    public long Count { get; set; }
    public long Uniques { get; set; }
}

public sealed class GitBatchAccountActionRequestDto
{
    public List<string> AccountIds { get; set; } = [];
    public string Action { get; set; } = string.Empty;
    public int MaxParallel { get; set; } = 4;
}

public sealed class GitBatchAccountActionResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<GitBatchAccountActionItemDto> Items { get; set; } = [];
    public int SuccessCount => Items.Count(x => x.Success);
    public int ErrorCount => Items.Count(x => !x.Success);
}

public sealed class GitBatchAccountActionItemDto
{
    public string AccountId { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class GitBatchRepositoryListRequestDto
{
    public List<string> AccountIds { get; set; } = [];
    public string Search { get; set; } = string.Empty;
    public int PageSize { get; set; } = 50;
    public int MaxParallel { get; set; } = 4;
}

public sealed class GitBatchCreateRepositoryRequestDto
{
    public List<string> AccountIds { get; set; } = [];
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Visibility { get; set; } = "private";
    public bool InitializeReadme { get; set; }
    public int MaxParallel { get; set; } = 3;
}
