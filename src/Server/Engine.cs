using System.Text.Json;
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

        var started = DateTimeOffset.Now;
        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(o.Executable, args, input, workDir, TimeSpan.FromMinutes(o.TimeoutMinutes), ct,
                psi =>
                {
                    // Set when the server is started from inside a Claude Code session; the child must not inherit it.
                    psi.Environment.Remove("CLAUDECODE");
                    psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");
                });
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            logger.LogWarning("claude {Prompt} ended without an answer after {Seconds:0}s: {Reason}",
                Path.GetFileName(systemPromptFile), (DateTimeOffset.Now - started).TotalSeconds, ex.Message);
            throw;
        }
        logger.LogInformation("claude {Prompt} exited with {Code} after {Seconds:0}s ({Out:N0} chars out){Err}",
            Path.GetFileName(systemPromptFile), result.ExitCode, (DateTimeOffset.Now - started).TotalSeconds, result.StdOut.Length,
            string.IsNullOrWhiteSpace(result.StdErr) ? "" : "; stderr: " + ProcessRunner.Tail(result.StdErr, 400));

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

        var answer = new ClaudeResult(
            text,
            root.TryGetProperty("duration_ms", out var d) && d.TryGetInt64(out var ms) ? ms : 0,
            root.TryGetProperty("total_cost_usd", out var c) && c.TryGetDecimal(out var usd) ? usd : null);
        logger.LogInformation("claude {Prompt} answered: API {Seconds:0}s, ${Cost:0.00}, {Chars:N0} chars",
            Path.GetFileName(systemPromptFile), answer.DurationMs / 1000.0, answer.CostUsd ?? 0, text.Length);
        return answer;
    }
}
