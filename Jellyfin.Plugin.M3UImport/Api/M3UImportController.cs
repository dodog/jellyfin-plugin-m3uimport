using System.Net.Mime;
using System.Text;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.M3UImport.Api;

public class ImportRequest
{
    public string Name { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public Guid UserId { get; set; }

    /// <summary>Optional, one per line: "m3u prefix =&gt; Jellyfin prefix".</summary>
    public string Mappings { get; set; } = string.Empty;

    /// <summary>Keep only the first occurrence of each track.</summary>
    public bool Deduplicate { get; set; } = true;

    /// <summary>Accept a track when its file name occurs exactly once in the whole library.</summary>
    public bool AllowNameOnly { get; set; } = true;

    /// <summary>
    /// What to do if the user already owns a playlist with this name:
    /// "stop" (default, change nothing and report it), "append", "replace" or "new" (create another).
    /// </summary>
    public string ExistingAction { get; set; } = "stop";
}

public class ImportResult
{
    /// <summary>Number of audio tracks Jellyfin knows about.</summary>
    public int LibraryTracks { get; set; }

    /// <summary>Non-empty, non-comment lines in the m3u file.</summary>
    public int TotalLines { get; set; }

    /// <summary>Lines that were matched to a library track.</summary>
    public int Matched { get; set; }

    /// <summary>Of the matched lines, how many matched by folder + file name.</summary>
    public int TailMatched { get; set; }

    /// <summary>Of the matched lines, how many matched by a unique file name only.</summary>
    public int NameOnlyMatched { get; set; }

    /// <summary>Matched lines skipped because the track was already in the playlist.</summary>
    public int Duplicates { get; set; }

    /// <summary>Number of tracks in the created playlist.</summary>
    public int TracksAdded { get; set; }

    /// <summary>Total number of lines that could not be matched.</summary>
    public int MissingCount { get; set; }

    /// <summary>The first unmatched lines (original text), with a hint, capped.</summary>
    public List<string> Missing { get; set; } = new();

    public string? PlaylistId { get; set; }

    /// <summary>The user already has a playlist with the requested name.</summary>
    public bool ExistingFound { get; set; }

    /// <summary>Number of items in that existing playlist.</summary>
    public int ExistingTracks { get; set; }

    /// <summary>What was done: "none", "created", "appended" or "replaced".</summary>
    public string Action { get; set; } = "none";
}

[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("M3UImport")]
[Produces(MediaTypeNames.Application.Json)]
public class M3UImportController : ControllerBase
{
    private const int MaxMissingReported = 200;

    private readonly ILibraryManager _library;
    private readonly IPlaylistManager _playlists;

    public M3UImportController(ILibraryManager library, IPlaylistManager playlists)
    {
        _library = library;
        _playlists = playlists;
    }

    [HttpPost("Import")]
    public async Task<ActionResult<ImportResult>> Import([FromBody] ImportRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || req.UserId == Guid.Empty)
        {
            return BadRequest("Playlist name and user are required.");
        }

        var maps = ParseMappings(req.Mappings);
        var index = BuildIndex();

        var result = new ImportResult { LibraryTracks = index.TrackCount };
        var ids = new List<Guid>();
        var seen = new HashSet<Guid>();

        foreach (var raw in req.Content.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            result.TotalLines++;

            var hit = FindMatch(line, maps, index, req.AllowNameOnly);
            if (hit is null)
            {
                result.MissingCount++;
                if (result.Missing.Count < MaxMissingReported)
                {
                    result.Missing.Add(Annotate(line, maps, index));
                }

                continue;
            }

            result.Matched++;
            switch (hit.Value.Kind)
            {
                case MatchKind.Tail:
                    result.TailMatched++;
                    break;
                case MatchKind.NameOnly:
                    result.NameOnlyMatched++;
                    break;
            }

            if (req.Deduplicate && !seen.Add(hit.Value.Id))
            {
                result.Duplicates++;
                continue;
            }

            ids.Add(hit.Value.Id);
        }

        result.TracksAdded = ids.Count;

        var name = req.Name.Trim();
        var action = (req.ExistingAction ?? "stop").Trim().ToLowerInvariant();
        if (action is not ("append" or "replace" or "new"))
        {
            action = "stop";
        }

        var existing = FindExisting(name, req.UserId);
        if (existing is not null)
        {
            result.ExistingFound = true;
            result.ExistingTracks = existing.LinkedChildren.Count();
            if (action == "stop")
            {
                result.TracksAdded = 0;
                return Ok(result);
            }
        }

        if (ids.Count == 0)
        {
            return Ok(result);
        }

        if (existing is not null && action == "append")
        {
            var have = existing.LinkedChildren.Select(c => (Guid?)c.ItemId).ToHashSet();
            var toAdd = req.Deduplicate ? ids.Where(i => !have.Contains(i)).ToList() : ids;
            result.Duplicates += ids.Count - toAdd.Count;
            result.TracksAdded = toAdd.Count;
            if (toAdd.Count > 0)
            {
                await _playlists.AddItemToPlaylistAsync(existing.Id, toAdd, null, req.UserId).ConfigureAwait(false);
            }

            result.PlaylistId = existing.Id.ToString("N");
            result.Action = "appended";
            return Ok(result);
        }

        // Create first, delete the old one afterwards, so a failure never loses the existing playlist.
        var created = await _playlists.CreatePlaylist(new PlaylistCreationRequest
        {
            Name = name,
            ItemIdList = ids,
            UserId = req.UserId,
            MediaType = MediaType.Audio
        }).ConfigureAwait(false);

        result.PlaylistId = created.Id;
        result.Action = "created";

        if (existing is not null && action == "replace")
        {
            _library.DeleteItem(existing, new DeleteOptions { DeleteFileLocation = true });
            result.Action = "replaced";
        }

        return Ok(result);
    }

    /// <summary>The playlist this user owns with the given name (case-insensitive), if any.</summary>
    private Playlist? FindExisting(string name, Guid userId)
    {
        return _playlists.GetPlaylists(userId)
            .FirstOrDefault(p => p.OwnerUserId == userId
                && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- matching

    private enum MatchKind
    {
        Exact,
        Tail,
        NameOnly
    }

    private readonly record struct Entry(string[] Segments, Guid Id, string Path);

    private sealed class LibraryIndex
    {
        public int TrackCount { get; set; }

        /// <summary>Normalized full path -> item id.</summary>
        public Dictionary<string, Guid> ByFullPath { get; } = new();

        /// <summary>Normalized file name -> all items with that file name.</summary>
        public Dictionary<string, List<Entry>> ByFileName { get; } = new();
    }

    private LibraryIndex BuildIndex()
    {
        var index = new LibraryIndex();
        var items = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            Recursive = true
        });

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Path))
            {
                continue;
            }

            var segs = Segments(item.Path);
            if (segs.Length == 0)
            {
                continue;
            }

            index.TrackCount++;
            index.ByFullPath.TryAdd(string.Join('/', segs), item.Id);

            var name = segs[^1];
            if (!index.ByFileName.TryGetValue(name, out var list))
            {
                list = new List<Entry>();
                index.ByFileName[name] = list;
            }

            list.Add(new Entry(segs, item.Id, item.Path));
        }

        return index;
    }

    /// <summary>
    /// Splits a path into lower-cased, unicode-normalized segments. Handles both slash styles,
    /// drops "." / ".." / empty segments and a leading Windows drive letter.
    /// </summary>
    private static string[] Segments(string path)
    {
        var p = path.Replace('\\', '/');
        try
        {
            p = p.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // malformed unicode in the path: use it as is
        }

        p = p.ToLowerInvariant();
        var segs = p.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != "." && s != "..")
            .ToList();

        if (segs.Count > 0 && segs[0].Length == 2 && segs[0][1] == ':' && char.IsLetter(segs[0][0]))
        {
            segs.RemoveAt(0);
        }

        return segs.ToArray();
    }

    private static List<(string From, string To)> ParseMappings(string text)
    {
        var maps = new List<(string, string)>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split("=>", 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                continue;
            }

            var from = parts[0];
            if (from.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                from = Uri.UnescapeDataString(from.Substring(7));
            }

            from = from.Replace('\\', '/');
            var to = parts[1];

            // "a/ => b" must not glue folder and file name together.
            if (from.EndsWith('/') && to.Length > 0 && !to.EndsWith('/'))
            {
                to += "/";
            }

            if (from.Length > 0)
            {
                maps.Add((from, to));
            }
        }

        return maps;
    }

    /// <summary>The different readings of a line worth trying (file:// URL, percent-encoded, plain).</summary>
    private static IEnumerable<string> Variants(string line)
    {
        if (line.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = line.Substring(7);
            if (!rest.StartsWith('/'))
            {
                // file://host/path -> drop the host
                var slash = rest.IndexOf('/');
                rest = slash >= 0 ? rest.Substring(slash) : rest;
            }

            yield return Uri.UnescapeDataString(rest);
            yield break;
        }

        yield return line;

        if (line.Contains('%'))
        {
            var decoded = Uri.UnescapeDataString(line);
            if (decoded != line)
            {
                yield return decoded;
            }
        }
    }

    private static string ApplyMappings(string path, List<(string From, string To)> maps)
    {
        var p = path.Replace('\\', '/');
        foreach (var (from, to) in maps)
        {
            if (p.StartsWith(from, StringComparison.OrdinalIgnoreCase))
            {
                return to + p.Substring(from.Length);
            }
        }

        return p;
    }

    private static (Guid Id, MatchKind Kind)? FindMatch(
        string line,
        List<(string From, string To)> maps,
        LibraryIndex index,
        bool allowNameOnly)
    {
        foreach (var variant in Variants(line))
        {
            var segs = Segments(ApplyMappings(variant, maps));
            if (segs.Length == 0)
            {
                continue;
            }

            // 1. Exact (case-insensitive) full-path match.
            if (index.ByFullPath.TryGetValue(string.Join('/', segs), out var id))
            {
                return (id, MatchKind.Exact);
            }

            if (!index.ByFileName.TryGetValue(segs[^1], out var candidates))
            {
                continue;
            }

            // 2. Same file name; the longest agreeing path tail wins, ties are rejected.
            var best = 0;
            var bestId = Guid.Empty;
            var tie = false;
            foreach (var c in candidates)
            {
                var n = CommonSuffix(segs, c.Segments);
                if (n > best)
                {
                    best = n;
                    bestId = c.Id;
                    tie = false;
                }
                else if (n == best && n > 0)
                {
                    tie = true;
                }
            }

            // Need at least "folder/file" to agree (or a bare file name that is unique in the library).
            var required = Math.Min(2, segs.Length);
            if (!tie && best >= required && (best >= 2 || candidates.Count == 1))
            {
                return (bestId, MatchKind.Tail);
            }

            // 3. Optional: the file name occurs exactly once in the whole library.
            if (allowNameOnly && candidates.Count == 1)
            {
                return (candidates[0].Id, MatchKind.NameOnly);
            }
        }

        return null;
    }

    /// <summary>Adds a hint to an unmatched line: where the library has a file with the same name.</summary>
    private static string Annotate(string line, List<(string From, string To)> maps, LibraryIndex index)
    {
        foreach (var variant in Variants(line))
        {
            var segs = Segments(ApplyMappings(variant, maps));
            if (segs.Length > 0 && index.ByFileName.TryGetValue(segs[^1], out var c))
            {
                var more = c.Count > 1 ? $" (+{c.Count - 1} more)" : string.Empty;
                return $"{line}\n      -> same file name in library: {c[0].Path}{more}";
            }
        }

        return line + "\n      -> file name not found in library";
    }

    private static int CommonSuffix(string[] a, string[] b)
    {
        var n = 0;
        var max = Math.Min(a.Length, b.Length);
        while (n < max && a[a.Length - 1 - n] == b[b.Length - 1 - n])
        {
            n++;
        }

        return n;
    }
}
