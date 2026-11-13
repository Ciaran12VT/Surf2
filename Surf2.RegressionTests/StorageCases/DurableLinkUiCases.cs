using System.Collections.Immutable;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.SqlClient;
using Surf2.Controls;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalDocuments;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage;
using Surf2.Storage.Relational.Access;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;

public static partial class StorageRegressionSuite
{
    private static async Task RuntimeWpfCheckDurableLinkUiAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        string connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_StateAccess_DurableLinkUi_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        var runtime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(connection));
        var main = new Surf2.MainWindow(runtime.Options);
        RuntimeWpfSetField(main, "_relational", runtime);
        var snapshot = new SnapshotSummary(11, Guid.NewGuid(), "legacy-fixture", "Fixture database", "Fixture database",
            DateTimeOffset.UtcNow, 0, null, new byte[8]);
        var resource = new SnapshotResourceSummary(12, Guid.NewGuid(), snapshot.SnapshotKey,
            DatabaseVersionedResourceKind.StoredProcedure, "dbo", "Compute", "StoredProcedure|dbo.Compute", 13, 0, new byte[8]);
        var scope = new ExplorerScope(new IndexRequestContext(runtime.Session.Epoch, 1, 1, "scope", "snapshot", "diagram", true, [], [], false),
            "scope", "Scope", [new(2, 0, "resource", ResourceKind.DatabaseSnapshot, snapshot.SnapshotId, "", true, true,
                snapshot, null, null, null, false)], [], "en-GB",
            ImmutableDictionary<long, ImmutableArray<ExplorerDatabaseItem>>.Empty.Add(snapshot.SnapshotKey,
                [new(resource, SqlDatabaseObjectKind.StoredProcedure)]));
        RuntimeWpfSetField(main, "_relationalExplorerScope", scope);
        string transient = RelationalDocumentService.IdentityPath(runtime.Session.Epoch, resource, ExplorerCategory.Procedures, false);
        string persistent = DurableDocumentLink.Create(snapshot.PublicId, resource.PublicId, DatabaseVersionedResourceKind.StoredProcedure);
        string readable = ExplorerCompatibility.DatabaseLocator(snapshot, ExplorerCategory.Procedures, resource.SchemaName, resource.ObjectName);
        var address = new RelationalDocumentAddress(new SnapshotDocumentIdentity(runtime.Session.Epoch, snapshot.SnapshotKey, resource.ResourceKey, false),
            transient, "document.sql", "dbo.Compute.sql", null, SnapshotResourceLocator.From(runtime.Session.Epoch, resource),
            null, snapshot, readable, persistent);
        try
        {
            using var trace = new StateAccessSqlTrace(connection);
            RuntimeWpfAssert((string)RuntimeWpfInvoke(main, "GetRelationalPersistentDocumentPath", transient)! == persistent, check,
                "Unopened Explorer and tab paths become durable using loaded catalogue identities");
            RuntimeWpfAssert((string)RuntimeWpfInvoke(main, "GetReadableDatabaseMetadataLink", persistent)! == readable, check,
                "Unopened durable diagram references display readable database and object names from the catalogue");
            string expired = RelationalDocumentService.IdentityPath(Guid.NewGuid(), resource, ExplorerCategory.Procedures, false);
            RuntimeWpfAssert((string)RuntimeWpfInvoke(main, "GetRelationalPersistentDocumentPath", expired)! == expired, check,
                "The UI never guesses a resource for an expired session address");
            RuntimeWpfInvoke(main, "RememberRelationalDocumentPresentation", address);
            string suffix = ":27:[\"Id\"=\"42\"]";
            RuntimeWpfAssert((string)RuntimeWpfInvoke(main, "NormalizeDatabaseMetadataLink", transient + suffix)! == persistent + suffix, check,
                "Metadata normalization preserves line and table filter syntax while replacing the transient address");

            var code = new FloatingCodeWindow(new OpenDocumentState { FilePath = transient, DisplayName = "dbo.Compute.sql" }, "dbo.Compute", null);
            RuntimeWpfInvoke(main, "SetRelationalDocumentFilenameSeed", code, address, scope, 0L);
            object?[] lineArguments = [null, 0];
            bool hasLine = (bool)typeof(FloatingCodeWindow).GetMethod("TryCreateCurrentLineAddress", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(code, lineArguments)!;
            RuntimeWpfAssert(hasLine && Equals(lineArguments[0], persistent + ":1") && code.State.FilePath == transient, check,
                "Copy Line Address uses durable identity while the open window retains its session identity");
            RuntimeWpfAssert(code.TryCreateCurrentReferenceDiagramObjectInfo(out var info) && info.LineAddress == persistent + ":1", check,
                "Copy Diagram Object obtains a durable source line address");
            var clipboardType = typeof(Surf2.MainWindow).GetNestedType("ReferenceDiagramObjectClipboard", BindingFlags.NonPublic)!;
            var clipboard = Activator.CreateInstance(clipboardType, info.ReferenceText, info.LineAddress)!;
            var pasted = (DiagramObjectSnapshot)RuntimeWpfInvoke(main, "CreateReferenceDiagramObjectSnapshot", clipboard)!;
            RuntimeWpfAssert(pasted.Metadata.Link == persistent + ":1", check,
                "Pasting the copied reference keeps its durable link in diagram metadata");
            code.ApplySearchReference(new CodeSearchReference(transient, "Compute", true, false, false));
            RuntimeWpfAssert(code.GetSearchReference()?.DocumentPath == persistent, check,
                "Copied search references use durable document identities");
            string search = CodeSearchReferenceService.Encode(new(transient, "Compute", true, false, false));
            string rewrittenSearch = (string)RuntimeWpfInvoke(main, "NormalizeDatabaseMetadataLink", search)!;
            RuntimeWpfAssert(CodeSearchReferenceService.TryDecode(rewrittenSearch, out var decoded) && decoded.DocumentPath == persistent &&
                decoded.SearchText == "Compute" && decoded.MatchCase, check,
                "Metadata search-reference normalization retains the query and search options");

            var shape = new DiagramShapeControl(DiagramShapeKind.Rectangle, "#000000", "#FFFFFF");
            shape.ApplyMetadata(new DiagramObjectMetadata { Link = persistent + ":1" });
            RuntimeWpfSetField(main, "_metadataEditorTarget", shape);
            RuntimeWpfSetField(main, "_metadataEditorOriginalLink", persistent + ":1");
            RuntimeWpfSetField(main, "_metadataEditorDisplayedLink", readable + ":1");
            RuntimeWpfNamed<TextBox>(main, "MetadataLinkTextBox").Text = readable + ":1";
            RuntimeWpfNamed<FrameworkElement>(main, "MetadataEditorPanel").Visibility = Visibility.Visible;
            RuntimeWpfAssert((string)RuntimeWpfInvoke(main, "GetMetadataEditorLink")! == persistent + ":1", check,
                "The metadata Open button targets the durable original behind its readable label");
            RuntimeWpfInvoke(main, "CommitMetadataEditorChanges");
            RuntimeWpfAssert(shape.Metadata.Link == persistent + ":1", check,
                "Saving documentation preserves an unchanged durable link behind its readable presentation");
            RuntimeWpfSetField(main, "_relationalExplorerScope", null!);
            RuntimeWpfAssert(code.PersistentDocumentPath == persistent &&
                (string)RuntimeWpfInvoke(main, "GetRelationalPersistentDocumentPath", transient)! == persistent, check,
                "Open windows and tabs keep their durable copy target after the original scope changes");
            RuntimeWpfAssert(trace.Commands.Count == 0, check,
                "Preparing copy links and readable metadata uses no database commands");
        }
        finally
        {
            await RuntimeWpfInvokeTaskAsync(main, "DrainRelationalStateQueriesAsync");
            RuntimeWpfField<TaskCompletionSource<bool>>(main, "_startupReadyCompletion").TrySetResult(true);
            RuntimeWpfSetField(main, "_shutdownSaveCompleted", true);
            main.Close();
        }
        await fixture.VerifySourceUnchangedAsync(check);
        await RuntimeWpfCheckDurableLinkStartupAsync(check);
    }
}
