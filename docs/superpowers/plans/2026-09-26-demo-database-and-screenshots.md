# Demo database and screenshots — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A long-lived `noof_ledger_demo` database refreshed with fixed mock data on every run, the real
app runnable on it with one command, and committed screenshots of every page and of the bot's replies
generated from it.

**Architecture:** A new console tool, `tools/Noof.Ledger.Demo`, rebuilds the demo database from
migrations and writes mock data through the app's own persistence (`AddNoofPersistence`), then either
runs the real host on it (`dotnet run` with environment overrides) or drives Playwright against that
host and renders Telegram-style pictures from the app's real reply text (`IRecordEcho`). `run.ps1`
gains `demo` and `screenshots`.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, Microsoft.Playwright 1.62.0, xunit v3 (MTP) with
AwesomeAssertions.

**Spec:** `docs/superpowers/specs/2026-09-26-demo-database-and-screenshots-design.md`

## Global Constraints

- Work in the worktree `C:\repos\dev\noof-ledger\.claude\worktrees\demo-environment`, branch `demo-environment`.
- Never connect to `noof_ledger`. The tool only ever drops or creates `noof_ledger_demo` or a `noof_test_*` database.
- Demo database name `noof_ledger_demo`; demo site `http://127.0.0.1:5264`; sign-in `demo` / `demo`.
- Demo files live under `%LOCALAPPDATA%\NoofLedger\demo\` (`logs\`, `dp-keys\`); backups off (`Backup__Enabled=false`).
- A connection string (it carries the PostgreSQL password) goes to a child process only through its environment, never its arguments, and is never printed.
- No new production configuration key. The only `src` change is `InternalsVisibleTo Include="Noof.Ledger.Demo"` in `Noof.Ledger.Persistence`, `Noof.Ledger.Host` and `Noof.Ledger.Telegram`.
- Mock data is fixed: dates 2026-09-01..2026-09-20, time zone `Europe/Belgrade`, fixed clock `2026-09-20T18:00:00Z`. The one exception is the `backup_runs` row, dated relative to the real clock (the Backups check judges age against now).
- Screenshots: phone 390×844 at device scale 2, desktop 1440×900 at scale 1, full page, animations disabled, caret hidden; written to `docs/screenshots/app/<screen>-<phone|desktop>.png` and `docs/screenshots/telegram/<scene>.png`; a file is rewritten only when its bytes change.
- Phone-deliverable copies: JPEG quality 88, slices no taller than 4000 device pixels, in `artifacts/screenshots/` (git-ignored).
- Code style per `CLAUDE.md` §3: file-scoped namespaces, primary constructors, records, no comments except *why*, `internal` by default. `TreatWarningsAsErrors` is on for every project.
- Tests: xunit v3, `AwesomeAssertions` (`.Should()`), DB tests carry `[Trait("Category", "Database")]` and skip with `Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.")` when PostgreSQL is unreachable.
- Test commands: unit — `dotnet test --project tests\Noof.Ledger.Demo.Tests\Noof.Ledger.Demo.Tests.csproj --filter "Category!=Database"`; database — same project with `--filter "Category=Database"`. Timeouts per `CLAUDE.md` "Waiting on tests" (small projects: 30000 ms, then one blocking wait of 120000 ms).
- Every commit message ends with these two lines:
  ```
  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01Vg9YxY3Rh4sJVe4ywKWuze
  ```

## Review Focus

- **A demo is already running when `demo` or `screenshots` starts** — both must refuse with a message naming `http://127.0.0.1:5264`, before building or dropping anything. Pinned: `PortProbeTests` (Task 4) and `Assert-DemoNotRunning` in `run.ps1` (Task 4).
- **`%LOCALAPPDATA%\NoofLedger\db.connection` is missing** — the tool must say to run `.\run.ps1 db-auth-reset`, not throw a bare `FileNotFoundException`. Pinned: `DemoDatabaseTests.Missing_credential_file_names_the_command_that_creates_it` (Task 1).
- **Any database name other than `noof_ledger_demo` / `noof_test_*` reaching drop/create** — refused. Pinned: `DemoDatabaseTests` (Task 1).
- **An unchanged screen rewriting its file** — must not happen, or git shows noise on every run. Pinned: `ShotFilesTests` (Task 6) and the two-run check (Task 6, Step 9).
- **The PostgreSQL password leaking into process arguments or console output** — pinned: `DemoHostTests.Connection_string_travels_only_in_the_environment` (Task 4).

---

### Task 1: The tool, its tests and the database-name guard

**Files:**
- Create: `tools/Noof.Ledger.Demo/Noof.Ledger.Demo.csproj`
- Create: `tools/Noof.Ledger.Demo/DemoEntryPoint.cs`
- Create: `tools/Noof.Ledger.Demo/DemoDatabase.cs`
- Create: `tests/Noof.Ledger.Demo.Tests/Noof.Ledger.Demo.Tests.csproj`
- Create: `tests/Noof.Ledger.Demo.Tests/xunit.runner.json`
- Create: `tests/Noof.Ledger.Demo.Tests/DemoDatabaseTests.cs`
- Create: `tests/Noof.Ledger.Architecture.Tests/ToolsBoundaryTests.cs`
- Modify: `Directory.Packages.props` (add `Microsoft.Playwright`)
- Modify: `NoofLedger.slnx` (add `/tools/` folder and the test project)
- Modify: `src/Noof.Ledger.Persistence/Noof.Ledger.Persistence.csproj`, `src/Noof.Ledger.Host/Noof.Ledger.Host.csproj`, `src/Noof.Ledger.Telegram/Noof.Ledger.Telegram.csproj` (one `InternalsVisibleTo` each)

**Interfaces:**
- Produces: `DemoDatabase.Name` (`"noof_ledger_demo"`), `DemoDatabase.Port` (`5264`), `DemoDatabase.DefaultCredentialFile`, `DemoDatabase.AdminConnectionString(string credentialFile) : string`, `DemoDatabase.For(string adminConnectionString, string database) : string`, `DemoDatabase.EnsureDisposable(string database)`, `DemoDatabase.RecreateAsync(string adminConnectionString, string database, CancellationToken) : Task`, `DemoDatabase.DropAsync(string adminConnectionString, string database, CancellationToken) : Task`.

- [ ] **Step 1: Create the projects**

`Directory.Packages.props` — add next to the other Playwright line:
```xml
<PackageVersion Include="Microsoft.Playwright" Version="1.62.0" />
```

`tools/Noof.Ledger.Demo/Noof.Ledger.Demo.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Noof.Ledger.Demo.Tests" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Playwright" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Noof.Ledger.Host\Noof.Ledger.Host.csproj" />
  </ItemGroup>

</Project>
```

`tools/Noof.Ledger.Demo/DemoEntryPoint.cs` — an explicit `Main`, not top-level statements: the
referenced host's own `Program` is visible here through `InternalsVisibleTo`, and a second `Program`
type would conflict with it (CS0436, an error under `TreatWarningsAsErrors`).
```csharp
namespace Noof.Ledger.Demo;

internal static class DemoEntryPoint
{
    public static int Main()
    {
        Console.Error.WriteLine("Usage: Noof.Ledger.Demo start | refresh | shots");
        return 2;
    }
}
```

`tests/Noof.Ledger.Demo.Tests/Noof.Ledger.Demo.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>

  <ItemGroup>
    <Content Include="xunit.runner.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="AwesomeAssertions" />
    <PackageReference Include="xunit.v3.mtp-v2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\tools\Noof.Ledger.Demo\Noof.Ledger.Demo.csproj" />
    <ProjectReference Include="..\Noof.Ledger.TestKit\Noof.Ledger.TestKit.csproj" />
  </ItemGroup>

</Project>
```

`tests/Noof.Ledger.Demo.Tests/xunit.runner.json`:
```json
{
    "$schema": "https://xunit.net/schema/current/xunit.runner.schema.json",
    "parallelizeTestCollections": false
}
```

`NoofLedger.slnx` — add after the `/tests/` folder's last project, keeping alphabetical order inside
`/tests/`:
```xml
    <Project Path="tests/Noof.Ledger.Demo.Tests/Noof.Ledger.Demo.Tests.csproj" />
```
and a new folder after `</Folder>` of `/tests/`:
```xml
  <Folder Name="/tools/">
    <Project Path="tools/Noof.Ledger.Demo/Noof.Ledger.Demo.csproj" />
  </Folder>
```

In each of `src/Noof.Ledger.Persistence/Noof.Ledger.Persistence.csproj`,
`src/Noof.Ledger.Host/Noof.Ledger.Host.csproj` and `src/Noof.Ledger.Telegram/Noof.Ledger.Telegram.csproj`,
add inside the `ItemGroup` that already holds `InternalsVisibleTo` entries:
```xml
    <InternalsVisibleTo Include="Noof.Ledger.Demo" />
```

- [ ] **Step 2: Write the failing guard tests**

`tests/Noof.Ledger.Demo.Tests/DemoDatabaseTests.cs`:
```csharp
using AwesomeAssertions;
using Npgsql;

namespace Noof.Ledger.Demo.Tests;

public sealed class DemoDatabaseTests
{
    const string Admin = "Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=not-used";

    [Fact]
    public void The_demo_connection_string_names_exactly_the_demo_database()
    {
        var connection = new NpgsqlConnectionStringBuilder(DemoDatabase.For(Admin, DemoDatabase.Name));

        connection.Database.Should().Be("noof_ledger_demo");
        connection.Host.Should().Be("127.0.0.1");
    }

    [Theory]
    [InlineData("noof_ledger")]
    [InlineData("noof_ledger_test_template")]
    [InlineData("postgres")]
    [InlineData("noof_ledger_demo_old")]
    public void Any_other_database_is_refused(string database)
    {
        var act = () => DemoDatabase.For(Admin, database);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{database}*");
    }

    [Fact]
    public void A_throwaway_test_database_is_allowed()
    {
        new NpgsqlConnectionStringBuilder(DemoDatabase.For(Admin, "noof_test_0123abcd"))
            .Database.Should().Be("noof_test_0123abcd");
    }

    [Fact]
    public void Missing_credential_file_names_the_command_that_creates_it()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"noof-missing-{Guid.NewGuid():N}", "db.connection");

        var act = () => DemoDatabase.AdminConnectionString(missing);

        act.Should().Throw<InvalidOperationException>().WithMessage("*run.ps1 db-auth-reset*");
    }

    [Fact]
    public void The_credential_file_is_read_and_trimmed()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("noof-demo-cred-").FullName, "db.connection");
        File.WriteAllText(file, Admin + "\r\n");

        DemoDatabase.AdminConnectionString(file).Should().Be(Admin);
    }
}
```

- [ ] **Step 3: Run the tests to see them fail**

Run: `dotnet test --project tests\Noof.Ledger.Demo.Tests\Noof.Ledger.Demo.Tests.csproj --filter "Category!=Database"`
Expected: build FAILS — `DemoDatabase` does not exist.

- [ ] **Step 4: Implement `DemoDatabase`**

`tools/Noof.Ledger.Demo/DemoDatabase.cs`:
```csharp
using Npgsql;

namespace Noof.Ledger.Demo;

internal static class DemoDatabase
{
    public const string Name = "noof_ledger_demo";
    public const int Port = 5264;
    const string TestDatabasePrefix = "noof_test_";

    public static string DefaultCredentialFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoofLedger", "db.connection");

    public static string AdminConnectionString(string credentialFile) =>
        File.Exists(credentialFile)
            ? File.ReadAllText(credentialFile).Trim()
            : throw new InvalidOperationException(
                $"No PostgreSQL credential at {credentialFile}. Run .\\run.ps1 db-auth-reset once to create it.");

    public static string For(string adminConnectionString, string database)
    {
        EnsureDisposable(database);
        var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = database }.ConnectionString;

        return new NpgsqlConnectionStringBuilder(connectionString).Database == database
            ? connectionString
            : throw new InvalidOperationException($"Could not point the connection string at {database}.");
    }

    public static void EnsureDisposable(string database)
    {
        if (database != Name && !database.StartsWith(TestDatabasePrefix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Refusing to touch database '{database}': the demo tool only rebuilds {Name} or a {TestDatabasePrefix}* database.");
    }

    public static async Task RecreateAsync(string adminConnectionString, string database, CancellationToken cancellationToken)
    {
        EnsureDisposable(database);
        NpgsqlConnection.ClearAllPools();

        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync(cancellationToken);
            await ExecuteAsync(admin, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", cancellationToken);
            await ExecuteAsync(admin, $"CREATE DATABASE \"{database}\"", cancellationToken);
        }

        await using var target = new NpgsqlConnection(For(adminConnectionString, database));
        await target.OpenAsync(cancellationToken);
        await ExecuteAsync(target, "CREATE EXTENSION IF NOT EXISTS pg_trgm; CREATE EXTENSION IF NOT EXISTS unaccent;", cancellationToken);
    }

    public static async Task DropAsync(string adminConnectionString, string database, CancellationToken cancellationToken)
    {
        EnsureDisposable(database);
        NpgsqlConnection.ClearAllPools();

        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync(cancellationToken);
        await ExecuteAsync(admin, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", cancellationToken);
    }

    static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
```

- [ ] **Step 5: Run the tests to see them pass**

Run: `dotnet test --project tests\Noof.Ledger.Demo.Tests\Noof.Ledger.Demo.Tests.csproj --filter "Category!=Database"`
Expected: PASS, 8 tests.

- [ ] **Step 6: Write the boundary tests**

`tests/Noof.Ledger.Architecture.Tests/ToolsBoundaryTests.cs`:
```csharp
using System.Xml.Linq;
using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

public class ToolsBoundaryTests
{
    static XDocument[] SourceProjects() =>
        [.. Directory.EnumerateFiles(Path.Combine(RepoRoot.Find().FullName, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(XDocument.Load)];

    [Fact]
    public void No_src_project_references_a_tool()
    {
        var references = SourceProjects()
            .SelectMany(project => project.Descendants("ProjectReference"))
            .Select(reference => reference.Attribute("Include")!.Value.Replace('\\', '/'))
            .ToList();

        references.Should().NotBeEmpty();
        references.Should().NotContain(include => include.Contains("/tools/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Only_tests_and_the_demo_tool_see_src_internals()
    {
        var targets = SourceProjects()
            .SelectMany(project => project.Descendants("InternalsVisibleTo"))
            .Select(entry => entry.Attribute("Include")!.Value)
            .ToList();

        targets.Should().Contain("Noof.Ledger.Demo");
        targets.Should().OnlyContain(target =>
            target.EndsWith(".Tests", StringComparison.Ordinal)
            || target == "DynamicProxyGenAssembly2"
            || target == "Noof.Ledger.Demo");
    }
}
```

- [ ] **Step 7: Watch each boundary test fail, then put it back**

Temporarily add `<InternalsVisibleTo Include="Noof.Ledger.Sneaky" />` to
`src/Noof.Ledger.Telegram/Noof.Ledger.Telegram.csproj`, then run:
`dotnet test --project tests\Noof.Ledger.Architecture.Tests\Noof.Ledger.Architecture.Tests.csproj --filter "FullyQualifiedName~ToolsBoundaryTests"`
Expected: `Only_tests_and_the_demo_tool_see_src_internals` FAILS naming `Noof.Ledger.Sneaky`. Remove it.
Temporarily add `<ProjectReference Include="..\..\tools\Noof.Ledger.Demo\Noof.Ledger.Demo.csproj" />` to
`src/Noof.Ledger.Application/Noof.Ledger.Application.csproj`; the same command → `No_src_project_references_a_tool`
FAILS (a build error from the cycle counts as red too). Remove it. Run again: PASS.

- [ ] **Step 8: Run the whole Architecture project**

Run: `dotnet test --project tests\Noof.Ledger.Architecture.Tests\Noof.Ledger.Architecture.Tests.csproj`
Expected: PASS (all tests, including `ProjectReferenceTests` and `PublicSurfaceTests` unchanged).

- [ ] **Step 9: Commit**

```bash
git add Directory.Packages.props NoofLedger.slnx tools tests/Noof.Ledger.Demo.Tests tests/Noof.Ledger.Architecture.Tests/ToolsBoundaryTests.cs src/Noof.Ledger.Persistence/Noof.Ledger.Persistence.csproj src/Noof.Ledger.Host/Noof.Ledger.Host.csproj src/Noof.Ledger.Telegram/Noof.Ledger.Telegram.csproj
git commit -m "feat(demo): add the demo tool with a database-name guard"
```

---

### Task 2: Refresh — rebuild the schema, the user and the fake keys

**Files:**
- Create: `tools/Noof.Ledger.Demo/DemoPaths.cs`
- Create: `tools/Noof.Ledger.Demo/FixedClock.cs`
- Create: `tools/Noof.Ledger.Demo/DemoServices.cs`
- Create: `tools/Noof.Ledger.Demo/MockData.cs`
- Create: `tools/Noof.Ledger.Demo/MockDataWriter.cs`
- Create: `tools/Noof.Ledger.Demo/Refresh.cs`
- Create: `tests/Noof.Ledger.Demo.Tests/DemoTestDatabase.cs`
- Create: `tests/Noof.Ledger.Demo.Tests/RefreshTests.cs`

**Interfaces:**
- Consumes: `DemoDatabase.RecreateAsync`, `DemoDatabase.For`, `DemoDatabase.DropAsync` (Task 1).
- Produces: `DemoPaths(string Root)` with `Logs`, `KeyRing`, `static ForOperator()`; `DemoServices.Build(string connectionString, DemoPaths paths) : ServiceProvider`; `Refresh.RunAsync(string adminConnectionString, string database, DemoPaths paths, CancellationToken) : Task`; `MockData.Now`, `MockData.TimeZoneId`, `MockData.Username`, `MockData.Password`, `MockData.TelegramChatId`, `MockData.FakeKey`; `MockDataWriter.WriteAsync(IServiceProvider services, CancellationToken) : Task` (Task 3 extends it).

- [ ] **Step 1: Write the failing database test**

`tests/Noof.Ledger.Demo.Tests/DemoTestDatabase.cs`:
```csharp
using Noof.Ledger.TestKit;
using Npgsql;

namespace Noof.Ledger.Demo.Tests;

public sealed class DemoTestDatabase : IAsyncLifetime
{
    public string Name { get; } = $"noof_test_{Guid.NewGuid():N}";
    internal DemoPaths Paths { get; } = new(Directory.CreateTempSubdirectory("noof-demo-test-").FullName);
    public string Admin => DatabaseSettings.AdminConnectionString;
    public string ConnectionString => DemoDatabase.For(Admin, Name);
    public bool Unavailable { get; private set; }

    public async ValueTask InitializeAsync()
    {
        try
        {
            await using var connection = new NpgsqlConnection(Admin + ";Timeout=3");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
        }
        catch (Exception unreachable) when (unreachable is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            Unavailable = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!Unavailable)
            await DemoDatabase.DropAsync(Admin, Name, CancellationToken.None);

        Directory.Delete(Paths.Root, recursive: true);
    }
}
```

`tests/Noof.Ledger.Demo.Tests/RefreshTests.cs`:
```csharp
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Demo.Tests;

[Trait("Category", "Database")]
public sealed class RefreshTests(DemoTestDatabase database) : IClassFixture<DemoTestDatabase>
{
    [Fact]
    public async Task Refresh_creates_the_demo_user_and_fake_ai_keys_but_no_telegram_token()
    {
        if (database.Unavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await Refresh.RunAsync(database.Admin, database.Name, database.Paths, TestContext.Current.CancellationToken);

        await using var services = DemoServices.Build(database.ConnectionString, database.Paths);
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserStore>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();

        var user = await users.FindByUsernameAsync("demo", TestContext.Current.CancellationToken);
        user.Should().NotBeNull();
        hasher.Verify(user!, user!.PasswordHash, "demo").Should().NotBe(PasswordVerifyResult.Failed);

        (await secrets.GetAsync("anthropic-api-key", TestContext.Current.CancellationToken)).Value
            .Should().Be("demo-not-a-real-key");
        (await secrets.GetAsync("groq-api-key", TestContext.Current.CancellationToken)).Value
            .Should().Be("demo-not-a-real-key");
        (await secrets.GetStatusAsync(SecretKeys.TelegramBotToken, TestContext.Current.CancellationToken)).State
            .Should().Be(SecretState.Missing);
    }

    [Fact]
    public async Task Refreshing_twice_leaves_exactly_one_demo_user()
    {
        if (database.Unavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await Refresh.RunAsync(database.Admin, database.Name, database.Paths, TestContext.Current.CancellationToken);
        await Refresh.RunAsync(database.Admin, database.Name, database.Paths, TestContext.Current.CancellationToken);

        await using var services = DemoServices.Build(database.ConnectionString, database.Paths);
        await using var scope = services.CreateAsyncScope();
        var user = await scope.ServiceProvider.GetRequiredService<IUserStore>()
            .FindByUsernameAsync("demo", TestContext.Current.CancellationToken);

        user.Should().NotBeNull();
    }
}
```
(`DemoTestDatabase.Paths` is `internal` because `DemoPaths` is: a public member of an internal type
does not compile.)

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test --project tests\Noof.Ledger.Demo.Tests\Noof.Ledger.Demo.Tests.csproj --filter "Category=Database"`
Expected: build FAILS — `Refresh`, `DemoServices`, `DemoPaths` do not exist.

- [ ] **Step 3: Implement paths, clock and services**

`tools/Noof.Ledger.Demo/DemoPaths.cs`:
```csharp
namespace Noof.Ledger.Demo;

internal sealed record DemoPaths(string Root)
{
    public string Logs => Path.Combine(Root, "logs");
    public string KeyRing => Path.Combine(Root, "dp-keys");

    public static DemoPaths ForOperator() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoofLedger", "demo"));
}
```

`tools/Noof.Ledger.Demo/FixedClock.cs`:
```csharp
namespace Noof.Ledger.Demo;

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
```

`tools/Noof.Ledger.Demo/DemoServices.cs`:
```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Host.Auth;
using Noof.Ledger.Host.Startup;
using Noof.Ledger.Persistence;

namespace Noof.Ledger.Demo;

internal static class DemoServices
{
    const int MaxJobAttempts = 8;

    public static ServiceProvider Build(string connectionString, DemoPaths paths)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Ledger"] = connectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<TimeProvider>(new FixedClock(MockData.Now));
        services.AddSingleton(CaptureTimeZoneGuard.Resolve(MockData.TimeZoneId));
        DataProtectionSetup.Configure(services, new DirectoryInfo(paths.KeyRing));
        services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
        services.AddNoofPersistence(configuration, MaxJobAttempts);

        return services.BuildServiceProvider();
    }
}
```

- [ ] **Step 4: Implement the first slice of mock data and the refresh**

`tools/Noof.Ledger.Demo/MockData.cs`:
```csharp
namespace Noof.Ledger.Demo;

internal static class MockData
{
    public const string TimeZoneId = "Europe/Belgrade";
    public const string Username = "demo";
    public const string Password = "demo";
    public const long TelegramChatId = 555_000_001;
    public const string FakeKey = "demo-not-a-real-key";
    public const string AnthropicKeySecret = "anthropic-api-key";
    public const string GroqKeySecret = "groq-api-key";

    public static readonly DateTimeOffset Now = new(2026, 9, 20, 18, 0, 0, TimeSpan.Zero);
    public static readonly Guid UserId = Guid.Parse("7a1c0000-0000-4000-8000-0000000000aa");
}
```

`tools/Noof.Ledger.Demo/MockDataWriter.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence;
using Noof.Ledger.Persistence.Backup;

namespace Noof.Ledger.Demo;

internal static class MockDataWriter
{
    public static async Task WriteAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await WriteUserAsync(services, cancellationToken);
        await WriteSecretsAsync(services.GetRequiredService<ISecretStore>(), cancellationToken);
        await WriteBackupRunAsync(services.GetRequiredService<LedgerDbContext>(), cancellationToken);
    }

    static async Task WriteUserAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var user = new AppUser
        {
            Id = MockData.UserId,
            Username = MockData.Username,
            PasswordHash = string.Empty,
            CreatedAt = MockData.Now,
        };
        user.PasswordHash = services.GetRequiredService<IPasswordHasher>().Hash(user, MockData.Password);

        await services.GetRequiredService<IUserStore>().UpsertAsync(user, cancellationToken);
    }

    static async Task WriteSecretsAsync(ISecretStore secrets, CancellationToken cancellationToken)
    {
        await secrets.SetAsync(MockData.AnthropicKeySecret, MockData.FakeKey, cancellationToken);
        await secrets.SetAsync(MockData.GroqKeySecret, MockData.FakeKey, cancellationToken);
    }

    static async Task WriteBackupRunAsync(LedgerDbContext db, CancellationToken cancellationToken)
    {
        // The real clock, not MockData.Now: the Backups health check measures a backup's age against the
        // host's own clock, and anything older than 26 hours reads as stale.
        var finishedAt = DateTimeOffset.UtcNow.AddHours(-2);

        db.BackupRuns.Add(new BackupRun
        {
            Id = Guid.Parse("7a1c0000-0000-4000-8000-0000000000bb"),
            StartedAt = finishedAt.AddMinutes(-1),
            FinishedAt = finishedAt,
            Succeeded = true,
            FileName = "noof_ledger_demo-mock.dump",
            SizeBytes = 1_048_576,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
```

`tools/Noof.Ledger.Demo/Refresh.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Persistence;

namespace Noof.Ledger.Demo;

internal static class Refresh
{
    public static async Task RunAsync(string adminConnectionString, string database, DemoPaths paths, CancellationToken cancellationToken)
    {
        await DemoDatabase.RecreateAsync(adminConnectionString, database, cancellationToken);

        await using var services = DemoServices.Build(DemoDatabase.For(adminConnectionString, database), paths);
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.MigrateAsync(cancellationToken);
        await MockDataWriter.WriteAsync(scope.ServiceProvider, cancellationToken);
    }
}
```

- [ ] **Step 5: Run the database test to see it pass**

Run: `dotnet test --project tests\Noof.Ledger.Demo.Tests\Noof.Ledger.Demo.Tests.csproj --filter "Category=Database"`
Expected: PASS, 2 tests (or both skipped only if PostgreSQL is down — then start it with
`.\run.ps1 pg start` from an elevated shell and rerun; a skip is not a pass).

- [ ] **Step 6: Commit**

```bash
git add tools/Noof.Ledger.Demo tests/Noof.Ledger.Demo.Tests
git commit -m "feat(demo): rebuild the demo database with its user and fake AI keys"
```

---

### Task 3: The mock ledger — wallets, a month of activity, a trace and log rows

**Files:**
- Modify: `tools/Noof.Ledger.Demo/MockData.cs`
- Modify: `tools/Noof.Ledger.Demo/MockDataWriter.cs`
- Create: `tests/Noof.Ledger.Demo.Tests/MockLedgerTests.cs`

**Interfaces:**
- Consumes: `DemoServices.Build`, `Refresh.RunAsync`, `DemoTestDatabase` (Task 2).
- Produces: `MockData.Wallets : IReadOnlyList<MockWallet>`, `MockData.Records : IReadOnlyList<MockRecord>`, `MockData.TracedTransactionId : Guid`, `MockData.FailedTransactionId : Guid`, `MockData.LogWindowStart : DateOnly` (2026-09-01), `MockData.LogWindowEnd : DateOnly` (2026-09-21, exclusive) — Task 6 uses the ids and the window.

- [ ] **Step 1: Write the failing balance test**

`tests/Noof.Ledger.Demo.Tests/MockLedgerTests.cs`:
```csharp
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Demo.Tests;

[Trait("Category", "Database")]
public sealed class MockLedgerTests(DemoTestDatabase database) : IClassFixture<DemoTestDatabase>
{
    [Fact]
    public async Task Every_wallet_ends_the_mock_month_on_its_hand_computed_balance()
    {
        if (database.Unavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await Refresh.RunAsync(database.Admin, database.Name, database.Paths, TestContext.Current.CancellationToken);

        await using var services = DemoServices.Build(database.ConnectionString, database.Paths);
        await using var scope = services.CreateAsyncScope();
        var balances = await scope.ServiceProvider.GetRequiredService<IBalanceReadModel>()
            .BalancesAsync(TestContext.Current.CancellationToken);

        balances.ToDictionary(wallet => wallet.WalletName, wallet => wallet.Balances).Should().BeEquivalentTo(
            new Dictionary<string, IReadOnlyList<Money>>
            {
                // 3000 - 34.50 - 3.20 + 2800 - 42.00 = 5720.30, re-anchored to 5700.00 on 15.09, then - 40.30
                ["Wise"] = [new(5659.70m, CurrencyCode.Eur)],
                // 180000 - 3450 - 850 - 1200
                ["Raiffeisen"] = [new(174500.00m, CurrencyCode.Rsd)],
                // 600 - 4.50 - 45 + 450
                ["Cash"] = [new(1000.50m, CurrencyCode.Usd)],
                // 50000 - 599 - 1450
                ["Tinkoff"] = [new(47951.00m, CurrencyCode.Rub)],
                // 200000 - 6000 - 8500
                ["Kaspi"] = [new(185500.00m, CurrencyCode.Kzt)],
                ["Old Revolut"] = [new(900.00m, CurrencyCode.Eur)],
                ["Main Wallet"] = [],
            });
    }

    [Fact]
    public async Task Raiffeisen_takes_the_rsd_default_and_old_revolut_is_archived()
    {
        if (database.Unavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await Refresh.RunAsync(database.Admin, database.Name, database.Paths, TestContext.Current.CancellationToken);

        await using var services = DemoServices.Build(database.ConnectionString, database.Paths);
        await using var scope = services.CreateAsyncScope();
        var wallets = await scope.ServiceProvider.GetRequiredService<Noof.Ledger.Application.Wallets.IWalletAdmin>()
            .ListAsync(TestContext.Current.CancellationToken);

        wallets.Single(wallet => wallet.Name == "Raiffeisen").IsDefaultForCurrency.Should().BeTrue();
        wallets.Single(wallet => wallet.Name == "Main Wallet").IsDefaultForCurrency.Should().BeFalse();
        wallets.Single(wallet => wallet.Name == "Old Revolut").Archived.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test --project tests\Noof.Ledger.Demo.Tests\Noof.Ledger.Demo.Tests.csproj --filter "Category=Database"`
Expected: FAIL — only "Main Wallet" exists, with no balances.

- [ ] **Step 3: Add the mock ledger to `MockData`**

Add to `tools/Noof.Ledger.Demo/MockData.cs` (inside the class, plus the records at the end of the file):
```csharp
    public static readonly DateOnly OpeningDate = new(2026, 9, 1);
    public static readonly DateOnly LogWindowStart = new(2026, 9, 1);
    public static readonly DateOnly LogWindowEnd = new(2026, 9, 21);
    public static readonly Guid TracedTransactionId = Id(900);
    public static readonly Guid FailedTransactionId = Id(901);

    public static IReadOnlyList<MockWallet> Wallets { get; } =
    [
        new("Wise", CurrencyCode.Eur, 3000.00m, ["wise"], Default: true),
        new("Raiffeisen", CurrencyCode.Rsd, 180000.00m, ["raif"], Default: true),
        new("Cash", CurrencyCode.Usd, 600.00m, [], Default: true),
        new("Tinkoff", CurrencyCode.Rub, 50000.00m, [], Default: true),
        new("Kaspi", CurrencyCode.Kzt, 200000.00m, [], Default: true),
        new("Old Revolut", CurrencyCode.Eur, 900.00m, [], Default: false, Archived: true),
    ];

    public static IReadOnlyList<MockRecord> Records { get; } =
    [
        Expense("Wise", "Maxi groceries 34.50 eur", Day(2), [new("Groceries", 34.50m, "groceries", "Maxi")]),
        Expense("Kaspi", "haircut 6000 kzt", Day(2), [new("Haircut", 6000m, "personal-care", null)]),
        Expense("Raiffeisen", "Maxi 3450 rsd", Day(3), [new("Groceries", 3450m, "groceries", "Maxi")]),
        Expense("Cash", "coffee 4.50 usd", Day(4), [new("Coffee", 4.50m, "coffee", null)]),
        Expense("Wise", "coffee 3.20 eur", Day(5), [new("Coffee", 3.20m, "coffee", null)]),
        Expense("Tinkoff", "netflix 599 rub", Day(7), [new("Netflix", 599m, "subscriptions", null)]),
        Expense("Raiffeisen", "taxi 850 rsd", Day(8), [new("Taxi", 850m, "transport", null)]),
        Expense("Cash", "internet 45 usd", Day(9), [new("Internet", 45.00m, "utilities", null)]),
        Income("Wise", "salary 2800 eur", Day(10), new("Salary", 2800.00m, "salary", null)),
        Income("Cash", "+450 usd freelance", Day(11), new("Freelance", 450.00m, "other-income", null)),
        Expense("Wise", "dinner at Walter 42 eur", Day(12), [new("Dinner", 42.00m, "restaurants", "Walter")]),
        Expense("Tinkoff", "groceries 1450 rub", Day(13), [new("Groceries", 1450m, "groceries", null)]),
        Expense("Raiffeisen", "pharmacy 1200 rsd", Day(14), [new("Pharmacy", 1200m, "health", null)]),
        Statement("Wise", "wise balance 5700", Day(15), stated: 5700.00m, computedBefore: 5720.30m),
        Expense("Kaspi", "groceries 8500 kzt", Day(16), [new("Groceries", 8500m, "groceries", null)]),
        Expense("Wise", "Lidl groceries 27.80, wine 12.50 eur", Day(18),
            [new("Groceries", 27.80m, "groceries", "Lidl"), new("Wine", 12.50m, "groceries", "Lidl")],
            TracedTransactionId),
        Failed("Raiffeisen", "#@%& ???", Day(19), FailedTransactionId),
    ];

    public static Guid Id(int number) => Guid.Parse($"7a1c0000-0000-4000-8000-{number:D12}");

    static DateOnly Day(int day) => new(2026, 9, day);

    static MockRecord Expense(string wallet, string raw, DateOnly day, IReadOnlyList<MockLine> lines, Guid? id = null) =>
        new(wallet, TransactionKind.Expense, TransactionStatus.Completed, raw, day, lines, id);

    static MockRecord Income(string wallet, string raw, DateOnly day, MockLine line) =>
        new(wallet, TransactionKind.Income, TransactionStatus.Completed, raw, day, [line]);

    static MockRecord Statement(string wallet, string raw, DateOnly day, decimal stated, decimal computedBefore) =>
        new(wallet, TransactionKind.BalanceCheck, TransactionStatus.Completed, raw, day, [], Stated: stated, ComputedBefore: computedBefore);

    static MockRecord Failed(string wallet, string raw, DateOnly day, Guid id) =>
        new(wallet, TransactionKind.Expense, TransactionStatus.Failed, raw, day, [], id);
```
At the end of the file (outside `MockData`):
```csharp
internal sealed record MockWallet(string Name, CurrencyCode Currency, decimal Opening, IReadOnlyList<string> Aliases, bool Default, bool Archived = false);

internal sealed record MockLine(string Description, decimal Amount, string CategorySlug, string? Merchant);

internal sealed record MockRecord(
    string Wallet,
    TransactionKind Kind,
    TransactionStatus Status,
    string RawText,
    DateOnly Day,
    IReadOnlyList<MockLine> Lines,
    Guid? Id = null,
    decimal? Stated = null,
    decimal? ComputedBefore = null);
```
Add `using Noof.Ledger.Domain;` at the top of `MockData.cs`.

- [ ] **Step 4: Write the ledger in `MockDataWriter`**

In `MockDataWriter.WriteAsync`, before `WriteUserAsync`, add:
```csharp
        var db = services.GetRequiredService<LedgerDbContext>();
        var wallets = await WriteWalletsAsync(services.GetRequiredService<IWalletAdmin>(), cancellationToken);
        var categories = await db.Categories.ToDictionaryAsync(category => category.Slug, category => category.Id, cancellationToken);
        var merchants = await WriteMerchantsAsync(db, cancellationToken);

        for (var index = 0; index < MockData.Records.Count; index++)
            await WriteRecordAsync(db, MockData.Records[index], index + 1, wallets, categories, merchants, cancellationToken);

        await WriteTraceAsync(db, cancellationToken);
```
And add these members (plus `using Microsoft.EntityFrameworkCore; using Noof.Ledger.Application.Diagnostics; using Noof.Ledger.Application.Wallets; using Noof.Ledger.Persistence.Diagnostics; using Noof.Ledger.Persistence.Revisions;`):
```csharp
    static async Task<Dictionary<string, Guid>> WriteWalletsAsync(IWalletAdmin admin, CancellationToken cancellationToken)
    {
        var ids = new Dictionary<string, Guid>();
        foreach (var wallet in MockData.Wallets)
        {
            ids[wallet.Name] = await admin.CreateAsync(
                new NewWallet(wallet.Name, wallet.Currency, wallet.Opening, MockData.OpeningDate, wallet.Aliases, wallet.Default),
                cancellationToken);

            if (wallet.Archived)
                await admin.ArchiveAsync(ids[wallet.Name], cancellationToken);
        }

        return ids;
    }

    static async Task<Dictionary<string, Guid>> WriteMerchantsAsync(LedgerDbContext db, CancellationToken cancellationToken)
    {
        var names = MockData.Records.SelectMany(record => record.Lines)
            .Select(line => line.Merchant).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList();

        var ids = names.Select((name, index) => (name, id: MockData.Id(800 + index))).ToDictionary(pair => pair.name, pair => pair.id);
        foreach (var (name, id) in ids)
        {
            db.Merchants.Add(new Merchant { Id = id, DisplayName = name, Kind = MerchantKind.Retail });
            db.MerchantAliases.Add(new MerchantAlias { Folded = MerchantName.Fold(name), MerchantId = id, CreatedAt = MockData.Now });
        }

        await db.SaveChangesAsync(cancellationToken);
        return ids;
    }

    static async Task WriteRecordAsync(
        LedgerDbContext db, MockRecord record, int messageId, Dictionary<string, Guid> wallets,
        Dictionary<string, Guid> categories, Dictionary<string, Guid> merchants, CancellationToken cancellationToken)
    {
        var walletId = wallets[record.Wallet];
        var currency = MockData.Wallets.Single(wallet => wallet.Name == record.Wallet).Currency;
        var occurredAt = ZonedClock.StartOfDay(record.Day, MockData.TimeZoneId).AddHours(12).AddMinutes(messageId);
        var transaction = new Transaction
        {
            Id = record.Id ?? MockData.Id(messageId),
            WalletId = walletId,
            Kind = record.Kind,
            RawText = record.RawText,
            CaptureKind = CaptureKind.Text,
            Status = record.Status,
            TimeZoneId = MockData.TimeZoneId,
            OccurredAt = occurredAt,
            OccurredOn = record.Day,
            TelegramChatId = MockData.TelegramChatId,
            TelegramMessageId = messageId,
            BotMessageId = 10_000 + messageId,
            CreatedAt = occurredAt,
        };
        db.Transactions.Add(transaction);

        if (record is { Kind: TransactionKind.BalanceCheck, Stated: { } stated, ComputedBefore: { } computedBefore })
        {
            db.BalanceChecks.Add(new BalanceCheck
            {
                TransactionId = transaction.Id,
                WalletId = walletId,
                Stated = new Money(stated, currency),
                ComputedBefore = computedBefore,
            });
        }
        else if (record.Status == TransactionStatus.Completed)
        {
            var total = record.Lines.Sum(line => line.Amount);
            db.Entries.Add(new Entry
            {
                Id = MockData.Id(1000 + messageId),
                TransactionId = transaction.Id,
                WalletId = walletId,
                Amount = new Money(record.Kind == TransactionKind.Income ? total : -total, currency),
                Role = EntryRole.Principal,
            });

            for (var line = 0; line < record.Lines.Count; line++)
            {
                db.LineItems.Add(new LineItem
                {
                    Id = MockData.Id(2000 + messageId * 10 + line),
                    TransactionId = transaction.Id,
                    Description = record.Lines[line].Description,
                    Amount = new Money(record.Lines[line].Amount, currency),
                    CategoryId = categories[record.Lines[line].CategorySlug],
                    CategorizedBy = CategorizationAuthority.Model,
                    MerchantId = record.Lines[line].Merchant is { } merchant ? merchants[merchant] : null,
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        if (record.Status == TransactionStatus.Completed)
        {
            await RevisionLog.AppendAsync(
                db, transaction, RevisionKind.Initial, null, TransactionStatus.Captured, occurredAt.AddSeconds(2), cancellationToken);
        }

        if (transaction.Id == MockData.TracedTransactionId)
        {
            await RevisionLog.AppendAsync(
                db, transaction, RevisionKind.Correction, "wine was 12.50, not 15", TransactionStatus.Completed,
                occurredAt.AddMinutes(2), cancellationToken);
        }
    }

    static async Task WriteTraceAsync(LedgerDbContext db, CancellationToken cancellationToken)
    {
        var traced = await db.Transactions.SingleAsync(transaction => transaction.Id == MockData.TracedTransactionId, cancellationToken);
        var failed = await db.Transactions.SingleAsync(transaction => transaction.Id == MockData.FailedTransactionId, cancellationToken);

        db.AppLogs.AddRange(
            Stage(traced.Id, traced.OccurredAt, "Noof.Ledger.Telegram.TelegramUpdateRouter", TransactionStages.Received, TransactionStages.ReceivedEventId),
            Stage(traced.Id, traced.OccurredAt.AddMilliseconds(1450), "Noof.Ledger.Host.Workers.CategorizationWorker", TransactionStages.Categorized, TransactionStages.CategorizedEventId),
            Stage(traced.Id, traced.OccurredAt.AddMilliseconds(1520), "Noof.Ledger.Host.Workers.CategorizationWorker", TransactionStages.Persisted, TransactionStages.PersistedEventId),
            Stage(traced.Id, traced.OccurredAt.AddMilliseconds(1690), "Noof.Ledger.Telegram.TelegramChatNotifier", TransactionStages.Replied, TransactionStages.RepliedEventId),
            Stage(failed.Id, failed.OccurredAt, "Noof.Ledger.Telegram.TelegramUpdateRouter", TransactionStages.Received, TransactionStages.ReceivedEventId),
            StageFailed(failed.Id, failed.OccurredAt.AddMilliseconds(2300), TransactionStages.Categorized),
            Row(new DateTimeOffset(2026, 9, 19, 9, 0, 0, TimeSpan.Zero), LogSeverity.Debug, "Noof.Ledger.Telegram.TelegramPollingService", "Polled Telegram: 1 update"),
            Row(new DateTimeOffset(2026, 9, 19, 9, 5, 0, TimeSpan.Zero), LogSeverity.Information, "Noof.Ledger.Host.Workers.BackupWorker", "Backup finished: noof_ledger-20260919.dump"),
            Row(new DateTimeOffset(2026, 9, 19, 14, 30, 0, TimeSpan.Zero), LogSeverity.Warning, "Noof.Ledger.Ai", "Model call took 31.2 s, over its 30 s threshold"),
            Row(new DateTimeOffset(2026, 9, 20, 8, 15, 0, TimeSpan.Zero), LogSeverity.Error, "Noof.Ledger.Ai", "Categorisation call failed",
                "System.TimeoutException: The operation timed out after 00:00:30."));

        await db.SaveChangesAsync(cancellationToken);
    }

    static AppLogEntry Stage(Guid transactionId, DateTimeOffset at, string source, string stage, int eventId) => new()
    {
        Id = 0,
        LoggedAt = at,
        Level = LogSeverity.Information,
        Source = source,
        Message = stage,
        Template = "{Stage}",
        TransactionId = transactionId,
        PropertiesJson = $$"""{"Stage":"{{stage}}","EventId":{"Id":{{eventId}},"Name":"{{stage}}"},"TransactionId":"{{transactionId}}"}""",
    };

    static AppLogEntry StageFailed(Guid transactionId, DateTimeOffset at, string failedStage) => new()
    {
        Id = 0,
        LoggedAt = at,
        Level = LogSeverity.Error,
        Source = "Noof.Ledger.Host.Workers.CategorizationWorker",
        Message = $"{TransactionStages.StageFailed} at stage {failedStage}",
        Template = "{Stage} at stage {FailedStage}",
        Exception = "Noof.Ledger.Application.Categorization.ModelCallException: the message could not be read as an amount",
        TransactionId = transactionId,
        PropertiesJson = $$"""{"Stage":"{{TransactionStages.StageFailed}}","FailedStage":"{{failedStage}}","EventId":{"Id":{{TransactionStages.StageFailedEventId}},"Name":"{{TransactionStages.StageFailed}}"},"TransactionId":"{{transactionId}}"}""",
    };

    static AppLogEntry Row(DateTimeOffset at, LogSeverity level, string source, string message, string? exception = null) => new()
    {
        Id = 0,
        LoggedAt = at,
        Level = level,
        Source = source,
        Message = message,
        Template = "{Message}",
        Exception = exception,
    };
```

- [ ] **Step 5: Run the database tests to see them pass**

Run: `dotnet test --project tests\Noof.Ledger.Demo.Tests\Noof.Ledger.Demo.Tests.csproj --filter "Category=Database"`
Expected: PASS, 4 tests. If a balance differs, fix the mock data, not the expected number — the
comments in the test are the arithmetic.

- [ ] **Step 6: Commit**

```bash
git add tools/Noof.Ledger.Demo tests/Noof.Ledger.Demo.Tests
git commit -m "feat(demo): fill the demo database with a month of mock activity"
```

---

### Task 4: Run the app on the demo — `.\run.ps1 demo`

**Files:**
- Create: `tools/Noof.Ledger.Demo/RepoPaths.cs`
- Create: `tools/Noof.Ledger.Demo/PortProbe.cs`
- Create: `tools/Noof.Ledger.Demo/DemoHost.cs`
- Create: `tools/Noof.Ledger.Demo/DemoCommands.cs`
- Modify: `tools/Noof.Ledger.Demo/DemoEntryPoint.cs`
- Create: `tests/Noof.Ledger.Demo.Tests/PortProbeTests.cs`
- Create: `tests/Noof.Ledger.Demo.Tests/DemoHostTests.cs`
- Modify: `run.ps1` (helper, command table, `demo` branch, `test fast`/`db` suites)
- Modify: `tests/Noof.Ledger.Architecture.Tests/RunScriptTests.cs` (`CommandNames`)
- Modify: `ops/clean-test-databases.ps1` (`$Protected`)

**Interfaces:**
- Consumes: `Refresh.RunAsync`, `DemoDatabase`, `DemoPaths`, `MockData.Username/Password` (Tasks 1–3).
- Produces: `RepoPaths.Root : DirectoryInfo`, `RepoPaths.Host`, `RepoPaths.Screenshots`, `RepoPaths.SendReady`; `PortProbe.IsListening(int port) : bool`; `DemoHost.BaseUrl`; `DemoHost.StartInfo(string connectionString, DemoPaths paths, bool redirect) : ProcessStartInfo`; `DemoHost.StartAsync(string connectionString, DemoPaths paths, CancellationToken) : Task<DemoHost>` (`IAsyncDisposable`); `DemoCommands.RefreshDemoAsync(DemoPaths paths, CancellationToken) : Task<string>` (returns the demo connection string).

- [ ] **Step 1: Write the failing unit tests**

`tests/Noof.Ledger.Demo.Tests/PortProbeTests.cs`:
```csharp
using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;

namespace Noof.Ledger.Demo.Tests;

public sealed class PortProbeTests
{
    [Fact]
    public void A_listening_port_is_seen_and_a_closed_one_is_not()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        PortProbe.IsListening(port).Should().BeTrue();

        listener.Stop();
        PortProbe.IsListening(port).Should().BeFalse();
    }
}
```

`tests/Noof.Ledger.Demo.Tests/DemoHostTests.cs`:
```csharp
using AwesomeAssertions;

namespace Noof.Ledger.Demo.Tests;

public sealed class DemoHostTests
{
    const string ConnectionString = "Host=127.0.0.1;Port=5432;Database=noof_ledger_demo;Username=postgres;Password=s3cret-value";
    static readonly DemoPaths Paths = new(@"C:\demo-root");

    [Fact]
    public void Connection_string_travels_only_in_the_environment()
    {
        var start = DemoHost.StartInfo(ConnectionString, Paths, redirect: true);

        start.Environment["ConnectionStrings__Ledger"].Should().Be(ConnectionString);
        start.ArgumentList.Should().NotContain(argument => argument.Contains("s3cret-value", StringComparison.Ordinal));
        start.Arguments.Should().NotContain("s3cret-value");
    }

    [Fact]
    public void The_host_listens_on_loopback_5264_and_writes_only_under_the_demo_root()
    {
        var start = DemoHost.StartInfo(ConnectionString, Paths, redirect: true);

        start.Environment["Urls"].Should().Be("http://127.0.0.1:5264");
        start.Environment["Logging__File__Directory"].Should().Be(@"C:\demo-root\logs");
        start.Environment["DataProtection__KeyRingDirectory"].Should().Be(@"C:\demo-root\dp-keys");
        start.Environment["Backup__Enabled"].Should().Be("false");
    }

    [Fact]
    public void The_host_runs_like_run_ps1_start()
    {
        var start = DemoHost.StartInfo(ConnectionString, Paths, redirect: true);

        start.FileName.Should().Be("dotnet");
        start.ArgumentList.Should().ContainInOrder("run", "--project");
        start.ArgumentList.Should().Contain(["-c", "Release", "--no-launch-profile"]);
        start.UseShellExecute.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test --project tests\Noof.Ledger.Demo.Tests\Noof.Ledger.Demo.Tests.csproj --filter "Category!=Database"`
Expected: build FAILS — `PortProbe`, `DemoHost` do not exist.

- [ ] **Step 3: Implement `RepoPaths`, `PortProbe` and `DemoHost`**

`tools/Noof.Ledger.Demo/RepoPaths.cs`:
```csharp
namespace Noof.Ledger.Demo;

internal static class RepoPaths
{
    public static DirectoryInfo Root { get; } = FindRoot();
    public static string Host => Path.Combine(Root.FullName, "src", "Noof.Ledger.Host");
    public static string Screenshots => Path.Combine(Root.FullName, "docs", "screenshots");
    public static string SendReady => Path.Combine(Root.FullName, "artifacts", "screenshots");

    static DirectoryInfo FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
            directory = directory.Parent;

        return directory ?? throw new InvalidOperationException("global.json not found above the demo tool's output directory.");
    }
}
```

`tools/Noof.Ledger.Demo/PortProbe.cs`:
```csharp
using System.Net;
using System.Net.Sockets;

namespace Noof.Ledger.Demo;

internal static class PortProbe
{
    public static bool IsListening(int port)
    {
        using var client = new TcpClient();
        try
        {
            client.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
```

`tools/Noof.Ledger.Demo/DemoHost.cs`:
```csharp
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Noof.Ledger.Demo;

internal sealed class DemoHost(Process process) : IAsyncDisposable
{
    static readonly TimeSpan StartupLimit = TimeSpan.FromMinutes(3);

    public static string BaseUrl => $"http://127.0.0.1:{DemoDatabase.Port}";

    public static ProcessStartInfo StartInfo(string connectionString, DemoPaths paths, bool redirect)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            WorkingDirectory = RepoPaths.Root.FullName,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
        };
        foreach (var argument in (string[])["run", "--project", RepoPaths.Host, "-c", "Release", "--no-launch-profile"])
            start.ArgumentList.Add(argument);

        start.Environment["ConnectionStrings__Ledger"] = connectionString;
        start.Environment["Urls"] = BaseUrl;
        start.Environment["Logging__File__Directory"] = paths.Logs;
        start.Environment["DataProtection__KeyRingDirectory"] = paths.KeyRing;
        start.Environment["Backup__Enabled"] = "false";
        return start;
    }

    public static async Task<DemoHost> StartAsync(string connectionString, DemoPaths paths, CancellationToken cancellationToken)
    {
        var recent = new ConcurrentQueue<string>();
        var process = Process.Start(StartInfo(connectionString, paths, redirect: true))
            ?? throw new InvalidOperationException("Could not start dotnet.");
        process.OutputDataReceived += (_, line) => Remember(recent, line.Data);
        process.ErrorDataReceived += (_, line) => Remember(recent, line.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var host = new DemoHost(process);
        try
        {
            await WaitUntilReadyAsync(process, recent, cancellationToken);
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);

        await process.WaitForExitAsync();
        process.Dispose();
    }

    static async Task WaitUntilReadyAsync(Process process, ConcurrentQueue<string> recent, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + StartupLimit;

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                throw new InvalidOperationException($"The demo host exited early:\n{string.Join('\n', recent)}");

            try
            {
                var page = await http.GetStringAsync($"{BaseUrl}/account/login", cancellationToken);
                if (!page.Contains("id=\"database-waiting\"", StringComparison.Ordinal))
                    return;
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new TimeoutException($"The demo host did not come up within {StartupLimit}:\n{string.Join('\n', recent)}");
    }

    static void Remember(ConcurrentQueue<string> recent, string? line)
    {
        if (line is null)
            return;

        recent.Enqueue(line);
        while (recent.Count > 40)
            recent.TryDequeue(out _);
    }
}
```
(The ready check waits for the sign-in page to render without `id="database-waiting"`, never for a
bare 200 — `.claude/rules/web-ui.md`. `DateTime.UtcNow` here is only a wall-clock deadline for a
console tool, not app logic.)

- [ ] **Step 4: Run the unit tests to see them pass**

Run: `dotnet test --project tests\Noof.Ledger.Demo.Tests\Noof.Ledger.Demo.Tests.csproj --filter "Category!=Database"`
Expected: PASS.

- [ ] **Step 5: Wire the `start` and `refresh` commands**

`tools/Noof.Ledger.Demo/DemoCommands.cs`:
```csharp
using System.Diagnostics;

namespace Noof.Ledger.Demo;

internal static class DemoCommands
{
    public static async Task<int> RefreshAsync()
    {
        await RefreshDemoAsync(DemoPaths.ForOperator(), CancellationToken.None);
        Console.WriteLine($"{DemoDatabase.Name} refreshed with the mock data.");
        return 0;
    }

    public static async Task<int> StartAsync()
    {
        var paths = DemoPaths.ForOperator();
        var connectionString = await RefreshDemoAsync(paths, CancellationToken.None);

        using var process = Process.Start(DemoHost.StartInfo(connectionString, paths, redirect: false))
            ?? throw new InvalidOperationException("Could not start dotnet.");
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, press) =>
        {
            press.Cancel = true;
            stop.Cancel();
        };

        Console.WriteLine($"Demo on {DemoHost.BaseUrl} - sign in as {MockData.Username} / {MockData.Password}. Ctrl+C stops it.");
        try
        {
            await process.WaitForExitAsync(stop.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        return 0;
    }

    public static async Task<string> RefreshDemoAsync(DemoPaths paths, CancellationToken cancellationToken)
    {
        if (PortProbe.IsListening(DemoDatabase.Port))
            throw new InvalidOperationException($"A demo is already running on {DemoHost.BaseUrl} - stop it first.");

        var admin = DemoDatabase.AdminConnectionString(DemoDatabase.DefaultCredentialFile);
        await Refresh.RunAsync(admin, DemoDatabase.Name, paths, cancellationToken);
        return DemoDatabase.For(admin, DemoDatabase.Name);
    }
}
```

Replace `tools/Noof.Ledger.Demo/DemoEntryPoint.cs`:
```csharp
namespace Noof.Ledger.Demo;

internal static class DemoEntryPoint
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["start"] => await DemoCommands.StartAsync(),
                ["refresh"] => await DemoCommands.RefreshAsync(),
                _ => Usage(),
            };
        }
        catch (InvalidOperationException refused)
        {
            Console.Error.WriteLine(refused.Message);
            return 1;
        }
    }

    static int Usage()
    {
        Console.Error.WriteLine("Usage: Noof.Ledger.Demo start | refresh | shots");
        return 2;
    }
}
```

- [ ] **Step 6: Add `demo` to `run.ps1` and the suites**

Add this function next to `Invoke-Checked`:
```powershell
function Assert-DemoNotRunning {
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $listening = $false
        try { $listening = $client.ConnectAsync('127.0.0.1', 5264).Wait(500) } catch [System.AggregateException] { }
        if ($listening) {
            throw 'A demo is already running on http://127.0.0.1:5264 - stop it (Ctrl+C in its window) first.'
        }
    } finally { $client.Dispose() }
}
```

Add to `$Commands`, after the `'start'` entry:
```powershell
    'demo' = @{
        Summary = 'Refresh the demo database with mock data and run the app on it (127.0.0.1:5264)'
        Detail  = @'
demo [start|refresh]

start (the default) rebuilds noof_ledger_demo at this branch's schema, fills it with the fixed mock
data from tools\Noof.Ledger.Demo, then runs the real host on it at http://127.0.0.1:5264 until
Ctrl+C. Sign in as demo / demo. The real app keeps 5263, so both can run at once.

refresh only rebuilds and refills the database.

The demo never touches noof_ledger: it has its own database, logs and key ring
(%LOCALAPPDATA%\NoofLedger\demo), and backups are off. Refuses while a demo is already running.

Prerequisites: PostgreSQL reachable; a .NET 10 SDK.
'@
    }
```

Add to the command `switch`, after the `'start'` branch:
```powershell
    'demo' {
        $action = if ($Rest.Count -ge 1) { $Rest[0] } else { 'start' }
        if ($action -notin @('start', 'refresh')) { throw "Unknown demo action '$action'. Use start or refresh." }
        Assert-DemoNotRunning
        Invoke-Checked { dotnet run --project (Join-Path $Root 'tools\Noof.Ledger.Demo') -c Release -- $action }
    }
```

In `'test'`: add `'tests\Noof.Ledger.Demo.Tests\Noof.Ledger.Demo.Tests.csproj'` to `$fastProjects`;
change the `fast` loop's base filter line to
```powershell
                    $baseFilter = if ($project -like '*Noof.Ledger.Host.Tests*' -or $project -like '*Noof.Ledger.Demo.Tests*') { $hostTestsDatabaseExclusion } else { $null }
```
and in `'db'`, after the Host.Tests line, add
```powershell
                    $demoTests = Join-Path $Root 'tests\Noof.Ledger.Demo.Tests\Noof.Ledger.Demo.Tests.csproj'
                    Invoke-Checked { dotnet test --project $demoTests --filter 'Category=Database' }
```
Update the `'test'` entry's Detail text: `fast` also runs Demo.Tests except its Database-tagged
classes, and `db` runs those classes (RefreshTests, MockLedgerTests) too.

`ops/clean-test-databases.ps1` line 24:
```powershell
$Protected = @('noof_ledger', 'noof_ledger_test_template', 'noof_ledger_demo', 'postgres', 'template0', 'template1')
```

`tests/Noof.Ledger.Architecture.Tests/RunScriptTests.cs` — `CommandNames`:
```csharp
    static readonly string[] CommandNames =
    [
        "start", "demo", "publish", "start-published", "set-password", "test", "update-test-template",
        "clean-test-dbs", "restore-check", "db-auth-reset", "pg", "status", "logs", "backups", "inspect",
    ];
```

- [ ] **Step 7: Run the Architecture tests**

Run: `dotnet test --project tests\Noof.Ledger.Architecture.Tests\Noof.Ledger.Architecture.Tests.csproj`
Expected: PASS (`Help_lists_every_command` and `Help_for_each_command_prints_non_empty_detail(demo)` included).

- [ ] **Step 8: Try it for real**

Run `.\run.ps1 demo` in the background (it runs until stopped). Then:
`curl -s http://127.0.0.1:5264/account/login | findstr /C:"name=\"username\""` → prints the username input.
Run `.\run.ps1 demo` a second time → it refuses with "A demo is already running on http://127.0.0.1:5264".
Stop the first one (kill its process tree), confirm `%LOCALAPPDATA%\NoofLedger\demo\logs` holds a
log file and `%LOCALAPPDATA%\NoofLedger\logs` got no new file from this run.

- [ ] **Step 9: Commit**

```bash
git add tools/Noof.Ledger.Demo tests/Noof.Ledger.Demo.Tests run.ps1 ops/clean-test-databases.ps1 tests/Noof.Ledger.Architecture.Tests/RunScriptTests.cs
git commit -m "feat(demo): run the app on the demo database with .\run.ps1 demo"
```

---

### Task 5: Telegram pictures — the chat page and its scenes

**Files:**
- Create: `tools/Noof.Ledger.Demo/Shots/TelegramChatPage.cs`
- Create: `tools/Noof.Ledger.Demo/Shots/TelegramScenes.cs`
- Create: `tests/Noof.Ledger.Demo.Tests/TelegramChatPageTests.cs`
- Create: `tests/Noof.Ledger.Demo.Tests/TelegramScenesTests.cs`

**Interfaces:**
- Consumes: `MockData.TelegramChatId`, `MockData.Now` (Task 2); `IRecordEcho` (`AddNoofApplication`); `RecordActionButtons.ToButton(RecordAction) : InlineKeyboardButton` and `HealthReplyFormatter.Format(SystemHealthReport) : string` (Telegram, internal).
- Produces: `ChatSide`, `ChatBubble(ChatSide Side, string Text, string Time, bool Edited = false, IReadOnlyList<string>? Buttons = null, string? Quote = null)`, `ChatScene(string Name, string Title, IReadOnlyList<ChatBubble> Bubbles)`, `TelegramChatPage.Render(ChatScene) : string`, `TelegramScenes.Build(IRecordEcho echo) : IReadOnlyList<ChatScene>`, `TelegramScenes.CreateEcho() : IRecordEcho`.

- [ ] **Step 1: Write the failing tests**

`tests/Noof.Ledger.Demo.Tests/TelegramChatPageTests.cs`:
```csharp
using AwesomeAssertions;
using Noof.Ledger.Demo.Shots;

namespace Noof.Ledger.Demo.Tests;

public sealed class TelegramChatPageTests
{
    [Fact]
    public void A_bot_reply_shows_its_text_its_edited_mark_and_its_buttons()
    {
        var html = TelegramChatPage.Render(new ChatScene("expense", "Expense",
        [
            new ChatBubble(ChatSide.Operator, "coffee 350 rsd", "18:30"),
            new ChatBubble(ChatSide.Bot, "Recorded — Raiffeisen\n• Coffee", "18:30", Edited: true, Buttons: ["Cancel", "Edit"]),
        ]));

        html.Should().Contain("coffee 350 rsd");
        html.Should().Contain("Recorded — Raiffeisen\n• Coffee");
        html.Should().Contain("edited 18:30");
        html.Should().Contain(">Cancel<").And.Contain(">Edit<");
        html.Should().Contain("class=\"row out\"").And.Contain("class=\"row in\"");
    }

    [Fact]
    public void Text_is_html_encoded()
    {
        var html = TelegramChatPage.Render(new ChatScene("x", "X", [new ChatBubble(ChatSide.Operator, "<b>#@%&</b>", "09:00")]));

        html.Should().Contain("&lt;b&gt;#@%&amp;&lt;/b&gt;");
        html.Should().NotContain("<b>#@%&</b>");
    }

    [Fact]
    public void A_reply_quotes_only_the_first_line_of_what_it_answers()
    {
        var html = TelegramChatPage.Render(new ChatScene("x", "X",
            [new ChatBubble(ChatSide.Operator, "no, 42", "09:00", Quote: "What should I fix?\nsecond line")]));

        html.Should().Contain("<div class=\"quote\">What should I fix?</div>");
        html.Should().NotContain("second line");
    }
}
```

`tests/Noof.Ledger.Demo.Tests/TelegramScenesTests.cs`:
```csharp
using AwesomeAssertions;
using Noof.Ledger.Demo.Shots;

namespace Noof.Ledger.Demo.Tests;

public sealed class TelegramScenesTests
{
    static readonly IReadOnlyList<ChatScene> Scenes = TelegramScenes.Build(TelegramScenes.CreateEcho());

    [Fact]
    public void Every_scene_the_spec_lists_is_built()
    {
        Scenes.Select(scene => scene.Name).Should().Equal(
            "expense", "receipt", "income", "balance", "cancel-restore", "correction", "failure", "health");
    }

    [Theory]
    [InlineData("expense", "Recorded — Raiffeisen", new[] { "Cancel", "Edit" })]
    [InlineData("income", "Income — Wise", new[] { "Cancel", "Edit" })]
    [InlineData("cancel-restore", "Cancelled — Raiffeisen", new[] { "Restore" })]
    [InlineData("failure", "Could not read that message.", new[] { "Edit" })]
    public void The_bot_bubble_carries_the_apps_own_text_and_buttons(string scene, string start, string[] buttons)
    {
        var reply = Scenes.Single(candidate => candidate.Name == scene).Bubbles.Last(bubble => bubble.Side == ChatSide.Bot);

        reply.Text.Should().StartWith(start);
        reply.Buttons.Should().Equal(buttons);
    }

    [Fact]
    public void The_receipt_names_its_merchant_and_the_balance_statement_says_it_adjusted()
    {
        Scenes.Single(scene => scene.Name == "receipt").Bubbles[^1].Text.Should().Contain("Lidl");
        Scenes.Single(scene => scene.Name == "balance").Bubbles[^1].Text.Should().Contain("adjusted");
    }

    [Fact]
    public void The_health_reply_is_the_bots_own_format()
    {
        Scenes.Single(scene => scene.Name == "health").Bubbles[^1].Text.Should().StartWith("Health: all good");
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test --project tests\Noof.Ledger.Demo.Tests\Noof.Ledger.Demo.Tests.csproj --filter "Category!=Database"`
Expected: build FAILS — `Noof.Ledger.Demo.Shots` does not exist.

- [ ] **Step 3: Implement the chat page**

`tools/Noof.Ledger.Demo/Shots/TelegramChatPage.cs`:
```csharp
using System.Net;
using System.Text;

namespace Noof.Ledger.Demo.Shots;

internal enum ChatSide
{
    Operator,
    Bot,
}

internal sealed record ChatBubble(
    ChatSide Side, string Text, string Time, bool Edited = false, IReadOnlyList<string>? Buttons = null, string? Quote = null);

internal sealed record ChatScene(string Name, string Title, IReadOnlyList<ChatBubble> Bubbles);

internal static class TelegramChatPage
{
    public static string Render(ChatScene scene)
    {
        var html = new StringBuilder();
        html.Append($"""<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>{Encode(scene.Title)}</title><style>{Css}</style></head><body>""");
        html.Append("""<header><div class="avatar">N</div><div><div class="name">Noof Ledger</div><div class="status">bot</div></div></header><main>""");
        foreach (var bubble in scene.Bubbles)
            AppendBubble(html, bubble);
        html.Append("</main></body></html>");
        return html.ToString();
    }

    static void AppendBubble(StringBuilder html, ChatBubble bubble)
    {
        html.Append($"""<div class="row {(bubble.Side == ChatSide.Operator ? "out" : "in")}"><div class="stack"><div class="bubble">""");
        if (bubble.Quote is { } quote)
            html.Append($"""<div class="quote">{Encode(quote.Split('\n')[0])}</div>""");

        html.Append($"""<div class="text">{Encode(bubble.Text)}</div><div class="meta">{(bubble.Edited ? "edited " : "")}{Encode(bubble.Time)}</div></div>""");
        if (bubble.Buttons is { Count: > 0 } buttons)
            html.Append($"""<div class="keyboard">{string.Concat(buttons.Select(Button))}</div>""");

        html.Append("</div></div>");
    }

    static string Button(string label) => $"""<span class="button">{Encode(label)}</span>""";

    static string Encode(string text) => WebUtility.HtmlEncode(text);

    const string Css = """
        * { box-sizing: border-box; margin: 0; }
        body { font-family: "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif; font-size: 15px; background: #c9d8b5; color: #111; min-height: 100vh; }
        header { display: flex; align-items: center; gap: 10px; padding: 10px 14px; background: #fff; border-bottom: 1px solid #d9d9d9; }
        .avatar { width: 38px; height: 38px; border-radius: 50%; background: #5caff0; color: #fff; display: flex; align-items: center; justify-content: center; font-weight: 600; }
        .name { font-weight: 600; }
        .status { font-size: 13px; color: #8a8a8a; }
        main { padding: 12px 10px 18px; display: flex; flex-direction: column; gap: 8px; }
        .row { display: flex; }
        .row.out { justify-content: flex-end; }
        .stack { max-width: 84%; display: flex; flex-direction: column; gap: 4px; }
        .bubble { padding: 6px 10px 5px; border-radius: 14px; box-shadow: 0 1px 1px rgba(0, 0, 0, .12); }
        .in .bubble { background: #fff; border-bottom-left-radius: 4px; }
        .out .bubble { background: #e1fec6; border-bottom-right-radius: 4px; }
        .text { white-space: pre-wrap; overflow-wrap: anywhere; line-height: 1.35; }
        .meta { font-size: 12px; color: #8a9aa5; text-align: right; margin-top: 2px; }
        .out .meta { color: #5fa561; }
        .quote { border-left: 3px solid #3a95d5; padding: 1px 0 1px 7px; margin-bottom: 4px; color: #3a6d99; font-size: 13.5px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .keyboard { display: flex; gap: 4px; }
        .button { flex: 1; text-align: center; padding: 8px 6px; border-radius: 8px; background: rgba(0, 0, 0, .22); color: #fff; font-weight: 600; font-size: 14px; }
        """;
}
```
(Styling is written from scratch after Telegram's look — nothing is copied from Telegram's GPL-3.0
clients. The font list names real fonts, per the `ThemeTests` lesson.)

- [ ] **Step 4: Implement the scenes**

`tools/Noof.Ledger.Demo/Shots/TelegramScenes.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Domain;
using Noof.Ledger.Telegram;

namespace Noof.Ledger.Demo.Shots;

internal static class TelegramScenes
{
    static readonly DateOnly Day = new(2026, 9, 18);

    public static IRecordEcho CreateEcho()
    {
        using var services = new ServiceCollection().AddNoofApplication(new SlowOperationOptions()).BuildServiceProvider();
        return services.GetRequiredService<IRecordEcho>();
    }

    public static IReadOnlyList<ChatScene> Build(IRecordEcho echo)
    {
        var dinner = Expense("dinner at Walter 45 eur", "Wise", CurrencyCode.Eur, 5657.00m, [Line("Dinner", 45.00m, CurrencyCode.Eur, "Restaurants", "Walter")]);
        var dinnerFixed = dinner with
        {
            Lines = [Line("Dinner", 42.00m, CurrencyCode.Eur, "Restaurants", "Walter")],
            WalletBalances = [new Money(5660.00m, CurrencyCode.Eur)],
        };

        return
        [
            new("expense", "Expense",
            [
                Operator("coffee 350 rsd", "18:30"),
                Reply(echo.Compose(Expense("coffee 350 rsd", "Raiffeisen", CurrencyCode.Rsd, 174150.00m,
                    [Line("Coffee", 350m, CurrencyCode.Rsd, "Coffee", null)])), "18:30"),
            ]),
            new("receipt", "Receipt with a merchant",
            [
                Operator("Lidl groceries 27.80, wine 12.50 eur", "18:31"),
                Reply(echo.Compose(Expense("Lidl groceries 27.80, wine 12.50 eur", "Wise", CurrencyCode.Eur, 5659.70m,
                [
                    Line("Groceries", 27.80m, CurrencyCode.Eur, "Groceries", "Lidl"),
                    Line("Wine", 12.50m, CurrencyCode.Eur, "Groceries", "Lidl"),
                ])), "18:31"),
            ]),
            new("income", "Income",
            [
                Operator("salary 2800 eur", "09:02"),
                Reply(echo.Compose(Expense("salary 2800 eur", "Wise", CurrencyCode.Eur, 5757.20m,
                    [Line("Salary", 2800.00m, CurrencyCode.Eur, "Salary", null)]) with { Kind = TransactionKind.Income }), "09:02"),
            ]),
            new("balance", "Balance statement",
            [
                Operator("wise balance 5700", "20:10"),
                Reply(echo.Compose(Expense("wise balance 5700", "Wise", CurrencyCode.Eur, 5700.00m, []) with
                {
                    Kind = TransactionKind.BalanceCheck,
                    Statement = new BalanceStatement(new Money(5700.00m, CurrencyCode.Eur), 5720.30m),
                }), "20:10"),
            ]),
            new("cancel-restore", "Cancelled, with Restore",
            [
                Operator("taxi 850 rsd", "22:47"),
                Reply(echo.Compose(Expense("taxi 850 rsd", "Raiffeisen", CurrencyCode.Rsd, 175350.00m,
                    [Line("Taxi", 850m, CurrencyCode.Rsd, "Transport", null)]) with { Status = TransactionStatus.Cancelled }), "22:47"),
            ]),
            new("correction", "Fixing a mistake",
            [
                Operator("dinner at Walter 45 eur", "21:05"),
                Reply(echo.Compose(dinnerFixed), "21:05"),
                new(ChatSide.Bot, echo.EditPrompt, "21:06", Quote: echo.Compose(dinnerFixed).Text),
                Operator("no, 42", "21:06") with { Quote = echo.EditPrompt },
            ]),
            new("failure", "A message it could not read",
            [
                Operator("#@%& ???", "11:12"),
                Reply(echo.Failure, "11:12"),
            ]),
            new("health", "/health",
            [
                Operator("/health", "08:00"),
                new(ChatSide.Bot, HealthReplyFormatter.Format(Health), "08:00"),
            ]),
        ];
    }

    static readonly SystemHealthReport Health = new(HealthLevel.Ok,
    [
        new("Database", HealthLevel.Ok, "ready", MockData.Now, string.Empty),
        new("Telegram", HealthLevel.Ok, "polling", MockData.Now, string.Empty),
        new("AI keys", HealthLevel.Ok, "configured", MockData.Now, string.Empty),
        new("Backup", HealthLevel.Ok, "last success 2 h ago", MockData.Now, string.Empty),
    ]);

    static ChatBubble Operator(string text, string time) => new(ChatSide.Operator, text, time);

    static ChatBubble Reply(EchoMessage echo, string time) =>
        new(ChatSide.Bot, echo.Text, time, Edited: true, Buttons: [.. echo.Actions.Select(action => RecordActionButtons.ToButton(action).Text)]);

    static RecordedLine Line(string description, decimal amount, CurrencyCode currency, string category, string? merchant) =>
        new(description, new Money(amount, currency), category.ToLowerInvariant(), category, merchant);

    static CategorizationSubject Expense(string raw, string wallet, CurrencyCode currency, decimal balance, IReadOnlyList<RecordedLine> lines) =>
        new(Guid.Empty, raw, MockData.TelegramChatId, 1, wallet, TransactionStatus.Completed, Day, Day, lines,
            WalletCurrency: currency, WalletBalances: [new Money(balance, currency)]);
}
```
(`Reply` marks a reply as edited because the bot always turns its "Recording…" message into the
echo; the correction's echo already shows the corrected amount, as the edited message does in
Telegram by the time the operator looks.)

- [ ] **Step 5: Run the tests to see them pass**

Run: `dotnet test --project tests\Noof.Ledger.Demo.Tests\Noof.Ledger.Demo.Tests.csproj --filter "Category!=Database"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add tools/Noof.Ledger.Demo/Shots tests/Noof.Ledger.Demo.Tests
git commit -m "feat(demo): draw the bot's real replies as Telegram-style chat pages"
```

---

### Task 6: `.\run.ps1 screenshots` — capture, write only what changed, gallery

**Files:**
- Create: `tools/Noof.Ledger.Demo/Shots/ShotFiles.cs`
- Create: `tools/Noof.Ledger.Demo/Shots/ShotWriter.cs`
- Create: `tools/Noof.Ledger.Demo/Shots/AppScreens.cs`
- Create: `tools/Noof.Ledger.Demo/Shots/AppShots.cs`
- Create: `tools/Noof.Ledger.Demo/Shots/TelegramShots.cs`
- Create: `tools/Noof.Ledger.Demo/Shots/Gallery.cs`
- Create: `tools/Noof.Ledger.Demo/Shots/ShotsCommand.cs`
- Modify: `tools/Noof.Ledger.Demo/DemoEntryPoint.cs`
- Create: `tests/Noof.Ledger.Demo.Tests/ShotFilesTests.cs`
- Create: `tests/Noof.Ledger.Demo.Tests/GalleryTests.cs`
- Modify: `run.ps1`, `tests/Noof.Ledger.Architecture.Tests/RunScriptTests.cs`
- Create (generated): `docs/screenshots/**`

**Interfaces:**
- Consumes: `DemoCommands.RefreshDemoAsync`, `DemoHost.StartAsync`, `DemoHost.BaseUrl`, `RepoPaths` (Task 4); `MockData.TracedTransactionId`, `MockData.FailedTransactionId`, `MockData.LogWindowStart/End`, `MockData.Username/Password` (Tasks 2–3); `TelegramScenes`, `TelegramChatPage`, `ChatScene` (Task 5).
- Produces: `ShotFiles.WriteIfChanged(string path, byte[] content) : bool`, `ShotFiles.Slices(int pageHeight, int sliceHeight) : IReadOnlyList<(int Y, int Height)>`, `Viewport(string Name, int Width, int Height, float Scale)`, `AppScreen`, `AppScreens.All`, `Gallery.Render(IReadOnlyList<AppScreen>, IReadOnlyList<ChatScene>) : string`.

- [ ] **Step 1: Write the failing tests**

`tests/Noof.Ledger.Demo.Tests/ShotFilesTests.cs`:
```csharp
using AwesomeAssertions;
using Noof.Ledger.Demo.Shots;

namespace Noof.Ledger.Demo.Tests;

public sealed class ShotFilesTests
{
    [Fact]
    public void An_identical_image_is_not_rewritten()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("noof-shots-").FullName, "app", "dashboard-phone.png");

        ShotFiles.WriteIfChanged(file, [1, 2, 3]).Should().BeTrue();
        var written = File.GetLastWriteTimeUtc(file);

        ShotFiles.WriteIfChanged(file, [1, 2, 3]).Should().BeFalse();
        File.GetLastWriteTimeUtc(file).Should().Be(written);
    }

    [Fact]
    public void A_changed_image_is_rewritten()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("noof-shots-").FullName, "a.png");
        ShotFiles.WriteIfChanged(file, [1, 2, 3]);

        ShotFiles.WriteIfChanged(file, [1, 2, 4]).Should().BeTrue();
        File.ReadAllBytes(file).Should().Equal(1, 2, 4);
    }

    [Theory]
    [InlineData(1500, 2000, new[] { 0 }, new[] { 1500 })]
    [InlineData(2000, 2000, new[] { 0 }, new[] { 2000 })]
    [InlineData(4500, 2000, new[] { 0, 2000, 4000 }, new[] { 2000, 2000, 500 })]
    public void A_tall_page_is_cut_into_slices_no_taller_than_the_limit(int height, int slice, int[] ys, int[] heights)
    {
        var slices = ShotFiles.Slices(height, slice);

        slices.Select(part => part.Y).Should().Equal(ys);
        slices.Select(part => part.Height).Should().Equal(heights);
    }
}
```

`tests/Noof.Ledger.Demo.Tests/GalleryTests.cs`:
```csharp
using AwesomeAssertions;
using Noof.Ledger.Demo.Shots;

namespace Noof.Ledger.Demo.Tests;

public sealed class GalleryTests
{
    [Fact]
    public void The_gallery_shows_every_screen_at_both_sizes_and_every_chat_scene()
    {
        var scenes = TelegramScenes.Build(TelegramScenes.CreateEcho());

        var markdown = Gallery.Render(AppScreens.All, scenes);

        foreach (var screen in AppScreens.All)
        {
            markdown.Should().Contain($"app/{screen.Name}-desktop.png");
            markdown.Should().Contain($"app/{screen.Name}-phone.png");
        }

        foreach (var scene in scenes)
            markdown.Should().Contain($"telegram/{scene.Name}.png");

        markdown.Should().NotContain("\r");
    }

    [Fact]
    public void Every_page_of_the_app_has_a_screen()
    {
        AppScreens.All.Select(screen => screen.Name).Should().Equal(
            "login", "dashboard", "wallets", "transactions", "trace", "trace-failed",
            "diagnostics", "logs", "log-settings", "secrets");
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test --project tests\Noof.Ledger.Demo.Tests\Noof.Ledger.Demo.Tests.csproj --filter "Category!=Database"`
Expected: build FAILS — `ShotFiles`, `Gallery`, `AppScreens` do not exist.

- [ ] **Step 3: Implement `ShotFiles`, `Gallery` and `AppScreens`**

`tools/Noof.Ledger.Demo/Shots/ShotFiles.cs`:
```csharp
namespace Noof.Ledger.Demo.Shots;

internal static class ShotFiles
{
    public static bool WriteIfChanged(string path, byte[] content)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return true;
    }

    public static IReadOnlyList<(int Y, int Height)> Slices(int pageHeight, int sliceHeight) =>
        [.. Enumerable.Range(0, (pageHeight + sliceHeight - 1) / sliceHeight)
            .Select(index => (index * sliceHeight, Math.Min(sliceHeight, pageHeight - index * sliceHeight)))];
}
```

`tools/Noof.Ledger.Demo/Shots/AppScreens.cs`:
```csharp
using Microsoft.Playwright;

namespace Noof.Ledger.Demo.Shots;

internal sealed record Viewport(string Name, int Width, int Height, float Scale);

internal sealed record AppScreen(
    string Name, string Title, string Path, string ReadyMarker, bool SignedIn = true, Func<IPage, Task>? Prepare = null);

internal static class AppScreens
{
    public static readonly Viewport Phone = new("phone", 390, 844, 2);
    public static readonly Viewport Desktop = new("desktop", 1440, 900, 1);

    // The one value on these pages that follows the host's real clock: each health check's "Checked at".
    public const string HideLiveValues = "#diagnostics-checks tbody td:nth-child(4) { visibility: hidden; }";

    public static IReadOnlyList<AppScreen> All { get; } =
    [
        new("login", "Sign in", "/", "input[name='username']", SignedIn: false),
        new("dashboard", "Dashboard", "/", "#balances"),
        new("wallets", "Wallets", "/wallets", "#create-wallet", Prepare: page => page.FillAsync("#new-wallet-date", "2026-09-20")),
        new("transactions", "Transactions", "/transactions", "#transactions-grid"),
        new("trace", "Transaction trace", $"/transactions/{MockData.TracedTransactionId}/trace", "#trace-timeline"),
        new("trace-failed", "A failed transaction's trace", $"/transactions/{MockData.FailedTransactionId}/trace", "#trace-timeline"),
        new("diagnostics", "Diagnostics", "/diagnostics", "#diagnostics-checks"),
        new("logs", "Logs", "/diagnostics/logs", "#logs-grid", Prepare: FilterLogsToTheMockMonthAsync),
        new("log-settings", "Log settings", "/diagnostics/logs/settings", "#retention-verbose"),
        new("secrets", "Secrets", "/settings/secrets", "#status-anthropic-api-key"),
    ];

    static async Task FilterLogsToTheMockMonthAsync(IPage page)
    {
        await page.FillAsync("#logs-filter-from", $"{MockData.LogWindowStart:yyyy-MM-dd}T00:00");
        await page.FillAsync("#logs-filter-to", $"{MockData.LogWindowEnd:yyyy-MM-dd}T00:00");
    }
}
```

`tools/Noof.Ledger.Demo/Shots/Gallery.cs`:
```csharp
using System.Text;

namespace Noof.Ledger.Demo.Shots;

internal static class Gallery
{
    public static string Render(IReadOnlyList<AppScreen> screens, IReadOnlyList<ChatScene> scenes)
    {
        var markdown = new StringBuilder();
        Line(markdown, "# Screenshots");
        Line(markdown, "");
        Line(markdown, "Generated by `.\\run.ps1 screenshots` from the demo database's mock data (`tools/Noof.Ledger.Demo`).");
        Line(markdown, "Do not edit by hand; a change to what the app shows regenerates them.");
        Line(markdown, "");
        Line(markdown, "## App");

        foreach (var screen in screens)
        {
            Line(markdown, "");
            Line(markdown, $"### {screen.Title}");
            Line(markdown, "");
            Line(markdown, $"""<img src="app/{screen.Name}-desktop.png" width="640" alt="{screen.Title}, desktop"> <img src="app/{screen.Name}-phone.png" width="200" alt="{screen.Title}, phone">""");
        }

        Line(markdown, "");
        Line(markdown, "## Telegram");

        foreach (var scene in scenes)
        {
            Line(markdown, "");
            Line(markdown, $"### {scene.Title}");
            Line(markdown, "");
            Line(markdown, $"""<img src="telegram/{scene.Name}.png" width="300" alt="{scene.Title}">""");
        }

        return markdown.ToString();
    }

    static void Line(StringBuilder markdown, string text) => markdown.Append(text).Append('\n');
}
```

- [ ] **Step 4: Run the unit tests to see them pass**

Run: `dotnet test --project tests\Noof.Ledger.Demo.Tests\Noof.Ledger.Demo.Tests.csproj --filter "Category!=Database"`
Expected: PASS.

- [ ] **Step 5: Implement capture**

`tools/Noof.Ledger.Demo/Shots/ShotWriter.cs`:
```csharp
using Microsoft.Playwright;

namespace Noof.Ledger.Demo.Shots;

internal sealed class ShotWriter(string screenshotsRoot, string sendRoot)
{
    const int MaxSliceDevicePixels = 4000;
    readonly List<string> changed = [];

    public IReadOnlyList<string> Changed => changed;

    public async Task SaveAsync(IPage page, string relativePath, Viewport viewport, string? hideCss)
    {
        var png = await page.ScreenshotAsync(Options(hideCss));
        if (!ShotFiles.WriteIfChanged(Path.Combine(screenshotsRoot, relativePath), png))
            return;

        changed.Add(relativePath);
        await SaveSendReadyAsync(page, relativePath, viewport, hideCss);
    }

    async Task SaveSendReadyAsync(IPage page, string relativePath, Viewport viewport, string? hideCss)
    {
        var height = await page.EvaluateAsync<int>("() => document.documentElement.scrollHeight");
        var slices = ShotFiles.Slices(height, (int)(MaxSliceDevicePixels / viewport.Scale));
        var stem = Path.ChangeExtension(relativePath, null).Replace('/', '-');

        for (var index = 0; index < slices.Count; index++)
        {
            var options = Options(hideCss);
            options.Clip = new Clip { X = 0, Y = slices[index].Y, Width = viewport.Width, Height = slices[index].Height };
            options.Type = ScreenshotType.Jpeg;
            options.Quality = 88;
            options.Path = Path.Combine(sendRoot, slices.Count == 1 ? $"{stem}.jpg" : $"{stem}-part{index + 1}.jpg");
            await page.ScreenshotAsync(options);
        }
    }

    static PageScreenshotOptions Options(string? hideCss) => new()
    {
        FullPage = true,
        Animations = ScreenshotAnimations.Disabled,
        Caret = ScreenshotCaret.Hide,
        Style = hideCss,
    };
}
```

`tools/Noof.Ledger.Demo/Shots/AppShots.cs`:
```csharp
using Microsoft.Playwright;

namespace Noof.Ledger.Demo.Shots;

internal static class AppShots
{
    public static async Task CaptureAsync(IBrowser browser, ShotWriter writer)
    {
        foreach (var viewport in (Viewport[])[AppScreens.Phone, AppScreens.Desktop])
        {
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = viewport.Width, Height = viewport.Height },
                DeviceScaleFactor = viewport.Scale,
            });
            var page = await context.NewPageAsync();

            foreach (var screen in AppScreens.All.Where(screen => !screen.SignedIn))
                await ShootAsync(page, screen, viewport, writer);

            await SignInAsync(page);

            foreach (var screen in AppScreens.All.Where(screen => screen.SignedIn))
                await ShootAsync(page, screen, viewport, writer);
        }
    }

    static async Task ShootAsync(IPage page, AppScreen screen, Viewport viewport, ShotWriter writer)
    {
        await page.GotoAsync(DemoHost.BaseUrl + screen.Path);
        await page.Locator(screen.ReadyMarker).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        if (screen.Prepare is { } prepare)
        {
            await prepare(page);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }

        await page.WaitForTimeoutAsync(500);
        await writer.SaveAsync(page, $"app/{screen.Name}-{viewport.Name}.png", viewport, AppScreens.HideLiveValues);
    }

    static async Task SignInAsync(IPage page)
    {
        await page.GotoAsync(DemoHost.BaseUrl + "/");
        await page.WaitForURLAsync("**/account/login*");
        await page.FillAsync("input[name='username']", MockData.Username);
        await page.FillAsync("input[name='password']", MockData.Password);
        await page.ClickAsync("button[type='submit']");
        await page.WaitForURLAsync(DemoHost.BaseUrl + "/");
    }
}
```

`tools/Noof.Ledger.Demo/Shots/TelegramShots.cs`:
```csharp
using Microsoft.Playwright;

namespace Noof.Ledger.Demo.Shots;

internal static class TelegramShots
{
    public static async Task CaptureAsync(IBrowser browser, ShotWriter writer, IReadOnlyList<ChatScene> scenes)
    {
        var phone = AppScreens.Phone;
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = phone.Width, Height = phone.Height },
            DeviceScaleFactor = phone.Scale,
        });
        var page = await context.NewPageAsync();

        foreach (var scene in scenes)
        {
            await page.SetContentAsync(TelegramChatPage.Render(scene));
            await writer.SaveAsync(page, $"telegram/{scene.Name}.png", phone, hideCss: null);
        }
    }
}
```

`tools/Noof.Ledger.Demo/Shots/ShotsCommand.cs`:
```csharp
using System.Text;
using Microsoft.Playwright;

namespace Noof.Ledger.Demo.Shots;

internal static class ShotsCommand
{
    public static async Task<int> RunAsync()
    {
        var paths = DemoPaths.ForOperator();
        var connectionString = await DemoCommands.RefreshDemoAsync(paths, CancellationToken.None);
        await using var host = await DemoHost.StartAsync(connectionString, paths, CancellationToken.None);

        if (Directory.Exists(RepoPaths.SendReady))
            Directory.Delete(RepoPaths.SendReady, recursive: true);
        Directory.CreateDirectory(RepoPaths.SendReady);

        var writer = new ShotWriter(RepoPaths.Screenshots, RepoPaths.SendReady);
        var scenes = TelegramScenes.Build(TelegramScenes.CreateEcho());

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchChromiumAsync(playwright);
        await AppShots.CaptureAsync(browser, writer);
        await TelegramShots.CaptureAsync(browser, writer, scenes);

        var galleryChanged = ShotFiles.WriteIfChanged(
            Path.Combine(RepoPaths.Screenshots, "README.md"), Encoding.UTF8.GetBytes(Gallery.Render(AppScreens.All, scenes)));

        Report(writer.Changed, galleryChanged);
        return 0;
    }

    static async Task<IBrowser> LaunchChromiumAsync(IPlaywright playwright)
    {
        try
        {
            return await playwright.Chromium.LaunchAsync();
        }
        catch (PlaywrightException missing) when (missing.Message.Contains("Executable doesn't exist", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Chromium for Playwright is not installed. Run: pwsh {Path.Combine(AppContext.BaseDirectory, "playwright.ps1")} install chromium");
        }
    }

    static void Report(IReadOnlyList<string> changed, bool galleryChanged)
    {
        if (changed.Count == 0 && !galleryChanged)
        {
            Console.WriteLine("No screenshot changed.");
            return;
        }

        Console.WriteLine($"{changed.Count} screenshot(s) changed in docs\\screenshots:");
        foreach (var path in changed)
            Console.WriteLine($"  {path}");

        if (galleryChanged)
            Console.WriteLine("  README.md (gallery)");

        Console.WriteLine($"Phone-sized copies to send: {RepoPaths.SendReady}");
    }
}
```

In `DemoEntryPoint.Main`'s switch, add `["shots"] => await Shots.ShotsCommand.RunAsync(),`.

- [ ] **Step 6: Add `screenshots` to `run.ps1`**

`$Commands`, after `'demo'`:
```powershell
    'screenshots' = @{
        Summary = 'Refresh the demo, photograph every page and the bot replies into docs\screenshots'
        Detail  = @'
screenshots

Refreshes the demo database, starts the host on it, and writes phone and desktop screenshots of
every page, plus Telegram-style pictures of the bot's real replies, to docs\screenshots (with its
README.md gallery). Only images whose pixels changed are rewritten, so git shows exactly what a
change did. The changed ones are listed, and phone-sized JPEG copies of them land in
artifacts\screenshots for sending. Refuses while a demo is running.

Prerequisites: PostgreSQL reachable; the Chromium install the E2E tests use.
'@
    }
```
The switch branch, after `'demo'`:
```powershell
    'screenshots' {
        Assert-DemoNotRunning
        Invoke-Checked { dotnet run --project (Join-Path $Root 'tools\Noof.Ledger.Demo') -c Release -- shots }
    }
```
Add `"screenshots"` after `"demo"` in `RunScriptTests.CommandNames`.

- [ ] **Step 7: Run the fast tests**

Run: `dotnet test --project tests\Noof.Ledger.Demo.Tests\Noof.Ledger.Demo.Tests.csproj --filter "Category!=Database"` and
`dotnet test --project tests\Noof.Ledger.Architecture.Tests\Noof.Ledger.Architecture.Tests.csproj`
Expected: PASS.

- [ ] **Step 8: Generate the screenshots and look at them**

Run: `.\run.ps1 screenshots` (a few minutes: build, refresh, host start, 28 pictures).
Expected: "28 screenshot(s) changed" (20 app + 8 Telegram) plus the gallery. Open at least
`docs/screenshots/app/dashboard-desktop.png`, `app/transactions-phone.png`, `app/trace-desktop.png`,
`telegram/correction.png` and `telegram/health.png` (the Read tool shows images): the dashboard shows
six wallets with the balances from `MockLedgerTests`, the trace shows four stages and two revisions,
the chat pictures show bubbles and buttons. A blank or waiting-banner image is a failure — fix the
ready marker or the wait, never accept it.

- [ ] **Step 9: Prove the pictures are stable**

Run `.\run.ps1 screenshots` again. Expected: "No screenshot changed." and
`git status --short docs/screenshots` prints nothing new since Step 8. If a screen changed, compare the
two images, find the live value (a time, a duration), and hide it by extending
`AppScreens.HideLiveValues` with its selector (or pin it in that screen's `Prepare`) — then repeat
Steps 8–9.

- [ ] **Step 10: Commit**

```bash
git add tools/Noof.Ledger.Demo tests/Noof.Ledger.Demo.Tests run.ps1 tests/Noof.Ledger.Architecture.Tests/RunScriptTests.cs docs/screenshots
git commit -m "feat(demo): .\run.ps1 screenshots writes the app and bot pictures to docs/screenshots"
```

---

### Task 7: The rule, the README and the rest of the docs

**Files:**
- Modify: `CLAUDE.md` (§5), `README.md`, `docs/CLOSING-A-PHASE.md`, `ops/RUNBOOK.md`, `docs/BACKLOG.md`, `docs/OPEN-QUESTIONS.md`, `docs/STATUS.md`

- [ ] **Step 1: The rule — `CLAUDE.md` §5 Conventions, a new last bullet**

```markdown
- **Screenshots stay current** *(settled 2026-09-26)*. A change to anything the operator sees — a
  page, or a message the bot sends — adds mock data for anything new (`tools/Noof.Ledger.Demo`), runs
  `.\run.ps1 screenshots`, commits the changed images in `docs/screenshots/` with the change, and sends
  them to the operator (the command lists them; phone-sized copies are in `artifacts/screenshots/`).
  A new page or bot reply gets a screen or a scene added. Never from `noof_ledger`.
```

- [ ] **Step 2: `README.md` — a Screenshots section before `## Running it`, and one line in it**

```markdown
## Screenshots

Taken from the demo database's mock data by `.\run.ps1 screenshots`; every image is in
[docs/screenshots](docs/screenshots/README.md), and git history shows how each screen changed.

<img src="docs/screenshots/app/dashboard-desktop.png" width="640" alt="Dashboard, desktop"> <img src="docs/screenshots/app/dashboard-phone.png" width="200" alt="Dashboard, phone">

<img src="docs/screenshots/app/transactions-desktop.png" width="640" alt="Transactions, desktop">

<img src="docs/screenshots/telegram/receipt.png" width="260" alt="A receipt in Telegram"> <img src="docs/screenshots/telegram/correction.png" width="260" alt="Fixing a mistake in Telegram">
```
In `## Running it`'s code block, add after the `start` line:
```powershell
.\run.ps1 demo                      # the app on mock data at 127.0.0.1:5264 (demo / demo), nothing to configure
```

- [ ] **Step 3: `docs/CLOSING-A-PHASE.md` — a new bullet before "Leave nothing uncommitted"**

```markdown
- **The screenshots are current.** Run `.\run.ps1 screenshots`; if anything changed, commit it and send
  the changed images to the operator.
```

- [ ] **Step 4: `ops/RUNBOOK.md` — a new section after `## run.ps1 — the one entry point`**

```markdown
## Demo database and screenshots

- `.\run.ps1 demo` rebuilds `noof_ledger_demo` from this branch's migrations, fills it with the fixed
  mock data in `tools/Noof.Ledger.Demo/MockData.cs`, and runs the real host on it at
  http://127.0.0.1:5264 until Ctrl+C. Sign in as `demo` / `demo`. `.\run.ps1 demo refresh` only
  rebuilds the database.
- `.\run.ps1 screenshots` does the same refresh, starts the host, and writes `docs/screenshots/`
  (app pages at phone and desktop size, the bot's replies as Telegram-style pictures, and a gallery
  `README.md`). Unchanged images are not rewritten; the changed ones are listed and copied as
  phone-sized JPEG slices into `artifacts/screenshots/`.
- The demo's files: `%LOCALAPPDATA%\NoofLedger\demo\logs` and `...\demo\dp-keys` (its own key ring).
  Backups are off; a mock "backup succeeded" row keeps the Backups check green. The Telegram token is
  left empty on purpose — a fake one would make the host call Telegram every 5 seconds — so
  Diagnostics shows Telegram as not configured. The AI keys are fake and never used: the mock data
  queues nothing for the model.
- Only one demo at a time: both commands refuse while something listens on 5264.
- Mock data is fixed (September 2026, clock pinned at 2026-09-20 18:00 UTC) so the pictures are
  stable. A feature that adds something visible adds mock data for it, a screen in
  `Shots/AppScreens.cs` or a scene in `Shots/TelegramScenes.cs`, and then regenerates the pictures.
- Chromium missing: the command prints the `playwright.ps1 install chromium` line to run.
```

- [ ] **Step 5: `docs/BACKLOG.md` — a new entry at the end**

```markdown
## Demo: what was cut from the first design — 2026-09-26

The operator cut the demo back to a database with mock data and screenshots
(`docs/superpowers/specs/2026-09-26-demo-database-and-screenshots-design.md`). Not built, so nobody
re-proposes it as new: a live fake Telegram chat driving the real bot through a fake Bot API; a
rule-based fake model so the demo could categorise messages; seeding by replaying a conversation
through the app instead of writing mock rows; labelled before/after screenshot runs (git history does
that now); pixel-diff regression tests (`Verify.Playwright`). Voice notes and receipt photos have no
pictures yet — receipts once Phase 6 lands.
```

- [ ] **Step 6: `docs/OPEN-QUESTIONS.md` — record the decisions**

Add a section in the file's existing style for decided items:
```markdown
## Demo database and screenshots — decided 2026-09-26

- S-1: a long-lived `noof_ledger_demo` refreshed with fixed mock data on every run, and screenshots
  taken from it.
- S-2: mocks are for pictures, not behaviour — fake AI keys and a fake backup row; bot replies drawn
  from the app's real reply text. No working fake Telegram, no fake model.
- S-3: screenshots are committed under `docs/screenshots/` and shown in `README.md`; git history is
  the before/after.
- S-4: a change to anything the operator sees regenerates them, commits the changed images with the
  change and sends them to the operator (`CLAUDE.md` §5).
```

- [ ] **Step 7: `docs/STATUS.md` — one sentence at the end of the status paragraph**

```markdown
> `.\run.ps1 demo` runs the app on a mock-data database with nothing to configure, and
> `.\run.ps1 screenshots` keeps `docs/screenshots/` current.
```

- [ ] **Step 8: Final test runs**

Run: `.\run.ps1 test fast` — Expected: PASS (Demo.Tests' unit tests included).
Run: `dotnet test --project tests\Noof.Ledger.Demo.Tests\Noof.Ledger.Demo.Tests.csproj --filter "Category=Database"` — Expected: PASS, 4 tests.
Run: `dotnet build NoofLedger.slnx -c Release` — Expected: 0 warnings, 0 errors.

- [ ] **Step 9: Commit**

```bash
git add CLAUDE.md README.md docs/CLOSING-A-PHASE.md ops/RUNBOOK.md docs/BACKLOG.md docs/OPEN-QUESTIONS.md docs/STATUS.md
git commit -m "docs: the demo database, the screenshots and the rule that keeps them current"
```

- [ ] **Step 10: Send the pictures to the operator**

Send every file in `artifacts/screenshots/` from the Task 6 Step 8 run through `SendUserFile`
(phone copies, sliced where tall), app pages first, then the Telegram pictures, each named in the
message.
