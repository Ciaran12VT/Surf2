using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Surf2.Models;
using Surf2.Services;

namespace Surf2.Storage.Relational.Index;

// Parent StorageSuite owns the opt-in fixture lifecycle and builds. These hooks never connect implicitly.
public static class IndexContractChecks
{
    public static IReadOnlyList<string> Run()
    {
        var passed = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Index contract failed: " + name);
            passed.Add(name);
        }
        var literal = new IndexTextMatcher("%[_]", false);
        Check(literal.IsMatch("prefix%[_]suffix") && !literal.IsMatch("prefixanything"), "Literal SQL wildcard characters remain literal");
        foreach (var pair in new[] { ("i", "I"), ("i", "\u0130"), ("\u03c3", "\u03a3"), ("\ud801\udc00", "\ud801\udc28"), ("e", "\u00e9") })
            Check(new IndexTextMatcher(pair.Item1, false).IsMatch(pair.Item2) == pair.Item2.Contains(pair.Item1, StringComparison.OrdinalIgnoreCase),
                "OrdinalIgnoreCase Unicode pair " + passed.Count);
        Check(new IndexTextMatcher("a\\r?\\nb", true).IsMatch("a\r\nb"), "Regex matches across lines");
        Check(new IndexTextMatcher("(word) \\1", true).IsMatch("WORD word"), "Regex backreferences retain .NET semantics");
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Check(new IndexTextMatcher("i", true).IsMatch("I"), "Regex remains culture invariant");
        }
        finally { CultureInfo.CurrentCulture = previous; }
        bool invalid = false;
        try { _ = new IndexTextMatcher("[", true); } catch (ArgumentException) { invalid = true; }
        Check(invalid, "Invalid regex is an error, not an empty successful search");
        bool timeout = false;
        try { new IndexTextMatcher("(a+)+$", true, TimeSpan.FromMilliseconds(1)).IsMatch(new string('a', 20000) + "!"); }
        catch (RegexMatchTimeoutException) { timeout = true; }
        Check(timeout, "Pathological regex has a finite timeout");
        Check(ParseSql("CREATE TABLE surf.CheckFixture(LineNumber int, EndLineNumber int CHECK(EndLineNumber>=LineNumber));").Count > 0,
            "Schema check catches SQL Server 8141 cross-column column CHECK");
        Check(ParseSql("CREATE TABLE surf.CheckFixture(LineNumber int, EndLineNumber int, CHECK(EndLineNumber>=LineNumber));").Count == 0,
            "Cross-column table CHECK is accepted");

        var legacy = new ScopeReferenceIndex();
        ReferenceEntity[] definitions = [
            new() { Name="Call",QualifiedName="A.Call",Kind=ReferenceEntityKind.Method,FilePath="a.cs",ParameterCount=2,MinimumArgumentCount=1,MaximumArgumentCount=2 },
            new() { Name="Call",QualifiedName="B.Call",Kind=ReferenceEntityKind.Method,FilePath="b.cs",ParameterCount=1,MinimumArgumentCount=1,MaximumArgumentCount=1 },
            new() { Name="Call",QualifiedName="Call.cs",Kind=ReferenceEntityKind.File,FilePath="Call.cs" },
            new() { Name="Thing",QualifiedName="dbo.Thing",Kind=ReferenceEntityKind.View,FilePath="db://fixture/object/View/Thing.sql" }
        ];
        foreach (ReferenceEntity entity in definitions) legacy.Add(entity);
        SymbolSummary[] metadata = definitions.Select((e, i) => new SymbolSummary(i + 1, i + 1, i + 1,
            SymbolInput.FromReference(e), IndexFreshness.Indexed)).ToArray();
        foreach (var request in new[] { ("[Call]", (int?)null), ("[A].[Call]", (int?)1), ("unknown.Call", (int?)1), ("Call", (int?)2), ("`dbo.Thing`", (int?)null) })
        {
            string[] expected = legacy.Resolve(request.Item1, request.Item2).Select(e => e.QualifiedName + "|" + e.FilePath).ToArray();
            string[] actual = ReferenceMetadata.Resolve(metadata, request.Item1, request.Item2)
                .Select(s => s.Definition.QualifiedName + "|" + s.Definition.Locator).ToArray();
            Check(expected.SequenceEqual(actual), "Legacy normalization/fallback/overload ranking " + request.Item1);
        }
        var context = new IndexRequestContext(Guid.NewGuid(), 1, 0, new string('0',16), new string('0',16), new string('0',16),
            false, [], [], false);
        var shared = new SharedHighlightMetadata(context,
            [new("Call", [ReferenceEntityKind.Method]), new("call", [ReferenceEntityKind.Function])], "style-v1");
        Check(shared.Names.Count == 1 && shared.Names["CALL"].Length == 2, "Immutable highlight lookup combines name/kind metadata");
        foreach (Type type in new[] { typeof(SymbolInput), typeof(SymbolSummary), typeof(IndexedDocumentSummary), typeof(SearchHit), typeof(HighlightName) })
            Check(type.GetProperties().All(p => p.Name is not ("Text" or "FullText" or "Rows" or "SyntaxTree" or "Library") &&
                p.PropertyType != typeof(ReferenceEntity)), type.Name + " is payload/GUI free");
        var coverage = new SearchCoverage(1,1,1,0,0,0,true,false,true,false);
        Check(coverage.FullyCurrentDocumentSet && !coverage.IncludesExplorerContainers, "Completion is explicitly document-set coverage");
        Check(!(coverage with { Completed=false }).FullyCurrentDocumentSet && !(coverage with { Stale=1 }).FullyCurrentDocumentSet &&
            !(coverage with { Incomplete=1 }).FullyCurrentDocumentSet && !(coverage with { Delegated=1 }).FullyCurrentDocumentSet &&
            !(coverage with { GenerationChanged=true }).FullyCurrentDocumentSet && !(coverage with { IsSubset=true }).FullyCurrentDocumentSet &&
            !(coverage with { DiscoveryReconciled=false }).FullyCurrentDocumentSet, "Incomplete/stale/subset/delegated generations are not fully current");
        return passed;
    }

    public static async Task<IReadOnlyList<string>> RunAsync(CancellationToken token = default)
    {
        var passed = Run().ToList();
        using var input = new StringReader("a\r\nb ");
        if (await IndexTextMatcher.ReadBoundedAsync(input, 6, token) != "a\r\nb ") throw new InvalidOperationException("Index text changed.");
        passed.Add("Bounded streaming retains exact text/line endings/trailing space");
        bool oversized = false;
        using var huge = new StringReader("0123456789");
        try { await IndexTextMatcher.ReadBoundedAsync(huge, 5, token); } catch (IndexDocumentTooLargeException) { oversized = true; }
        if (!oversized) throw new InvalidOperationException("Oversized source was silently truncated.");
        passed.Add("Oversized source is an explicit incomplete source");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool cancellation = false;
        using var small = new StringReader("x");
        try { await IndexTextMatcher.ReadBoundedAsync(small, 10, cancelled.Token); } catch (OperationCanceledException) { cancellation = true; }
        if (!cancellation) throw new InvalidOperationException("Streaming cancellation was ignored.");
        passed.Add("Streaming forwards cancellation");
        return passed;
    }

    // ScriptDom normally accepts a cross-column COLUMN CHECK that SQL Server rejects with 8141.
    // Walk that topology too, so the original LocalDB installation failure stays covered.
    public static IReadOnlyList<string> ParseSql(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var input = new StringReader(sql);
        TSqlFragment fragment = parser.Parse(input, out var errors);
        var issues = errors.Select(e => $"{e.Line}:{e.Column}: {e.Message}").ToList();
        fragment.Accept(new ColumnCheckVisitor(issues));
        return issues;
    }
    private sealed class ColumnCheckVisitor(List<string> issues) : TSqlFragmentVisitor
    {
        public override void ExplicitVisit(ColumnDefinition node)
        {
            foreach (CheckConstraintDefinition check in node.Constraints.OfType<CheckConstraintDefinition>())
            {
                var references = new ColumnReferenceVisitor();
                check.CheckCondition.Accept(references);
                if (references.Names.Any(n => !string.Equals(n, node.ColumnIdentifier.Value, StringComparison.OrdinalIgnoreCase)))
                    issues.Add($"{check.StartLine}: column CHECK on {node.ColumnIdentifier.Value} references another column; use a table constraint.");
            }
            base.ExplicitVisit(node);
        }
    }
    private sealed class ColumnReferenceVisitor : TSqlFragmentVisitor
    {
        public List<string> Names { get; } = [];
        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            if (node.MultiPartIdentifier?.Identifiers.LastOrDefault() is { } identifier) Names.Add(identifier.Value);
        }
    }

    // Opt-in: fixture MUST be an owned disposable database, with this published current source and scope resource.
    // Only the index slice is mutated. No bootstrap, source files, domain revisions or Ready marker are written.
    public static async Task<IReadOnlyList<string>> RunStorageAsync(RelationalSession fixtureSession,
        IndexStorageFixture fixture, CancellationToken token = default)
    {
        await fixtureSession.RequireReadyAsync(token);
        var passed = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Index SQL contract failed: " + name);
            passed.Add(name);
        }
        var store = new RelationalIndexStore(fixtureSession);
        DocumentHandle document = await store.RegisterAsync(new(new SnapshotDocumentOwner(fixture.SnapshotKey, fixture.ResourceKey, false),
            "Index fixture", "SQL Server"), token);
        await store.SetMembershipAsync(document.DocumentKey,
            [new(fixture.ScopeResourceKey,"Index fixture",fixture.CanonicalLocator,"fixture-document","fixture-parent",0)],
            [new(fixture.ScopeResourceKey,1,fixture.ReadableLocator),new(fixture.ScopeResourceKey,2,fixture.CanonicalLocator)], token);
        var context = await store.CaptureContextAsync(fixture.ScopeKey, documentKeys:[document.DocumentKey], token:token);
        IndexDocumentPage initial = await store.ReadDocumentsPageAsync(context, token:token);
        Check(initial.Items.Single().RevisionKey == null && initial.Items.Single().Freshness == IndexFreshness.Unindexed,
            "Schema/catalogue alone is not a published generation");
        PreparedIndexSource source = await store.PrepareDefinitionAsync(fixture.SourceRevisionKey, fixture.CanonicalLocator,"Fixture",token:token);
        IndexWorkLease lease = await store.BeginWorkAsync(document,Guid.NewGuid(),source.Fingerprint,new("catalogue-v1","raw-v1","fixture-v1"),token);
        var publication = new IndexPublication(lease,source.Text,source.Language,source.Symbols,source.SourceRevisionKey);
        long revision = await store.PublishAsync(publication,token);
        Check(await store.PublishAsync(publication,token) == revision && await store.FindPublishedRevisionAsync(lease.PublicationId,token) == revision,
            "One-source atomic publication has idempotent commit recovery");
        Check(await store.ReadRevisionTextAsync(document.DocumentKey,revision,token:token) == source.Text, "Exact requested revision opens independently");
        Check(!await store.IsContextCurrentAsync(context,token), "Publication invalidates captured generation");
        context = await store.CaptureContextAsync(fixture.ScopeKey,documentKeys:[document.DocumentKey],token:token);
        IndexPage<ResolvedIndexLocator> locators = await store.ResolveLocatorPageAsync(context,fixture.CanonicalLocator,token:token);
        Check(locators.Items.Single().DocumentKey == document.DocumentKey, "Canonical locator resolves through explicit scope membership");
        var unloaded = await store.CaptureContextAsync(fixture.ScopeKey,[fixture.ScopeResourceKey],[document.DocumentKey],token);
        Check((await store.ReadDocumentsPageAsync(unloaded,token:token)).Items.Length == 0, "Workspace unload filters membership, not global document identity");
        if (source.Symbols.Length > 0)
        {
            var names = await store.LookupNamesPageAsync(context,[source.Symbols[0].Name],token:token);
            Check(names.Items.Any(s => s.RevisionKey == revision), "Batched scoped name lookup carries revision, not fulltext");
        }
        bool complete = false;
        await foreach (IndexSearchBatch batch in store.SearchAsync(new(context,"",false,IndexSearchTarget.Content),token))
        {
            if (batch.Hits.Length > 0) Check(batch.Hits.All(h => h.DocumentKey == document.DocumentKey), "Search hits contain only pinned in-scope identities");
            complete |= batch.Coverage.Completed;
        }
        Check(complete, "Document scan completion is separate from scope freshness/subset coverage");
        IndexWorkLease failed = await store.BeginWorkAsync(await store.GetHandleAsync(document.DocumentKey,token),Guid.NewGuid(),source.Fingerprint,lease.Policy,token);
        await store.FailWorkAsync(failed,"ParserFailed",token:token);
        context = await store.CaptureContextAsync(fixture.ScopeKey,documentKeys:[document.DocumentKey],token:token);
        IndexedDocumentSummary stale = (await store.ReadDocumentsPageAsync(context,token:token)).Items.Single();
        Check(stale.RevisionKey == revision && stale.Freshness == IndexFreshness.Failed &&
            await store.ReadRevisionTextAsync(document.DocumentKey,revision,token:token) == source.Text,
            "Failed refresh retains last successful text and symbols as non-current, not empty success");
        return passed;
    }
}

public sealed record IndexStorageFixture(long ScopeKey,long ScopeResourceKey,long SnapshotKey,long ResourceKey,
    long SourceRevisionKey,string CanonicalLocator,string ReadableLocator);
