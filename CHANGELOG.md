# Changelog

## [Unreleased]
### Changed
- **`SaveChanges` on modified entities no longer emits one `union` leg per row.** `AppendUpdateOperation` built `let A = union(T | where pk == k1 | extend ..., (T | where pk == k2 | extend ...), ...)`, so the query-operator count grew with the batch size: a 1000-row batch became a ~470 KB command with 1000 legs, measured at ~35-47 s and ~30-71 CPU-seconds on a 2-node `Standard_E2ads_v5` cluster against a 443-column table. Each entity now contributes one inline `datatable` row - its key plus a `dynamic` bag of only the columns it changed - and the batch resolves each column once via `lookup` + `bag_has_key`. The same batch measures **~2 s and ~1 CPU-second**, and cost follows the batch's union of changed columns rather than its row count.

  Three behaviours are preserved, each easy to lose in a change of this kind:
  - rows in one batch may change **different** column sets;
  - a column a row does not mention keeps its **live** value, so a concurrent writer that changed a different column on that row is not rolled back;
  - `bag_has_key` separates "clear this column" from "leave it alone", which a null check cannot.

  The store-type to KQL-conversion mapping lives in `KustoLiteral` beside `TypedNull`, which already owns that vocabulary. `BuildJsonPayload` and the change bag share one JSON writer differing only in whether nulls are skipped. `BuildExtendClause` is removed - nothing called it once the union shape was gone.

### Fixed
- **A batch is now bounded by command bytes as well as row count.** Kusto rejects a command whose text exceeds 2 MiB (`SYN0009: Query length ... too large (max: 2097152)`), but `MaxBatchSize` counts rows - the wrong unit when row size varies widely. Measured on a 443-column table: 1000 rows changing ten small columns is ~0.12 MB and succeeds, while 1000 rows each carrying an 8000-character text column is ~8.1 MB and the entire batch fails; the practical ceiling is ~250 such rows. This affected the previous `union` shape identically (~8.5 MB for the same data), so it was a pre-existing limit that neither shape handled. `TryAddCommand` now refuses a command that would overflow the limit, which is EF's signal to close the batch and continue in the next one. The first command in a batch is always accepted, so a single oversized row surfaces Kusto's own error instead of looping.

## [0.2.11]
### Fixed
- `Any(predicate)`/`All(predicate)` over a shadow array-column property (`EF.Property<T>(entity, "col").AsQueryable().Any/All(...)`) silently discarded the predicate whenever it wasn't a single equality/inequality against one constant, collapsing to "array is non-empty" regardless of what the predicate actually checked. Compound predicates (`Any(a => a == x || a == y)`, `All(a => a != x && a != y)`) now translate correctly into repeated array-membership checks; anything outside that shape (mixed `&&`/`||`, ranges, method calls) now throws `NotSupportedException` instead of silently returning the wrong answer.
- `Queryable.Contains` over the same shadow array-column property mis-typed a parameterized non-`string` value (e.g. a captured `int`), binding it with `DbType = String` instead of its correct type.

## [0.2.10]
### Fixed
- `string.Contains`/`StartsWith`/`EndsWith` (the plain single-`string`-argument overloads) now translate to Kusto's native `contains_cs`/`startswith_cs`/`endswith_cs` operators. Previously unsupported: the call fell through untranslated, throwing `NotSupportedException` — notably breaking OData's `$filter=contains(...)` (and `startswith`/`endswith`) query functions. The `_cs` (case-sensitive) operator variants are used to match C#'s case-sensitive default semantics and this provider's existing case-sensitive `==`/`strcmp`-based comparisons; Kusto's plain `contains`/`startswith`/`endswith` are case-insensitive by default.

## [0.2.9]
### Fixed
- Batched deletes (e.g. `RemoveRange`) generated malformed KQL for 2+ rows due to an unbalanced parenthesis in `AppendDeleteOperation`.

## [0.2.8]
### Added
- `string.IsNullOrEmpty`/`IsNullOrWhiteSpace` now translate to `isempty()`/`isempty(trim(...))`. Previously unsupported: the call fell through to EF Core's default expansion (`IsNull(x) OR x == ""`), which this provider's null handling collapsed into a bare `x == ""`, silently missing rows where the column was actually null.
- Opt-in `UseIsEmptyForStringIsNull()` on `KustoDbContextOptionsBuilder`: when enabled, `x.Field == null` / `!= null` on a string-typed operand generates `isempty()`/`isnotempty()` instead of `isnull()`/`isnotnull()`. A Kusto string column can never actually hold a database null, so `isnull()` is structurally always false for one — this switches to Kusto's own recommended idiom instead. Disabled by default; existing `isnull`/`isnotnull` behavior is unchanged unless called.

### Fixed
- KQL has no bare `null` keyword. Null literals reached through a constant or `CASE` branch (e.g. a ternary with an explicit `null` arm) previously rendered as the literal text `null`, which Kusto doesn't recognize. These now render as typed nulls (`int(null)`, `datetime(null)`, `guid(null)`, ...); strings fall back to `""`, since a Kusto string can't represent null at all.

## [0.2.7]
### Added
- Support for inner and right joins (previously only left join was translated).

### Fixed
- `DbType.Decimal` was mapped to Kusto's `real` (binary floating-point) type instead of `decimal`, which could silently lose precision on decimal parameters.
- Control-command routing (`.show`/`.drop`/etc. vs. a query) is now decided once from the provider's own pristine command text, before any `DbCommandInterceptor` can mutate it — closing a gap where a header-prepending interceptor could cause a control command to be misrouted as a query. Commands created outside the EF Core pipeline (e.g. via `DbConnection.CreateCommand()` directly) keep the original execution-time text-sniffing fallback.
- Unrecognized join expression types now throw `NotSupportedException` instead of silently being translated as a left join.

## [0.2.6]
### Fixed
- Regression for count translation introduced in 0.2.3.

## [0.2.5]
### Added
- Multi-targeting for `net8.0`, `net9.0` and `net10.0`, building against EF Core 8, 9 and 10 respectively. EF Core 8 support is retained unchanged.

### Fixed
- Adapted to the EF Core 9 migrations API: `HistoryRepository`'s database-lock members (a no-op lock, since Kusto has no advisory-lock primitive) and the new `IMigrationCommandExecutor` overloads.
- Adapted to the EF Core 10 `RelationalCommand` `logCommandText` constructor parameter.
- Query-parameter rendering now strips the captured-variable `__` prefix only when present, so translation works on EF Core 10 (which dropped the prefix) as well as EF Core 8/9.

## [0.2.4]
### Added
- Experimental EF Core migrations support: schema operations translate to KQL control commands (`.create-merge table`, `.alter-merge table`, `.drop`, `.rename`), with applied migrations tracked in an `EFMigrationsHistory` table. Non-transactional; `.alter column type=` clears column data; relational-only constructs (indexes, FKs, constraints, sequences) are no-ops.

## [0.2.3]
### Added
- `GroupBy` → KQL `summarize` translation. `Sum`/`Min`/`Max`/`Average`/`Count`/`LongCount`, `Count(predicate)` → `countif`, `Distinct().Count()` → `dcount`. Composite keys, multi-aggregate projections, and aggregate-alias `OrderBy` supported.
- Conditional `?:` translation → `iif` (two-way) and `case` (multi-way), including inside aggregates.

### Fixed
- Parameter substitution now emits proper typed KQL literals (strings, dates, GUIDs, nulls were all broken under raw `ToString()`).

## [0.2.2]
### Fixed
- `KustoQuerySqlGenerator` when the same parameter is used multiple times in a query.

## [0.2.1]
### Added
- Support for Hex strings in byte arrays. 

## [0.2.0]
### Added
- Support for `Any` 

## [0.1.9]
### Added
- Support for OUTER APPLY and CROSS APPLY.

## [0.1.8]
### Fixed
- Inequality comparisons on strings.

## [0.1.7]
### Fixed
- `not` operator translation
- Duplicate column issue in joins

### Added
- Support for `Contains`

## [0.1.6]
### Fixed
- NULL handling in PATCH requests.
- String escaping in PATCH requests.

## [0.1.5]
### Optimized
- `.update` command to use less nesting and support larger batches

## [0.1.4]
### Fixed
- `COUNT(*)` regression resulting from `KustoQuerySqlGenerator.WriteProjection` refactor

## [0.1.3]
### Added
- Support for `DateOnly` type translation

## [0.1.2]
### Added
- Write command batching per entity/table

## [0.1.1]
### Added
- Update support via Kusto `.update table` commands

## [0.1.0]
- Initial release (read-only query support)
