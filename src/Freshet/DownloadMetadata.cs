using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Freshet;

public static class DownloadSizes
{
    public static string Format(decimal bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        var (value, unit) = bytes >= 1_000_000_000 ? (bytes / 1_000_000_000, "GB") :
            bytes >= 1_000_000 ? (bytes / 1_000_000, "MB") : bytes >= 1_000 ? (bytes / 1_000, "KB") : (bytes, bytes == 0 ? "KB" : "B");
        return value.ToString("0.#", CultureInfo.InvariantCulture) + " " + unit;
    }
    public static string Summary(IEnumerable<PackageEntry> entries, bool selectedOnly = true)
    {
        var selected = entries.Where(p => (!selectedOnly || p.Selected) && !p.Excluded).ToList();
        if (selected.Count == 0) return "예상 다운로드 —";
        var known = selected.Where(p => p.EstimatedBytes.HasValue).ToList();
        int pending = selected.Count(p => !p.EstimatedBytes.HasValue && p.SizeState == "pending");
        int unknown = selected.Count - known.Count - pending;
        var parts = new List<string> { known.Count == 0 ? "예상 다운로드" : "예상 다운로드 약 " + Format(known.Sum(p => (decimal)p.EstimatedBytes!.Value)) };
        if (unknown > 0) parts.Add($"용량 미확인 {unknown}개");
        if (pending > 0) parts.Add($"조회 중 {pending}개");
        return string.Join(" · ", parts);
    }
}

public sealed record SizeEstimate(long? Bytes, string Note);

public sealed class DownloadMetadata : IDisposable
{
    private readonly IUpdateCommands commands;
    private readonly HttpClient client;
    private readonly string? cachePath;
    private readonly Dictionary<string, CachedSize> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object cacheLock = new();
    public DownloadMetadata(IUpdateCommands commands, string? root = null, HttpMessageHandler? handler = null)
    {
        this.commands = commands;
        client = new HttpClient(handler ?? CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Freshet/" + typeof(DownloadMetadata).Assembly.GetName().Version!.ToString(3));
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");
        cachePath = root is null ? null : Path.Combine(root, "download-size-cache-v1.json");
        if (cachePath is not null)
        {
            try
            {
                if (File.Exists(cachePath) && new FileInfo(cachePath).Length <= 512_000)
                    foreach (var c in JsonSerializer.Deserialize<List<CachedSize>>(File.ReadAllText(cachePath)) ?? [])
                        if (!string.IsNullOrWhiteSpace(c.Key) && c.Bytes >= 0 && c.CheckedAt > DateTimeOffset.UtcNow.AddHours(-12) && c.CheckedAt <= DateTimeOffset.UtcNow.AddMinutes(5)) cache[c.Key] = c;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
    }
    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        ConnectCallback = async (context, token) => {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
            if (addresses.Length == 0 || addresses.Any(a => !IsPublicAddress(a))) throw new HttpRequestException("공개 배포 서버 주소만 조회합니다.");
            Exception? last = null;
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token); return new NetworkStream(socket, ownsSocket: true); }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException) { socket.Dispose(); last = ex; token.ThrowIfCancellationRequested(); }
            }
            throw new HttpRequestException("배포 서버에 연결하지 못했습니다.", last);
        }
    };
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal) return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return (b[0] & 0xE0) == 0x20;
        return b[0] is not (0 or 10 or 127) && b[0] < 224 && !(b[0] == 169 && b[1] == 254) &&
            !(b[0] == 172 && b[1] is >= 16 and <= 31) && !(b[0] == 192 && b[1] == 168) &&
            !(b[0] == 100 && b[1] is >= 64 and <= 127) && !(b[0] == 198 && b[1] is 18 or 19);
    }
    public static bool IsSafeUrl(Uri uri) => uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0 &&
        uri.Host.Length > 0 && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
        !uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) &&
        (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || IsPublicAddress(address));

    public static Uri? ParseInstallerUrl(string text)
    {
        // Only accept the labelled installer field, never a homepage or release-note URL.
        var match = Regex.Match(WingetTable.Clean(text), @"^\s*(?:Installer\s+Url|설치\s*관리자\s*URL)\s*:\s*(https?://\S+)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return match.Success && Uri.TryCreate(match.Groups[1].Value.Trim(), UriKind.Absolute, out var uri) ? uri : null;
    }
    private static string CacheKey(PackageEntry p) => string.Join("|", p.Source, p.Id, p.Installed, p.Available,
        RuntimeInformation.OSArchitecture, CultureInfo.CurrentUICulture.Name, Environment.OSVersion.Version);

    public async Task<SizeEstimate> EstimateAsync(PackageEntry p, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        string key = CacheKey(p);
        lock (cacheLock)
            if (cache.TryGetValue(key, out var found) && found.CheckedAt > DateTimeOffset.UtcNow.AddHours(-12))
                return new(found.Bytes, $"HEAD로 확인한 설치 파일 용량 · {found.CheckedAt.ToLocalTime():MM.dd HH:mm} 조회 캐시. 추가 다운로드와 의존 패키지는 별도입니다.");
        if (!WingetTable.IsIdentifier(p.Id) || !WingetTable.IsIdentifier(p.Source) || string.IsNullOrWhiteSpace(p.Available)) return new(null, "패키지 정보가 불완전합니다.");
        try
        {
            var shown = await commands.RunAsync(["show", "--id", p.Id, "--exact", "--source", p.Source, "--version", p.Available, "--disable-interactivity"], token: token, timeout: TimeSpan.FromSeconds(50));
            if (shown.ExitCode != 0) return new(null, $"설치 파일 정보 조회 실패 (0x{shown.ExitCode:X8}). 업데이트 자체는 시도할 수 있습니다.");
            Uri? uri = ParseInstallerUrl(shown.Output);
            if (uri is null) return new(null, "winget이 설치 파일 URL을 제공하지 않았습니다. Store 앱 또는 별도 설치 방식일 수 있습니다.");
            var estimate = await ProbeAsync(uri, token);
            if (estimate.Bytes is long size)
            {
                lock (cacheLock)
                {
                    cache[key] = new(key, size, DateTimeOffset.UtcNow);
                    if (cachePath is not null)
                        try { Storage.AtomicWrite(cachePath, JsonSerializer.Serialize(cache.Values.OrderByDescending(x => x.CheckedAt).Take(256))); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            return estimate;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or TaskCanceledException or IOException or System.ComponentModel.Win32Exception)
        { return new(null, "용량을 확인하지 못했습니다. 서버·네트워크 상태에 따라 달라집니다. " + ex.Message); }
    }
    public async Task<SizeEstimate> ProbeAsync(Uri uri, CancellationToken token = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        for (int hop = 0; hop < 6; hop++)
        {
            budget.Token.ThrowIfCancellationRequested();
            if (!IsSafeUrl(uri)) return new(null, "용량 조회는 공개 HTTPS 배포 서버만 지원합니다.");
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                if (location is null) return new(null, "다운로드 서버가 이동 주소를 제공하지 않았습니다.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            if (!response.IsSuccessStatusCode) return new(null, $"서버에서 용량 조회를 지원하지 않습니다 (HTTP {(int)response.StatusCode}).");
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true || media?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
                return new(null, "설치 파일 대신 안내 페이지가 반환되어 용량을 표시하지 않습니다.");
            return response.Content.Headers.ContentLength is long length && length >= 0 ?
                new(length, "HEAD 응답의 설치 파일 용량입니다. 실제 설치 파일 선택에 따라 달라질 수 있으며 추가 다운로드·의존 패키지는 포함하지 않습니다.") :
                new(null, "서버가 설치 파일 크기를 제공하지 않았습니다. 파일 본문은 다운로드하지 않았습니다.");
        }
        return new(null, "다운로드 서버의 이동 횟수가 너무 많아 용량을 확인하지 못했습니다.");
    }
    public void Dispose() => client.Dispose();
    public sealed record CachedSize(string Key, long Bytes, DateTimeOffset CheckedAt);
}
