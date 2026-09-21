using System.Text;

namespace JameJam.Divan;

/// <summary>A grouping of notes (the Divan registry's sections).</summary>
/// <param name="Id">Assigned by the store.</param>
/// <param name="Name">Unique, case-insensitive name.</param>
/// <param name="CreatedAt">When the notebook was created.</param>
/// <param name="IsArchived">Archived notebooks hide from lists but keep their notes.</param>
/// <param name="UpdatedAt">When the notebook last changed (rename/archive); drives sync last-write-wins.</param>
public sealed record Notebook(long Id, string Name, DateTimeOffset CreatedAt, bool IsArchived, DateTimeOffset UpdatedAt = default);

/// <summary>One markdown note in the pad.</summary>
/// <param name="Id">Assigned by the store.</param>
/// <param name="NotebookId">The notebook this note belongs to.</param>
/// <param name="Title">Human title (plain text).</param>
/// <param name="Body">Markdown body.</param>
/// <param name="Tags">Comma-joined tags (empty when untagged).</param>
/// <param name="Pinned">Pinned notes sort first.</param>
/// <param name="Archived">Soft-deleted notes.</param>
/// <param name="CreatedAt">When the note was created.</param>
/// <param name="UpdatedAt">When the note was last changed.</param>
/// <param name="SyncId">Stable cross-device identity (a version-7 GUID); stores assign one when empty.</param>
public sealed record Note(
    long Id,
    long NotebookId,
    string Title,
    string Body,
    string Tags,
    bool Pinned,
    bool Archived,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid SyncId = default);

/// <summary>A deletion marker for sync: "the note with this sync id was deleted at this time".</summary>
/// <param name="SyncId">The deleted note's sync identity.</param>
/// <param name="DeletedAt">When the deletion happened (UTC).</param>
public sealed record DivanTombstone(Guid SyncId, DateTimeOffset DeletedAt);

/// <summary>One `- [ ]` / `- [x]` item parsed from a note body.</summary>
/// <param name="Text">The item text.</param>
/// <param name="Done">Whether the box is checked.</param>
/// <param name="LineNumber">1-based line in the body.</param>
public sealed record ChecklistItem(string Text, bool Done, int LineNumber);

/// <summary>Derived metrics for one note.</summary>
/// <param name="Words">Word count of the body.</param>
/// <param name="Characters">Character count of the body.</param>
/// <param name="ReadingSeconds">Estimated reading time at the configured pace.</param>
/// <param name="ChecklistTotal">Checklist items in the body.</param>
/// <param name="ChecklistDone">Checked checklist items.</param>
/// <param name="Links">Wiki-links ([[...]]) found in the body.</param>
public sealed record NoteMetrics(
    int Words,
    int Characters,
    int ReadingSeconds,
    int ChecklistTotal,
    int ChecklistDone,
    IReadOnlyList<string> Links);

/// <summary>Filters accepted by listing and AI context selection.</summary>
/// <param name="NotebookId">Only this notebook.</param>
/// <param name="Tag">Only notes carrying this tag (case-insensitive).</param>
/// <param name="Query">Substring over title and body.</param>
/// <param name="PinnedOnly">Only pinned notes.</param>
/// <param name="ArchivedOnly">Only archived notes (list excludes them by default).</param>
/// <param name="ChecklistsOnly">Only notes containing at least one checklist item.</param>
public sealed record DivanFilter(
    long? NotebookId = null,
    string? Tag = null,
    string? Query = null,
    bool PinnedOnly = false,
    bool ArchivedOnly = false,
    bool ChecklistsOnly = false);

/// <summary>Aggregate pad statistics.</summary>
/// <param name="Notebooks">Notebook count.</param>
/// <param name="Notes">Active (non-archived) note count.</param>
/// <param name="ArchivedNotes">Archived note count.</param>
/// <param name="TaggedNotes">Notes carrying at least one tag.</param>
/// <param name="Words">Total word count across active notes.</param>
/// <param name="OpenChecklistItems">Unchecked checklist items across active notes.</param>
/// <param name="Links">Wiki-links across active notes.</param>
public sealed record DivanStats(
    int Notebooks,
    int Notes,
    int ArchivedNotes,
    int TaggedNotes,
    int Words,
    int OpenChecklistItems,
    int Links);

/// <summary>Pure text analytics for markdown bodies: word counts, checklists, wiki-links.</summary>
public static class DivanText
{
    /// <summary>Counts whitespace-separated words.</summary>
    /// <summary>Trims and clips text to a hard length (the shared rail helper).</summary>
    public static string Clip(string? value, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    public static int WordCount(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>Parses markdown checklist items (`- [ ]`, `- [x]`, `* [X]`) line by line.</summary>
    public static List<ChecklistItem> Checklist(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        List<ChecklistItem> items = [];
        var lines = body.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            // Shape: "- [ ] text" / "* [x] text" (marker at index 3, close bracket at 4).
            if (line.Length < 6 || (line[0] != '-' && line[0] != '*' && line[0] != '+'))
            {
                continue;
            }

            if (line[1] != ' ' || line[2] != '[' || line[4] != ']')
            {
                continue;
            }

            var marker = char.ToLowerInvariant(line[3]);
            if (marker is not (' ' or 'x'))
            {
                continue;
            }

            items.Add(new ChecklistItem(line[5..].Trim(), marker == 'x', i + 1));
        }

        return items;
    }

    /// <summary>
    /// Extracts wiki-link targets (`[[Note Title]]`) in order, deduplicated
    /// case-insensitively. Unclosed brackets are ignored.
    /// </summary>
    public static List<string> ExtractLinks(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        List<string> links = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rest = body.AsSpan();
        while (true)
        {
            var start = rest.IndexOf("[[", StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            var after = rest[(start + 2)..];
            var end = after.IndexOf("]]", StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            var candidate = after[..end].Trim();
            rest = after[(end + 2)..];
            if (candidate.Length == 0 || candidate.Contains('\n') || candidate.Contains('|'))
            {
                continue;
            }

            if (seen.Add(candidate.ToString()))
            {
                links.Add(candidate.ToString());
            }
        }

        return links;
    }

    /// <summary>Builds a plain-text snippet of at most <paramref name="maxLength"/> characters.</summary>
    public static string Snippet(string body, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(body);
        var flat = new StringBuilder(body.Length);
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                if (flat.Length > 0)
                {
                    flat.Append(' ');
                }
                flat.Append(trimmed);
            }

            if (flat.Length >= maxLength)
            {
                break;
            }
        }

        return flat.Length <= maxLength ? flat.ToString() : flat.ToString()[..maxLength] + "…";
    }
}
