# MMR Retrieval Diversity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace plain top-k retrieval with MMR (maximal marginal relevance) diversity selection so versioned-URL near-clone chunks no longer flood the top-k (issue #6).

**Architecture:** `SqliteVectorStore.TopK` becomes `Pool(query)` returning every scored chunk with its vector retained; `Retriever` gains a pure static `MmrSelect` (λ = 0.7) that picks the set by marginal relevance, then numbers citations relevance-descending. Pool = whole scored set (eng review D1); MMR decides set membership, relevance decides order (D2).

**Tech Stack:** C#/.NET 10, Microsoft.Data.Sqlite, xUnit 2.9.3, no new packages.

**Spec:** `docs/designs/design-2026-09-08-mmr-diversity.md` — the binding authority; this plan argues from it. Spec's eng review is CLEARED with `NO UNRESOLVED DECISIONS`.

## Global Constraints

- λ = 0.7 fixed in code (`Retriever.Lambda`) — no new env vars, no new `CS_AGENT_*` vars.
- Pool is the WHOLE scored set — deliberately no fetch_k/pool-size constant (spec D1).
- MMR decides the SET; relevance order decides citation NUMBERING (spec D2). `CitedChunk.Score` keeps its meaning: query-relevance cosine. The result-object shape (CLI, `--json`, HTTP, MCP) is unchanged — only set membership changes.
- Cost invariants untouched: exactly 1 embed call, worst case 3 LLM calls, no new runtime dependencies, no store schema change, no re-ingest needed.
- `Pool` inherits the `empty-store` guard from `TopK` (spec failure-mode table): named error `store/empty-store`, never a silent empty result.
- Validate loudly: named structured errors (`CsAgentError` component + code), no stack traces, no silent wrong answers.
- Local-only repro-store tests SKIP (not vacuous-pass) when `cs-agent-docusaurus-io.db` is absent — custom `[ReproStoreFact]` attribute, no new package.
- D5.1 STOP gate: if the measured clone-vs-adjacent similarity gap in the repro store fails its thresholds, STOP — bring the numbers to the user, re-decide λ. Never tweak λ under a red gate.
- D6 miss procedure (pre-committed): if an eval gate goes red after MMR lands, revert the increment and reopen issue #6 with the D5 measurements. λ is re-decided on numbers, not tuned in flight.
- Commits: Conventional Commits, subject ≤50 chars, imperative, no period, NO Co-Authored-By / attribution trailer (workspace rule).
- RTK prefix on shell commands (`rtk dotnet test`); test framework is xUnit 2.9.3 (no SkippableFact package — do not add one).

---

### Task 1: Repro-store test infra + D5.1 measurement STOP-gate

**Files:**
- Create: `tests/CsAgent.Tests/ReproStore.cs`
- Create: `tests/CsAgent.Tests/ReproStoreTests.cs`

**Interfaces:**
- Consumes: `VectorMath.Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)` (exists, `src/CsAgent.Core/VectorMath.cs:6`); `Microsoft.Data.Sqlite` (flows transitively from the Core project reference).
- Produces: `[ReproStoreFact]` attribute (skips when the gitignored repro `.db` is absent); `ReproStore.FindDbPath()` → `string?`; `ReproStore.OpenStore()` → `SqliteVectorStore` opened on the repro store with its own recorded embedding model; `ReproStore.ReadChunks(path)` → `IReadOnlyList<(string PagePath, int Ordinal, float[] Vector)>`; `CloneFamilyKey(string pagePath)` → `string`. Tasks 3 and 4 use these.

**Why first:** Spec D5.1 requires measuring the real similarity gap BEFORE implementation. This is a measurement harness, not a feature — the "test" asserts the gap exists; red means STOP and re-decide λ with the user, not implement anyway.

- [ ] **Step 1: Write the helper and the attribute**

Create `tests/CsAgent.Tests/ReproStore.cs`:

```csharp
using CsAgent.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CsAgent.Tests;

/// <summary>
/// Local-only gate over the gitignored 2026-09-06 docusaurus repro store
/// (cs-agent-docusaurus-io.db, repo root, ~3,960 chunks). CI cannot see the
/// .db, so these facts SKIP there — honest scope documented in the design
/// doc (design-2026-09-08-mmr-diversity.md, D5).
/// </summary>
public sealed class ReproStoreFactAttribute : FactAttribute
{
    public const string DbFileName = "cs-agent-docusaurus-io.db";

    public ReproStoreFactAttribute()
    {
        if (ReproStore.FindDbPath() is null)
            Skip = $"{DbFileName} not found (gitignored local artifact) — runs only where the repro store exists";
    }
}

public static class ReproStore
{
    public const string DbFileName = ReproStoreFactAttribute.DbFileName;

    /// <summary>Walks up from the test assembly to the repo root looking for the repro .db.</summary>
    public static string? FindDbPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent!)
        {
            var candidate = Path.Combine(dir.FullName, DbFileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Opens the repro store through SqliteVectorStore, passing whatever embedding
    /// model the store itself records (read from store_meta first, so the test never
    /// guesses the model name). Note: the store constructor rewrites the same-value
    /// embedding_model meta row on open — benign, same value, no data change.
    /// </summary>
    public static SqliteVectorStore OpenStore()
    {
        var path = FindDbPath() ?? throw new InvalidOperationException(
            $"{DbFileName} not found — see {nameof(ReproStoreFactAttribute)}.");
        var model = ReadMeta(path, "embedding_model")
            ?? throw new InvalidOperationException($"{DbFileName} has no embedding_model meta row.");
        return new SqliteVectorStore(path, model);
    }

    /// <summary>Raw read-only read of every chunk vector — no store API, no writes.</summary>
    public static IReadOnlyList<(string PagePath, int Ordinal, float[] Vector)> ReadChunks(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT page_path, ordinal, embedding FROM chunks";
        using var reader = cmd.ExecuteReader();
        var chunks = new List<(string PagePath, int Ordinal, float[] Vector)>();
        while (reader.Read())
        {
            var blob = (byte[])reader[2];
            var vector = new float[blob.Length / sizeof(float)];
            Buffer.BlockCopy(blob, 0, vector, 0, blob.Length);
            chunks.Add((reader.GetString(0), reader.GetInt32(1), vector));
        }
        return chunks;
    }

    private static string? ReadMeta(string dbPath, string key)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM store_meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }
}
```

- [ ] **Step 2: Write the D5.1 measurement test**

Create `tests/CsAgent.Tests/ReproStoreTests.cs`:

```csharp
using System.Text.RegularExpressions;
using CsAgent.Core;
using Xunit;
using Xunit.Abstractions;

namespace CsAgent.Tests;

/// <summary>
/// D5.1 measurement gate (design-2026-09-08-mmr-diversity.md): before MMR lands,
/// prove the real repro store has the similarity gap λ=0.7 exploits — clone twins
/// (same page content under versioned URLs) must sit clearly above adjacent
/// same-page chunks. RED = STOP: bring the printed numbers to the user and
/// re-decide λ. Never implement past a red gate here.
/// </summary>
public sealed class ReproStoreTests
{
    private readonly ITestOutputHelper _output;
    public ReproStoreTests(ITestOutputHelper output) => _output = output;

    /// <summary>Strips version segments so /docs/3.9.2/installation,
    /// /docs/installation and /docs/next/installation map to one family key.</summary>
    internal static string CloneFamilyKey(string pagePath)
    {
        var segments = pagePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != "next" && !Regex.IsMatch(s, @"^v?\d+(\.\d+)+$"));
        return string.Join("/", segments);
    }

    [ReproStoreFact]
    public void CloneTwins_ClearlyMoreSimilarThanAdjacentChunks()
    {
        var chunks = ReproStore.ReadChunks(ReproStore.FindDbPath()!);
        _output.WriteLine($"{chunks.Count} chunks in repro store");

        // Cross-page pairs inside a clone family = versioned-URL clone twins.
        // Ordinal-ALIGNED only: chunk i of one version vs chunk i of another is
        // the same content. Cross-ordinal pairs are different content and drag
        // the median to the noise floor (~0.57 measured) — do not compare them.
        var cloneSims = new List<float>();
        foreach (var family in chunks.GroupBy(c => CloneFamilyKey(c.PagePath)))
        {
            var pages = family.GroupBy(c => c.PagePath).ToList();
            if (pages.Count < 2) continue;
            foreach (var pageA in pages)
                foreach (var pageB in pages.Where(p => !ReferenceEquals(p, pageA)))
                    foreach (var a in pageA)
                        foreach (var b in pageB.Where(b => b.Ordinal == a.Ordinal))
                            cloneSims.Add(VectorMath.Cosine(a.Vector, b.Vector));
        }

        // Same-page adjacent ordinals share the 150-char chunk overlap.
        var adjacentSims = new List<float>();
        foreach (var page in chunks.GroupBy(c => c.PagePath))
        {
            var byOrdinal = page.OrderBy(c => c.Ordinal).ToList();
            for (var i = 1; i < byOrdinal.Count; i++)
                adjacentSims.Add(VectorMath.Cosine(byOrdinal[i - 1].Vector, byOrdinal[i].Vector));
        }

        Assert.NotEmpty(cloneSims);     // no clone families = wrong repro data — STOP
        Assert.NotEmpty(adjacentSims);

        cloneSims.Sort();
        adjacentSims.Sort();
        float Median(List<float> xs) => xs[xs.Count / 2];

        var cloneMedian = Median(cloneSims);
        var adjacentMedian = Median(adjacentSims);
        _output.WriteLine(
            $"clone-twin sims: n={cloneSims.Count}, median={cloneMedian:F4}, min={cloneSims[0]:F4}");
        _output.WriteLine(
            $"adjacent-chunk sims: n={adjacentSims.Count}, median={adjacentMedian:F4}, max={adjacentSims[^1]:F4}");

        // STOP gate (spec D5.1). Break-even: an MMR pick flips when
        // 0.3·(clone_sim − diverse_sim) > 0.7·(relevance_gap), so λ=0.7 needs
        // clones clearly above everything else a query can retrieve.
        Assert.True(cloneMedian >= 0.9f,
            $"clone-twin median {cloneMedian:F4} < 0.9 — versioned URLs are not near-clones. STOP (spec D5.1).");
        Assert.True(cloneMedian - adjacentMedian >= 0.05f,
            $"gap {cloneMedian - adjacentMedian:F4} < 0.05 — λ=0.7 cannot separate clones from adjacent chunks. STOP (spec D5.1).");
    }
}
```

- [ ] **Step 3: Run it locally — this is the STOP gate**

Run: `rtk dotnet test tests/CsAgent.Tests --filter "FullyQualifiedName~ReproStoreTests"`
Expected: PASS with the two median lines printed (via `--logger "console;verbosity=detailed"` if you need to see `_output`). Real-data anchor (controller-verified 2026-09-08 via direct SQLite read, ordinal-aligned): clone-twin median ≈ 0.999, adjacent-chunk median ≈ 0.727, gap ≈ 0.27 — the two junk families the family regex over-groups (blog/releases, changelog) cannot flip the aggregate (7,115 doc-family pairs at ~1.0 vs 1,666 junk pairs). If your numbers are wildly off these anchors, your pair generation differs from the corrected code.

**If RED:** STOP. Do not start Task 2. Report the printed medians to the user — per spec D5.1 λ is re-decided on the numbers. **If the test SKIPS** (`cs-agent-docusaurus-io.db` not at repo root): the artifact is missing — also STOP and tell the user; the D5 gate cannot run without the real repro data.

- [ ] **Step 4: Commit**

```bash
rtk git add tests/CsAgent.Tests/ReproStore.cs tests/CsAgent.Tests/ReproStoreTests.cs
rtk git commit -m "test: repro-store similarity measurement gate"
```

---

### Task 2: SqliteVectorStore — `TopK` → `Pool` (vectors retained)

**Files:**
- Modify: `src/CsAgent.Core/SqliteVectorStore.cs:96-124`
- Modify: `tests/CsAgent.Tests/SqliteVectorStoreTests.cs:15-58`
- Modify: `tests/CsAgent.Tests/CorpusLoaderTests.cs:153-160`

**Interfaces:**
- Consumes: `VectorMath.Cosine` (exists); existing `ChunkHit` record, `HasDocuments`, `FromBlob`.
- Produces: `public sealed record ChunkCandidate(ChunkHit Hit, float[] Vector);` (top-level record in `SqliteVectorStore.cs`, beside `ChunkHit`) and `public IReadOnlyList<ChunkCandidate> Pool(ReadOnlySpan<float> query)` — every chunk scored by cosine, relevance-descending, vectors retained. `TopK` survives this task (production caller still on it) and is deleted in Task 3. Task 3 consumes both.

**Why Pool inherits the empty-store guard:** the guard lives inside `TopK` (`SqliteVectorStore.cs:99`); dropping it in the move would turn the named `store/empty-store` error into a silent empty result — validate-loudly violation (spec failure-mode table).

- [ ] **Step 1: Write the failing test**

Add to `tests/CsAgent.Tests/SqliteVectorStoreTests.cs` (inside the existing test class):

```csharp
    [Fact]
    public void Pool_ReturnsAllChunks_RelevanceOrdered_WithVectors()
    {
        using var store = new SqliteVectorStore(_dbPath, "m");
        store.UpsertPage("a.md", "h1", new[]
        {
            ("one", new float[] { 1, 0 }),
            ("two", new float[] { 0, 1 }),
        });

        var pool = store.Pool(new float[] { 0.9f, 0.1f });

        Assert.Equal(2, pool.Count);                        // whole scored set, not top-k
        Assert.Equal("one", pool[0].Hit.Text);              // relevance-descending
        Assert.Equal(2, pool[0].Vector.Length);              // vectors retained
        Assert.True(pool[0].Hit.Score >= pool[1].Hit.Score); // scores monotonic
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `rtk dotnet test tests/CsAgent.Tests --filter "FullyQualifiedName~SqliteVectorStoreTests"`
Expected: compile error — `SqliteVectorStore` has no `Pool` method.

- [ ] **Step 3: Implement `Pool` and `ChunkCandidate` (keep `TopK` until Step 5)**

In `src/CsAgent.Core/SqliteVectorStore.cs`, add beside the `ChunkHit` record (line 5):

```csharp
public sealed record ChunkCandidate(ChunkHit Hit, float[] Vector);
```

Replace the `TopK` method (lines 96-124) with BOTH methods temporarily — `Pool` first, `TopK` left in place until Step 5 removes it:

```csharp
    /// <summary>Every chunk scored by cosine against the query, relevance-ordered, vectors retained.</summary>
    public IReadOnlyList<ChunkCandidate> Pool(ReadOnlySpan<float> query)
    {
        if (!HasDocuments)
            throw new CsAgentException(new CsAgentError(
                "store", "empty-store",
                "No documents ingested. Run `cs-agent ingest <path>` first."));

        var dim = query.Length;
        var results = new List<ChunkCandidate>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT page_path, ordinal, text, embedding FROM chunks";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var vector = FromBlob((byte[])reader[3], dim);
                results.Add(new ChunkCandidate(
                    new ChunkHit(reader.GetString(0), reader.GetInt32(1), reader.GetString(2),
                        VectorMath.Cosine(query, vector)),
                    vector));
            }
        }
        return results.OrderByDescending(r => r.Hit.Score).ToList();
    }
```

- [ ] **Step 4: Run the new test to verify it passes**

Run: `rtk dotnet test tests/CsAgent.Tests --filter "FullyQualifiedName~SqliteVectorStoreTests"`
Expected: PASS (the pre-existing `TopK` tests still pass too).

- [ ] **Step 5: Migrate the 4 `TopK` call sites, then delete `TopK`**

5a. `tests/CsAgent.Tests/SqliteVectorStoreTests.cs` — rename `UpsertThenTopK_ReturnsBestMatch` to `UpsertThenPool_ReturnsBestMatchFirst` and change its body:

```csharp
    [Fact]
    public void UpsertThenPool_ReturnsBestMatchFirst()
    {
        using (var store = new SqliteVectorStore(_dbPath, "text-embedding-3-small"))
        {
            store.UpsertPage("docs/api-keys.md", "h1", new[]
            {
                ("Rotating keys from Settings", new float[] { 1, 0, 0 }),
                ("Billing plans overview", new float[] { 0, 1, 0 }),
            });
        }

        using (var store = new SqliteVectorStore(_dbPath, "text-embedding-3-small"))
        {
            var pool = store.Pool(new float[] { 0.9f, 0.1f, 0 });
            var hit = pool[0];
            Assert.Equal("docs/api-keys.md", hit.Hit.PagePath);
            Assert.Equal("Rotating keys from Settings", hit.Hit.Text);
            Assert.True(hit.Hit.Score > 0.99f);
        }
    }
```

5b. `IdempotentReingest_NoDuplicateChunks` — replace the assertion:

```csharp
            Assert.Equal(2, store.Pool(new float[] { 1, 0 }).Count);
```

5c. Rename `EmptyStore_TopK_ThrowsNamedError` to `EmptyStore_Pool_ThrowsNamedError`:

```csharp
    [Fact]
    public void EmptyStore_Pool_ThrowsNamedError()
    {
        using var store = new SqliteVectorStore(_dbPath, "m");
        var ex = Assert.Throws<CsAgentException>(() => store.Pool(new float[] { 1, 0 }));
        Assert.Equal("empty-store", ex.Error.Code);
        Assert.Contains("ingest", ex.Error.Message);
    }
```

5d. `tests/CsAgent.Tests/CorpusLoaderTests.cs:153-159` — replace the `TopK` usage in `UrlKeyedPage_TitleFallsBackToSegmentOrHost`:

```csharp
        using var store = new SqliteVectorStore(_dbPath, "fake-embedding");
        // hash vectors rank arbitrarily — Pool returns all; pick by content
        var pool = store.Pool(FakeEmbeddingGenerator.HashToVector("no heading either"));
        Assert.StartsWith("api — ",
            pool.Single(c => c.Hit.Text.Contains("no heading here")).Hit.Text);   // last URL segment
        Assert.StartsWith("docs.foo.com — ",
            pool.Single(c => c.Hit.Text.Contains("no heading either")).Hit.Text); // host for root URLs
```

5e. `TopK` itself STAYS in `src/CsAgent.Core/SqliteVectorStore.cs` for now — `Retriever.Retrieve` is still its production caller and would not compile without it. Task 3 rewires `Retrieve` to `Pool` and deletes `TopK` in the same commit, so every commit on the branch builds green.

- [ ] **Step 6: Run the full suite**

Run: `rtk dotnet test`
Expected: all green (130 existing tests + the new Pool test). `TopK` itself is now test-dead (no test references it) but still compiled — production `Retrieve` uses it until Task 3.

- [ ] **Step 7: Commit**

```bash
rtk git add src/CsAgent.Core/SqliteVectorStore.cs tests/CsAgent.Tests/SqliteVectorStoreTests.cs tests/CsAgent.Tests/CorpusLoaderTests.cs
rtk git commit -m "feat: store Pool over whole scored set"
```

---

### Task 3: Retriever — `MmrSelect` + `Retrieve` wiring

**Files:**
- Modify: `src/CsAgent.Core/Retriever.cs`
- Create: `tests/CsAgent.Tests/MmrSelectTests.cs`

**Interfaces:**
- Consumes: `ChunkCandidate` and `store.Pool` (Task 2); `VectorMath.Cosine`; `FakeEmbeddingGenerator` pattern from `CorpusLoaderTests.cs:8`.
- Produces: `public const float Lambda = 0.7f;` and `public static IReadOnlyList<ChunkCandidate> MmrSelect(ReadOnlySpan<float> query, IReadOnlyList<ChunkCandidate> pool, int k, float lambda)` on `Retriever` — returns the selected set relevance-descending (tie-break: pool order). Task 4 and the eval gates consume these.

**Synthetic-vector arithmetic (verified by hand, use these exact vectors):**

Clone-collapse scenario, query `q = (1, 0, 0, 0)`:
- `a  = (0.9, 0.43589, 0, 0)` — rel(q,a) = 0.90
- `aClone = (0.89, 0.456, 0, 0)` — rel = 0.89, cos(a, aClone) ≈ 0.9998
- `b  = (0.85, -0.1492, 0.5052, 0)` — rel = 0.85, cos(a, b) = 0.70
- `c  = (0.85, -0.1492, 0, 0.5052)` — rel = 0.85, cos(a, c) = 0.70, cos(b, c) ≈ 0.7448

MMR picks with λ = 0.7: pick 1 = `a` (highest relevance). Pick 2: aClone margin = 0.7·0.89 − 0.3·0.9998 = 0.323; b margin = 0.7·0.85 − 0.3·0.70 = 0.385 → `b` wins. Pick 3: aClone margin = 0.323; c margin = 0.7·0.85 − 0.3·0.7448 = 0.372 → `c` wins. k=3 → {a, b, c}, aClone EXCLUDED even though it is the 2nd-most-relevant candidate. Relevance-descending order: a (0.90), b (0.85), c (0.85) — b before c via the pool-order tie-break.

λ-dominance scenario, same `q`: `a = (0.95, 0.31225, 0, 0)` rel 0.95; `a2 = (0.9, 0.43589, 0, 0)` rel 0.90, cos(a, a2) ≈ 0.991; `d = (0.3, 0, 0, 0.95394)` rel 0.30. k=2: a2 margin = 0.7·0.90 − 0.3·0.991 = 0.333; d margin = 0.7·0.30 − 0.3·0.285 = 0.125 → {a, a2}; the weak-but-diverse `d` loses. Relevance stays dominant at λ = 0.7.

- [ ] **Step 1: Write the failing tests**

Create `tests/CsAgent.Tests/MmrSelectTests.cs`:

```csharp
using CsAgent.Core;
using Microsoft.Extensions.AI;
using Xunit;

namespace CsAgent.Tests;

public sealed class MmrSelectTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cs-agent-mmr-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static ChunkCandidate Candidate(string page, float[] vector, ReadOnlySpan<float> query) =>
        new(new ChunkHit(page, 0, page, VectorMath.Cosine(query, vector)), vector);

    [Fact]
    public void NoRedundancy_MmrSetEqualsPlainTopK_RelevanceOrder()
    {
        float[] query = { 1, 0, 0 };
        var pool = new List<ChunkCandidate>
        {
            Candidate("x", new float[] { 0.9f, 0.1f, 0 }, query),
            Candidate("y", new float[] { 0.5f, 0.8f, 0 }, query),
            Candidate("z", new float[] { 0.2f, 0.3f, 0.9f }, query),
            Candidate("w", new float[] { 0.1f, 0.1f, 0.1f }, query),
        };

        var selected = Retriever.MmrSelect(query, pool, k: 3, Retriever.Lambda);

        // mutual sims are low, so MMR = relevance ranking: x, w, y
        Assert.Equal(new[] { "x", "w", "y" }, selected.Select(c => c.Hit.PagePath).ToArray());
    }

    [Fact]
    public void CloneTwins_CollapseToOneDistinctCandidate()
    {
        float[] query = { 1, 0, 0, 0 };
        var a = Candidate("a", new float[] { 0.9f, 0.43589f, 0, 0 }, query);
        var clone = Candidate("clone", new float[] { 0.89f, 0.456f, 0, 0 }, query);
        var b = Candidate("b", new float[] { 0.85f, -0.1492f, 0.5052f, 0 }, query);
        var c = Candidate("c", new float[] { 0.85f, -0.1492f, 0, 0.5052f }, query);
        var pool = new List<ChunkCandidate> { a, clone, b, c }; // relevance-desc, as Pool returns

        var selected = Retriever.MmrSelect(query, pool, k: 3, Retriever.Lambda);

        Assert.Equal(new[] { "a", "b", "c" }, selected.Select(c => c.Hit.PagePath).ToArray());
        Assert.DoesNotContain(selected, s => s.Hit.PagePath == "clone");
    }

    [Fact]
    public void LambdaDominance_StrongRelevantBeatsWeakDiverse()
    {
        float[] query = { 1, 0, 0, 0 };
        var pool = new List<ChunkCandidate>
        {
            Candidate("a",  new float[] { 0.95f, 0.31225f, 0, 0 }, query),
            Candidate("a2", new float[] { 0.9f, 0.43589f, 0, 0 }, query),
            Candidate("d",  new float[] { 0.3f, 0, 0, 0.95394f }, query),
        };

        var selected = Retriever.MmrSelect(query, pool, k: 2, Retriever.Lambda);

        Assert.Equal(new[] { "a", "a2" }, selected.Select(c => c.Hit.PagePath).ToArray());
    }

    [Fact]
    public void PoolSmallerThanK_ReturnsAll()
    {
        float[] query = { 1, 0 };
        var pool = new List<ChunkCandidate>
        {
            Candidate("a", new float[] { 1, 0 }, query),
            Candidate("b", new float[] { 0, 1 }, query),
        };

        var selected = Retriever.MmrSelect(query, pool, k: 5, Retriever.Lambda);

        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void EmptyPool_ReturnsEmpty()
    {
        float[] query = { 1, 0 };
        var selected = Retriever.MmrSelect(query, new List<ChunkCandidate>(), k: 3, Retriever.Lambda);
        Assert.Empty(selected);
    }

    [Fact]
    public void Retrieve_NumbersSelectedSet_RelevanceDescending()
    {
        // 8-dim store (FakeEmbeddingGenerator is 8-dim); embedding is fixed so the
        // query vector is exactly (1,0,0,0,0,0,0,0) and the clone scenario applies.
        using (var store = new SqliteVectorStore(_dbPath, "fixed"))
        {
            store.UpsertPage("docs/a.md", "h1", new[]
            {
                ("strong relevant", new float[] { 0.9f, 0.43589f, 0, 0, 0, 0, 0, 0 }),
            });
            store.UpsertPage("docs/clone.md", "h2", new[]
            {
                ("near-identical twin", new float[] { 0.89f, 0.456f, 0, 0, 0, 0, 0, 0 }),
            });
            store.UpsertPage("docs/b.md", "h3", new[]
            {
                ("diverse b", new float[] { 0.85f, -0.1492f, 0.5052f, 0, 0, 0, 0, 0 }),
            });
            store.UpsertPage("docs/c.md", "h4", new[]
            {
                ("diverse c", new float[] { 0.85f, -0.1492f, 0, 0.5052f, 0, 0, 0, 0 }),
            });
        }

        using var store2 = new SqliteVectorStore(_dbPath, "fixed");
        var retriever = new Retriever(new FixedEmbeddingGenerator(), store2, topK: 3);

        var cited = retriever.Retrieve("anything");

        Assert.Equal(3, cited.Count);
        Assert.Equal(new[] { "docs/a.md", "docs/b.md", "docs/c.md" },
            cited.Select(c => c.PagePath).ToArray());
        Assert.DoesNotContain(cited, c => c.PagePath == "docs/clone.md");
        Assert.Equal(new[] { 1, 2, 3 }, cited.Select(c => c.Number).ToArray());
        Assert.True(cited[0].Score >= cited[1].Score && cited[1].Score >= cited[2].Score);
    }

    /// <summary>Always returns (1,0,0,0,0,0,0,0) — the seeded scenario's query vector.</summary>
    private sealed class FixedEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var embeddings = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var _ in values)
                embeddings.Add(new Embedding<float>(new float[] { 1, 0, 0, 0, 0, 0, 0, 0 }));
            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `rtk dotnet test tests/CsAgent.Tests --filter "FullyQualifiedName~MmrSelectTests"`
Expected: compile error — `Retriever.MmrSelect` / `Retriever.Lambda` do not exist.

- [ ] **Step 3: Implement `MmrSelect` and rewire `Retrieve`**

Replace `src/CsAgent.Core/Retriever.cs` body with:

```csharp
using Microsoft.Extensions.AI;

namespace CsAgent.Core;

public sealed record CitedChunk(
    [property: System.Text.Json.Serialization.JsonPropertyName("number")] int Number,
    [property: System.Text.Json.Serialization.JsonPropertyName("page_path")] string PagePath,
    [property: System.Text.Json.Serialization.JsonPropertyName("ordinal")] int Ordinal,
    [property: System.Text.Json.Serialization.JsonPropertyName("text")] string Text,
    [property: System.Text.Json.Serialization.JsonPropertyName("score")] float Score);

/// <summary>
/// Query embed + MMR diversity selection over the store's full scored pool;
/// chunks numbered [1..k] relevance-descending for citation.
/// </summary>
public sealed class Retriever(IEmbeddingGenerator<string, Embedding<float>> embeddings, SqliteVectorStore store, int topK)
{
    /// <summary>Relevance/diversity trade-off — fixed, not env-tunable (agreed 2026-09-08).</summary>
    public const float Lambda = 0.7f;

    public IReadOnlyList<CitedChunk> Retrieve(string question, CancellationToken cancellationToken = default)
    {
        var vector = embeddings.GenerateVectorAsync(question, cancellationToken: cancellationToken).GetAwaiter().GetResult();
        var pool = store.Pool(vector.Span);
        var selected = MmrSelect(vector.Span, pool, topK, Lambda);
        return [.. selected.Select((c, i) => new CitedChunk(i + 1, c.Hit.PagePath, c.Hit.Ordinal, c.Hit.Text, c.Hit.Score))];
    }

    /// <summary>
    /// MMR selection (Carbonell & Goldstein 1998): each pick maximizes
    /// lambda * relevance(query, candidate) - (1 - lambda) * max similarity to
    /// anything already selected. First pick: the selected set is empty so the
    /// penalty term is 0 — the highest-relevance candidate. An empty pool
    /// returns an empty list (defensive; Retrieve throws empty-store first).
    /// Returns the selected SET, relevance-descending (ties keep pool order).
    /// </summary>
    public static IReadOnlyList<ChunkCandidate> MmrSelect(
        ReadOnlySpan<float> query, IReadOnlyList<ChunkCandidate> pool, int k, float lambda)
    {
        if (pool.Count == 0 || k <= 0)
            return [];

        var count = Math.Min(k, pool.Count);
        var relevance = new float[pool.Count];
        for (var i = 0; i < pool.Count; i++)
            relevance[i] = VectorMath.Cosine(query, pool[i].Vector);

        var remaining = Enumerable.Range(0, pool.Count).ToList();
        var picked = new List<int>(count);
        while (picked.Count < count)
        {
            var bestAt = 0;
            var bestMargin = float.MinValue;
            for (var r = 0; r < remaining.Count; r++)
            {
                var i = remaining[r];
                var maxSim = 0f;
                foreach (var p in picked)
                    maxSim = MathF.Max(maxSim, VectorMath.Cosine(pool[i].Vector, pool[p].Vector));
                var margin = lambda * relevance[i] - (1f - lambda) * maxSim;
                if (margin > bestMargin)
                {
                    bestMargin = margin;
                    bestAt = r;
                }
            }
            picked.Add(remaining[bestAt]);
            remaining.RemoveAt(bestAt);
        }

        return [.. picked
            .OrderByDescending(i => relevance[i])
            .ThenBy(i => i)
            .Select(i => pool[i])];
    }
}
```

Note: `MmrSelect` recomputes relevance from `query` rather than trusting `Hit.Score` — keeps the function pure over its declared inputs; `Hit.Score` (computed identically by `Pool`) is what surfaces in `CitedChunk.Score`.

Also in this step: DELETE the `TopK` method from `src/CsAgent.Core/SqliteVectorStore.cs` — `Retrieve` no longer calls it, so the migration is complete.

- [ ] **Step 4: Run the MMR tests to verify they pass**

Run: `rtk dotnet test tests/CsAgent.Tests --filter "FullyQualifiedName~MmrSelectTests"`
Expected: 6/6 PASS. If `CloneTwins_CollapseToOneDistinctCandidate` or `Retrieve_NumbersSelectedSet_RelevanceDescending` fails with the clone still selected, the margin arithmetic is wrong — recheck the synthetic vectors against the arithmetic above before touching anything else.

- [ ] **Step 5: Run the full suite (TopK is now deleted from the store)**

Run: `rtk dotnet test`
Expected: all green. Every pipeline test (`AskPipelineTests`, `EvalGateTests`, HTTP endpoint tests) now exercises MMR implicitly — hash-vector stores are near-orthogonal so MMR there ≈ plain top-k, and their assertions (chunk counts, citations) must hold unchanged. Any red pipeline test means MMR changed observable behavior on a clone-free store — investigate before proceeding; do NOT relax the pipeline test.

- [ ] **Step 6: Commit**

```bash
rtk git add src/CsAgent.Core/Retriever.cs src/CsAgent.Core/SqliteVectorStore.cs tests/CsAgent.Tests/MmrSelectTests.cs
rtk git commit -m "feat: MMR diversity selection in Retriever"
```

---

### Task 4: D5.2 integration check — real repro vectors

**Files:**
- Modify: `tests/CsAgent.Tests/ReproStoreTests.cs`

**Interfaces:**
- Consumes: `ReproStore.OpenStore()`, `ReproStore.ReadChunks()`, `CloneFamilyKey` (Task 1); `Retriever.MmrSelect`, `Retriever.Lambda`, `store.Pool` (Tasks 2-3).
- Produces: nothing consumed downstream — this is the spec's real-data proof (D5.2): "take a stored clone chunk's own vector as the query (no embedding call), run Pool + MmrSelect, assert the selected top-k spans distinct pages instead of the versioned-URL clones."

- [ ] **Step 1: Add the integration test to `tests/CsAgent.Tests/ReproStoreTests.cs`**

```csharp
    [ReproStoreFact]
    public void MmrSelect_OnRealCloneVectors_ReturnsDistinctPages()
    {
        using var store = ReproStore.OpenStore();
        var chunks = ReproStore.ReadChunks(ReproStore.FindDbPath()!);

        // The issue #6 scenario family, pinned: the installation page cloned
        // across versioned URLs (twins ~0.999 per the D5.1 measurement). Do NOT
        // pick "family with the most versions" — that selects the blog/releases
        // regex over-group (14 DIFFERENT release posts, mutual sims ~0.77, not
        // clones, legitimately all attractive to a release-post query).
        var hubFamily = "https:/docusaurus.io/docs/installation";
        var hub = chunks.Where(c => CloneFamilyKey(c.PagePath) == hubFamily).ToList();
        Assert.NotEmpty(hub); // wrong repro store or changed paths — STOP-class anomaly
        _output.WriteLine(
            $"clone hub: family '{hubFamily}', {hub.GroupBy(c => c.PagePath).Count()} versions, {hub.Count()} chunks");

        // A clone chunk's own vector as the query — no embedding call (spec D5.2).
        var query = hub.OrderBy(c => c.Ordinal).First().Vector;

        var pool = store.Pool(query);
        var selected = Retriever.MmrSelect(query, pool, k: 8, Retriever.Lambda);

        Assert.Equal(8, selected.Count);
        var distinctPages = selected.Select(c => c.Hit.PagePath).Distinct().Count();
        var hubClones = selected.Count(c => CloneFamilyKey(c.Hit.PagePath) == hubFamily);
        _output.WriteLine("selected pages: " + string.Join(", ", selected.Select(c => c.Hit.PagePath)));

        Assert.True(distinctPages >= 6,
            $"only {distinctPages} distinct pages in top-8 — MMR failed on the real repro store (spec D5.2)");
        Assert.True(hubClones <= 2,
            $"{hubClones} clone-family chunks still in top-8 — diversity selection failed on the real repro store");
    }
```

- [ ] **Step 2: Run it locally**

Run: `rtk dotnet test tests/CsAgent.Tests --filter "FullyQualifiedName~ReproStoreTests" --logger "console;verbosity=detailed"`
Expected: 2/2 PASS (the Task 1 measurement test + this one), with the selected-pages line printed. Runtime: Pool full-scans ~3,960 vectors and MMR runs 8 picks — a few seconds, zero model calls. Real-data anchor (controller-verified 2026-09-08 by running the same MMR over the raw store): for the installation-chunk query, top-8 = installation (the query chunk itself, rel 1.0000) + docs index, category/getting-started, configuration, cli, deployment, typescript-support + one more installation twin at rank 8 (rel 0.7551) → distinctPages=8, hubClones=2.

**Threshold ruling (pre-registered):** `distinctPages >= 6` and `hubClones <= 2` are measured bets from the issue #6 data. If this test comes back RED with real numbers, do NOT loosen the threshold silently and do NOT tune λ — record the printed selected-pages list in the ledger, STOP, and take it to the user alongside the Task 1 medians (this is the D6 numbers-not-tweaking rule applied to the integration gate).

- [ ] **Step 3: Commit**

```bash
rtk git add tests/CsAgent.Tests/ReproStoreTests.cs
rtk git commit -m "test: MMR integration check on repro store"
```

---

### Task 5: Eval gates + docs (README limitation #2, TODOS, issue #6)

**Files:**
- Modify: `README.md:313` (limitation #2)
- Modify: `docs/designs/TODOS.md:53-61` (issue #6 OPEN entry → DONE)
- No src changes.

**Interfaces:**
- Consumes: nothing from earlier tasks' code; consumes the green state they produced.
- Produces: shipped increment — README honest about what MMR does and does not add; eval-gate evidence for the D6 miss procedure never firing.

- [ ] **Step 1: Run the full suite**

Run: `rtk dotnet test`
Expected: all green (existing 130 + Pool migration + 6 MMR tests + 2 repro-store tests run locally; the 2 repro facts skip in CI).

- [ ] **Step 2: Run the eval gates (the D6 regression bar)**

Requires `CS_AGENT_MODEL_KEY` in the environment (default all-deepseek pair). ~45 live model asks, roughly 25-45 minutes combined.

```bash
dotnet run --project src/CsAgent.Cli -- eval
dotnet run --project src/CsAgent.Cli -- eval --heldout
```

Expected: BOTH exit 0 (eval exit code IS the gate — honest-fail). Pass targets: tuning ≥80% answerable, 5/5 unanswerable escalated, proxy 0; held-out ≥80%/16 answerable, 4/4 unanswerable, proxy 0.

**If either exits 1:** D6 pre-committed procedure — revert the increment, reopen issue #6 with the Task 1 measurements attached. Do not tune λ under a red gate, do not edit fixtures.

- [ ] **Step 3: Rewrite README limitation #2**

`README.md:313`, replace:

```markdown
2. **Plain top-k search** — no reranking or hybrid retrieval.
```

with:

```markdown
2. **Plain vector search + MMR diversity** — clone suppression via maximal
   marginal relevance (λ = 0.7) is present: versioned-URL near-duplicate
   chunks no longer flood the top-k. Relevance reranking (cross-encoder or
   LLM) and hybrid (BM25) retrieval are still absent.
```

- [ ] **Step 4: Close the issue #6 entry in TODOS.md**

In `docs/designs/TODOS.md`, move the OPEN entry "versioned-docs URLs flood top-k with near-clone chunks (issue #6...)" (lines 53-61) into the DONE section (top of DONE, matching file convention) rewritten as:

```markdown
## DONE: MMR retrieval diversity — issue #6 closed (2026-09-08)

**What shipped:** `SqliteVectorStore.TopK` replaced by `Pool(query)` (every
chunk scored, relevance-ordered, vectors retained — same single scan, zero
extra DB work) + `Retriever.MmrSelect` (classic MMR, λ = 0.7 fixed in code,
first pick = highest relevance, set returned relevance-descending so
citation numbering is unchanged). Pool = whole scored set (no fetch_k — eng
review D1); MMR decides set, relevance decides order (D2). Result object,
cost model (1 embed, ≤3 LLM calls), env vars, schema: all unchanged; crawled
corpora benefit with no re-ingest. Verified on the real repro store (D5):
measured clone-twin vs adjacent-chunk similarity gap, then integration check
(clone vector as query → top-8 spans distinct pages, zero model calls).
Eval gates green on the default pair. README limitation #2 rewritten.
Deferred with it: ingest-time URL canonicalization; reranking/hybrid search
stay README limitations.
```

- [ ] **Step 5: Commit**

```bash
rtk git add README.md docs/designs/TODOS.md
rtk git commit -m "docs: close issue 6, rewrite limitation 2"
```

- [ ] **Step 6: GitHub issue close + live docusaurus re-run — user-facing, confirm first**

Two outward-facing follow-ups live OUTSIDE this plan's autonomous scope — surface both to the user at handoff, do not run without their say-so:
- `gh issue close 6 --comment "Fixed by MMR diversity selection (λ=0.7): store Pool + Retriever.MmrSelect. See docs/designs/design-2026-09-08-mmr-diversity.md."` — closing a public issue is publish-shaped.
- Post-merge live-eval workflow dispatch on the freshly crawled docusaurus corpus (spec: the live re-run is a follow-up, NOT the done-definition — the unit + D5-measurement bar carries the correctness claim).

---

## Notes for the executor

- **Worktree:** run superpowers:using-git-worktrees first; never implement on `main`.
- **Ledger:** per subagent-driven-development, record the Task 1 medians and the Task 4 selected-pages list in the ledger — they are the evidence the D6 procedure and any future λ re-decision rely on.
- **No `TopK` may survive anywhere:** `rtk grep -rn "TopK" src tests` after Task 3 must return nothing — the method is deleted and every test call site is renamed to `Pool`.
- **Do not touch:** `AskPipeline`, `EvalRunner`, prompts, `CsAgentConfig` (no env vars), the store schema, cost table. MMR is retrieval-only.
- **Known risk the eval gates cover:** adjacent same-page chunks share a 150-char overlap; λ = 0.7 keeps relevance dominant so a needed second chunk from a page stays selectable. If a tuning/heldout question misses after MMR, that is the D6 path — revert + reopen, not fixture edits.