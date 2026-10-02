using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace JobHunting.Server;

public sealed record ThemeInfo(string Id, string Name, string? Description, bool Personal);

public sealed record ThemeList(string Default, IReadOnlyList<ThemeInfo> Themes);

/// <summary>
/// Themes are folders holding theme.css (required) and theme.json (name, description).
/// Built-ins live in themes/; personal ones in data/themes/ and win on an id clash.
/// </summary>
public sealed class ThemeCatalog(Paths paths, IOptions<JobHuntingOptions> options)
{
    private sealed record Manifest(string? Name, string? Description);

    public ThemeList List()
    {
        var themes = new Dictionary<string, ThemeInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, personal) in new[] { (paths.Themes, false), (paths.PersonalThemes, true) })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root).Where(d => File.Exists(Path.Combine(d, "theme.css"))))
            {
                var id = Path.GetFileName(dir);
                var manifest = ReadManifest(dir);
                themes[id] = new ThemeInfo(id, manifest?.Name ?? id, manifest?.Description, personal);
            }
        }
        return new ThemeList(Resolve(null) ?? "", [.. themes.Values.OrderBy(t => t.Name)]);
    }

    public string? Folder(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return null;
        return new[] { paths.PersonalThemes, paths.Themes }
            .Select(root => Path.Combine(root, id))
            .FirstOrDefault(dir => File.Exists(Path.Combine(dir, "theme.css")));
    }

    /// <summary>The theme to use: the requested one if it exists, else the configured default, else any.</summary>
    public string? Resolve(string? requested)
    {
        if (Folder(requested) is not null) return requested;
        var fallback = options.Value.Render.Theme;
        if (Folder(fallback) is not null) return fallback;
        if (!Directory.Exists(paths.Themes)) return null;
        return Directory.EnumerateDirectories(paths.Themes)
            .Select(dir => Path.GetFileName(dir))
            .Order(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(id => Folder(id) is not null);
    }

    private static Manifest? ReadManifest(string dir)
    {
        var file = Path.Combine(dir, "theme.json");
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(file), Json.Files) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public static class ChromeLocator
{
    /// <summary>The configured browser if it exists, else the first Chrome/Edge/Chromium found in the usual places.</summary>
    public static string? Find(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;

        string[] candidates;
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates =
            [
                Path.Combine(programFiles, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(programFilesX86, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(localAppData, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(programFilesX86, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(programFiles, @"Microsoft\Edge\Application\msedge.exe"),
            ];
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates =
            [
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                "/Applications/Chromium.app/Contents/MacOS/Chromium",
                "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
            ];
        }
        else
        {
            candidates = ["/usr/bin/google-chrome", "/usr/bin/google-chrome-stable", "/usr/bin/chromium", "/usr/bin/chromium-browser", "/snap/bin/chromium"];
        }
        return candidates.FirstOrDefault(File.Exists);
    }
}

/// <summary>
/// Prints the dashboard's print page (print.html) to resume.pdf with headless Chrome, so the PDF is
/// exactly what the job page previews.
/// </summary>
public sealed partial class ResumeRenderer(
    IOptions<JobHuntingOptions> options, ThemeCatalog themes, IServer server, ILogger<ResumeRenderer> logger)
{
    // Chrome profiles are reused, one per concurrent render. A fresh profile per render makes Chrome do first-run work
    // (component downloads) that can leave --print-to-pdf waiting forever; sharing one between parallel renders fails.
    private readonly System.Collections.Concurrent.ConcurrentBag<string> _idleProfiles = [];
    private int _profileCount;

    public string? Chrome => ChromeLocator.Find(options.Value.Render.ChromePath);

    public bool IsAvailable => options.Value.Render.Enabled && Chrome is not null;

    /// <summary>Writes resume.pdf; returns its page count and the theme actually used.</summary>
    public async Task<(int Pages, string Theme)> RenderAsync(string jobId, string folder, string? theme, CancellationToken ct)
    {
        var chrome = Chrome ?? throw new InvalidOperationException("Chrome was not found. Install it, or set JobHunting:Render:ChromePath.");
        var themeId = themes.Resolve(theme) ?? throw new InvalidOperationException("No themes found in themes/ or data/themes/.");
        var url = $"{BaseUrl()}/print.html?job={Uri.EscapeDataString(jobId)}&theme={Uri.EscapeDataString(themeId)}";
        var pdf = Path.Combine(folder, "resume.pdf");
        var tmp = Path.Combine(folder, "resume.tmp.pdf");

        // Our own profile (a running Chrome would otherwise pick up the request), taken from the pool for this render.
        var profile = _idleProfiles.TryTake(out var idle)
            ? idle
            : Path.Combine(Path.GetTempPath(), "jobhunting-chrome", $"slot-{Interlocked.Increment(ref _profileCount)}");
        try
        {
            // One retry: a render that times out is usually fine the second time.
            for (var attempt = 1; ; attempt++)
            {
                File.Delete(tmp);
                var started = DateTimeOffset.Now;
                try
                {
                    var result = await ProcessRunner.RunAsync(chrome, Args(profile, tmp, url), null, Path.GetTempPath(), TimeSpan.FromMinutes(1), ct);
                    if (!File.Exists(tmp) || new FileInfo(tmp).Length == 0)
                        throw new InvalidOperationException($"Chrome produced no PDF (exit code {result.ExitCode}). {ProcessRunner.Tail(result.StdErr, 500)}");
                    logger.LogInformation("Rendered job {Id} in {Seconds:0.0}s ({Profile})", jobId, (DateTimeOffset.Now - started).TotalSeconds, Path.GetFileName(profile));
                    break;
                }
                catch (Exception ex) when (attempt == 1 && ex is TimeoutException or InvalidOperationException && !ct.IsCancellationRequested)
                {
                    logger.LogWarning("Rendering job {Id} failed after {Seconds:0}s, retrying: {Reason}", jobId, (DateTimeOffset.Now - started).TotalSeconds, ex.Message);
                }
            }
        }
        finally
        {
            _idleProfiles.Add(profile);
        }
        File.Move(tmp, pdf, overwrite: true);
        return (CountPages(pdf), themeId);
    }

    private static string[] Args(string profile, string tmp, string url) =>
    [
        "--headless",
        "--disable-gpu",
        "--disable-extensions",
        "--no-first-run",
        "--no-default-browser-check",
        // No background traffic (component updates, sync, telemetry): print mode waits for pending fetches.
        "--disable-background-networking",
        "--disable-component-update",
        "--disable-default-apps",
        "--disable-sync",
        "--disable-domain-reliability",
        "--disable-client-side-phishing-detection",
        "--disable-breakpad",
        $"--user-data-dir={profile}",
        "--no-pdf-header-footer",
        // Lets the page fetch the resume and load the theme before printing...
        "--virtual-time-budget=15000",
        // ...and if anything is still pending after 20 s, print what is there rather than wait forever.
        "--timeout=20000",
        $"--print-to-pdf={tmp}",
        url,
    ];

    /// <summary>Chrome writes one uncompressed "/Type /Page" dictionary per page.</summary>
    private static int CountPages(string pdf) =>
        PageObject().Matches(System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(pdf))).Count;

    [System.Text.RegularExpressions.GeneratedRegex(@"/Type\s*/Page(?![A-Za-z])")]
    private static partial System.Text.RegularExpressions.Regex PageObject();

    private string BaseUrl()
    {
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault() ?? "http://localhost:5317";
        // Wildcard bindings aren't browsable addresses.
        foreach (var wildcard in new[] { "://+", "://*", "://0.0.0.0", "://[::]" })
            address = address.Replace(wildcard, "://localhost");
        return address.TrimEnd('/');
    }
}
