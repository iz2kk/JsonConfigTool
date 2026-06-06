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
                existing.Username = account.Username.Trim();
                existing.Password = account.Password;
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
                existing.Hostname = domain.Hostname.Trim();
                existing.Enabled = domain.Enabled;
                existing.LastIp = domain.LastIp;
                existing.LastStatus = domain.LastStatus;
                existing.LastMessage = domain.LastMessage;
                existing.LastUpdatedAt = domain.LastUpdatedAt;
                existing.UpdateCount = domain.UpdateCount;
            }

            account.UpdatedAt = DateTimeOffset.Now;
            await SaveNoLockAsync(config, cancellationToken);
            return new SqlActionResultDto { Success = true, Message = "Đã lưu domain DNS." };
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
        config.Accounts ??= [];
        config.Logs ??= [];
        foreach (var account in config.Accounts)
        {
            NormalizeAccount(account);
        }
    }

    private static void NormalizeAccount(DynamicDnsAccountDto account)
    {
        if (string.IsNullOrWhiteSpace(account.Id)) account.Id = Guid.NewGuid().ToString("N");
        account.Provider = NormalizeProvider(account.Provider);
        if (string.IsNullOrWhiteSpace(account.Name)) account.Name = account.Provider == "dynu" ? "Dynu account" : "No-IP account";
        account.Username = account.Username?.Trim() ?? string.Empty;
        account.Password ??= string.Empty;
        account.Note ??= string.Empty;
        account.Domains ??= [];
        foreach (var domain in account.Domains) NormalizeDomain(domain);
    }

    private static void NormalizeDomain(DynamicDnsDomainDto domain)
    {
        if (string.IsNullOrWhiteSpace(domain.Id)) domain.Id = Guid.NewGuid().ToString("N");
        domain.Hostname = domain.Hostname?.Trim() ?? string.Empty;
        domain.LastIp ??= string.Empty;
        domain.LastStatus ??= string.Empty;
        domain.LastMessage ??= string.Empty;
    }

    public static string NormalizeProvider(string? provider)
    {
        provider = (provider ?? string.Empty).Trim().ToLowerInvariant();
        return provider is "dynu" or "dynu.com" ? "dynu" : "noip";
    }
}
