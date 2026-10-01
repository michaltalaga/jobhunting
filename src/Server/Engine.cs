using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace JobHunting.Server;

public sealed record ClaudeResult(string Text, long DurationMs, decimal? CostUsd);

/// <summary>Runs the headless Claude CLI: system prompt from a file, input on stdin, no tools.</summary>
public sealed class ClaudeRunner(IOptions<JobHuntingOptions> options, ILogger<ClaudeRunner> logger)
{
    private string? _version;

    /// <summary>`claude --version`, or null when the CLI isn't installed. Only a success is cached.</summary>
    public async Task<string?> VersionAsync()
    {
        if (_version is not null) return _version;
        try
        {
            var result = await ProcessRunner.RunAsync(options.Value.Claude.Executable, ["--version"], null,
                Path.GetTempPath(), TimeSpan.FromSeconds(30), CancellationToken.None);
            return result.ExitCode == 0 ? _version = result.StdOut.Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // not on PATH
        }
    }

    public async Task<ClaudeResult> RunAsync(string systemPromptFile, string input, ClaudeStepOptions step, CancellationToken ct)
    {
        var o = options.Value.Claude;
        if (!File.Exists(systemPromptFile)) throw new FileNotFoundException("Prompt file not found.", systemPromptFile);

        // A neutral working directory keeps any project CLAUDE.md out of the engine's context.
        var workDir = Path.Combine(Path.GetTempPath(), "jobhunting-engine");
        Directory.CreateDirectory(workDir);

        string[] args =
        [
            "-p",
            "--model", step.Model,
            "--effort", step.Effort,
            "--output-format", "json",
            "--tools", "",
            "--no-session-persistence",
            "--system-prompt-file", systemPromptFile,
        ];

        logger.LogInformation("claude {Prompt} ({Model}, {Effort}) with {Chars:N0} chars of input",
            Path.GetFileName(systemPromptFile), step.Model, step.Effort, input.Length);

        var result = await ProcessRunner.RunAsync(o.Executable, args, input, workDir, TimeSpan.FromMinutes(o.TimeoutMinutes), ct,
            psi =>
            {
                // Set when the server is started from inside a Claude Code session; the child must not inherit it.
                psi.Environment.Remove("CLAUDECODE");
                psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");
            });

        var jsonStart = result.StdOut.IndexOf('{');
        if (jsonStart < 0)
            throw new InvalidOperationException($"claude exited with code {result.ExitCode}: {ProcessRunner.Tail(result.StdErr + result.StdOut)}");

        using var doc = JsonDocument.Parse(result.StdOut[jsonStart..]);
        var root = doc.RootElement;
        var text = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "";

        if (root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException($"claude reported an error: {ProcessRunner.Tail(text)}");
        if (root.TryGetProperty("stop_reason", out var stop) && stop.GetString() == "max_tokens")
            throw new InvalidOperationException("claude's answer was cut off (max output tokens reached).");

        return new ClaudeResult(
            text,
            root.TryGetProperty("duration_ms", out var d) && d.TryGetInt64(out var ms) ? ms : 0,
            root.TryGetProperty("total_cost_usd", out var c) && c.TryGetDecimal(out var usd) ? usd : null);
    }
}

/// <summary>Runs tool/render.mjs (JSON Resume theme + Chrome) to produce resume.pdf.</summary>
public sealed class ResumeRenderer(Paths paths, IOptions<JobHuntingOptions> options)
{
    public bool IsAvailable => options.Value.Render.Enabled && File.Exists(paths.RenderScript);

    public async Task RenderAsync(string folder, CancellationToken ct)
    {
        var o = options.Value.Render;
        var pdf = Path.Combine(folder, "resume.pdf");
        var args = new List<string> { paths.RenderScript, "--in", Path.Combine(folder, "resume.json"), "--out", pdf, "--theme", o.Theme };
        if (!string.IsNullOrWhiteSpace(o.ChromePath)) args.AddRange(["--chrome", o.ChromePath]);

        var result = await ProcessRunner.RunAsync(o.Node, args, null, Path.GetDirectoryName(paths.RenderScript)!, TimeSpan.FromMinutes(3), ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"render failed: {ProcessRunner.Tail(result.StdErr + result.StdOut)}");
        if (!File.Exists(pdf))
            throw new InvalidOperationException("render finished but produced no resume.pdf.");
    }
}

/// <summary>Deterministic guard rails applied to every tailored resume.</summary>
public static class ResumeChecks
{
    private static readonly string[] ContactFields = ["name", "email", "phone", "url", "image", "location", "profiles"];

    public static (string Json, List<string> Warnings) Apply(string tailoredJson, string masterJson)
    {
        var tailored = JsonNode.Parse(tailoredJson) as JsonObject
            ?? throw new InvalidOperationException("The tailored resume is not a JSON object.");
        var master = JsonNode.Parse(masterJson) as JsonObject
            ?? throw new InvalidOperationException("The master resume is not a JSON object.");
        var warnings = new List<string>();

        // Contact details always come from the master resume, never from the model.
        if (master["basics"] is JsonObject masterBasics)
        {
            if (tailored["basics"] is not JsonObject basics) tailored["basics"] = basics = [];
            foreach (var field in ContactFields)
                if (masterBasics[field] is { } value) basics[field] = value.DeepClone();
        }

        CheckEntries(tailored, master, "work", e => Str(e, "name") ?? Str(e, "company"), "position", warnings);
        CheckEntries(tailored, master, "education", e => Str(e, "institution"), "studyType", warnings);

        return (tailored.ToJsonString(Json.Files), warnings);
    }

    /// <summary>Employers, titles and dates must match the master resume exactly.</summary>
    private static void CheckEntries(JsonObject tailored, JsonObject master, string section,
        Func<JsonObject, string?> nameOf, string titleField, List<string> warnings)
    {
        var masterEntries = (master[section] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        foreach (var entry in (tailored[section] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var name = nameOf(entry);
            var start = Str(entry, "startDate");
            var match = masterEntries.FirstOrDefault(m => Same(nameOf(m), name) && Str(m, "startDate") == start);
            if (match is null)
            {
                warnings.Add($"{section}: \"{name}\" (start {start ?? "?"}) does not match any entry in the master resume.");
                continue;
            }
            if (!Same(Str(match, titleField), Str(entry, titleField)))
                warnings.Add($"{section}: \"{name}\" {titleField} changed from \"{Str(match, titleField)}\" to \"{Str(entry, titleField)}\".");
            if (Str(match, "endDate") != Str(entry, "endDate"))
                warnings.Add($"{section}: \"{name}\" end date changed from \"{Str(match, "endDate") ?? "present"}\" to \"{Str(entry, "endDate") ?? "present"}\".");
        }
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
