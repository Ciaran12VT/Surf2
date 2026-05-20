namespace Surf2.Models;

public sealed class DiagramObjectMetadata
{
    public string Link { get; set; } = string.Empty;

    public string DocumentationXaml { get; set; } = string.Empty;

    public List<QueryItem> Queries { get; set; } = [];

    public DiagramObjectMetadata Clone()
    {
        return new DiagramObjectMetadata
        {
            Link = Link,
            DocumentationXaml = DocumentationXaml,
            Queries = Queries.Select(query => query.Clone()).ToList()
        };
    }
}

public sealed class QueryItem
{
    public string QueryId { get; set; } = Guid.NewGuid().ToString("N");

    public int QueryNumber { get; set; }

    public DateTimeOffset CreatedDateUtc { get; set; } = DateTimeOffset.UtcNow;

    public QueryStatus Status { get; set; } = QueryStatus.Active;

    public string QueryDescription { get; set; } = string.Empty;

    public QueryItem Clone()
    {
        return new QueryItem
        {
            QueryId = QueryId,
            QueryNumber = QueryNumber,
            CreatedDateUtc = CreatedDateUtc,
            Status = Status,
            QueryDescription = QueryDescription
        };
    }
}

public enum QueryStatus
{
    Active,
    Resolved,
    Irrelevant
}

public static class DiagramQueryState
{
    public static bool HasUnresolvedQueries(IEnumerable<QueryItem>? queries)
    {
        return queries?.Any(query => query.Status == QueryStatus.Active) == true;
    }

    public static bool HasUnresolvedMetadataQueries(DiagramObjectMetadata? metadata)
    {
        return HasUnresolvedQueries(metadata?.Queries);
    }

    public static bool HasUnresolvedMetadataQueries(DiagramDocument? diagram)
    {
        return diagram?.Objects.Any(diagramObject => HasUnresolvedMetadataQueries(diagramObject.Metadata)) == true;
    }

    public static bool HasUnresolvedWorkflowQueries(WorkflowItem? item)
    {
        return HasUnresolvedQueries(item?.Queries);
    }

    public static bool HasUnresolvedWorkflowQueries(WorkflowDocument? workflow)
    {
        return workflow?.Items.Any(HasUnresolvedWorkflowQueries) == true;
    }
}

public sealed class WorkflowDocument
{
    public string WorkflowId { get; set; } = Guid.NewGuid().ToString("N");

    public string WorkflowName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public bool AreMarkersVisible { get; set; }

    public List<WorkflowItem> Items { get; set; } = [];

    public WorkflowDocument Clone()
    {
        return new WorkflowDocument
        {
            WorkflowId = WorkflowId,
            WorkflowName = WorkflowName,
            CreatedAtUtc = CreatedAtUtc,
            UpdatedAtUtc = UpdatedAtUtc,
            AreMarkersVisible = AreMarkersVisible,
            Items = Items.Select(item => item.Clone()).ToList()
        };
    }
}

public sealed class WorkflowItem
{
    public string WorkflowItemId { get; set; } = Guid.NewGuid().ToString("N");

    public string MarkerDiagramObjectId { get; set; } = string.Empty;

    public int ItemNumber { get; set; }

    public string ItemDescription { get; set; } = string.Empty;

    public string ItemDocumentationXaml { get; set; } = string.Empty;

    public List<QueryItem> Queries { get; set; } = [];

    public WorkflowItem Clone()
    {
        return new WorkflowItem
        {
            WorkflowItemId = WorkflowItemId,
            MarkerDiagramObjectId = MarkerDiagramObjectId,
            ItemNumber = ItemNumber,
            ItemDescription = ItemDescription,
            ItemDocumentationXaml = ItemDocumentationXaml,
            Queries = Queries.Select(query => query.Clone()).ToList()
        };
    }
}
