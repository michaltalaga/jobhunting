using System.Text;
using System.Text.Json.Nodes;

namespace JobHunting.Server;

/// <summary>One tailoring setting. The description is shown in the UI and given to Claude verbatim.</summary>
public sealed record SettingDef(string Key, string Label, string Type, JsonNode Default, string Description, string[]? Options = null);

/// <summary>
/// How resumes are tailored: length, depth, how much history and how many projects.
/// Effective value = built-in default, overridden by data/tailoring.json, overridden by the job's own overrides.
/// To add a setting, add it to <see cref="Definitions"/>; the UI and the prompt pick it up from there.
/// </summary>
public sealed class TailoringSettings(Paths paths)
{
    public static readonly IReadOnlyList<SettingDef> Definitions =
    [
        new("pages", "Length target (A4 pages)", "number", 2,
            "Target length of the rendered resume in A4 pages. Page 1 must work on its own."),
        new("maxPages", "Length limit (A4 pages)", "number", 3,
            "Never exceed this. Go past the target only when the extra page holds relevant experience, not projects or awards."),
        new("fitRows", "Fit for this role (rows)", "number", 5,
            "Rows in the \"Fit for This Role\" block on page 1: the advert's top must-haves the candidate clearly meets, each " +
            "with one line of proof. With the block, page 1 holds only the header, summary, skills and fit, and experience " +
            "starts on page 2. 0 leaves it out."),
        new("detailYears", "Detailed history (years)", "number", 10,
            "Positions that ended within this many years get their own entry with a scope line and highlights."),
        new("detailRoles", "Detailed history (at least N roles)", "number", 4,
            "At least this many of the most recent positions get the detailed treatment, even if older than the year window."),
        new("bulletsCurrentRole", "Bullets: current role", "number", 6,
            "One-line highlights for the current position."),
        new("bulletsRecentRoles", "Bullets: roles in the last 5 years", "number", 3,
            "One-line highlights for each other position that ended within the last 5 years."),
        new("bulletsOlderRoles", "Bullets: older detailed roles", "number", 2,
            "One-line highlights for each detailed position older than 5 years; fewer when it's less relevant to the advert."),
        new("earliestYear", "Earliest year", "number", 0,
            "Leave out positions that started before this year; 0 keeps all. The summary's years of experience still counts the whole career."),
        new("earlierCareer", "Earlier career", "choice", "list",
            "Positions outside the detailed window. list: one entry per position (by its ref), with a one-clause outcome as " +
            "its summary and no highlights. collapse: a single work entry named \"Earlier career\" whose \"refs\" list those " +
            "positions (the server sets its dates), naming the employers in a one-sentence summary. omit: leave them out.",
            ["list", "collapse", "omit"]),
        new("projects", "Projects", "number", 5,
            "Number of projects to show as one-liners, featured projects included: the most substantial builds first " +
            "(scope, complexity, originality, effort), then the most relevant. 0 hides the section. When the master resume " +
            "has more, add a final entry pointing to the rest (see the projects rules)."),
        new("featuredProjects", "Featured projects", "text", "",
            "Comma-separated project names (from the master resume or background) that are always shown, whatever the advert."),
        new("certificates", "Certifications", "number", 6,
            "Maximum number of certifications. Technology certifications are kept whatever their age; within one family " +
            "(e.g. Microsoft MCP → MCAD → MCSD/MCPD) show only the highest."),
        new("awards", "Awards", "number", 6,
            "Maximum number of awards, the most distinguished and relevant first. Leadership and communication awards " +
            "count, especially for leadership roles; age alone is no reason to drop one."),
        new("bulletChars", "Max characters per bullet", "number", 110,
            "Maximum length of each highlight, summary line and project description, so each fits on one printed line. " +
            "The first two highlights of the current role may run 20 characters longer."),
        new("tailorAutomatically", "Tailor automatically", "choice", "off",
            "A new capture never starts by itself; you start it with Score. off: after scoring it waits at " +
            "'Ready to tailor' until you click Tailor. on: tailoring follows straight after scoring.",
            ["off", "on"]),
        new("review", "Pre-send review", "choice", "revise",
            "revise: after the first tailoring, an HR-consultant reviewer checks quality, ATS fit and facts against the " +
            "sources, and the writer applies its fixes once before you see the resume. off: skip it (faster, cheaper).",
            ["revise", "off"]),
        new("instructions", "Extra instructions", "textarea", "",
            "Free-text instructions from the candidate for every resume. Follow them unless they conflict with the truthfulness rules."),
    ];

    private string GlobalFile => Path.Combine(paths.RepoRoot, "data", "tailoring.json");

    /// <summary>The global settings: defaults overlaid with data/tailoring.json.</summary>
    public JsonObject Global()
    {
        var values = Defaults();
        if (Files.ReadIfExists(GlobalFile) is { } text && JsonNode.Parse(text) is JsonObject saved)
            Overlay(values, saved);
        return values;
    }

    public void SaveGlobal(JsonObject values) =>
        Files.WriteAtomic(GlobalFile, Clean(values, keepNulls: false).ToJsonString(Json.Files) + "\n");

    /// <summary>Writes data/tailoring.json with the defaults on first run, so there's a file to find and edit.</summary>
    public void EnsureGlobalFile()
    {
        if (!File.Exists(GlobalFile)) SaveGlobal(Defaults());
    }

    public JsonObject Effective(JsonObject? jobOverrides)
    {
        var values = Global();
        if (jobOverrides is not null) Overlay(values, jobOverrides);
        return values;
    }

    /// <summary>Keeps only known keys with values of the right type; nulls mean "no override" and are dropped.</summary>
    public static JsonObject Clean(JsonObject input, bool keepNulls)
    {
        var result = new JsonObject();
        foreach (var def in Definitions)
        {
            if (!input.TryGetPropertyValue(def.Key, out var value)) continue;
            if (value is null)
            {
                if (keepNulls) result[def.Key] = null;
                continue;
            }
            if (Coerce(def, value) is { } ok) result[def.Key] = ok;
        }
        return result;
    }

    /// <summary>The settings as Claude sees them: one line per setting, with what it controls.</summary>
    public static string ToPrompt(JsonObject effective)
    {
        var sb = new StringBuilder();
        foreach (var def in Definitions)
        {
            var value = effective[def.Key];
            var shown = value is JsonValue v && v.TryGetValue<string>(out var s) ? (s.Length == 0 ? "(none)" : s) : value?.ToJsonString() ?? "(none)";
            sb.AppendLine($"- {def.Key} = {shown}: {def.Description}");
        }
        return sb.ToString();
    }

    private static JsonObject Defaults()
    {
        var values = new JsonObject();
        foreach (var def in Definitions) values[def.Key] = def.Default.DeepClone();
        return values;
    }

    private static void Overlay(JsonObject target, JsonObject overrides)
    {
        foreach (var (key, value) in Clean(overrides, keepNulls: false)) target[key] = value?.DeepClone();
    }

    private static JsonNode? Coerce(SettingDef def, JsonNode value)
    {
        if (value is not JsonValue v) return null;
        switch (def.Type)
        {
            case "number":
                if (v.TryGetValue<int>(out var n)) return n;
                if (v.TryGetValue<double>(out var d)) return (int)Math.Round(d);
                if (v.TryGetValue<string>(out var str) && int.TryParse(str, out var parsed)) return parsed;
                return null;
            case "choice":
                return v.TryGetValue<string>(out var choice) && def.Options!.Contains(choice) ? choice : null;
            default:
                return v.TryGetValue<string>(out var text) ? text : null;
        }
    }
}
