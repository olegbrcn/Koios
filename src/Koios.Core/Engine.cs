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
/// The warm Roslyn engine. Loads one solution/project, projects a catalog, and
/// answers hot + relational queries against an immutable snapshot.
///
/// Concurrency model: queries capture the current <see cref="Snapshot"/> once at
/// entry and read only from it, so they stay consistent while the watcher swaps
/// snapshots underneath. All mutation (<see cref="ApplyCsBatchAsync"/>,
/// <see cref="ReloadAsync"/>) is single-writer — only the watcher's apply loop
/// (or the initial load, before serving starts) may call it.
/// </summary>
public sealed class Engine : IDisposable
{
    /// <summary>One immutable, queryable state of the world. Swapped atomically;
    /// Version feeds the wire-visible snapshot_id ("sln@N").</summary>
    private sealed record Snapshot(Solution Solution, Catalog Catalog, int Version)
    {
        public string Id => $"sln@{Version}";
    }

    private MSBuildWorkspace? workspace;
    private volatile Snapshot? current;
    private readonly List<string> loadErrors = new();
    private string? fatalLoadError;
    private string root = "";
    private string solutionFile = "";
    private string targetPath = "";
    private string msbuild = "";
    private long loadMs;
    private int projectsTotal, projectsLoaded, projectsFailed, documents;

    public string Root => root;
    public string SnapshotId => current?.Id ?? "sln@0";

    /// <summary>Memoized relational results (see Protocol.DispatchAsync). Keys embed
    /// the snapshot_id, so snapshot swaps invalidate without any explicit flush.</summary>
    public QueryCache Cache { get; } = new(capacity: 512);

    public IReadOnlyList<string> LoadErrors { get { lock (loadErrors) return loadErrors.ToArray(); } }

    private Snapshot Snap() => current ?? throw new InvalidOperationException("engine not loaded");

    public async Task LoadAsync(string path, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        if (Directory.Exists(full))
            full = ResolveInDirectory(full);

        var ext = Path.GetExtension(full).ToLowerInvariant();
        if (ext is not (".sln" or ".slnx" or ".slnf" or ".csproj"))
            throw new ArgumentException($"Unsupported target '{path}'. Expected a .sln/.slnx/.csproj or a directory.");

        root = Path.GetDirectoryName(full) ?? full;
        solutionFile = Path.GetFileName(full);
        targetPath = full;

        await OpenAndSwapAsync(initial: true, ct);
    }

    /// <summary>Reopen the solution from disk in a fresh workspace and swap the
    /// snapshot. On failure the previous snapshot keeps serving and the error is
    /// recorded. Single-writer: watcher apply loop only.</summary>
    public Task<bool> ReloadAsync(CancellationToken ct = default) => OpenAndSwapAsync(initial: false, ct);

    private async Task<bool> OpenAndSwapAsync(bool initial, CancellationToken ct)
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
        var errors = new List<string>();
        using var failedRegistration = ws.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind != WorkspaceDiagnosticKind.Failure)
                return;
            var msg = e.Diagnostic.Message;
            // NuGet restore advisories (NU1903 etc.) surface as workspace failures
            // but do not prevent the project from loading — they are noise here.
            if (msg.Contains("NU1903") || msg.Contains("known high severity vulnerability")
                || msg.Contains("known moderate severity vulnerability"))
                return;
            lock (errors) errors.Add(msg);
        });

        Solution opened;
        try
        {
            if (targetPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                opened = (await ws.OpenProjectAsync(targetPath, cancellationToken: ct)).Solution;
            else
                opened = await ws.OpenSolutionAsync(targetPath, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // The solution could not be opened (most often a global.json SDK pin that
            // is not installed, or a broken edit to a project file). Initial load:
            // degrade to an error state — status must keep working. Reload: keep the
            // previous snapshot serving and record why the reload failed.
            ws.Dispose();
            var msg = HumanizeLoadFailure(ex.Message);
            if (initial)
            {
                fatalLoadError = msg;
                lock (loadErrors) loadErrors.Add(msg);
                loadMs = sw.ElapsedMilliseconds;
            }
            else
            {
                lock (loadErrors) loadErrors.Add($"reload failed (still serving {SnapshotId}): {msg}");
            }
            return false;
        }

        // Drop analyzer references. We never run analyzers for navigation, and an
        // UnresolvedAnalyzerReference (common in MSBuildWorkspace loads) crashes
        // SymbolFinder operations that compute project checksums. Compiler
        // diagnostics from GetDiagnostics are unaffected.
        opened = StripAnalyzerReferences(opened);

        var (built, total, loaded, failed, docs) = await BuildCatalogAsync(opened, errors, ct);

        var old = workspace;
        workspace = ws;
        lock (loadErrors) { loadErrors.Clear(); loadErrors.AddRange(errors); }
        fatalLoadError = null;
        projectsTotal = total;
        projectsLoaded = loaded;
        projectsFailed = failed;
        documents = docs;
        loadMs = sw.ElapsedMilliseconds;
        current = new Snapshot(opened, built, (current?.Version ?? 0) + 1);
        old?.Dispose();
        return true;
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

    private async Task<(Catalog Catalog, int Total, int Loaded, int Failed, int Documents)> BuildCatalogAsync(
        Solution sln, List<string> errors, CancellationToken ct)
    {
        var built = new Catalog();
        int total = sln.Projects.Count(), loaded = 0, failed = 0, docs = 0;

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
                failed++;
                lock (errors) errors.Add($"{project.Name}: {ex.Message}");
                continue;
            }

            if (comp is null)
            {
                failed++;
                continue;
            }

            loaded++;
            docs += project.Documents.Count();
            foreach (var type in EnumerateTypes(comp.Assembly.GlobalNamespace))
                AddTypeAndMembers(built, type);
        }

        return (built, total, loaded, failed, docs);
    }

    private void AddTypeAndMembers(Catalog into, INamedTypeSymbol type)
    {
        AddSymbol(into, type);
        foreach (var member in type.GetMembers())
        {
            if (member is INamedTypeSymbol)
                continue; // nested types are projected as types in their own right
            if (member.IsImplicitlyDeclared)
                continue;
            // Property/event accessors are navigation noise; the property/event
            // itself is the user-facing symbol.
            if (member is IMethodSymbol m && m.MethodKind is
                MethodKind.PropertyGet or MethodKind.PropertySet or
                MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise)
                continue;
            AddSymbol(into, member);
        }
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

    // ---- Live-edit mutation (single-writer: the watcher's apply loop) --------

    /// <summary>
    /// Apply a debounced batch of .cs file changes to the current snapshot: an
    /// existing file gets new text, a missing file's documents are removed, and a
    /// new file is added to the project(s) whose directory contains it (matching
    /// SDK-style globbing; multi-TFM projects each get a copy). The whole batch
    /// yields ONE new (Solution, Catalog) pair and one snapshot_id bump.
    /// </summary>
    public async Task ApplyCsBatchAsync(IReadOnlyList<string> absPaths, CancellationToken ct = default)
    {
        var snap = Snap();
        var sln = snap.Solution;
        var changedRel = new HashSet<string>(StringComparer.Ordinal);
        var touched = new List<DocumentId>();

        foreach (var raw in absPaths)
        {
            ct.ThrowIfCancellationRequested();
            var abs = Path.GetFullPath(raw);
            changedRel.Add(RelPath(abs));
            var ids = sln.GetDocumentIdsWithFilePath(abs);
            if (File.Exists(abs))
            {
                SourceText text;
                try { text = SourceText.From(File.ReadAllText(abs)); }
                catch (IOException) { continue; } // mid-write; the next event retries
                if (!ids.IsEmpty)
                {
                    foreach (var id in ids)
                    {
                        sln = sln.WithDocumentText(id, text);
                        touched.Add(id);
                    }
                }
                else
                {
                    foreach (var project in ProjectsOwning(sln, abs))
                    {
                        var id = DocumentId.CreateNewId(project.Id);
                        sln = sln.AddDocument(id, Path.GetFileName(abs), text, filePath: abs);
                        touched.Add(id);
                    }
                }
            }
            else
            {
                foreach (var id in ids)
                    sln = sln.RemoveDocument(id);
            }
        }

        // Rebuild the catalog incrementally: keep entries untouched by this batch,
        // re-project the touched documents, then re-resolve symbols that were dropped
        // but still have a declaration in an unchanged file (a partial type whose
        // other part survives must not vanish from the catalog).
        var rebuilt = new Catalog();
        var dropped = new List<SymbolEntry>();
        foreach (var e in snap.Catalog.All)
        {
            if (e.DeclLocations.Any(d => changedRel.Contains(d.RelPath)))
                dropped.Add(e);
            else
                rebuilt.Add(e);
        }

        foreach (var id in touched.Distinct())
        {
            var doc = sln.GetDocument(id);
            if (doc is null) continue;
            var model = await doc.GetSemanticModelAsync(ct);
            var unit = await doc.GetSyntaxRootAsync(ct);
            if (model is null || unit is null) continue;
            foreach (var type in DeclaredTypes(model, unit, ct))
                AddTypeAndMembers(rebuilt, type);
        }

        foreach (var e in dropped)
        {
            if (rebuilt.ByMoniker(e.Moniker) is not null) continue;
            var survivor = e.DeclLocations.FirstOrDefault(d => !changedRel.Contains(d.RelPath));
            if (survivor is null) continue;
            var abs = Path.GetFullPath(Path.Combine(root, survivor.RelPath));
            var docId = sln.GetDocumentIdsWithFilePath(abs).FirstOrDefault();
            if (docId is null) continue;
            var comp = await sln.GetProject(docId.ProjectId)!.GetCompilationAsync(ct);
            if (comp is null) continue;
            if (DocumentationCommentId.GetFirstSymbolForDeclarationId(e.Moniker, comp) is { } sym)
                AddSymbol(rebuilt, sym);
        }

        documents = sln.Projects.Sum(p => p.Documents.Count());
        current = new Snapshot(sln, rebuilt, snap.Version + 1);
    }

    /// <summary>Whether any loaded document lives under the given directory. The
    /// watcher uses this to detect that a deleted/renamed directory took source
    /// files with it (inotify reports only the directory, not its children).</summary>
    public bool HasDocumentsUnder(string absDir)
    {
        var snap = current;
        if (snap is null) return false;
        var prefix = Path.GetFullPath(absDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return snap.Solution.Projects.Any(p => p.Documents.Any(d =>
            d.FilePath is { } f && f.StartsWith(prefix, StringComparison.Ordinal)));
    }

    /// <summary>Type declarations in one syntax tree (descending into namespaces and
    /// type bodies for nested types, but not into member bodies). Members are then
    /// enumerated via the symbol API so the projection matches the full load exactly.</summary>
    private static IEnumerable<INamedTypeSymbol> DeclaredTypes(SemanticModel model, SyntaxNode unit, CancellationToken ct)
    {
        foreach (var node in unit.DescendantNodes(n =>
                     n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or BaseTypeDeclarationSyntax))
        {
            if (node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax
                && model.GetDeclaredSymbol(node, ct) is INamedTypeSymbol t)
                yield return t;
        }
    }

    /// <summary>Projects whose directory contains the file — the deepest match wins
    /// (SDK-style projects glob-include .cs under their own directory). Several
    /// projects can share one directory (multi-TFM); all of them get the file.</summary>
    private static IEnumerable<Project> ProjectsOwning(Solution sln, string absFile)
    {
        var owners = sln.Projects
            .Where(p => p.Language == LanguageNames.CSharp && p.FilePath is not null)
            .Select(p => (Project: p, Dir: Path.GetDirectoryName(p.FilePath)!))
            .Where(t => absFile.StartsWith(t.Dir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToList();
        if (owners.Count == 0)
            yield break;
        var deepest = owners.Max(t => t.Dir.Length);
        foreach (var t in owners.Where(t => t.Dir.Length == deepest))
            yield return t.Project;
    }

    // ---- Queries -----------------------------------------------------------

    /// <summary>True when the solution could not be opened at all.</summary>
    public bool LoadFailed => current is null;
    public string? FatalLoadError => fatalLoadError;

    /// <summary>Some projects/tasks failed to load (e.g. F#/Aspire projects, missing
    /// build tasks); C# navigation still works for what loaded, but completeness is reduced.</summary>
    private bool Degraded
    {
        get { lock (loadErrors) return projectsFailed > 0 || loadErrors.Count > 0; }
    }

    public Envelope<StatusInfo> Status(ResidentInfo? resident = null)
    {
        var snap = current;
        var state = snap is null || projectsLoaded == 0 ? "error" : "ready";
        var snapshotId = snap?.Id ?? "sln@0";
        string[] errs;
        lock (loadErrors) errs = loadErrors.ToArray();
        return new Envelope<StatusInfo>
        {
            SnapshotId = snapshotId,
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
                    SnapshotId = snapshotId,
                    Projects = new ProjectCounts
                    {
                        Total = projectsTotal,
                        Loaded = projectsLoaded,
                        Failed = projectsFailed,
                    },
                    Documents = documents,
                    Symbols = snap?.Catalog.Count ?? 0,
                    LoadErrors = errs,
                    LoadMs = loadMs,
                    Resident = resident,
                }
            },
        };
    }

    public Envelope<SymbolItem> Search(string query, IReadOnlySet<string>? kinds, int limit)
    {
        var snap = Snap();
        var sw = Stopwatch.StartNew();
        var hits = snap.Catalog.Search(query, kinds, limit);
        var items = hits.Select(h => ToSymbolItem(h.Entry, h.Score)).ToArray();
        return new Envelope<SymbolItem>
        {
            Tier = "hot",
            SnapshotId = snap.Id,
            Items = items,
            ElapsedMs = sw.ElapsedMilliseconds,
            Degraded = Degraded,
        };
    }

    public Envelope<OutlineNode> Outline(string relOrAbsPath)
    {
        var snap = Snap();
        var catalog = snap.Catalog;
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
                SnapshotId = snap.Id,
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
            roots.Add(BuildOutline(catalog, e, rel));
        }

        return new Envelope<OutlineNode>
        {
            Tier = "hot",
            SnapshotId = snap.Id,
            Items = roots,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    private OutlineNode BuildOutline(Catalog catalog, SymbolEntry e, string rel)
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
            node.Children.Add(BuildOutline(catalog, child, rel));
        }
        return node;
    }

    private Envelope<HoverItem> HoverById(Snapshot snap, string symbolId)
    {
        var e = snap.Catalog.ByMoniker(symbolId);
        if (e is null)
            return NotFoundHover(snap);
        return new Envelope<HoverItem>
        {
            Tier = "hot",
            SnapshotId = snap.Id,
            Items = new[] { ToHover(e) },
        };
    }

    public async Task<Envelope<SymbolItem>> DefinitionAsync(string? path, int? line, int? col, string? symbolId, CancellationToken ct)
    {
        var snap = Snap();
        if (!string.IsNullOrEmpty(symbolId))
        {
            var (moniker, candidates) = NormalizeTargetId(snap.Catalog, symbolId!);
            if (candidates is not null) return Ambiguous<SymbolItem>(snap, candidates);
            var e = snap.Catalog.ByMoniker(moniker ?? symbolId!);
            if (e is null)
                return SymbolNotFound<SymbolItem>(snap);
            return new Envelope<SymbolItem>
            {
                Tier = "hot",
                SnapshotId = snap.Id,
                Items = new[] { ToSymbolItem(e, null) },
            };
        }

        var (sym, _) = await ResolvePositionalAsync(snap, path, line, col, ct);
        if (sym is null)
            return SymbolNotFound<SymbolItem>(snap);

        var entry = snap.Catalog.ByMoniker(sym.GetDocumentationCommentId() ?? "");
        if (entry is not null)
            return new Envelope<SymbolItem> { Tier = "hot_semantic", SnapshotId = snap.Id, Items = new[] { ToSymbolItem(entry, null) } };

        // metadata symbol
        return new Envelope<SymbolItem>
        {
            Tier = "hot_semantic",
            SnapshotId = snap.Id,
            Items = new[] { ToSymbolItemFromMetadata(sym) },
        };
    }

    public async Task<Envelope<HoverItem>> HoverAsync(string? path, int? line, int? col, string? symbolId, CancellationToken ct)
    {
        var snap = Snap();
        if (!string.IsNullOrEmpty(symbolId))
        {
            var (moniker, candidates) = NormalizeTargetId(snap.Catalog, symbolId!);
            if (candidates is not null) return Ambiguous<HoverItem>(snap, candidates);
            return HoverById(snap, moniker ?? symbolId!);
        }

        var (sym, _) = await ResolvePositionalAsync(snap, path, line, col, ct);
        if (sym is null)
            return NotFoundHover(snap);

        var entry = snap.Catalog.ByMoniker(sym.GetDocumentationCommentId() ?? "");
        if (entry is not null)
            return new Envelope<HoverItem> { Tier = "hot_semantic", SnapshotId = snap.Id, Items = new[] { ToHover(entry) } };

        return new Envelope<HoverItem>
        {
            Tier = "hot_semantic",
            SnapshotId = snap.Id,
            Items = new[] { ToHoverFromMetadata(sym) },
        };
    }

    private async Task<(ISymbol? Symbol, Document? Doc)> ResolvePositionalAsync(
        Snapshot snap, string? path, int? line, int? col, CancellationToken ct)
    {
        if (path is null || line is null || col is null)
            throw new ArgumentException("Positional lookup needs path, line, and col (or pass symbol_id).");

        var rel = NormalizeToRel(path);
        var abs = Path.GetFullPath(Path.Combine(root, rel));
        var docId = snap.Solution.GetDocumentIdsWithFilePath(abs).FirstOrDefault();
        if (docId is null)
            return (null, null);
        var doc = snap.Solution.GetDocument(docId);
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
    private static (string? Moniker, IReadOnlyList<SymbolEntry>? Candidates) NormalizeTargetId(Catalog catalog, string raw)
    {
        if (catalog.ByMoniker(raw) is not null) return (raw, null);
        if (raw.Length > 1 && raw[1] == ':') return (raw, null); // doc-comment id shape

        var matches = catalog.ByExactName(raw);
        if (matches.Count == 1) return (matches[0].Moniker, null);
        if (matches.Count == 0) return (null, null);
        return (null, matches);
    }

    /// <summary>Resolve a target (positional, symbol_id, or bare name) to a live ISymbol.</summary>
    private async Task<Resolved> ResolveSymbolAsync(
        Snapshot snap, string? path, int? line, int? col, string? symbolId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(symbolId))
        {
            var (moniker, candidates) = NormalizeTargetId(snap.Catalog, symbolId!);
            if (candidates is not null) return new Resolved(null, candidates);
            var id = moniker ?? symbolId!;
            foreach (var project in snap.Solution.Projects)
            {
                if (project.Language != LanguageNames.CSharp) continue;
                var comp = await project.GetCompilationAsync(ct);
                if (comp is null) continue;
                var sym = DocumentationCommentId.GetFirstSymbolForDeclarationId(id, comp);
                if (sym is not null) return new Resolved(sym, null);
            }
            return new Resolved(null, null);
        }
        var (s, _) = await ResolvePositionalAsync(snap, path, line, col, ct);
        return new Resolved(s, null);
    }

    public async Task<Envelope<ReferenceItem>> FindReferencesAsync(
        string? path, int? line, int? col, string? symbolId, bool includeDeclaration, int limit, CancellationToken ct)
    {
        var snap = Snap();
        var sw = Stopwatch.StartNew();
        var res = await ResolveSymbolAsync(snap, path, line, col, symbolId, ct);
        if (res.Ambiguous is { } amb) return Ambiguous<ReferenceItem>(snap, amb);
        var symbol = res.Symbol;
        if (symbol is null) return SymbolNotFound<ReferenceItem>(snap);

        var found = await SymbolFinder.FindReferencesAsync(symbol, snap.Solution, ct);
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
        return Relational(snap, items, sw, notes);
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
        var snap = Snap();
        var sw = Stopwatch.StartNew();
        var res = await ResolveSymbolAsync(snap, path, line, col, symbolId, ct);
        if (res.Ambiguous is { } amb) return Ambiguous<SymbolItem>(snap, amb);
        var symbol = res.Symbol;
        if (symbol is null) return SymbolNotFound<SymbolItem>(snap);

        var results = new List<(ISymbol Sym, string Relation)>();
        if (symbol is INamedTypeSymbol type && type.TypeKind != TypeKind.Interface)
        {
            foreach (var s in await SymbolFinder.FindDerivedClassesAsync(type, snap.Solution, transitive: true, null, ct))
                results.Add((s, "derives"));
        }
        else
        {
            foreach (var s in await SymbolFinder.FindImplementationsAsync(symbol, snap.Solution, null, ct))
                results.Add((s, "implements"));
            if (symbol is not INamedTypeSymbol)
                foreach (var s in await SymbolFinder.FindOverridesAsync(symbol, snap.Solution, null, ct))
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
        return Relational(snap, items, sw, notes);
    }

    public async Task<Envelope<SymbolItem>> FindInjectorsAsync(
        string? path, int? line, int? col, string? symbolId, int limit, CancellationToken ct)
    {
        var snap = Snap();
        var sw = Stopwatch.StartNew();
        var res = await ResolveSymbolAsync(snap, path, line, col, symbolId, ct);
        if (res.Ambiguous is { } amb) return Ambiguous<SymbolItem>(snap, amb);
        var symbol = res.Symbol;
        if (symbol is null) return SymbolNotFound<SymbolItem>(snap);
        if (symbol is not INamedTypeSymbol)
            return InvalidArg<SymbolItem>(snap, "select a type (the dependency being injected)");

        // A reference to the type that sits inside a constructor parameter's type is an
        // injection site; the constructor's containing class is the injector.
        var found = await SymbolFinder.FindReferencesAsync(symbol, snap.Solution, ct);
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
        return Relational(snap, items, sw, notes);
    }

    // The inverse of `injectors`: the constructor dependencies a type declares (what it
    // injects). Pure semantic API — `InstanceConstructors` already covers classic and
    // primary constructors, so no syntax walking is needed.
    public async Task<Envelope<SymbolItem>> FindDependenciesAsync(
        string? path, int? line, int? col, string? symbolId, int limit, CancellationToken ct)
    {
        var snap = Snap();
        var sw = Stopwatch.StartNew();
        var res = await ResolveSymbolAsync(snap, path, line, col, symbolId, ct);
        if (res.Ambiguous is { } amb) return Ambiguous<SymbolItem>(snap, amb);
        var symbol = res.Symbol;
        if (symbol is null) return SymbolNotFound<SymbolItem>(snap);
        if (symbol is not INamedTypeSymbol type)
            return InvalidArg<SymbolItem>(snap, "select a type (the class whose constructor dependencies you want)");

        // Skip compiler-synthesized constructors — a record's copy ctor takes the record
        // itself as its one parameter and would masquerade as a dependency. Tie-break by
        // declaration order so the pick is deterministic.
        var ctors = type.InstanceConstructors
            .Where(c => !c.IsImplicitlyDeclared && c.Parameters.Length > 0)
            .OrderByDescending(c => c.Parameters.Length)
            .ThenBy(c => c.DeclaringSyntaxReferences.FirstOrDefault()?.Span.Start ?? int.MaxValue)
            .ToList();
        var notes = new List<string>();
        if (ctors.Count == 0)
            return Relational(snap, Array.Empty<SymbolItem>(), sw,
                new List<string> { "no constructor parameters (no injected dependencies)" });

        var ctor = ctors[0];
        if (ctors.Count > 1)
            notes.Add($"{ctors.Count} constructors; showing the greediest ({ctor.Parameters.Length} params)");

        var items = new List<SymbolItem>();
        foreach (var p in ctor.Parameters)
        {
            var pt = p.Type;
            var src = pt.Locations.FirstOrDefault(l => l.IsInSource);
            items.Add(new SymbolItem
            {
                SymbolId = pt.OriginalDefinition.GetDocumentationCommentId(),
                Name = pt.Name,
                Kind = Display.KindOf(pt),
                Container = pt.ContainingNamespace?.ToDisplayString(),
                Signature = $"{pt.ToDisplayString(Display.Fqn)} {p.Name}",
                Loc = src is not null ? LocFrom(src) : null,
                Relation = "injects",
                IsMetadata = src is null,
                Source = "semantic",
            });
        }
        if (items.Count > limit) items = items.Take(limit).ToList();
        return Relational(snap, items, sw, notes);
    }

    // Outgoing calls (the inverse of `callers`): first-party members used by the
    // target method, or by every method of the target type — method invocations,
    // constructor calls (incl. target-typed new), and property/indexer accesses.
    // Source-only — calls into metadata/BCL are counted and reported, not listed.
    public async Task<Envelope<CallNode>> FindCalleesAsync(
        string? path, int? line, int? col, string? symbolId, int limit, CancellationToken ct)
    {
        var snap = Snap();
        var sw = Stopwatch.StartNew();
        var res = await ResolveSymbolAsync(snap, path, line, col, symbolId, ct);
        if (res.Ambiguous is { } amb) return Ambiguous<CallNode>(snap, amb);
        var symbol = res.Symbol;
        if (symbol is null) return SymbolNotFound<CallNode>(snap);

        IEnumerable<IMethodSymbol> methods = symbol switch
        {
            IMethodSymbol m => new[] { m },
            INamedTypeSymbol t => t.GetMembers().OfType<IMethodSymbol>(),
            _ => Array.Empty<IMethodSymbol>(),
        };
        if (!methods.Any())
            return InvalidArg<CallNode>(snap, "select a method or a type");

        var sites = new Dictionary<string, List<Loc>>(StringComparer.Ordinal);
        var meta = new Dictionary<string, ISymbol>(StringComparer.Ordinal);
        var order = new List<string>();
        var metaOmitted = 0;

        void Record(ISymbol callee, Location loc)
        {
            var target = callee.OriginalDefinition;
            if (!target.Locations.Any(l => l.IsInSource)) { metaOmitted++; return; }
            var id = target.GetDocumentationCommentId() ?? target.ToDisplayString();
            if (!sites.TryGetValue(id, out var list))
            {
                list = new List<Loc>();
                sites[id] = list;
                meta[id] = target;
                order.Add(id);
            }
            if (loc.IsInSource) list.Add(LocFrom(loc));
        }

        foreach (var method in methods)
        {
            foreach (var sref in method.DeclaringSyntaxReferences)
            {
                var node = await sref.GetSyntaxAsync(ct);
                var doc = snap.Solution.GetDocument(node.SyntaxTree);
                if (doc is null) continue;
                var model = await doc.GetSemanticModelAsync(ct);
                if (model is null) continue;
                foreach (var n in node.DescendantNodes())
                {
                    switch (n)
                    {
                        case InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax:
                        {
                            // Method calls and constructor calls (incl. target-typed new()).
                            var info = model.GetSymbolInfo(n, ct);
                            if ((info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is IMethodSymbol callee)
                                Record(callee, n.GetLocation());
                            break;
                        }
                        case ElementAccessExpressionSyntax element:
                        {
                            // Indexer accesses.
                            if (model.GetSymbolInfo(element, ct).Symbol is IPropertySymbol indexer)
                                Record(indexer, element.GetLocation());
                            break;
                        }
                        case SimpleNameSyntax name:
                        {
                            // Property accessor calls. Resolving the simple name (not the
                            // enclosing member-access) counts each access exactly once.
                            if (model.GetSymbolInfo(name, ct).Symbol is IPropertySymbol prop)
                                Record(prop, name.GetLocation());
                            break;
                        }
                    }
                }
            }
        }

        var items = order
            .Select(id =>
            {
                var t = meta[id];
                return new CallNode
                {
                    SymbolId = t.GetDocumentationCommentId(),
                    Name = t.Name,
                    Kind = Display.KindOf(t),
                    Container = t.ContainingType?.ToDisplayString(Display.Fqn),
                    CallSites = sites[id],
                    Depth = 1,
                };
            })
            .OrderBy(n => n.Container, StringComparer.Ordinal).ThenBy(n => n.Name, StringComparer.Ordinal)
            .ToList();
        var notes = new List<string>();
        if (items.Count == 0) notes.Add("no first-party calls found in source");
        if (metaOmitted > 0) notes.Add($"{metaOmitted} call(s) to metadata/BCL members omitted (source only)");
        if (items.Count > limit) items = items.Take(limit).ToList();
        return Relational(snap, items, sw, notes);
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
        var snap = Snap();
        var sw = Stopwatch.StartNew();
        var res = await ResolveSymbolAsync(snap, path, line, col, symbolId, ct);
        if (res.Ambiguous is { } amb) return Ambiguous<HierarchyItem>(snap, amb);
        var symbol = res.Symbol;
        if (symbol is null) return SymbolNotFound<HierarchyItem>(snap);
        if (symbol is not INamedTypeSymbol type)
            return InvalidArg<HierarchyItem>(snap, "select a type, not a member");

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
                ? (await SymbolFinder.FindImplementationsAsync(type, snap.Solution, null, ct)).OfType<INamedTypeSymbol>()
                : await SymbolFinder.FindDerivedClassesAsync(type, snap.Solution, transitive: true, null, ct);
            var derived = found.ToList();
            var source = derived.Where(d => d.Locations.Any(l => l.IsInSource)).ToList();
            foreach (var dt in source) items.Add(HierItem(dt, "derived", 1));
            if (derived.Count - source.Count > 0)
                notes.Add($"{derived.Count - source.Count} metadata derived type(s) omitted (source only)");
        }
        if (items.Count > limit) items = items.Take(limit).ToList();
        return Relational(snap, items, sw, notes);
    }

    public async Task<Envelope<CallNode>> CallersAsync(
        string? path, int? line, int? col, string? symbolId, int depth, int limit, CancellationToken ct)
    {
        var snap = Snap();
        var sw = Stopwatch.StartNew();
        var res = await ResolveSymbolAsync(snap, path, line, col, symbolId, ct);
        if (res.Ambiguous is { } amb) return Ambiguous<CallNode>(snap, amb);
        var symbol = res.Symbol;
        if (symbol is null) return SymbolNotFound<CallNode>(snap);

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var nodes = await CollectCallersAsync(snap.Solution, symbol, 1, Math.Max(1, depth), visited, ct);
        var notes = nodes.Count == 0 ? new List<string> { "no callers found" } : new();
        if (nodes.Count > limit) nodes = nodes.Take(limit).ToList();
        return Relational(snap, nodes, sw, notes);
    }

    private async Task<List<CallNode>> CollectCallersAsync(
        Solution solution, ISymbol symbol, int depth, int maxDepth, HashSet<string> visited, CancellationToken ct)
    {
        var callers = await SymbolFinder.FindCallersAsync(symbol, solution, ct);
        var list = new List<CallNode>();
        foreach (var c in callers)
        {
            if (!c.IsDirect) continue;
            var calling = c.CallingSymbol;
            var id = calling.GetDocumentationCommentId();
            var sites = c.Locations.Where(l => l.IsInSource).Select(LocFrom).ToList();
            List<CallNode>? children = null;
            if (depth < maxDepth && id is not null && visited.Add(id))
                children = await CollectCallersAsync(solution, calling, depth + 1, maxDepth, visited, ct);
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
        var snap = Snap();
        var sw = Stopwatch.StartNew();
        var threshold = ParseSeverity(minSeverity);
        var diags = new List<Diagnostic>();

        if (scope == "file")
        {
            if (path is null) return InvalidArg<DiagnosticItem>(snap, "file scope requires a path");
            var rel = NormalizeToRel(path);
            var abs = Path.GetFullPath(Path.Combine(root, rel));
            var docId = snap.Solution.GetDocumentIdsWithFilePath(abs).FirstOrDefault();
            if (docId is null) return InvalidArg<DiagnosticItem>(snap, $"no loaded document for '{rel}'");
            var model = await snap.Solution.GetDocument(docId)!.GetSemanticModelAsync(ct);
            if (model is null) return new Envelope<DiagnosticItem>
            {
                Ok = false, SnapshotId = snap.Id,
                Error = new ErrorInfo { Code = "requires_semantic", Message = "No semantic model for the file.", Retryable = true },
            };
            diags.AddRange(model.GetDiagnostics(null, ct));
        }
        else if (scope == "project")
        {
            var proj = snap.Solution.Projects.FirstOrDefault(p => p.Name == projectName)
                       ?? snap.Solution.Projects.FirstOrDefault(p => p.Language == LanguageNames.CSharp);
            if (proj is null) return InvalidArg<DiagnosticItem>(snap, "no matching project");
            var comp = await proj.GetCompilationAsync(ct);
            if (comp is not null) diags.AddRange(comp.GetDiagnostics(ct));
        }
        else // solution
        {
            foreach (var p in snap.Solution.Projects)
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
        return Relational(snap, items, sw, notes, degraded: refErrs > 0);
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

    private Envelope<T> Relational<T>(Snapshot snap, IReadOnlyList<T> items, Stopwatch sw, List<string> notes, bool degraded = false) => new()
    {
        Tier = "relational",
        SnapshotId = snap.Id,
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

    private Envelope<T> InvalidArg<T>(Snapshot snap, string message) => new()
    {
        Ok = false,
        SnapshotId = snap.Id,
        Error = new ErrorInfo { Code = "invalid_argument", Message = message, Retryable = false },
    };

    // A bare name matched more than one declaration. Report the candidates (with their
    // symbol_ids) so the caller can re-run precisely via --id or file:line:col.
    private Envelope<T> Ambiguous<T>(Snapshot snap, IReadOnlyList<SymbolEntry> cands)
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
            SnapshotId = snap.Id,
            Error = new ErrorInfo { Code = "ambiguous_symbol", Message = msg, Retryable = false },
        };
    }

    private Envelope<HoverItem> NotFoundHover(Snapshot snap) => new()
    {
        Ok = false,
        SnapshotId = snap.Id,
        Error = new ErrorInfo { Code = "symbol_not_found", Message = "No symbol at the given target.", Retryable = false },
    };

    private Envelope<T> SymbolNotFound<T>(Snapshot snap) => new()
    {
        Ok = false,
        SnapshotId = snap.Id,
        Error = new ErrorInfo { Code = "symbol_not_found", Message = "No symbol at the given target.", Retryable = false },
    };

    public void Dispose() => workspace?.Dispose();
}
