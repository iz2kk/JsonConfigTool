using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ConfigTool.Models;

namespace ConfigTool.Services;

public sealed class DynamicDnsUpdateService
{
    private readonly DynamicDnsConfigService _configService;
    private readonly HttpClient _httpClient;

    public DynamicDnsUpdateService(DynamicDnsConfigService configService)
    {
        _configService = configService;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ConfigTool-DDNS/1.0 sanphamso-local-tool");
    }

    public Task<DynamicDnsResponseDto> LoadAsync(CancellationToken cancellationToken = default)
        => _configService.LoadResponseAsync(cancellationToken);

    public async Task<SqlActionResultDto> SaveAccountAsync(DynamicDnsAccountDto account, CancellationToken cancellationToken = default)
        => await _configService.SaveAccountAsync(account, cancellationToken);

    public async Task<SqlActionResultDto> DeleteAccountAsync(string accountId, CancellationToken cancellationToken = default)
        => await _configService.DeleteAccountAsync(accountId, cancellationToken);

    public async Task<SqlActionResultDto> SaveDomainAsync(string accountId, DynamicDnsDomainDto domain, CancellationToken cancellationToken = default)
        => await _configService.SaveDomainAsync(accountId, domain, cancellationToken);

    public async Task<SqlActionResultDto> DeleteDomainAsync(string accountId, string domainId, CancellationToken cancellationToken = default)
        => await _configService.DeleteDomainAsync(accountId, domainId, cancellationToken);

    public async Task<SqlActionResultDto> GetAndSavePublicIpAsync(CancellationToken cancellationToken = default)
    {
        var ipResult = await GetPublicIpAsync(cancellationToken);
        if (!ipResult.Success)
        {
            return ipResult;
        }

        await _configService.UpdatePublicIpAsync(ipResult.Message, "public-ip-check", cancellationToken);
        return new SqlActionResultDto { Success = true, Message = ipResult.Message };
    }

    public async Task<SqlActionResultDto> GetPublicIpAsync(CancellationToken cancellationToken = default)
    {
        var sources = new[]
        {
            "https://api.ipify.org?format=text",
            "https://checkip.amazonaws.com/"
        };

        foreach (var source in sources)
        {
            try
            {
                using var response = await _httpClient.GetAsync(source, cancellationToken);
                var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
                if (response.IsSuccessStatusCode && IPAddress.TryParse(body, out _))
                {
                    return new SqlActionResultDto { Success = true, Message = body };
                }
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                // thử nguồn tiếp theo
            }
        }

        return new SqlActionResultDto { Success = false, Message = "Không lấy được IP public. Kiểm tra kết nối internet." };
    }

    public async Task<DynamicDnsBulkUpdateResultDto> UpdateAsync(DynamicDnsUpdateRequest request, CancellationToken cancellationToken = default)
    {
        request.Provider = DynamicDnsConfigService.NormalizeProvider(request.Provider);
        var snapshot = await _configService.LoadAsync(cancellationToken);
        var ip = request.Ip?.Trim();
        if (string.IsNullOrWhiteSpace(ip))
        {
            var ipResult = await GetPublicIpAsync(cancellationToken);
            if (!ipResult.Success)
            {
                return new DynamicDnsBulkUpdateResultDto
                {
                    Success = false,
                    Message = ipResult.Message,
                    Snapshot = await _configService.LoadResponseAsync(cancellationToken)
                };
            }

            ip = ipResult.Message;
        }

        var targets = snapshot.Accounts
            .Where(x => string.Equals(x.Provider, request.Provider, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(request.AccountId) || string.Equals(x.Id, request.AccountId, StringComparison.OrdinalIgnoreCase))
            .Where(x => !request.OnlyEnabled || x.Enabled)
            .SelectMany(account => account.Domains
                .Where(domain => string.IsNullOrWhiteSpace(request.DomainId) || string.Equals(domain.Id, request.DomainId, StringComparison.OrdinalIgnoreCase))
                .Where(domain => !request.OnlyEnabled || domain.Enabled)
                .Where(domain => !string.IsNullOrWhiteSpace(domain.Hostname))
                .Select(domain => new DynamicDnsTarget(account, domain)))
            .ToList();

        if (targets.Count == 0)
        {
            return new DynamicDnsBulkUpdateResultDto
            {
                Success = false,
                PublicIp = ip,
                Message = "Không có domain bật sẵn để update.",
                Snapshot = await _configService.LoadResponseAsync(cancellationToken)
            };
        }

        var logs = new List<DynamicDnsLogDto>();
        var logsLock = new object();
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 12)
        };

        await Parallel.ForEachAsync(targets, parallelOptions, async (target, token) =>
        {
            var log = await UpdateOneAsync(target.Account, target.Domain, ip!, token);
            await _configService.ApplyUpdateLogAsync(log, token);
            lock (logsLock) logs.Add(log);
        });

        var response = await _configService.LoadResponseAsync(cancellationToken);
        var successCount = logs.Count(x => x.Success);
        return new DynamicDnsBulkUpdateResultDto
        {
            Success = successCount > 0,
            PublicIp = ip,
            Total = logs.Count,
            SuccessCount = successCount,
            FailedCount = logs.Count - successCount,
            Logs = logs.OrderByDescending(x => x.UpdatedAt).ToList(),
            Snapshot = response,
            Message = $"Đã update {successCount}/{logs.Count} domain với IP {ip}."
        };
    }

    private async Task<DynamicDnsLogDto> UpdateOneAsync(DynamicDnsAccountDto account, DynamicDnsDomainDto domain, string ip, CancellationToken cancellationToken)
    {
        var log = new DynamicDnsLogDto
        {
            Provider = DynamicDnsConfigService.NormalizeProvider(account.Provider),
            AccountId = account.Id,
            AccountName = account.Name,
            DomainId = domain.Id,
            Hostname = domain.Hostname,
            Ip = ip,
            UpdatedAt = DateTimeOffset.Now
        };

        try
        {
            if (string.IsNullOrWhiteSpace(account.Username) || string.IsNullOrWhiteSpace(account.Password))
            {
                log.Success = false;
                log.Status = "missing-auth";
                log.Message = "Thiếu tài khoản hoặc mật khẩu/DDNS key.";
                return log;
            }

            var url = BuildUpdateUrl(log.Provider, domain.Hostname, ip);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{account.Username}:{account.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
            request.Headers.UserAgent.ParseAdd("ConfigTool-DDNS/1.0 sanphamso-local-tool");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            log.Status = response.StatusCode.ToString();
            log.Message = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase ?? string.Empty : body;
            log.Success = response.IsSuccessStatusCode && IsProviderSuccess(body);
            if (!response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(log.Message))
            {
                log.Message = "HTTP " + (int)response.StatusCode;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            log.Success = false;
            log.Status = "exception";
            log.Message = ex.Message;
        }

        return log;
    }

    private static string BuildUpdateUrl(string provider, string hostname, string ip)
    {
        var encodedHost = Uri.EscapeDataString(hostname.Trim());
        var encodedIp = Uri.EscapeDataString(ip.Trim());
        return provider == "dynu"
            ? $"https://api.dynu.com/nic/update?hostname={encodedHost}&myip={encodedIp}"
            : $"https://dynupdate.no-ip.com/nic/update?hostname={encodedHost}&myip={encodedIp}";
    }

    private static bool IsProviderSuccess(string responseBody)
    {
        var value = (responseBody ?? string.Empty).Trim().ToLowerInvariant();
        return value.StartsWith("good", StringComparison.Ordinal)
               || value.StartsWith("nochg", StringComparison.Ordinal)
               || value.Contains("success", StringComparison.Ordinal);
    }

    private sealed record DynamicDnsTarget(DynamicDnsAccountDto Account, DynamicDnsDomainDto Domain);
}
