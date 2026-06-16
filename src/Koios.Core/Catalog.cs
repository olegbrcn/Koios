namespace Koios.Core;

/// <summary>One declared symbol, projected from the live snapshot.</summary>
public sealed class SymbolEntry
{
    public required string Moniker { get; init; }
    public required string Name { get; init; }
    public required string Fqn { get; init; }
    public required string Kind { get; init; }
    public string? Accessibility { get; init; }
    public string? Signature { get; init; }
    public string? DocSummary { get; init; }
    public string? ContainingMoniker { get; init; }
    public bool Generated { get; init; }
    public string Source { get; init; } = "semantic";
    /// <summary>All declaration sites (more than one for partials).</summary>
    public required IReadOnlyList<DeclLocation> DeclLocations { get; init; }
    public DeclLocation? Primary => DeclLocations.Count > 0 ? DeclLocations[0] : null;
}

public sealed record DeclLocation(string RelPath, int Line, int Col, string Snippet);

/// <summary>In-memory projection backing hot queries.</summary>
public sealed class Catalog
{
    private readonly List<SymbolEntry> all = new();
    private readonly Dictionary<string, SymbolEntry> byMoniker = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SymbolEntry>> byNameCi = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<SymbolEntry>> byFile = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SymbolEntry>> byContaining = new(StringComparer.Ordinal);

    public int Count => all.Count;
    public IReadOnlyList<SymbolEntry> All => all;

    public void Add(SymbolEntry e)
    {
        if (byMoniker.ContainsKey(e.Moniker))
            return; // dedupe by moniker (partials, multi-TFM)
        all.Add(e);
        byMoniker[e.Moniker] = e;
        Bucket(byNameCi, e.Name, e);
        if (e.Primary is { } p)
            Bucket(byFile, p.RelPath, e);
        if (e.ContainingMoniker is { } c)
            Bucket(byContaining, c, e);
    }

    public SymbolEntry? ByMoniker(string moniker) =>
        byMoniker.TryGetValue(moniker, out var e) ? e : null;

    /// <summary>Entries whose simple name matches exactly. Prefers case-sensitive hits,
    /// falling back to the case-insensitive bucket when none match exactly.</summary>
    public IReadOnlyList<SymbolEntry> ByExactName(string name)
    {
        if (!byNameCi.TryGetValue(name, out var list)) return Array.Empty<SymbolEntry>();
        var exact = list.Where(e => string.Equals(e.Name, name, StringComparison.Ordinal)).ToList();
        return exact.Count > 0 ? exact : list;
    }

    public IReadOnlyList<SymbolEntry> InFile(string relPath) =>
        byFile.TryGetValue(relPath, out var list) ? list : Array.Empty<SymbolEntry>();

    /// <summary>Resolve a file by exact rel-path, else by unique path-suffix match.</summary>
    public string? ResolveFile(string relPath)
    {
        if (byFile.ContainsKey(relPath))
            return relPath;
        var norm = "/" + relPath.TrimStart('/');
        var matches = byFile.Keys
            .Where(k => ("/" + k).EndsWith(norm, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    public IReadOnlyList<SymbolEntry> MembersOf(string containingMoniker) =>
        byContaining.TryGetValue(containingMoniker, out var list) ? list : Array.Empty<SymbolEntry>();

    /// <summary>Ranked name/substring/subsequence search.</summary>
    public List<(SymbolEntry Entry, double Score)> Search(
        string query, IReadOnlySet<string>? kinds, int limit)
    {
        var results = new List<(SymbolEntry, double)>();
        foreach (var e in all)
        {
            if (kinds is not null && !kinds.Contains(e.Kind))
                continue;
            var score = ScoreName(e.Name, query);
            if (score <= 0)
                continue;
            results.Add((e, score));
        }

        results.Sort((a, b) =>
        {
            var c = b.Item2.CompareTo(a.Item2);
            if (c != 0) return c;
            return string.Compare(a.Item1.Fqn, b.Item1.Fqn, StringComparison.Ordinal);
        });

        if (results.Count > limit)
            results.RemoveRange(limit, results.Count - limit);
        return results;
    }

    // exact (1.0) > prefix (0.8) > camel-hump subsequence (0.6) > substring (0.4)
    private static double ScoreName(string name, string query)
    {
        if (name.Equals(query, StringComparison.Ordinal)) return 1.0;
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0.92;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 0.8;
        if (CamelHumpMatch(name, query)) return 0.6;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 0.4;
        return 0;
    }

    private static bool CamelHumpMatch(string name, string query)
    {
        // Match query chars against the uppercase humps of name (e.g. "OS" -> "OrderService").
        int qi = 0;
        foreach (var ch in name)
        {
            if (qi >= query.Length) break;
            if (char.IsUpper(ch) && char.ToUpperInvariant(query[qi]) == ch)
                qi++;
        }
        return qi == query.Length && query.Length > 0;
    }

    private static void Bucket(Dictionary<string, List<SymbolEntry>> map, string key, SymbolEntry e)
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = new List<SymbolEntry>();
        list.Add(e);
    }
}
