using System.Diagnostics;
using System.Reflection;

namespace SloomyTweaks.Tests.TestFramework;

// Minimal xUnit-compatible surface so the suite runs without NuGet access.
// Only [Fact] and the Assert members used by this suite are implemented.

[AttributeUsage(AttributeTargets.Method)]
public sealed class FactAttribute : Attribute
{
    public string? Skip { get; set; }
}

public sealed class AssertException : Exception
{
    public AssertException(string message) : base(message) { }
    public AssertException() { }
    public AssertException(string message, Exception inner) : base(message, inner) { }
}

public static class Assert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition) throw new AssertException(message ?? "Expected true.");
    }

    public static void False(bool condition, string? message = null)
    {
        if (condition) throw new AssertException(message ?? "Expected false.");
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertException((message is null ? "" : message + " — ") + $"Expected: {Fmt(expected)}, Actual: {Fmt(actual)}");
    }

    public static void Equal(double expected, double actual, double tolerance)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new AssertException($"Expected {expected} ± {tolerance}, Actual: {actual}");
    }

    public static void NotEqual<T>(T notExpected, T actual)
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
            throw new AssertException($"Did not expect: {Fmt(actual)}");
    }

    public static void Null(object? value)
    {
        if (value is not null) throw new AssertException($"Expected null, got {Fmt(value)}");
    }

    public static T NotNull<T>(T? value) where T : class =>
        value ?? throw new AssertException("Expected a non-null value.");

    public static void Contains(string expectedSubstring, string? actual)
    {
        if (actual is null || !actual.Contains(expectedSubstring, StringComparison.Ordinal))
            throw new AssertException($"Expected \"{actual}\" to contain \"{expectedSubstring}\".");
    }

    public static void Contains<T>(T item, IEnumerable<T> collection)
    {
        if (!collection.Contains(item)) throw new AssertException($"Collection does not contain {Fmt(item)}.");
    }

    public static void Empty<T>(IEnumerable<T> collection)
    {
        if (collection.Any()) throw new AssertException($"Expected empty collection, found {collection.Count()} item(s).");
    }

    public static void NotEmpty<T>(IEnumerable<T> collection)
    {
        if (!collection.Any()) throw new AssertException("Expected a non-empty collection.");
    }

    public static void InRange(double actual, double low, double high)
    {
        if (actual < low || actual > high) throw new AssertException($"{actual} is not in range [{low}, {high}].");
    }

    public static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T e) { return e; }
        catch (Exception e) { throw new AssertException($"Expected {typeof(T).Name}, got {e.GetType().Name}: {e.Message}"); }
        throw new AssertException($"Expected {typeof(T).Name}, but nothing was thrown.");
    }

    private static string Fmt(object? v) => v switch { null => "null", string s => $"\"{s}\"", _ => v.ToString() ?? "" };
}

public static class TestRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? filter = args.FirstOrDefault();
        var tests = typeof(TestRunner).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.IsPublic)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<FactAttribute>() is not null)
                .Select(m => (Type: t, Method: m)))
            .Where(x => filter is null || $"{x.Type.Name}.{x.Method.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Type.Name).ThenBy(x => x.Method.Name)
            .ToList();

        int passed = 0, failed = 0, skipped = 0;
        var total = Stopwatch.StartNew();
        string? currentClass = null;
        foreach (var (type, method) in tests)
        {
            if (type.Name != currentClass)
            {
                currentClass = type.Name;
                Console.WriteLine($"\n{type.Name}");
            }
            var fact = method.GetCustomAttribute<FactAttribute>()!;
            if (fact.Skip is not null)
            {
                skipped++;
                Console.WriteLine($"  SKIP  {method.Name} ({fact.Skip})");
                continue;
            }

            var sw = Stopwatch.StartNew();
            object? instance = null;
            try
            {
                instance = Activator.CreateInstance(type);
                object? result = method.Invoke(instance, null);
                if (result is Task task) await task;
                passed++;
                Console.WriteLine($"  PASS  {method.Name} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception e)
            {
                Exception inner = e is TargetInvocationException { InnerException: { } ie } ? ie : e;
                failed++;
                Console.WriteLine($"  FAIL  {method.Name}: {inner.GetType().Name}: {inner.Message}");
                if (inner is not AssertException) Console.WriteLine(Indent(inner.StackTrace));
            }
            finally
            {
                if (instance is IAsyncDisposable ad) await ad.DisposeAsync();
                else if (instance is IDisposable d) d.Dispose();
            }
        }

        Console.WriteLine($"\n{passed} passed, {failed} failed, {skipped} skipped, {tests.Count} total in {total.Elapsed.TotalSeconds:0.0}s");
        return failed == 0 ? 0 : 1;
    }

    private static string Indent(string? s) => string.Join('\n', (s ?? "").Split('\n').Take(6).Select(l => "        " + l.Trim()));
}
