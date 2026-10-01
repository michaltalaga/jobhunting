namespace JobHunting.Server;

/// <summary>The personal data folder: first-run scaffolding, and the optional background material.</summary>
public static class DataFolder
{
    /// <summary>Explains the background folder to new users. Not sent to the model.</summary>
    private const string BackgroundNote = "README.md";

    private const string BackgroundNoteText =
        """
        # Background material (optional)

        Every .md and .txt file in this folder (subfolders included) is given to Claude when it tailors
        your resume, alongside `../resume.json`.

        Put anything here that shows what you've done but doesn't fit on a resume, for example:

        - notes on projects and achievements, with numbers, scope and stack;
        - older or longer versions of your CV;
        - a LinkedIn profile export, performance reviews, a brag document.

        Tailoring may draw on all of it, but never invents anything beyond what's here and in `resume.json`.
        Your resume stays the authority on employers, job titles and dates.

        This README is not sent.

        """;

    public static void Initialize(Paths paths)
    {
        Directory.CreateDirectory(paths.Inbox);
        Directory.CreateDirectory(paths.Background);
        var note = Path.Combine(paths.Background, BackgroundNote);
        if (!File.Exists(note)) Files.WriteAtomic(note, BackgroundNoteText);
    }

    /// <summary>Background files in a stable order (stable order keeps the prompt cache warm between jobs).</summary>
    public static IEnumerable<string> BackgroundFiles(Paths paths) =>
        !Directory.Exists(paths.Background)
            ? []
            : Directory.EnumerateFiles(paths.Background, "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .Where(f => !Path.GetRelativePath(paths.Background, f).Equals(BackgroundNote, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase);
}
