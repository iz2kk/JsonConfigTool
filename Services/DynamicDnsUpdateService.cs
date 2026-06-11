using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ConfigTool-DDNS/2.0 sanphamso-local-tool");
    }

    public Task<DynamicDnsResponseDto> LoadAsync(CancellationToken cancellationToken = default)
        => _configService.LoadResponseAsync(cancellationToken);

    public async Task<SqlActionResultDto> SaveAutoUpdateAsync(DynamicDnsAutoUpdateDto settings, CancellationToken cancellationToken = default)
        => await _configService.SaveAutoUpdateAsync(settings, cancellationToken);

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

    public async Task<SqlActionResultDto> AuthorizeAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var account = await _configService.FindAccountAsync(accountId, cancellationToken);
        if (account is null)
        {
            return new SqlActionResultDto { Success = false, Message = "Không tìm thấy tài khoản DNS để OAuth." };
        }

        account.Provider = DynamicDnsConfigService.NormalizeProvider(account.Provider);
        account.AuthMode = DynamicDnsConfigService.NormalizeAuthMode(account.Provider, account.AuthMode);
        if (account.Provider != "dynu" || account.AuthMode != "oauth2")
        {
            return new SqlActionResultDto { Success = false, Message = "Hiện chỉ Dynu dùng OAuth2 client_id/secret trong mục DNS Update." };
        }

        var token = await RequestDynuOAuthTokenAsync(account, cancellationToken);
        if (!token.Success)
        {
            return new SqlActionResultDto { Success = false, Message = token.Message };
        }

        return await _configService.SaveAccountTokenAsync(account.Id, token.AccessToken, token.ExpiresAt, cancellationToken);
    }

    public async Task<DynamicDnsScanResultDto> ScanAsync(DynamicDnsScanRequest request, CancellationToken cancellationToken = default)
    {
        request.Provider = DynamicDnsConfigService.NormalizeProvider(request.Provider);
        var snapshot = await _configService.LoadAsync(cancellationToken);
        var accounts = snapshot.Accounts
            .Where(x => string.Equals(DynamicDnsConfigService.NormalizeProvider(x.Provider), request.Provider, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(request.AccountId) || string.Equals(x.Id, request.AccountId, StringComparison.OrdinalIgnoreCase))
            .Where(x => !request.OnlyEnabled || x.Enabled)
            .ToList();

        if (accounts.Count == 0)
        {
            return new DynamicDnsScanResultDto
            {
                Success = false,
                Message = "Không có tài khoản phù hợp để scan DNS.",
                Snapshot = await _configService.LoadResponseAsync(cancellationToken)
            };
        }

        var allRecords = new List<DynamicDnsDomainDto>();
        var added = 0;
        var updated = 0;
        DynamicDnsResponseDto? lastSnapshot = null;
        var errors = new List<string>();

        foreach (var account in accounts)
        {
            try
            {
                var records = await ScanAccountAsync(account, cancellationToken);
                allRecords.AddRange(records);
                if (request.SaveToConfig)
                {
                    var merge = await _configService.MergeScannedDomainsAsync(account.Id, records, cancellationToken);
                    added += merge.Added;
                    updated += merge.Updated;
                    lastSnapshot = merge.Snapshot;
                    if (!merge.Success && !string.IsNullOrWhiteSpace(merge.Message))
                    {
                        errors.Add($"{account.Name}: {merge.Message}");
                    }
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add($"{account.Name}: {ex.Message}");
            }
        }

        var success = allRecords.Count > 0 && errors.Count == 0;
        var partial = allRecords.Count > 0 && errors.Count > 0;
        return new DynamicDnsScanResultDto
        {
            Success = success || partial,
            Total = allRecords.Count,
            Added = added,
            Updated = updated,
            Records = allRecords,
            Snapshot = lastSnapshot ?? await _configService.LoadResponseAsync(cancellationToken),
            Message = errors.Count == 0
                ? $"Đã scan {allRecords.Count} record DNS, thêm {added}, cập nhật {updated}."
                : $"Scan được {allRecords.Count} record nhưng có lỗi: {string.Join(" | ", errors.Take(3))}"
        };
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
            .Where(x => string.Equals(DynamicDnsConfigService.NormalizeProvider(x.Provider), request.Provider, StringComparison.OrdinalIgnoreCase))
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
                Message = "Không có domain/record bật sẵn để update.",
                Snapshot = await _configService.LoadResponseAsync(cancellationToken)
            };
        }

        var logs = new List<DynamicDnsLogDto>();
        var logsLock = new object();
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8)
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
            Message = $"Đã update {successCount}/{logs.Count} record với IP {ip}."
        };
    }

    private async Task<List<DynamicDnsDomainDto>> ScanAccountAsync(DynamicDnsAccountDto account, CancellationToken cancellationToken)
    {
        account.Provider = DynamicDnsConfigService.NormalizeProvider(account.Provider);
        account.AuthMode = DynamicDnsConfigService.NormalizeAuthMode(account.Provider, account.AuthMode);
        return account.Provider switch
        {
            "cloudflare" => await ScanCloudflareAsync(account, cancellationToken),
            "dynu" => await ScanDynuAsync(account, cancellationToken),
            _ => await ScanNoIpAsync(account, cancellationToken)
        };
    }

    private async Task<List<DynamicDnsDomainDto>> ScanCloudflareAsync(DynamicDnsAccountDto account, CancellationToken cancellationToken)
    {
        var token = GetSecret(account.ApiKey, account.Password);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Cloudflare cần API Token trong ô API Key / Token.");
        }

        var zones = new List<CloudflareZone>();
        for (var page = 1; page <= 50; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.cloudflare.com/client/v4/zones?per_page=50&page={page}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("Cloudflare list zones lỗi HTTP " + (int)response.StatusCode + ": " + TrimBody(body));
            }

            using var doc = JsonDocument.Parse(body);
            foreach (var item in EnumerateArray(doc.RootElement, "result"))
            {
                var id = GetString(item, "id");
                var name = GetString(item, "name");
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                {
                    zones.Add(new CloudflareZone(id, name));
                }
            }

            if (!HasMoreCloudflarePages(doc.RootElement, page)) break;
        }

        var records = new List<DynamicDnsDomainDto>();
        foreach (var zone in zones)
        {
            for (var page = 1; page <= 100; page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.cloudflare.com/client/v4/zones/{Uri.EscapeDataString(zone.Id)}/dns_records?per_page=500&page={page}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Cloudflare list records {zone.Name} lỗi HTTP {(int)response.StatusCode}: {TrimBody(body)}");
                }

                using var doc = JsonDocument.Parse(body);
                foreach (var item in EnumerateArray(doc.RootElement, "result"))
                {
                    var type = GetString(item, "type").ToUpperInvariant();
                    if (type is not ("A" or "AAAA")) continue;
                    var name = GetString(item, "name");
                    var id = GetString(item, "id");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                    records.Add(new DynamicDnsDomainDto
                    {
                        Id = DeterministicId("cloudflare", zone.Id, id, type, name),
                        Hostname = name,
                        ZoneName = zone.Name,
                        ZoneId = zone.Id,
                        RecordId = id,
                        RecordName = name,
                        RecordType = type,
                        LastIp = GetString(item, "content"),
                        Ttl = GetInt(item, "ttl"),
                        Proxied = GetBool(item, "proxied"),
                        ScanSource = "cloudflare-api",
                        Enabled = true,
                        LastScannedAt = DateTimeOffset.Now
                    });
                }

                if (!HasMoreCloudflarePages(doc.RootElement, page)) break;
            }
        }

        return records.OrderBy(x => x.ZoneName).ThenBy(x => x.Hostname).ThenBy(x => x.RecordType).ToList();
    }

    private async Task<List<DynamicDnsDomainDto>> ScanDynuAsync(DynamicDnsAccountDto account, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.dynu.com/v2/dns");
        await ApplyDynuApiAuthAsync(request, account, cancellationToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Dynu /v2/dns lỗi HTTP " + (int)response.StatusCode + ": " + TrimBody(body));
        }

        using var doc = JsonDocument.Parse(body);
        var records = new List<DynamicDnsDomainDto>();
        foreach (var item in EnumerateAnyDataArray(doc.RootElement))
        {
            var name = FirstNonEmpty(
                GetString(item, "name"),
                GetString(item, "domainName"),
                GetString(item, "hostname"),
                GetString(item, "host"));
            if (string.IsNullOrWhiteSpace(name)) continue;

            var id = GetString(item, "id");
            var ipv4 = FirstNonEmpty(GetString(item, "ipv4Address"), GetString(item, "ipv4_address"), GetString(item, "ipv4"), GetString(item, "content"));
            var ipv6 = FirstNonEmpty(GetString(item, "ipv6Address"), GetString(item, "ipv6_address"), GetString(item, "ipv6"));
            records.Add(new DynamicDnsDomainDto
            {
                Id = DeterministicId("dynu", id, name, "A"),
                Hostname = name,
                ZoneName = name,
                ZoneId = id,
                RecordName = name,
                RecordType = "A",
                LastIp = ipv4,
                ScanSource = "dynu-v2-api",
                Enabled = true,
                LastScannedAt = DateTimeOffset.Now
            });

            if (!string.IsNullOrWhiteSpace(ipv6))
            {
                records.Add(new DynamicDnsDomainDto
                {
                    Id = DeterministicId("dynu", id, name, "AAAA"),
                    Hostname = name,
                    ZoneName = name,
                    ZoneId = id,
                    RecordName = name,
                    RecordType = "AAAA",
                    LastIp = ipv6,
                    ScanSource = "dynu-v2-api",
                    Enabled = true,
                    LastScannedAt = DateTimeOffset.Now
                });
            }
        }

        return records.GroupBy(x => x.Id).Select(x => x.First()).OrderBy(x => x.Hostname).ToList();
    }

    private async Task<List<DynamicDnsDomainDto>> ScanNoIpAsync(DynamicDnsAccountDto account, CancellationToken cancellationToken)
    {
        var apiKey = GetSecret(account.ApiKey, account.AuthMode == "api-key" ? account.Password : string.Empty);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("No-IP scan cần API Key. Username/password hoặc DDNS key vẫn dùng cho update IP qua dynupdate.");
        }

        var zones = new List<string>();
        using (var request = new HttpRequestMessage(HttpMethod.Get, "https://api.noip.com/v1/dns/zones"))
        {
            ApplyNoIpApiAuth(request, apiKey);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("No-IP list zones lỗi HTTP " + (int)response.StatusCode + ": " + TrimBody(body));
            }

            using var doc = JsonDocument.Parse(body);
            foreach (var item in EnumerateAnyDataArray(doc.RootElement))
            {
                var name = FirstNonEmpty(GetString(item, "name"), GetString(item, "zone_name"), GetString(item, "zone"));
                if (!string.IsNullOrWhiteSpace(name)) zones.Add(name.Trim('.'));
            }
        }

        var records = new List<DynamicDnsDomainDto>();
        foreach (var zone in zones.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var zoneRecords = await ScanNoIpZoneAsync(apiKey, zone, cancellationToken);
            records.AddRange(zoneRecords);
        }

        return records.GroupBy(x => x.Id).Select(x => x.First()).OrderBy(x => x.ZoneName).ThenBy(x => x.Hostname).ToList();
    }

    private async Task<List<DynamicDnsDomainDto>> ScanNoIpZoneAsync(string apiKey, string zone, CancellationToken cancellationToken)
    {
        var urls = new[]
        {
            $"https://api.noip.com/v1/dns/records/{Uri.EscapeDataString(zone)}?limit=500&view=rrsets",
            $"https://api.noip.com/v1/dns/records?limit=500&zone={Uri.EscapeDataString(zone)}&view=rrsets"
        };

        Exception? lastError = null;
        foreach (var url in urls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyNoIpApiAuth(request, apiKey);
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    lastError = new InvalidOperationException("HTTP " + (int)response.StatusCode + ": " + TrimBody(body));
                    continue;
                }

                using var doc = JsonDocument.Parse(body);
                var records = new List<DynamicDnsDomainDto>();
                foreach (var item in EnumerateAnyDataArray(doc.RootElement))
                {
                    records.AddRange(ParseNoIpNameRecord(zone, item));
                }

                return records;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException($"No-IP không scan được zone {zone}: {lastError?.Message}");
    }

    private async Task<DynamicDnsLogDto> UpdateOneAsync(DynamicDnsAccountDto account, DynamicDnsDomainDto domain, string ip, CancellationToken cancellationToken)
    {
        var provider = DynamicDnsConfigService.NormalizeProvider(account.Provider);
        var log = new DynamicDnsLogDto
        {
            Provider = provider,
            AccountId = account.Id,
            AccountName = account.Name,
            DomainId = domain.Id,
            Hostname = domain.Hostname,
            RecordType = domain.RecordType,
            Ip = ip,
            UpdatedAt = DateTimeOffset.Now
        };

        try
        {
            if (provider == "cloudflare")
            {
                await UpdateCloudflareAsync(account, domain, ip, log, cancellationToken);
            }
            else if (provider == "dynu" && (account.AuthMode is "api-key" or "oauth2") && !HasBasicAuth(account))
            {
                await UpdateDynuApiAsync(account, domain, ip, log, cancellationToken);
            }
            else
            {
                await UpdateByNicProtocolAsync(account, domain, ip, log, cancellationToken);
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

    private async Task UpdateCloudflareAsync(DynamicDnsAccountDto account, DynamicDnsDomainDto domain, string ip, DynamicDnsLogDto log, CancellationToken cancellationToken)
    {
        var token = GetSecret(account.ApiKey, account.Password);
        if (string.IsNullOrWhiteSpace(token))
        {
            log.Success = false;
            log.Status = "missing-token";
            log.Message = "Cloudflare cần API Token.";
            return;
        }

        if (string.IsNullOrWhiteSpace(domain.ZoneId) || string.IsNullOrWhiteSpace(domain.RecordId))
        {
            log.Success = false;
            log.Status = "missing-record-id";
            log.Message = "Cloudflare cần scan record trước để có zone_id và dns_record_id.";
            return;
        }

        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["content"] = ip
        });
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"https://api.cloudflare.com/client/v4/zones/{Uri.EscapeDataString(domain.ZoneId)}/dns_records/{Uri.EscapeDataString(domain.RecordId)}")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        log.Status = response.StatusCode.ToString();
        log.Message = TrimBody(body);
        log.Success = response.IsSuccessStatusCode && JsonSuccess(body, fallback: true);
    }

    private async Task UpdateDynuApiAsync(DynamicDnsAccountDto account, DynamicDnsDomainDto domain, string ip, DynamicDnsLogDto log, CancellationToken cancellationToken)
    {
        var name = FirstNonEmpty(domain.ZoneName, domain.Hostname);
        var body = new Dictionary<string, object?>
        {
            ["name"] = name
        };
        if (int.TryParse(domain.ZoneId, out var id))
        {
            body["id"] = id;
        }

        if (string.Equals(domain.RecordType, "AAAA", StringComparison.OrdinalIgnoreCase))
        {
            body["ipv6Address"] = ip;
        }
        else
        {
            body["ipv4Address"] = ip;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.dynu.com/v2/dns")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        await ApplyDynuApiAuthAsync(request, account, cancellationToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        log.Status = response.StatusCode.ToString();
        log.Message = TrimBody(responseBody);
        log.Success = response.IsSuccessStatusCode;
    }

    private async Task UpdateByNicProtocolAsync(DynamicDnsAccountDto account, DynamicDnsDomainDto domain, string ip, DynamicDnsLogDto log, CancellationToken cancellationToken)
    {
        if (!HasBasicAuth(account))
        {
            log.Success = false;
            log.Status = "missing-auth";
            log.Message = log.Provider == "noip"
                ? "No-IP update cần DDNS key hoặc username/password. API Key chỉ dùng scan DNS."
                : "Thiếu tài khoản/mật khẩu hoặc DDNS key.";
            return;
        }

        var url = BuildUpdateUrl(log.Provider, domain.Hostname, ip);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{account.Username}:{account.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
        request.Headers.UserAgent.ParseAdd("ConfigTool-DDNS/2.0 sanphamso-local-tool");

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

    private async Task ApplyDynuApiAuthAsync(HttpRequestMessage request, DynamicDnsAccountDto account, CancellationToken cancellationToken)
    {
        account.AuthMode = DynamicDnsConfigService.NormalizeAuthMode("dynu", account.AuthMode);
        if (account.AuthMode == "oauth2")
        {
            var token = account.AccessToken;
            if (string.IsNullOrWhiteSpace(token) || account.AccessTokenExpiresAt <= DateTimeOffset.Now.AddMinutes(5))
            {
                var result = await RequestDynuOAuthTokenAsync(account, cancellationToken);
                if (!result.Success)
                {
                    throw new InvalidOperationException(result.Message);
                }

                token = result.AccessToken;
                await _configService.SaveAccountTokenAsync(account.Id, result.AccessToken, result.ExpiresAt, cancellationToken);
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return;
        }

        var apiKey = GetSecret(account.ApiKey, account.Password);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Dynu API scan cần API Key hoặc OAuth2 token.");
        }

        request.Headers.TryAddWithoutValidation("API-Key", apiKey);
    }

    private async Task<DynuOAuthResult> RequestDynuOAuthTokenAsync(DynamicDnsAccountDto account, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account.OAuthClientId) || string.IsNullOrWhiteSpace(account.OAuthSecret))
        {
            return new DynuOAuthResult(false, string.Empty, null, "Thiếu Dynu OAuth client_id hoặc secret.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.dynu.com/v2/oauth2/token");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{account.OAuthClientId}:{account.OAuthSecret}")));
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new DynuOAuthResult(false, string.Empty, null, "Dynu OAuth lỗi HTTP " + (int)response.StatusCode + ": " + TrimBody(body));
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var token = GetString(doc.RootElement, "access_token");
            var expiresIn = GetInt(doc.RootElement, "expires_in") ?? 28800;
            if (string.IsNullOrWhiteSpace(token))
            {
                return new DynuOAuthResult(false, string.Empty, null, "Dynu OAuth không trả về access_token.");
            }

            return new DynuOAuthResult(true, token, DateTimeOffset.Now.AddSeconds(Math.Max(60, expiresIn - 60)), "OK");
        }
        catch (Exception ex)
        {
            return new DynuOAuthResult(false, string.Empty, null, "Không đọc được phản hồi Dynu OAuth: " + ex.Message);
        }
    }

    private static void ApplyNoIpApiAuth(HttpRequestMessage request, string apiKey)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + apiKey)));
        request.Headers.UserAgent.ParseAdd("ConfigTool-DDNS/2.0 sanphamso-local-tool");
    }

    private static List<DynamicDnsDomainDto> ParseNoIpNameRecord(string zone, JsonElement item)
    {
        var records = new List<DynamicDnsDomainDto>();
        var rawName = FirstNonEmpty(GetString(item, "name"), GetString(item, "hostname"), GetString(item, "fqdn"));
        var host = BuildNoIpHostname(zone, rawName);
        var rrsets = EnumerateArray(item, "rrsets").ToList();
        if (rrsets.Count > 0)
        {
            foreach (var rrset in rrsets)
            {
                records.AddRange(ParseNoIpRrset(zone, host, rawName, rrset));
            }
        }
        else
        {
            records.AddRange(ParseNoIpRrset(zone, host, rawName, item));
        }

        return records;
    }

    private static List<DynamicDnsDomainDto> ParseNoIpRrset(string zone, string host, string rawName, JsonElement rrset)
    {
        var records = new List<DynamicDnsDomainDto>();
        var type = FirstNonEmpty(GetString(rrset, "dns_type"), GetString(rrset, "type"), GetString(rrset, "record_type")).ToUpperInvariant();
        if (type is not ("A" or "AAAA")) return records;
        var ttl = GetInt(rrset, "ttl");
        var rdata = EnumerateArray(rrset, "rdata").ToList();
        var content = string.Empty;
        if (rdata.Count > 0)
        {
            content = FirstNonEmpty(rdata.Select(x => FirstNonEmpty(GetString(x, "value"), GetString(x, "content"), GetString(x, "address"))).ToArray());
        }
        else
        {
            content = FirstNonEmpty(GetString(rrset, "value"), GetString(rrset, "content"), GetString(rrset, "address"));
        }

        records.Add(new DynamicDnsDomainDto
        {
            Id = DeterministicId("noip", zone, rawName, type, host),
            Hostname = host,
            ZoneName = zone,
            RecordName = rawName,
            RecordType = type,
            LastIp = content,
            Ttl = ttl,
            ScanSource = "noip-api",
            Enabled = true,
            LastScannedAt = DateTimeOffset.Now
        });
        return records;
    }

    private static string BuildNoIpHostname(string zone, string name)
    {
        name = (name ?? string.Empty).Trim().Trim('.');
        zone = (zone ?? string.Empty).Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(name) || name == "@") return zone;
        if (name.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase) || string.Equals(name, zone, StringComparison.OrdinalIgnoreCase)) return name;
        return name + "." + zone;
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

    private static bool JsonSuccess(string body, bool fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("success", out var success) && success.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return success.GetBoolean();
            }
        }
        catch
        {
            // ignore JSON parse and use fallback.
        }

        return fallback;
    }

    private static bool HasMoreCloudflarePages(JsonElement root, int currentPage)
    {
        if (!root.TryGetProperty("result_info", out var info) || info.ValueKind != JsonValueKind.Object) return false;
        var totalPages = GetInt(info, "total_pages") ?? currentPage;
        return currentPage < totalPages;
    }

    private static IEnumerable<JsonElement> EnumerateAnyDataArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray()) yield return item;
            yield break;
        }

        foreach (var name in new[] { "data", "result", "results", "domains", "records", "items" })
        {
            foreach (var item in EnumerateArray(root, name)) yield return item;
        }
    }

    private static IEnumerable<JsonElement> EnumerateArray(JsonElement root, string propertyName)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray()) yield return item;
        }
    }

    private static string GetString(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object) return string.Empty;
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
            if (value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) return value.ToString();
        }

        return string.Empty;
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)) return number;
        return null;
    }

    private static bool? GetBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value)) return null;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var result)) return result;
        return null;
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;

    private static string GetSecret(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;

    private static bool HasBasicAuth(DynamicDnsAccountDto account)
        => !string.IsNullOrWhiteSpace(account.Username) && !string.IsNullOrWhiteSpace(account.Password);

    private static string TrimBody(string body)
    {
        body = (body ?? string.Empty).Trim();
        return body.Length <= 500 ? body : body[..500] + "...";
    }

    private static string DeterministicId(params string?[] parts)
    {
        var raw = string.Join("|", parts.Select(x => (x ?? string.Empty).Trim().ToLowerInvariant()));
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..24].ToLowerInvariant();
    }

    private sealed record DynamicDnsTarget(DynamicDnsAccountDto Account, DynamicDnsDomainDto Domain);
    private sealed record CloudflareZone(string Id, string Name);
    private sealed record DynuOAuthResult(bool Success, string AccessToken, DateTimeOffset? ExpiresAt, string Message);
}
