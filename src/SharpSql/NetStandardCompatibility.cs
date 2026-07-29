using System.Runtime.CompilerServices;

namespace SharpSql;

#if NETSTANDARD2_0
internal static class NetStandardCompatibilityExtensions
{
    public static HashSet<T> ToHashSet<T>(this IEnumerable<T> source) => new(source);

    public static HashSet<T> ToHashSet<T>(
        this IEnumerable<T> source,
        IEqualityComparer<T> comparer) => new(source, comparer);

    public static bool TryAdd<TKey, TValue>(
        this Dictionary<TKey, TValue> dictionary,
        TKey key,
        TValue value)
        where TKey : notnull
    {
        if (dictionary.ContainsKey(key))
            return false;
        dictionary.Add(key, value);
        return true;
    }

    public static TValue? GetValueOrDefault<TKey, TValue>(
        this IReadOnlyDictionary<TKey, TValue> dictionary,
        TKey key) => dictionary.TryGetValue(key, out var value) ? value : default;

    public static TValue GetValueOrDefault<TKey, TValue>(
        this IReadOnlyDictionary<TKey, TValue> dictionary,
        TKey key,
        TValue defaultValue) => dictionary.TryGetValue(key, out var value) ? value : defaultValue;

    public static bool TryDequeue<T>(this Queue<T> queue, out T value)
    {
        if (queue.Count > 0)
        {
            value = queue.Dequeue();
            return true;
        }
        value = default!;
        return false;
    }

    public static void Deconstruct<TKey, TValue>(
        this KeyValuePair<TKey, TValue> pair,
        out TKey key,
        out TValue value)
    {
        key = pair.Key;
        value = pair.Value;
    }

    public static bool Contains(this string value, string candidate, StringComparison comparison) =>
        value.IndexOf(candidate, comparison) >= 0;

    public static string Replace(
        this string value,
        string oldValue,
        string newValue,
        StringComparison comparison)
    {
        if (comparison == StringComparison.Ordinal)
            return value.Replace(oldValue, newValue);

        var result = new System.Text.StringBuilder();
        var start = 0;
        while (true)
        {
            var index = value.IndexOf(oldValue, start, comparison);
            if (index < 0)
            {
                result.Append(value, start, value.Length - start);
                return result.ToString();
            }
            result.Append(value, start, index - start);
            result.Append(newValue);
            start = index + oldValue.Length;
        }
    }

    public static string[] Split(
        this string value,
        char separator,
        StringSplitOptions options) => value.Split([separator], options);
}

internal sealed class ReferenceComparer<T> : IEqualityComparer<T>
    where T : class
{
    public static ReferenceComparer<T> Instance { get; } = new();

    public bool Equals(T? left, T? right) => ReferenceEquals(left, right);

    public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
}
#else
internal static class ReferenceComparer<T>
    where T : class
{
    public static IEqualityComparer<T> Instance { get; } =
        (IEqualityComparer<T>)ReferenceEqualityComparer.Instance;
}
#endif

internal static class ReadOnlyDictionaryCompatibilityExtensions
{
    public static bool SetEquals<T>(this IReadOnlyCollection<T> source, IEnumerable<T> other) =>
        new HashSet<T>(source).SetEquals(other);

    public static Dictionary<TKey, TValue> CopyToDictionary<TKey, TValue>(
        this IReadOnlyDictionary<TKey, TValue> source,
        IEqualityComparer<TKey> comparer)
        where TKey : notnull => source.ToDictionary(item => item.Key, item => item.Value, comparer);
}
