namespace JobHunting.Server;

public sealed class JobHuntingOptions
{
    public const string Section = "JobHunting";

    /// <summary>Repository root, relative to the server's content root.</summary>
    public string RepoRoot { get; set; } = "../..";

    /// <summary>How many jobs are processed at the same time.</summary>
    public int Workers { get; set; } = 1;

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
    public int TimeoutMinutes { get; set; } = 20;
    public ClaudeStepOptions Extract { get; set; } = new();
    public ClaudeStepOptions Tailor { get; set; } = new();
    public ClaudeStepOptions Review { get; set; } = new() { Effort = "high" };
    public ClaudeStepOptions Match { get; set; } = new() { Effort = "high" };
}

public sealed class ClaudeStepOptions
{
    public string Model { get; set; } = "claude-opus-5-5";
    public string Effort { get; set; } = "xhigh";
}

public sealed class RenderOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Theme id (a folder under themes/ or data/themes/) for jobs that haven't picked one.</summary>
    public string Theme { get; set; } = "classic";

    /// <summary>Chrome or Edge executable; found automatically when not set.</summary>
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
        Themes = Path.Combine(RepoRoot, "themes");
        PersonalThemes = Path.Combine(RepoRoot, "data", "themes");
    }

    public string RepoRoot { get; }
    public string Applications { get; }
    public string Inbox { get; }
    public string Prompts { get; }
    public string MasterResume { get; }
    public string Background { get; }
    /// <summary>Built-in themes, shipped with the repo.</summary>
    public string Themes { get; }
    /// <summary>Your own themes; one with the same id as a built-in theme replaces it.</summary>
    public string PersonalThemes { get; }

    public string Prompt(string name) => Path.Combine(Prompts, name);
}
