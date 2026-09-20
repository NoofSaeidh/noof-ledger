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

1. **A login POST with no antiforgery token must be rejected.** `.WithMetadata(new RequireAntiforgeryTokenAttribute())` alone does **not** protect a `MapPost` — verified returning 200 with no token supplied. Binding via `[FromForm]` parameters is what engages validation, and the failure is silent. → Task 10.
2. **A wrong password must not produce an auth cookie.** The obvious bug is redirecting back to the login page while still signing the user in. → Task 10.
3. **`/healthz` must stay anonymous under BOTH modes.** The "secure by default" reflex adds a global `FallbackPolicy`, which breaks the deploy script's post-publish poll. → Task 11.
4. **A username that differs only by case must not create a second user.** `UPSERT` on a case-sensitive unique index silently produces two accounts. → Task 3 (citext/lower index) and Task 12.
5. **The app must boot with PostgreSQL unreachable** when `Database:MigrateOnStartup=false`, and fail fast rather than hang when `true`. Every integration test depends on the first half. → Task 9.

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

## Remaining tasks

Tasks 9–13 (the Blazor Server shell, `Program.cs` auth wiring, the login form and endpoint, the auth-mode integration matrix, the `user set-password` CLI verb, and the four Playwright smoke tests) depend on the RCL/Host split shape, which is being established by an empirical probe. They are appended to this plan once that lands, so that their steps name real files rather than a guessed layout.

Tasks 1–8 above have no dependency on that shape and are ready to execute.
