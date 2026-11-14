using System.Data;
using System.IO;
using Surf2.Models;

namespace Surf2.Storage.Relational.State;

public sealed partial class RelationalStateStore
{
    /// <summary>Page credential-free registered source descriptors; never hydrate other scope graphs.</summary>
    public Task<StatePage<PhysicalResourceChoice>> ListSourceResourceChoicesAsync(SourceProvider provider, long targetScopeKey,
        int pageSize = 100, StateCursor? after = null, CancellationToken ct = default)
    {
        if (provider is not (SourceProvider.Git or SourceProvider.AzureBlob or SourceProvider.LiveSql))
            throw new ArgumentOutOfRangeException(nameof(provider));
        if (targetScopeKey <= 0) throw new ArgumentOutOfRangeException(nameof(targetScopeKey));
        return ReadyAsync(async (connection, transaction) =>
        {
            var generation = await CatalogueGenerationAsync(connection, transaction, 0, pageSize, after, ct);
            string sql = $$"""
                WITH Choices AS (
                    SELECT MIN(r.ScopeResourceKey) AS ScopeResourceKey FROM surf.ScopeResource r
                    JOIN surf.Scope s ON s.ScopeKey=r.ScopeKey
                    JOIN surf.SourceDefinition sd ON sd.SourceId=r.SourceId
                    WHERE s.ProfileKey=1 AND r.ScopeKey<>{{targetScopeKey}} AND sd.Provider={{(int)provider}}
                      AND NOT EXISTS(SELECT 1 FROM surf.ScopeResource present
                        WHERE present.ScopeKey={{targetScopeKey}} AND present.SourceId=r.SourceId)
                    GROUP BY r.SourceId
                )
                SELECT TOP (@Take) r.ScopeResourceKey,CONVERT(bigint,0),s.Version,s.PublicationId,
                    CONVERT(bigint,DATALENGTH(s.Name)),s.Name,
                    CONVERT(bigint,DATALENGTH(r.Path)),r.Path,
                    CONVERT(bigint,DATALENGTH(r.DisplayNameOverride)),r.DisplayNameOverride,
                    CONVERT(bigint,DATALENGTH(d.Text)),d.Text,r.IncludeChildren,
                    sd.SourceId,sd.Provider,sd.ConfigurationRevision,LEFT(sd.Endpoint,32769),LEFT(sd.Container,32769),
                    LEFT(sd.Prefix,32769),LEFT(sd.Ref,32769),LEFT(sd.Subfolder,32769),LEFT(sd.DatabaseName,32769),LEFT(sd.AuthenticationKind,32769)
                FROM Choices choice JOIN surf.ScopeResource r ON r.ScopeResourceKey=choice.ScopeResourceKey
                JOIN surf.Scope s ON s.ScopeKey=r.ScopeKey JOIN surf.SourceDefinition sd ON sd.SourceId=r.SourceId
                JOIN surf.TextContent d ON d.ContentKey=r.DetailsOverrideContentKey
                WHERE r.ScopeResourceKey>@Key ORDER BY r.ScopeResourceKey;
                """;
            return await PageAsync(connection, transaction, sql, pageSize, after, generation, async (reader, _, _, budget, cancellation) =>
            {
                string scopeName = (await StringAsync(reader, 4, budget, cancellation))!;
                string path = (await StringAsync(reader, 6, budget, cancellation))!;
                string alias = (await StringAsync(reader, 8, budget, cancellation))!;
                string details = (await StringAsync(reader, 10, budget, cancellation))!;
                bool children = reader.GetBoolean(12);
                var source = new ResourceSourceDefinition { SourceId = reader.GetGuid(13), Provider = (SourceProvider)reader.GetInt32(14),
                    ConfigurationRevision = reader.GetInt64(15) };
                string Read(int ordinal)
                {
                    string value = reader.GetString(ordinal);
                    if (value.Length > 32768) throw new InvalidDataException("The source descriptor exceeds its field limit.");
                    budget.Text(value.Length * 2L); return value;
                }
                source.Endpoint = Read(16); source.Container = Read(17); source.Prefix = Read(18); source.Ref = Read(19);
                source.Subfolder = Read(20); source.DatabaseName = Read(21); source.AuthenticationKind = Read(22);
                return new PhysicalResourceChoice(scopeName, new ScopedResource
                {
                    Kind = provider == SourceProvider.LiveSql ? ResourceKind.LiveDatabase : ResourceKind.Folder,
                    Path = path, DisplayNameOverride = alias, DetailsOverride = details, IncludeChildren = children, Source = source
                });
            }, ct);
        }, ct);
    }
}
