using Surf2.Models;

namespace Surf2.Services;

public sealed class ScopeReferenceIndex
{
    private readonly Dictionary<string, List<ReferenceEntity>> _entitiesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ReferenceEntity> _entities = [];

    public static ScopeReferenceIndex Empty { get; } = new();

    public int EntityCount => _entities.Count;

    public int IndexedFileCount { get; private set; }

    public IReadOnlyList<ReferenceEntity> Entities => _entities;

    public IReadOnlyCollection<string> ReferenceHighlightNames => _entities
        .Where(entity => entity.Kind != ReferenceEntityKind.File)
        .Select(entity => entity.Name)
        .Where(name => name.Length > 1)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public IReadOnlyList<ReferenceEntity> HighlightableEntities => _entities
        .Where(entity => entity.Kind != ReferenceEntityKind.File && entity.Name.Length > 1)
        .ToList();

    public void AddFile()
    {
        IndexedFileCount++;
    }

    public void Add(ReferenceEntity entity)
    {
        if (string.IsNullOrWhiteSpace(entity.Name) || string.IsNullOrWhiteSpace(entity.FilePath))
        {
            return;
        }

        Add(entity, entity.Name);

        if (!string.IsNullOrWhiteSpace(entity.QualifiedName) &&
            !string.Equals(entity.QualifiedName, entity.Name, StringComparison.OrdinalIgnoreCase))
        {
            Add(entity, entity.QualifiedName);
        }
    }

    public IReadOnlyList<ReferenceEntity> Resolve(string token, int? argumentCount = null)
    {
        string key = NormalizeKey(token);
        if (string.IsNullOrWhiteSpace(key))
        {
            return [];
        }

        if (!_entitiesByName.TryGetValue(key, out List<ReferenceEntity>? matches) && key.Contains('.', StringComparison.Ordinal))
        {
            key = key.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
            _entitiesByName.TryGetValue(key, out matches);
        }

        if (matches == null)
        {
            return [];
        }

        List<ReferenceEntity> distinctMatches = matches
            .DistinctBy(entity => $"{entity.Kind}|{entity.FilePath}|{entity.LineNumber}|{entity.ColumnNumber}|{entity.QualifiedName}")
            .ToList();

        if (distinctMatches.Any(entity => entity.Kind != ReferenceEntityKind.File))
        {
            distinctMatches = distinctMatches
                .Where(entity => entity.Kind != ReferenceEntityKind.File)
                .ToList();
        }

        if (argumentCount.HasValue)
        {
            List<ReferenceEntity> overloadMatches = distinctMatches
                .Where(entity => IsCallable(entity) && MatchesArgumentCount(entity, argumentCount.Value))
                .ToList();

            if (overloadMatches.Count > 0)
            {
                int bestScore = overloadMatches.Min(entity => GetArgumentMatchScore(entity, argumentCount.Value));
                distinctMatches = overloadMatches
                    .Where(entity => GetArgumentMatchScore(entity, argumentCount.Value) == bestScore)
                    .ToList();
            }
        }

        return distinctMatches
            .OrderBy(GetNavigationRank)
            .ThenBy(entity => entity.QualifiedName)
            .ThenBy(entity => entity.FilePath)
            .ToList();
    }

    private void Add(ReferenceEntity entity, string key)
    {
        key = NormalizeKey(key);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (!_entitiesByName.TryGetValue(key, out List<ReferenceEntity>? entities))
        {
            entities = [];
            _entitiesByName[key] = entities;
        }

        entities.Add(entity);

        if (!_entities.Contains(entity))
        {
            _entities.Add(entity);
        }
    }

    private static string NormalizeKey(string token)
    {
        return token
            .Trim()
            .Trim('[', ']', '`', '"', '\'')
            .Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal);
    }

    private static int GetNavigationRank(ReferenceEntity entity)
    {
        return entity.Kind switch
        {
            ReferenceEntityKind.Method => 0,
            ReferenceEntityKind.Function => 1,
            ReferenceEntityKind.StoredProcedure => 2,
            ReferenceEntityKind.View => 3,
            ReferenceEntityKind.Trigger => 4,
            ReferenceEntityKind.Class => 5,
            ReferenceEntityKind.Interface => 6,
            ReferenceEntityKind.Struct => 7,
            ReferenceEntityKind.Enum => 8,
            ReferenceEntityKind.Delegate => 9,
            ReferenceEntityKind.Table => 10,
            ReferenceEntityKind.Field => 11,
            ReferenceEntityKind.File => 12,
            _ => 99
        };
    }

    private static bool IsCallable(ReferenceEntity entity)
    {
        return entity.Kind is ReferenceEntityKind.Method or ReferenceEntityKind.Function or ReferenceEntityKind.StoredProcedure;
    }

    private static bool MatchesArgumentCount(ReferenceEntity entity, int argumentCount)
    {
        if (!entity.MinimumArgumentCount.HasValue || !entity.MaximumArgumentCount.HasValue)
        {
            return false;
        }

        return argumentCount >= entity.MinimumArgumentCount.Value && argumentCount <= entity.MaximumArgumentCount.Value;
    }

    private static int GetArgumentMatchScore(ReferenceEntity entity, int argumentCount)
    {
        int minimumArgumentCount = entity.MinimumArgumentCount ?? argumentCount;
        int maximumArgumentCount = entity.MaximumArgumentCount ?? argumentCount;
        int parameterCount = entity.ParameterCount ?? maximumArgumentCount;

        if (parameterCount == argumentCount)
        {
            return 0;
        }

        int optionalSlack = maximumArgumentCount == int.MaxValue
            ? 100
            : Math.Max(0, maximumArgumentCount - argumentCount);
        int requiredDistance = Math.Abs(argumentCount - minimumArgumentCount);

        return (optionalSlack * 10) + requiredDistance;
    }
}
