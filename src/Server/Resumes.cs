using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace JobHunting.Server;

/// <summary>
/// A master-resume section whose entries the model picks by reference ("W3" is the third work entry).
/// <see cref="Facts"/> are always copied from the master; the model only chooses entries and writes their text.
/// </summary>
public sealed record ResumeSection(string Key, string Prefix, string[] Facts, string NameField, string? TitleField, string? DateField, string? AltNameField = null)
{
    public string? NameOf(JsonObject entry) => ResumeChecks.Str(entry, NameField) ?? (AltNameField is null ? null : ResumeChecks.Str(entry, AltNameField));

    public string RefOf(int index) => $"{Prefix}{index + 1}";
}

/// <summary>
/// Deterministic guard rails applied to every tailored resume: facts come from the master resume, never from
/// the model, and every number in the text must appear somewhere in the candidate's sources.
/// </summary>
public static partial class ResumeChecks
{
    public const string EarlierCareer = "Earlier career";
    public const string MoreProjects = "More projects";

    private static readonly string[] ContactFields = ["name", "email", "phone", "url", "image", "location", "profiles"];

    public static readonly ResumeSection[] Sections =
    [
        new("work", "W", ["name", "company", "position", "location", "url", "startDate", "endDate"], "name", "position", "startDate", "company"),
        new("volunteer", "V", ["organization", "position", "url", "startDate", "endDate"], "organization", "position", "startDate"),
        new("education", "E", ["institution", "url", "area", "studyType", "startDate", "endDate", "score"], "institution", "studyType", "startDate"),
        new("projects", "P", ["name", "url", "startDate", "endDate", "entity", "type", "roles"], "name", null, "startDate"),
        new("certificates", "C", ["name", "issuer", "date", "url"], "name", null, "date"),
        new("awards", "A", ["title", "awarder", "date"], "title", null, "date"),
        new("publications", "PUB", ["name", "publisher", "releaseDate", "url"], "name", null, "releaseDate"),
    ];

    /// <param name="otherSources">Everything else a number may come from: background documents, instructions, the candidate's change requests.</param>
    public static (string Json, List<string> Warnings) Apply(string tailoredJson, string masterJson, IEnumerable<string> otherSources)
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

        var shownProjects = 0;
        foreach (var section in Sections)
        {
            if (tailored[section.Key] is not JsonArray entries) continue;
            var masterEntries = Entries(master, section.Key);
            var used = new HashSet<JsonObject>();
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i] is not JsonObject entry) continue;
                var name = section.NameOf(entry);
                if (section.Key == "work" && Same(name, EarlierCareer))
                {
                    SpanDates(entry, section, masterEntries, warnings);
                    continue;
                }
                if (section.Key == "projects" && Same(name, MoreProjects)) continue;

                var source = Resolve(section, entry, masterEntries);
                if (source is null)
                {
                    warnings.Add($"{section.Key}: \"{name ?? Str(entry, "ref") ?? "(unnamed)"}\" isn't in your master resume, so its details couldn't be checked.");
                    continue;
                }
                if (!used.Add(source))
                    warnings.Add($"{section.Key}: \"{section.NameOf(source)}\" appears more than once.");
                entries[i] = Stamp(section, entry, source, section.RefOf(masterEntries.IndexOf(source)));
            }
            if (section.Key == "projects") shownProjects = used.Count;
        }

        CheckNumbers(tailored, master, otherSources, warnings);
        DescribeMoreProjects(tailored, master, shownProjects);
        return (tailored.ToJsonString(Json.Files), warnings);
    }

    /// <summary>A section's entries, in the order their references count them.</summary>
    public static List<JsonObject> Entries(JsonObject resume, string section) =>
        (resume[section] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

    /// <summary>
    /// The master entry a tailored entry stands for: the one its reference points to, unless the entry names a
    /// different one (the master was reordered since this resume was written); then the one with that name.
    /// </summary>
    private static JsonObject? Resolve(ResumeSection section, JsonObject entry, List<JsonObject> master)
    {
        var byRef = ByRef(section, Str(entry, "ref"), master);
        var name = section.NameOf(entry);
        if (name is null || (byRef is not null && Same(name, section.NameOf(byRef)))) return byRef;

        var byName = master.Where(m => Same(section.NameOf(m), name)).ToList();
        if (byName.Count > 1 && section.DateField is { } date) byName = Narrow(byName, m => Str(m, date) == Str(entry, date));
        if (byName.Count > 1 && section.TitleField is { } title) byName = Narrow(byName, m => Same(Str(m, title), Str(entry, title)));
        return byName.Count == 1 ? byName[0] : byRef; // a name not in the master is the model's wording: trust the reference
    }

    private static List<JsonObject> Narrow(List<JsonObject> entries, Func<JsonObject, bool> predicate) =>
        entries.Where(predicate).ToList() is { Count: > 0 } narrowed ? narrowed : entries;

    private static JsonObject? ByRef(ResumeSection section, string? reference, List<JsonObject> master) =>
        reference is not null && reference.StartsWith(section.Prefix, StringComparison.OrdinalIgnoreCase)
        && int.TryParse(reference.AsSpan(section.Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
        && n >= 1 && n <= master.Count
            ? master[n - 1]
            : null;

    /// <summary>The master's facts first, then the model's own fields (its text).</summary>
    private static JsonObject Stamp(ResumeSection section, JsonObject entry, JsonObject source, string reference)
    {
        var stamped = new JsonObject { ["ref"] = reference };
        foreach (var field in section.Facts)
            if (source[field] is { } value) stamped[field] = value.DeepClone();
        foreach (var (key, value) in entry)
            if (key != "ref" && !section.Facts.Contains(key)) stamped[key] = value?.DeepClone();
        return stamped;
    }

    /// <summary>The "collapse" setting's single entry spans exactly the positions listed in its "refs".</summary>
    private static void SpanDates(JsonObject entry, ResumeSection section, List<JsonObject> master, List<string> warnings)
    {
        var covered = (entry["refs"] as JsonArray)?.Select(r => ByRef(section, AsString(r), master)).OfType<JsonObject>().ToList() ?? [];
        if (covered.Count == 0)
        {
            warnings.Add($"work: \"{EarlierCareer}\" doesn't list the positions it covers, so its dates couldn't be checked.");
            return;
        }
        var starts = covered.Select(m => Str(m, "startDate")).OfType<string>().ToList();
        var ends = covered.Select(m => Str(m, "endDate")).ToList();
        entry.Remove("startDate");
        entry.Remove("endDate");
        if (starts.Count > 0) entry["startDate"] = starts.Min(StringComparer.Ordinal);
        if (ends.All(e => e is not null)) entry["endDate"] = ends.Max(StringComparer.Ordinal);
    }

    /// <summary>The closing "More projects" line, with the real number of projects left out.</summary>
    private static void DescribeMoreProjects(JsonObject tailored, JsonObject master, int shown)
    {
        if (tailored["projects"] is not JsonArray projects) return;
        var more = projects.OfType<JsonObject>().FirstOrDefault(p => Same(Str(p, "name"), MoreProjects));
        if (more is null) return;

        var left = Entries(master, "projects").Count - shown;
        if (left <= 0)
        {
            projects.Remove(more);
            return;
        }
        var count = left > 20 ? $"{left / 10 * 10}+" : left.ToString(CultureInfo.InvariantCulture);
        var profile = ProfileUrl(master);
        projects[projects.IndexOf(more)] = new JsonObject
        {
            ["name"] = MoreProjects,
            ["description"] = profile is null ? $"{count} more" : $"{count} more on {profile}",
        };
    }

    /// <summary>The GitHub profile (or else the first profile) without "https://".</summary>
    private static string? ProfileUrl(JsonObject master)
    {
        var profiles = ((master["basics"] as JsonObject)?["profiles"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var url = profiles.Where(p => Same(Str(p, "network"), "GitHub")).Concat(profiles).Select(p => Str(p, "url")).FirstOrDefault(u => u is not null);
        return url is null ? null : UrlScheme().Replace(url, "").TrimEnd('/');
    }

    private readonly record struct Number(string Raw, string Value, bool Percent, int Index);

    /// <summary>
    /// Every number in the resume's text must appear somewhere in the sources, and a percentage as a percentage.
    /// This catches invented and mistyped figures; a real figure used in the wrong place is the reviewer's job.
    /// </summary>
    private static void CheckNumbers(JsonObject tailored, JsonObject master, IEnumerable<string> otherSources, List<string> warnings)
    {
        var known = new HashSet<string>();
        var knownPercent = new HashSet<string>();
        foreach (var number in Texts(master).Select(t => t.Text).Concat(otherSources).SelectMany(Numbers))
        {
            known.Add(number.Value);
            if (number.Percent) knownPercent.Add(number.Value);
        }

        var unsupported = Texts(tailored)
            .SelectMany(t => Numbers(t.Text)
                .Where(n => !(n.Percent ? knownPercent : known).Contains(n.Value))
                .Select(n => (n.Raw, t.Where, Excerpt: Around(t.Text, n.Index))))
            .GroupBy(u => u.Raw);
        foreach (var group in unsupported)
        {
            var (raw, where, excerpt) = group.First();
            var others = group.Select(u => u.Where).Distinct().Count() - 1;
            var more = others > 0 ? $", and {others} more place{(others > 1 ? "s" : "")}" : "";
            warnings.Add($"\"{raw}\" isn't in your master resume or background ({where}: \"{excerpt}\"{more}).");
        }
    }

    private static IEnumerable<Number> Numbers(string text)
    {
        // A date's month and day ("2023-11-01") would otherwise vouch for every small number; its year still counts.
        text = IsoDate().Replace(text, m => m.Groups[1].Value.PadRight(m.Length));
        foreach (Match m in NumberPattern().Matches(text))
            yield return new Number(m.Value.Trim(), Normalize(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : null), m.Groups[3].Success, m.Index);
    }

    /// <summary>"1,200" and "1.2k" are both 1200; something that isn't a plain number (a version such as 1.2.3) stays as written.</summary>
    private static string Normalize(string digits, string? scale)
    {
        var plain = ThousandsPattern().IsMatch(digits) ? digits.Replace(",", "") : digits.Replace(',', '.');
        if (!decimal.TryParse(plain, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value > 1_000_000_000_000m)
            return digits;
        value *= scale?.ToLowerInvariant() switch
        {
            "k" or "thousand" => 1_000m,
            "m" or "mln" or "million" or "millions" => 1_000_000m,
            "b" or "bn" or "billion" or "billions" => 1_000_000_000m,
            _ => 1m,
        };
        return value.ToString("0.##########", CultureInfo.InvariantCulture);
    }

    private static readonly HashSet<string> NotText = ["url", "email", "phone", "image", "ref", "refs"];

    /// <summary>The resume's own wording and where it sits; not URLs or contacts, nor the fit block's quotes of the advert.</summary>
    private static IEnumerable<(string Where, string Text)> Texts(JsonObject resume)
    {
        foreach (var (key, node) in resume)
        {
            if (key is "$schema" or "meta") continue;
            if (node is JsonArray entries)
                for (var i = 0; i < entries.Count; i++)
                {
                    var label = entries[i] is JsonObject o ? EntryLabel(o) : null;
                    if (key == "projects" && Same(label, MoreProjects)) continue; // written by the server
                    foreach (var (_, text) in Strings(entries[i], null, key == "fit" ? "requirement" : null))
                        yield return ($"{key} › {label ?? $"#{i + 1}"}", text);
                }
            else
                foreach (var (field, text) in Strings(node, key, null))
                    yield return (key == "basics" ? (field == "label" ? "headline" : field ?? key) : key, text);
        }
    }

    private static string? EntryLabel(JsonObject o) =>
        Str(o, "name") ?? Str(o, "company") ?? Str(o, "organization") ?? Str(o, "institution") ?? Str(o, "title") ?? Str(o, "language") ?? Str(o, "requirement");

    private static IEnumerable<(string? Field, string Text)> Strings(JsonNode? node, string? field, string? skip)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                yield return (field, s);
                break;
            case JsonArray a:
                foreach (var item in a)
                    foreach (var x in Strings(item, field, skip)) yield return x;
                break;
            case JsonObject o:
                foreach (var (key, value) in o)
                    if (!NotText.Contains(key) && key != skip)
                        foreach (var x in Strings(value, key, skip)) yield return x;
                break;
        }
    }

    /// <summary>About 90 characters of the text around a position.</summary>
    private static string Around(string text, int index)
    {
        if (text.Length <= 90) return text;
        var start = Math.Clamp(index - 40, 0, text.Length - 90);
        return (start > 0 ? "…" : "") + text.Substring(start, 90).Trim() + (start + 90 < text.Length ? "…" : "");
    }

    internal static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static string? AsString(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // A number not glued to a word ("S3", "B2B", "Q3" are names), with an optional scale and percent sign.
    [GeneratedRegex(@"(?<![\p{L}\d.,])(\d+(?:[.,]\d+)*)(?:\s?(k|m|mln|millions?|bn|b|billions?|thousand)(?![\p{L}\d]))?(\s?%|\s?per\s?cent\b)?", RegexOptions.IgnoreCase)]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"^\d{1,3}(,\d{3})+(\.\d+)?$")]
    private static partial Regex ThousandsPattern();

    [GeneratedRegex(@"\b(\d{4})-\d{2}(?:-\d{2})?\b")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"^https?://(www\.)?", RegexOptions.IgnoreCase)]
    private static partial Regex UrlScheme();
}

/// <summary>
/// The master resume as compact Markdown for prompts, with every entry tagged by the reference the model
/// uses to pick it (see <see cref="ResumeSection"/>). Nothing is dropped except contact details, which the
/// server copies itself: fields without a place in an entry's heading are listed as "key: value".
/// </summary>
public static class MasterMarkdown
{
    // Fields joined into each entry's heading line, in order; its dates follow.
    private static readonly Dictionary<string, string[]> HeadingFields = new()
    {
        ["work"] = ["position", "name", "company", "location"],
        ["volunteer"] = ["position", "organization"],
        ["education"] = ["studyType", "area", "institution"],
        ["projects"] = ["name", "entity", "type"],
        ["certificates"] = ["name", "issuer"],
        ["awards"] = ["title", "awarder"],
        ["publications"] = ["name", "publisher"],
        ["languages"] = ["language", "fluency"],
    };

    private static readonly string[] DateFields = ["startDate", "endDate", "date", "releaseDate"];

    public static string Render(string masterJson)
    {
        var master = JsonNode.Parse(masterJson) as JsonObject
            ?? throw new InvalidOperationException("The master resume is not a JSON object.");
        var md = new StringBuilder();
        if (master["basics"] is JsonObject basics)
        {
            md.AppendLine($"# {ResumeChecks.Str(basics, "name")}");
            foreach (var (key, value) in basics)
                if (key is not ("name" or "image" or "email" or "phone") && value is not null)
                    md.AppendLine($"{key}: {Flat(value)}");
            md.AppendLine();
        }

        foreach (var (key, node) in master)
        {
            if (key is "$schema" or "meta" or "basics" || node is null) continue;
            md.AppendLine($"## {key}");
            if (node is JsonArray)
            {
                var section = ResumeChecks.Sections.FirstOrDefault(s => s.Key == key);
                var entries = ResumeChecks.Entries(master, key);
                for (var i = 0; i < entries.Count; i++) Entry(md, key, entries[i], section?.RefOf(i));
            }
            else md.AppendLine(Flat(node));
            md.AppendLine();
        }
        return md.ToString().TrimEnd() + "\n";
    }

    private static void Entry(StringBuilder md, string section, JsonObject entry, string? reference)
    {
        var heading = HeadingFields.GetValueOrDefault(section, ["name"]);
        var parts = heading.Select(f => ResumeChecks.Str(entry, f)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (Dates(entry) is { } dates) parts.Add(dates);
        var rest = entry.Where(kv => kv.Value is not null && !heading.Contains(kv.Key) && !DateFields.Contains(kv.Key)).ToList();

        if (reference is null)
        {
            // Sections nothing is picked from (skills, languages, interests): one line per entry.
            md.Append("- ").Append(string.Join(" · ", parts));
            foreach (var (key, value) in rest) md.Append($" · {key}: {Flat(value)}");
            md.AppendLine();
            return;
        }

        md.AppendLine($"### [{reference}] {string.Join(" · ", parts)}");
        foreach (var (key, value) in rest)
        {
            if (key == "highlights" && value is JsonArray highlights)
            {
                md.AppendLine("highlights:");
                foreach (var h in highlights) md.AppendLine($"- {Flat(h)}");
            }
            else md.AppendLine($"{key}: {Flat(value)}");
        }
    }

    private static string? Dates(JsonObject entry)
    {
        var start = ResumeChecks.Str(entry, "startDate");
        var end = ResumeChecks.Str(entry, "endDate");
        if (start is not null) return $"{start} – {end ?? "present"}";
        return end ?? ResumeChecks.Str(entry, "date") ?? ResumeChecks.Str(entry, "releaseDate");
    }

    private static string Flat(JsonNode? node) => node switch
    {
        JsonValue v => v.TryGetValue<string>(out var s) ? s : v.ToJsonString(),
        JsonArray a => string.Join(a.All(x => x is JsonValue) ? ", " : "; ", a.Select(Flat)),
        JsonObject o => string.Join(", ", o.Where(kv => kv.Value is not null).Select(kv => $"{kv.Key}: {Flat(kv.Value)}")),
        _ => "",
    };
}
