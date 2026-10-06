using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Serialization;

namespace AxePower.Core.Helpers;

/// <summary>What <see cref="JsonNullRepair.Repair"/> did to one loaded object.</summary>
/// <param name="Restored">Properties that were null and now hold their declared default.</param>
/// <param name="Dropped">List items removed because they were null or could not be made complete.</param>
/// <param name="Missing">Properties that are still null: they have no declared default to fall back on.</param>
public sealed record JsonRepairResult(IReadOnlyList<string> Restored, IReadOnlyList<string> Dropped, IReadOnlyList<string> Missing)
{
    /// <summary>Every non-nullable property now holds a value.</summary>
    public bool IsComplete => Missing.Count == 0;

    /// <summary>Something was restored or dropped, so the data on disk differed from the model.</summary>
    public bool Changed => Restored.Count > 0 || Dropped.Count > 0;

    /// <summary>A short, value-free description for the log (property paths only).</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Restored.Count > 0) parts.Add($"restored to defaults: {string.Join(", ", Restored)}");
        if (Dropped.Count > 0) parts.Add($"dropped: {string.Join(", ", Dropped)}");
        if (Missing.Count > 0) parts.Add($"missing: {string.Join(", ", Missing)}");
        return string.Join("; ", parts);
    }
}

/// <summary>
/// System.Text.Json writes an explicit JSON <c>null</c> straight into a property the model declares
/// non-nullable, and the compiler cannot see it happen. A file written by another version, edited by
/// hand or cut short can therefore load "successfully" and fail much later, far from the cause.
/// <para>
/// <see cref="Repair"/> runs at that boundary. Each such property goes back to the value a new
/// instance starts with (its declared default), null items are removed from lists, and list items
/// that cannot be made complete are removed too. A property whose declared default is null as well
/// has nothing to fall back on; it is reported in <see cref="JsonRepairResult.Missing"/> and the
/// caller rejects the record. Only AxePower.Core models are walked.
/// </para>
/// </summary>
public static class JsonNullRepair
{
    private sealed record Slot(PropertyInfo Property, bool NotNull, bool ItemsNotNull);

    private static readonly ConcurrentDictionary<Type, Slot[]> SlotCache = new();
    private static readonly NullabilityInfoContext Nullability = new();
    private static readonly object NullabilityGate = new();

    public static JsonRepairResult Repair(object model)
    {
        var restored = new List<string>();
        var dropped = new List<string>();
        var missing = new List<string>();
        Visit(model, model.GetType().Name, restored, dropped, missing, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return new JsonRepairResult(restored, dropped, missing);
    }

    /// <summary>
    /// Repairs each item of a loaded list and removes the null and incomplete ones. Returns what was
    /// done; <see cref="JsonRepairResult.Missing"/> is always empty because incomplete items are dropped.
    /// </summary>
    public static JsonRepairResult RepairItems<T>(List<T> items) where T : class
    {
        var restored = new List<string>();
        var dropped = new List<string>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var name = typeof(T).Name;
        for (int i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            var itemMissing = new List<string>();
            if (item is not null)
                Visit(item, $"{name}[{i}]", restored, dropped, itemMissing, seen);
            if (item is null || itemMissing.Count > 0)
            {
                items.RemoveAt(i);
                dropped.Add($"{name}[{i}]");
            }
        }
        return new JsonRepairResult(restored, dropped, Array.Empty<string>());
    }

    private static void Visit(object model, string path, List<string> restored, List<string> dropped, List<string> missing, HashSet<object> seen)
    {
        if (!seen.Add(model)) return;

        object? fresh = null;
        bool freshMade = false;
        foreach (var slot in SlotsFor(model.GetType()))
        {
            var property = slot.Property;
            var value = property.GetValue(model);
            var at = $"{path}.{property.Name}";

            if (value is null)
            {
                if (!slot.NotNull) continue;
                if (!freshMade)
                {
                    fresh = NewInstance(model.GetType());
                    freshMade = true;
                }
                value = fresh is null ? null : property.GetValue(fresh);
                if (value is null)
                {
                    missing.Add(at);
                    continue;
                }
                property.SetValue(model, value);
                restored.Add(at);
            }

            switch (value)
            {
                case Array array:
                    var kept = RepairSequence(array, array.GetType().GetElementType()!, slot.ItemsNotNull, at, restored, dropped, seen);
                    if (kept is not null)
                    {
                        var replacement = Array.CreateInstance(array.GetType().GetElementType()!, kept.Count);
                        for (int i = 0; i < kept.Count; i++) replacement.SetValue(kept[i], i);
                        property.SetValue(model, replacement);
                    }
                    break;
                case IList list when ListItemType(list.GetType()) is { } itemType:
                    var keptItems = RepairSequence(list, itemType, slot.ItemsNotNull, at, restored, dropped, seen);
                    if (keptItems is not null)
                    {
                        list.Clear();
                        foreach (var item in keptItems) list.Add(item);
                    }
                    break;
                default:
                    if (IsModel(value.GetType()))
                        Visit(value, at, restored, dropped, missing, seen);
                    break;
            }
        }
    }

    /// <summary>The items to keep, or null when nothing was removed.</summary>
    private static List<object?>? RepairSequence(IList items, Type itemType, bool itemsNotNull, string at,
        List<string> restored, List<string> dropped, HashSet<object> seen)
    {
        List<object?>? kept = null;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            bool remove = item is null && itemsNotNull;
            if (item is not null && IsModel(itemType))
            {
                var itemMissing = new List<string>();
                Visit(item, $"{at}[{i}]", restored, dropped, itemMissing, seen);
                remove = itemMissing.Count > 0;
            }

            if (remove)
            {
                dropped.Add($"{at}[{i}]");
                if (kept is null)
                {
                    kept = new List<object?>(items.Count);
                    for (int j = 0; j < i; j++) kept.Add(items[j]);
                }
            }
            else
            {
                kept?.Add(item);
            }
        }
        return kept;
    }

    private static Slot[] SlotsFor(Type type) => SlotCache.GetOrAdd(type, static t =>
    {
        // NullabilityInfoContext is not thread-safe.
        lock (NullabilityGate)
        {
            var slots = new List<Slot>();
            foreach (var property in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || property.PropertyType.IsValueType) continue;
                if (property.GetMethod is not { IsPublic: true } || property.SetMethod is not { IsPublic: true }) continue;
                if (property.IsDefined(typeof(JsonIgnoreAttribute))) continue;

                var info = Nullability.Create(property);
                var itemInfo = info.ElementType ?? (info.GenericTypeArguments.Length == 1 ? info.GenericTypeArguments[0] : null);
                bool itemsNotNull = itemInfo is { ReadState: NullabilityState.NotNull } && !itemInfo.Type.IsValueType;
                slots.Add(new Slot(property, info.ReadState == NullabilityState.NotNull, itemsNotNull));
            }
            return slots.ToArray();
        }
    });

    private static Type? ListItemType(Type listType) =>
        listType.IsGenericType && listType.GetGenericArguments() is { Length: 1 } args ? args[0] : null;

    private static bool IsModel(Type type) =>
        type.IsClass && type != typeof(string) && type.Assembly == typeof(JsonNullRepair).Assembly;

    private static object? NewInstance(Type type)
    {
        var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes);
        return constructor?.Invoke(null);
    }
}
