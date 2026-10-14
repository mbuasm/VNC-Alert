using System.Reflection;
using MaszynyAlert.Core.Tests.Cluster;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests;

public sealed class TestClassLifetimeTests
{
    /// <summary>Klasy, które xUnit uruchamia jako klasy testów: nieabstrakcyjne, z metodą <c>[Fact]</c>/<c>[Theory]</c> (także dziedziczoną).</summary>
    internal static IReadOnlyList<Type> TestClasses(Assembly assembly) =>
        [.. assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Any(m => m.IsDefined(typeof(FactAttribute), inherit: true)))];

    [Fact]
    public void KlasyTestow_NieSprzatajaPrzezIAsyncDisposable_TylkoPrzezIAsyncLifetime()
    {
        IReadOnlyList<Type> testClasses = TestClasses(typeof(TestClassLifetimeTests).Assembly);
        // Kontrola wykrywania: bez niej pusta lista (np. inny typ atrybutu) dawałaby fałszywą zieleń.
        Assert.Contains(typeof(TestClassLifetimeTests), testClasses);
        Assert.Contains(typeof(DowntimeSyncTests), testClasses);
        Assert.Contains(typeof(GroupContractTests), testClasses);
        Assert.DoesNotContain(typeof(TestCluster), testClasses);

        string[] neverDisposed = [.. testClasses
            .Where(t => typeof(IAsyncDisposable).IsAssignableFrom(t) && !typeof(IAsyncLifetime).IsAssignableFrom(t))
            .Select(t => t.FullName!)
            .Order(StringComparer.Ordinal)];

        Assert.True(neverDisposed.Length == 0,
            "Klasy testów z IAsyncDisposable bez IAsyncLifetime — xUnit 2.9.3 nie zawoła ich DisposeAsync (zamień na IAsyncLifetime: " +
            "InitializeAsync => Task.CompletedTask, DisposeAsync zwraca Task): " + string.Join(", ", neverDisposed));
    }
}
