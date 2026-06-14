using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

        await ProjectCatalogAsync(ct);
        loadMs = sw.ElapsedMilliseconds;
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

    public Envelope<StatusInfo> Status()
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
            var e = catalog.ByMoniker(symbolId!);
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
            return HoverById(symbolId!);

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
