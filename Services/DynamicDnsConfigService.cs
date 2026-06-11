using System.Text.Encodings.Web;
using System.Text.Json;
using ConfigTool.Models;

namespace ConfigTool.Services;

public sealed class DynamicDnsConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IAppStartupPathProvider _paths;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public DynamicDnsConfigService(IAppStartupPathProvider paths)
    {
        _paths = paths;
    }

    public string DynamicFilePath => Path.Combine(_paths.ConfigDirectory, "dynamic.json");

    public async Task<DynamicDnsResponseDto> LoadResponseAsync(CancellationToken cancellationToken = default)
    {
        var config = await LoadAsync(cancellationToken);
        return new DynamicDnsResponseDto
        {
            DynamicFilePath = DynamicFilePath,
            Config = config
        };
    }

    public async Task<DynamicDnsConfigDto> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return await LoadNoLockAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<DynamicDnsAccountDto?> FindAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var config = await LoadAsync(cancellationToken);
        return config.Accounts.FirstOrDefault(x => string.Equals(x.Id, accountId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<SqlActionResultDto> SaveAutoUpdateAsync(DynamicDnsAutoUpdateDto settings, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var config = await LoadNoLockAsync(cancellationToken);
            NormalizeAutoUpdate(settings);
            settings.LastRunAt = config.AutoUpdate.LastRunAt;
            settings.LastIp = config.AutoUpdate.LastIp;
            settings.LastStatus = config.AutoUpdate.LastStatus;
            settings.LastMessage = config.AutoUpdate.LastMessage;
            config.AutoUpdate = settings;
            await SaveNoLockAsync(config, cancellationToken);
            return new SqlActionResultDto { Success = true, Message = settings.Enabled ? "Đã bật cấu hình Auto Update DNS." : "Đã tắt Auto Update DNS." };
        }
        catch (Exception ex)
        {
            return new SqlActionResultDto { Success = false, Message = "Không lưu được cấu hình Auto Update DNS: " + ex.Message };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAutoUpdateRuntimeAsync(string status, string message, string? ip, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var config = await LoadNoLockAsync(cancellationToken);
            NormalizeAutoUpdate(config.AutoUpdate);
            config.AutoUpdate.LastRunAt = DateTimeOffset.Now;
            config.AutoUpdate.LastStatus = status;
            config.AutoUpdate.LastMessage = message;
            if (!string.IsNullOrWhiteSpace(ip))
            {
                config.AutoUpdate.LastIp = ip.Trim();
            }

            await SaveNoLockAsync(config, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SqlActionResultDto> SaveAccountAsync(DynamicDnsAccountDto account, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var config = await LoadNoLockAsync(cancellationToken);
            NormalizeAccount(account);
            var existing = config.Accounts.FirstOrDefault(x => string.Equals(x.Id, account.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                account.CreatedAt = DateTimeOffset.Now;
                account.UpdatedAt = DateTimeOffset.Now;
                config.Accounts.Insert(0, account);
            }
            else
            {
                var domains = existing.Domains;
                var createdAt = existing.CreatedAt;
                existing.Provider = NormalizeProvider(account.Provider);
                existing.Name = string.IsNullOrWhiteSpace(account.Name) ? existing.Name : account.Name.Trim();
                existing.AuthMode = NormalizeAuthMode(existing.Provider, account.AuthMode);
                existing.Username = account.Username.Trim();
                existing.Password = account.Password;
                existing.ApiKey = account.ApiKey.Trim();
                existing.OAuthClientId = account.OAuthClientId.Trim();
                existing.OAuthSecret = account.OAuthSecret;
                existing.AccessToken = account.AccessToken.Trim();
                existing.AccessTokenExpiresAt = account.AccessTokenExpiresAt;
                existing.Enabled = account.Enabled;
                existing.Note = account.Note.Trim();
                existing.CreatedAt = createdAt == default ? DateTimeOffset.Now : createdAt;
                existing.UpdatedAt = DateTimeOffset.Now;
                existing.Domains = domains;
            }

            await SaveNoLockAsync(config, cancellationToken);
            return new SqlActionResultDto { Success = true, Message = "Đã lưu tài khoản DNS." };
        }
        catch (Exception ex)
        {
            return new SqlActionResultDto { Success = false, Message = "Không lưu được tài khoản DNS: " + ex.Message };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SqlActionResultDto> SaveAccountTokenAsync(string accountId, string accessToken, DateTimeOffset? expiresAt, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var config = await LoadNoLockAsync(cancellationToken);
            var account = config.Accounts.FirstOrDefault(x => string.Equals(x.Id, accountId, StringComparison.OrdinalIgnoreCase));
            if (account is null)
            {
                return new SqlActionResultDto { Success = false, Message = "Không tìm thấy tài khoản DNS để lưu OAuth token." };
            }

            account.AccessToken = accessToken.Trim();
            account.AccessTokenExpiresAt = expiresAt;
            account.UpdatedAt = DateTimeOffset.Now;
            await SaveNoLockAsync(config, cancellationToken);
            return new SqlActionResultDto { Success = true, Message = "Đã lấy và lưu OAuth access token." };
        }
        catch (Exception ex)
        {
            return new SqlActionResultDto { Success = false, Message = "Không lưu được OAuth token: " + ex.Message };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SqlActionResultDto> DeleteAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var config = await LoadNoLockAsync(cancellationToken);
            var removed = config.Accounts.RemoveAll(x => string.Equals(x.Id, accountId, StringComparison.OrdinalIgnoreCase));
            await SaveNoLockAsync(config, cancellationToken);
            return new SqlActionResultDto
            {
                Success = removed > 0,
                Message = removed > 0 ? "Đã xóa tài khoản DNS." : "Không tìm thấy tài khoản DNS để xóa."
            };
        }
        catch (Exception ex)
        {
            return new SqlActionResultDto { Success = false, Message = "Không xóa được tài khoản DNS: " + ex.Message };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SqlActionResultDto> SaveDomainAsync(string accountId, DynamicDnsDomainDto domain, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var config = await LoadNoLockAsync(cancellationToken);
            var account = config.Accounts.FirstOrDefault(x => string.Equals(x.Id, accountId, StringComparison.OrdinalIgnoreCase));
            if (account is null)
            {
                return new SqlActionResultDto { Success = false, Message = "Không tìm thấy tài khoản để thêm domain." };
            }

            NormalizeDomain(domain);
            var existing = account.Domains.FirstOrDefault(x => string.Equals(x.Id, domain.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                account.Domains.Insert(0, domain);
            }
            else
            {
                CopyDomainEditableFields(existing, domain, keepRuntimeStatus: false);
            }

            account.UpdatedAt = DateTimeOffset.Now;
            await SaveNoLockAsync(config, cancellationToken);
            return new SqlActionResultDto { Success = true, Message = "Đã lưu domain/record DNS." };
        }
        catch (Exception ex)
        {
            return new SqlActionResultDto { Success = false, Message = "Không lưu được domain DNS: " + ex.Message };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SqlActionResultDto> DeleteDomainAsync(string accountId, string domainId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var config = await LoadNoLockAsync(cancellationToken);
            var account = config.Accounts.FirstOrDefault(x => string.Equals(x.Id, accountId, StringComparison.OrdinalIgnoreCase));
            if (account is null)
            {
                return new SqlActionResultDto { Success = false, Message = "Không tìm thấy tài khoản DNS." };
            }

            var removed = account.Domains.RemoveAll(x => string.Equals(x.Id, domainId, StringComparison.OrdinalIgnoreCase));
            account.UpdatedAt = DateTimeOffset.Now;
            await SaveNoLockAsync(config, cancellationToken);
            return new SqlActionResultDto
            {
                Success = removed > 0,
                Message = removed > 0 ? "Đã xóa domain DNS." : "Không tìm thấy domain DNS để xóa."
            };
        }
        catch (Exception ex)
        {
            return new SqlActionResultDto { Success = false, Message = "Không xóa được domain DNS: " + ex.Message };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<DynamicDnsScanResultDto> MergeScannedDomainsAsync(string accountId, IReadOnlyCollection<DynamicDnsDomainDto> scanned, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var config = await LoadNoLockAsync(cancellationToken);
            var account = config.Accounts.FirstOrDefault(x => string.Equals(x.Id, accountId, StringComparison.OrdinalIgnoreCase));
            if (account is null)
            {
                return new DynamicDnsScanResultDto
                {
                    Success = false,
                    Message = "Không tìm thấy tài khoản để merge record đã scan.",
                    Snapshot = new DynamicDnsResponseDto { DynamicFilePath = DynamicFilePath, Config = config }
                };
            }

            var added = 0;
            var updated = 0;
            foreach (var item in scanned)
            {
                NormalizeDomain(item);
                var existing = account.Domains.FirstOrDefault(x => SameRecord(x, item));
                if (existing is null)
                {
                    item.Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id;
                    item.Enabled = true;
                    item.LastScannedAt = DateTimeOffset.Now;
                    account.Domains.Add(item);
                    added++;
                }
                else
                {
                    var previousEnabled = existing.Enabled;
                    var previousUpdateCount = existing.UpdateCount;
                    CopyDomainEditableFields(existing, item, keepRuntimeStatus: true);
                    existing.Enabled = previousEnabled;
                    existing.UpdateCount = previousUpdateCount;
                    existing.LastScannedAt = DateTimeOffset.Now;
                    updated++;
                }
            }

            account.UpdatedAt = DateTimeOffset.Now;
            await SaveNoLockAsync(config, cancellationToken);
            return new DynamicDnsScanResultDto
            {
                Success = true,
                Total = scanned.Count,
                Added = added,
                Updated = updated,
                Records = scanned.ToList(),
                Snapshot = new DynamicDnsResponseDto { DynamicFilePath = DynamicFilePath, Config = config },
                Message = $"Đã scan {scanned.Count} record: thêm {added}, cập nhật {updated}."
            };
        }
        catch (Exception ex)
        {
            return new DynamicDnsScanResultDto { Success = false, Message = "Không merge được record scan: " + ex.Message };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<DynamicDnsConfigDto> UpdatePublicIpAsync(string ip, string source, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var config = await LoadNoLockAsync(cancellationToken);
            config.PublicIp.LastIp = ip;
            config.PublicIp.Source = source;
            config.PublicIp.CheckedAt = DateTimeOffset.Now;
            await SaveNoLockAsync(config, cancellationToken);
            return config;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ApplyUpdateLogAsync(DynamicDnsLogDto log, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var config = await LoadNoLockAsync(cancellationToken);
            var account = config.Accounts.FirstOrDefault(x => string.Equals(x.Id, log.AccountId, StringComparison.OrdinalIgnoreCase));
            var domain = account?.Domains.FirstOrDefault(x => string.Equals(x.Id, log.DomainId, StringComparison.OrdinalIgnoreCase));
            if (domain is not null)
            {
                domain.LastIp = log.Ip;
                domain.LastStatus = log.Status;
                domain.LastMessage = log.Message;
                domain.LastUpdatedAt = log.UpdatedAt;
                domain.UpdateCount++;
            }

            config.PublicIp.LastIp = log.Ip;
            config.PublicIp.CheckedAt = log.UpdatedAt;
            config.PublicIp.Source = "dynamic-dns-update";
            config.Logs.Insert(0, log);
            if (config.Logs.Count > 500)
            {
                config.Logs = config.Logs.Take(500).ToList();
            }

            await SaveNoLockAsync(config, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<DynamicDnsConfigDto> LoadNoLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        if (!File.Exists(DynamicFilePath))
        {
            var empty = new DynamicDnsConfigDto();
            await SaveNoLockAsync(empty, cancellationToken);
            return empty;
        }

        try
        {
            await using var stream = File.Open(DynamicFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var config = await JsonSerializer.DeserializeAsync<DynamicDnsConfigDto>(stream, JsonOptions, cancellationToken) ?? new DynamicDnsConfigDto();
            NormalizeConfig(config);
            return config;
        }
        catch
        {
            var backup = DynamicFilePath + ".broken." + DateTimeOffset.Now.ToString("yyyyMMddHHmmss") + ".bak";
            try { File.Copy(DynamicFilePath, backup, overwrite: false); } catch { }
            var empty = new DynamicDnsConfigDto();
            await SaveNoLockAsync(empty, cancellationToken);
            return empty;
        }
    }

    private async Task SaveNoLockAsync(DynamicDnsConfigDto config, CancellationToken cancellationToken)
    {
        NormalizeConfig(config);
        Directory.CreateDirectory(_paths.ConfigDirectory);
        var tempFile = DynamicFilePath + ".tmp";
        await using (var stream = File.Open(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, config, JsonOptions, cancellationToken);
        }

        File.Copy(tempFile, DynamicFilePath, overwrite: true);
        File.Delete(tempFile);
    }

    private static void NormalizeConfig(DynamicDnsConfigDto config)
    {
        config.PublicIp ??= new DynamicDnsPublicIpDto();
        config.AutoUpdate ??= new DynamicDnsAutoUpdateDto();
        NormalizeAutoUpdate(config.AutoUpdate);
        config.Accounts ??= [];
        config.Logs ??= [];
        foreach (var account in config.Accounts)
        {
            NormalizeAccount(account);
        }
    }

    private static void NormalizeAutoUpdate(DynamicDnsAutoUpdateDto settings)
    {
        settings.IntervalMinutes = Math.Clamp(settings.IntervalMinutes <= 0 ? 10 : settings.IntervalMinutes, 1, 1440);
        settings.LastIp = settings.LastIp?.Trim() ?? string.Empty;
        settings.LastStatus = settings.LastStatus?.Trim() ?? string.Empty;
        settings.LastMessage = settings.LastMessage?.Trim() ?? string.Empty;
    }

    private static void NormalizeAccount(DynamicDnsAccountDto account)
    {
        if (string.IsNullOrWhiteSpace(account.Id)) account.Id = Guid.NewGuid().ToString("N");
        account.Provider = NormalizeProvider(account.Provider);
        if (string.IsNullOrWhiteSpace(account.Name)) account.Name = ProviderTitle(account.Provider) + " account";
        account.AuthMode = NormalizeAuthMode(account.Provider, account.AuthMode);
        account.Username = account.Username?.Trim() ?? string.Empty;
        account.Password ??= string.Empty;
        account.ApiKey = account.ApiKey?.Trim() ?? string.Empty;
        account.OAuthClientId = account.OAuthClientId?.Trim() ?? string.Empty;
        account.OAuthSecret ??= string.Empty;
        account.AccessToken = account.AccessToken?.Trim() ?? string.Empty;
        account.Note ??= string.Empty;
        account.Domains ??= [];
        foreach (var domain in account.Domains) NormalizeDomain(domain);
    }

    private static void NormalizeDomain(DynamicDnsDomainDto domain)
    {
        if (string.IsNullOrWhiteSpace(domain.Id)) domain.Id = Guid.NewGuid().ToString("N");
        domain.Hostname = domain.Hostname?.Trim() ?? string.Empty;
        domain.ZoneName = domain.ZoneName?.Trim() ?? string.Empty;
        domain.ZoneId = domain.ZoneId?.Trim() ?? string.Empty;
        domain.RecordId = domain.RecordId?.Trim() ?? string.Empty;
        domain.RecordName = domain.RecordName?.Trim() ?? string.Empty;
        domain.RecordType = string.IsNullOrWhiteSpace(domain.RecordType) ? "A" : domain.RecordType.Trim().ToUpperInvariant();
        domain.ScanSource = domain.ScanSource?.Trim() ?? string.Empty;
        domain.LastIp ??= string.Empty;
        domain.LastStatus ??= string.Empty;
        domain.LastMessage ??= string.Empty;
    }

    private static void CopyDomainEditableFields(DynamicDnsDomainDto target, DynamicDnsDomainDto source, bool keepRuntimeStatus)
    {
        target.Hostname = source.Hostname.Trim();
        target.ZoneName = source.ZoneName.Trim();
        target.ZoneId = source.ZoneId.Trim();
        target.RecordId = source.RecordId.Trim();
        target.RecordName = source.RecordName.Trim();
        target.RecordType = string.IsNullOrWhiteSpace(source.RecordType) ? "A" : source.RecordType.Trim().ToUpperInvariant();
        target.Ttl = source.Ttl;
        target.Proxied = source.Proxied;
        target.ScanSource = source.ScanSource.Trim();
        target.Enabled = source.Enabled;
        target.LastScannedAt = source.LastScannedAt;
        if (!keepRuntimeStatus)
        {
            target.LastIp = source.LastIp;
            target.LastStatus = source.LastStatus;
            target.LastMessage = source.LastMessage;
            target.LastUpdatedAt = source.LastUpdatedAt;
            target.UpdateCount = source.UpdateCount;
        }
        else if (!string.IsNullOrWhiteSpace(source.LastIp))
        {
            target.LastIp = source.LastIp;
        }
    }

    private static bool SameRecord(DynamicDnsDomainDto left, DynamicDnsDomainDto right)
    {
        if (!string.IsNullOrWhiteSpace(left.RecordId) && !string.IsNullOrWhiteSpace(right.RecordId))
        {
            return string.Equals(left.RecordId, right.RecordId, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(left.Hostname, right.Hostname, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.RecordType, right.RecordType, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.ZoneName, right.ZoneName, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeProvider(string? provider)
    {
        provider = (provider ?? string.Empty).Trim().ToLowerInvariant();
        return provider switch
        {
            "dynu" or "dynu.com" => "dynu",
            "cloudflare" or "cf" => "cloudflare",
            _ => "noip"
        };
    }

    public static string NormalizeAuthMode(string? provider, string? authMode)
    {
        provider = NormalizeProvider(provider);
        authMode = (authMode ?? string.Empty).Trim().ToLowerInvariant();
        return provider switch
        {
            "cloudflare" => "api-token",
            "dynu" when authMode is "oauth" or "oauth2" => "oauth2",
            "dynu" when authMode is "apikey" or "api-key" => "api-key",
            "noip" when authMode is "apikey" or "api-key" => "api-key",
            _ => "basic"
        };
    }

    public static string ProviderTitle(string provider) => NormalizeProvider(provider) switch
    {
        "dynu" => "Dynu.com",
        "cloudflare" => "Cloudflare",
        _ => "No-IP.org"
    };
}
