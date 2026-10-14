using Xunit.Abstractions;
using Xunit.Sdk;

namespace WerHigiena;

internal static class WerSeed
{
    public static readonly int Value =
        int.TryParse(Environment.GetEnvironmentVariable("WER_SEED"), out int s) ? s : 20261012;

    // Stabilny skrót FNV-1a (string.GetHashCode jest losowy dla każdego procesu).
    public static int Stable(string text)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in text) { h ^= c; h *= 16777619; }
            return (int)h;
        }
    }

    public static List<T> Shuffle<T>(IEnumerable<T> items, Func<T, string> key, string salt)
    {
        var list = items.OrderBy(key, StringComparer.Ordinal).ToList();
        var rng = new Random(Value ^ Stable(salt));
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }
}

public sealed class RandomTestCaseOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases) where TTestCase : ITestCase
    {
        var list = testCases.ToList();
        string salt = list.Count > 0 ? list[0].TestMethod.TestClass.Class.Name : string.Empty;
        return WerSeed.Shuffle(list, t => t.UniqueID, salt);
    }
}

public sealed class RandomTestCollectionOrderer : ITestCollectionOrderer
{
    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
        WerSeed.Shuffle(testCollections, c => c.DisplayName, "collections");
}
