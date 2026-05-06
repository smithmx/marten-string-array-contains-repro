using System.Linq.Expressions;
using JasperFx;
using Marten;

const string ConnectionString =
    "Host=localhost;Port=5432;Database=marten_repro;Username=postgres;Password=postgres";

using var store = DocumentStore.For(opts =>
{
    opts.Connection(ConnectionString);
    opts.AutoCreateSchemaObjects = AutoCreate.All;
});

await using (var initSession = store.LightweightSession())
{
    await initSession.Database.EnsureStorageExistsAsync(typeof(Sample));
}

PrintExpressionTrees();

RunFailingPattern(store);
RunArrayLiteralPattern(store);
RunListRemediationPattern(store);

static void PrintExpressionTrees()
{
    var methodInit = MakeStrings();
    Expression<Func<Sample, bool>> failing = s => methodInit.Contains(s.Name);

    var literalInit = new[] { "a", "b" };
    Expression<Func<Sample, bool>> working = s => literalInit.Contains(s.Name);

    Console.WriteLine("=== EXPRESSION TREES ===");
    Console.WriteLine($"Failing  (method init):  {failing}");
    Console.WriteLine($"Working  (literal init): {working}");
    Console.WriteLine();
}

static void RunFailingPattern(IDocumentStore store)
{
    Console.WriteLine("=== FAILING PATTERN: var values = MakeStrings(); ===");
    try
    {
        var values = MakeStrings();
        using var session = store.LightweightSession();
        var rows = session.Query<Sample>().Where(s => values.Contains(s.Name)).ToList();
        Console.WriteLine($"OK — query returned {rows.Count} rows (UNEXPECTED — bug did not fire)");
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex);
    }
    Console.WriteLine();
}

static void RunArrayLiteralPattern(IDocumentStore store)
{
    Console.WriteLine("=== WORKING PATTERN: var values = new[] { \"a\", \"b\" }; ===");
    try
    {
        var values = new[] { "a", "b" };
        using var session = store.LightweightSession();
        var rows = session.Query<Sample>().Where(s => values.Contains(s.Name)).ToList();
        Console.WriteLine($"OK — query returned {rows.Count} rows");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"UNEXPECTED FAILURE: {ex.GetType().FullName}: {ex.Message}");
    }
    Console.WriteLine();
}

static void RunListRemediationPattern(IDocumentStore store)
{
    Console.WriteLine("=== REMEDIATION: var values = MakeStrings().ToList(); ===");
    try
    {
        var values = MakeStrings().ToList();
        using var session = store.LightweightSession();
        var rows = session.Query<Sample>().Where(s => values.Contains(s.Name)).ToList();
        Console.WriteLine($"OK — query returned {rows.Count} rows");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"UNEXPECTED FAILURE: {ex.GetType().FullName}: {ex.Message}");
    }
    Console.WriteLine();
}

static string[] MakeStrings() => new[] { "a", "b" };

public sealed class Sample
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}
