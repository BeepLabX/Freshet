using System.IO;
using System.Net;
using System.Net.Http;

namespace Freshet;

public static class MetadataTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        check(DownloadSizes.Format(1_000) == "1 KB" && DownloadSizes.Format(1_000_000) == "1 MB" && DownloadSizes.Format(1_000_000_000) == "1 GB", "Decimal KB/MB/GB unit boundaries");
        check(DownloadSizes.Format(66_837_216) == "66.8 MB" && !DownloadSizes.Format(66_837_216).Contains("MiB"), "File size uses familiar decimal units");
        var p = new PackageEntry { Name = "Git", Id = "Git.Git", Source = "winget", Installed = "1.0", Available = "2.0", Selected = true, EstimatedBytes = 66_837_216, SizeState = "known" };
        var q = new PackageEntry { Name = "CLI", Selected = true, EstimatedBytes = 16_800_000, SizeState = "known" };
        check(DownloadSizes.Summary([p, q]) == "예상 다운로드 약 83.6 MB", "Selected package sizes are summed");
        q.EstimatedBytes = null; q.SizeState = "unavailable";
        check(DownloadSizes.Summary([p, q]).Contains("66.8 MB · 용량 미확인 1개"), "Unknown packages remain visible beside the known total");
        check(DownloadSizes.Summary([q]) == "예상 다운로드 · 용량 미확인 1개", "All unknown sizes never appear as zero download");
        q.SizeState = "pending";
        q.Selected = false;
        check(DownloadSizes.Summary([q], selectedOnly: false).Contains("조회 중 1개") && DownloadSizes.Summary([q]) == "예상 다운로드 —", "Review snapshot total is independent of selection flags; pending stays distinct");
        q.Selected = true;
        p.Excluded = true;
        check(!DownloadSizes.Summary([p, q]).Contains("66.8"), "Held packages are excluded from the size total");
        p.Excluded = false; p.Selected = true;
        check(DownloadMetadata.ParseInstallerUrl("Publisher Url: https://wrong.test/\n  Installer Url: https://downloads.test/setup.exe\n")?.Host == "downloads.test", "Only the labelled installer URL is parsed");
        check(DownloadMetadata.ParseInstallerUrl("  설치 관리자 URL: https://downloads.test/setup.exe\r\n")?.AbsolutePath == "/setup.exe", "Korean installer URL field");
        check(DownloadMetadata.ParseInstallerUrl("Release Notes Url: https://wrong.test/") is null, "Homepages and release notes cannot become installer estimates");
        check(!DownloadMetadata.IsSafeUrl(new("http://downloads.test/a")) && !DownloadMetadata.IsSafeUrl(new("https://127.0.0.1/a")) && !DownloadMetadata.IsSafeUrl(new("https://user:password@downloads.test/a")), "HTTP, private targets and credential-bearing URLs rejected");
        check(!DownloadMetadata.IsPublicAddress(IPAddress.Parse("::ffff:192.168.1.1")) && !DownloadMetadata.IsPublicAddress(IPAddress.Parse("fe80::1")) && DownloadMetadata.IsPublicAddress(IPAddress.Parse("8.8.8.8")), "DNS connection policy covers IPv4-mapped and link-local addresses");
        var handler = new FakeHeaders(_ => Response(HttpStatusCode.OK, 66_837_216));
        var commands = new FakeShow();
        string cache = Path.Combine(root, "metadata-cache"); Directory.CreateDirectory(cache);
        using (var service = new DownloadMetadata(commands, cache, handler))
        {
            var estimate = service.EstimateAsync(p).GetAwaiter().GetResult();
            check(estimate.Bytes == 66_837_216 && handler.Methods.SequenceEqual(["HEAD"]), "Successful HEAD estimate reads no installer body");
            check(commands.LastArguments.Contains("--exact") && commands.LastArguments[Array.IndexOf(commands.LastArguments, "--version") + 1] == "2.0", "Metadata query uses the selected exact target version");
            service.EstimateAsync(p).GetAwaiter().GetResult();
            check(commands.Calls == 1, "Unchanged package uses the local size cache");
            p.Available = "3.0";
            service.EstimateAsync(p).GetAwaiter().GetResult();
            check(commands.Calls == 2, "Changing target version invalidates the size cache");
            p.Installed = "1.1";
            service.EstimateAsync(p).GetAwaiter().GetResult();
            check(commands.Calls == 3, "Changing installed context invalidates the size cache");
        }
        var cachedCommands = new FakeShow();
        using (var service = new DownloadMetadata(cachedCommands, cache, new FakeHeaders(_ => throw new InvalidOperationException("Network should not be used"))))
            check(service.EstimateAsync(p).GetAwaiter().GetResult().Bytes == 66_837_216 && cachedCommands.Calls == 0, "Successful size cache survives an app restart");
        foreach (var (status, length, type, name) in new[] {
            (HttpStatusCode.MethodNotAllowed, (long?)null, "application/octet-stream", "HEAD refusal stays unknown and never falls back to GET"),
            (HttpStatusCode.OK, (long?)null, "application/octet-stream", "Missing Content-Length stays unknown"),
            (HttpStatusCode.OK, (long?)500, "text/html", "HTML error pages are not counted as installers") })
        {
            var fake = new FakeHeaders(_ => Response(status, length, type));
            using var service = new DownloadMetadata(new FakeShow(), handler: fake);
            check(service.ProbeAsync(new("https://downloads.test/a")).GetAwaiter().GetResult().Bytes is null && fake.Methods.All(m => m == "HEAD"), name);
        }
        using (var service = new DownloadMetadata(new FakeShow(), handler: new FakeHeaders(_ => Response(HttpStatusCode.OK, 0))))
            check(service.ProbeAsync(new("https://downloads.test/a")).GetAwaiter().GetResult().Bytes == 0, "Explicit zero Content-Length differs from missing length");
        int redirects = 0;
        using (var service = new DownloadMetadata(new FakeShow(), handler: new FakeHeaders(_ => {
            redirects++;
            if (redirects == 1) { var response = Response(HttpStatusCode.Redirect, null); response.Headers.Location = new Uri("/final", UriKind.Relative); return response; }
            return Response(HttpStatusCode.OK, 42_000);
        }))) check(service.ProbeAsync(new("https://downloads.test/a")).GetAwaiter().GetResult().Bytes == 42_000 && redirects == 2, "Relative HTTPS redirect is resolved without a body download");
        var privateRedirect = new FakeHeaders(_ => { var response = Response(HttpStatusCode.Redirect, null); response.Headers.Location = new Uri("https://192.168.1.1/a"); return response; });
        using (var service = new DownloadMetadata(new FakeShow(), handler: privateRedirect))
            check(service.ProbeAsync(new("https://downloads.test/a")).GetAwaiter().GetResult().Bytes is null && privateRedirect.Methods.Count == 1, "Private redirect is blocked before making another request");
        using (var service = new DownloadMetadata(new FakeShow { Exit = 1 }, handler: new FakeHeaders(_ => throw new InvalidOperationException("No request expected"))))
            check(service.EstimateAsync(p).GetAwaiter().GetResult().Bytes is null, "winget show failure does not trigger an unrelated network probe");
        using (var cancel = new CancellationTokenSource())
        using (var service = new DownloadMetadata(new FakeShow(), handler: new FakeHeaders(_ => Response(HttpStatusCode.OK, 1))))
        {
            cancel.Cancel(); bool cancelled = false;
            try { service.ProbeAsync(new("https://downloads.test/a"), cancel.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "Metadata probes honor cancellation");
        }
        var reference = AppIcons.ParseReference("\"C:\\Program Files\\한글 앱\\app.exe\",-2");
        check(reference is { Path: "C:\\Program Files\\한글 앱\\app.exe", Index: -2 }, "Quoted icon references preserve Unicode paths and resource index");
        check(AppIcons.ParseReference(@"\\server\share\app.exe") is null && AppIcons.ParseReference("https://example.test/a.ico") is null && AppIcons.ParseReference(@"C:\app.exe --run") is null, "Remote paths and command strings are never executed as icons");
        var registry = new[] { new InstalledIcon("Git version 1", "Git.Git", @"C:\Git\git.exe"), new InstalledIcon("Other", "Other.App", @"C:\Other\app.exe") };
        check(AppIcons.Match(p, registry)?.Identifier == "Git.Git", "Exact package identifier wins icon matching");
        var notion = new PackageEntry { Name = "Notion 7.39.0", Id = "Notion.Notion" };
        check(AppIcons.Match(notion, [new("Notion", "random", @"C:\Notion\Notion.exe")]) is not null, "Versioned display names can match a unique installed app");
        check(AppIcons.Match(notion, [new("Notion", "a", @"C:\A\a.exe"), new("Notion", "b", @"C:\B\b.exe")]) is null, "Ambiguous icon matches use a fallback");
        check(AppIcons.Default.IsFrozen && AppIcons.LoadReference(@"C:\nonexistent-freshet-test.exe") is null, "Fallback icons are thread-safe and missing files do not crash");
        var whiteIcon = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { 255, 255, 255, 255 }, 4);
        whiteIcon.Freeze();
        check(AppIcons.BackgroundFor(whiteIcon) is System.Windows.Media.SolidColorBrush background && background.Color.B < 150, "White icons receive a visible contrasting background");
    }
    private static HttpResponseMessage Response(HttpStatusCode status, long? length, string type = "application/octet-stream")
    {
        var response = new HttpResponseMessage(status) { Content = new NeverReadContent() };
        response.Content.Headers.ContentType = new(type);
        if (length.HasValue) response.Content.Headers.ContentLength = length;
        return response;
    }
    private sealed class NeverReadContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("Installer body must never be read");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    private sealed class FakeHeaders(Func<HttpRequestMessage, HttpResponseMessage> factory) : HttpMessageHandler
    {
        public List<string> Methods { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Methods.Add(request.Method.Method); return Task.FromResult(factory(request));
        }
    }
    private sealed class FakeShow : IUpdateCommands
    {
        public int Calls { get; private set; }
        public int Exit { get; init; }
        public string[] LastArguments { get; private set; } = [];
        public Task<CommandResult> RunAsync(IEnumerable<string> args, Action<string>? progress = null, CancellationToken token = default, TimeSpan? timeout = null)
        { token.ThrowIfCancellationRequested(); Calls++; LastArguments = args.ToArray(); return Task.FromResult(new CommandResult(Exit, "Installer Url: https://downloads.test/setup.exe\n", "")); }
        public Task<string> InstalledVersionAsync(PackageEntry p) => Task.FromResult("");
    }
}
