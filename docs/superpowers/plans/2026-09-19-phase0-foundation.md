# Phase 0 — Foundation and Database Proof — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the nine-project solution with enforced boundaries, prove PostgreSQL handles money correctly in the user's own cultures where SQLite does not, and lock the three conventions that are expensive to reverse.

**Architecture:** A .slnx solution with nine `src/` projects and five `tests/` projects for this phase. `Noof.Domain` has zero package references; `Noof.Web` is a UI-only Razor Class Library. Boundaries are enforced by tests that parse `.csproj` files, not by convention, because project references are transitive at compile time. Money is `decimal` + `Currency` in the domain, mapped to native `numeric(19,4)` in PostgreSQL 18.

**Tech Stack:** .NET 10 (SDK 10.0.204) · PostgreSQL 18 · EF Core 10 + Npgsql · xUnit v3 on Microsoft Testing Platform · AwesomeAssertions · ArchUnitNET

**Spec:** `docs/superpowers/specs/2026-09-19-noof-finance-design.md`

## Global Constraints

- Target framework `net10.0`. SDK pinned to `10.0.204` with `rollForward: latestFeature`.
- `global.json` **must** contain `{"test":{"runner":"Microsoft.Testing.Platform"}}` or `dotnet test` fails outright on this SDK.
- `InvariantGlobalization` is **false**. ICU is required for RSD/KZT/RUB formatting and Russian output.
- `TreatWarningsAsErrors` is true. `NuGetAuditMode=all` with `NU1903;NU1904` as errors.
- Money is `decimal` + `Currency` in the domain. Never `double`, never `float`, never minor units in a domain type.
- All monetary columns are `numeric(19,4)`. All rate columns are `numeric(24,12)`.
- All timestamps are `DateTimeOffset` mapped to `timestamptz`. Never `DateTime`.
- `EnsureCreated()` is banned everywhere, including test helpers.
- `Noof.Domain` has zero `PackageReference` entries.
- Central package management: all versions live in `Directory.Packages.props`.
- Code style: minimal-to-zero comments, self-documenting names, modern C# (file-scoped namespaces, primary constructors, records, collection expressions, pattern matching).
- Public repo: no secrets, no real financial data, no database files in git.

## Review Focus

Five conditions the spec implies that no task's happy path would exercise. Each has a test assigned to the task that owns the code.

1. **A decimal with more than 4 decimal places is stored.** `numeric(19,4)` silently rounds; the spec never says what should happen. Expected: rounding is explicit and tested, not discovered. → Task 5.
2. **A decimal exceeding `numeric(19,4)` range is stored.** Expected: a clear database error, not a truncated balance. → Task 5.
3. **Migrations are applied twice.** The spec requires "creatable from empty and upgradeable" in one mechanism. Expected: the second `MigrateAsync()` is a no-op. → Task 6.
4. **The database is unreachable at startup.** The Postgres service ships as `Manual` and the PC sleeps. Expected: a clear diagnostic, not an opaque socket error. → Task 4.
5. **A negative amount round-trips.** Refunds exist; `numeric` sign handling and ordering across zero must be proven, not assumed. → Task 5.

---

## File Structure

| File | Responsibility |
|---|---|
| `global.json` | SDK pin + Microsoft Testing Platform runner |
| `Directory.Build.props` | TFM, nullable, warnings, globalization, CPM |
| `Directory.Build.targets` | Publish output to `publish/` for deployable apps only |
| `Directory.Packages.props` | Every package version, centrally |
| `NoofFinance.slnx` | Solution |
| `src/Noof.Domain/Money.cs` | The money value object |
| `src/Noof.Domain/CurrencyCode.cs` | ISO-4217 currency wrapper |
| `src/Noof.Persistence/NoofDbContext.cs` | The single DbContext |
| `src/Noof.Persistence/NoofDbContextConventions.cs` | The three irreversible mapping conventions |
| `src/Noof.Persistence/DesignTimeDbContextFactory.cs` | Lets `dotnet ef` run without booting the host |
| `tests/Noof.Architecture.Tests/ProjectReferenceTests.cs` | Boundary enforcement by parsing csproj XML |
| `tests/Noof.Persistence.Tests/MoneyStorageTests.cs` | **The gate test** — culture-correct money |
| `tests/Noof.Persistence.Tests/MigrationContractTests.cs` | Create-from-empty, idempotency, no pending changes |
| `tests/Noof.TestKit/PostgresFixture.cs` | Template-database cloning per test class |

---

## Task 1: Build foundation

**Files:**
- Create: `global.json`, `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `nuget.config`, `.editorconfig`

**Interfaces:**
- Produces: MSBuild properties every project inherits; the `IsDeployableApp` flag consumed by Task 8.

- [ ] **Step 1: Create `global.json`**

```json
{
  "sdk": {
    "version": "10.0.204",
    "rollForward": "latestFeature"
  },
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

- [ ] **Step 2: Create `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningsAsErrors>$(WarningsAsErrors);NU1903;NU1904</WarningsAsErrors>
    <NuGetAuditMode>all</NuGetAuditMode>
    <InvariantGlobalization>false</InvariantGlobalization>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <ArtifactsPath>$(MSBuildThisFileDirectory)artifacts</ArtifactsPath>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Create `Directory.Build.targets`**

Must be `.targets`, not `.props` — import order matters, because `IsDeployableApp` is set in the project file and is not yet defined when `.props` is imported.

```xml
<Project>
  <PropertyGroup Condition="'$(IsDeployableApp)' == 'true'">
    <PublishDir>$(MSBuildThisFileDirectory)publish\</PublishDir>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>false</SelfContained>
    <PublishSingleFile>false</PublishSingleFile>
  </PropertyGroup>
</Project>
```

- [ ] **Step 4: Create `Directory.Packages.props`**

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.3" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.12" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Relational" Version="10.0.12" />
  </ItemGroup>
  <ItemGroup Label="Testing">
    <PackageVersion Include="xunit.v3" Version="4.0.1" />
    <PackageVersion Include="AwesomeAssertions" Version="9.6.0" />
    <PackageVersion Include="NSubstitute" Version="6.2.0" />
    <PackageVersion Include="coverlet.MTP" Version="10.0.1" />
    <PackageVersion Include="TngTech.ArchUnitNET.xUnitV3" Version="0.13.4" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Create `.editorconfig`**

```ini
root = true

[*.cs]
indent_style = space
indent_size = 4
end_of_line = crlf
insert_final_newline = true
charset = utf-8
csharp_style_namespace_declarations = file_scoped:error
csharp_style_prefer_primary_constructors = true:suggestion
csharp_style_var_when_type_is_apparent = true:suggestion
dotnet_style_prefer_collection_expression = true:suggestion
dotnet_diagnostic.CS1591.severity = none

[*.{json,yml,yaml,props,targets,csproj,slnx}]
indent_style = space
indent_size = 2
```

- [ ] **Step 6: Create `nuget.config`**

A single source — more than one logs `NU1507` under central package management, which is an error here.

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
```

- [ ] **Step 7: Verify the SDK resolves and the test runner is configured**

Run: `dotnet --version`
Expected: `10.0.204`

- [ ] **Step 8: Commit**

```bash
git add global.json Directory.Build.props Directory.Build.targets Directory.Packages.props nuget.config .editorconfig
git commit -m "build: pin SDK, central packages and Microsoft Testing Platform runner"
```

---

## Task 2: Solution and project skeleton

**Files:**
- Create: `NoofFinance.slnx`, nine `src/` projects, five `tests/` projects

**Interfaces:**
- Produces: the reference graph that Task 3 asserts.

- [ ] **Step 1: Install the xUnit v3 templates**

`dotnet new xunit` still scaffolds xUnit v2 on VSTest, which will not run on this SDK.

```bash
dotnet new install xunit.v3.templates
```

- [ ] **Step 2: Create the solution and source projects**

SDK 10.0.204 is expected to emit `NoofFinance.slnx`. Confirm which extension you actually got before continuing — every later command in this plan names `NoofFinance.slnx`, and if you have a `.sln` instead you must either use `dotnet new sln --format slnx` or substitute the name throughout.

```bash
dotnet new sln -n NoofFinance
ls NoofFinance.*          # expect NoofFinance.slnx
dotnet new classlib -o src/Noof.Domain -f net10.0
dotnet new classlib -o src/Noof.Application -f net10.0
dotnet new classlib -o src/Noof.Persistence -f net10.0
dotnet new classlib -o src/Noof.Ai -f net10.0
dotnet new classlib -o src/Noof.Fx -f net10.0
dotnet new classlib -o src/Noof.Receipts -f net10.0
dotnet new classlib -o src/Noof.Telegram -f net10.0
dotnet new razorclasslib -o src/Noof.Web -f net10.0
dotnet new web -o src/Noof.Host -f net10.0
```

- [ ] **Step 3: Create the test projects**

```bash
dotnet new xunit3 -o tests/Noof.TestKit -f net10.0
dotnet new xunit3 -o tests/Noof.Domain.Tests -f net10.0
dotnet new xunit3 -o tests/Noof.Application.Tests -f net10.0
dotnet new xunit3 -o tests/Noof.Persistence.Tests -f net10.0
dotnet new xunit3 -o tests/Noof.Architecture.Tests -f net10.0
```

- [ ] **Step 4: Wire the reference graph**

```bash
dotnet add src/Noof.Application reference src/Noof.Domain
dotnet add src/Noof.Persistence reference src/Noof.Application src/Noof.Domain
dotnet add src/Noof.Ai reference src/Noof.Application src/Noof.Domain
dotnet add src/Noof.Fx reference src/Noof.Application src/Noof.Domain
dotnet add src/Noof.Receipts reference src/Noof.Application src/Noof.Domain
dotnet add src/Noof.Telegram reference src/Noof.Application src/Noof.Domain
dotnet add src/Noof.Web reference src/Noof.Application src/Noof.Domain
dotnet add src/Noof.Host reference src/Noof.Domain src/Noof.Application src/Noof.Persistence src/Noof.Ai src/Noof.Fx src/Noof.Receipts src/Noof.Telegram src/Noof.Web

dotnet add tests/Noof.TestKit reference src/Noof.Application src/Noof.Domain
dotnet add tests/Noof.Domain.Tests reference src/Noof.Domain tests/Noof.TestKit
dotnet add tests/Noof.Application.Tests reference src/Noof.Application tests/Noof.TestKit
dotnet add tests/Noof.Persistence.Tests reference src/Noof.Persistence tests/Noof.TestKit
```

- [ ] **Step 5: Add every project to the solution**

```bash
dotnet sln add $(find src tests -name "*.csproj")
```

- [ ] **Step 6: Mark the host as deployable**

Edit `src/Noof.Host/Noof.Host.csproj`, adding inside the first `<PropertyGroup>`:

```xml
<IsDeployableApp>true</IsDeployableApp>
```

- [ ] **Step 7: Lock the Web boundary at compile time**

Edit `src/Noof.Web/Noof.Web.csproj`, adding inside the first `<PropertyGroup>`. Without this, a transitive reference makes `Noof.Web` able to see EF Core types and the boundary is only a convention:

```xml
<DisableTransitiveProjectReferences>true</DisableTransitiveProjectReferences>
```

- [ ] **Step 8: Verify the solution builds**

Run: `dotnet build NoofFinance.slnx`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 9: Verify the test runner works**

Run: `dotnet test NoofFinance.slnx`
Expected: all test projects discovered and passing. If this fails with *"Testing with VSTest target is no longer supported"*, the `global.json` `test.runner` stanza from Task 1 is missing or malformed.

- [ ] **Step 10: Commit**

```bash
git add .
git commit -m "build: scaffold nine source projects and five test projects"
```

---

## Task 3: Enforce the architecture boundaries

**Files:**
- Create: `tests/Noof.Architecture.Tests/ProjectReferenceTests.cs`
- Test: same file

**Interfaces:**
- Consumes: the reference graph from Task 2.
- Produces: `RepoRoot.Find()` — a static helper other test projects reuse to locate the repository root.

- [ ] **Step 1: Write the failing test**

Create `tests/Noof.Architecture.Tests/RepoRoot.cs`:

```csharp
namespace Noof.Architecture.Tests;

public static class RepoRoot
{
    public static DirectoryInfo Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
            dir = dir.Parent;

        return dir ?? throw new InvalidOperationException("global.json not found above the test output directory.");
    }
}
```

Create `tests/Noof.Architecture.Tests/ProjectReferenceTests.cs`:

```csharp
using System.Xml.Linq;
using AwesomeAssertions;

namespace Noof.Architecture.Tests;

public class ProjectReferenceTests
{
    [Theory]
    [InlineData("Noof.Domain")]
    [InlineData("Noof.Application", "Noof.Domain")]
    [InlineData("Noof.Persistence", "Noof.Application", "Noof.Domain")]
    [InlineData("Noof.Web", "Noof.Application", "Noof.Domain")]
    [InlineData("Noof.Telegram", "Noof.Application", "Noof.Domain")]
    public void Project_references_exactly_its_allowed_set(string project, params string[] allowed)
    {
        References(project).Should().BeEquivalentTo(allowed);
    }

    [Fact]
    public void Domain_has_no_package_references()
    {
        Packages("Noof.Domain").Should().BeEmpty();
    }

    [Fact]
    public void Web_has_no_entity_framework_package()
    {
        Packages("Noof.Web").Should().NotContain(p => p.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));
    }

    static XDocument Load(string project)
    {
        var path = Path.Combine(RepoRoot.Find().FullName, "src", project, $"{project}.csproj");
        return XDocument.Load(path);
    }

    static string[] References(string project) =>
        [.. Load(project).Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/')))];

    static string[] Packages(string project) =>
        [.. Load(project).Descendants("PackageReference")
            .Select(e => e.Attribute("Include")!.Value)];
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Noof.Architecture.Tests`
Expected: FAIL — `AwesomeAssertions` is not referenced yet.

- [ ] **Step 3: Add the assertion package**

```bash
dotnet add tests/Noof.Architecture.Tests package AwesomeAssertions
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Noof.Architecture.Tests`
Expected: PASS, 7 tests.

- [ ] **Step 5: Prove the test actually catches a violation**

This step exists because a boundary test that cannot fail is worse than none — it creates false confidence.

```bash
dotnet add src/Noof.Web reference src/Noof.Persistence
dotnet test tests/Noof.Architecture.Tests
```

Expected: FAIL on `Project_references_exactly_its_allowed_set` for `Noof.Web`.

- [ ] **Step 6: Revert the deliberate violation**

```bash
dotnet remove src/Noof.Web reference src/Noof.Persistence
dotnet test tests/Noof.Architecture.Tests
```

Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add tests/Noof.Architecture.Tests
git commit -m "test: enforce project boundaries by parsing csproj references"
```

---

## Task 4: PostgreSQL ready and reachable

**Files:**
- Create: `ops/setup-database.ps1`, `tests/Noof.TestKit/DatabaseSettings.cs`

**Interfaces:**
- Produces: `DatabaseSettings.AdminConnectionString` and `DatabaseSettings.TemplateDatabase`, consumed by Task 5 and Task 6.

- [ ] **Step 1: Confirm the service name and start it**

PostgreSQL 18 was installed via choco; the service name follows the major version.

```powershell
Get-Service postgresql*
```

Expected: a service named `postgresql-x64-18`. Note the actual name — the next step uses it.

- [ ] **Step 2: Create `ops/setup-database.ps1`**

```powershell
#Requires -RunAsAdministrator
param(
    [string]$ServiceName = 'postgresql-x64-18',
    [string]$Superuser   = 'postgres'
)

$ErrorActionPreference = 'Stop'

Set-Service -Name $ServiceName -StartupType Automatic
Start-Service -Name $ServiceName

$psql = (Get-ChildItem 'C:\Program Files\PostgreSQL\*\bin\psql.exe' | Sort-Object FullName -Descending)[0].FullName

& $psql -U $Superuser -v ON_ERROR_STOP=1 -c "CREATE DATABASE noof_finance;"
& $psql -U $Superuser -v ON_ERROR_STOP=1 -c "CREATE DATABASE noof_test_template;"

foreach ($db in @('noof_finance', 'noof_test_template')) {
    & $psql -U $Superuser -d $db -v ON_ERROR_STOP=1 -c "CREATE EXTENSION IF NOT EXISTS pg_trgm;"
    & $psql -U $Superuser -d $db -v ON_ERROR_STOP=1 -c "CREATE EXTENSION IF NOT EXISTS unaccent;"
}

Write-Host 'Databases ready.'
```

- [ ] **Step 3: Run it from an elevated PowerShell**

Run: `powershell -ExecutionPolicy Bypass -File ops/setup-database.ps1`
Expected: `Databases ready.`

- [ ] **Step 4: Create the test connection settings**

Create `tests/Noof.TestKit/DatabaseSettings.cs`:

```csharp
namespace Noof.TestKit;

public static class DatabaseSettings
{
    public const string TemplateDatabase = "noof_test_template";

    public static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("NOOF_TEST_PG")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Include Error Detail=true";

    public static string For(string database) =>
        AdminConnectionString.Replace("Database=postgres", $"Database={database}", StringComparison.Ordinal);
}
```

- [ ] **Step 5: Write the failing connectivity test**

This is **Review Focus #4** — the database being unreachable must produce a clear diagnostic.

Create `tests/Noof.Persistence.Tests/DatabaseReachableTests.cs`:

```csharp
using AwesomeAssertions;
using Npgsql;
using Noof.TestKit;

namespace Noof.Persistence.Tests;

public class DatabaseReachableTests
{
    [Fact]
    public async Task Server_is_reachable_and_is_postgres_18_or_later()
    {
        await using var connection = new NpgsqlConnection(DatabaseSettings.AdminConnectionString);
        await connection.OpenAsync();

        connection.PostgreSqlVersion.Major.Should().BeGreaterThanOrEqualTo(18);
    }

    [Fact]
    public async Task Unreachable_server_fails_with_a_socket_diagnostic_not_a_hang()
    {
        var unreachable = DatabaseSettings.AdminConnectionString
            .Replace("Port=5432", "Port=5433", StringComparison.Ordinal) + ";Timeout=2";

        await using var connection = new NpgsqlConnection(unreachable);

        var act = async () => await connection.OpenAsync();

        await act.Should().ThrowAsync<NpgsqlException>();
    }
}
```

- [ ] **Step 6: Run to verify it fails**

Run: `dotnet test tests/Noof.Persistence.Tests`
Expected: FAIL — `Npgsql` is not referenced yet.

- [ ] **Step 7: Add the packages**

```bash
dotnet add src/Noof.Persistence package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add tests/Noof.Persistence.Tests package AwesomeAssertions
```

- [ ] **Step 8: Run to verify it passes**

Run: `dotnet test tests/Noof.Persistence.Tests`
Expected: PASS, 2 tests.

- [ ] **Step 9: Commit**

```bash
git add ops/setup-database.ps1 tests/
git commit -m "feat: database setup script and reachability tests"
```

---

## Task 5: The gate test — money is correct in every culture

This is the executable form of the answer to *"Maybe postgres then?"*. It stays in the suite forever.

**Files:**
- Create: `src/Noof.Domain/CurrencyCode.cs`, `src/Noof.Domain/Money.cs`, `tests/Noof.Persistence.Tests/MoneyStorageTests.cs`
- Test: `tests/Noof.Domain.Tests/MoneyTests.cs`

**Interfaces:**
- Produces: `Money(decimal Amount, CurrencyCode Currency)` and `CurrencyCode(string Value)`, consumed by every later task.

- [ ] **Step 1: Write the failing domain test**

Create `tests/Noof.Domain.Tests/MoneyTests.cs`:

```csharp
using AwesomeAssertions;
using Noof.Domain;

namespace Noof.Domain.Tests;

public class MoneyTests
{
    [Fact]
    public void Adding_same_currency_sums_the_amounts()
    {
        var sum = new Money(10.50m, CurrencyCode.Eur) + new Money(2.25m, CurrencyCode.Eur);

        sum.Should().Be(new Money(12.75m, CurrencyCode.Eur));
    }

    [Fact]
    public void Adding_different_currencies_throws()
    {
        var act = () => new Money(10m, CurrencyCode.Eur) + new Money(10m, CurrencyCode.Rsd);

        act.Should().Throw<CurrencyMismatchException>()
           .WithMessage("*EUR*RSD*");
    }

    [Fact]
    public void Currency_code_rejects_anything_that_is_not_three_letters()
    {
        var act = () => new CurrencyCode("EURO");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Currency_code_is_upper_cased()
    {
        new CurrencyCode("eur").Value.Should().Be("EUR");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Noof.Domain.Tests`
Expected: FAIL — `Money` and `CurrencyCode` do not exist.

- [ ] **Step 3: Implement the domain types**

Create `src/Noof.Domain/CurrencyCode.cs`:

```csharp
namespace Noof.Domain;

public readonly record struct CurrencyCode
{
    public CurrencyCode(string value)
    {
        if (value is not { Length: 3 } || !value.All(char.IsAsciiLetter))
            throw new ArgumentException($"'{value}' is not a three-letter ISO 4217 code.", nameof(value));

        Value = value.ToUpperInvariant();
    }

    public string Value { get; }

    public static readonly CurrencyCode Eur = new("EUR");
    public static readonly CurrencyCode Rsd = new("RSD");
    public static readonly CurrencyCode Usd = new("USD");
    public static readonly CurrencyCode Rub = new("RUB");
    public static readonly CurrencyCode Kzt = new("KZT");

    public override string ToString() => Value;
}
```

Create `src/Noof.Domain/CurrencyMismatchException.cs`:

```csharp
namespace Noof.Domain;

public sealed class CurrencyMismatchException(CurrencyCode left, CurrencyCode right)
    : InvalidOperationException($"Cannot combine {left} with {right}.");
```

Create `src/Noof.Domain/Money.cs`:

```csharp
namespace Noof.Domain;

public readonly record struct Money(decimal Amount, CurrencyCode Currency)
{
    public static Money operator +(Money left, Money right) =>
        left.Currency == right.Currency
            ? left with { Amount = left.Amount + right.Amount }
            : throw new CurrencyMismatchException(left.Currency, right.Currency);

    public static Money operator -(Money left, Money right) =>
        left.Currency == right.Currency
            ? left with { Amount = left.Amount - right.Amount }
            : throw new CurrencyMismatchException(left.Currency, right.Currency);

    public static Money operator -(Money value) => value with { Amount = -value.Amount };

    public override string ToString() => $"{Amount} {Currency}";
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Noof.Domain.Tests`
Expected: PASS, 4 tests.

- [ ] **Step 5: Write the failing gate test**

The values matter. `1234.50` vs `999.99` and `9.55` vs `10.00` invert under `sr-Latn-RS`, where `.` is the group separator. A naive set like `{9.5, 10.0, 102.18}` passes with or without the bug and proves nothing.

Create `tests/Noof.Persistence.Tests/MoneyStorageTests.cs`:

```csharp
using System.Globalization;
using AwesomeAssertions;
using Npgsql;
using Noof.TestKit;

namespace Noof.Persistence.Tests;

[Collection("postgres")]
public class MoneyStorageTests(PostgresFixture fixture)
{
    static readonly decimal[] Amounts = [-45.25m, 0.10m, 2.05m, 9.55m, 10.00m, 100.00m, 999.99m, 1234.50m];

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    [InlineData("sr-Latn-RS")]
    public async Task Ordering_and_aggregation_are_correct_in_every_culture(string culture)
    {
        CultureInfo.CurrentCulture = new CultureInfo(culture);

        await using var db = await fixture.CreateDatabaseAsync();
        await SeedAsync(db, Amounts);

        var ordered = await QueryDecimalsAsync(db, "SELECT amount FROM money_probe ORDER BY amount");
        var sum = await QuerySingleAsync(db, "SELECT SUM(amount) FROM money_probe");
        var max = await QuerySingleAsync(db, "SELECT MAX(amount) FROM money_probe");

        ordered.Should().BeInAscendingOrder().And.Equal([.. Amounts.Order()]);
        sum.Should().Be(Amounts.Sum());
        max.Should().Be(1234.50m);
    }

    [Fact]
    public async Task Negative_amounts_round_trip_exactly()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await SeedAsync(db, [-1234.5678m]);

        var value = await QuerySingleAsync(db, "SELECT amount FROM money_probe");

        value.Should().Be(-1234.5678m);
    }

    [Fact]
    public async Task More_than_four_decimal_places_is_rounded_not_rejected()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await SeedAsync(db, [1.00005m]);

        var value = await QuerySingleAsync(db, "SELECT amount FROM money_probe");

        value.Should().Be(1.0001m);
    }

    [Fact]
    public async Task An_amount_beyond_the_column_range_is_rejected_loudly()
    {
        await using var db = await fixture.CreateDatabaseAsync();

        var act = async () => await SeedAsync(db, [12345678901234567.89m]);

        await act.Should().ThrowAsync<PostgresException>();
    }

    static async Task SeedAsync(NpgsqlConnection db, IEnumerable<decimal> amounts)
    {
        await using (var create = new NpgsqlCommand("CREATE TABLE IF NOT EXISTS money_probe (id serial PRIMARY KEY, amount numeric(19,4) NOT NULL)", db))
            await create.ExecuteNonQueryAsync();

        foreach (var amount in amounts)
        {
            await using var insert = new NpgsqlCommand("INSERT INTO money_probe (amount) VALUES (@a)", db);
            insert.Parameters.AddWithValue("a", amount);
            await insert.ExecuteNonQueryAsync();
        }
    }

    static async Task<decimal[]> QueryDecimalsAsync(NpgsqlConnection db, string sql)
    {
        await using var command = new NpgsqlCommand(sql, db);
        await using var reader = await command.ExecuteReaderAsync();

        var results = new List<decimal>();
        while (await reader.ReadAsync())
            results.Add(reader.GetDecimal(0));

        return [.. results];
    }

    static async Task<decimal> QuerySingleAsync(NpgsqlConnection db, string sql)
    {
        await using var command = new NpgsqlCommand(sql, db);
        return (decimal)(await command.ExecuteScalarAsync())!;
    }
}
```

- [ ] **Step 6: Write the fixture the gate test needs**

Create `tests/Noof.TestKit/PostgresFixture.cs`. Two things here are not optional:

- `ClearAllPools()` **before** `DROP DATABASE` — without it the drop blocks on pooled connections and teardown hangs forever with no error.
- Nothing may hold a connection to `noof_test_template` while a clone runs. `CREATE DATABASE … TEMPLATE` fails with *"source database is being accessed by other users"*. If you see that, close any open pgAdmin or psql session pointed at the template.

```csharp
using Npgsql;
using Xunit;

namespace Noof.TestKit;

public sealed class PostgresFixture : IAsyncLifetime
{
    readonly List<string> created = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async Task<NpgsqlConnection> CreateDatabaseAsync()
    {
        var name = $"noof_test_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(DatabaseSettings.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE {DatabaseSettings.TemplateDatabase}", admin);
            await create.ExecuteNonQueryAsync();
        }

        created.Add(name);

        var connection = new NpgsqlConnection(DatabaseSettings.For(name));
        await connection.OpenAsync();
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();

        await using var admin = new NpgsqlConnection(DatabaseSettings.AdminConnectionString);
        await admin.OpenAsync();

        foreach (var name in created)
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
```

- [ ] **Step 7: Run to verify it fails**

Run: `dotnet test tests/Noof.Persistence.Tests`
Expected: FAIL — `Npgsql` and `xunit.v3` are not referenced from `Noof.TestKit`.

- [ ] **Step 8: Add the packages**

```bash
dotnet add tests/Noof.TestKit package Npgsql
dotnet add tests/Noof.TestKit package xunit.v3
```

- [ ] **Step 9: Run to verify it passes**

Run: `dotnet test tests/Noof.Persistence.Tests`
Expected: PASS, 7 tests — including all three cultures.

- [ ] **Step 10: Commit**

```bash
git add src/Noof.Domain tests/
git commit -m "feat: Money value object and the culture-correctness gate test"
```

---

## Task 6: EF conventions, first migration, migration contract

**Files:**
- Create: `src/Noof.Persistence/NoofDbContext.cs`, `src/Noof.Persistence/DesignTimeDbContextFactory.cs`, `tests/Noof.Persistence.Tests/MigrationContractTests.cs`

**Interfaces:**
- Consumes: `Money`, `CurrencyCode` from Task 5.
- Produces: `NoofDbContext`, consumed by every later persistence task.

- [ ] **Step 1: Write the failing convention test**

Create `tests/Noof.Persistence.Tests/MigrationContractTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Persistence;
using Noof.TestKit;

namespace Noof.Persistence.Tests;

[Collection("postgres")]
public class MigrationContractTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migrations_create_the_database_from_empty()
    {
        await using var db = await fixture.CreateContextAsync();

        await db.Database.MigrateAsync();

        var applied = await db.Database.GetAppliedMigrationsAsync();
        applied.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Migrating_twice_is_a_no_op()
    {
        await using var db = await fixture.CreateContextAsync();

        await db.Database.MigrateAsync();
        await db.Database.MigrateAsync();

        var pending = await db.Database.GetPendingMigrationsAsync();
        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task The_model_has_no_pending_changes()
    {
        await using var db = await fixture.CreateContextAsync();

        db.Database.HasPendingModelChanges().Should().BeFalse(
            "a model change without a migration means the next deploy silently diverges from the schema");
    }

    [Fact]
    public async Task Money_columns_are_numeric_19_4()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync();

        var type = await db.Database
            .SqlQuery<string>($"""
                SELECT data_type || '(' || numeric_precision || ',' || numeric_scale || ')'
                FROM information_schema.columns
                WHERE table_name = 'money_probe_entities' AND column_name = 'amount'
                """)
            .SingleAsync();

        type.Should().Be("numeric(19,4)");
    }

    [Fact]
    public async Task Timestamp_columns_are_timestamptz()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync();

        var type = await db.Database
            .SqlQuery<string>($"""
                SELECT data_type
                FROM information_schema.columns
                WHERE table_name = 'money_probe_entities' AND column_name = 'recorded_at'
                """)
            .SingleAsync();

        type.Should().Be("timestamp with time zone");
    }
}
```

- [ ] **Step 2: Extend the fixture to hand out a DbContext**

Append to `tests/Noof.TestKit/PostgresFixture.cs`, inside the `PostgresFixture` class:

```csharp
    public async Task<NoofDbContext> CreateContextAsync()
    {
        var name = $"noof_test_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(DatabaseSettings.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        created.Add(name);

        var options = new DbContextOptionsBuilder<NoofDbContext>()
            .UseNpgsql(DatabaseSettings.For(name))
            .Options;

        return new NoofDbContext(options);
    }
```

Add the matching usings at the top of the file: `using Microsoft.EntityFrameworkCore;` and `using Noof.Persistence;`, and add the project reference:

```bash
dotnet add tests/Noof.TestKit reference src/Noof.Persistence
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/Noof.Persistence.Tests`
Expected: FAIL — `NoofDbContext` does not exist.

- [ ] **Step 4: Implement the DbContext with the three irreversible conventions**

Create `src/Noof.Persistence/MoneyProbeEntity.cs`. This entity exists only so Phase 0 can assert the conventions produce the right column types; Phase 1 replaces it with the real model.

```csharp
using Noof.Domain;

namespace Noof.Persistence;

public sealed class MoneyProbeEntity
{
    public int Id { get; init; }
    public required Money Amount { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
}
```

Create `src/Noof.Persistence/NoofDbContext.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Noof.Domain;

namespace Noof.Persistence;

public class NoofDbContext(DbContextOptions<NoofDbContext> options) : DbContext(options)
{
    public DbSet<MoneyProbeEntity> MoneyProbes => Set<MoneyProbeEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<decimal>().HavePrecision(19, 4);
        builder.Properties<DateTimeOffset>().HaveColumnType("timestamptz");
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("public");

        builder.Entity<MoneyProbeEntity>(entity =>
        {
            entity.ToTable("money_probe_entities");
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.RecordedAt).HasColumnName("recorded_at");

            entity.ComplexProperty(e => e.Amount, money =>
            {
                money.Property(m => m.Amount).HasColumnName("amount").HasPrecision(19, 4);
                money.Property(m => m.Currency)
                     .HasColumnName("currency")
                     .HasMaxLength(3)
                     .HasConversion(c => c.Value, v => new CurrencyCode(v));
            });
        });
    }
}
```

Create `src/Noof.Persistence/DesignTimeDbContextFactory.cs`. This lets `dotnet ef` run without booting the web host:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Noof.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<NoofDbContext>
{
    public NoofDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("NOOF_DESIGN_TIME_PG")
            ?? "Host=127.0.0.1;Port=5432;Database=noof_finance;Username=postgres";

        var options = new DbContextOptionsBuilder<NoofDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new NoofDbContext(options);
    }
}
```

- [ ] **Step 5: Add the design package and create the first migration**

```bash
dotnet add src/Noof.Persistence package Microsoft.EntityFrameworkCore.Design
dotnet ef migrations add InitialCreate --project src/Noof.Persistence --startup-project src/Noof.Persistence
```

Expected: a `Migrations/` folder appears under `src/Noof.Persistence`.

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test tests/Noof.Persistence.Tests`
Expected: PASS, 12 tests.

- [ ] **Step 7: Prime the template database**

The fixture's `CreateDatabaseAsync` clones `noof_test_template`, so the template must carry the migrated schema.

```bash
dotnet ef database update --project src/Noof.Persistence --startup-project src/Noof.Persistence --connection "Host=127.0.0.1;Port=5432;Database=noof_test_template;Username=postgres"
```

- [ ] **Step 8: Run the whole suite**

Run: `dotnet test NoofFinance.slnx`
Expected: PASS, all projects.

- [ ] **Step 9: Commit**

```bash
git add src/Noof.Persistence tests/
git commit -m "feat: DbContext conventions, first migration and migration contract tests"
```

---

## Task 7: Verify the SQLite counterfactual

The spec's central claim is that SQLite fails where Postgres succeeds. This task proves it once, records the evidence, and deletes the code. Without it, the database decision rests on a report rather than on something this repository can demonstrate.

**Files:**
- Create then delete: `tests/Noof.Persistence.Tests/SqliteCounterfactualTests.cs`
- Modify: `docs/superpowers/specs/2026-09-19-noof-finance-design.md`

- [ ] **Step 1: Write the counterfactual test**

```csharp
using System.Globalization;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;

namespace Noof.Persistence.Tests;

public class SqliteCounterfactualTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    [InlineData("sr-Latn-RS")]
    public void Record_what_sqlite_does_with_decimal_as_text(string culture)
    {
        CultureInfo.CurrentCulture = new CultureInfo(culture);

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE probe (amount TEXT NOT NULL)";
            create.ExecuteNonQuery();
        }

        foreach (var amount in new[] { "-45.25", "0.10", "2.05", "9.55", "10.00", "100.00", "999.99", "1234.50" })
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = $"INSERT INTO probe VALUES ('{amount}')";
            insert.ExecuteNonQuery();
        }

        using var query = connection.CreateCommand();
        query.CommandText = "SELECT MAX(amount) FROM probe";
        var max = query.ExecuteScalar()!.ToString();

        max.Should().NotBe("1234.50",
            "this test documents the defect that made PostgreSQL the choice; if it ever passes, re-open the decision");
    }
}
```

- [ ] **Step 2: Add the SQLite package and run it**

```bash
dotnet add tests/Noof.Persistence.Tests package Microsoft.Data.Sqlite
dotnet test tests/Noof.Persistence.Tests --filter "SqliteCounterfactual"
```

Expected: PASS — SQLite's lexicographic `MAX` returns `999.99`, not `1234.50`. **Record the actual value printed.**

- [ ] **Step 3: Write the observed result into the spec**

Edit `docs/superpowers/specs/2026-09-19-noof-finance-design.md`, in §3, replacing the `MAX` line of the raw-connection code block with the value you actually observed, and append: *"Reproduced in this repository on <date>; see git history for `SqliteCounterfactualTests`."*

- [ ] **Step 4: Remove the counterfactual and its dependency**

The decision is made and recorded. Leaving a SQLite dependency in a Postgres project invites someone to use it.

```bash
rm tests/Noof.Persistence.Tests/SqliteCounterfactualTests.cs
dotnet remove tests/Noof.Persistence.Tests package Microsoft.Data.Sqlite
dotnet test NoofFinance.slnx
```

Expected: PASS, all projects.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "test: reproduce and record the SQLite decimal defect, then remove it"
```

---

## Task 8: Publish pipeline

**Files:**
- Create: `ops/publish.ps1`

- [ ] **Step 1: Verify publish lands in `publish/`**

```bash
dotnet publish src/Noof.Host/Noof.Host.csproj -c Release -o publish
ls publish/Noof.Host.exe
```

Expected: the executable exists. Note that `-o` on a *solution* is an error since SDK 7.0.200 — always publish the host project.

- [ ] **Step 2: Create `ops/publish.ps1`**

`dotnet publish` does not clean its output, so a stale DLL survives a republish. Wiping is safe only because no data lives in `publish/`.

```powershell
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

Push-Location $root
try {
    dotnet build NoofFinance.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    dotnet test NoofFinance.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; refusing to publish.' }

    if (Test-Path publish) { Remove-Item publish -Recurse -Force }

    dotnet publish src/Noof.Host/Noof.Host.csproj -c Release -o publish
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    Write-Host "Published to $root\publish"
}
finally { Pop-Location }
```

- [ ] **Step 3: Run it end to end**

Run: `powershell -ExecutionPolicy Bypass -File ops/publish.ps1`
Expected: `Published to C:\repos\dev\noof-finance\publish`

- [ ] **Step 4: Confirm nothing published is tracked by git**

Run: `git status --short`
Expected: no `publish/` or `artifacts/` entries.

- [ ] **Step 5: Commit**

```bash
git add ops/publish.ps1
git commit -m "build: test-gated publish script targeting the publish folder"
```

---

## Phase 0 exit criteria

- [ ] `dotnet test NoofFinance.slnx` is green, running on Microsoft Testing Platform.
- [ ] The culture gate test passes for `en-US`, `ru-RU` and `sr-Latn-RS`.
- [ ] Adding a forbidden `ProjectReference` turns the architecture test red — proven, not assumed.
- [ ] `money_probe_entities.amount` is `numeric(19,4)`; `recorded_at` is `timestamp with time zone`.
- [ ] `MigrateAsync()` creates from empty, is idempotent, and reports no pending model changes.
- [ ] `ops/publish.ps1` produces `publish/Noof.Host.exe` and refuses to publish when tests fail.
- [ ] The SQLite defect is reproduced, recorded in the spec, and the dependency removed.
- [ ] The Postgres service is `Automatic` and survives a reboot.
