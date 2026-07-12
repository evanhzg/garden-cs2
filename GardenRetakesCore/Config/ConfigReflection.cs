using System.Collections;
using System.Globalization;
using System.Reflection;

namespace GardenRetakes.Core.Config;

/// <summary>
/// Reflection engine behind the in-game !gconfig command (ROADMAP R2).
/// Walks dotted, case-insensitive property paths ("Ranked.MinPlayers") over a
/// config object graph: lists sections, reads leaves and writes scalar values
/// with type validation. Collections are read-only here — edit the JSON file.
/// Pure logic, no CounterStrikeSharp dependency (unit tested).
/// </summary>
public static class ConfigReflection
{
    private const int MaxDepth = 6;

    public static bool IsLeafType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(string) || type == typeof(bool) || type.IsEnum ||
               type == typeof(int) || type == typeof(uint) ||
               type == typeof(long) || type == typeof(ulong) ||
               type == typeof(short) || type == typeof(ushort) ||
               type == typeof(float) || type == typeof(double) || type == typeof(decimal);
    }

    private static bool IsCollection(Type type) =>
        type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type);

    /// <summary>
    /// Resolves all path segments but the last; returns the parent object and
    /// the final PropertyInfo, or null with an error.
    /// </summary>
    private static (object Parent, PropertyInfo Property)? Resolve(object root, string path, out string? error)
    {
        error = null;
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || segments.Length > MaxDepth)
        {
            error = "invalid_path";
            return null;
        }

        object current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            var property = current.GetType().GetProperty(segments[i],
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is null)
            {
                error = $"unknown:{string.Join(".", segments.Take(i + 1))}";
                return null;
            }

            if (i == segments.Length - 1)
            {
                return (current, property);
            }

            var next = property.GetValue(current);
            if (next is null || IsLeafType(property.PropertyType) || IsCollection(property.PropertyType))
            {
                error = $"not_a_section:{string.Join(".", segments.Take(i + 1))}";
                return null;
            }

            current = next;
        }

        error = "invalid_path";
        return null;
    }

    /// <summary>Formats the value at the path (leaf) or lists its children (section).</summary>
    public static bool TryDescribe(object root, string? path, out List<string> lines, out string? error)
    {
        lines = [];
        error = null;

        object target = root;
        if (!string.IsNullOrWhiteSpace(path))
        {
            var resolved = Resolve(root, path, out error);
            if (resolved is null)
            {
                return false;
            }

            var (parent, property) = resolved.Value;
            var value = property.GetValue(parent);

            if (IsLeafType(property.PropertyType))
            {
                lines.Add($"{property.Name} = {FormatValue(value)}");
                return true;
            }

            if (value is null)
            {
                error = "not_a_section:" + path;
                return false;
            }

            if (IsCollection(property.PropertyType))
            {
                lines.Add($"{property.Name} = {FormatValue(value)} (collections: edit the JSON file)");
                return true;
            }

            target = value;
        }

        foreach (var property in target.GetType()
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .OrderBy(p => p.Name))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var value = property.GetValue(target);
            if (IsLeafType(property.PropertyType))
            {
                lines.Add($"{property.Name} = {FormatValue(value)}");
            }
            else if (IsCollection(property.PropertyType))
            {
                lines.Add($"{property.Name} = {FormatValue(value)} (edit the JSON file)");
            }
            else if (value is not null)
            {
                lines.Add($"{property.Name} {{...}}");
            }
        }

        return true;
    }

    /// <summary>Sets a scalar leaf value from its string representation.</summary>
    public static bool TrySet(object root, string path, string rawValue, out string? oldValue, out string? error)
    {
        oldValue = null;
        var resolved = Resolve(root, path, out error);
        if (resolved is null)
        {
            return false;
        }

        var (parent, property) = resolved.Value;
        if (!IsLeafType(property.PropertyType) || !property.CanWrite)
        {
            error = "not_settable:" + path;
            return false;
        }

        if (!TryConvert(rawValue, property.PropertyType, out var converted, out error))
        {
            return false;
        }

        oldValue = FormatValue(property.GetValue(parent));
        property.SetValue(parent, converted);
        return true;
    }

    private static bool TryConvert(string raw, Type targetType, out object? converted, out string? error)
    {
        converted = null;
        error = null;
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        raw = raw.Trim();

        try
        {
            if (type == typeof(string))
            {
                converted = raw;
            }
            else if (type == typeof(bool))
            {
                converted = raw.ToLowerInvariant() switch
                {
                    "true" or "1" or "on" or "yes" => true,
                    "false" or "0" or "off" or "no" => false,
                    _ => throw new FormatException(),
                };
            }
            else if (type.IsEnum)
            {
                converted = Enum.Parse(type, raw, ignoreCase: true);
            }
            else
            {
                converted = Convert.ChangeType(raw, type, CultureInfo.InvariantCulture);
            }

            return true;
        }
        catch
        {
            error = $"bad_value:{type.Name}";
            return false;
        }
    }

    public static string FormatValue(object? value)
    {
        return value switch
        {
            null => "null",
            string s => $"\"{s}\"",
            bool b => b ? "true" : "false",
            IDictionary dict => $"[dict: {dict.Count} entries]",
            ICollection collection => $"[list: {collection.Count} items]",
            IEnumerable enumerable and not string => $"[list: {enumerable.Cast<object>().Count()} items]",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "?",
        };
    }
}
