using System.Data;
using Surf2.Models;

namespace Surf2.Storage.Relational.State;

public sealed record PhysicalResourceChoice(string ScopeName, ScopedResource Resource);

public sealed partial class RelationalStateStore
{
    /// <summary>Page reusable physical references without reading scope graphs, definitions, or assets.</summary>
    public Task<StatePage<PhysicalResourceChoice>> ListPhysicalResourceChoicesAsync(ResourceKind kind, long targetScopeKey,
        int pageSize = 100, StateCursor? after = null, CancellationToken ct = default)
    {
        if (kind is not (ResourceKind.Folder or ResourceKind.File)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (targetScopeKey <= 0) throw new ArgumentOutOfRangeException(nameof(targetScopeKey));
        return ReadyAsync(async (connection, transaction) =>
        {
            var generation = await CatalogueGenerationAsync(connection, transaction, 0, pageSize, after, ct);
            // Kind and target are validated numeric values, never user-supplied SQL text.
            string sql = $$"""
WITH Physical AS (
    SELECT r.ScopeResourceKey,r.ScopeKey,
           CASE WHEN trimmed.Path LIKE N'[A-Za-z]:' AND LEN(slashed.Path)>2
                THEN trimmed.Path+N'\' ELSE trimmed.Path END COLLATE Latin1_General_100_CI_AS AS PathIdentity
    FROM surf.ScopeResource r
    JOIN surf.Scope s ON s.ScopeKey=r.ScopeKey
    CROSS APPLY (VALUES(REPLACE(r.Path,N'/',N'\'))) slashed(Path)
    CROSS APPLY (VALUES(LEFT(slashed.Path,LEN(slashed.Path+N'x')-
        PATINDEX(N'%[^\]%',REVERSE(slashed.Path)+N'x')))) trimmed(Path)
    WHERE s.ProfileKey=1 AND r.Kind={{(int)kind}}
), Choices AS (
    SELECT candidate.ScopeResourceKey,
           ROW_NUMBER() OVER(PARTITION BY candidate.PathIdentity ORDER BY candidate.ScopeResourceKey) AS DuplicateOrdinal
    FROM Physical candidate
    WHERE candidate.ScopeKey<>{{targetScopeKey}}
      AND NOT EXISTS (SELECT 1 FROM Physical present WHERE present.ScopeKey={{targetScopeKey}}
          AND present.PathIdentity=candidate.PathIdentity)
)
SELECT TOP (@Take) r.ScopeResourceKey, CONVERT(bigint,0), s.Version, s.PublicationId,
       CONVERT(bigint,DATALENGTH(s.Name)),s.Name,
       CONVERT(bigint,DATALENGTH(r.Path)),r.Path,
       CONVERT(bigint,DATALENGTH(r.DisplayNameOverride)),r.DisplayNameOverride,
       CONVERT(bigint,DATALENGTH(d.Text)),d.Text,r.IncludeChildren
FROM Choices choice
JOIN surf.ScopeResource r ON r.ScopeResourceKey=choice.ScopeResourceKey
JOIN surf.Scope s ON s.ScopeKey=r.ScopeKey
JOIN surf.TextContent d ON d.ContentKey=r.DetailsOverrideContentKey
WHERE choice.DuplicateOrdinal=1 AND r.ScopeResourceKey>@Key
ORDER BY r.ScopeResourceKey;
""";
            return await PageAsync(connection, transaction, sql, pageSize, after, generation, async (reader, _, _, budget, cancellation) =>
            {
                string scopeName = (await StringAsync(reader, 4, budget, cancellation))!;
                string path = (await StringAsync(reader, 6, budget, cancellation))!;
                string alias = (await StringAsync(reader, 8, budget, cancellation))!;
                string details = (await StringAsync(reader, 10, budget, cancellation))!;
                return new PhysicalResourceChoice(scopeName, new ScopedResource
                {
                    Kind = kind, Path = path, DisplayNameOverride = alias, DetailsOverride = details,
                    IncludeChildren = reader.GetBoolean(12)
                });
            }, ct);
        }, ct);
    }
}
