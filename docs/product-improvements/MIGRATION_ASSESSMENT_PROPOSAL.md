# Obsync — Migration Assessment Module Proposal

**Date:** 2026-07-16 · **Status:** Proposal only — no analyzer code ships with this document · **Origin:** `IMPROVEMENT_ASSESSMENT.md` §27 (Migration assessment — P3 · Proposal only) · **Grounded against:** `main` @ `a917680` (v0.8.3 + performance pass)

**Targets covered:** SQL Server → PostgreSQL · SQL Server → AlloyDB for PostgreSQL · SQL Server → BigQuery

---

## 1. Positioning

### 1.1 What this module is

A **Migration Assessment** module answers one question, per database and per target platform: *"What would it take to move this SQL Server database to PostgreSQL, AlloyDB, or BigQuery — and what will fight back?"* It produces an evidence-backed readiness assessment: an inventory of everything the database contains, a compatibility verdict for every data type and language construct it uses, a transparent complexity score, and a set of reports a team can hand to an architect, a migration vendor, or a steering committee.

### 1.2 What this module is not — hard boundaries

These are product commitments, not implementation details:

1. **Assessment and analysis only. Never migration execution.** The module never generates target-platform DDL intended for deployment, never converts code, never moves data, never connects to a target platform. There is no "migrate" button and there never will be *in this module*. If conversion tooling is ever built, it is a different product surface with a different risk profile and a different proposal.
2. **A separate navigation module.** Migration assessment gets its own top-level entry in the app shell, alongside (not inside) Jobs. It is never mixed into sync jobs: an assessment is not a run, does not commit to git, does not touch sync watermarks or tracked-object state, and cannot be scheduled by the sync scheduler in its first versions. The Sync Job stays the one clean concept it is today.
3. **Read-only, using what Obsync already collects.** Every input is either (a) script text and metadata Obsync's existing pipeline already produces, or (b) new catalog queries with the same guarantees as everything else Obsync does: `SELECT` against system views only, no DDL/DML, no `sysadmin`, bounded by the existing query/lock timeouts, honest reporting when a permission is missing. The least-privilege story (`CONNECT` + `VIEW DEFINITION` + `VIEW DATABASE STATE`) must remain sufficient for the core assessment; anything needing more (e.g. msdb Agent metadata) degrades to a reported "not assessed" finding, exactly like the existing Agent-jobs skip behavior.

### 1.3 Why Obsync is positioned to do this

Obsync already holds the two hardest inputs an assessment needs, at VLDB scale:

- **Every module definition**, normalized and deterministic, from the scripting pipeline (`sys.sql_modules` fast path + SMO) — the corpus a T-SQL analyzer runs over, with no new server load for databases already synced.
- **A trusted read-only reputation.** The module inherits "Obsync never writes to your server" — which is precisely the trust level an assessment tool must have to be pointed at production.

The differentiator over generic assessment tools (e.g. cloud vendors' one-shot assessors) is **repeatability with history**: because Obsync versions the source scripts, an assessment can be re-run after each remediation sprint and the score delta is meaningful and auditable.

### 1.4 Honesty requirements (carried from the working rules)

- No claim without evidence: every finding cites the object(s) and construct(s) that produced it.
- No fake completeness: constructs the analyzer cannot see (dynamic SQL contents, application-side SQL, encrypted modules) are reported as **explicit blind spots**, never silently omitted.
- No false precision: no time/cost estimates presented as facts (see §7.5).

---

## 2. Inventory — what the assessment collects

The inventory is the factual foundation. Everything downstream (type matrix, syntax analysis, scoring) is derived from it. Each item below is tagged with its source: **[Pipeline]** = already captured by Obsync's scripting pipeline today; **[Catalog-new]** = requires a new read-only catalog query; **[Partial]** = partially captured, needs extension.

### 2.1 Schema objects

| Item | Source | Notes |
|---|---|---|
| Tables (columns, computed columns, defaults, identity, nullability) | **[Pipeline]** | SMO table scripting + `DatabaseDocumentationReader` (`sys.tables`, `sys.columns`, `sys.types`, `sys.default_constraints`) |
| Data types per column (incl. UDDTs resolved to base types, max length, precision, scale, collation) | **[Partial]** | Column names/types exist in the documentation reader; the assessment needs a dedicated typed reader returning structured rows (not doc text): `sys.columns` × `sys.types`, plus per-column collation and sparse/filestream flags |
| Constraints (PK, FK incl. cascade rules, UNIQUE, CHECK, DEFAULT) | **[Pipeline]** | In SMO table scripts; also structurally via `sys.foreign_keys` (already queried in `SqlServerProbe`), `sys.check_constraints`, `sys.key_constraints` **[Catalog-new]** for structured form |
| Indexes (clustered/nonclustered, filtered, included columns, columnstore, XML/spatial/full-text indexes) | **[Partial]** | Scripted with tables via SMO; structured index metadata for scoring needs `sys.indexes`/`sys.index_columns` (already used for reference-data keys) extended with type/filter/compression columns **[Catalog-new]** |
| Views (incl. indexed views, `WITH SCHEMABINDING`) | **[Pipeline]** | `MetadataScriptProvider` (`sys.sql_modules`) |
| Stored procedures, functions (scalar/inline/TVF/CLR), DML triggers, DDL triggers | **[Pipeline]** | `MetadataScriptProvider`; CLR modules surface as definition-unavailable skips — for the assessment that is itself a finding, not a gap |
| Synonyms, sequences, schemas | **[Pipeline]** | `MetadataScriptProvider` |
| User-defined types (UDDT, table types, CLR types/aggregates), XML schema collections | **[Pipeline]** | `SqlObjectType` already enumerates them; SMO scripts them |
| Partition functions/schemes | **[Pipeline]** | `SqlObjectType.PartitionFunction/PartitionScheme` |
| Security objects (users, roles, permissions, security policies/RLS, Always Encrypted keys) | **[Pipeline]** | Permission artifacts + `SecurityAnalysisReader` + `SqlObjectType.SecurityPolicy` |
| Extended properties (`MS_Description` etc.) | **[Pipeline]** | Documentation reader |

### 2.2 Server- and platform-level features

| Item | Source | Notes |
|---|---|---|
| SQL Agent jobs, operators, alerts | **[Pipeline]** | Server-level scripting pass (needs msdb `SQLAgentReaderRole`; degrade to "not assessed" without it) |
| Linked servers | **[Pipeline]** | Server-level scripting pass (`server/linked-servers/`) |
| Server configuration (`sp_configure`) | **[Pipeline]** | `server/server-configuration.sql` via `DatabaseArtifactReader` (`sys.configurations`) |
| CLR assemblies | **[Pipeline]** | `SqlObjectType.Assembly`; assembly *presence and permission set* is what matters — a target has no CLR host |
| Service Broker (message types, contracts, queues, services, activation procs) | **[Catalog-new]** | `sys.service_queues`, `sys.services`, `sys.service_contracts`, `sys.service_message_types` — presence + counts is sufficient for assessment |
| CDC / Change Tracking | **[Catalog-new]** | `sys.databases.is_cdc_enabled`, `sys.tables.is_tracked_by_cdc`, `sys.change_tracking_databases/_tables` |
| Temporal tables | **[Catalog-new]** | `sys.tables.temporal_type`, history table linkage |
| FILESTREAM / FileTable, full-text catalogs/indexes | **[Partial]** | Full-text catalogs in `SqlObjectType`; FILESTREAM flags via `sys.columns.is_filestream`, `sys.tables.is_filetable` **[Catalog-new]** |
| In-Memory OLTP (memory-optimized tables, natively compiled modules) | **[Catalog-new]** | `sys.tables.is_memory_optimized`, `sys.sql_modules.uses_native_compilation` |
| Replication participation | **[Catalog-new]** | `sys.databases.is_published/is_subscribed/is_merge_published` (presence only; no distributor queries) |
| Database-scoped configuration, compatibility level, collation, containment | **[Partial]** | Database options artifact exists; assessment consumes it structurally |

### 2.3 Size, volume, and dependencies

| Item | Source | Notes |
|---|---|---|
| Database size (data/log, per file) | **[Partial]** | `SqlServerProbe` already reads `sys.master_files`; assessment persists it per run |
| Row counts per table | **[Partial]** | `SqlServerProbe` already reads `sys.partitions` row counts (used for the reference-data picker) — **safe**: catalog metadata, no table scans, approximate by design and labeled as such |
| Largest tables / LOB-heavy tables | **[Catalog-new]** | `sys.dm_db_partition_stats` or `sys.allocation_units` (needs `VIEW DATABASE STATE` — already required) |
| Object dependencies | **[Partial]** | `sys.sql_expression_dependencies` one-level Uses/Used-by already shipped (`SqlServerProbe`); assessment needs the **full edge list** persisted (all edges, one query, no per-object round trips) to compute cross-database references, dependency depth, and cluster sizes |
| Cross-database and cross-server (linked-server) references | **[Catalog-new]** | `sys.sql_expression_dependencies.referenced_database_name / referenced_server_name` + syntax analysis (§4) as a second detector |

### 2.4 Explicit non-collection

The assessment never samples table data, never reads user rows, and never executes user code. Row counts and sizes come from catalog metadata only. This keeps the module inside the existing security promise and avoids any data-privacy conversation.

---

## 3. Data-type compatibility matrix

### 3.1 Model

Every column, parameter, and return type in the inventory is mapped against each target and assigned exactly one **mapping class**:

| Class | Meaning |
|---|---|
| **Direct** | Same semantics, lossless, mechanical rename at most |
| **Mapping with caveat** | A standard mapping exists; behavior differs in a documented, usually tolerable way |
| **Precision risk** | Mapping exists but numeric precision/scale/rounding can silently change values |
| **Length risk** | Mapping exists but length limits differ; data may not fit or padding semantics change |
| **Unicode risk** | Encoding/collation semantics differ (SQL Server UTF-16/collations vs UTF-8 targets); comparisons, sorts, or lengths can change |
| **Time-zone risk** | Temporal semantics differ (offset preservation, implicit UTC conversion, range) |
| **Semantic difference** | A "same-named" or obvious mapping behaves differently in ways that break logic (comparisons, defaults, functions over the type) |
| **Unsupported** | No native equivalent; requires an extension, emulation, or dropping the feature |
| **Manual redesign** | No mapping is honest; the schema or application must change |

A column can carry findings from multiple classes (e.g. `NVARCHAR(4000)` → BigQuery is Direct for storage but Unicode-risk for collation-dependent comparisons); the matrix records the **worst class** as the verdict and all findings as evidence.

### 3.2 Representative worked rows

The full matrix covers every scriptable SQL Server type (~35 rows). The table below shows the genuinely tricky rows worked in full plus enough ordinary rows to establish the pattern. PG = PostgreSQL; AlloyDB inherits every PG verdict (§6.2) — it is listed only where it differs (it does not, for types); BQ = BigQuery.

| SQL Server type | → PostgreSQL | → BigQuery | Notes / evidence the report must carry |
|---|---|---|---|
| `BIGINT` | **Direct** → `bigint` | **Direct** → `INT64` | — |
| `INT`, `SMALLINT` | **Direct** → `integer`/`smallint` | **Mapping with caveat** → `INT64` (widened; range checks lost) | BQ has one integer type |
| `TINYINT` | **Mapping with caveat** → `smallint` (PG has no unsigned 1-byte type; range 0–255 fits) | **Mapping with caveat** → `INT64` | Flag CHECK-constraint suggestion in findings, not DDL |
| `BIT` | **Semantic difference** → `boolean` | **Semantic difference** → `BOOL` | T-SQL treats BIT as 0/1 integer in expressions (`SUM(bit)`, `bit = 1`); PG/BQ booleans reject arithmetic and integer comparison — every expression touching the column needs review |
| `DECIMAL/NUMERIC(p,s)` | **Direct** → `numeric(p,s)` | **Precision risk** → `NUMERIC` (38,9 fixed) or `BIGNUMERIC` | BQ `NUMERIC` caps scale at 9; `DECIMAL(20,10)` silently needs `BIGNUMERIC`; report per-column p/s vs target limits |
| `MONEY` / `SMALLMONEY` | **Precision risk** → `numeric(19,4)` (never PG `money` — locale-dependent, no arithmetic precision guarantees) | **Precision risk** → `NUMERIC` | MONEY is exactly 4 decimal places with *truncating* division semantics in T-SQL money-money ops; code doing money division can change results. Findings must list expressions dividing MONEY columns (from §4) |
| `FLOAT(n)` / `REAL` | **Direct** → `double precision`/`real` | **Mapping with caveat** → `FLOAT64` (REAL widened) | — |
| `DATETIME` | **Precision risk** → `timestamp(3)` | **Precision risk** → `DATETIME` | DATETIME rounds to 3.33 ms ticks (values end .000/.003/.007); targets store true milliseconds — equality joins/comparisons against re-inserted data can stop matching, and range boundaries like `< '…23:59:59.997'` are idiomatic SQL Server that must be found (§4) and rewritten. Range floor 1753-01-01 disappears (benign) |
| `DATETIME2(n)` | **Direct** → `timestamp(n)` (PG max precision 6 vs 7 — **Precision risk** only when n=7) | **Mapping with caveat** → `DATETIME` (µs precision; n=7 loses 100 ns digit) | Per-column: verdict depends on declared n |
| `SMALLDATETIME` | **Mapping with caveat** → `timestamp(0)` | **Mapping with caveat** → `DATETIME` | Minute precision + rounding *up* at ≥29.998 s is an SQL Server quirk that disappears |
| `DATETIMEOFFSET` | **Time-zone risk** → `timestamptz` | **Time-zone risk** → `TIMESTAMP` | Both targets **normalize to UTC and discard the original offset**; PG `timestamptz` and BQ `TIMESTAMP` are instants, not (instant, offset) pairs. Applications reading the offset back (e.g. displaying local time of capture) need a companion offset column — **Manual redesign** when §4 finds `SWITCHOFFSET`/`DATEPART(tz, …)` usage |
| `DATE`, `TIME(n)` | **Direct** → `date`/`time(n)` | **Direct** → `DATE`/`TIME` (µs) | TIME(7): same 100 ns note as DATETIME2 |
| `CHAR/VARCHAR(n)` | **Unicode risk** → `char/varchar(n)` | **Unicode risk** → `STRING` | n is *bytes* in SQL Server vs *characters* in PG/BQ; non-UTF-8-trivial collations (case-insensitive, accent-insensitive) are the real hazard: PG default collations are case-sensitive — every `WHERE name = @p` under a CI collation changes behavior. Report per-database + per-column collation and flag CI/AI collations explicitly |
| `NCHAR/NVARCHAR(n)` | **Mapping with caveat** → `varchar(n)` (UTF-8 covers the data) + the same collation caveat | **Mapping with caveat** → `STRING` | `NVARCHAR(MAX)` → PG `text` / BQ `STRING`: **Direct-with-caveat** |
| `TEXT` / `NTEXT` / `IMAGE` | **Mapping with caveat** → `text`/`bytea` | **Mapping with caveat** → `STRING`/`BYTES` | Deprecated since 2005; the *type* maps fine but code using `WRITETEXT/READTEXT/UPDATETEXT/TEXTPTR` (§4 detector) is **Unsupported** on both targets. Their presence is also a modernization signal fed to scoring |
| `BINARY/VARBINARY(n/MAX)` | **Direct** → `bytea` | **Direct** → `BYTES` | — |
| `UNIQUEIDENTIFIER` | **Mapping with caveat** → `uuid` | **Semantic difference** → `STRING(36)` | PG: type maps, but **sort order differs** (SQL Server sorts GUIDs by a byte-group order unlike memcmp) — any ORDER BY/range logic over GUIDs changes; `NEWSEQUENTIALID()` has no equivalent. BQ: no UUID type; joins work as strings but storage/ordering differ |
| `ROWVERSION` / `TIMESTAMP` | **Manual redesign** | **Manual redesign** | It is not a time type — it is an auto-incrementing binary(8) used for optimistic concurrency. PG idiom is `xmin` or a trigger-maintained version column; BQ has no row versioning at all. Every column found triggers a finding on the *application's* concurrency strategy |
| `SQL_VARIANT` | **Unsupported** → no PG equivalent; redesign to `text`/`jsonb` + type tag | **Unsupported** | Also flag every `SQL_VARIANT_PROPERTY` call (§4). Common in generic audit tables — pair the finding with the owning table's purpose |
| `HIERARCHYID` | **Unsupported** natively; **Mapping with caveat** if the `ltree` extension is acceptable (AlloyDB supports `ltree`) — methods (`GetAncestor`, `IsDescendantOf`) all rewrite | **Manual redesign** → materialized path `STRING` | CLR type: its column data is opaque bytes; every method call found in code is a separate finding |
| `GEOGRAPHY` / `GEOMETRY` | **Mapping with caveat** → PostGIS (`geography`/`geometry`) — the *types* map well; the ~400 methods map case-by-case, most have PostGIS equivalents with different names/signatures. AlloyDB: PostGIS supported | **Semantic difference** → `GEOGRAPHY` (BQ has geography only, spherical, no `GEOMETRY` planar type; SRID handling differs) | Verdict is per-*usage*: a stored-only spatial column is caveat-level; heavy method usage in code escalates to Major rewrite in §5 |
| `XML` | **Mapping with caveat** → `xml` (PG type exists; **XQuery/`.nodes()`/`.value()` methods do not** — rewrite to `xpath()`/`xmltable`) | **Manual redesign** → `STRING` (no XML functions) | Typed XML (schema collections) is Unsupported on both. Every XML method call is enumerated by §4 |
| `JSON` (2025+ native type / `NVARCHAR` + `ISJSON`) | **Mapping with caveat** → `jsonb` (function surface differs: `JSON_VALUE/JSON_QUERY/OPENJSON` → `->>`/`jsonb_path_query`) | **Mapping with caveat** → `JSON` | — |
| `CURSOR`, `TABLE` (variables/UDTT as parameter types) | **Manual redesign** (PG: refcursor exists but TVP call patterns rewrite to arrays/temp tables) | **Manual redesign** | Assessed via §4/§5, reported here for completeness |

**Rule for generating the remaining rows.** Every remaining base type gets a row derived mechanically: (1) start from the authoritative vendor mapping (PostgreSQL documentation / Google's SQL Server-to-BigQuery mapping); (2) assign the *most severe honest class* — when in doubt between Direct and a risk class, choose the risk class and say why; (3) attach the catalog predicate that finds affected columns; (4) attach the list of T-SQL functions/operators over that type whose behavior differs, so the syntax analyzer (§4) can cross-reference. A row is only shippable when it has a fixture-database test proving the detector finds a planted column of that type (§9.3). UDDTs are resolved to their base type and reported under it, with the alias name preserved in evidence.

---

## 4. T-SQL syntax analysis

### 4.1 Corpus

The analyzer runs over **script text Obsync has already produced**: every module definition from the pipeline (procedures, functions, views, triggers, DDL triggers), plus table/constraint scripts (for computed-column expressions, CHECK/DEFAULT expressions) and SQL Agent job step commands from the server-level pass. No new server round trips are needed for databases already synced; for a fresh assessment the existing scripting pipeline is invoked in export mode (full snapshot, no git).

### 4.2 Approach: tokenizer + pattern rules — not a full parser

A full T-SQL grammar is a multi-year liability. The proposal is a **T-SQL-aware tokenizer** (correct handling of comments, string literals, quoted/bracketed identifiers, batch separators — so rules never fire inside a comment or string) with a **declarative rule catalog** on top: each rule matches token patterns and emits findings with object, line, matched text, target verdicts, and remediation notes. This is the same honesty trade-off SQL Server's own upgrade advisors make, and it is testable rule-by-rule.

What a tokenizer-based approach **cannot** do, stated up front in every report (§8.11): resolve names through dynamic SQL, evaluate conditional branches, or see SQL that lives in the application. Detection of *dynamic SQL itself* is therefore a first-class rule (below), and any object containing `EXEC(@sql)` / `sp_executesql` with non-literal input is capped at **confidence: partial** — the analyzer reports what it can see and explicitly reports that it cannot see the rest.

### 4.3 Detection catalog (initial rule set)

| # | Construct | Detection sketch | Why it matters (per target) |
|---|---|---|---|
| 1 | `TOP (n)` / `TOP n PERCENT` / `TOP … WITH TIES` | keyword pattern after SELECT/UPDATE/DELETE | PG: `LIMIT`/`FETCH FIRST … WITH TIES`; PERCENT has no direct form. TOP in UPDATE/DELETE has no PG/BQ equivalent (rewrite via CTE) |
| 2 | `CROSS/OUTER APPLY` | keyword pair | PG: `LATERAL` join (mechanical-ish); BQ: correlated `UNNEST`/rewrite, often structural |
| 3 | `PIVOT` / `UNPIVOT` | keyword | PG: `crosstab`/conditional aggregation; BQ: has native `PIVOT/UNPIVOT` (Direct-with-caveat) |
| 4 | `MERGE` | keyword at statement start | PG 15+: `MERGE` exists with differences (no `OUTPUT`, different match semantics); BQ: `MERGE` supported (one of the few BQ conveniences) — but see DML quotas §6.3 |
| 5 | `OUTPUT` clause | keyword after INSERT/UPDATE/DELETE/MERGE | PG: `RETURNING` (single-table, no `deleted.`/`inserted.` pseudo-tables in the same way); BQ: unsupported |
| 6 | `RAISERROR` / `THROW` | keyword | PG: `RAISE` in PL/pgSQL — severity/state semantics differ, `WITH NOWAIT` (progress messaging) has no equivalent; BQ scripting: `RAISE USING MESSAGE` |
| 7 | `TRY…CATCH`, `ERROR_*()`, `XACT_ABORT`, `XACT_STATE()` | keywords | PG: `BEGIN…EXCEPTION` blocks (subtransaction cost caveat); BQ: `BEGIN…EXCEPTION WHEN ERROR` (limited) |
| 8 | Table variables `DECLARE @t TABLE` | pattern | PG: temp tables / arrays; BQ scripting: temp tables with quota caveats |
| 9 | Temp tables `#t`, `##t`, `tempdb..` | token prefix | PG: `CREATE TEMP TABLE` (per-session, mostly fine — global `##` is not); BQ: `CREATE TEMP TABLE` inside scripts only |
| 10 | Cursors (`DECLARE … CURSOR`, `FETCH`, `@@FETCH_STATUS`) | keywords | PG: cursors exist, usually rewritten to set-based/`FOR` loops; BQ: **no cursors** — loops over query results only; heavy cursor use is a §5 class driver |
| 11 | Dynamic SQL (`EXEC(@s)`, `sp_executesql`) | call pattern | Both: the *construct* ports, the *contents* are invisible — emits a finding **and** a blind-spot marker that caps object confidence |
| 12 | `EXECUTE AS` (impersonation, module clause) | keyword | PG: `SECURITY DEFINER` approximates the module clause; statement-level `EXECUTE AS USER` has no equivalent; BQ: no equivalent (IAM model) |
| 13 | `OPENQUERY` / `OPENROWSET` / `OPENDATASOURCE` | function names | PG: `postgres_fdw`/`dblink` redesign; BQ: `EXTERNAL_QUERY` / external tables — always at least Minor rewrite |
| 14 | Cross-database references `db.schema.object` | 3–4-part name resolution against the inventory's database list, cross-checked with `sys.sql_expression_dependencies.referenced_database_name` | PG: schemas-within-one-database redesign or FDW; BQ: dataset-qualified names (mapping decision needed). One of the highest-weight scoring inputs (§7) |
| 15 | Linked-server references (4-part names) | 4-part name pattern + `sys.sql_expression_dependencies.referenced_server_name` + linked-server inventory | Both targets: architecture redesign (FDW / EXTERNAL_QUERY / pipelines) |
| 16 | Query hints (`OPTION(…)`), table hints (`WITH (NOLOCK/HOLDLOCK/…)`), index hints, `FORCESEEK` | hint syntax | PG/BQ: no hint system (PG deliberately). `NOLOCK` gets its own rule — ubiquitous, and its *intent* (dirty reads for perf) is simply inapplicable under PG MVCC / BQ snapshots: usually deletable, but flags a team culture of blocking problems worth a report note |
| 17 | Identity/rowcount functions `@@IDENTITY`, `SCOPE_IDENTITY()`, `IDENT_CURRENT`, `@@ROWCOUNT` | function names | PG: `lastval()`/`RETURNING`/`GET DIAGNOSTICS`; BQ: no identity concept. `@@IDENTITY` vs `SCOPE_IDENTITY` mismatches are also a latent-bug finding |
| 18 | System functions with divergent equivalents: `GETDATE`/`SYSDATETIME`/`GETUTCDATE`, `ISNULL`, `LEN` (trailing-space semantics!), `CHARINDEX`, `PATINDEX`, `STUFF`, `FORMAT`, `CONVERT` with style codes, `DATEADD/DATEDIFF/DATEPART` (datepart tokens), `IIF`, `CHOOSE`, `NEWID`, `HOST_NAME`, `SUSER_SNAME`, `DB_NAME`, `OBJECT_NAME`, `@@SERVERNAME`, `@@TRANCOUNT` | function-name catalog with per-function target mapping table | Each function row carries its own class per target (e.g. `DATEDIFF` counts *boundaries crossed*, not elapsed intervals — a classic silent-behavior change vs PG `age()`/subtraction) |
| 19 | Deprecated LOB ops `WRITETEXT`/`READTEXT`/`TEXTPTR` | keywords | Unsupported everywhere (pairs with TEXT/NTEXT/IMAGE type findings) |
| 20 | Transaction control in modules (`BEGIN/COMMIT/ROLLBACK TRAN`, savepoints, named transactions) | keywords | PG functions cannot control transactions (procedures can, with limits); BQ: multi-statement transactions are heavily restricted (§6.3) |
| 21 | Service Broker verbs (`SEND ON CONVERSATION`, `RECEIVE`, `WAITFOR (RECEIVE…)`), `WAITFOR DELAY` | keywords | Unsupported on both — architecture finding |
| 22 | CLR usage in code (references to CLR UDTs/UDFs/aggregates, resolved via inventory) | identifier cross-reference | Unsupported on both — each referenced CLR object is a redesign finding |

The catalog is extensible by design (a rule = pattern + metadata + tests); the list above is the shipping bar for the first full version, chosen to cover what drives real migration effort.

### 4.4 Output

Per object: a finding list (rule, location, snippet, per-target verdict) + a **confidence marker** (full / partial-due-to-dynamic-SQL). Per database: construct frequency table feeding §5 classification and §7 scoring. All findings carry evidence — no aggregate number appears anywhere without a drill-down to the objects behind it (§7.4).

---

## 5. Procedural-code classification

Every module (procedure, function, trigger) receives one class per target, derived mechanically from its §4 findings and §3 type usage. The classes and their driving signals:

| Class | Driving signals |
|---|---|
| **Likely portable** | Set-based SELECT/INSERT/UPDATE/DELETE only; ANSI joins; no findings beyond Direct-class functions; no temp objects; no transaction control; types all Direct |
| **Automated conversion candidate** | Only mechanical-rewrite findings: `TOP`→`LIMIT`, `ISNULL`→`COALESCE`, `GETDATE()`→`now()`, `+` string concat, bracket identifiers, simple `RAISERROR`→`RAISE`. (Named honestly: *candidate* — this module ships no converter; the class means "a conversion tool or a competent engineer converts this mechanically") |
| **Minor manual rewrite** | Bounded, local rewrites: single `APPLY`→`LATERAL`, `OUTPUT`→`RETURNING`, `@@ROWCOUNT`→`GET DIAGNOSTICS`, temp-table DDL differences, `TRY/CATCH`→`EXCEPTION` block, one `MERGE` |
| **Major manual rewrite** | Structural work: cursor loops that should become set-based; transaction control inside modules; dynamic SQL building object names; `EXECUTE AS`; PIVOT with dynamic column lists; heavy `DATEDIFF`-boundary or MONEY-arithmetic logic where *behavior*, not syntax, must be re-verified |
| **Unsupported design** | Depends on a feature the target lacks outright: Service Broker verbs, CLR references, linked-server queries (without an approved federation design), `WAITFOR`, global temp tables as IPC, typed-XML methods; on BigQuery additionally: row-by-row OLTP patterns, high-frequency single-row DML, cursors |
| **Requires application redesign** | The database code is load-bearing for application architecture: ROWVERSION-based optimistic concurrency, `SCOPE_IDENTITY` contracts with the app, cross-database transactions, impersonation-based security flows, SQL-Agent-orchestrated business processes — the finding names the application contract that breaks |

Classification rules: an object's class is the **worst class any finding assigns**, with counts shown (an object with 40 mechanical findings and 1 unsupported finding is Unsupported-design, and the report says "1 finding drives this"). Objects with dynamic SQL are annotated *"≥ this class — dynamic SQL contents not analyzable"* — the class is a floor, not a verdict. Encrypted modules are classified **Not assessable** (their existence is already surfaced by the pipeline's skip reporting) and counted separately, never bucketed optimistically.

---

## 6. Target feature-compatibility matrices

### 6.1 PostgreSQL

| SQL Server feature | PostgreSQL story |
|---|---|
| Transactions / isolation | Full MVCC; `READ COMMITTED` default similar in spirit; no lock hints; `READ UNCOMMITTED` behaves as READ COMMITTED (NOLOCK culture is moot) |
| Procedures/functions | PL/pgSQL is capable but different: functions can't manage transactions; procedures (11+) can; no `PRINT`/`RAISERROR WITH NOWAIT` progress idiom |
| Referential integrity | Full, enforced; deferrable constraints (a capability SQL Server lacks) |
| Indexing | B-tree/GIN/GiST/BRIN/partial/expression; no included-columns-with-key ordering nuances of SQL Server covering indexes (INCLUDE exists 11+); filtered indexes map to partial indexes well; no columnstore in vanilla PG (see AlloyDB) |
| Identity/sequences | `GENERATED AS IDENTITY` + sequences — good mapping; `NEWSEQUENTIALID` absent |
| Partitioning | Declarative partitioning; partition *functions/schemes* as separate objects don't exist — restructure |
| Security | Roles/grants map structurally; `EXECUTE AS` → `SECURITY DEFINER` partially; RLS exists (different predicate model); Always Encrypted has no equivalent (client-side crypto redesign); TDE not in vanilla PG |
| SQL Agent | None — external scheduling (cron, pg_cron where available) |
| Linked servers | `postgres_fdw` and other FDWs — different model, per-table foreign tables rather than ad-hoc 4-part queries |
| CDC / replication | Logical replication/decoding covers CDC-like needs differently; no built-in temporal tables (triggers/extensions) |
| Full-text | `tsvector`/`tsquery` — capable, different query language; CONTAINS/FREETEXT rewrite |
| Service Broker / CLR / SQL_VARIANT / HIERARCHYID (native) | None — redesign findings (§3/§4) |

### 6.2 AlloyDB for PostgreSQL

AlloyDB **is** PostgreSQL for compatibility purposes: **there is no meaningful T-SQL-conversion delta versus vanilla PG** — every verdict in §3–§5 and §6.1 applies unchanged. The assessment treats AlloyDB as *PostgreSQL plus a different operational and performance posture*, and only these deltas are assessed separately:

- **Columnar engine**: an in-memory columnar accelerator over row storage. Assessment relevance: databases whose SQL Server usage leans on columnstore indexes or heavy analytical scans get a *better* target-suitability note on AlloyDB than vanilla PG, without schema changes.
- **Managed HA / storage**: regional availability, automated failover, and a disaggregated storage layer replace the Always On / FCI conversation — an operational-complexity note, not a code finding.
- **Extension surface**: a large but *curated* extension list (PostGIS, `ltree`, `pg_cron` etc. available; anything requiring superuser or unlisted extensions is not) — the matrix marks any PG verdict that relied on an extension with its AlloyDB availability.
- **No self-managed escape hatches**: no filesystem access, no custom C extensions — relevant only when a PG remediation plan would have used one.

Report-wise, AlloyDB is a column next to PostgreSQL sharing all code findings, with a short operational-delta section — never a duplicated matrix.

### 6.3 BigQuery — an analytics target, not a drop-in OLTP replacement

The assessment must say this in bold at the top of every BigQuery section: **BigQuery is a serverless analytics warehouse. Assessing an OLTP SQL Server database against BigQuery is usually assessing a re-architecture, not a migration.** The target-suitability dimension (§7) exists largely to make this conclusion loud when the source is OLTP-shaped, instead of burying it under 400 syntax findings.

| Area | BigQuery reality |
|---|---|
| Transactions | Multi-statement transactions exist but are limited (session-scoped, no long-lived interactive transactions, restricted concurrent mutations per table); no OLTP-style concurrency. High-frequency transactional code is **Requires application redesign**, full stop |
| Procedures / scripting | SQL scripting with procedures, `BEGIN…EXCEPTION`, loops — but no cursors, no triggers at all, quotas on statement counts; trigger-based logic must move to pipelines |
| Referential integrity | PK/FK are **declared but unenforced** (metadata for the optimizer). Every FK in the inventory becomes a finding: "integrity moves to the ingestion pipeline / application" |
| Indexing | **No indexes.** Clustering + partitioning replace them. The assessment maps each clustered/covering-index *intent* to a clustering/partitioning recommendation and marks index-hint-dependent code Unsupported. Search indexes exist for point lookup on strings — a caveat, not an index system |
| DML patterns | DML is set-oriented with **table mutation quotas**; the streaming/`Storage Write` path is for ingestion, not `UPDATE`. Single-row UPDATE/DELETE loops (cursor patterns) are Unsupported-design |
| Concurrency | Snapshot-based; jobs queue rather than lock; no blocking model to tune — and no lock-based semantics to port |
| SQL Agent replacement | Scheduled queries for simple cases; Cloud Scheduler / Cloud Composer (Airflow) for real orchestration — every Agent job in the inventory maps to a "reimplement as…" finding |
| Linked servers / cross-DB | `EXTERNAL_QUERY` (Cloud SQL/AlloyDB federation), external tables/BigLake for lake data, cross-*dataset* queries are native. 4-part-name code rewrites regardless |
| Security model | IAM + dataset/table/row/column-level security; database users/roles do not map 1:1 — a security-redesign dimension input, especially where `EXECUTE AS` or ownership chaining is load-bearing |
| CDC / replication | Datastream (CDC *into* BQ) is a migration aid, not a feature parity answer; SQL Server CDC consumers must be re-pointed |
| Query semantics | GoogleSQL is close to standard but: default case-sensitive string comparison (vs typical CI collations — Unicode-risk amplifier), different NULL-ordering defaults, no `SELECT … FOR UPDATE`, timezone handling via explicit functions |
| Cost model | On-demand bytes-scanned / capacity slots — "performance redesign risk" includes *cost* redesign: a pattern that is cheap on SQL Server (indexed point lookup) can be expensive per-query on BQ |

### 6.4 Matrix mechanics

Feature verdicts use the same class vocabulary as §3 where applicable (Direct / caveat / Unsupported / Manual redesign) so scoring can consume both uniformly. Each row carries: SQL Server feature, detection source (inventory field or rule #), per-target verdict, and remediation direction (one sentence, no effort claims).

---

## 7. Migration scoring model

### 7.1 Dimensions

Eleven dimensions, each scored 0–100 (0 = no friction, 100 = maximal friction) from countable evidence:

| # | Dimension | Primary inputs |
|---|---|---|
| 1 | Schema complexity | object counts by type, constraint density, index sophistication (filtered/columnstore/XML/spatial), partitioning, computed columns |
| 2 | Data-type complexity | column counts per §3 mapping class, weighted by class severity; distinct problem types |
| 3 | Procedural-code complexity | module counts per §5 class, total findings, dynamic-SQL blind-spot ratio |
| 4 | SQL Server-specific feature usage | CLR, Service Broker, CDC/CT, temporal, FILESTREAM, In-Memory OLTP, full-text, replication, typed XML — presence and spread |
| 5 | Dependency complexity | dependency edge count, max chain depth, cross-database reference count, cycle count |
| 6 | Data volume | database size, row counts, LOB-heavy table count (migration *mechanics* friction, not code) |
| 7 | Operational complexity | Agent jobs/operators/alerts, linked servers, maintenance-plan-style jobs, alerting integrations |
| 8 | Performance redesign risk | hint usage, index-hint dependence, NOLOCK density, covering-index patterns; for BQ: OLTP access patterns and cost-model exposure |
| 9 | Application coupling | identity-function contracts, ROWVERSION concurrency, OUTPUT-clause reliance, exception-contract usage (`ERROR_NUMBER` values), impersonation flows |
| 10 | Security redesign | Always Encrypted, RLS policies, `EXECUTE AS` density, ownership chaining, cross-DB permission grants; for BQ: role model distance |
| 11 | Target suitability | workload-shape signals vs target intent: for BQ, OLTP shape (many small transactions-oriented modules, high trigger count, FK density) scores high friction; for PG/AlloyDB, mostly neutral; AlloyDB gets analytical-workload credit (§6.2) |

### 7.2 Formula — transparent and boring by design

Per target:

```
TargetScore = Σ (weight_i × dimension_i) / Σ weight_i          (0–100, higher = harder)
```

Default weights (per target, visible and adjustable in the UI, always printed in the report):

- PostgreSQL / AlloyDB: procedural-code 20, data-type 15, SQL-Server-specific features 15, dependencies 10, schema 10, application coupling 10, security 5, operational 5, performance 5, volume 5, target suitability 0 (informational).
- BigQuery: target suitability 25 (it must be able to dominate), procedural-code 15, application coupling 15, SQL-Server-specific 10, data-type 10, performance/cost 10, dependencies 5, schema 5, operational 5, volume 0 (BQ ingests volume well) — suitability is also reported stand-alone, not only blended.

No dimension is ever computed from another dimension (no double counting), and each dimension documents its own 0/50/100 anchor examples in the report appendix.

### 7.3 Ranges and confidence, not points

The headline is a **band**, never a number alone: **Low (0–25) · Moderate (26–50) · Substantial (51–75) · Major (76–100)**, plus a **confidence level**:

- **High** — < 2 % of modules carry dynamic-SQL/encrypted blind-spot markers, all inventory collectors ran.
- **Medium** — blind spots 2–15 %, or one collector degraded (e.g. no msdb access).
- **Low** — blind spots > 15 %, or size/dependency collectors unavailable. A Low-confidence score renders with the caveat *before* the band.

### 7.4 Explainability — the non-negotiable requirement

**Every score must decompose, on click, into the findings that produced it.** Concretely: TargetScore → per-dimension contributions (weight × value, shown as a bar) → each dimension value → the counted findings → each finding → the object and matched construct. No number in the module — headline, dimension, count — may exist without this drill-down path. A score that cannot explain itself is a bug, with the same severity the sync engine gives to silent data loss. This is also the acceptance test for the scoring engine: the UI test walks a score to a specific object and construct.

### 7.5 Forbidden outputs

The module never emits duration or cost estimates ("six weeks", "3 FTE-months", "$40k"). Migration duration is dominated by variables the tool cannot see: team skill, application code, testing depth, data-pipeline cutover, organizational latency. The report's vocabulary for effort is the band + the finding counts + the §5 class distribution — an experienced reader converts those to a plan; the tool pretending to do so would be false precision and is explicitly out of scope. (Effort language appears exactly once, about building the module itself — §9.4 — where we control the variables.)

---

## 8. Reports

All reports reuse the existing report infrastructure and conventions: self-contained HTML (no external assets, prints fine), CSV (one row per finding/object for pipelines and Excel), JSON (full structured output for tooling), generated on demand, secret-free, deterministic given the same assessment snapshot, with the same version/date headers as run reports. Rendering extends `RunReportWriter`/`RunReportExport` patterns in `src/Obsync.App/Services/` rather than introducing a new engine (the PDF rejection from IMPROVEMENT_ASSESSMENT.md §26 stands).

| # | Report | Audience | Contents |
|---|---|---|---|
| 1 | Executive summary | Sponsors | Per-target band + confidence, top 10 blockers, suitability statement (esp. the BQ OLTP warning), inventory headline counts — two pages, zero jargon beyond the class names |
| 2 | Technical assessment | Architects | Full dimension breakdown per target with the §7.4 drill-down rendered as expandable sections |
| 3 | Object compatibility | DBAs/devs | Every object × target with §5 class and finding count; filterable CSV is the primary format |
| 4 | Data-type report | DBAs | §3 matrix instantiated: every affected column, grouped by mapping class, with per-column evidence |
| 5 | Unsupported features | Architects | Everything classed Unsupported/Manual redesign, grouped by feature, with the objects that use it |
| 6 | Code conversion findings | Devs | All §4 findings with snippets and per-target remediation direction; per-object confidence markers |
| 7 | Dependency report | Architects | Edge list, depth/cluster metrics, cross-database and linked-server reference map |
| 8 | Risks | Steering | Findings re-cut by risk: silent-behavior changes (DATETIME rounding, DATEDIFF boundaries, CI-collation comparisons, MONEY arithmetic, BIT semantics) ranked above loud breakage — loud breakage gets found in testing; silent changes reach production |
| 9 | Target suitability | Sponsors/architects | The §7 dimension 11 story per target, incl. AlloyDB-vs-PG operational delta and the BQ analytics-target framing |
| 10 | Next steps | Everyone | Ordered remediation themes derived from findings (e.g. "eliminate linked-server references (12 objects)"), each linked to its evidence — directions, never dated plans |
| 11 | Assumptions & limitations | Everyone, mandatory in every export | Blind-spot inventory (dynamic SQL %, encrypted modules, application-side SQL not visible, collectors that degraded), rule-catalog version, snapshot timestamp |

Report 11 is appended to every other report automatically — an assessment export can never omit its own limitations.

---

## 9. Phasing, proof of concept, and the shipping bar

### 9.1 Proof of concept (first milestone, deliberately narrow)

**PoC = inventory + data-type matrix, PostgreSQL only, built on top of existing scripted output.** Specifically: the structured column/type reader (§2.1), the PG column of the §3 matrix with its detectors, and reports 1 (skeleton), 4, and 11 in HTML/CSV/JSON. No syntax analysis, no scoring, no BigQuery/AlloyDB, no new nav polish beyond a minimal page. The PoC exists to validate the two riskiest assumptions cheaply: that the pipeline's output plus a handful of catalog queries genuinely suffices as input, and that class-based verdicts with evidence drill-down are what users actually want. If the PoC's verdicts aren't trusted by a pilot user on a real database, the syntax analyzer never gets built on top of them.

### 9.2 Phases

| Phase | Scope | Exit criterion |
|---|---|---|
| P0 — PoC | As §9.1 | A pilot database's PG type report validated by an external reviewer against manual analysis; zero false "Direct" verdicts |
| P1 — Full inventory + matrix | All §2 collectors (with degradation paths), all §3 rows, all three targets' type columns, reports 4/5/11 complete | Fixture-database suite green (§9.3); AlloyDB delta rendering |
| P2 — Syntax analysis | Tokenizer + rule catalog §4.3, §5 classification, reports 3/6, confidence markers | Every rule has planted-fixture tests incl. non-firing tests (comments/strings); dynamic-SQL blind-spot accounting verified |
| P3 — Scoring + full reporting | §7 model, §7.4 drill-down UI, reports 1/2/7/8/9/10 | Explainability acceptance test (score → object walk) green; weight transparency in every export |
| P4 — Re-assessment history | Persisted assessments, score deltas across runs, remediation tracking | Deterministic re-run on unchanged database yields identical output byte-for-byte |

Each phase ships independently and is useful alone; there is no big-bang release.

### 9.3 The expertise and testing bar (the reason this is a proposal, not a build)

This module makes *claims about other people's migrations*. A wrong "Direct" verdict is the migration-assessment equivalent of the sync engine losing data. Before any phase ships:

- **Dedicated rules with dedicated fixtures.** Every matrix row and every syntax rule requires a fixture database (or fixture script corpus) containing a planted instance of the construct, plus negative fixtures (the construct inside comments, strings, and near-miss identifiers). A rule without a fixture does not merge — same discipline as the existing 561-test + E2E bar.
- **Cross-verification against authority.** Type-matrix verdicts are sourced to vendor documentation (PostgreSQL docs, Google's official migration mapping) and at least one verdict-per-class validated empirically on a live PG/BQ instance during development (dev-time validation; the shipped module still never connects to targets).
- **External review.** At least one person with real SQL Server→PostgreSQL migration experience and one with BigQuery warehouse experience review the matrix and rule catalog before P1/P2 ship. The project must acquire or contract this expertise; shipping without it would produce exactly the shallow analyzer the improvement assessment rejected.
- **Version-stamped rule catalog.** Every report names the catalog version that produced it, so a verdict can be traced to the rule text that made it.

### 9.4 Effort ranges (for building the module — with confidence, not dates)

Relative to recent Obsync feature work (the reporting stack, the security-review reader):

| Phase | Effort band | Confidence | Dominant risk |
|---|---|---|---|
| P0 PoC | M | High | none novel — catalog queries + report rendering, both well-trodden here |
| P1 | M–L | High | breadth (collector count), not difficulty |
| P2 | L–XL | **Medium** | the tokenizer must be genuinely correct about comments/strings/brackets, and the rule catalog is where the domain expertise bar (§9.3) bites |
| P3 | M | Medium | UI drill-down work; the model itself is arithmetic |
| P4 | S–M | High | persistence + diffing, patterns exist |

These are bands with confidence, per §7.5's own standard — no date commitments. The overall sequencing recommendation stands as in the improvement assessment: this remains **P3** behind the v0.9.0 P0/P1 work, and P0 (PoC) should only start once a pilot user with a real migration question exists — building this without one would be speculation.

---

*This proposal ships as documentation only. No analyzer, collector, or UI code accompanies it.*
