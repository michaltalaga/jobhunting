namespace JobHunting.Server;

public sealed class JobHuntingOptions
{
    public const string Section = "JobHunting";

    /// <summary>Repository root, relative to the server's content root.</summary>
    public string RepoRoot { get; set; } = "../..";

    // The paths below are relative to RepoRoot. Everything personal lives under data/.
    public string ApplicationsDir { get; set; } = "data/applications";
    public string PromptsDir { get; set; } = "prompts";
    public string MasterResumePath { get; set; } = "data/resume.json";
    /// <summary>Optional folder of .md/.txt files with extra career material, all passed to tailoring.</summary>
    public string BackgroundDir { get; set; } = "data/background";

    public ClaudeOptions Claude { get; set; } = new();
    public RenderOptions Render { get; set; } = new();
}

public sealed class ClaudeOptions
{
    public string Executable { get; set; } = "claude";
    public int TimeoutMinutes { get; set; } = 30;
    public ClaudeStepOptions Extract { get; set; } = new();
    public ClaudeStepOptions Tailor { get; set; } = new();
}

public sealed class ClaudeStepOptions
{
    public string Model { get; set; } = "claude-opus-5-5";
    public string Effort { get; set; } = "xhigh";
}

public sealed class RenderOptions
{
    public bool Enabled { get; set; } = true;
    public string Node { get; set; } = "node";
    public string Script { get; set; } = "tool/render.mjs";
    public string Theme { get; set; } = "jsonresume-theme-even";
    public string? ChromePath { get; set; }
}

/// <summary>Absolute paths resolved from <see cref="JobHuntingOptions"/>.</summary>
public sealed class Paths
{
    public Paths(JobHuntingOptions options, string contentRoot)
    {
        // GetFullPath everywhere: folder comparisons rely on normalised separators.
        RepoRoot = Path.GetFullPath(Path.Combine(contentRoot, options.RepoRoot));
        Applications = Path.GetFullPath(Path.Combine(RepoRoot, options.ApplicationsDir));
        Inbox = Path.Combine(Applications, "_inbox");
        Prompts = Path.GetFullPath(Path.Combine(RepoRoot, options.PromptsDir));
        MasterResume = Path.GetFullPath(Path.Combine(RepoRoot, options.MasterResumePath));
        Background = Path.GetFullPath(Path.Combine(RepoRoot, options.BackgroundDir));
        RenderScript = Path.GetFullPath(Path.Combine(RepoRoot, options.Render.Script));
    }

    public string RepoRoot { get; }
    public string Applications { get; }
    public string Inbox { get; }
    public string Prompts { get; }
    public string MasterResume { get; }
    public string Background { get; }
    public string RenderScript { get; }

    public string Prompt(string name) => Path.Combine(Prompts, name);
}
