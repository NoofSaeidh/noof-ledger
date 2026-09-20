# Phase 0b — Authentication and the Data Layer — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A single-user login that can be switched on without touching a route, over a real `app_user` row in PostgreSQL, with every seam covered by tests that run rather than skip.

**Architecture:** `Auth:Mode` (`Off` | `Cookie`, default `Off`) selects *which authentication handler is registered*, never which middleware runs. `UseAuthentication`, `UseAuthorization`, `AddCascadingAuthenticationState` and `[Authorize]` ship unconditionally from the first commit, so **no route's metadata depends on the flag** and a forgotten flag cannot leave a route ungated. Under `Off` a ~15-line handler authenticates every request as the local owner. A startup guard reads the addresses Kestrel *actually bound* and refuses to run non-loopback while `Auth:Mode=Off`.

**Tech Stack:** .NET 10 (SDK 10.0.204) · PostgreSQL 18 · EF Core 10 + Npgsql · xUnit v3 on Microsoft Testing Platform · AwesomeAssertions · NSubstitute

**Spec:** `docs/superpowers/specs/2026-09-19-noof-finance-design.md` §15 (auth addendum)

**Predecessor:** Phase 0, merged at `8443961`. 65 tests green.

---

## Global Constraints

Every task's requirements implicitly include this section. Values are exact.

- **`Noof.Ledger.Domain` has zero `PackageReference`.** Asserted by an existing test. `AppUser` is BCL types only.
- **`Noof.Ledger.Web` is UI only** — no `DbContext`, no EF type, no `HttpClient`, **no `Program.cs`**. Its `ProjectReference` set stays exactly `{Noof.Ledger.Application, Noof.Ledger.Domain}`. It gains exactly one package: `Microsoft.AspNetCore.Components.Authorization` at **10.0.8**, lockstep with `Components.Web`.
- **Central Package Management.** Never write `Version=` on a `PackageReference`; add a `PackageVersion` to `Directory.Packages.props`. Inline versions are NU1008.
- **`TreatWarningsAsErrors=true`.** Consequences that will bite:
  - `xUnit1051` — every async call in a test must pass `TestContext.Current.CancellationToken`. It is a warning by default and an **error** here.
  - `CS0618` — the 4-argument `AuthenticationHandler` ctor taking `ISystemClock` is obsolete and **fails the build**. Use the 3-arg `(IOptionsMonitor<T>, ILoggerFactory, UrlEncoder)` overload. Every Microsoft sample shows the wrong one.
  - `IDE0161` — **file-scoped namespaces are enforced by the compiler.** `dotnet ef migrations add` emits block-scoped namespaces and will **fail the build** until converted. Convert it; do not disable the rule.
- **Never call `EnsureCreated()`** anywhere, including test helpers. It bypasses migrations and permanently poisons that database for `Migrate()`.
- **Tests run against real PostgreSQL**, never the EF InMemory provider. Postgres is reachable; credentials resolve from `NOOF_TEST_PG` then `%LOCALAPPDATA%\NoofLedger\db.connection`. **Do not write skipped tests** — the credential problem that motivated `Assert.SkipUnless` is solved.
- **`[CollectionDefinition]` must live in the same assembly as the tests that name it.** A definition in `Noof.Ledger.TestKit` fails at runtime with *"did not have matching fixture data"*. Each DB-touching test assembly gets its own wrapper.
- **Any startup-time database touch must be config-gated.** Code between `builder.Build()` and `app.Run()` genuinely executes under `WebApplicationFactory`. Add `Database:MigrateOnStartup` (bool, **default `true`** in `appsettings.json`); tests pass `false` via `UseSetting`.
- **Do not assert a specific inner exception type** for a dead-database connection. One party observed `SocketException`, another `TimeoutException` for the identical scenario. Catch `NpgsqlException`/`DbException` broadly.
- **Secrets — public repo.** No password in `appsettings.json`, the repo, a log, an exception message or an LLM prompt. The first user is created by CLI only.
- **Code style.** File-scoped namespaces, primary constructors, `required`/`init`, pattern matching, collection expressions. Avoid comments; a comment that explains *what* is a defect. No XML doc blocks on private/internal members.

---

## Review Focus

Input classes the spec implies that no task's happy path exercises. Each is assigned to the task that owns the code.

1. **A login POST with no antiforgery token must be rejected.** `.WithMetadata(new RequireAntiforgeryTokenAttribute())` alone does **not** protect a `MapPost` — verified returning 200 with no token supplied. Binding via `[FromForm]` parameters is what engages validation, and the failure is silent. → Task 11.
2. **A wrong password must not produce an auth cookie.** The obvious bug is redirecting back to the login page while still signing the user in. → Task 11.
3. **`/healthz` must stay anonymous under BOTH modes.** The "secure by default" reflex adds a global `FallbackPolicy`, which breaks the deploy script's post-publish poll. → Task 10, asserted again in Task 12.
4. **A username that differs only by case must not create a second user.** `UPSERT` on a case-sensitive unique index silently produces two accounts. → Task 3 (the `lower(username)` index) and Task 8 (proved against a real database).
5. **The app must boot with PostgreSQL unreachable** when `Database:MigrateOnStartup=false`, and fail fast rather than hang when `true`. Every integration test depends on the first half. → Task 10.

---

## Task 1: Domain — `AppUser`

**Files:**
- Create: `src/Noof.Ledger.Domain/AppUser.cs`
- Create: `tests/Noof.Ledger.Domain.Tests/AppUserTests.cs`

**Interfaces:**
- Produces: `AppUser` with `Guid Id`, `string Username`, `string PasswordHash`, `DateTimeOffset CreatedAt`. `PasswordHash` has a public setter — EF change tracking needs one, and `set-password` updates it in place.

- [ ] **Step 1: Write the failing test**

```csharp
using AwesomeAssertions;

namespace Noof.Ledger.Domain.Tests;

public class AppUserTests
{
    [Fact]
    public void Round_trips_its_properties()
    {
        var created = DateTimeOffset.UtcNow;
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Username = "noof",
            PasswordHash = "hash",
            CreatedAt = created,
        };

        user.Username.Should().Be("noof");
        user.PasswordHash.Should().Be("hash");
        user.CreatedAt.Should().Be(created);
    }

    [Fact]
    public void Password_hash_can_be_replaced_without_rebuilding_the_user()
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Username = "noof",
            PasswordHash = "old",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        user.PasswordHash = "new";

        user.PasswordHash.Should().Be("new");
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Domain.Tests/Noof.Ledger.Domain.Tests.csproj`
Expected: build error, `AppUser` does not exist.

- [ ] **Step 3: Write the minimal implementation**

```csharp
namespace Noof.Ledger.Domain;

public sealed class AppUser
{
    public required Guid Id { get; init; }
    public required string Username { get; init; }
    public required string PasswordHash { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test --project tests/Noof.Ledger.Domain.Tests/Noof.Ledger.Domain.Tests.csproj`
Expected: PASS. The existing `Domain_has_no_package_references` architecture test must also stay green — run `dotnet test --solution NoofLedger.slnx`.

- [ ] **Step 5: Commit**

```bash
git add src/Noof.Ledger.Domain/AppUser.cs tests/Noof.Ledger.Domain.Tests/AppUserTests.cs
git commit -m "feat(domain): AppUser"
```

---

## Task 2: Application — the auth ports

**Files:**
- Create: `src/Noof.Ledger.Application/Auth/IUserStore.cs`
- Create: `src/Noof.Ledger.Application/Auth/IPasswordHasher.cs`
- Create: `src/Noof.Ledger.Application/Auth/PasswordVerifyResult.cs`

**Interfaces:**
- Consumes: `AppUser` (Task 1).
- Produces — later tasks depend on these signatures **exactly**:

```csharp
Task<AppUser?> IUserStore.FindByUsernameAsync(string username, CancellationToken cancellationToken)
Task IUserStore.UpsertAsync(AppUser user, CancellationToken cancellationToken)
string IPasswordHasher.Hash(AppUser user, string password)
PasswordVerifyResult IPasswordHasher.Verify(AppUser user, string hash, string password)
enum PasswordVerifyResult { Failed, Success, SuccessRehashNeeded }
```

**Why `PasswordVerifyResult` is our own enum:** it deliberately mirrors ASP.NET Core's `PasswordVerificationResult` rather than referencing it. `Noof.Ledger.Application` must not take a dependency on `Microsoft.Extensions.Identity.Core`; the adapter that does live in Host.

- [ ] **Step 1: Write the failing test**

`tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs` already asserts Application's reference set. Add to that file:

```csharp
    [Fact]
    public void Application_has_no_package_references()
    {
        Packages("Noof.Ledger.Application").Should().BeEmpty();
    }
```

- [ ] **Step 2: Run it**

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`
Expected: PASS immediately — Application has no packages today. This test exists to **fail later** if someone reaches for `Microsoft.Extensions.Identity.Core` while writing the store.

- [ ] **Step 3: Write the ports**

```csharp
// src/Noof.Ledger.Application/Auth/PasswordVerifyResult.cs
namespace Noof.Ledger.Application.Auth;

public enum PasswordVerifyResult
{
    Failed,
    Success,
    SuccessRehashNeeded,
}
```

```csharp
// src/Noof.Ledger.Application/Auth/IPasswordHasher.cs
using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Auth;

public interface IPasswordHasher
{
    string Hash(AppUser user, string password);

    PasswordVerifyResult Verify(AppUser user, string hash, string password);
}
```

```csharp
// src/Noof.Ledger.Application/Auth/IUserStore.cs
using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Auth;

public interface IUserStore
{
    Task<AppUser?> FindByUsernameAsync(string username, CancellationToken cancellationToken);

    Task UpsertAsync(AppUser user, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Run the full suite**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: all green.

- [ ] **Step 5: Commit**

```bash
git add src/Noof.Ledger.Application/Auth tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs
git commit -m "feat(application): auth ports, with a test pinning Application's zero-package rule"
```

---

## Task 3: Persistence — entity configurations and the case-insensitive username

**Files:**
- Create: `src/Noof.Ledger.Persistence/Configurations/AppUserConfiguration.cs`
- Create: `src/Noof.Ledger.Persistence/Configurations/MoneyProbeEntityConfiguration.cs`
- Modify: `src/Noof.Ledger.Persistence/LedgerDbContext.cs`
- Create: `tests/Noof.Ledger.Persistence.Tests/AppUserModelTests.cs`

**Interfaces:**
- Consumes: `AppUser` (Task 1).
- Produces: table `app_user`, columns `id`, `username`, `password_hash`, `created_at`. `DbSet<AppUser> Users` on `LedgerDbContext`.

**Two changes, one reason.** `LedgerDbContext.OnModelCreating` currently configures `MoneyProbeEntity` inline. Adding a second entity inline is the moment that stops scaling, so both move to `IEntityTypeConfiguration` classes and the context switches to `ApplyConfigurationsFromAssembly`. Move the probe's configuration **unchanged** — this task must not alter the existing money mapping, and the existing `MigrationContractTests` proves it did not.

**The username index is case-insensitive.** A plain unique index lets `Noof` and `noof` both exist, which on a single-user app means silently locking yourself out of your own account. Use a functional index on `lower(username)`.

- [ ] **Step 1: Write the failing test**

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Tests;

public class AppUserModelTests
{
    static LedgerDbContext BuildOfflineContext()
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none")
            .Options;

        return new LedgerDbContext(options);
    }

    [Fact]
    public void Maps_to_the_app_user_table_with_snake_case_columns()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(AppUser))!;

        entity.GetTableName().Should().Be("app_user");
        entity.GetProperty(nameof(AppUser.Id)).GetColumnName().Should().Be("id");
        entity.GetProperty(nameof(AppUser.Username)).GetColumnName().Should().Be("username");
        entity.GetProperty(nameof(AppUser.PasswordHash)).GetColumnName().Should().Be("password_hash");
        entity.GetProperty(nameof(AppUser.CreatedAt)).GetColumnName().Should().Be("created_at");
    }

    [Fact]
    public void Every_column_is_required_and_bounded()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(AppUser))!;

        entity.GetProperty(nameof(AppUser.Username)).IsNullable.Should().BeFalse();
        entity.GetProperty(nameof(AppUser.Username)).GetMaxLength().Should().Be(64);
        entity.GetProperty(nameof(AppUser.PasswordHash)).IsNullable.Should().BeFalse();
        entity.GetProperty(nameof(AppUser.CreatedAt)).GetColumnType().Should().Be("timestamptz");
    }

    [Fact]
    public void The_primary_key_is_id()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(AppUser))!;

        entity.FindPrimaryKey()!.Properties.Select(p => p.Name).Should().Equal(nameof(AppUser.Id));
    }

    [Fact]
    public void The_money_probe_mapping_is_unchanged_by_the_move_to_configuration_classes()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(MoneyProbeEntity))!;
        var money = entity.GetComplexProperties().Single();

        entity.GetTableName().Should().Be("money_probe_entities");
        money.ComplexType.FindProperty("Amount")!.GetColumnName().Should().Be("amount");
        money.ComplexType.FindProperty("Currency")!.GetMaxLength().Should().Be(3);
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: FAIL — `FindEntityType(typeof(AppUser))` returns null.

- [ ] **Step 3: Write the configurations**

```csharp
// src/Noof.Ledger.Persistence/Configurations/AppUserConfiguration.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.ToTable("app_user");

        builder.HasKey(u => u.Id);

        builder.Property(u => u.Id).HasColumnName("id");
        builder.Property(u => u.Username).HasColumnName("username").HasMaxLength(64).IsRequired();
        builder.Property(u => u.PasswordHash).HasColumnName("password_hash").IsRequired();
        builder.Property(u => u.CreatedAt).HasColumnName("created_at");
    }
}
```

> **Deliberately no `HasIndex` here.** The uniqueness this table needs is on `lower(username)`, and EF Core cannot express a functional index in the fluent model. Declaring a fluent index *as well* would put a second, case-**sensitive** unique index on the same column — which is both redundant and wrong, since it would permit exactly the `Noof`/`noof` pair the functional index exists to forbid. The index is created as raw SQL in the migration (Task 4 Step 2), and Task 8 proves against a real database that the collision is actually rejected.
>
> Consequence to expect, not to fix: `GenerateCreateScript()` renders the *model*, so the golden DDL snapshot in Task 4 will **not** contain this index. That is why Task 8's `A_second_user_differing_only_by_case_is_rejected_by_the_database` exists — it is the only thing covering it, and it must not be deleted as redundant.

Move the existing `MoneyProbeEntity` block out of `OnModelCreating` verbatim into `Configurations/MoneyProbeEntityConfiguration.cs` as `internal sealed class MoneyProbeEntityConfiguration : IEntityTypeConfiguration<MoneyProbeEntity>`.

Then `LedgerDbContext` becomes:

```csharp
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence;

public class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<MoneyProbeEntity> MoneyProbes => Set<MoneyProbeEntity>();

    public DbSet<AppUser> Users => Set<AppUser>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<decimal>().HavePrecision(19, 4);
        builder.Properties<DateTimeOffset>().HaveColumnType("timestamptz");
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("public");
        builder.ApplyConfigurationsFromAssembly(typeof(LedgerDbContext).Assembly);
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: the four new tests PASS. `MigrationContractTests.HasPendingModelChanges` now **FAILS** — that is correct, the model changed and Task 4 adds the migration.

- [ ] **Step 5: Commit**

```bash
git add src/Noof.Ledger.Persistence tests/Noof.Ledger.Persistence.Tests/AppUserModelTests.cs
git commit -m "feat(persistence): app_user mapping, and move entity config out of OnModelCreating"
```

---

## Task 4: Persistence — the `AppUser` migration and the golden DDL gate

**Files:**
- Create: `src/Noof.Ledger.Persistence/Migrations/<timestamp>_AddAppUser.cs` (generated)
- Create: `tests/Noof.Ledger.Persistence.Tests/SchemaSnapshotTests.cs`
- Create: `tests/Noof.Ledger.Persistence.Tests/schema.expected.sql`

**Interfaces:**
- Consumes: the model from Task 3.

**Why the golden DDL file.** `HasPendingModelChanges()` catches "you forgot to run `migrations add`". It does **not** catch a migration that generates the wrong SQL. A committed `GenerateCreateScript()` snapshot does, entirely offline, and is the highest-value schema regression test available without a database.

- [ ] **Step 1: Generate the migration**

```bash
dotnet ef migrations add AddAppUser --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence
```

This runs offline against the design-time factory. **The generated file will have a block-scoped namespace and will fail the build with `IDE0161`.** Convert it to file-scoped. Do not disable the rule.

- [ ] **Step 2: Add the case-insensitive unique index by hand**

EF cannot express a functional index, and Task 3 deliberately declared none, so the generated migration will contain **no** index on `username`. Append to the generated `Up`, after the `CreateTable`:

```csharp
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX ix_app_user_username_lower
                    ON public.app_user (lower(username));
                """);
```

and in `Down`:

```csharp
            migrationBuilder.Sql("DROP INDEX IF EXISTS public.ix_app_user_username_lower;");
```

- [ ] **Step 3: Write the failing snapshot test**

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace Noof.Ledger.Persistence.Tests;

public class SchemaSnapshotTests
{
    [Fact]
    public void The_generated_schema_matches_the_committed_snapshot()
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none")
            .Options;

        using var db = new LedgerDbContext(options);

        var actual = Normalise(db.Database.GenerateCreateScript());
        var expected = Normalise(File.ReadAllText(SnapshotPath));

        actual.Should().Be(expected,
            "the schema changed; review the diff and update schema.expected.sql deliberately");
    }

    static string SnapshotPath =>
        Path.Combine(AppContext.BaseDirectory, "schema.expected.sql");

    static string Normalise(string sql) =>
        string.Join('\n', sql.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd())).Trim();
}
```

Add to `Noof.Ledger.Persistence.Tests.csproj`:

```xml
  <ItemGroup>
    <None Update="schema.expected.sql" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 4: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: FAIL — `schema.expected.sql` does not exist.

- [ ] **Step 5: Generate the snapshot, then read it before trusting it**

```bash
dotnet ef migrations script --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence --output /tmp/check.sql
```

Write `tests/Noof.Ledger.Persistence.Tests/schema.expected.sql` from `GenerateCreateScript()` output. **Read it and confirm by eye** that `app_user.username` is `character varying(64) NOT NULL`, `created_at` is `timestamp with time zone`, and the money columns are still `numeric(19,4)` and `character varying(3)`. A snapshot committed without reading it asserts nothing.

- [ ] **Step 6: Apply the migration and run everything**

```bash
dotnet ef database update --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence
dotnet test --solution NoofLedger.slnx
```

Expected: all green, including `MigrationContractTests`.

- [ ] **Step 7: Update the test template database**

`PostgresFixture` clones `noof_ledger_test_template`, so the template must carry the new schema:

```bash
dotnet ef database update --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence --connection "$(powershell -NoProfile -Command "(Get-Content \$env:LOCALAPPDATA\NoofLedger\db.connection) -replace 'Database=postgres','Database=noof_ledger_test_template'")"
```

Then re-run `dotnet test --solution NoofLedger.slnx` and confirm still green.

- [ ] **Step 8: Commit**

```bash
git add src/Noof.Ledger.Persistence/Migrations tests/Noof.Ledger.Persistence.Tests
git commit -m "feat(persistence): AddAppUser migration with a case-insensitive username index and a golden DDL gate"
```

---

## Task 5: Host — `PasswordHasherAdapter`

**Files:**
- Create: `src/Noof.Ledger.Host/Auth/PasswordHasherAdapter.cs`
- Create: `tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
- Create: `tests/Noof.Ledger.Host.Tests/PasswordHasherAdapterTests.cs`
- Modify: `NoofLedger.slnx`

**Interfaces:**
- Consumes: `IPasswordHasher`, `PasswordVerifyResult` (Task 2), `AppUser` (Task 1).
- Produces: `PasswordHasherAdapter : IPasswordHasher`.

**Why this lives in Host and not Persistence.** `PasswordHasher<TUser>` ships in the shared framework. `Microsoft.NET.Sdk.Web` carries an implicit `FrameworkReference` to `Microsoft.AspNetCore.App`; `Noof.Ledger.Persistence` is a plain `Microsoft.NET.Sdk` library and would need an explicit `<FrameworkReference>` to see it. Putting the adapter in Host keeps **zero new NuGet** in both.

**This task also creates the Host test project**, which does not exist. Roughly half the auth surface lands in Host, so without it the auth layer ships untested no matter how good the Domain suite is.

- [ ] **Step 1: Create the test project**

```bash
dotnet new xunit3 -o tests/Noof.Ledger.Host.Tests
dotnet sln NoofLedger.slnx add tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj
dotnet add tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj reference src/Noof.Ledger.Host/Noof.Ledger.Host.csproj
```

**The template emits inline `Version=` attributes on its `PackageReference` items.** Strip every one of them (NU1008 under CPM) and confirm each package already has a `PackageVersion` in `Directory.Packages.props`. Add `AwesomeAssertions` and `NSubstitute` references without versions.

- [ ] **Step 2: Write the failing test**

```csharp
using AwesomeAssertions;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Domain;
using Noof.Ledger.Host.Auth;

namespace Noof.Ledger.Host.Tests;

public class PasswordHasherAdapterTests
{
    static readonly AppUser User = new()
    {
        Id = Guid.NewGuid(),
        Username = "noof",
        PasswordHash = string.Empty,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void A_hash_verifies_against_its_own_password()
    {
        var hasher = new PasswordHasherAdapter();
        var hash = hasher.Hash(User, "correct horse battery staple");

        hasher.Verify(User, hash, "correct horse battery staple").Should().Be(PasswordVerifyResult.Success);
    }

    [Fact]
    public void A_wrong_password_fails()
    {
        var hasher = new PasswordHasherAdapter();
        var hash = hasher.Hash(User, "correct horse battery staple");

        hasher.Verify(User, hash, "wrong").Should().Be(PasswordVerifyResult.Failed);
    }

    [Fact]
    public void The_same_password_hashes_differently_every_time()
    {
        var hasher = new PasswordHasherAdapter();

        hasher.Hash(User, "same").Should().NotBe(hasher.Hash(User, "same"));
    }

    [Fact]
    public void A_malformed_hash_fails_rather_than_throwing()
    {
        var hasher = new PasswordHasherAdapter();

        hasher.Verify(User, "not-a-hash", "whatever").Should().Be(PasswordVerifyResult.Failed);
    }
}
```

- [ ] **Step 3: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: build error, `PasswordHasherAdapter` does not exist.

- [ ] **Step 4: Write the adapter**

```csharp
using Microsoft.AspNetCore.Identity;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Host.Auth;

public sealed class PasswordHasherAdapter : IPasswordHasher
{
    readonly PasswordHasher<AppUser> hasher = new();

    public string Hash(AppUser user, string password) => hasher.HashPassword(user, password);

    public PasswordVerifyResult Verify(AppUser user, string hash, string password)
    {
        try
        {
            return hasher.VerifyHashedPassword(user, hash, password) switch
            {
                PasswordVerificationResult.Success => PasswordVerifyResult.Success,
                PasswordVerificationResult.SuccessRehashNeeded => PasswordVerifyResult.SuccessRehashNeeded,
                _ => PasswordVerifyResult.Failed,
            };
        }
        catch (FormatException)
        {
            return PasswordVerifyResult.Failed;
        }
    }
}
```

> The `catch` is narrow and load-bearing: `VerifyHashedPassword` throws `FormatException` on a hash that is not valid base64, which a corrupted or hand-edited `password_hash` column would produce. Letting that escape turns a bad row into a 500 on the login page.

- [ ] **Step 5: Run the tests**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add tests/Noof.Ledger.Host.Tests src/Noof.Ledger.Host/Auth NoofLedger.slnx Directory.Packages.props
git commit -m "feat(host): password hashing adapter over the framework hasher, plus the Host test project"
```

---

## Task 6: Host — `LocalOwnerHandler`

**Files:**
- Create: `src/Noof.Ledger.Host/Auth/LocalOwnerHandler.cs`
- Create: `tests/Noof.Ledger.Host.Tests/LocalOwnerHandlerTests.cs`

**Interfaces:**
- Produces: `LocalOwnerHandler`, and the scheme name constant `AuthSchemes.LocalOwner = "LocalOwner"`.

**The constructor is not a free choice.** The 4-argument `AuthenticationHandler<T>` ctor taking `ISystemClock` is `[Obsolete]`, which `TreatWarningsAsErrors` turns into **CS0618 build errors**. Use the 3-argument overload. Every Microsoft sample online shows the obsolete form.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Security.Claims;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Noof.Ledger.Host.Auth;

namespace Noof.Ledger.Host.Tests;

public class LocalOwnerHandlerTests
{
    static LocalOwnerHandler CreateHandler()
    {
        var options = Options.Create(new AuthenticationSchemeOptions());
        var monitor = new StaticOptionsMonitor(options.Value);

        return new LocalOwnerHandler(monitor, NullLoggerFactory.Instance, UrlEncoder.Default);
    }

    [Fact]
    public async Task Authenticates_every_request_as_the_local_owner()
    {
        var handler = CreateHandler();
        var scheme = new AuthenticationScheme(AuthSchemes.LocalOwner, null, typeof(LocalOwnerHandler));
        await handler.InitializeAsync(scheme, new DefaultHttpContext());

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeTrue();
        result.Principal!.Identity!.IsAuthenticated.Should().BeTrue();
        result.Principal.FindFirst(ClaimTypes.Name)!.Value.Should().Be("local owner");
        result.Principal.FindFirst(ClaimTypes.NameIdentifier).Should().NotBeNull();
    }

    sealed class StaticOptionsMonitor(AuthenticationSchemeOptions value)
        : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue => value;

        public AuthenticationSchemeOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: build error, `LocalOwnerHandler` does not exist.

- [ ] **Step 3: Write the handler**

```csharp
// src/Noof.Ledger.Host/Auth/AuthSchemes.cs
namespace Noof.Ledger.Host.Auth;

public static class AuthSchemes
{
    public const string LocalOwner = "LocalOwner";
    public const string Cookie = "Cookie";
}
```

```csharp
// src/Noof.Ledger.Host/Auth/LocalOwnerHandler.cs
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Noof.Ledger.Host.Auth;

public sealed class LocalOwnerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, "local-owner"),
            new(ClaimTypes.Name, "local owner"),
        ];

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: all green. **The build succeeding is itself the assertion** that the non-obsolete constructor was used.

- [ ] **Step 5: Commit**

```bash
git add src/Noof.Ledger.Host/Auth tests/Noof.Ledger.Host.Tests/LocalOwnerHandlerTests.cs
git commit -m "feat(host): LocalOwnerHandler for Auth:Mode=Off"
```

---

## Task 7: Host — the loopback guard

**Files:**
- Create: `src/Noof.Ledger.Host/Startup/LoopbackGuard.cs`
- Create: `tests/Noof.Ledger.Host.Tests/LoopbackGuardTests.cs`

**Interfaces:**
- Produces: `static void LoopbackGuard.AssertSafe(IReadOnlyList<string> boundAddresses, string authMode)`. Throws `InvalidOperationException` when any address is non-loopback while `authMode` is `Off`.

**A pure function, deliberately.** The caller (Task 9) passes the addresses Kestrel *actually bound*, read from `IServerAddressesFeature` in `ApplicationStarted`. Re-deriving Kestrel's precedence across `ASPNETCORE_URLS`, `Kestrel:Endpoints` and `launchSettings.json` by hand is a bug source; observing what it bound is not.

- [ ] **Step 1: Write the failing test**

```csharp
using AwesomeAssertions;
using Noof.Ledger.Host.Startup;

namespace Noof.Ledger.Host.Tests;

public class LoopbackGuardTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://localhost:5000")]
    [InlineData("http://[::1]:5000")]
    [InlineData("https://127.0.0.1:5001")]
    public void Loopback_is_allowed_while_auth_is_off(string address)
    {
        var act = () => LoopbackGuard.AssertSafe([address], "Off");

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("http://0.0.0.0:5000")]
    [InlineData("http://+:5000")]
    [InlineData("http://*:5000")]
    [InlineData("http://192.168.1.10:5000")]
    [InlineData("http://noof-desktop:5000")]
    public void A_non_loopback_binding_refuses_to_run_while_auth_is_off(string address)
    {
        var act = () => LoopbackGuard.AssertSafe([address], "Off");

        act.Should().Throw<InvalidOperationException>().WithMessage("*Auth:Mode*");
    }

    [Theory]
    [InlineData("http://0.0.0.0:5000")]
    [InlineData("http://192.168.1.10:5000")]
    [InlineData("http://+:5000")]
    public void The_same_bindings_are_fine_once_auth_is_on(string address)
    {
        var act = () => LoopbackGuard.AssertSafe([address], "Cookie");

        act.Should().NotThrow();
    }

    [Fact]
    public void One_non_loopback_address_among_several_still_refuses()
    {
        var act = () => LoopbackGuard.AssertSafe(["http://127.0.0.1:5000", "http://192.168.1.10:5000"], "Off");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void No_bound_addresses_is_not_treated_as_safe_by_accident()
    {
        var act = () => LoopbackGuard.AssertSafe([], "Off");

        act.Should().NotThrow();
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: build error, `LoopbackGuard` does not exist.

- [ ] **Step 3: Write the guard**

```csharp
using System.Net;

namespace Noof.Ledger.Host.Startup;

public static class LoopbackGuard
{
    public static void AssertSafe(IReadOnlyList<string> boundAddresses, string authMode)
    {
        if (!string.Equals(authMode, "Off", StringComparison.OrdinalIgnoreCase))
            return;

        var exposed = boundAddresses.Where(address => !IsLoopback(address)).ToArray();

        if (exposed.Length is 0)
            return;

        throw new InvalidOperationException(
            $"Refusing to start: {string.Join(", ", exposed)} is reachable beyond this machine while " +
            $"Auth:Mode=Off. Set Auth:Mode=Cookie and create a user with `Noof.Ledger.Host.exe user set-password <name>`.");
    }

    static bool IsLoopback(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host;

        if (host is "+" or "*")
            return false;

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }
}
```

> `Uri` parses `http://+:5000` with `Host` of `+`, and `http://*:5000` with `*`. Both are Kestrel wildcards meaning "every interface", and neither parses as an `IPAddress`, so the explicit check is what stops them being treated as an unknown hostname.

- [ ] **Step 4: Run the tests**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: all green.

- [ ] **Step 5: Commit**

```bash
git add src/Noof.Ledger.Host/Startup tests/Noof.Ledger.Host.Tests/LoopbackGuardTests.cs
git commit -m "feat(host): loopback guard as a pure function over the addresses Kestrel bound"
```

---

## Task 8: Persistence — `EfUserStore`

**Files:**
- Create: `src/Noof.Ledger.Persistence/Auth/EfUserStore.cs`
- Create: `tests/Noof.Ledger.Persistence.Tests/EfUserStoreTests.cs`

**Interfaces:**
- Consumes: `IUserStore` (Task 2), `LedgerDbContext` (Task 3).
- Produces: `EfUserStore(LedgerDbContext db) : IUserStore`.

These tests **run against real PostgreSQL** through the existing `PostgresFixture` and `[Collection("postgres")]`.

- [ ] **Step 1: Write the failing test**

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Auth;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfUserStoreTests(PostgresFixture fixture)
{
    static AppUser NewUser(string username) => new()
    {
        Id = Guid.NewGuid(),
        Username = username,
        PasswordHash = "hash",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Round_trips_a_user()
    {
        await using var db = await fixture.CreateContextAsync();
        var store = new EfUserStore(db);

        await store.UpsertAsync(NewUser("noof"), TestContext.Current.CancellationToken);
        var found = await store.FindByUsernameAsync("noof", TestContext.Current.CancellationToken);

        found.Should().NotBeNull();
        found!.Username.Should().Be("noof");
    }

    [Fact]
    public async Task Finds_a_user_regardless_of_case()
    {
        await using var db = await fixture.CreateContextAsync();
        var store = new EfUserStore(db);

        await store.UpsertAsync(NewUser("Noof"), TestContext.Current.CancellationToken);

        var found = await store.FindByUsernameAsync("nOOf", TestContext.Current.CancellationToken);

        found.Should().NotBeNull();
    }

    [Fact]
    public async Task A_second_user_differing_only_by_case_is_rejected_by_the_database()
    {
        await using var db = await fixture.CreateContextAsync();
        var store = new EfUserStore(db);

        await store.UpsertAsync(NewUser("noof"), TestContext.Current.CancellationToken);

        var act = async () => await store.UpsertAsync(NewUser("NOOF"), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Upserting_an_existing_username_replaces_the_hash_instead_of_adding_a_row()
    {
        await using var db = await fixture.CreateContextAsync();
        var store = new EfUserStore(db);
        var user = NewUser("noof");

        await store.UpsertAsync(user, TestContext.Current.CancellationToken);
        user.PasswordHash = "replaced";
        await store.UpsertAsync(user, TestContext.Current.CancellationToken);

        var all = await db.Users.ToListAsync(TestContext.Current.CancellationToken);
        all.Should().ContainSingle();
        all[0].PasswordHash.Should().Be("replaced");
    }

    [Fact]
    public async Task An_unknown_username_returns_null()
    {
        await using var db = await fixture.CreateContextAsync();
        var store = new EfUserStore(db);

        var found = await store.FindByUsernameAsync("nobody", TestContext.Current.CancellationToken);

        found.Should().BeNull();
    }
}
```

> Every async call passes `TestContext.Current.CancellationToken`. `xUnit1051` is an **error** in this repo.

- [ ] **Step 2: Extend the fixture if needed**

`PostgresFixture` currently hands out raw connections. Add `Task<LedgerDbContext> CreateContextAsync()` that clones the template, builds a `LedgerDbContext` against the clone and returns it. **Do not call `EnsureCreated()`** — the clone already carries the migrated schema.

- [ ] **Step 3: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: build error, `EfUserStore` does not exist.

- [ ] **Step 4: Write the store**

```csharp
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Auth;

public sealed class EfUserStore(LedgerDbContext db) : IUserStore
{
    public Task<AppUser?> FindByUsernameAsync(string username, CancellationToken cancellationToken) =>
        db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower(), cancellationToken);

    public async Task UpsertAsync(AppUser user, CancellationToken cancellationToken)
    {
        var existing = await FindByUsernameAsync(user.Username, cancellationToken);

        if (existing is null)
            db.Users.Add(user);
        else
            existing.PasswordHash = user.PasswordHash;

        await db.SaveChangesAsync(cancellationToken);
    }
}
```

> `u.Username.ToLower() == username.ToLower()` translates to `lower(username) = lower(@p)` in PostgreSQL, which is exactly the expression the unique index is built on, so the lookup uses the index rather than scanning.

- [ ] **Step 5: Run the tests**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add src/Noof.Ledger.Persistence/Auth tests/Noof.Ledger.Persistence.Tests
git commit -m "feat(persistence): EfUserStore with case-insensitive lookup"
```

---

---

## Task 9: Web + Host — the Blazor Server shell across the UI-only boundary

**Files:**
- Create: `src/Noof.Ledger.Web/Components/App.razor`, `Routes.razor`, `Layout/MainLayout.razor`, `Pages/Home.razor`, `Pages/Counter.razor`
- Create: `src/Noof.Ledger.Web/wwwroot/app.css`
- Modify: `src/Noof.Ledger.Web/_Imports.razor`, `Noof.Ledger.Web.csproj`
- Modify: `src/Noof.Ledger.Host/Program.cs`, `Noof.Ledger.Host.csproj`
- Create: `src/Noof.Ledger.Host/wwwroot/.gitkeep`
- Modify: `Directory.Packages.props`
- Modify: `tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs`

**Interfaces:**
- Produces: `Noof.Ledger.Web.Components.App` — the root component `Program.cs` passes to `MapRazorComponents<App>()`.

**This shape was established empirically, not guessed.** A two-project replica was built and run. Everything marked **VERIFIED** below was observed as real HTTP output.

> ### The finding that will cost you an afternoon if you skip it
>
> **`_framework/blazor.web.js` returns 404 unless `Noof.Ledger.Host.csproj` sets `RequiresAspNetWebAssets`.**
>
> The Web SDK only pulls in the package that physically contains `blazor.web.js` when the **Host project itself** has at least one `.razor` file as `Content` — `Microsoft.NET.Sdk.Web.ProjectSystem.targets` gates it on `@(Content->AnyHaveMetadataValue(Extension, .razor))`. This architecture puts *every* `.razor` file in the RCL, so Host has none, the heuristic never fires, and you get a **silent 404**: the build is clean, the page renders, and interactivity is simply dead. Set it explicitly.

- [ ] **Step 1: Write the failing architecture tests first**

Two rules in CLAUDE.md are currently enforced by nothing, and both go live the moment this task runs. Add to `tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs`:

```csharp
    [Fact]
    public void Web_package_references_are_exactly_its_allowed_set()
    {
        Packages("Noof.Ledger.Web").Should().BeEquivalentTo(
            "Microsoft.AspNetCore.Components.Web",
            "Microsoft.AspNetCore.Components.Authorization");
    }

    [Fact]
    public void Web_has_no_program_cs()
    {
        var web = Path.Combine(RepoRoot.Find().FullName, "src", "Noof.Ledger.Web");

        Directory.EnumerateFiles(web, "Program.cs", SearchOption.AllDirectories)
            .Should().BeEmpty("Noof.Ledger.Web is a UI-only class library; the host owns startup");
    }

    [Fact]
    public void Web_is_a_razor_class_library_not_a_web_app()
    {
        Load("Noof.Ledger.Web").Root!.Attribute("Sdk")!.Value.Should().Be("Microsoft.NET.Sdk.Razor");
    }
```

The existing package assertions are blacklists (`NotContain`). An exact set is what turns adding a package into a reviewed decision rather than an unwatched accumulation — which matters precisely because this task adds one.

- [ ] **Step 2: Run them**

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`
Expected: `Web_package_references_are_exactly_its_allowed_set` FAILS (Authorization not added yet). The other two PASS — they are regression guards for the rest of this task.

- [ ] **Step 3: Add the package**

`Directory.Packages.props`:

```xml
    <PackageVersion Include="Microsoft.AspNetCore.Components.Authorization" Version="10.0.8" />
```

`src/Noof.Ledger.Web/Noof.Ledger.Web.csproj`, in the existing `PackageReference` ItemGroup:

```xml
    <PackageReference Include="Microsoft.AspNetCore.Components.Authorization" />
```

**VERIFIED:** 10.0.8 restores and builds clean under CPM + `TreatWarningsAsErrors` + `NuGetAuditMode=all`, needs no extra transitive pin, and produces no NU1008. Keep it lockstep with `Components.Web`. `AuthorizeRouteView`, `AuthorizeView`, `CascadingAuthenticationState` and `AuthenticationStateProvider` all live in this package — **not** in `Components.Web`, despite `AuthorizeRouteView`'s routing-flavoured name. `[Authorize]` and `<AntiforgeryToken />` already work without it.

- [ ] **Step 4: Write the components**

`src/Noof.Ledger.Web/_Imports.razor`:

```razor
@using Microsoft.AspNetCore.Authorization
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.AspNetCore.Components.Forms
@using Microsoft.AspNetCore.Components.Routing
@using Microsoft.AspNetCore.Components.Web
@using Noof.Ledger.Web
@using Noof.Ledger.Web.Components
@using Noof.Ledger.Web.Components.Layout
```

`src/Noof.Ledger.Web/Components/App.razor`:

```razor
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <base href="/" />
    <link rel="stylesheet" href="@Assets["_content/Noof.Ledger.Web/app.css"]" />
    <HeadOutlet />
</head>
<body>
    <Routes />
    <script src="_framework/blazor.web.js"></script>
</body>
</html>
```

> **Do not copy the .NET 10 template's `<ResourcePreloader />` or `<ImportMap />` tags.** They live in `Microsoft.AspNetCore.Components.Endpoints`, which a `Microsoft.NET.Sdk.Razor` project does not have, and adding `<FrameworkReference Include="Microsoft.AspNetCore.App" />` to get them makes NuGet flag the existing `Components.Web` package as redundant — **NU1510, a hard error** here. Both tags are optional optimisations. `HeadOutlet` is fine; it is in `Components.Web`.
>
> **VERIFIED:** the `@Assets[...]` key must carry the full `_content/<AssemblyName>/` prefix. Indexing the bare filename silently falls back to the unfingerprinted literal instead of erroring.

`src/Noof.Ledger.Web/Components/Routes.razor`:

```razor
<Router AppAssembly="typeof(Routes).Assembly">
    <Found Context="routeData">
        <AuthorizeRouteView RouteData="routeData" DefaultLayout="typeof(MainLayout)" />
        <FocusOnNavigate RouteData="routeData" Selector="h1" />
    </Found>
</Router>
```

`src/Noof.Ledger.Web/Components/Layout/MainLayout.razor`:

```razor
@inherits LayoutComponentBase

<main class="page">
    @Body
</main>
```

`src/Noof.Ledger.Web/Components/Pages/Home.razor`:

```razor
@page "/"
<PageTitle>Ledger</PageTitle>

<h1>Ledger</h1>
<p>Nothing to show yet.</p>
```

`src/Noof.Ledger.Web/Components/Pages/Counter.razor`:

```razor
@page "/counter"
@rendermode InteractiveServer

<PageTitle>Counter</PageTitle>

<h1>Counter</h1>

<p role="status">Current count: @count</p>

<button class="btn" @onclick="Increment">Click me</button>

@code {
    int count;

    void Increment() => count++;
}
```

> `Counter` exists to give the E2E suite a real server-pushed DOM update to assert against (Task 14, E2E-2). It is the seam a future chart-interactivity test slots into. Delete it only when something real replaces it.

`src/Noof.Ledger.Web/wwwroot/app.css` — a minimal stylesheet is enough; its job is to prove RCL static assets serve.

- [ ] **Step 5: Wire the Host**

`src/Noof.Ledger.Host/Noof.Ledger.Host.csproj`, add to the existing `PropertyGroup`:

```xml
    <RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>
```

Create `src/Noof.Ledger.Host/wwwroot/.gitkeep` — without a `wwwroot`, startup logs a `WebRootPath was not found` warning.

`src/Noof.Ledger.Host/Program.cs`:

```csharp
using Noof.Ledger.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/healthz", () => Results.Ok("ok"));

app.Run();
```

> **`AddAdditionalAssemblies` is NOT needed here, and that is a consequence of the layout.** Endpoint discovery roots at the assembly containing the `TRootComponent` passed to `MapRazorComponents<App>()`. Because `App.razor` **and** the `@page` components both live in `Noof.Ledger.Web.dll`, they are the same assembly. **VERIFIED both ways:** with `App` in Host and a page in a separate referenced library, that page returned **404** until `.AddAdditionalAssemblies(...)` was added, then **200**. If pages are ever split across assemblies, this becomes mandatory.

- [ ] **Step 6: Prove it actually serves, not merely compiles**

```bash
dotnet run --project src/Noof.Ledger.Host/Noof.Ledger.Host.csproj --urls http://127.0.0.1:5199
```

In another shell, and record the real status codes:

```bash
curl -s -o /dev/null -w "%{http_code} /\n"             http://127.0.0.1:5199/
curl -s -o /dev/null -w "%{http_code} /counter\n"      http://127.0.0.1:5199/counter
curl -s -o /dev/null -w "%{http_code} blazor.web.js\n" http://127.0.0.1:5199/_framework/blazor.web.js
curl -s -o /dev/null -w "%{http_code} app.css\n"       http://127.0.0.1:5199/_content/Noof.Ledger.Web/app.css
curl -s -o /dev/null -w "%{http_code} /healthz\n"      http://127.0.0.1:5199/healthz
curl -s -o /dev/null -w "%{http_code} /nope-xyz\n"     http://127.0.0.1:5199/nope-xyz
```

Expected: `200 200 200 200 200` and **`404` for `/nope-xyz`**. The 404 matters — without it you have not shown routing works, only that something answers every request.

**If `blazor.web.js` is 404, `RequiresAspNetWebAssets` is missing.** That is the whole point of Step 5.

Stop the host.

- [ ] **Step 7: Run the suite**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: all green, including the three new architecture tests.

- [ ] **Step 8: Commit**

```bash
git add src/Noof.Ledger.Web src/Noof.Ledger.Host Directory.Packages.props tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs
git commit -m "feat(web): Blazor Server shell in the UI-only RCL, with the boundary now test-enforced"
```

---

## Task 10: Host — auth wiring, the migrate gate, and the loopback guard

**Files:**
- Modify: `src/Noof.Ledger.Host/Program.cs`, `appsettings.json`
- Create: `tests/Noof.Ledger.Host.Tests/BootTests.cs`

**Interfaces:**
- Consumes: `AuthSchemes`, `LocalOwnerHandler` (Task 6), `LoopbackGuard` (Task 7), `IUserStore`/`IPasswordHasher` (Task 2), `EfUserStore` (Task 8), `PasswordHasherAdapter` (Task 5).
- Produces: `public partial class Program` so `WebApplicationFactory<Program>` can reference it.

**The flag selects a handler, never a branch.** Every piece of middleware and every attribute ships unconditionally. Only the registered scheme differs.

- [ ] **Step 1: Write the failing boot tests**

```csharp
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Noof.Ledger.Host.Tests;

public class BootTests
{
    static WebApplicationFactory<Program> Factory(string authMode, bool migrateOnStartup) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:Mode", authMode);
            builder.UseSetting("Database:MigrateOnStartup", migrateOnStartup.ToString());
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
        });

    [Fact]
    public async Task Boots_and_serves_with_the_database_unreachable_when_migration_is_off()
    {
        using var factory = Factory("Off", migrateOnStartup: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue();
    }

    [Theory]
    [InlineData("Off")]
    [InlineData("Cookie")]
    public async Task Healthz_is_anonymous_under_both_modes(string mode)
    {
        using var factory = Factory(mode, migrateOnStartup: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue($"/healthz must stay anonymous under Auth:Mode={mode}");
    }

    [Fact]
    public async Task Fails_fast_rather_than_hanging_when_migration_is_on_and_the_database_is_dead()
    {
        using var factory = Factory("Off", migrateOnStartup: true);

        var act = async () =>
        {
            using var client = factory.CreateClient();
            await client.GetAsync("/", TestContext.Current.CancellationToken);
        };

        await act.Should().ThrowAsync<Exception>();
    }
}
```

> The last test deliberately asserts `Exception`, not a specific type. One party observed `SocketException` for this scenario and another `TimeoutException`. The substantive behaviour — it fails, catchably, without hanging — is what matters, and pinning the inner type makes the test environment-dependent.

Add `Microsoft.AspNetCore.Mvc.Testing` to the Host test project and a matching `PackageVersion` at **10.0.8** in `Directory.Packages.props`.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: FAIL — `Program` is not accessible, and the config keys do nothing yet.

- [ ] **Step 3: Write the wiring**

```csharp
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Host.Auth;
using Noof.Ledger.Host.Startup;
using Noof.Ledger.Persistence;
using Noof.Ledger.Persistence.Auth;
using Noof.Ledger.Web.Components;

var builder = WebApplication.CreateBuilder(args);

var authMode = builder.Configuration["Auth:Mode"] ?? "Off";
var cookieMode = authMode.Equals("Cookie", StringComparison.OrdinalIgnoreCase);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContext<LedgerDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Ledger")));

builder.Services.AddScoped<IUserStore, EfUserStore>();
builder.Services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();

var authentication = builder.Services.AddAuthentication(
    cookieMode ? AuthSchemes.Cookie : AuthSchemes.LocalOwner);

if (cookieMode)
{
    authentication.AddCookie(AuthSchemes.Cookie, options =>
    {
        options.LoginPath = "/account/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(180);
        options.SlidingExpiration = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });
}
else
{
    authentication.AddScheme<AuthenticationSchemeOptions, LocalOwnerHandler>(AuthSchemes.LocalOwner, null);
}

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.MigrateAsync();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/healthz", () => Results.Ok("ok")).AllowAnonymous();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var addresses = app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()?.Addresses;

    LoopbackGuard.AssertSafe([.. addresses ?? []], authMode);
});

app.Run();

public partial class Program;
```

> **`CookieSecurePolicy.Always` would break sign-in entirely** over plain-HTTP loopback. `SameAsRequest` is deliberate, not an oversight — the reflex copied from internet-facing tutorials is wrong here.
>
> **No global `FallbackPolicy`.** The "secure by default" instinct breaks `/healthz` and the deploy script's post-publish poll. Authorization stays opt-in per endpoint.
>
> The guard reads what Kestrel **actually bound**, in `ApplicationStarted`. Re-deriving Kestrel's precedence across `ASPNETCORE_URLS`, `Kestrel:Endpoints` and `launchSettings.json` by hand is a bug source.

`appsettings.json` gains:

```json
  "Auth": { "Mode": "Off" },
  "Database": { "MigrateOnStartup": true },
  "ConnectionStrings": { "Ledger": "" }
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: all green.

- [ ] **Step 5: Commit**

```bash
git add src/Noof.Ledger.Host tests/Noof.Ledger.Host.Tests Directory.Packages.props
git commit -m "feat(host): auth wiring that selects a handler rather than branching the pipeline"
```

---

## Task 11: Web + Host — the login form and its endpoint

**Files:**
- Create: `src/Noof.Ledger.Web/Components/Account/Login.razor`
- Create: `src/Noof.Ledger.Host/Endpoints/AccountEndpoints.cs`
- Modify: `src/Noof.Ledger.Host/Program.cs`
- Create: `tests/Noof.Ledger.Host.Tests/LoginEndpointTests.cs`, `FakeUserStore.cs`, `LoginHelper.cs`

**Interfaces:**
- Consumes: `IUserStore`, `IPasswordHasher`, `PasswordVerifyResult`, `AuthSchemes`.

> ### The silent failure this task exists to prevent
>
> **`.WithMetadata(new RequireAntiforgeryTokenAttribute())` does NOT protect a `MapPost`.** Verified returning **200 with no token supplied**. What actually engages antiforgery validation is binding the form through **`[FromForm]` parameters**. A future "simplification" that switches the handler to `HttpContext` + `ReadFormAsync()` removes CSRF protection from login with **no compile error and no visible symptom**. The four-case test below is the only thing that would catch it. Do not delete it as redundant.

**`Login.razor` is a bare form and must stay one.** No `@inject`, no `@rendermode`, no `HttpContext`. A cookie must be set by a terminal HTTP response; it cannot be set from inside an upgraded SignalR circuit. Microsoft's scaffolded `Login.razor` injects `SignInManager` and `HttpContext` straight into the component — copying it would drag EF-backed services into the UI-only RCL and break the architecture test.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Noof.Ledger.Host.Tests;

public class LoginEndpointTests
{
    static WebApplicationFactory<Program> CookieMode() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:Mode", "Cookie");
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
            builder.ConfigureServices(FakeUserStore.Register);
        });

    [Fact]
    public async Task A_post_without_an_antiforgery_token_is_rejected()
    {
        using var factory = CookieMode();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/account/login",
            new FormUrlEncodedContent([
                new KeyValuePair<string, string>("username", "noof"),
                new KeyValuePair<string, string>("password", "correct")]),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_login_page_renders_a_plain_post_form_with_a_token()
    {
        using var factory = CookieMode();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/account/login", TestContext.Current.CancellationToken);

        html.Should().Contain("method=\"post\"")
            .And.Contain("action=\"/account/login\"")
            .And.Contain("__RequestVerificationToken");
    }

    [Fact]
    public async Task Correct_credentials_set_an_auth_cookie()
    {
        using var factory = CookieMode();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await LoginHelper.PostWithTokenAsync(client, "noof", "correct");

        response.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
        cookies!.Should().Contain(c => c.Contains(".AspNetCore.Cookie", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_wrong_password_sets_no_auth_cookie()
    {
        using var factory = CookieMode();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await LoginHelper.PostWithTokenAsync(client, "noof", "wrong");

        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values
            : Enumerable.Empty<string>();

        cookies.Should().NotContain(c => c.Contains(".AspNetCore.Cookie", StringComparison.OrdinalIgnoreCase));
    }
}
```

Write two small helpers in the same project:
- `FakeUserStore` — a hand-written `IUserStore` holding one `AppUser` whose stored hash was produced by a real `PasswordHasherAdapter` from the password `"correct"`, plus a static `Register(IServiceCollection)` that replaces the registered `IUserStore`. A hand-written fake beats a mocking framework here: the interface has two methods.
- `LoginHelper.PostWithTokenAsync(HttpClient, string, string)` — GET `/account/login`, extract the `__RequestVerificationToken` hidden input value with a regex, carry the returned cookies, and POST both fields plus the token.

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: FAIL — `/account/login` does not exist.

- [ ] **Step 3: Write the form**

`src/Noof.Ledger.Web/Components/Account/Login.razor`:

```razor
@page "/account/login"
@attribute [AllowAnonymous]

<PageTitle>Sign in</PageTitle>

<h1>Sign in</h1>

<form method="post" action="/account/login">
    <AntiforgeryToken />
    <label>Username <input name="username" autocomplete="username" /></label>
    <label>Password <input name="password" type="password" autocomplete="current-password" /></label>
    <button type="submit">Sign in</button>
</form>
```

- [ ] **Step 4: Write the endpoint**

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Host.Auth;

namespace Noof.Ledger.Host.Endpoints;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder routes) =>
        routes.MapPost("/account/login", async (
            [FromForm] string username,
            [FromForm] string password,
            HttpContext context,
            IUserStore users,
            IPasswordHasher hasher,
            CancellationToken cancellationToken) =>
        {
            var user = await users.FindByUsernameAsync(username, cancellationToken);

            if (user is null || hasher.Verify(user, user.PasswordHash, password) is PasswordVerifyResult.Failed)
                return Results.Redirect("/account/login?failed=1");

            Claim[] claims =
            [
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Name, user.Username),
            ];

            await context.SignInAsync(
                AuthSchemes.Cookie,
                new ClaimsPrincipal(new ClaimsIdentity(claims, AuthSchemes.Cookie)),
                new AuthenticationProperties { IsPersistent = true });

            return Results.Redirect("/");
        });
}
```

> **Do not set `AuthenticationProperties.ExpiresUtc`.** It overrides `SlidingExpiration`, turning the 180-day sliding window into a hard expiry.
>
> The `[FromForm]` parameters are load-bearing for CSRF protection, not a style choice.

Add `app.MapAccountEndpoints();` to `Program.cs` after `MapRazorComponents`.

- [ ] **Step 5: Run the tests**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: all green. If `A_post_without_an_antiforgery_token_is_rejected` sees a 200 rather than 400, the binding is wrong — re-read the box above.

- [ ] **Step 6: Commit**

```bash
git add src/Noof.Ledger.Web/Components/Account src/Noof.Ledger.Host/Endpoints src/Noof.Ledger.Host/Program.cs tests/Noof.Ledger.Host.Tests
git commit -m "feat(auth): login form and endpoint, with antiforgery proven by test"
```

---

## Task 12: Host — the auth-mode matrix

**Files:**
- Create: `tests/Noof.Ledger.Host.Tests/AuthModeTests.cs`
- Modify: `src/Noof.Ledger.Web/Components/Pages/Home.razor`

**Both modes are exercised in CI.** The whole design rests on the claim that `Auth:Mode` does not branch the pipeline; that claim is worth a test rather than an argument.

- [ ] **Step 1: Put `[Authorize]` on a page**

Add `@attribute [Authorize]` to `Home.razor`. Under `Off` the `LocalOwnerHandler` satisfies it, so nothing changes visibly — which is exactly the property under test.

- [ ] **Step 2: Write the failing tests**

```csharp
using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Noof.Ledger.Host.Tests;

public class AuthModeTests
{
    static WebApplicationFactory<Program> Factory(string mode) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:Mode", mode);
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
            builder.ConfigureServices(FakeUserStore.Register);
        });

    [Fact]
    public async Task Off_serves_an_authorized_page_without_a_login()
    {
        using var factory = Factory("Off");
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Cookie_redirects_an_anonymous_visitor_to_the_login_page()
    {
        using var factory = Factory("Cookie");
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("/account/login");
    }

    [Fact]
    public async Task The_login_page_itself_is_reachable_anonymously_under_cookie_mode()
    {
        using var factory = Factory("Cookie");
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/account/login", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
```

- [ ] **Step 3: Run, fix whatever is missing, run again**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: all green. A redirect loop on the login page means it is missing its anonymous allowance.

- [ ] **Step 4: Commit**

```bash
git add tests/Noof.Ledger.Host.Tests/AuthModeTests.cs src/Noof.Ledger.Web/Components/Pages/Home.razor
git commit -m "test(auth): both modes exercised, including the login page's own anonymous access"
```

---

## Task 13: Host — the `user set-password` CLI verb

**Files:**
- Create: `src/Noof.Ledger.Host/Cli/UserCommand.cs`
- Modify: `src/Noof.Ledger.Host/Program.cs`
- Create: `tests/Noof.Ledger.Host.Tests/CliVerbParsingTests.cs`

**The only way a user is ever created.** There is no `/register`, no `/setup` page and no seeded credential — *a password in any appsettings file is one commit from being permanent in a public repo*. This verb doubles as the recovery path, which is why no reset flow is needed.

It **upserts**: it creates the row if absent (question B1 in `docs/OPEN-QUESTIONS.md`). Nothing else can create the first user.

- [ ] **Step 1: Write the failing parser test**

```csharp
using AwesomeAssertions;
using Noof.Ledger.Host.Cli;

namespace Noof.Ledger.Host.Tests;

public class CliVerbParsingTests
{
    [Theory]
    [InlineData(new[] { "user", "set-password", "noof" }, "noof")]
    [InlineData(new[] { "user", "set-password", "someone-else" }, "someone-else")]
    public void Recognises_the_verb_and_extracts_the_username(string[] args, string expected)
    {
        UserCommand.TryParse(args, out var username).Should().BeTrue();
        username.Should().Be(expected);
    }

    [Theory]
    [InlineData(new string[] { })]
    [InlineData(new[] { "user" })]
    [InlineData(new[] { "user", "set-password" })]
    [InlineData(new[] { "users", "set-password", "noof" })]
    [InlineData(new[] { "user", "setpassword", "noof" })]
    [InlineData(new[] { "--urls", "http://127.0.0.1:5000" })]
    public void Rejects_everything_else(string[] args)
    {
        UserCommand.TryParse(args, out _).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: build error, `UserCommand` does not exist.

- [ ] **Step 3: Write the parser as a pure function**

```csharp
namespace Noof.Ledger.Host.Cli;

public static class UserCommand
{
    public static bool TryParse(string[] args, out string username)
    {
        if (args is ["user", "set-password", var name, ..] && !string.IsNullOrWhiteSpace(name))
        {
            username = name;
            return true;
        }

        username = string.Empty;
        return false;
    }
}
```

- [ ] **Step 4: Wire it before the web host is built**

At the very top of `Program.cs`, before `WebApplication.CreateBuilder`:

```csharp
if (UserCommand.TryParse(args, out var cliUsername))
    return await UserCommand.RunAsync(cliUsername, args);
```

`RunAsync` builds a `Host.CreateApplicationBuilder` (**not** a `WebApplication`), reads the password from stdin with echo suppressed via `Console.ReadKey(intercept: true)`, hashes it with `PasswordHasherAdapter`, upserts through `EfUserStore`, and returns 0. It must never construct the web host.

**Never log, echo or include the password in an exception message.**

- [ ] **Step 5: Verify against the real database by hand**

```bash
dotnet run --project src/Noof.Ledger.Host/Noof.Ledger.Host.csproj -- user set-password noof
```

Then confirm a row exists and that the stored value is a hash, not the plaintext.

- [ ] **Step 6: Run the suite and commit**

```bash
dotnet test --solution NoofLedger.slnx
git add src/Noof.Ledger.Host/Cli src/Noof.Ledger.Host/Program.cs tests/Noof.Ledger.Host.Tests/CliVerbParsingTests.cs
git commit -m "feat(host): user set-password, the only path to a first user"
```

---

## Task 14: End-to-end — four Playwright smoke tests

**Files:**
- Create: `tests/Noof.Ledger.E2E.Tests/` (project, `HostProcessFixture`, tests)
- Modify: `Directory.Packages.props`

**Deliberately NOT added to `NoofLedger.slnx`,** so `ops/publish.ps1`'s gate is untouched and a browser suite can never block a deploy.

**Exactly four tests. This is not a menu.** On a solo project every extra browser test is pure downside unless it buys a genuinely new failure mode. A flaky browser suite gets disabled within a month; four reliable ones survive.

What E2E uniquely catches, that nothing else in the pyramid can see:
1. The SignalR circuit actually connecting and round-tripping a server-computed DOM update.
2. Static asset delivery **in the really served output** — not theoretical: an app launched from `dotnet build` output reproducibly failed to load a component's JS module, while the same app from `dotnet publish` output worked 5/5 times.
3. A real cookie jar: real form POST, real `Set-Cookie`, real subsequent authorized navigation.

- [ ] **Step 1: Create the project**

```bash
dotnet new xunit3 -o tests/Noof.Ledger.E2E.Tests
dotnet add tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj reference src/Noof.Ledger.Host/Noof.Ledger.Host.csproj
```

Strip the template's inline `Version=` attributes. Add `Microsoft.Playwright.Xunit.v3` with `PackageVersion` **1.62.0**. **Do not add this project to the solution.** Install browsers once: `pwsh tests/Noof.Ledger.E2E.Tests/bin/Debug/net10.0/playwright.ps1 install chromium`.

- [ ] **Step 2: Write the host fixture**

`HostProcessFixture : IAsyncLifetime` (**`ValueTask` signatures — xUnit v3**) that:
1. runs `dotnet publish` of `Noof.Ledger.Host` to a temp directory,
2. launches `dotnet Noof.Ledger.Host.dll --urls http://127.0.0.1:0` as a child process with `Auth__Mode=Off` and `Database__MigrateOnStartup=false`,
3. reads the assigned port from the `Now listening on:` stdout line,
4. polls the root URL until 2xx, capped at 30s,
5. `Kill(entireProcessTree: true)` in teardown.

> **Do NOT host the app under `WebApplicationFactory<Program>` with a Kestrel-forcing `CreateHost` override.** Reproduced verbatim by two independent parties: `InvalidCastException` — *"Unable to cast KestrelServerImpl to TestServer"* from `WebApplicationFactory.get_Server()`, even though Kestrel really started.
>
> Launch from **`dotnet publish` output, never `dotnet build` output.**

- [ ] **Step 3: Write exactly four tests**

- **E2E-1** — `/` loads, correct title, no redirect loop under `Auth:Mode=Off`.
- **E2E-2** — click `/counter`'s button and assert the server-pushed DOM change. **Always precede the click with `WaitForLoadStateAsync(LoadState.NetworkIdle)`** — the click-before-SignalR-connects race is real and was actually reproduced.
- **E2E-3** — subscribe to `Page.Response` and assert zero responses `>= 400` after NetworkIdle.
- **E2E-4** — login round trip under `Auth:Mode=Cookie`: real form POST, real `Set-Cookie`, landing on an `[Authorize]` page. Seed the user via the Task 13 CLI verb in fixture setup.

- [ ] **Step 4: Run and commit**

```bash
dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj
dotnet test --solution NoofLedger.slnx   # must be unaffected
git add tests/Noof.Ledger.E2E.Tests Directory.Packages.props
git commit -m "test(e2e): four Playwright smoke tests, outside the publish gate"
```

---

## Exit criteria

- [ ] `dotnet test --solution NoofLedger.slnx` green, count grown by at least 40.
- [ ] `dotnet test --project tests/Noof.Ledger.E2E.Tests/...` green, Chromium headless.
- [ ] `ops/publish.ps1` still produces `publish/Noof.Ledger.Host.exe`.
- [ ] Browsing `http://127.0.0.1:<port>/` under `Auth:Mode=Off` shows the dashboard with no login.
- [ ] Flipping `Auth:Mode=Cookie` and running `user set-password` yields a working sign-in.
- [ ] Binding a non-loopback URL while `Auth:Mode=Off` refuses to start.
