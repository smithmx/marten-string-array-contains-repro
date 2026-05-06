# Marten 8.34.2 LINQ-provider regression: `string[].Contains` in Where predicate throws under C# 14 + nullable-enabled scope

A standalone .NET 10 console reproduction of a Marten 8.34.2 LINQ-parser bug fired by C# 14's closure-emission shape for method-typed array initialisers under `<Nullable>enable</Nullable>`.

## Versions

- **Marten:** 8.34.2
- **JasperFx.Events:** 1.31.1 (transitive)
- **JasperFx (core):** 1.28.2 (transitive)
- **Weasel.Postgresql:** 8.15.0 (transitive)
- **.NET SDK:** 10.0.103
- **Target framework:** `net10.0`
- **C# language:** 14 (TFM default for `net10.0` under SDK 10)
- **Nullable:** `enable`
- **Postgres:** 17 (any 14+ should reproduce — bug is parse-time, before SQL is executed)

## Summary

`string[].Contains(string)` inside a Marten `.Where(...)` predicate throws `System.InvalidOperationException` at query time when **both** of these hold at the call site:

1. The file (or project) has nullable annotations enabled (`#nullable enable` directive or `<Nullable>enable</Nullable>` MSBuild property).
2. The captured `string[]` local was initialised from a **method-typed return** (`var v = MakeStrings();` where `MakeStrings()` returns `string[]`). Initialising from an array literal (`var v = new[] { "a", "b" };`) does **not** trigger the failure.

The bug fires at parse time — no SQL is executed and no rows are required. Materialising the array as `List<T>` via `.ToList()` is an effective workaround: `List<T>` is not span-convertible, so binding stays on `Enumerable.Contains`, which Marten parses correctly.

The failure also fires on `session.Events.QueryAllRawEvents()` queries — both event and document queryables route through `MemoryExtensionsContains.Parse → ValueCollectionMember.ParseWhereForContains`. This repro uses a document query for minimality.

## Reproduction

```bash
docker compose up -d   # Postgres 17 on localhost:5432
dotnet run             # builds + runs all three patterns
```

The program prints expression trees for the failing and working lambdas, then runs three Where-predicate variants in sequence:

| Variant | Receiver init | Result |
|---|---|---|
| 1. Failing pattern | `var values = MakeStrings();` (method) | Throws `InvalidOperationException` |
| 2. Array literal | `var values = new[] { "a", "b" };` | OK |
| 3. List remediation | `var values = MakeStrings().ToList();` | OK |

`MakeStrings()` is `static string[] MakeStrings() => new[] { "a", "b" };` — same payload as variant 2. Only the closure-emission shape differs.

## Expression-tree comparison (the smoking gun)

The C# 14 compiler emits structurally different receiver expressions depending on init shape. Captured from `Expression<Func<Sample, bool>>` in this repro:

```
Failing  (method init):   s => op_Implicit(Convert(value(...).methodInit, String[])).Contains(s.Name)
Working  (literal init):  s => op_Implicit(value(...).literalInit).Contains(s.Name)
```

The difference is the inner `Convert(closureField, String[])` wrapper, which the C# 14 compiler inserts under `<Nullable>enable</Nullable>` when the local was initialised from a method-typed return. Marten's `UnwrapConversions` strips the outer `op_Implicit` (it handles `MethodCallExpression` whose `Method.Name == "op_Implicit"`) but does **not** peel the inner `UnaryExpression(ExpressionType.Convert)`.

## Expected behaviour

All three variants should parse the predicate and emit equivalent SQL `... WHERE name = ANY(:values)` (or `IN (...)`) clauses. They differ only in how the local is constructed; the parser shouldn't care.

## Actual behaviour (variant 1)

```
System.InvalidOperationException: variable 's' of type 'Sample' referenced from scope '', but it is not defined
   at System.Linq.Expressions.Compiler.VariableBinder.Reference(ParameterExpression node, VariableStorageKind storage)
   at System.Linq.Expressions.Compiler.VariableBinder.VisitParameter(ParameterExpression node)
   at System.Linq.Expressions.ExpressionVisitor.VisitMember(MemberExpression node)
   at System.Linq.Expressions.Compiler.VariableBinder.VisitUnary(UnaryExpression node)
   at System.Linq.Expressions.ExpressionVisitor.Visit(ReadOnlyCollection`1 nodes)
   at System.Linq.Expressions.Compiler.VariableBinder.VisitLambda[T](Expression`1 node)
   at System.Linq.Expressions.Compiler.VariableBinder.Bind(LambdaExpression lambda)
   at System.Linq.Expressions.Compiler.LambdaCompiler.Compile(LambdaExpression lambda)
   at System.Linq.Expressions.Expression`1.Compile()
   at FastExpressionCompiler.ExpressionCompiler.CompileSys[TDelegate](Expression`1 lambdaExpr)
   at FastExpressionCompiler.ExpressionCompiler.CompileFast[R](Expression`1 lambdaExpr, Boolean ifFastFailedReturnNull, CompilerFlags flags)
   at Marten.Linq.Parsing.LinqInternalExtensions.ReduceToConstant(Expression expression) in /_/src/Marten/Linq/Parsing/LinqInternalExtensions.cs:line 340
   at Marten.Linq.Members.ValueCollections.ValueCollectionMember.ParseWhereForContains(MethodCallExpression body, IReadOnlyStoreOptions options) in /_/src/Marten/Linq/Members/ValueCollections/ValueCollectionMember.cs:line 97
   at Marten.Linq.Parsing.Methods.MemoryExtensionsContains.Parse(IQueryableMemberCollection memberCollection, IReadOnlyStoreOptions options, MethodCallExpression expression) in /_/src/Marten/Linq/Parsing/Methods/MemoryExtensionsContains.cs:line 38
   at Marten.Linq.Parsing.WhereClauseParser.VisitMethodCall(MethodCallExpression node) in /_/src/Marten/Linq/Parsing/WhereClauseParser.cs:line 106
   at Marten.Linq.Parsing.WhereClauseParser.Visit(Expression node) in /_/src/Marten/Linq/Parsing/WhereClauseParser.cs:line 43
   at Marten.Linq.SqlGeneration.Statement.ParseWhereClause(IReadOnlyList`1 wheres, IMartenSession session, IQueryableMemberCollection collection, IDocumentStorage storage) in /_/src/Marten/Linq/SqlGeneration/Statement.WhereParsing.cs:line 58
   at Marten.Linq.CollectionUsage.BuildTopStatement(IMartenSession session, IQueryableMemberCollection collection, IDocumentStorage storage, QueryStatistics statistics) in /_/src/Marten/Linq/CollectionUsage.Compilation.cs:line 40
   at Marten.Linq.Parsing.LinqQueryParser.BuildStatements() in /_/src/Marten/Linq/Parsing/LinqQueryParser.Statements.cs:line 33
   at Marten.Linq.Parsing.LinqQueryParser.BuildHandler[TResult]() in /_/src/Marten/Linq/Parsing/LinqQueryParser.Handlers.cs:line 58
   at Marten.Linq.MartenLinqQueryProvider.Execute[TResult](Expression expression) in /_/src/Marten/Linq/MartenLinqQueryProvider.cs:line 56
   at Marten.Linq.MartenLinqQueryable`1.GetEnumerator() in /_/src/Marten/Linq/MartenLinqQueryable.cs:line 136
   at System.Collections.Generic.List`1..ctor(IEnumerable`1 collection)
   at System.Linq.Enumerable.ToList[TSource](IEnumerable`1 source)
```

## Diagnosis

Under C# 14, `string[].Contains(string)` rebinds from `Enumerable.Contains` (the `IEnumerable<T>` extension) to `MemoryExtensions.Contains<T>(ReadOnlySpan<T>, T)` (the span overload). Marten dispatches to `MemoryExtensionsContains.Parse` for that binding (`src/Marten/Linq/Parsing/Methods/MemoryExtensionsContains.cs:38`), which calls `UnwrapConversions` on the receiver to strip the implicit-conversion shell before reducing to a constant.

The expression tree the parser receives differs by closure-init shape:

| Init shape | Receiver expression tree |
|---|---|
| `var v = MakeStrings();` (method, the failing case) | `op_Implicit(Convert(closureField, String[]))` |
| `var v = new[] { "a", "b" };` (literal, working) | `op_Implicit(closureField)` |

Under `<Nullable>enable</Nullable>`, the C# compiler inserts the additional `Convert(closureField, String[])` wrapper for method-typed initialisations. `UnwrapConversions` strips the outer `op_Implicit` (it handles `MethodCallExpression` whose `Method.Name == "op_Implicit"`) but does not peel through `UnaryExpression(ExpressionType.Convert)` nodes. The Convert node remains; the receiver fails to reduce to a constant; parsing falls through to `ValueCollectionMember.ParseWhereForContains` (`src/Marten/Linq/Members/ValueCollections/ValueCollectionMember.cs:97`), which calls `LinqInternalExtensions.ReduceToConstant → FastExpressionCompiler.CompileFast` (`src/Marten/Linq/Parsing/LinqInternalExtensions.cs:340`) on **the entire Where lambda**. The compiled delegate references the lambda parameter (`s` here) from a scope where it isn't defined — the IL is malformed and `VariableBinder` rejects it.

## Where the fix could land

The expression-tree shape opens up three candidate fix sites, each addressing a different layer of the problem. They're not mutually exclusive — a maintainer might land any one, or combine a targeted root-cause fix with defense-in-depth.

### Option 1 — peel `Convert` in `UnwrapConversions`

`src/Marten/Linq/Parsing/LinqInternalExtensions.cs`

Narrowest scope. `UnwrapConversions` already strips `op_Implicit` `MethodCallExpression`s; extending it to also peel `UnaryExpression(ExpressionType.Convert | ExpressionType.ConvertChecked)` covers the compiler-inserted `string[] → string[]` no-op observed here, and any structurally similar Convert in adjacent shapes.

**Tradeoff**: peel rules accumulate. The C# compiler keeps introducing new emission shapes — `<Nullable>enable</Nullable>`, span overloads, primary constructors — and each one that escapes the existing peel rules turns into a fresh user-facing crash. A liberal peel covers the immediate bug; the parser stays sensitive to whatever emission the compiler invents next.

**Prior art**: peeling compiler-inserted Convert nodes is a common pattern in providers that translate LINQ to SQL — EF Core handles it throughout its expression-extension helpers.

### Option 2 — eager-evaluate the receiver in `MemoryExtensionsContains.Parse`

`src/Marten/Linq/Parsing/Methods/MemoryExtensionsContains.cs`

`Contains` semantics only need the resolved values for the `IN (...)` SQL clause — there's no semantic benefit to the structural pattern-match-then-reduce path. Replacing the receiver reduction with `Expression.Lambda<Func<T[]>>(receiverExpression).CompileFast()()` sidesteps the compiler-emission shape entirely: whatever wrapper nodes show up, the lambda compiler resolves them to the same array.

**Tradeoff**: cost is one compile-and-invoke per *unique* receiver-expression at parse time, not per query execution. Loses any structural introspection — relevant only if anything downstream of `UnwrapConversions` cares about the receiver's expression shape beyond `ReduceToConstant`'s output. If it's all "give me the values", the eager path is sufficient.

### Option 3 — harden the `ParseWhereForContains` fall-through

`src/Marten/Linq/Members/ValueCollections/ValueCollectionMember.cs:97`

When `ReduceToConstant` is called on a receiver that doesn't reduce, the current code compiles *the entire Where lambda* as a `Func<bool>`. The receiver references the predicate's lambda parameter (`s` in this repro), so the resulting delegate references a variable from outside its own scope, and `VariableBinder` rejects the IL. That's what produces the `"variable 's' … referenced from scope ''"` message — confusing because it surfaces deep inside `FastExpressionCompiler` rather than at the LINQ-parser level.

Defense-in-depth, not a root-cause fix: detecting "the receiver references the predicate parameter" before invoking the compiler would convert the malformed-IL crash into a clear `NotSupportedException`-style message. Even if Options 1 or 2 close the door, this fall-through is a sharp edge — the next emission shape that escapes the peel rules will land here, and a clearer error makes the next round of triage shorter.

### Decision axes

- Does anything downstream of `UnwrapConversions` rely on the structural form of the receiver, or only on `ReduceToConstant`'s output? If only the value matters, Option 2 is the lowest-future-maintenance choice.
- Are other parsers in `Marten.Linq.Parsing.Methods` routing through `ParseWhereForContains` (or analogous fall-throughs in `ValueCollectionMember`) for non-`Contains` calls? If the fall-through is broader than this single method, Option 3 earns its keep across more call sites.
- Test coverage over the closure-emission matrix (literal init / method-typed init / `<Nullable>` on / off) alongside whichever fix lands would catch the next compiler-shape regression at CI rather than user-side.

## Workaround in our codebase

Materialise the array as `List<T>` via `.ToList()` — `List<T>` is not span-convertible, so binding stays on `Enumerable.Contains`, which Marten parses correctly:

```csharp
// ❌ Throws
var values = MakeStrings();
session.Query<Sample>().Where(s => values.Contains(s.Name)).ToList();

// ✅ Works
var values = MakeStrings().ToList();
session.Query<Sample>().Where(s => values.Contains(s.Name)).ToList();
```

## Layout

```
.
├── README.md              # this bug report
├── MartenBugRepro.csproj  # net10.0, <Nullable>enable</Nullable>, Marten 8.34.2
├── Program.cs             # the three Where variants + expression-tree dump
└── docker-compose.yml     # Postgres 17 on localhost:5432
```

`Program.cs` connects to the Postgres started by docker-compose using the const at the top of the file — edit if your local Postgres lives elsewhere.
