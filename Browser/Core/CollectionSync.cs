using System.Collections.ObjectModel;

namespace Browser.Core;

public static class CollectionSync
{
    /// <summary>Reconciles an observable collection to match source with minimal Insert/Move/Remove.</summary>
    public static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> source) where T : class
    {
        if (source.Count == 0)
        {
            if (target.Count > 0) target.Clear();
            return;
        }

        var wanted = new HashSet<T>(source, ReferenceEqualityComparer.Instance);
        for (int i = target.Count - 1; i >= 0; i--)
            if (!wanted.Contains(target[i])) target.RemoveAt(i);

        for (int i = 0; i < source.Count; i++)
        {
            var item = source[i];
            if (i < target.Count && ReferenceEquals(target[i], item)) continue;

            int existing = -1;
            for (int j = i + 1; j < target.Count; j++)
                if (ReferenceEquals(target[j], item)) { existing = j; break; }

            if (existing >= 0) target.Move(existing, i);
            else target.Insert(i, item);
        }

        while (target.Count > source.Count) target.RemoveAt(target.Count - 1);
    }
}

public static class Plural
{
    public static string Of(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    public static string Tabs(int n) => Of(n, "tab", "tabs");
    public static string Asleep(int n) => $"{n} asleep";
}
