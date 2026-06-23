using System.Collections.Immutable;
using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace Koios.Core;

/// <summary>
/// The warm Roslyn engine. For this first iteration it loads one solution/project,
/// projects a catalog, and answers the HOT-tier queries (the foundation + HOT slice).
/// </summary>
public sealed class Engine : IDisposable
{
    private MSBuildWorkspace? workspace;
    private Solution? solution;
    private Catalog catalog = new();
    private readonly List<string> loadErrors = new();
    private string? fatalLoadError;
    private string root = "";
    private string solutionFile = "";
    private string msbuild = "";
    private long loadMs;
    private int projectsTotal, projectsLoaded, projectsFailed, documents;

    public const string SnapshotId = "sln@1";
    public string Root => root;

    public IReadOnlyList<string> LoadErrors => loadErrors;

    public async Task LoadAsync(string path, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        msbuild = MSBuildBootstrap.Register();

        var props = new Dictionary<string, string>
        {
            ["DesignTimeBuild"] = "true",
            ["ProvideCommandLineArgs"] = "true",
            ["SkipCompilerExecution"] = "true",
        };
        var ws = MSBuildWorkspace.Create(props);
        ws.SkipUnrecognizedProjects = true;
        ws.WorkspaceFailed += (_, e) =>
        {
            if (e.Diagnostic.Kind != WorkspaceDiagnosticKind.Failure)
                return;
            var msg = e.Diagnostic.Message;
            // NuGet restore advisories (NU1903 etc.) surface as workspace failures
            // but do not prevent the project from loading — they are noise here.
            if (msg.Contains("NU1903") || msg.Contains("known high severity vulnerability")
                || msg.Contains("known moderate severity vulnerability"))
                return;
            lock (loadErrors) loadErrors.Add(msg);
        };
        workspace = ws;

        var full = Path.GetFullPath(path);
        if (Directory.Exists(full))
            full = ResolveInDirectory(full);

        var ext = Path.GetExtension(full).ToLowerInvariant();
        if (ext is not (".sln" or ".slnx" or ".slnf" or ".csproj"))
            throw new ArgumentException($"Unsupported target '{path}'. Expected a .sln/.slnx/.csproj or a directory.");

        root = Path.GetDirectoryName(full) ?? full;
        solutionFile = Path.GetFileName(full);

        try
        {
            if (ext == ".csproj")
            {
                var proj = await ws.OpenProjectAsync(full, cancellationToken: ct);
                solution = proj.Solution;
            }
            else
            {
                solution = await ws.OpenSolutionAsync(full, cancellationToken: ct);
            }
        }
        catch (Exception ex)
        {
            // The solution could not be opened at all (most often a global.json SDK
            // pin that is not installed). Degrade to an error state with an actionable
            // message instead of crashing — status must keep working.
            fatalLoadError = HumanizeLoadFailure(ex.Message);
            lock (loadErrors) loadErrors.Add(fatalLoadError);
            loadMs = sw.ElapsedMilliseconds;
            return;
        }

        // Drop analyzer references. We never run analyzers for navigation, and an
        // UnresolvedAnalyzerReference (common in MSBuildWorkspace loads) crashes
        // SymbolFinder operations that compute project checksums. Compiler
        // diagnostics from GetDiagnostics are unaffected.
        solution = StripAnalyzerReferences(solution!);

        await ProjectCatalogAsync(ct);
        loadMs = sw.ElapsedMilliseconds;
    }

    private static Solution StripAnalyzerReferences(Solution s)
    {
        var none = Array.Empty<Microsoft.CodeAnalysis.Diagnostics.AnalyzerReference>();
        foreach (var p in s.Projects.ToList())
            if (p.AnalyzerReferences.Count > 0)
                s = s.WithProjectAnalyzerReferences(p.Id, none);
        return s;
    }

    private static string HumanizeLoadFailure(string message)
    {
        if (message.Contains("hostfxr_resolve_sdk2") || message.Contains("A compatible .NET SDK was not found"))
        {
            string? requested = Extract(message, "Requested SDK version: ", '\n');
            var installed = TryListInstalledSdks();
            var reqPart = requested is null ? "" : $" The repo's global.json pins SDK {requested.Trim()}.";
            var instPart = installed.Count > 0 ? $" Installed: {string.Join(", ", installed)}." : "";
            return $"SDK resolution failed: the SDK required by global.json is not installed.{reqPart}{instPart} "
                 + "Install that SDK, or set KOIOS_MSBUILD_PATH to an installed SDK directory, or point at a repo-local .dotnet.";
        }
        return message;
    }

    private static string? Extract(string text, string after, char until)
    {
        var i = text.IndexOf(after, StringComparison.Ordinal);
        if (i < 0) return null;
        i += after.Length;
        var j = text.IndexOf(until, i);
        return j < 0 ? text[i..] : text[i..j];
    }

    private static List<string> TryListInstalledSdks()
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet", "--list-sdks") { RedirectStandardOutput = true, UseShellExecute = false };
            using var proc = Process.Start(psi);
            if (proc is null) return new();
            var outp = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return outp.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split(' ')[0].Trim())
                .Where(v => v.Length > 0)
                .Distinct()
                .ToList();
        }
        catch { return new(); }
    }

    private static string ResolveInDirectory(string dir)
    {
        var slns = Directory.GetFiles(dir, "*.slnx")
            .Concat(Directory.GetFiles(dir, "*.sln"))
            .ToList();
        if (slns.Count == 1) return slns[0];
        if (slns.Count > 1)
            throw new ArgumentException($"Multiple solution files in {dir}; pass one explicitly.");
        var projs = Directory.GetFiles(dir, "*.csproj");
        if (projs.Length == 1) return projs[0];
        throw new ArgumentException($"No single .sln/.slnx/.csproj found in {dir}.");
    }

    private async Task ProjectCatalogAsync(CancellationToken ct)
    {
        var sln = solution!;
        var built = new Catalog();
        projectsTotal = sln.Projects.Count();

        foreach (var project in sln.Projects)
        {
            if (project.Language != LanguageNames.CSharp)
                continue;
            ct.ThrowIfCancellationRequested();
            Compilation? comp;
            try
            {
                comp = await project.GetCompilationAsync(ct);
            }
            catch (Exception ex)
            {
                projectsFailed++;
                lock (loadErrors) loadErrors.Add($"{project.Name}: {ex.Message}");
                continue;
            }

            if (comp is null)
            {
                projectsFailed++;
                continue;
            }

            projectsLoaded++;
            documents += project.Documents.Count();
            foreach (var type in EnumerateTypes(comp.Assembly.GlobalNamespace))
            {
                AddSymbol(built, type);
                foreach (var member in type.GetMembers())
                {
                    if (member is INamedTypeSymbol)
                        continue; // nested types come from EnumerateTypes
                    if (member.IsImplicitlyDeclared)
                        continue;
                    // Property/event accessors are navigation noise; the property/event
                    // itself is the user-facing symbol.
                    if (member is IMethodSymbol m && m.MethodKind is
                        MethodKind.PropertyGet or MethodKind.PropertySet or
                        MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise)
                        continue;
                    AddSymbol(built, member);
                }
            }
        }

        catalog = built;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol child)
                foreach (var t in EnumerateTypes(child))
                    yield return t;
            else if (member is INamedTypeSymbol type)
            {
                yield return type;
                foreach (var nested in EnumerateNested(type))
                    yield return nested;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNested(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in EnumerateNested(nested))
                yield return deeper;
        }
    }

    private void AddSymbol(Catalog into, ISymbol symbol)
    {
        var moniker = symbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(moniker))
            return;

        // Use ISymbol.Locations: for source symbols these point at the identifier
        // token (one per partial declaration), NOT the whole declaration node — so a
        // type/member with leading attributes resolves to its name, not the attribute.
        var decls = new List<DeclLocation>();
        foreach (var loc in symbol.Locations)
        {
            if (!loc.IsInSource) continue;
            var dl = ToDeclLocation(loc);
            if (dl is not null) decls.Add(dl);
        }
        if (decls.Count == 0)
            return; // not source-declared; skip in catalog

        into.Add(new SymbolEntry
        {
            Moniker = moniker!,
            Name = symbol.Name,
            Fqn = symbol.ToDisplayString(Display.Fqn),
            Kind = Display.KindOf(symbol),
            Accessibility = Display.AccessibilityOf(symbol),
            Signature = Display.SignatureOf(symbol),
            DocSummary = ParseSummary(symbol),
            ContainingMoniker = symbol.ContainingSymbol?.GetDocumentationCommentId(),
            Generated = false,
            Source = "semantic",
            DeclLocations = decls,
        });
    }

    private DeclLocation? ToDeclLocation(Location loc)
    {
        if (!loc.IsInSource || loc.SourceTree is null)
            return null;
        var span = loc.GetLineSpan();
        var line = span.StartLinePosition.Line;
        var col = span.StartLinePosition.Character;
        var text = loc.SourceTree.GetText();
        var snippet = line < text.Lines.Count ? text.Lines[line].ToString().Trim() : "";
        return new DeclLocation(RelPath(loc.SourceTree.FilePath), line + 1, col + 1, snippet);
    }

    private string RelPath(string? absolute)
    {
        if (string.IsNullOrEmpty(absolute))
            return "";
        try { return Path.GetRelativePath(root, absolute).Replace('\\', '/'); }
        catch { return absolute!; }
    }

    private static string? ParseSummary(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
            return null;
        try
        {
            var doc = XDocument.Parse(xml);
            var summary = doc.Descendants("summary").FirstOrDefault();
            if (summary is null) return null;
            var text = string.Join(" ", summary.Value.Split('\n', '\r', '\t')
                .Select(s => s.Trim()).Where(s => s.Length > 0));
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch { return null; }
    }

    // ---- Queries -----------------------------------------------------------

    /// <summary>True when the solution could not be opened at all.</summary>
    public bool LoadFailed => solution is null;
    public string? FatalLoadError => fatalLoadError;

    /// <summary>Some projects/tasks failed to load (e.g. F#/Aspire projects, missing
    /// build tasks); C# navigation still works for what loaded, but completeness is reduced.</summary>
    private bool Degraded => projectsFailed > 0 || loadErrors.Count > 0;

    public Envelope<StatusInfo> Status(ResidentInfo? resident = null)
    {
        var state = solution is null || projectsLoaded == 0 ? "error" : "ready";
        return new Envelope<StatusInfo>
        {
            SnapshotId = SnapshotId,
            State = state,
            Degraded = Degraded,
            Items = new[]
            {
                new StatusInfo
                {
                    State = state,
                    Version = "0.1.0",
                    Roslyn = "5.3.0",
                    MSBuild = msbuild,
                    Solution = new SolutionInfo { Root = root, File = solutionFile },
                    SnapshotId = SnapshotId,
                    Projects = new ProjectCounts
                    {
                        Total = projectsTotal,
                        Loaded = projectsLoaded,
                        Failed = projectsFailed,
                    },
                    Documents = documents,
                    Symbols = catalog.Count,
                    LoadErrors = loadErrors.ToArray(),
                    LoadMs = loadMs,
                    Resident = resident,
                }
            },
        };
    }

    public Envelope<SymbolItem> Search(string query, IReadOnlySet<string>? kinds, int limit)
    {
        var sw = Stopwatch.StartNew();
        var hits = catalog.Search(query, kinds, limit);
        var items = hits.Select(h => ToSymbolItem(h.Entry, h.Score)).ToArray();
        return new Envelope<SymbolItem>
        {
            Tier = "hot",
            SnapshotId = SnapshotId,
            Items = items,
            ElapsedMs = sw.ElapsedMilliseconds,
            Degraded = Degraded,
        };
    }

    public Envelope<OutlineNode> Outline(string relOrAbsPath)
    {
        var sw = Stopwatch.StartNew();
        var rel = catalog.ResolveFile(NormalizeToRel(relOrAbsPath))
                  ?? catalog.ResolveFile(Path.GetFileName(relOrAbsPath.Replace('\\', '/')))
                  ?? NormalizeToRel(relOrAbsPath);
        var entries = catalog.InFile(rel);
        if (entries.Count == 0)
        {
            return new Envelope<OutlineNode>
            {
                Ok = false,
                SnapshotId = SnapshotId,
                Error = new ErrorInfo
                {
                    Code = "invalid_argument",
                    Message = $"No indexed symbols for '{rel}'. Check the path is a loaded .cs file.",
                    Retryable = false,
                },
                ElapsedMs = sw.ElapsedMilliseconds,
            };
        }

        // Build a tree by containing moniker for symbols declared in this file.
        var inFile = new HashSet<string>(entries.Select(e => e.Moniker), StringComparer.Ordinal);
        var roots = new List<OutlineNode>();
        foreach (var e in entries.OrderBy(e => e.Primary?.Line ?? 0))
        {
            if (e.ContainingMoniker is { } c && inFile.Contains(c))
                continue; // child; emitted under parent
            roots.Add(BuildOutline(e, rel));
        }

        return new Envelope<OutlineNode>
        {
            Tier = "hot",
            SnapshotId = SnapshotId,
            Items = roots,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    private OutlineNode BuildOutline(SymbolEntry e, string rel)
    {
        var node = new OutlineNode
        {
            SymbolId = e.Moniker,
            Name = e.Name,
            Kind = e.Kind,
            Signature = e.Signature,
            Accessibility = e.Accessibility,
            Loc = e.Primary is { } p ? new Loc { Path = p.RelPath, Line = p.Line, Col = p.Col, Snippet = p.Snippet } : null,
            Source = e.Source,
        };
        foreach (var child in catalog.MembersOf(e.Moniker)
                     .Where(c => c.Primary?.RelPath == rel)
                     .OrderBy(c => c.Primary?.Line ?? 0))
        {
            node.Children.Add(BuildOutline(child, rel));
        }
        return node;
    }

    public Envelope<HoverItem> HoverById(string symbolId)
    {
        var e = catalog.ByMoniker(symbolId);
        if (e is null)
            return NotFoundHover();
        return new Envelope<HoverItem>
        {
            Tier = "hot",
            SnapshotId = SnapshotId,
            Items = new[] { ToHover(e) },
        };
    }

    public async Task<Envelope<SymbolItem>> DefinitionAsync(string? path, int? line, int? col, string? symbolId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(symbolId))
        {
            var (moniker, candidates) = NormalizeTargetId(symbolId!);
            if (candidates is not null) return Ambiguous<SymbolItem>(candidates);
            var e = catalog.ByMoniker(moniker ?? symbolId!);
            if (e is null)
                return SymbolNotFound<SymbolItem>();
            return new Envelope<SymbolItem>
            {
                Tier = "hot",
                SnapshotId = SnapshotId,
                Items = new[] { ToSymbolItem(e, null) },
            };
        }

        var (sym, _) = await ResolvePositionalAsync(path, line, col, ct);
        if (sym is null)
            return SymbolNotFound<SymbolItem>();

        var entry = catalog.ByMoniker(sym.GetDocumentationCommentId() ?? "");
        if (entry is not null)
            return new Envelope<SymbolItem> { Tier = "hot_semantic", SnapshotId = SnapshotId, Items = new[] { ToSymbolItem(entry, null) } };

        // metadata symbol
        return new Envelope<SymbolItem>
        {
            Tier = "hot_semantic",
            SnapshotId = SnapshotId,
            Items = new[] { ToSymbolItemFromMetadata(sym) },
        };
    }

    public async Task<Envelope<HoverItem>> HoverAsync(string? path, int? line, int? col, string? symbolId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(symbolId))
        {
            var (moniker, candidates) = NormalizeTargetId(symbolId!);
            if (candidates is not null) return Ambiguous<HoverItem>(candidates);
            return HoverById(moniker ?? symbolId!);
        }

        var (sym, _) = await ResolvePositionalAsync(path, line, col, ct);
        if (sym is null)
            return NotFoundHover();

        var entry = catalog.ByMoniker(sym.GetDocumentationCommentId() ?? "");
        if (entry is not null)
            return new Envelope<HoverItem> { Tier = "hot_semantic", SnapshotId = SnapshotId, Items = new[] { ToHover(entry) } };

        return new Envelope<HoverItem>
        {
            Tier = "hot_semantic",
            SnapshotId = SnapshotId,
            Items = new[] { ToHoverFromMetadata(sym) },
        };
    }

    private async Task<(ISymbol? Symbol, Document? Doc)> ResolvePositionalAsync(string? path, int? line, int? col, CancellationToken ct)
    {
        if (path is null || line is null || col is null)
            throw new ArgumentException("Positional lookup needs path, line, and col (or pass symbol_id).");

        var rel = NormalizeToRel(path);
        var abs = Path.GetFullPath(Path.Combine(root, rel));
        var docId = solution!.GetDocumentIdsWithFilePath(abs).FirstOrDefault();
        if (docId is null)
            return (null, null);
        var doc = solution.GetDocument(docId);
        if (doc is null)
            return (null, null);

        var text = await doc.GetTextAsync(ct);
        var position = text.Lines[line.Value - 1].Start + (col.Value - 1);
        var model = await doc.GetSemanticModelAsync(ct);
        var syntaxRoot = await doc.GetSyntaxRootAsync(ct);
        if (model is null || syntaxRoot is null)
            return (null, doc);

        var token = syntaxRoot.FindToken(position);
        var node = token.Parent;
        while (node is not null)
        {
            var symbolInfo = model.GetSymbolInfo(node, ct);
            var sym = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
            if (sym is null)
                sym = model.GetDeclaredSymbol(node, ct);
            if (sym is not null)
                return (sym, doc);
            node = node.Parent;
        }
        return (null, doc);
    }

    // ---- Mapping helpers ---------------------------------------------------

    private SymbolItem ToSymbolItem(SymbolEntry e, double? score) => new()
    {
        SymbolId = e.Moniker,
        Name = e.Name,
        Kind = e.Kind,
        Container = ContainerOf(e),
        Accessibility = e.Accessibility,
        Signature = e.Signature,
        Loc = e.Primary is { } p ? new Loc { Path = p.RelPath, Line = p.Line, Col = p.Col, Snippet = p.Snippet } : null,
        Score = score,
        IsMetadata = false,
        Generated = e.Generated,
        Source = e.Source,
    };

    private SymbolItem ToSymbolItemFromMetadata(ISymbol sym) => new()
    {
        SymbolId = sym.GetDocumentationCommentId(),
        Name = sym.Name,
        Kind = Display.KindOf(sym),
        Container = sym.ContainingSymbol?.ToDisplayString(Display.Fqn),
        Accessibility = Display.AccessibilityOf(sym),
        Signature = sym.ToDisplayString(Display.Signature),
        Loc = null,
        IsMetadata = true,
        Source = "semantic",
    };

    private HoverItem ToHover(SymbolEntry e) => new()
    {
        SymbolId = e.Moniker,
        Name = e.Name,
        Kind = e.Kind,
        Signature = e.Signature ?? e.Name,
        Accessibility = e.Accessibility,
        Container = ContainerOf(e),
        DocSummary = e.DocSummary,
        Loc = e.Primary is { } p ? new Loc { Path = p.RelPath, Line = p.Line, Col = p.Col, Snippet = p.Snippet } : null,
        IsMetadata = false,
        Source = e.Source,
    };

    private HoverItem ToHoverFromMetadata(ISymbol sym) => new()
    {
        SymbolId = sym.GetDocumentationCommentId(),
        Name = sym.Name,
        Kind = Display.KindOf(sym),
        Signature = sym.ToDisplayString(Display.Signature),
        ReturnType = (sym as IMethodSymbol)?.ReturnType?.ToDisplayString(Display.Fqn)
                     ?? (sym as IPropertySymbol)?.Type?.ToDisplayString(Display.Fqn),
        Accessibility = Display.AccessibilityOf(sym),
        Container = sym.ContainingSymbol?.ToDisplayString(Display.Fqn),
        DocSummary = ParseSummary(sym),
        Loc = null,
        IsMetadata = true,
        Source = "semantic",
    };

    private static string? ContainerOf(SymbolEntry e)
    {
        if (e.ContainingMoniker is null) return null;
        var idx = e.ContainingMoniker.IndexOf(':');
        return idx >= 0 ? e.ContainingMoniker[(idx + 1)..] : e.ContainingMoniker;
    }

    private string NormalizeToRel(string path)
    {
        var p = path.Replace('\\', '/');
        // Resolve relative inputs against the current directory, then relativize to
        // the workspace root. This makes repo-relative paths work even when the root
        // is a project subdirectory.
        string abs = Path.IsPathRooted(p) ? p : Path.GetFullPath(p, Directory.GetCurrentDirectory());
        try
        {
            var rel = Path.GetRelativePath(root, abs).Replace('\\', '/');
            return rel;
        }
        catch { return p; }
    }

    // ---- Relational queries (live SymbolFinder / diagnostics) --------------

    /// <summary>Outcome of resolving a target: a live symbol, or — when a bare name
    /// matched more than one declaration — the ambiguous candidates to report back.</summary>
    private readonly record struct Resolved(ISymbol? Symbol, IReadOnlyList<SymbolEntry>? Ambiguous);

    /// <summary>
    /// Map a target string (the symbol_id slot) to a concrete moniker. A known catalog
    /// moniker or a doc-comment id (e.g. "T:…", "M:…") passes through unchanged; anything
    /// else is treated as a bare name and resolved against the catalog.
    /// Exactly one of the returned fields is meaningful: a non-null moniker is a unique
    /// resolution; a non-null candidates list means the name was ambiguous. (null, null)
    /// means the name matched nothing.
    /// </summary>
    private (string? Moniker, IReadOnlyList<SymbolEntry>? Candidates) NormalizeTargetId(string raw)
    {
        if (catalog.ByMoniker(raw) is not null) return (raw, null);
        if (raw.Length > 1 && raw[1] == ':') return (raw, null); // doc-comment id shape

        var matches = catalog.ByExactName(raw);
        if (matches.Count == 1) return (matches[0].Moniker, null);
        if (matches.Count == 0) return (null, null);
        return (null, matches);
    }

    /// <summary>Resolve a target (positional, symbol_id, or bare name) to a live ISymbol.</summary>
    private async Task<Resolved> ResolveSymbolAsync(string? path, int? line, int? col, string? symbolId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(symbolId))
        {
            var (moniker, candidates) = NormalizeTargetId(symbolId!);
            if (candidates is not null) return new Resolved(null, candidates);
            var id = moniker ?? symbolId!;
            foreach (var project in solution!.Projects)
            {
                if (project.Language != LanguageNames.CSharp) continue;
                var comp = await project.GetCompilationAsync(ct);
                if (comp is null) continue;
                var sym = DocumentationCommentId.GetFirstSymbolForDeclarationId(id, comp);
                if (sym is not null) return new Resolved(sym, null);
            }
            return new Resolved(null, null);
        }
        var (s, _) = await ResolvePositionalAsync(path, line, col, ct);
        return new Resolved(s, null);
    }

    public async Task<Envelope<ReferenceItem>> FindReferencesAsync(
        string? path, int? line, int? col, string? symbolId, bool includeDeclaration, int limit, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var res = await ResolveSymbolAsync(path, line, col, symbolId, ct);
        if (res.Ambiguous is { } amb) return Ambiguous<ReferenceItem>(amb);
        var symbol = res.Symbol;
        if (symbol is null) return SymbolNotFound<ReferenceItem>();

        var found = await SymbolFinder.FindReferencesAsync(symbol, solution!, ct);
        var items = new List<ReferenceItem>();
        foreach (var rs in found)
        {
            if (includeDeclaration)
                foreach (var dl in rs.Definition.Locations)
                    if (dl.IsInSource) items.Add(new ReferenceItem { Kind = "definition", Loc = LocFrom(dl) });
            foreach (var refLoc in rs.Locations)
            {
                if (!refLoc.Location.IsInSource) continue;
                var kind = await ClassifyReferenceAsync(refLoc, ct);
                items.Add(new ReferenceItem { Kind = kind, Loc = LocFrom(refLoc.Location) });
            }
        }
        items = items
            .OrderBy(i => i.Loc.Path, StringComparer.Ordinal).ThenBy(i => i.Loc.Line).ThenBy(i => i.Loc.Col)
            .ToList();
        var total = items.Count;
        var notes = new List<string>();
        if (total > limit) { items = items.Take(limit).ToList(); notes.Add($"{total} references; showing {limit}"); }
        return Relational(items, sw, notes);
    }

    // Best-effort read/write/call classification from syntax context (Roslyn's
    // ReferenceLocation does not classify these).
    private static async Task<string> ClassifyReferenceAsync(ReferenceLocation refLoc, CancellationToken ct)
    {
        var root = await refLoc.Document.GetSyntaxRootAsync(ct);
        if (root is null) return "reference";
        var span = refLoc.Location.SourceSpan;
        var node = root.FindToken(span.Start).Parent;
        for (var n = node; n is not null; n = n.Parent)
        {
            if (n is InvocationExpressionSyntax inv && inv.Expression.Span.Contains(span)) return "call";
            if (n is ObjectCreationExpressionSyntax oc && oc.Type.Span.Contains(span)) return "call";
            if (n is AssignmentExpressionSyntax asg && asg.Left.Span.Contains(span)) return "write";
            if (n is ArgumentSyntax arg && !arg.RefKindKeyword.IsKind(SyntaxKind.None)) return "write";
            if (n is StatementSyntax) break;
        }
        return "read";
    }

    public async Task<Envelope<SymbolItem>> FindImplementationsAsync(
        string? path, int? line, int? col, string? symbolId, string? ofTypeArg, int limit, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var res = await ResolveSymbolAsync(path, line, col, symbolId, ct);
        if (res.Ambiguous is { } amb) return Ambiguous<SymbolItem>(amb);
        var symbol = res.Symbol;
        if (symbol is null) return SymbolNotFound<SymbolItem>();

        var results = new List<(ISymbol Sym, string Relation)>();
        if (symbol is INamedTypeSymbol type && type.TypeKind != TypeKind.Interface)
        {
            foreach (var s in await SymbolFinder.FindDerivedClassesAsync(type, solution!, transitive: true, null, ct))
                results.Add((s, "derives"));
        }
        else
        {
            foreach (var s in await SymbolFinder.FindImplementationsAsync(symbol, solution!, null, ct))
                results.Add((s, "implements"));
            if (symbol is not INamedTypeSymbol)
                foreach (var s in await SymbolFinder.FindOverridesAsync(symbol, solution!, null, ct))
                    results.Add((s, "overrides"));
        }

        // Source implementers only — a metadata interface (e.g. IDisposable) has
        // thousands of BCL implementers that are noise for code navigation.
        var sourceResults = results.Where(r => r.Sym.Locations.Any(l => l.IsInSource)).ToList();
        var metaOmitted = results.Count - sourceResults.Count;

        var notes = new List<string>();

        // --of: keep only implementers of the closed generic IFace<ofTypeArg>.
        // We match on the simple Name of the first type argument, comparing by
        // DocumentationCommentId of the original definition so the filter is stable
        // across projects that reference the same interface from different compilations.
        if (!string.IsNullOrEmpty(ofTypeArg) && symbol is INamedTypeSymbol ifaceSymbol)
        {
            var ifaceId = ifaceSymbol.OriginalDefinition.GetDocumentationCommentId();
            var beforeFilter = sourceResults.Count;
            sourceResults = sourceResults
                .Where(r =>
                {
                    if (r.Sym is not INamedTypeSymbol cls) return false;
                    return cls.AllInterfaces.Any(iface =>
                        iface.IsGenericType &&
                        iface.OriginalDefinition.GetDocumentationCommentId() == ifaceId &&
                        iface.TypeArguments.Length > 0 &&
                        iface.TypeArguments[0].Name.Equals(ofTypeArg, StringComparison.OrdinalIgnoreCase));
                })
                .ToList();
            notes.Add($"filtered to {ifaceSymbol.Name}<{ofTypeArg}>: {beforeFilter} → {sourceResults.Count}");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<SymbolItem>();
        foreach (var (s, rel) in sourceResults)
        {
            if (!seen.Add((s.GetDocumentationCommentId() ?? s.Name) + "|" + rel)) continue;
            items.Add(SymbolItemFrom(s, rel));
        }
        items = items.OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
        if (items.Count == 0) notes.Add("no implementations / derived types found in source");
        if (metaOmitted > 0) notes.Add($"{metaOmitted} metadata implementer(s) omitted (source only)");
        if (items.Count > limit) items = items.Take(limit).ToList();
        return Relational(items, sw, notes);
    }

    public async Task<Envelope<SymbolItem>> FindInjectorsAsync(
        string? path, int? line, int? col, string? symbolId, int limit, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var res = await ResolveSymbolAsync(path, line, col, symbolId, ct);
        if (res.Ambiguous is { } amb) return Ambiguous<SymbolItem>(amb);
        var symbol = res.Symbol;
        if (symbol is null) return SymbolNotFound<SymbolItem>();
        if (symbol is not INamedTypeSymbol)
            return InvalidArg<SymbolItem>("select a type (the dependency being injected)");

        // A reference to the type that sits inside a constructor parameter's type is an
        // injection site; the constructor's containing class is the injector.
        var found = await SymbolFinder.FindReferencesAsync(symbol, solution!, ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<SymbolItem>();
        foreach (var rs in found)
        {
            foreach (var refLoc in rs.Locations)
            {
                if (!refLoc.Location.IsInSource) continue;
                var root = await refLoc.Document.GetSyntaxRootAsync(ct);
                var node = root?.FindToken(refLoc.Location.SourceSpan.Start).Parent;
                var injectorType = ConstructorInjectorType(node, refLoc.Location.SourceSpan);
                if (injectorType is null) continue;

                var model = await refLoc.Document.GetSemanticModelAsync(ct);
                if (model?.GetDeclaredSymbol(injectorType, ct) is not INamedTypeSymbol cls) continue;
                if (!cls.Locations.Any(l => l.IsInSource)) continue;
                if (!seen.Add(cls.GetDocumentationCommentId() ?? cls.Name)) continue;
                items.Add(SymbolItemFrom(cls, "injects"));
            }
        }
        items = items.OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
        var notes = new List<string>();
        if (items.Count == 0) notes.Add("no constructor-injection sites found in source");
        if (items.Count > limit) items = items.Take(limit).ToList();
        return Relational(items, sw, notes);
    }

    // Walk up from a type reference to the class that injects it. The reference must be
    // part of a constructor parameter's declared *type* (not its default value or an
    // attribute), in either a classic constructor or a primary constructor (whose
    // parameters hang off the type declaration itself).
    private static TypeDeclarationSyntax? ConstructorInjectorType(
        SyntaxNode? node, Microsoft.CodeAnalysis.Text.TextSpan span)
    {
        ParameterSyntax? param = null;
        for (var n = node; n is not null; n = n.Parent)
        {
            if (n is ParameterSyntax p && p.Type is not null && p.Type.Span.Contains(span))
                param = p;
            else if (n is ConstructorDeclarationSyntax ctor)
                return param is not null ? ctor.Parent as TypeDeclarationSyntax : null;
            else if (n is TypeDeclarationSyntax type) // primary-constructor parameters
                return param is not null && type.ParameterList?.Span.Contains(param.Span) == true
                    ? type : null;
            else if (n is MemberDeclarationSyntax)
                return null; // a different member (method/property/field) — not a ctor param
        }
        return null;
    }

    public async Task<Envelope<HierarchyItem>> TypeHierarchyAsync(
        string? path, int? line, int? col, string? symbolId, string direction, int limit, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var res = await ResolveSymbolAsync(path, line, col, symbolId, ct);
        if (res.Ambiguous is { } amb) return Ambiguous<HierarchyItem>(amb);
        var symbol = res.Symbol;
        if (symbol is null) return SymbolNotFound<HierarchyItem>();
        if (symbol is not INamedTypeSymbol type)
            return InvalidArg<HierarchyItem>("select a type, not a member");

        var items = new List<HierarchyItem>();
        var wantBase = direction is "both" or "base";
        var wantDerived = direction is "both" or "derived";
        if (wantBase)
        {
            var d = 1;
            for (var b = type.BaseType; b is not null && b.SpecialType != SpecialType.System_Object; b = b.BaseType)
                items.Add(HierItem(b, "base", d++));
            foreach (var i in type.AllInterfaces)
                items.Add(HierItem(i, "interface", 1));
        }
        var notes = new List<string>();
        if (wantDerived)
        {
            IEnumerable<INamedTypeSymbol> found = type.TypeKind == TypeKind.Interface
                ? (await SymbolFinder.FindImplementationsAsync(type, solution!, null, ct)).OfType<INamedTypeSymbol>()
                : await SymbolFinder.FindDerivedClassesAsync(type, solution!, transitive: true, null, ct);
            var derived = found.ToList();
            var source = derived.Where(d => d.Locations.Any(l => l.IsInSource)).ToList();
            foreach (var dt in source) items.Add(HierItem(dt, "derived", 1));
            if (derived.Count - source.Count > 0)
                notes.Add($"{derived.Count - source.Count} metadata derived type(s) omitted (source only)");
        }
        if (items.Count > limit) items = items.Take(limit).ToList();
        return Relational(items, sw, notes);
    }

    public async Task<Envelope<CallNode>> CallersAsync(
        string? path, int? line, int? col, string? symbolId, int depth, int limit, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var res = await ResolveSymbolAsync(path, line, col, symbolId, ct);
        if (res.Ambiguous is { } amb) return Ambiguous<CallNode>(amb);
        var symbol = res.Symbol;
        if (symbol is null) return SymbolNotFound<CallNode>();

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var nodes = await CollectCallersAsync(symbol, 1, Math.Max(1, depth), visited, ct);
        var notes = nodes.Count == 0 ? new List<string> { "no callers found" } : new();
        if (nodes.Count > limit) nodes = nodes.Take(limit).ToList();
        return Relational(nodes, sw, notes);
    }

    private async Task<List<CallNode>> CollectCallersAsync(
        ISymbol symbol, int depth, int maxDepth, HashSet<string> visited, CancellationToken ct)
    {
        var callers = await SymbolFinder.FindCallersAsync(symbol, solution!, ct);
        var list = new List<CallNode>();
        foreach (var c in callers)
        {
            if (!c.IsDirect) continue;
            var calling = c.CallingSymbol;
            var id = calling.GetDocumentationCommentId();
            var sites = c.Locations.Where(l => l.IsInSource).Select(LocFrom).ToList();
            List<CallNode>? children = null;
            if (depth < maxDepth && id is not null && visited.Add(id))
                children = await CollectCallersAsync(calling, depth + 1, maxDepth, visited, ct);
            list.Add(new CallNode
            {
                SymbolId = id,
                Name = calling.Name,
                Kind = Display.KindOf(calling),
                Container = calling.ContainingSymbol?.ToDisplayString(Display.Fqn),
                CallSites = sites,
                Depth = depth,
                Children = children,
            });
        }
        return list.OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
    }

    public async Task<Envelope<DiagnosticItem>> DiagnosticsAsync(
        string scope, string? path, string? projectName, string minSeverity, int limit, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var threshold = ParseSeverity(minSeverity);
        var diags = new List<Diagnostic>();

        if (scope == "file")
        {
            if (path is null) return InvalidArg<DiagnosticItem>("file scope requires a path");
            var rel = NormalizeToRel(path);
            var abs = Path.GetFullPath(Path.Combine(root, rel));
            var docId = solution!.GetDocumentIdsWithFilePath(abs).FirstOrDefault();
            if (docId is null) return InvalidArg<DiagnosticItem>($"no loaded document for '{rel}'");
            var model = await solution.GetDocument(docId)!.GetSemanticModelAsync(ct);
            if (model is null) return new Envelope<DiagnosticItem>
            {
                Ok = false, SnapshotId = SnapshotId,
                Error = new ErrorInfo { Code = "requires_semantic", Message = "No semantic model for the file.", Retryable = true },
            };
            diags.AddRange(model.GetDiagnostics(null, ct));
        }
        else if (scope == "project")
        {
            var proj = solution!.Projects.FirstOrDefault(p => p.Name == projectName)
                       ?? solution.Projects.FirstOrDefault(p => p.Language == LanguageNames.CSharp);
            if (proj is null) return InvalidArg<DiagnosticItem>("no matching project");
            var comp = await proj.GetCompilationAsync(ct);
            if (comp is not null) diags.AddRange(comp.GetDiagnostics(ct));
        }
        else // solution
        {
            foreach (var p in solution!.Projects)
            {
                if (p.Language != LanguageNames.CSharp) continue;
                var comp = await p.GetCompilationAsync(ct);
                if (comp is not null) diags.AddRange(comp.GetDiagnostics(ct));
            }
        }

        var matched = diags.Where(d => d.Severity >= threshold).ToList();
        int errors = matched.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warnings = matched.Count(d => d.Severity == DiagnosticSeverity.Warning);
        int infos = matched.Count(d => d.Severity == DiagnosticSeverity.Info);

        // Missing-reference errors mean the compilation's references are incomplete
        // (usually an unrestored project), not real code errors — flag rather than
        // present them as ground truth.
        int refErrs = matched.Count(d => ReferenceResolutionErrors.Contains(d.Id));

        var items = matched
            .Select(ToDiagnosticItem)
            .OrderBy(i => i.Loc?.Path ?? "", StringComparer.Ordinal)
            .ThenBy(i => i.Loc?.Line ?? 0)
            .ToList();
        var notes = new List<string> { $"errors: {errors}, warnings: {warnings}, info: {infos}" };
        if (refErrs > 0)
            notes.Add($"{refErrs} reference-resolution error(s) — the target looks unrestored; run a build/restore for reliable diagnostics");
        if (items.Count > limit) { notes.Add($"{items.Count} diagnostics; showing {limit}"); items = items.Take(limit).ToList(); }
        return Relational(items, sw, notes, degraded: refErrs > 0);
    }

    private static DiagnosticSeverity ParseSeverity(string s) => s.ToLowerInvariant() switch
    {
        "hidden" => DiagnosticSeverity.Hidden,
        "info" => DiagnosticSeverity.Info,
        "error" => DiagnosticSeverity.Error,
        _ => DiagnosticSeverity.Warning,
    };

    private DiagnosticItem ToDiagnosticItem(Diagnostic d)
    {
        Loc? loc = null;
        EndPos? end = null;
        if (d.Location.IsInSource)
        {
            loc = LocFrom(d.Location);
            var span = d.Location.GetLineSpan();
            end = new EndPos { Line = span.EndLinePosition.Line + 1, Col = span.EndLinePosition.Character + 1 };
        }
        return new DiagnosticItem
        {
            Id = d.Id,
            Severity = d.Severity.ToString().ToLowerInvariant(),
            Message = d.GetMessage(),
            Loc = loc,
            End = end,
            Category = d.Descriptor.Category,
        };
    }

    // Missing-reference / unresolved-assembly compiler errors (see DiagnosticsAsync).
    private static readonly HashSet<string> ReferenceResolutionErrors =
        new(StringComparer.Ordinal) { "CS0006", "CS0009", "CS0012", "CS0234", "CS0246" };

    private Envelope<T> Relational<T>(IReadOnlyList<T> items, Stopwatch sw, List<string> notes, bool degraded = false) => new()
    {
        Tier = "relational",
        SnapshotId = SnapshotId,
        Items = items,
        ElapsedMs = sw.ElapsedMilliseconds,
        Degraded = Degraded || degraded,
        Notes = notes,
    };

    private SymbolItem SymbolItemFrom(ISymbol s, string? relation)
    {
        var src = s.Locations.FirstOrDefault(l => l.IsInSource);
        return new SymbolItem
        {
            SymbolId = s.GetDocumentationCommentId(),
            Name = s.Name,
            Kind = Display.KindOf(s),
            Container = s.ContainingSymbol?.ToDisplayString(Display.Fqn),
            Accessibility = Display.AccessibilityOf(s),
            Signature = Display.SignatureOf(s),
            Loc = src is not null ? LocFrom(src) : null,
            Relation = relation,
            IsMetadata = src is null,
            Source = "semantic",
        };
    }

    private HierarchyItem HierItem(INamedTypeSymbol t, string relation, int depth)
    {
        var src = t.Locations.FirstOrDefault(l => l.IsInSource);
        return new HierarchyItem
        {
            SymbolId = t.GetDocumentationCommentId(),
            Name = t.Name,
            Kind = Display.KindOf(t),
            Relation = relation,
            Depth = depth,
            Loc = src is not null ? LocFrom(src) : null,
            IsMetadata = src is null,
            Source = "semantic",
        };
    }

    private Loc LocFrom(Location loc)
    {
        var span = loc.GetLineSpan();
        var line = span.StartLinePosition.Line;
        var snippet = "";
        if (loc.SourceTree is not null)
        {
            var text = loc.SourceTree.GetText();
            if (line < text.Lines.Count) snippet = text.Lines[line].ToString().Trim();
        }
        return new Loc
        {
            Path = RelPath(loc.SourceTree?.FilePath),
            Line = line + 1,
            Col = span.StartLinePosition.Character + 1,
            Snippet = snippet,
        };
    }

    private Envelope<T> InvalidArg<T>(string message) => new()
    {
        Ok = false,
        SnapshotId = SnapshotId,
        Error = new ErrorInfo { Code = "invalid_argument", Message = message, Retryable = false },
    };

    // A bare name matched more than one declaration. Report the candidates (with their
    // symbol_ids) so the caller can re-run precisely via --id or file:line:col.
    private Envelope<T> Ambiguous<T>(IReadOnlyList<SymbolEntry> cands)
    {
        const int Max = 25;
        var shown = cands
            .OrderBy(c => c.Fqn, StringComparer.Ordinal)
            .Take(Max)
            .Select(c =>
            {
                var loc = c.Primary is { } p ? $"  {p.RelPath}:{p.Line}:{p.Col}" : "";
                return $"{c.Kind} {c.Fqn}  [{c.Moniker}]{loc}";
            });
        var more = cands.Count > Max ? $"\n  … {cands.Count - Max} more" : "";
        var msg = $"'{cands[0].Name}' is ambiguous — {cands.Count} matches. "
                + "Re-run with --id <symbol_id> or file:line:col:\n  "
                + string.Join("\n  ", shown) + more;
        return new Envelope<T>
        {
            Ok = false,
            SnapshotId = SnapshotId,
            Error = new ErrorInfo { Code = "ambiguous_symbol", Message = msg, Retryable = false },
        };
    }

    private Envelope<HoverItem> NotFoundHover() => new()
    {
        Ok = false,
        SnapshotId = SnapshotId,
        Error = new ErrorInfo { Code = "symbol_not_found", Message = "No symbol at the given target.", Retryable = false },
    };

    private Envelope<T> SymbolNotFound<T>() => new()
    {
        Ok = false,
        SnapshotId = SnapshotId,
        Error = new ErrorInfo { Code = "symbol_not_found", Message = "No symbol at the given target.", Retryable = false },
    };

    public void Dispose() => workspace?.Dispose();
}
