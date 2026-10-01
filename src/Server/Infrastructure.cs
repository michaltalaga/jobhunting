using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JobHunting.Server;

public static class Json
{
    /// <summary>For files on disk: indented, and Polish characters kept readable.</summary>
    public static readonly JsonSerializerOptions Files = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>Single-line, for server-sent events.</summary>
    public static readonly JsonSerializerOptions Compact = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    /// <summary>An enum value as it appears in JSON ("notApplied").</summary>
    public static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}

public static class Files
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>Writes via a temp file so readers never see a half-written file.</summary>
    public static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, Utf8NoBom);
        File.Move(tmp, path, overwrite: true);
    }

    public static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, Utf8NoBom, ct);
        File.Move(tmp, path, overwrite: true);
    }

    public static string? ReadIfExists(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
}

public static class Slug
{
    public static string Make(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        // 'ł' has no Unicode decomposition, so FormD alone would drop it.
        var normalized = value.Replace('ł', 'l').Replace('Ł', 'L').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length > maxLength ? slug[..maxLength].TrimEnd('-') : slug;
    }
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? stdin,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct,
        Action<ProcessStartInfo>? configure = null)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (stdin is not null) psi.StandardInputEncoding = new UTF8Encoding(false);
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        configure?.Invoke(psi);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {executable}.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), cts.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"{Path.GetFileName(executable)} did not finish within {timeout.TotalMinutes:0} minutes.");
        }
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    public static string Tail(string text, int max = 1500) =>
        text.Length <= max ? text.Trim() : "…" + text[^max..].Trim();
}

/// <summary>Pulls &lt;tag&gt;…&lt;/tag&gt; sections out of a model response.</summary>
public static class TaggedOutput
{
    public static string? Get(string text, string tag)
    {
        var open = $"<{tag}>";
        var close = $"</{tag}>";
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return null;
        start += open.Length;
        var end = text.LastIndexOf(close, StringComparison.Ordinal);
        if (end < start) return null;
        return StripFence(text[start..end].Trim());
    }

    private static string StripFence(string content)
    {
        if (!content.StartsWith("```", StringComparison.Ordinal) || !content.EndsWith("```", StringComparison.Ordinal)) return content;
        var firstNewline = content.IndexOf('\n');
        if (firstNewline < 0) return content;
        return content[(firstNewline + 1)..^3].Trim();
    }
}
