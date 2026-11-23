using System.Diagnostics;

namespace IptvPlayer.Tests;

/// <summary>Minimale testrunner zonder externe packages. Start met: dotnet run --project tests/IptvPlayer.Tests</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var filter = args.Length > 0 ? args[0] : null;
        var passed = 0;
        var failed = new List<(string Name, Exception Error)>();

        foreach (var (name, test) in TestRegistry.All())
        {
            if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var sw = Stopwatch.StartNew();
            try
            {
                await test();
                passed++;
                Console.WriteLine($"  OK    {name} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                failed.Add((name, ex));
                Console.WriteLine($"  FAIL  {name}");
                Console.WriteLine("        " + ex.Message.Replace("\n", "\n        "));
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{passed} geslaagd, {failed.Count} mislukt.");
        return failed.Count == 0 ? 0 : 1;
    }
}

public sealed class TestFailure : Exception
{
    public TestFailure(string message) : base(message) { }
}

public static class Assert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition) throw new TestFailure(message ?? "Voorwaarde was niet waar.");
    }

    public static void False(bool condition, string? message = null) => True(!condition, message ?? "Voorwaarde was onverwacht waar.");

    public static void Equal<T>(T expected, T actual, string? what = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new TestFailure($"{what ?? "Waarde"}: verwacht <{expected}>, kreeg <{actual}>.");
    }

    public static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? what = null)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        if (!e.SequenceEqual(a))
            throw new TestFailure($"{what ?? "Reeks"}: verwacht [{string.Join(", ", e)}], kreeg [{string.Join(", ", a)}].");
    }

    public static void Throws<TEx>(Action action) where TEx : Exception
    {
        try { action(); }
        catch (TEx) { return; }
        throw new TestFailure($"Verwachtte uitzondering {typeof(TEx).Name}, maar er kwam geen.");
    }

    public static async Task ThrowsAsync<TEx>(Func<Task> action) where TEx : Exception
    {
        try { await action(); }
        catch (TEx) { return; }
        throw new TestFailure($"Verwachtte uitzondering {typeof(TEx).Name}, maar er kwam geen.");
    }
}
