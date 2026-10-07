using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Surf2.Models;

namespace Surf2.Storage.Relational.Migration;

public sealed record LegacyInventory(string DocumentKey, long Values, long DataRows, int? SourceSchemaVersion);

public sealed class LegacySchemaInspector
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly NullabilityInfoContext _nullability = new();
    private long _values;
    private long _dataRows;
    private int? _sourceSchemaVersion;
    private Action<long>? _positionProgress;

    public async Task<LegacyInventory> InspectAsync(string key, StreamingJsonCursor cursor,
        CancellationToken cancellationToken = default, Action<long>? positionProgress = null)
    {
        if (!LegacySourceStage.Documents.TryGetValue(key, out var definition))
        {
            throw new InvalidDataException("An unsupported legacy document was selected.");
        }

        _values = 0;
        _dataRows = 0;
        _sourceSchemaVersion = null;
        _positionProgress = positionProgress;
        if (!await cursor.MoveNextAsync(cancellationToken))
        {
            throw new InvalidDataException("A legacy document is empty.");
        }
        cursor.Require(JsonTokenType.StartObject);

        await InspectValueAsync(definition.Type, cursor, cancellationToken, root: true);
        if (await cursor.MoveNextAsync(cancellationToken))
        {
            throw new InvalidDataException("A legacy document contains extra top-level values.");
        }

        if (key == "scope-library" && _sourceSchemaVersion is not (null or 1) ||
            key == "database-snapshots" && _sourceSchemaVersion is not (null or 1 or 2))
        {
            throw new InvalidDataException("The legacy library schema version is not supported.");
        }

        return new(key, _values, _dataRows, _sourceSchemaVersion);
    }

    private async Task InspectValueAsync(Type type, StreamingJsonCursor cursor, CancellationToken ct, bool root = false)
    {
        ct.ThrowIfCancellationRequested();
        _values++;
        if ((_values & 1023) == 0) _positionProgress?.Invoke(cursor.TokenOffset);
        Type effective = Nullable.GetUnderlyingType(type) ?? type;
        if (effective == typeof(JsonElement))
        {
            // Dataset rows deliberately retain unknown source columns and duplicate property names.
            JsonElement row = await cursor.ReadValueAsync(ct);
            if (System.Text.Encoding.UTF8.GetByteCount(row.GetRawText()) > Capture.CaptureLimits.MaximumRowUtf8Bytes)
            {
                throw new InvalidDataException("A captured row exceeds the supported conversion limit.");
            }

            _dataRows++;
            return;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            Type[] arguments = type.GetGenericArguments();
            await cursor.ReadObjectAsync(async (name, value, cancellation) =>
            {
                if (arguments[0] != typeof(int) || !int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    throw new InvalidDataException("A persisted dictionary key is unsupported.");
                }

                await InspectValueAsync(arguments[1], value, cancellation);
            }, ct);
            return;
        }

        if (type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type))
        {
            Type itemType = type.GetGenericArguments().Single();
            await cursor.ReadArrayAsync(async (_, item, cancellation) =>
                await InspectValueAsync(itemType, item, cancellation), ct);
            return;
        }

        if (effective == typeof(string) || effective.IsPrimitive || effective.IsEnum ||
            effective == typeof(DateTimeOffset) || effective == typeof(DateTime) || effective == typeof(decimal))
        {
            JsonElement scalar = await cursor.ReadValueAsync(ct);
            object? value;
            try
            {
                value = JsonSerializer.Deserialize(scalar, type, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("A legacy scalar has an incompatible type.", ex);
            }

            if (effective.IsEnum && value != null && !Enum.IsDefined(effective, value) ||
                value is double number && !double.IsFinite(number) ||
                value is float single && !float.IsFinite(single))
            {
                throw new InvalidDataException("A legacy scalar contains an unsupported enum or non-finite number.");
            }

            return;
        }

        if (cursor.TokenType == JsonTokenType.Null)
        {
            return;
        }

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0)
            .ToDictionary(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name,
                StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await cursor.ReadObjectAsync(async (name, value, cancellation) =>
        {
            seen.Add(name);
            if (!properties.TryGetValue(name, out var property))
            {
                throw new InvalidDataException("The source contains an unmapped model property.");
            }

            if (property.SetMethod == null || property.GetCustomAttribute<JsonIgnoreAttribute>() != null)
            {
                await value.SkipValueAsync(cancellation);
                return;
            }

            if (value.TokenType == JsonTokenType.Null && _nullability.Create(property).WriteState == NullabilityState.NotNull)
            {
                throw new InvalidDataException("A required persisted value is null. It cannot be silently replaced during conversion.");
            }

            if (root && name == "SchemaVersion")
            {
                JsonElement version = await value.ReadValueAsync(cancellation);
                _sourceSchemaVersion = version.GetInt32();
            }
            else if (type == typeof(KeyboardShortcutSettings) && name == "Version")
            {
                JsonElement version = await value.ReadValueAsync(cancellation);
                if (version.GetInt32() > KeyboardShortcutSettings.CurrentVersion)
                {
                    throw new InvalidDataException("The keyboard preference version requires a newer reader.");
                }
            }
            else
            {
                await InspectValueAsync(property.PropertyType, value, cancellation);
            }
        }, ct);
        string[] required = type == typeof(DatabaseMetadataSnapshot) ? ["SnapshotId", "ImportedAtUtc"]
            : type == typeof(DatabaseSnapshotVersion) ? ["VersionId", "CreatedAtUtc"]
            : type == typeof(DiagramDocument) ? ["DiagramId", "CreatedAtUtc", "UpdatedAtUtc"]
            : type == typeof(WorkbenchState) ? ["WorkbenchId", "CreatedAtUtc", "UpdatedAtUtc", "SavedAtUtc"]
            : type == typeof(Scope) ? ["ScopeId"]
            : type == typeof(ReferenceConnectionLineState) ? ["ConnectionId"]
            : Array.Empty<string>();
        if (required.Any(name => !seen.Contains(name)))
            throw new InvalidDataException("A persisted entity is missing its identity or timestamp. Migration cannot invent unstable defaults.");
    }

    public static T Deserialize<T>(JsonElement value) where T : class
    {
        T decoded = JsonSerializer.Deserialize<T>(value, JsonOptions)
            ?? throw new InvalidDataException("A legacy model value could not be decoded.");
        // The existing converter's omitted-value semantics must not change across a recovery retry.
        // New runtime settings default logging on, but already journaled legacy settings defaulted off.
        if (decoded is AppSettings { Diagnostics: { } logging } && (!value.TryGetProperty("Diagnostics", out var diagnostics) ||
            diagnostics.ValueKind == JsonValueKind.Object && !diagnostics.TryGetProperty("EnableInternalLogging", out _)))
            logging.EnableInternalLogging = false;
        return decoded;
    }
}
