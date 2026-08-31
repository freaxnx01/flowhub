# Repo Inference Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When the classifier answers `"Bridge"` but the capture carries no repo alias, pick the target repo from the bridge catalog — embedding shortlist, LLM confirms — and route an abstain to `ideas-lab` as an idea.

**Architecture:** Widen `BridgeCatalog` to keep the name/description/topics it already receives and throws away. Persist one embedding per repo, keyed by a content hash so refreshes are normally free. On a Bridge result with no alias, embed the capture, take the cosine top-5 over that table, and make one LLM call returning repo (or null), action, title and body. A repo outside the five is a schema violation; a null repo becomes an idea in `ideas-lab`.

**Tech Stack:** .NET 10 / C#, EF Core + pgvector (`Pgvector.Vector`, `vector(384)`), Microsoft.Extensions.AI, xUnit + FluentAssertions + NSubstitute, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-08-29-repo-inference-design.md`

## Global Constraints

- Gated by the existing **`Ai:EnableBridgeClassification`** (from #37, default `false`). No second flag. With it off, none of this code runs — no embedding call, no confirm call.
- **Nothing throws.** Every rung of the failure ladder degrades: no embeddings → lexical shortlist; failed confirm → park as `Unhandled` with the existing reason `bridge candidate — repo undetermined`.
- **The alias short-circuit (`AiClassifier.cs:53-56`) is untouched.** `GetAliasesAsync` keeps its exact contract.
- Embedding column is **`vector(384)`**, matching `CaptureEntityTypeConfiguration.cs:30`. ADR 0006 governs any change.
- New migration is numbered **`0015_`** — the last is `20260825191346_0014_TelegramUpdates`.
- Entities follow the repo pattern: `<Name>Entity.cs` + `<Name>EntityTypeConfiguration.cs` in `source/FlowHub.Persistence/Entities/`, registered as an `internal DbSet` in `FlowHubDbContext`.
- Cosine search uses `FromSqlInterpolated` with a `<=>` ordering and a float-literal vector, mirroring `EfCaptureRepository.SearchByEmbeddingAsync`.
- No `#nullable disable`, no warning suppressions — warnings are errors.
- The abstain target repo is the literal `ideas-lab`.
- Conventional Commits; scopes `ai`, `skills`, `persistence`.

---

## File Structure

- `source/FlowHub.Core/Skills/IBridgeCatalog.cs` — add `GetReposAsync`; add the `BridgeRepo` record.
- `source/FlowHub.Skills/Bridge/BridgeCatalog.cs` — widen the DTO, cache both projections from one fetch.
- `source/FlowHub.AI/EmptyBridgeCatalog.cs` — implement the new member.
- `source/FlowHub.Core/Skills/IRepoEmbeddingStore.cs` — new port: sync + nearest.
- `source/FlowHub.Persistence/Entities/RepoEmbeddingEntity.cs` + `…TypeConfiguration.cs` — new.
- `source/FlowHub.Persistence/FlowHubDbContext.cs` — new `DbSet`.
- `source/FlowHub.Persistence/Migrations/…_0015_RepoEmbeddings.cs` — new.
- `source/FlowHub.Persistence/Repositories/EfRepoEmbeddingStore.cs` — new.
- `source/FlowHub.AI/RepoResolver.cs` — new: shortlist + confirm + abstain.
- `source/FlowHub.AI/AiPrompts.cs` — the confirm prompt.
- `source/FlowHub.AI/AiRepoConfirmResponse.cs` — new response schema.
- `source/FlowHub.AI/AiClassifier.cs` — call the resolver on Bridge-without-alias.
- `source/FlowHub.AI/AiServiceCollectionExtensions.cs` — register the resolver.
- Tests: `tests/FlowHub.Skills.Tests/Bridge/BridgeCatalogReposTests.cs`, `tests/FlowHub.Persistence.Tests/RepoEmbeddingStoreTests.cs`, `tests/FlowHub.Web.ComponentTests/Ai/RepoResolverTests.cs`, `tests/FlowHub.Web.ComponentTests/Ai/AiClassifierRepoInferenceTests.cs`.

---

### Task 1: Widen the bridge catalog

**Files:**
- Modify: `source/FlowHub.Core/Skills/IBridgeCatalog.cs`
- Modify: `source/FlowHub.Skills/Bridge/BridgeCatalog.cs`
- Modify: `source/FlowHub.AI/EmptyBridgeCatalog.cs`
- Test: `tests/FlowHub.Skills.Tests/Bridge/BridgeCatalogReposTests.cs` (create)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `BridgeRepo(string Name, string? Alias, string? Desc, IReadOnlyList<string> Topics, DateTimeOffset? LastUsed)` and `IBridgeCatalog.GetReposAsync(CancellationToken) → Task<IReadOnlyList<BridgeRepo>>`. Tasks 3 and 5 consume both.

- [ ] **Step 1: Write the failing test**

Create `tests/FlowHub.Skills.Tests/Bridge/BridgeCatalogReposTests.cs`. Follow the existing `BridgeCatalog` tests in that project for the `HttpClient`/handler stub style; if none exists, use a `DelegatingHandler` returning a canned response:

```csharp
using System.Net;
using FlowHub.Core.Skills;
using FlowHub.Skills.Bridge;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowHub.Skills.Tests.Bridge;

public sealed class BridgeCatalogReposTests
{
    private const string Payload = """
        [
          {"name":"flowhub","alias":"fh","desc":"Capture anything.","topics":["dotnet"],"last_used":"2026-08-20T10:00:00Z"},
          {"name":"game-nibbles","desc":"Faithful browser Nibbles/Snake clone"},
          {"name":"bare-repo"}
        ]
        """;

    private static BridgeCatalog Sut(HttpMessageHandler handler, TimeProvider time) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://bridge.test") },
            Options.Create(new BridgeOptions { BaseUrl = "https://bridge.test", ApiToken = "t" }),
            NullLogger<BridgeCatalog>.Instance,
            time);

    [Fact]
    public async Task GetReposAsync_ReturnsNameDescTopicsAndLastUsed()
    {
        var handler = new StubHandler(Payload);
        var repos = await Sut(handler, TimeProvider.System).GetReposAsync(default);

        repos.Should().HaveCount(3);
        var flowhub = repos.Single(r => r.Name == "flowhub");
        flowhub.Alias.Should().Be("fh");
        flowhub.Desc.Should().Be("Capture anything.");
        flowhub.Topics.Should().ContainSingle().Which.Should().Be("dotnet");
        flowhub.LastUsed.Should().NotBeNull();
    }

    [Fact]
    public async Task GetReposAsync_ToleratesMissingDescAndTopics()
    {
        var repos = await Sut(new StubHandler(Payload), TimeProvider.System).GetReposAsync(default);

        var bare = repos.Single(r => r.Name == "bare-repo");
        bare.Desc.Should().BeNull();
        bare.Topics.Should().BeEmpty();
        bare.Alias.Should().BeNull();
    }

    [Fact]
    public async Task GetAliasesAsync_AndGetReposAsync_ShareOneFetch()
    {
        var handler = new StubHandler(Payload);
        var sut = Sut(handler, TimeProvider.System);

        await sut.GetAliasesAsync(default);
        await sut.GetReposAsync(default);

        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task GetReposAsync_FetchFails_ReturnsEmptyWithoutThrowing()
    {
        var sut = Sut(new ThrowingHandler(), TimeProvider.System);

        var repos = await sut.GetReposAsync(default);

        repos.Should().BeEmpty();
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("bridge down");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/FlowHub.Skills.Tests --filter "FullyQualifiedName~BridgeCatalogReposTests"`
Expected: FAIL — compile error, `GetReposAsync` does not exist.

- [ ] **Step 3: Add the record and the port member**

In `source/FlowHub.Core/Skills/IBridgeCatalog.cs`:

```csharp
namespace FlowHub.Core.Skills;

/// <summary>One repository from bridge's <c>GET /api/repos</c> catalogue.</summary>
public sealed record BridgeRepo(
    string Name,
    string? Alias,
    string? Desc,
    IReadOnlyList<string> Topics,
    DateTimeOffset? LastUsed);
```

and add to the interface, keeping the existing member and its doc comment unchanged:

```csharp
    /// <summary>
    /// The full catalogue entries. Same cached fetch as <see cref="GetAliasesAsync"/>.
    /// Resilient in the same way: returns the last-known list (or empty) rather than throwing.
    /// </summary>
    Task<IReadOnlyList<BridgeRepo>> GetReposAsync(CancellationToken cancellationToken);
```

- [ ] **Step 4: Widen the DTO and cache both projections**

In `source/FlowHub.Skills/Bridge/BridgeCatalog.cs`, replace the DTO at line 100:

```csharp
    private sealed record BridgeRepoDto(
        string? Name,
        string? Alias,
        string? Desc,
        string[]? Topics,
        DateTimeOffset? Last_Used);
```

Add a second cache field beside `_cache`:

```csharp
    private IReadOnlyList<BridgeRepo>? _repoCache;
```

In the fetch block, after building the alias `HashSet`, also project the repos — both are filled from the same response, so one fetch serves both:

```csharp
                _repoCache = repos
                    .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                    .Select(r => new BridgeRepo(
                        r.Name!.Trim(),
                        string.IsNullOrWhiteSpace(r.Alias) ? null : r.Alias.Trim().ToLowerInvariant(),
                        string.IsNullOrWhiteSpace(r.Desc) ? null : r.Desc.Trim(),
                        r.Topics ?? [],
                        r.Last_Used))
                    .ToList();
```

Set `_repoCache = []` alongside `_cache` on both failure paths (keep-last-known and first-fetch-failed), mirroring the existing alias handling exactly.

Add the public member, delegating to the same gated refresh the aliases use:

```csharp
    public async Task<IReadOnlyList<BridgeRepo>> GetReposAsync(CancellationToken cancellationToken)
    {
        await GetAliasesAsync(cancellationToken);   // performs and caches the shared fetch
        return _repoCache ?? [];
    }
```

- [ ] **Step 5: Implement the member on the empty fallback**

In `source/FlowHub.AI/EmptyBridgeCatalog.cs`:

```csharp
    public Task<IReadOnlyList<BridgeRepo>> GetReposAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BridgeRepo>>([]);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/FlowHub.Skills.Tests --filter "FullyQualifiedName~BridgeCatalog"`
Expected: PASS, including the pre-existing alias tests.

- [ ] **Step 7: Commit**

```bash
git add source/FlowHub.Core/Skills/IBridgeCatalog.cs source/FlowHub.Skills/Bridge/BridgeCatalog.cs source/FlowHub.AI/EmptyBridgeCatalog.cs tests/FlowHub.Skills.Tests/Bridge/BridgeCatalogReposTests.cs
git commit -m "feat(skills): expose the full bridge repo catalogue

/api/repos already returns name, desc, topics and last_used; the DTO
parsed only Alias and dropped the rest. Both projections now come from
one cached fetch.

Refs #38"
```

---

### Task 2: Persist repo embeddings

> **Corrected 2026-08-31.** The original listing used a read-then-write upsert, which is
> racy on the primary key under concurrent catalogue syncs. Caught by the pre-preview review on
> PR #51 after it had shipped; fixed there and back-ported here so this plan does not teach the
> broken pattern. See `60c3210`.

**Files:**
- Create: `source/FlowHub.Core/Skills/IRepoEmbeddingStore.cs`
- Create: `source/FlowHub.Persistence/Entities/RepoEmbeddingEntity.cs`
- Create: `source/FlowHub.Persistence/Entities/RepoEmbeddingEntityTypeConfiguration.cs`
- Modify: `source/FlowHub.Persistence/FlowHubDbContext.cs`
- Create: `source/FlowHub.Persistence/Repositories/EfRepoEmbeddingStore.cs`
- Create: migration `0015_RepoEmbeddings`
- Test: `tests/FlowHub.Persistence.Tests/RepoEmbeddingStoreTests.cs` (create)

**Interfaces:**
- Consumes: `BridgeRepo` from Task 1.
- Produces:
  ```csharp
  Task<IReadOnlyDictionary<string, string>> GetHashesAsync(CancellationToken ct);
  Task UpsertAsync(string repoName, string contentHash, float[] embedding, CancellationToken ct);
  Task RemoveMissingAsync(IReadOnlyCollection<string> keepRepoNames, CancellationToken ct);
  Task<IReadOnlyList<string>> NearestAsync(float[] queryEmbedding, int limit, CancellationToken ct);
  ```
  Task 3 uses the first three; Task 4 uses `NearestAsync`.

- [ ] **Step 1: Write the failing test**

Create `tests/FlowHub.Persistence.Tests/RepoEmbeddingStoreTests.cs`, following the Testcontainers fixture the other tests in this project use (reuse the existing database fixture class rather than creating a second one):

```csharp
using FlowHub.Persistence.Repositories;

namespace FlowHub.Persistence.Tests;

public sealed class RepoEmbeddingStoreTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fx;

    public RepoEmbeddingStoreTests(PostgresFixture fx) => _fx = fx;

    private static float[] Vec(float first)
    {
        var v = new float[384];
        v[0] = first;
        v[1] = 1f;
        return v;
    }

    [Fact]
    public async Task UpsertAsync_ThenGetHashesAsync_ReturnsTheStoredHash()
    {
        await using var db = _fx.NewContext();
        var sut = new EfRepoEmbeddingStore(db);

        await sut.UpsertAsync("flowhub", "hash-1", Vec(1f), default);

        var hashes = await sut.GetHashesAsync(default);
        hashes["flowhub"].Should().Be("hash-1");
    }

    [Fact]
    public async Task UpsertAsync_SameRepoTwice_OverwritesRatherThanDuplicating()
    {
        await using var db = _fx.NewContext();
        var sut = new EfRepoEmbeddingStore(db);

        await sut.UpsertAsync("dup", "hash-1", Vec(1f), default);
        await sut.UpsertAsync("dup", "hash-2", Vec(2f), default);

        var hashes = await sut.GetHashesAsync(default);
        hashes["dup"].Should().Be("hash-2");
    }

    [Fact]
    public async Task RemoveMissingAsync_DropsRowsNotInTheKeepSet()
    {
        await using var db = _fx.NewContext();
        var sut = new EfRepoEmbeddingStore(db);
        await sut.UpsertAsync("keep", "h", Vec(1f), default);
        await sut.UpsertAsync("drop", "h", Vec(1f), default);

        await sut.RemoveMissingAsync(["keep"], default);

        (await sut.GetHashesAsync(default)).Keys.Should().NotContain("drop");
    }

    [Fact]
    public async Task UpsertAsync_ConcurrentCallsForTheSameRepo_AllSucceed()
    {
        // Overlapping catalogue syncs are reachable: RepoResolver syncs per classification
        // and the pipeline consumers run concurrently. A read-then-write would have both
        // callers INSERT the same primary key and one would fail. Each concurrent writer
        // needs its own DbContext - a DbContext is not thread-safe.
        await using var db = await fixture.CreateFreshDbAsync();
        var connectionString = db.Database.GetConnectionString()!;

        FlowHubDbContext Connect() => new(
            new DbContextOptionsBuilder<FlowHubDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.UseVector())
                .Options);

        var writes = Enumerable.Range(0, 8).Select(async i =>
        {
            await using var scoped = Connect();
            await new EfRepoEmbeddingStore(scoped).UpsertAsync("racy", $"hash-{i}", Vec(i), default);
        });

        var act = async () => await Task.WhenAll(writes);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NearestAsync_RowWithoutAnEmbedding_IsExcluded()
    {
        // The Embedding column is nullable, so a row can exist with a hash but no vector.
        await using var db = await fixture.CreateFreshDbAsync();
        var sut = new EfRepoEmbeddingStore(db);
        await sut.UpsertAsync("embedded", "h", Vec(10f), default);
        db.RepoEmbeddings.Add(new RepoEmbeddingEntity
        {
            RepoName = "pending", ContentHash = "h", Embedding = null, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var hits = await sut.NearestAsync(Vec(10f), 50, default);

        hits.Should().ContainSingle().Which.Should().Be("embedded");
    }

    [Fact]
    public async Task NearestAsync_OrdersByCosineDistance()
    {
        await using var db = _fx.NewContext();
        var sut = new EfRepoEmbeddingStore(db);
        await sut.UpsertAsync("near", "h", Vec(10f), default);
        await sut.UpsertAsync("far", "h", Vec(-10f), default);

        var hits = await sut.NearestAsync(Vec(10f), 2, default);

        hits.First().Should().Be("near");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/FlowHub.Persistence.Tests --filter "FullyQualifiedName~RepoEmbeddingStoreTests"`
Expected: FAIL — `EfRepoEmbeddingStore` does not exist.

- [ ] **Step 3: Define the port**

Create `source/FlowHub.Core/Skills/IRepoEmbeddingStore.cs`:

```csharp
namespace FlowHub.Core.Skills;

/// <summary>
/// Driven port for the per-repository embedding cache backing repo inference.
/// Keyed by repository name; <c>ContentHash</c> lets a catalogue refresh skip
/// re-embedding repositories whose name and description are unchanged.
/// </summary>
public interface IRepoEmbeddingStore
{
    Task<IReadOnlyDictionary<string, string>> GetHashesAsync(CancellationToken cancellationToken);

    Task UpsertAsync(string repoName, string contentHash, float[] embedding, CancellationToken cancellationToken);

    Task RemoveMissingAsync(IReadOnlyCollection<string> keepRepoNames, CancellationToken cancellationToken);

    /// <returns>Repository names ordered nearest-first.</returns>
    Task<IReadOnlyList<string>> NearestAsync(float[] queryEmbedding, int limit, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Add the entity, configuration and DbSet**

`source/FlowHub.Persistence/Entities/RepoEmbeddingEntity.cs`:

```csharp
using Pgvector;

namespace FlowHub.Persistence.Entities;

internal sealed class RepoEmbeddingEntity
{
    public required string RepoName { get; set; }
    public required string ContentHash { get; set; }
    public Vector? Embedding { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

`source/FlowHub.Persistence/Entities/RepoEmbeddingEntityTypeConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowHub.Persistence.Entities;

internal sealed class RepoEmbeddingEntityTypeConfiguration : IEntityTypeConfiguration<RepoEmbeddingEntity>
{
    public void Configure(EntityTypeBuilder<RepoEmbeddingEntity> builder)
    {
        builder.ToTable("RepoEmbeddings");
        builder.HasKey(r => r.RepoName);
        builder.Property(r => r.RepoName).HasMaxLength(256);
        builder.Property(r => r.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(r => r.UpdatedAt).IsRequired();

        // 384-dim, matching CaptureEntityTypeConfiguration — ADR 0006 governs any change.
        builder.Property(r => r.Embedding).HasColumnType("vector(384)").IsRequired(false);
    }
}
```

In `source/FlowHub.Persistence/FlowHubDbContext.cs`, beside the other sets:

```csharp
    internal DbSet<RepoEmbeddingEntity> RepoEmbeddings => Set<RepoEmbeddingEntity>();
```

- [ ] **Step 5: Generate the migration**

Run:

```bash
dotnet ef migrations add 0015_RepoEmbeddings \
  --project source/FlowHub.Persistence \
  --startup-project source/FlowHub.Web
```

Expected: creates `…_0015_RepoEmbeddings.cs` creating table `RepoEmbeddings` with a `vector(384)` column. Inspect it — if the column came out as anything other than `vector(384)`, the entity configuration is wrong; fix that rather than hand-editing the migration.

- [ ] **Step 6: Implement the store**

Create `source/FlowHub.Persistence/Repositories/EfRepoEmbeddingStore.cs`:

```csharp
using System.Globalization;
using FlowHub.Core.Skills;
using FlowHub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace FlowHub.Persistence.Repositories;

internal sealed class EfRepoEmbeddingStore : IRepoEmbeddingStore
{
    private readonly FlowHubDbContext _db;

    public EfRepoEmbeddingStore(FlowHubDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<string, string>> GetHashesAsync(CancellationToken cancellationToken) =>
        await _db.RepoEmbeddings
            .AsNoTracking()
            .ToDictionaryAsync(r => r.RepoName, r => r.ContentHash, cancellationToken);

    public async Task UpsertAsync(
        string repoName, string contentHash, float[] embedding, CancellationToken cancellationToken)
    {
        // A read-then-write here is racy on the primary key: overlapping catalogue syncs
        // (RepoResolver calls SyncAsync per classification, and the pipeline consumers run
        // concurrently) would both see no row, both INSERT, and one would fail on the PK
        // instead of overwriting. ON CONFLICT makes last-writer-wins the actual behaviour
        // rather than the intended one.
        var vectorLiteral = RepoEmbeddingSql.ToVectorLiteral(embedding);
        var updatedAt = DateTimeOffset.UtcNow;

        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "RepoEmbeddings" ("RepoName", "ContentHash", "Embedding", "UpdatedAt")
            VALUES ({repoName}, {contentHash}, {vectorLiteral}::vector, {updatedAt})
            ON CONFLICT ("RepoName") DO UPDATE SET
                "ContentHash" = EXCLUDED."ContentHash",
                "Embedding"   = EXCLUDED."Embedding",
                "UpdatedAt"   = EXCLUDED."UpdatedAt"
            """, cancellationToken);
    }

    public async Task RemoveMissingAsync(
        IReadOnlyCollection<string> keepRepoNames, CancellationToken cancellationToken)
    {
        var keep = keepRepoNames.ToHashSet(StringComparer.Ordinal);
        await _db.RepoEmbeddings
            .Where(r => !keep.Contains(r.RepoName))
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> NearestAsync(
        float[] queryEmbedding, int limit, CancellationToken cancellationToken)
    {
        var safeLimit = Math.Clamp(limit, 1, 50);

        // float[] values are IEEE 754 floats — no SQL injection risk in the literal.
        // Mirrors EfCaptureRepository.SearchByEmbeddingAsync.
        var vectorLiteral = "[" + string.Join(",",
            queryEmbedding.Select(f => f.ToString("G", CultureInfo.InvariantCulture))) + "]";

        var rows = await _db.RepoEmbeddings
            .FromSqlInterpolated($"""
                SELECT * FROM "RepoEmbeddings"
                WHERE "Embedding" IS NOT NULL
                ORDER BY "Embedding" <=> {vectorLiteral}::vector
                LIMIT {safeLimit}
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return rows.Select(r => r.RepoName).ToList();
    }
}
```

Register it wherever the other EF repositories are registered in `FlowHub.Persistence`'s DI extension, following the pattern already used there:

```csharp
        services.AddScoped<IRepoEmbeddingStore, EfRepoEmbeddingStore>();
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/FlowHub.Persistence.Tests --filter "FullyQualifiedName~RepoEmbeddingStoreTests"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add source/FlowHub.Core/Skills/IRepoEmbeddingStore.cs source/FlowHub.Persistence tests/FlowHub.Persistence.Tests/RepoEmbeddingStoreTests.cs
git commit -m "feat(persistence): persist one embedding per bridge repo

Keyed by repo name with a content hash so a catalogue refresh only
re-embeds what changed. vector(384), matching the Captures column.

Refs #38"
```

---

### Task 3: Sync the catalog into the embedding store

**Files:**
- Create: `source/FlowHub.AI/RepoEmbeddingSynchronizer.cs`
- Test: `tests/FlowHub.Web.ComponentTests/Ai/RepoEmbeddingSynchronizerTests.cs` (create)

**Interfaces:**
- Consumes: `IBridgeCatalog.GetReposAsync` (Task 1), `IRepoEmbeddingStore` (Task 2), the existing `IEmbeddingService`.
- Produces: `RepoEmbeddingSynchronizer.SyncAsync(CancellationToken) → Task`. Task 4 calls it before shortlisting.

- [ ] **Step 1: Write the failing test**

```csharp
using FlowHub.AI;
using FlowHub.Core.Captures;
using FlowHub.Core.Skills;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class RepoEmbeddingSynchronizerTests
{
    private readonly IBridgeCatalog _catalog = Substitute.For<IBridgeCatalog>();
    private readonly IRepoEmbeddingStore _store = Substitute.For<IRepoEmbeddingStore>();
    private readonly IEmbeddingService _embeddings = Substitute.For<IEmbeddingService>();

    private RepoEmbeddingSynchronizer Sut() =>
        new(_catalog, _store, _embeddings, NullLogger<RepoEmbeddingSynchronizer>.Instance);

    private static BridgeRepo Repo(string name, string? desc = null) =>
        new(name, null, desc, [], null);

    [Fact]
    public async Task SyncAsync_NewRepo_EmbedsAndUpserts()
    {
        _catalog.GetReposAsync(Arg.Any<CancellationToken>())
            .Returns([Repo("flowhub", "Capture anything.")]);
        _store.GetHashesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());
        _embeddings.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[384]);

        await Sut().SyncAsync(default);

        await _store.Received(1).UpsertAsync("flowhub", Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_UnchangedRepo_MakesNoEmbeddingCall()
    {
        var repo = Repo("flowhub", "Capture anything.");
        _catalog.GetReposAsync(Arg.Any<CancellationToken>()).Returns([repo]);
        _store.GetHashesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["flowhub"] = RepoEmbeddingSynchronizer.HashOf(repo) });

        await Sut().SyncAsync(default);

        await _embeddings.DidNotReceive().GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().UpsertAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_ChangedDescription_ReEmbeds()
    {
        _catalog.GetReposAsync(Arg.Any<CancellationToken>())
            .Returns([Repo("flowhub", "New description.")]);
        _store.GetHashesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["flowhub"] = "stale-hash" });
        _embeddings.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[384]);

        await Sut().SyncAsync(default);

        await _embeddings.Received(1).GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_Always_PrunesReposNoLongerInTheCatalogue()
    {
        _catalog.GetReposAsync(Arg.Any<CancellationToken>()).Returns([Repo("kept")]);
        _store.GetHashesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["kept"] = "h", ["gone"] = "h" });

        await Sut().SyncAsync(default);

        await _store.Received(1).RemoveMissingAsync(
            Arg.Is<IReadOnlyCollection<string>>(k => k.Contains("kept") && !k.Contains("gone")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_EmbeddingServiceReturnsNull_SkipsWithoutThrowing()
    {
        _catalog.GetReposAsync(Arg.Any<CancellationToken>()).Returns([Repo("flowhub", "x")]);
        _store.GetHashesAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<string, string>());
        _embeddings.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((float[]?)null);

        var act = async () => await Sut().SyncAsync(default);

        await act.Should().NotThrowAsync();
        await _store.DidNotReceive().UpsertAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~RepoEmbeddingSynchronizerTests"`
Expected: FAIL — `RepoEmbeddingSynchronizer` does not exist.

- [ ] **Step 3: Implement the synchronizer**

Create `source/FlowHub.AI/RepoEmbeddingSynchronizer.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using FlowHub.Core.Captures;
using FlowHub.Core.Skills;
using Microsoft.Extensions.Logging;

namespace FlowHub.AI;

/// <summary>
/// Brings the repo-embedding store in line with the bridge catalogue. Only repositories
/// whose name+description hash changed are re-embedded, so a steady-state refresh makes
/// zero embedding calls. Best-effort throughout: a null embedding (service unconfigured
/// or failing) skips that repository rather than failing the sync.
/// </summary>
internal sealed partial class RepoEmbeddingSynchronizer
{
    private readonly IBridgeCatalog _catalog;
    private readonly IRepoEmbeddingStore _store;
    private readonly IEmbeddingService _embeddings;
    private readonly ILogger<RepoEmbeddingSynchronizer> _log;

    public RepoEmbeddingSynchronizer(
        IBridgeCatalog catalog,
        IRepoEmbeddingStore store,
        IEmbeddingService embeddings,
        ILogger<RepoEmbeddingSynchronizer> log)
    {
        _catalog = catalog;
        _store = store;
        _embeddings = embeddings;
        _log = log;
    }

    /// <summary>Embedding input for a repository — the name carries real signal (e.g. the "game-" prefix).</summary>
    internal static string TextOf(BridgeRepo repo) => $"{repo.Name}\n{repo.Desc}".TrimEnd();

    internal static string HashOf(BridgeRepo repo) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TextOf(repo))));

    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        var repos = await _catalog.GetReposAsync(cancellationToken);
        if (repos.Count == 0)
        {
            return;
        }

        var known = await _store.GetHashesAsync(cancellationToken);

        foreach (var repo in repos)
        {
            var hash = HashOf(repo);
            if (known.TryGetValue(repo.Name, out var existing) && string.Equals(existing, hash, StringComparison.Ordinal))
            {
                continue;
            }

            var embedding = await _embeddings.GenerateAsync(TextOf(repo), cancellationToken);
            if (embedding is null)
            {
                LogEmbeddingUnavailable(repo.Name);
                continue;
            }

            await _store.UpsertAsync(repo.Name, hash, embedding, cancellationToken);
        }

        await _store.RemoveMissingAsync(repos.Select(r => r.Name).ToList(), cancellationToken);
    }

    [LoggerMessage(EventId = 3020, Level = LogLevel.Debug,
        Message = "No embedding available for repo {RepoName}; skipping")]
    private partial void LogEmbeddingUnavailable(string repoName);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~RepoEmbeddingSynchronizerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add source/FlowHub.AI/RepoEmbeddingSynchronizer.cs tests/FlowHub.Web.ComponentTests/Ai/RepoEmbeddingSynchronizerTests.cs
git commit -m "feat(ai): sync the bridge catalogue into the repo embedding store

Content-hashed, so a steady-state refresh makes zero embedding calls.
Repos gone from the catalogue are pruned.

Refs #38"
```

---

### Task 4: Confirm prompt and response schema

**Files:**
- Create: `source/FlowHub.AI/AiRepoConfirmResponse.cs`
- Modify: `source/FlowHub.AI/AiPrompts.cs`
- Test: `tests/FlowHub.Web.ComponentTests/Ai/AiPromptsTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `AiRepoConfirmResponse(string? Repo, string Action, string? Title, string? Body)` and `AiPrompts.BuildRepoConfirmMessages(string content, IReadOnlyList<(string Name, string? Desc)> candidates)`. Task 5 calls both.

- [ ] **Step 1: Write the failing test**

Add to `tests/FlowHub.Web.ComponentTests/Ai/AiPromptsTests.cs`:

```csharp
    private static readonly (string Name, string? Desc)[] Candidates =
    [
        ("game-nibbles", "Faithful browser Nibbles/Snake clone"),
        ("flowhub", "Capture anything. Let AI file it for you."),
        ("bare-repo", null),
    ];

    [Fact]
    public void BuildRepoConfirmMessages_ListsEveryCandidate()
    {
        var messages = AiPrompts.BuildRepoConfirmMessages("the snake game is too fast", Candidates);

        messages[0].Text.Should().Contain("game-nibbles");
        messages[0].Text.Should().Contain("flowhub");
        messages[0].Text.Should().Contain("bare-repo");
    }

    [Fact]
    public void BuildRepoConfirmMessages_ExplicitlyPermitsNull()
    {
        // A model pushed to always choose will file on the wrong repo, which is worse
        // than not filing. The abstain must be an offered option, not an inferred one.
        var messages = AiPrompts.BuildRepoConfirmMessages("something", Candidates);

        messages[0].Text.Should().Contain("null");
    }

    [Fact]
    public void BuildRepoConfirmMessages_SecondMessageIsRawCapture()
    {
        const string content = "the snake game is too fast";

        var messages = AiPrompts.BuildRepoConfirmMessages(content, Candidates);

        messages.Should().HaveCount(2);
        messages[1].Role.Should().Be(ChatRole.User);
        messages[1].Text.Should().Be(content);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~AiPromptsTests"`
Expected: FAIL — compile error, `BuildRepoConfirmMessages` does not exist.

- [ ] **Step 3: Add the response schema**

Create `source/FlowHub.AI/AiRepoConfirmResponse.cs`:

```csharp
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace FlowHub.AI;

internal sealed record AiRepoConfirmResponse(
    [property: Description("Exact name of one listed repository, or null if none fits")]
    [property: JsonPropertyName("repo")]
    string? Repo,

    [property: Description("issue for an actionable bug or feature request; idea for an exploratory thought")]
    [property: AllowedValues("issue", "idea")]
    [property: JsonPropertyName("action")]
    string Action,

    [property: Description("3–8 word title")]
    [property: JsonPropertyName("title")]
    string? Title,

    [property: Description("Cleaned-up detail: the issue description, or the idea text")]
    [property: JsonPropertyName("body")]
    string? Body);
```

- [ ] **Step 4: Add the prompt builder**

Append to `source/FlowHub.AI/AiPrompts.cs`:

```csharp
    internal static IList<ChatMessage> BuildRepoConfirmMessages(
        string content, IReadOnlyList<(string Name, string? Desc)> candidates)
    {
        var lines = string.Join("\n", candidates.Select(c =>
            c.Desc is null ? $"  - {c.Name}" : $"  - {c.Name} — {c.Desc}"));

        var system = string.Create(CultureInfo.InvariantCulture, $$"""
            You route a developer note to one of the operator's own code repositories.

            Candidate repositories:
            {{lines}}

            Return:
            - repo: the exact name of ONE listed repository, or null if none of them fits.
                    Choosing a repository that does not fit is worse than returning null.
                    Never invent a name that is not in the list above.
            - action: "issue" for an actionable bug report, task, or concrete feature
                      request; "idea" for a fuzzy or exploratory thought
            - title: a 3–8 word title
            - body: the cleaned-up detail

            Reply ONLY via the structured response schema. Never include explanations.
            """);

        return
        [
            new ChatMessage(ChatRole.System, system),
            new ChatMessage(ChatRole.User, content),
        ];
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~AiPromptsTests"`
Expected: PASS, including the #37 prompt tests already in that class.

- [ ] **Step 6: Commit**

```bash
git add source/FlowHub.AI/AiRepoConfirmResponse.cs source/FlowHub.AI/AiPrompts.cs tests/FlowHub.Web.ComponentTests/Ai/AiPromptsTests.cs
git commit -m "feat(ai): add the repo-confirm prompt and response schema

The prompt offers null explicitly - a model pushed to always choose
files on the wrong repo, which is worse than not filing.

Refs #38"
```

---

### Task 5: Resolve the repo and wire it into the classifier

**Files:**
- Create: `source/FlowHub.AI/RepoResolver.cs`
- Modify: `source/FlowHub.AI/AiClassifier.cs`
- Modify: `source/FlowHub.AI/AiServiceCollectionExtensions.cs`
- Test: `tests/FlowHub.Web.ComponentTests/Ai/RepoResolverTests.cs` (create)

**Interfaces:**
- Consumes: `RepoEmbeddingSynchronizer.SyncAsync` (Task 3), `IRepoEmbeddingStore.NearestAsync` (Task 2), `IBridgeCatalog.GetReposAsync` (Task 1), `AiPrompts.BuildRepoConfirmMessages` + `AiRepoConfirmResponse` (Task 4).
- Produces: `RepoResolver.ResolveAsync(string content, CancellationToken) → Task<RepoResolution?>` where `RepoResolution(string Repo, BridgeAction Action, string? Title, string? Body)`. `null` means "could not resolve — park".

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using FlowHub.AI;
using FlowHub.Core.Captures;
using FlowHub.Core.Classification;
using FlowHub.Core.Skills;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class RepoResolverTests
{
    private readonly IChatClient _chat = Substitute.For<IChatClient>();
    private readonly IBridgeCatalog _catalog = Substitute.For<IBridgeCatalog>();
    private readonly IRepoEmbeddingStore _store = Substitute.For<IRepoEmbeddingStore>();
    private readonly IEmbeddingService _embeddings = Substitute.For<IEmbeddingService>();

    public RepoResolverTests()
    {
        _catalog.GetReposAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new BridgeRepo("game-nibbles", null, "Faithful browser Nibbles/Snake clone", [], null),
            new BridgeRepo("flowhub", null, "Capture anything.", [], null),
        ]);
        _store.GetHashesAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<string, string>());
        _embeddings.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[384]);
        _store.NearestAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(["game-nibbles", "flowhub"]);
    }

    private RepoResolver Sut() =>
        new(_chat, _catalog, _store, _embeddings,
            new ChatOptions { MaxOutputTokens = 300, Temperature = 0.2f },
            new RepoEmbeddingSynchronizer(_catalog, _store, _embeddings, NullLogger<RepoEmbeddingSynchronizer>.Instance),
            NullLogger<RepoResolver>.Instance);

    private void ChatReturns(object payload) =>
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(payload))));

    [Fact]
    public async Task ResolveAsync_ModelPicksAListedRepo_ReturnsIt()
    {
        ChatReturns(new { repo = "game-nibbles", action = "issue", title = "Snake too fast", body = "It speeds up." });

        var result = await Sut().ResolveAsync("the snake game is too fast", default);

        result!.Repo.Should().Be("game-nibbles");
        result.Action.Should().Be(BridgeAction.Issue);
        result.Title.Should().Be("Snake too fast");
    }

    [Fact]
    public async Task ResolveAsync_ModelAbstains_RoutesToIdeasLabAsIdea()
    {
        ChatReturns(new { repo = (string?)null, action = "idea", title = "Minigolf game", body = "Browser minigolf." });

        var result = await Sut().ResolveAsync("Game browser Minigolf", default);

        result!.Repo.Should().Be("ideas-lab");
        result.Action.Should().Be(BridgeAction.Idea);
    }

    [Fact]
    public async Task ResolveAsync_ModelNamesAnUnlistedRepo_ReturnsNull()
    {
        // The catalogue is authoritative: a name outside the shortlist is a schema
        // violation, which is what makes a hallucinated repo structurally impossible.
        ChatReturns(new { repo = "some-other-repo", action = "issue", title = "x", body = "y" });

        var result = await Sut().ResolveAsync("anything", default);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_EmbeddingsUnavailable_StillResolvesViaLexicalShortlist()
    {
        _embeddings.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((float[]?)null);
        ChatReturns(new { repo = "game-nibbles", action = "issue", title = "t", body = "b" });

        var result = await Sut().ResolveAsync("nibbles snake clone is broken", default);

        result!.Repo.Should().Be("game-nibbles");
    }

    [Fact]
    public async Task ResolveAsync_ConfirmCallThrows_ReturnsNullWithoutThrowing()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("model down"));

        var result = await Sut().ResolveAsync("anything", default);

        result.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~RepoResolverTests"`
Expected: FAIL — `RepoResolver` does not exist.

- [ ] **Step 3: Implement the resolver**

Create `source/FlowHub.AI/RepoResolver.cs`:

```csharp
using FlowHub.Core.Captures;
using FlowHub.Core.Classification;
using FlowHub.Core.Skills;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FlowHub.AI;

/// <summary>Outcome of repo inference. <c>Repo</c> is always a real target.</summary>
internal sealed record RepoResolution(string Repo, BridgeAction Action, string? Title, string? Body);

/// <summary>
/// Picks the target repository for a Bridge-classified capture that carries no alias:
/// cosine top-5 over the repo embedding store, then one LLM call that may abstain.
/// An abstain becomes an idea in <c>ideas-lab</c>; an unresolvable capture returns null
/// so the pipeline parks it. Never throws.
/// </summary>
internal sealed partial class RepoResolver
{
    internal const string IdeaFallbackRepo = "ideas-lab";
    private const int ShortlistSize = 5;

    private readonly IChatClient _chat;
    private readonly IBridgeCatalog _catalog;
    private readonly IRepoEmbeddingStore _store;
    private readonly IEmbeddingService _embeddings;
    private readonly ChatOptions _options;
    private readonly RepoEmbeddingSynchronizer _sync;
    private readonly ILogger<RepoResolver> _log;

    public RepoResolver(
        IChatClient chat,
        IBridgeCatalog catalog,
        IRepoEmbeddingStore store,
        IEmbeddingService embeddings,
        ChatOptions options,
        RepoEmbeddingSynchronizer sync,
        ILogger<RepoResolver> log)
    {
        _chat = chat;
        _catalog = catalog;
        _store = store;
        _embeddings = embeddings;
        _options = options;
        _sync = sync;
        _log = log;
    }

    public async Task<RepoResolution?> ResolveAsync(string content, CancellationToken cancellationToken)
    {
        try
        {
            await _sync.SyncAsync(cancellationToken);

            var repos = await _catalog.GetReposAsync(cancellationToken);
            if (repos.Count == 0)
            {
                return null;
            }

            var shortlist = await ShortlistAsync(content, repos, cancellationToken);
            if (shortlist.Count == 0)
            {
                return null;
            }

            var response = await _chat.GetResponseAsync<AiRepoConfirmResponse>(
                AiPrompts.BuildRepoConfirmMessages(
                    content, shortlist.Select(r => (r.Name, r.Desc)).ToList()),
                _options,
                cancellationToken: cancellationToken);

            if (!response.TryGetResult(out var payload))
            {
                return null;
            }

            var action = string.Equals(payload.Action, "issue", StringComparison.Ordinal)
                ? BridgeAction.Issue
                : BridgeAction.Idea;

            if (string.IsNullOrWhiteSpace(payload.Repo))
            {
                // No existing home — typically a request to create a project.
                return new RepoResolution(IdeaFallbackRepo, BridgeAction.Idea, payload.Title, payload.Body);
            }

            // The catalogue is authoritative: only a name we offered is acceptable.
            if (!shortlist.Any(r => string.Equals(r.Name, payload.Repo, StringComparison.Ordinal)))
            {
                LogUnlistedRepo(payload.Repo);
                return null;
            }

            return new RepoResolution(payload.Repo, action, payload.Title, payload.Body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogResolveFailed(ex.GetType().Name);
            return null;
        }
    }

    private async Task<IReadOnlyList<BridgeRepo>> ShortlistAsync(
        string content, IReadOnlyList<BridgeRepo> repos, CancellationToken cancellationToken)
    {
        var embedding = await _embeddings.GenerateAsync(content, cancellationToken);
        if (embedding is not null)
        {
            var names = await _store.NearestAsync(embedding, ShortlistSize, cancellationToken);
            var byName = repos.ToDictionary(r => r.Name, StringComparer.Ordinal);
            var hits = names
                .Where(byName.ContainsKey)
                .Select(n => byName[n])
                .ToList();

            if (hits.Count > 0)
            {
                return hits;
            }
        }

        return LexicalShortlist(content, repos);
    }

    /// <summary>
    /// Fallback when embeddings are unconfigured or the store is empty. Deliberately
    /// crude — it only has to produce plausible candidates for the model to judge.
    /// </summary>
    private static IReadOnlyList<BridgeRepo> LexicalShortlist(string content, IReadOnlyList<BridgeRepo> repos)
    {
        var terms = content
            .Split([' ', '\t', '\n', ':', ',', '.', '/', '-', '(', ')', '?', '!'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 3)
            .Select(t => t.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        return repos
            .Select(r => (Repo: r, Score: Score(r, terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Repo.LastUsed ?? DateTimeOffset.MinValue)
            .Take(ShortlistSize)
            .Select(x => x.Repo)
            .ToList();

        static int Score(BridgeRepo repo, HashSet<string> terms)
        {
            var text = $"{repo.Name} {repo.Desc}".ToLowerInvariant();
            return terms.Count(t => text.Contains(t, StringComparison.Ordinal));
        }
    }

    [LoggerMessage(EventId = 3021, Level = LogLevel.Warning,
        Message = "Repo confirm returned an unlisted repository ({Repo}); parking for triage")]
    private partial void LogUnlistedRepo(string repo);

    [LoggerMessage(EventId = 3022, Level = LogLevel.Warning,
        Message = "Repo resolution failed (reason={Reason}); parking for triage")]
    private partial void LogResolveFailed(string reason);
}
```

- [ ] **Step 4: Run the resolver tests to verify they pass**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~RepoResolverTests"`
Expected: PASS.

- [ ] **Step 5: Call the resolver from the classifier**

In `source/FlowHub.AI/AiClassifier.cs`, add an optional trailing constructor parameter — **optional and last**, so existing positional constructions in the test suite keep compiling, exactly as `allowBridgeClassification` was added in #37:

```csharp
        bool allowBridgeClassification = false,
        RepoResolver? repoResolver = null)
```

with the field `private readonly RepoResolver? _repoResolver;` set from it.

In `ClassifyAsync`, after the allow-list re-validation and before the `ClassificationResult` is built, resolve the repo when the model chose Bridge:

```csharp
            if (string.Equals(payload.MatchedSkill, "Bridge", StringComparison.Ordinal)
                && _repoResolver is not null)
            {
                var resolution = await _repoResolver.ResolveAsync(content, cancellationToken);
                if (resolution is not null)
                {
                    sw.Stop();
                    return new ClassificationResult(
                        payload.Tags,
                        "Bridge",
                        Title: resolution.Title ?? payload.Title,
                        Trace: BuildTrace(sw, response),
                        BridgeAlias: resolution.Repo,
                        BridgeAction: resolution.Action,
                        BridgeBody: resolution.Body);
                }
                // Unresolved → fall through, producing Bridge with a null alias, which
                // CaptureEnrichmentConsumer parks as "bridge candidate — repo undetermined".
            }
```

Extract the trace construction already inlined in `ClassifyAsync` into a small `BuildTrace(Stopwatch, ChatResponse<AiClassificationResponse>)` helper so both paths share it rather than duplicating the cast-heavy expression.

- [ ] **Step 6: Register the resolver**

In `source/FlowHub.AI/AiServiceCollectionExtensions.cs`, beside the `AiClassifier` registration:

```csharp
        services.AddSingleton<RepoEmbeddingSynchronizer>();
        services.AddSingleton<RepoResolver>(sp => new RepoResolver(
            sp.GetRequiredService<IChatClient>(),
            sp.GetRequiredService<IBridgeCatalog>(),
            sp.GetRequiredService<IRepoEmbeddingStore>(),
            sp.GetRequiredService<IEmbeddingService>(),
            new ChatOptions { MaxOutputTokens = maxTokens, Temperature = 0.2f },
            sp.GetRequiredService<RepoEmbeddingSynchronizer>(),
            sp.GetRequiredService<ILogger<RepoResolver>>()));
```

and pass it as the last argument to the `AiClassifier` factory — **only when the flag is on**, so a disabled deployment does no work:

```csharp
            allowBridgeClassification,
            allowBridgeClassification ? sp.GetRequiredService<RepoResolver>() : null));
```

`IRepoEmbeddingStore` is registered scoped in Task 2 while `RepoResolver` is a singleton. Resolve the store through `IServiceScopeFactory` inside `RepoResolver` if DI validation rejects the lifetime mismatch; otherwise register `EfRepoEmbeddingStore` as a singleton alongside the other AI-path singletons.

- [ ] **Step 7: Write and run the classifier integration test**

Create `tests/FlowHub.Web.ComponentTests/Ai/AiClassifierRepoInferenceTests.cs`, reusing the `Sut()`/`JsonResponse` style of `AiClassifierBridgeTests`:

```csharp
    [Fact]
    public async Task ClassifyAsync_BridgeWithoutAlias_ResolverSuppliesTheRepo()
    {
        // First call: classification returns Bridge. Second: the confirm call.
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                JsonResponse(new { tags = new[] { "dev" }, matched_skill = "Bridge", title = "t", project = (string?)null, entities = (object?)null }),
                JsonResponse(new { repo = "game-nibbles", action = "issue", title = "Snake too fast", body = "It speeds up." }));

        var result = await SutWithResolver().ClassifyAsync("the snake game is too fast", default);

        result.MatchedSkill.Should().Be("Bridge");
        result.BridgeAlias.Should().Be("game-nibbles");
        result.BridgeAction.Should().Be(BridgeAction.Issue);
    }

    [Fact]
    public async Task ClassifyAsync_ResolverReturnsNull_LeavesAliasNullForParking()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                JsonResponse(new { tags = new[] { "dev" }, matched_skill = "Bridge", title = "t", project = (string?)null, entities = (object?)null }),
                JsonResponse(new { repo = "not-in-shortlist", action = "issue", title = "t", body = "b" }));

        var result = await SutWithResolver().ClassifyAsync("something", default);

        result.MatchedSkill.Should().Be("Bridge");
        result.BridgeAlias.Should().BeNull();
        result.BridgeAction.Should().Be(BridgeAction.Unknown);
    }
```

`SutWithResolver()` builds an `AiClassifier` with `allowBridgeClassification: true` and a `RepoResolver` wired to the same substituted `IChatClient`, plus substituted catalog/store/embeddings returning one candidate named `game-nibbles`.

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~AiClassifier"`
Expected: PASS, including all #37 tests.

- [ ] **Step 8: Commit**

```bash
git add source/FlowHub.AI tests/FlowHub.Web.ComponentTests/Ai
git commit -m "feat(ai): resolve the target repo for an alias-free Bridge capture

Cosine top-5 over the repo embedding store, then one LLM call that may
abstain. A name outside the shortlist is rejected, so a hallucinated
repo is structurally impossible. An abstain becomes an idea in
ideas-lab; an unresolved capture leaves the alias null and parks.

Refs #38"
```

---

### Task 6: Full suite and documentation

**Files:**
- Modify: `CHANGELOG.md`
- Test: the whole suite

**Interfaces:**
- Consumes: everything from Tasks 1–5.
- Produces: nothing.

- [ ] **Step 1: Run the affected suites**

Per repo convention, run per-project rather than a solution-wide `just test`:

```bash
dotnet test tests/FlowHub.Web.ComponentTests
dotnet test tests/FlowHub.Skills.Tests
dotnet test tests/FlowHub.Core.Tests
dotnet test tests/FlowHub.Persistence.Tests
```

Expected: all green.

- [ ] **Step 2: Add the CHANGELOG entry**

Under `## [Unreleased]` → `### Added` in `CHANGELOG.md`:

```markdown
- Repo inference for Bridge captures that carry no alias: cosine shortlist over the bridge catalogue, confirmed by the model, with an abstain routed to `ideas-lab` as an idea. Active only when `Ai:EnableBridgeClassification` is enabled. (#38)
```

- [ ] **Step 3: Commit**

```bash
git add CHANGELOG.md
git commit -m "docs(changelog): note repo inference for alias-free captures

Refs #38"
```

---

## Verification

The whole path is inert unless `Ai:EnableBridgeClassification` is on. To exercise it locally:

```bash
Ai__EnableBridgeClassification=true dotnet run --project source/FlowHub.Web
```

Submit `the snake game is too fast` and confirm the capture resolves to a `game-*` repo rather than parking; submit `Game browser Minigolf` and confirm it becomes an idea in `ideas-lab`.

**End-to-end verification against a real forge needs #36** — Skills configured and `bridge serve` running. Until then the store, the shortlist and the confirm call are exercisable, but nothing reaches a forge.

**Corpus check, not a test:** re-run the 53 candidates from `docs/ai-notes/2026-08-25-telegram-capture-taxonomy.md` §10 through the finished path and compare against the hand labels. The prototype scored ~10 right / 8 wrong / 9 no-match; anything short of a clear improvement means the shortlist, not the model, is the problem.
