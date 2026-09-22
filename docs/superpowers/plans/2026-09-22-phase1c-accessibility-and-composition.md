# Phase 1C — Accessibility and Composition Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Shrink every assembly's public surface to what actually crosses its boundary, move each assembly's DI registration inside that assembly, and leave behind machinery that fails when someone widens the surface again.

**Architecture:** Three moves, in a dependency order that matters. First a lock: a source-text test in `Noof.Ledger.Architecture.Tests` records the exact set of public types per `src` project, so every later shrink shows up as a deletion from an allowlist and every widening is a deliberate edit to a reviewed file. Then composition: each infrastructure assembly grows one public `AddNoofXxx(this IServiceCollection, ...)` extension and everything it registers becomes `internal` — which is what *lets* the surface shrink, because `Program.cs` can only stop naming `EfUserStore` once something inside `Noof.Ledger.Persistence` names it instead. Finally validation: a curated set of Roslyn rules at `error` for what the compiler can prove, plus a committed ReSharper settings layer and `ops/inspect.ps1` for the one thing no Roslyn analyzer can do — solution-wide "this public member is never called from outside its own assembly".

**Tech Stack:** C# / .NET 10, xUnit v3 on Microsoft Testing Platform, `Microsoft.Extensions.DependencyInjection`, `Microsoft.CodeAnalysis.NetAnalyzers` (ships in the SDK), `JetBrains.ReSharper.GlobalTools` as a .NET local tool.

**Spec:** `docs/superpowers/specs/2026-09-19-noof-finance-design.md` — this phase implements no spec feature. It is an operator-requested interlude between Phase 1B and Phase 2, and its four requirements are quoted verbatim below. The spec travels with it because the layering it mandates (`Domain ← Application ← infrastructure ← Host`) is exactly what the rules here turn from convention into enforcement.

---

## The four requirements, verbatim

The operator's words, 2026-09-22. These are the acceptance criteria.

1. «уровень доступности кода - минимальный необходимый. все методы приватные если нет необходимости в обратном. не нужно делать публичным "на всякий случай", лучше расширить потом. все классы интернал, если не нужны в других сборках. Исключение - тесты. Для них можно делать internal или private protected.»
2. «все сервисы используемые между сборками должны быть интерфейсами. Если это не примитивные экстешены или хелперы.»
3. «все сервисы регистрируются в самой сборке. Точнее публично торчит способ регистрации а хост его вызывает AddNoofDomain(), AddNoofPersistence() или как то так.»
4. «нужна валидация анализаторами. Возможно ли добавить решарпер анализатор?»

---

## Global Constraints

- **.NET 10**, `TargetFramework=net10.0`, `LangVersion=latest`, nullable enabled — all in `Directory.Build.props`. Do not change them.
- **`TreatWarningsAsErrors=true` and `EnforceCodeStyleInBuild=true`.** Every warning this plan introduces is a build failure. `IDE0161` (file-scoped namespaces) is an error.
- **Central Package Management.** A new package needs a `<PackageVersion>` in `Directory.Packages.props` and a **version-less** `<PackageReference>` in the project. A version attribute on the `PackageReference` is NU1008 — an error.
- **TDD, and watch the new test fail before letting it pass.** Break the thing deliberately, see the failure name it, put it back.
- **Never write to the `noof_ledger` database.** It holds the operator's real financial data. Tests use throwaway databases through `Noof.Ledger.TestKit`.
- **Never run the live-model suite.** The 8 tests gated on `NOOF_LEDGER_LIVE_ANTHROPIC_KEY` spend real money and need explicit operator authorisation. They must stay skipped.
- **No comments that restate the code.** Only comments that explain *why*. No XML docs on non-public members. See `CLAUDE.md` §3.
- **`Noof.Ledger.Domain` and `Noof.Ledger.Application` have zero package references.** Both asserted by `ProjectReferenceTests`. This phase must not change that.
- **`Noof.Ledger.Web` may not see a `DbContext`, an EF type, or an `HttpClient`.** Asserted by `ProjectReferenceTests` and by `DisableTransitiveProjectReferences`.
- **Baseline to preserve:** 452 solution tests (444 run, 8 live skipped) plus 13 Playwright E2E tests, all green. Every task ends green. No task may reduce the test count except by deleting a test its own commit message justifies.
- **Run `dotnet test --solution NoofLedger.slnx` and state the actual result.** Never claim green without having seen it.

---

## Decisions already taken, so no task re-litigates them

Each of these was settled while writing this plan, several of them by measurement. An implementer who disagrees should **stop and say so** rather than work around it — in Phase 1B every implementer who stopped turned out to be right.

- **There is no `AddNoofDomain()` and no `AddNoofApplication()`.** Both assemblies hold only types and contracts; there is nothing to register. An empty extension method is exactly the "ceremony that exists to look enterprise" `CLAUDE.md` §3 forbids. Requirement 3 is met by the assemblies that actually own services: `Persistence`, `Ai`, `Telegram`, and the Host's own workers.
- **Razor components stay `public`.** The Razor SDK generates `public partial class` per `.razor` file and offers no directive to change it. `App` and `Routes` genuinely cross into the Host (`app.MapRazorComponents<App>()`). A documented exception in the allowlist test, not an oversight.
- **EF migration classes stay `public`.** EF scaffolds them that way and rewrites them on the next scaffold. `CLAUDE.md` already exempts their generated companions from code style; the allowlist test excludes `Migrations/` for the same reason.
- **The Host keeps Razor, authentication, endpoint and data-protection wiring.** Those are composition-root concerns, and `AddRazorComponents` is not even available inside a Razor class library with no ASP.NET Core framework reference. Requirement 3 is about services, not about emptying `Program.cs` for its own sake.
- **`TimeZoneInfo` becomes a registered singleton**, alongside the existing `AddSingleton(TimeProvider.System)`. Today the read model's zone is threaded through a hand-written three-argument lambda in `Program.cs`; registering the zone once deletes that lambda and removes a parameter from `AddNoofPersistence`.
- **`Microsoft.CodeAnalysis.PublicApiAnalyzers` is not adopted.** It locks member-level surface at build time, which is attractive, but it needs a hand-maintained `PublicAPI.Unshipped.txt` per project — several hundred lines for `Domain` plus `Application` — and at type level it duplicates what Task 1 does in the house style with no package at all. Task 9 records it in `docs/BACKLOG.md` with this reasoning so it is not re-proposed as new.
- **`AnalysisMode=All` is not adopted.** Measured, not guessed: a full rebuild with `-p:AnalysisMode=All` emitted **842 distinct warnings across 31 rules** — 376 `CA1707` (underscores in names: our deliberate test-naming convention), 258 `CA2007` (`ConfigureAwait`, meaningless in an app with no synchronization context), 61 `CA2000`, 28 `CA1062` (argument-null boilerplate `CLAUDE.md` §3 forbids). Adopting it would mean suppressing most of the rulebook and calling the remainder strictness. Task 6 enables a named list instead, and Task 9 banks the measurement so nobody re-proposes the blanket switch blind.
- **`CA1812` ("avoid uninstantiated internal classes") is not enabled.** It flags internal types never constructed inside their own assembly — which is every DI-registered service this phase is about to make internal. False positives all the way down.
- **`CA1515`'s reach must be widened by hand.** It defaults to executable output kinds only, so on a class library it is silent. `.editorconfig` needs `dotnet_code_quality.CA1515.output_kind = ConsoleApplication, DynamicallyLinkedLibrary` or the rule does nothing for `Persistence`, `Ai` and `Telegram` — the three assemblies this phase is mostly about.

---

## What is true today (measured, 2026-09-22)

Public top-level types per `src` project, counted from source:

| Project | Public types now | Concrete services among them | Target |
|---|---|---|---|
| `Noof.Ledger.Domain` | 17 | 0 | 17 — unchanged, all genuinely cross assemblies |
| `Noof.Ledger.Application` | 39 | 0 | 39 — unchanged, these *are* the contracts |
| `Noof.Ledger.Persistence` | 16, of which **12 are scanned** — the other 4 are EF migration classes, which `PublicSurfaceTests` excludes (+ 13 already internal) | 11 | 2 |
| `Noof.Ledger.Ai` | 7 | 5 | 1 |
| `Noof.Ledger.Telegram` | 11 | 9 | 1 |
| `Noof.Ledger.Host` | 14 | 11 | 0 |
| `Noof.Ledger.Fx`, `Noof.Ledger.Receipts` | 0 | 0 | 0 — empty placeholder projects |
| `Noof.Ledger.Web` | 0 `.cs`, 7 `.razor` | 0 | unchanged (Razor exception) |

Which test project touches which concrete type — this decides where `InternalsVisibleTo` is needed:

| Test project | References | Concrete src types it names | Needs `InternalsVisibleTo` from |
|---|---|---|---|
| `Architecture.Tests` | **nothing** — reads `.cs`/`.csproj` as text | none | nothing. It cannot be broken by making types internal. |
| `Domain.Tests` | Domain, TestKit | none beyond Domain value types | nothing |
| `TestKit` | Application, Domain | none | nothing |
| `Persistence.Tests` | Persistence, TestKit | `LedgerDbContext`, `AppSecret`, `EfJobQueue`, `EfSpendingReadModel`, `EfSecretStore`, `EfCaptureStore`, `EfCategorizationStore`, `EfCategoryCatalog`, `EfMerchantDirectory`, `EfUserStore`, `LedgerConnectionString` | Persistence — **already granted** |
| `Ai.Tests` | Ai, Application | `AnthropicCategorizer`, `AnthropicOptions`, `CategorizationPrompt`, `CategorizationSchema`, `AnthropicClientFactory`, `AnthropicKeyProbe` | Ai — Task 3 adds it |
| `Telegram.Tests` | Telegram | `TelegramUpdateRouter`, `TelegramClientHandle`, `TelegramOwnerGate`, `TelegramUpdateOffsetStore`, `TelegramBackoff`, `TelegramBotClientFactory`, `TelegramChatNotifier`, `TelegramPollingService` | Telegram — Task 4 adds it |
| `Host.Tests` | Host | `CategorizationWorker`, `CategorizationWorkerOptions`, `EfJobQueue`, `PasswordHasherAdapter`, `LocalOwnerHandler`, `CategorizationReply`, `LoopbackGuard`, `CaptureTimeZoneGuard`, `DataProtectionSetup`, `UserCommand`, `AuthSchemes`, `Program` | Host — Task 5 adds it. `EfJobQueue` means **Persistence too** — Task 2 adds it. |
| `E2E.Tests` | Host, TestKit | `LedgerDbContext`, `EfSpendingReadModel`, `EfSecretStore`, `DataProtectionSetup`, `UserCommand`, `Program` | Persistence (Task 2) and Host (Task 5) |

`WebApplicationFactory<Program>` is used by seven `Host.Tests` classes and by `E2E.Tests`'s `CookieModeHostFixture`. `internal partial class Program` plus `InternalsVisibleTo` is the supported pattern and is what Task 5 does.

---

## File Structure

New files, and what each owns:

| File | Responsibility |
|---|---|
| `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs` | The lock. One allowlist of public type names per `src` project, plus the rule that an infrastructure assembly exposes no public concrete service. |
| `src/Noof.Ledger.Persistence/PersistenceRegistration.cs` | `AddNoofPersistence` and `MigrateNoofDatabaseAsync`. |
| `src/Noof.Ledger.Ai/AiRegistration.cs` | `AddNoofAi`. The only public type left in `Noof.Ledger.Ai`. |
| `src/Noof.Ledger.Telegram/TelegramRegistration.cs` | `AddNoofTelegram`. The only public type left in `Noof.Ledger.Telegram`. |
| `src/Noof.Ledger.Host/Workers/WorkerRegistration.cs` | `AddNoofWorkers`. Internal — only `Program.cs` calls it, from the same assembly. |
| `NoofLedger.sln.DotSettings` | Team-shared ReSharper/Rider settings layer, committed. Raises accessibility inspections to ERROR for anyone who opens the solution in Rider. |
| `.config/dotnet-tools.json` | Pins `JetBrains.ReSharper.GlobalTools` so `ops/inspect.ps1` is reproducible. |
| `ops/inspect.ps1` | Runs `jb inspectcode`, parses the report, fails on ERROR-severity findings. |

Modified: every `src/**/*.cs` whose accessibility changes, `src/Noof.Ledger.Host/Program.cs`, three infrastructure `.csproj` files (packages + `InternalsVisibleTo`), `tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs` (the package allowlists it asserts), `.editorconfig`, `CLAUDE.md`, `README.md`, `docs/BACKLOG.md`, `ops/RUNBOOK.md`, `.gitignore`.

---

## Task 1: The public-surface lock

Nothing can be safely shrunk until shrinking is observable. This task adds the test that makes every later task's effect visible, seeded with **today's** surface so the suite is green from the first commit. Tasks 2–5 then delete entries from it.

**Files:**
- Create: `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`
- Test: the file is itself the test.

**Interfaces:**
- Consumes: `RepoRoot.Find()` from `tests/Noof.Ledger.Architecture.Tests/RepoRoot.cs` — returns the `DirectoryInfo` containing `global.json`.
- Produces: `PublicSurfaceTests.Allowed` — a `Dictionary<string, string[]>` keyed by project folder name. Tasks 2–5 each edit exactly the entry for the assembly they touch.

- [ ] **Step 1: Write the test**

`Noof.Ledger.Architecture.Tests` has no `ProjectReference` to anything and must keep it that way — it reads source as text, which is why making `src` types internal cannot break it.

```csharp
using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

// Requirement 1 of Phase 1C: "не нужно делать публичным 'на всякий случай'". A convention cannot
// enforce that - Phase 1B shipped a README claim about [Authorize] that had already drifted. This
// is the same fix applied to accessibility: the surface is a list, and widening it is an edit to
// this file that a reviewer sees.
public class PublicSurfaceTests
{
    // Razor generates `public partial class` per .razor file with no directive to change it, and
    // EF scaffolds migration classes public and rewrites them on the next scaffold. Neither is a
    // decision anyone here gets to make, so neither is scanned.
    static readonly string[] ScannedProjects =
    [
        "Noof.Ledger.Domain",
        "Noof.Ledger.Application",
        "Noof.Ledger.Persistence",
        "Noof.Ledger.Ai",
        "Noof.Ledger.Telegram",
        "Noof.Ledger.Fx",
        "Noof.Ledger.Receipts",
        "Noof.Ledger.Host",
    ];

    static readonly Regex TopLevelPublicType = new(
        @"^public\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+|readonly\s+|ref\s+)*"
        + @"(?:record\s+struct|record\s+class|class|record|interface|enum|struct)\s+"
        + @"(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["Noof.Ledger.Domain"] =
        [
            "AppUser", "CategorizationAuthority", "CategorizationJob", "Category", "CurrencyCode",
            "CurrencyMismatchException", "JobStatus", "LineItem", "Merchant", "MerchantAlias",
            "MerchantKind", "MerchantName", "Money", "QuotedAmount", "Transaction",
            "TransactionStatus", "Wallet",
        ],
        ["Noof.Ledger.Application"] =
        [
            "IPasswordHasher", "IUserStore", "PasswordVerifyResult", "CapturedMessage",
            "ICaptureStore", "CategoryOption", "MerchantOption", "CategorizationRequest",
            "ProposedLineItem", "CategorizationProposal", "ResolvedLineItem", "CategorizedLineItem",
            "CategorizationSubject", "CategoryEntry", "MerchantAliasEntry", "ICategorizationStore",
            "ICategorizer", "ICategoryCatalog", "IMerchantDirectory", "MerchantScan",
            "ModelFailureKind", "ModelCallException", "ModelCallExceptionExtensions",
            "ProposalVerification", "IChatNotifier", "IJobQueue", "JobCompletionOutcome",
            "RecentLineItem", "RecentTransaction", "MonthTotal", "MonthSummary",
            "ISpendingReadModel", "ProbeResult", "ISecretProbe", "ISecretStore", "SecretKeys",
            "SecretResult", "SecretState", "SecretStatus",
        ],
        ["Noof.Ledger.Persistence"] =
        [
            "EfUserStore", "EfCaptureStore", "EfCategorizationStore", "EfCategoryCatalog",
            "EfMerchantDirectory", "DesignTimeDbContextFactory", "EfJobQueue",
            "LedgerConnectionString", "LedgerDbContext", "EfSpendingReadModel", "AppSecret",
            "EfSecretStore",
        ],
        ["Noof.Ledger.Ai"] =
        [
            "AnthropicCategorizer", "IAnthropicClientFactory", "AnthropicClientFactory",
            "AnthropicKeyProbe", "AnthropicOptions", "CategorizationPrompt", "CategorizationSchema",
        ],
        ["Noof.Ledger.Telegram"] =
        [
            "ITelegramBotClientFactory", "ITelegramUpdateRouter", "TelegramBackoff",
            "TelegramBotClientFactory", "TelegramChatNotifier", "TelegramClientHandle",
            "TelegramOwnerGate", "TelegramPollResult", "TelegramPollingService",
            "TelegramUpdateOffsetStore", "TelegramUpdateRouter",
        ],
        ["Noof.Ledger.Fx"] = [],
        ["Noof.Ledger.Receipts"] = [],
        ["Noof.Ledger.Host"] =
        [
            "AuthSchemes", "LocalOwnerHandler", "PasswordHasherAdapter", "UserCommand",
            "AccountEndpoints", "Program", "CaptureTimeZoneGuard", "DataProtectionSetup",
            "LoopbackGuard", "CategorizationReply", "CategorizationTickResult",
            "CategorizationWorker", "CategorizationWorkerOptions",
        ],
    };

    [Theory]
    [MemberData(nameof(Projects))]
    public void Public_types_are_exactly_the_allowed_set(string project)
    {
        PublicTypesIn(project).Should().BeEquivalentTo(Allowed[project],
            $"the public surface of {project} is a reviewed list, not whatever accumulated; widening "
            + "it means editing PublicSurfaceTests.Allowed, which is the point");
    }

    [Fact]
    public void No_public_concrete_service_crosses_an_infrastructure_boundary()
    {
        string[] infrastructure = ["Noof.Ledger.Persistence", "Noof.Ledger.Ai", "Noof.Ledger.Telegram"];

        var offenders = infrastructure
            .SelectMany(project => SourceFiles(project)
                .SelectMany(file => ConcreteServiceDeclarations(File.ReadAllText(file))
                    .Select(name => $"{project}/{name}")))
            .ToArray();

        // Requirement 2: a service that crosses an assembly boundary is reached through an
        // interface. The teeth are here rather than in a naming convention - if the implementation
        // is internal, the interface is the only way in, and this test is what notices when one
        // stops being internal.
        offenders.Should().BeEmpty(
            "an implementation that another assembly can name by type is an implementation another "
            + "assembly can depend on; Application owns the interface, the infrastructure owns the class");
    }

    [Fact]
    public void Every_scanned_project_exists_and_the_domain_surface_is_not_empty()
    {
        foreach (var project in ScannedProjects)
            Directory.Exists(ProjectRoot(project)).Should().BeTrue($"{project} must exist to be scanned");

        // Fx and Receipts are legitimately empty placeholders, so "every list is non-empty" would be
        // wrong. Domain standing in for the set proves the regex still matches real declarations - a
        // rule whose subject set is empty passes forever and enforces nothing.
        PublicTypesIn("Noof.Ledger.Domain").Should().NotBeEmpty();
    }

    public static TheoryData<string> Projects() => [.. ScannedProjects];

    static string ProjectRoot(string project) =>
        Path.Combine(RepoRoot.Find().FullName, "src", project);

    static IEnumerable<string> SourceFiles(string project) =>
        Directory.Exists(ProjectRoot(project))
            ? Directory.EnumerateFiles(ProjectRoot(project), "*.cs", SearchOption.AllDirectories)
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            : [];

    static string[] PublicTypesIn(string project) =>
        [.. SourceFiles(project)
            .SelectMany(file => TopLevelPublicType.Matches(File.ReadAllText(file)))
            .Select(match => match.Groups["name"].Value)];

    // A "concrete service" is a public non-static, non-abstract class. Records, enums, structs and
    // static classes are data and helpers - requirement 2 exempts "примитивные экстешены или хелперы".
    static IEnumerable<string> ConcreteServiceDeclarations(string source) =>
        TopLevelPublicType.Matches(source)
            .Where(match => match.Value.Contains(" class ", StringComparison.Ordinal))
            .Where(match => !match.Value.Contains(" static ", StringComparison.Ordinal))
            .Where(match => !match.Value.Contains(" abstract ", StringComparison.Ordinal))
            .Where(match => !match.Value.Contains(" record ", StringComparison.Ordinal))
            .Select(match => match.Groups["name"].Value);
}
```

- [ ] **Step 2: Run it and see the surface test pass and the concrete-service test fail**

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`

Expected: `Public_types_are_exactly_the_allowed_set` PASSES for all eight projects — the allowlist above was transcribed from today's tree, so any failure means the transcription is wrong and you must fix the **allowlist**, not the source. `No_public_concrete_service_crosses_an_infrastructure_boundary` **FAILS**, naming roughly 25 offenders across Persistence, Ai and Telegram. That is the true state of the repository right now.

- [ ] **Step 3: Make the failing test pass the only honest way available today — by scoping it**

Do not fix 25 classes here; Tasks 2–4 do that, one assembly at a time. Instead add `Skip` with the reason and the tracking, so the suite is green and the rule is not silently lost:

```csharp
    [Fact(Skip = "Enabled per-assembly by Phase 1C Tasks 2-4; Task 4 removes this Skip.")]
    public void No_public_concrete_service_crosses_an_infrastructure_boundary()
```

- [ ] **Step 4: Watch the surface lock actually fail**

A guard never seen red may be enforcing nothing. Add a throwaway file `src/Noof.Ledger.Domain/Rogue.cs` containing `namespace Noof.Ledger.Domain;` and `public sealed class Rogue;`, run the Architecture suite, and confirm `Public_types_are_exactly_the_allowed_set` fails naming `Rogue`. Then **delete the file** and confirm green again.

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`

- [ ] **Step 5: Run the whole solution suite**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: 455 total (452 baseline + 3 new: 8 theory cases count as 8, so expect 452 + 8 + 1 + 1 = 462 with 9 skipped — report the real number you see, and if it differs from this estimate say so rather than adjusting the estimate silently).

- [ ] **Step 6: Commit**

```bash
git add tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs
git commit -m "test(arch): lock each assembly's public surface to a reviewed list"
```

---

## Task 2: `AddNoofPersistence`

**Files:**
- Create: `src/Noof.Ledger.Persistence/PersistenceRegistration.cs`
- Modify: `src/Noof.Ledger.Persistence/Noof.Ledger.Persistence.csproj` (add `InternalsVisibleTo` for `Noof.Ledger.Host.Tests` and `Noof.Ledger.E2E.Tests`)
- Modify: every `src/Noof.Ledger.Persistence/**/*.cs` outside `Migrations/` that declares a public type, except `LedgerConnectionString`
- Modify: `src/Noof.Ledger.Host/Program.cs`
- Modify: `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs` (shrink the Persistence allowlist)

**Interfaces:**
- Consumes: `PublicSurfaceTests.Allowed` from Task 1.
- Produces, both in namespace `Noof.Ledger.Persistence`:
  - `public static IServiceCollection AddNoofPersistence(this IServiceCollection services, IConfiguration configuration, int maxJobAttempts)`
  - `public static Task MigrateNoofDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)`

- [ ] **Step 1: Write the failing test**

Add to `tests/Noof.Ledger.Host.Tests/CategorizationWiringTests.cs` — or, if that file's fixture does not fit, a new `tests/Noof.Ledger.Host.Tests/PersistenceRegistrationTests.cs` with the same `WebApplicationFactory<Program>` setup the neighbouring tests use. The behaviour under test is that one call registers the whole persistence surface:

```csharp
[Fact]
public void AddNoofPersistence_registers_every_store_the_application_asks_for()
{
    var services = new ServiceCollection();
    services.AddSingleton(TimeProvider.System);
    services.AddSingleton(TimeZoneInfo.Utc);
    services.AddDataProtection();
    services.AddNoofPersistence(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Ledger"] = "Host=127.0.0.1;Database=noof_ledger_never_opened;Username=x;Password=y",
            })
            .Build(),
        maxJobAttempts: 8);

    using var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();

    // Resolving is enough: none of these opens a connection in its constructor, so this test never
    // reaches PostgreSQL. The database name is deliberately one that does not exist, so a future
    // constructor that DID connect would fail loudly here rather than touching a real ledger.
    scope.ServiceProvider.GetRequiredService<IUserStore>().Should().NotBeNull();
    scope.ServiceProvider.GetRequiredService<ISecretStore>().Should().NotBeNull();
    scope.ServiceProvider.GetRequiredService<ICaptureStore>().Should().NotBeNull();
    scope.ServiceProvider.GetRequiredService<ICategorizationStore>().Should().NotBeNull();
    scope.ServiceProvider.GetRequiredService<ICategoryCatalog>().Should().NotBeNull();
    scope.ServiceProvider.GetRequiredService<IMerchantDirectory>().Should().NotBeNull();
    scope.ServiceProvider.GetRequiredService<ISpendingReadModel>().Should().NotBeNull();
    scope.ServiceProvider.GetRequiredService<IJobQueue>().Should().NotBeNull();
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: FAIL to **compile** — `AddNoofPersistence` does not exist. A compile failure is a legitimate red for a method that does not exist yet.

- [ ] **Step 3: Write the registration**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Application.Capture;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Persistence.Auth;
using Noof.Ledger.Persistence.Capture;
using Noof.Ledger.Persistence.Categorization;
using Noof.Ledger.Persistence.Jobs;
using Noof.Ledger.Persistence.Reporting;
using Noof.Ledger.Persistence.Secrets;

namespace Noof.Ledger.Persistence;

public static class PersistenceRegistration
{
    // maxJobAttempts is a parameter rather than a configuration key of its own so that EfJobQueue's
    // server-side attempt limit and CategorizationWorker's "is this the last attempt" check can
    // never independently drift. The Host passes CategorizationWorkerOptions.MaxAttempts to both.
    public static IServiceCollection AddNoofPersistence(
        this IServiceCollection services, IConfiguration configuration, int maxJobAttempts)
    {
        services.AddDbContext<LedgerDbContext>(options =>
            options.UseNpgsql(LedgerConnectionString.Resolve(configuration.GetConnectionString("Ledger"))));

        services.AddScoped<IUserStore, EfUserStore>();
        services.AddScoped<ISecretStore, EfSecretStore>();
        services.AddScoped<ICaptureStore, EfCaptureStore>();
        services.AddScoped<ICategorizationStore, EfCategorizationStore>();
        services.AddScoped<ICategoryCatalog, EfCategoryCatalog>();
        services.AddScoped<IMerchantDirectory, EfMerchantDirectory>();
        services.AddScoped<ISpendingReadModel, EfSpendingReadModel>();
        services.AddScoped<IJobQueue>(sp => new EfJobQueue(
            sp.GetRequiredService<LedgerDbContext>(),
            sp.GetRequiredService<TimeProvider>(),
            maxJobAttempts));

        return services;
    }

    public static async Task MigrateNoofDatabaseAsync(
        this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LedgerDbContext>()
            .Database.MigrateAsync(cancellationToken);
    }
}
```

`EfSpendingReadModel` can now be registered by type rather than by lambda because its third constructor argument, `TimeZoneInfo currentZone`, is resolved from the container — the Host registers that singleton in Step 5.

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Rewrite the Host's persistence wiring**

In `src/Noof.Ledger.Host/Program.cs`, delete these lines:

```csharp
builder.Services.AddDbContext<LedgerDbContext>(options =>
    options.UseNpgsql(LedgerConnectionString.Resolve(builder.Configuration.GetConnectionString("Ledger"))));

builder.Services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
builder.Services.AddScoped<IUserStore, EfUserStore>();
builder.Services.AddScoped<ISecretStore, EfSecretStore>();
builder.Services.AddScoped<ICaptureStore, EfCaptureStore>();
```

keeping only the `IPasswordHasher` line (that is a Host type, and Task 5 moves it). Delete the `ICategoryCatalog`, `IMerchantDirectory`, `ICategorizationStore`, `ISpendingReadModel` and `IJobQueue` registrations with their comments — the `IJobQueue` comment about `MaxAttempts` moves verbatim onto the `maxJobAttempts` parameter in `PersistenceRegistration` (it is already there in Step 3) and the `ISpendingReadModel` comment's point about the zone is now expressed by the singleton registration. Replace the `Database:MigrateOnStartup` block's body with the extension call. The result:

```csharp
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(CaptureTimeZoneGuard.Resolve(
    builder.Configuration["Capture:TimeZone"] ?? "Europe/Belgrade"));

var categorizationOptions = new CategorizationWorkerOptions();
builder.Configuration.GetSection("Categorization").Bind(categorizationOptions);
builder.Services.AddSingleton(categorizationOptions);

builder.Services.AddNoofPersistence(builder.Configuration, categorizationOptions.MaxAttempts);
```

and later:

```csharp
if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
    await app.Services.MigrateNoofDatabaseAsync();
```

The existing `CaptureTimeZoneGuard.Resolve(...)` call near the top of `Program.cs` that validates the configured zone at boot is now redundant with this registration — delete the standalone call and keep the registered one, since it performs the same validation and fails the same way.

- [ ] **Step 6: Make the Persistence implementations internal**

Change `public sealed class` to `internal sealed class` in: `Auth/EfUserStore.cs`, `Capture/EfCaptureStore.cs`, `Categorization/EfCategorizationStore.cs`, `Categorization/EfCategoryCatalog.cs`, `Categorization/EfMerchantDirectory.cs`, `Jobs/EfJobQueue.cs`, `Reporting/EfSpendingReadModel.cs`, `Secrets/EfSecretStore.cs`, `Secrets/AppSecret.cs`.

Change `public class LedgerDbContext` to `internal class LedgerDbContext` and `public sealed class DesignTimeDbContextFactory` to `internal sealed class DesignTimeDbContextFactory`.

`LedgerConnectionString` stays `public`: `src/Noof.Ledger.Host/Cli/UserCommand.cs` names it, and requirement 2 exempts helpers.

Add to `src/Noof.Ledger.Persistence/Noof.Ledger.Persistence.csproj`, in the existing `InternalsVisibleTo` `ItemGroup`:

```xml
    <InternalsVisibleTo Include="Noof.Ledger.Host.Tests" />
    <InternalsVisibleTo Include="Noof.Ledger.E2E.Tests" />
```

- [ ] **Step 7: Verify EF tooling still discovers an internal DbContext — this is the risky part**

`LedgerDbContext` being internal is the one change in this task that could break something no test covers: `dotnet ef` finds `IDesignTimeDbContextFactory<T>` implementations by reflection, and whether it accepts a non-public one is **not documented**.

Run: `dotnet ef dbcontext info --project src/Noof.Ledger.Persistence`

Expected: it prints the context name and provider. **If it fails**, do not work around it: revert `LedgerDbContext` and `DesignTimeDbContextFactory` to `public`, leave both in the Task 2 allowlist, and say so in the commit message and in your report. A database whose migrations cannot be scaffolded is a far worse outcome than two extra public types, and `CLAUDE.md` makes migrations the single mechanism for schema change.

- [ ] **Step 8: Shrink the allowlist**

In `PublicSurfaceTests.Allowed`, replace the `Noof.Ledger.Persistence` entry with what actually survived Step 7 — either:

```csharp
        ["Noof.Ledger.Persistence"] = ["LedgerConnectionString", "PersistenceRegistration"],
```

or, if Step 7 forced the revert:

```csharp
        ["Noof.Ledger.Persistence"] =
            ["LedgerConnectionString", "PersistenceRegistration", "LedgerDbContext", "DesignTimeDbContextFactory"],
```

- [ ] **Step 9: Run the full suite**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: green, with the same count as Task 1 plus the one new wiring test. `Persistence.Tests` and `E2E.Tests` compile against internals through `InternalsVisibleTo`.

- [ ] **Step 10: Commit**

```bash
git add src/Noof.Ledger.Persistence src/Noof.Ledger.Host/Program.cs tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs tests/Noof.Ledger.Host.Tests
git commit -m "refactor(persistence): register the stores inside the assembly that owns them"
```

---

## The rule Task 2 paid for: grep before you narrow

Task 2's file list was wrong in a way that cost real time. It listed the Persistence files and `Program.cs`, and missed that `src/Noof.Ledger.Host/Cli/UserCommand.cs` hand-rolled `AddDbContext<LedgerDbContext>` and `AddScoped<IUserStore, EfUserStore>()` of its own. The moment those types went internal, a file the plan never mentioned stopped compiling.

**So, in every remaining task, before changing a single `public` to `internal`:** grep the whole repository for each type name you are about to narrow and list where it is used. Anything outside the owning assembly and its own test project is a surprise. Handle it the way Task 2 did — route the caller through the public registration or an Application interface — **never** by widening accessibility back or by granting `InternalsVisibleTo` to a production assembly.

Watch for one specific compiler error while doing it. `CS0050` fires when a `public` member's signature mentions a type that has become internal; Task 2 hit it on `PostgresFixture.CreateContextAsync()`, which returned `LedgerDbContext`. The fix is to narrow the **test helper** to `internal`, which requirement 1 explicitly permits — not to re-widen the `src` type.

### And the one Task 4 paid for: NSubstitute cannot see internals either

`InternalsVisibleTo("Noof.Ledger.Telegram.Tests")` is not enough to let a test write `Substitute.For<ITelegramUpdateRouter>()` once that interface is internal. NSubstitute builds its proxies with Castle DynamicProxy, which emits them into a separate dynamic assembly — so the *declaring* assembly must also grant:

```xml
    <InternalsVisibleTo Include="DynamicProxyGenAssembly2" />
```

Without it the failure is a runtime `ProxyGenerationException` that names the type and, helpfully, tells you exactly this. Tasks 2 and 3 never saw it because `Persistence.Tests` and `Ai.Tests` only substitute `Application`-level public interfaces; Task 4 was the first to substitute an infrastructure-local one.

**Before narrowing any interface, grep that assembly's test project for `Substitute.For<`.** If it names a type you are about to make internal, add the grant in the same commit rather than discovering it as a red test.

---

## Task 3: `AddNoofAi`

**Files:**
- Create: `src/Noof.Ledger.Ai/AiRegistration.cs`
- Modify: `src/Noof.Ledger.Ai/Noof.Ledger.Ai.csproj` (packages + `InternalsVisibleTo`)
- Modify: `Directory.Packages.props`
- Modify: every other `src/Noof.Ledger.Ai/*.cs`
- Modify: `src/Noof.Ledger.Host/Program.cs`
- Modify: `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs` and `ProjectReferenceTests.cs`

**Interfaces:**
- Consumes: `AddNoofPersistence` (Task 2) — `AddNoofAi` must be called after it, because `AnthropicClientFactory` takes `ISecretStore`.
- Produces: `public static IServiceCollection AddNoofAi(this IServiceCollection services, IConfiguration configuration)` in namespace `Noof.Ledger.Ai`.

- [ ] **Step 1: Write the failing test**

In `tests/Noof.Ledger.Ai.Tests/AiRegistrationTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Secrets;
using NSubstitute;

namespace Noof.Ledger.Ai.Tests;

public class AiRegistrationTests
{
    [Fact]
    public void AddNoofAi_registers_the_categorizer_and_the_key_probe()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Substitute.For<ISecretStore>());
        services.AddNoofAi(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ICategorizer>().Should().BeOfType<AnthropicCategorizer>();
        scope.ServiceProvider.GetServices<ISecretProbe>().Should().ContainSingle()
            .Which.Should().BeOfType<AnthropicKeyProbe>();
    }

    [Fact]
    public void AddNoofAi_binds_the_Ai_configuration_section()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Substitute.For<ISecretStore>());
        services.AddNoofAi(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:MaxTokens"] = "4096" })
            .Build());

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<AnthropicOptions>().MaxTokens.Should().Be(4096);
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`
Expected: FAIL to compile — `AddNoofAi` does not exist.

- [ ] **Step 3: Add the packages**

`Noof.Ledger.Ai` currently references only the `Anthropic` package, so it has neither `IServiceCollection`, nor `IConfiguration`, nor `Bind`, nor `AddHttpClient`. Add to `Directory.Packages.props` (the `Microsoft.Extensions.*` versions already in that file are `10.0.12`; use the same):

```xml
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.12" />
    <PackageVersion Include="Microsoft.Extensions.Configuration.Binder" Version="10.0.12" />
```

and to `src/Noof.Ledger.Ai/Noof.Ledger.Ai.csproj`:

```xml
  <ItemGroup>
    <PackageReference Include="Anthropic" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Binder" />
    <PackageReference Include="Microsoft.Extensions.Http" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Noof.Ledger.Ai.Tests" />
  </ItemGroup>
```

`Microsoft.Extensions.Http` already has a `PackageVersion` entry.

- [ ] **Step 4: Write the registration**

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Ai;

public static class AiRegistration
{
    const string HttpClientName = "anthropic";

    public static IServiceCollection AddNoofAi(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new AnthropicOptions();
        configuration.GetSection("Ai").Bind(options);
        services.AddSingleton(options);

        // IHttpClientFactory's default logging handlers redact header values through
        // HttpClientFactoryOptions.ShouldRedactHeaderValue, but that is a per-client options
        // delegate reachable by anything that later calls services.Configure<HttpClientFactoryOptions>
        // ("anthropic", ...). x-api-key carries the Anthropic key on every request; removing the
        // logging handlers means there is nothing left for such a change to re-expose.
        services.AddHttpClient(HttpClientName).RemoveAllLoggers();

        // AddHttpClient registers IHttpClientFactory, never an HttpClient - AnthropicClientFactory
        // takes a real client, so it has to be built through a lambda.
        services.AddScoped<IAnthropicClientFactory>(sp => new AnthropicClientFactory(
            sp.GetRequiredService<ISecretStore>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
            sp.GetRequiredService<AnthropicOptions>()));

        services.AddScoped<ICategorizer, AnthropicCategorizer>();
        services.AddScoped<ISecretProbe, AnthropicKeyProbe>();

        return services;
    }
}
```

- [ ] **Step 5: Run the test and watch it pass**

Run: `dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`
Expected: PASS.

- [ ] **Step 6: Make everything else in `Noof.Ledger.Ai` internal and rewrite the Host wiring**

`internal` on: `AnthropicCategorizer`, `IAnthropicClientFactory`, `AnthropicClientFactory`, `AnthropicKeyProbe`, `AnthropicOptions`, `CategorizationPrompt`, `CategorizationSchema`.

In `Program.cs`, delete the `anthropicOptions` block, the `AddHttpClient("anthropic").RemoveAllLoggers()` line with its long comment (it now lives in `AiRegistration`), the `IAnthropicClientFactory` lambda, and the `ICategorizer` / `ISecretProbe` registrations. Replace with:

```csharp
builder.Services.AddNoofAi(builder.Configuration);
```

Remove the now-unused `using Noof.Ledger.Ai;` only if nothing else in `Program.cs` needs it — `AddNoofAi` is in that namespace, so the using stays.

- [ ] **Step 7: Update the architecture tests**

In `PublicSurfaceTests.Allowed`:

```csharp
        ["Noof.Ledger.Ai"] = ["AiRegistration"],
```

In `ProjectReferenceTests`, the `Noof.Ledger.Ai` package assertion (if one exists — check; if it does not, leave it alone) must now list `Anthropic`, `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Configuration.Binder`, `Microsoft.Extensions.Http`.

`AiBoundaryTests.Only_Ai_references_the_Anthropic_SDK_namespace` must still pass — `AiRegistration.cs` names no `Anthropic.` type.

- [ ] **Step 8: Run the full suite**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: green. The 8 live-model tests stay skipped.

- [ ] **Step 9: Commit**

```bash
git add src/Noof.Ledger.Ai src/Noof.Ledger.Host/Program.cs Directory.Packages.props tests
git commit -m "refactor(ai): one public way in, and the SDK sealed behind it"
```

---

## Task 4: `AddNoofTelegram`

**Files:**
- Create: `src/Noof.Ledger.Telegram/TelegramRegistration.cs`
- Modify: `src/Noof.Ledger.Telegram/Noof.Ledger.Telegram.csproj` (`InternalsVisibleTo`)
- Modify: every other `src/Noof.Ledger.Telegram/*.cs`
- Modify: `src/Noof.Ledger.Host/Program.cs`
- Modify: `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`

**Interfaces:**
- Consumes: `AddNoofPersistence` (Task 2) — `TelegramOwnerGate` and `TelegramUpdateOffsetStore` take `ISecretStore`.
- Produces: `public static IServiceCollection AddNoofTelegram(this IServiceCollection services)` in namespace `Noof.Ledger.Telegram`. No `IConfiguration` parameter — Telegram reads no configuration section; its token comes from `ISecretStore` at runtime.

- [ ] **Step 1: Write the failing test**

In `tests/Noof.Ledger.Telegram.Tests/TelegramRegistrationTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Secrets;
using NSubstitute;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramRegistrationTests
{
    [Fact]
    public void AddNoofTelegram_registers_the_notifier_the_router_and_the_poller()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        // TelegramPollingService takes IConfiguration (it reads Capture:TimeZone per update). The
        // Host always has one; a bare ServiceCollection does not, and GetServices<IHostedService>()
        // constructs the service, so without this the test fails on a missing dependency rather
        // than on the behaviour under test.
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped(_ => Substitute.For<ISecretStore>());
        services.AddScoped(_ => Substitute.For<Application.Capture.ICaptureStore>());
        services.AddNoofTelegram();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IChatNotifier>().Should().BeOfType<TelegramChatNotifier>();
        scope.ServiceProvider.GetRequiredService<ITelegramUpdateRouter>().Should().BeOfType<TelegramUpdateRouter>();
        provider.GetServices<IHostedService>().Should().ContainSingle()
            .Which.Should().BeOfType<TelegramPollingService>();
    }
}
```

Both constructors were read after this plan's first draft and the `ServiceCollection` above already covers them. For the record:

- `TelegramUpdateRouter(ICaptureStore, IChatNotifier, TelegramOwnerGate)` — the last two come from `AddNoofTelegram` itself.
- `TelegramPollingService(IServiceScopeFactory, ITelegramBotClientFactory, TelegramClientHandle, IConfiguration, TimeProvider, ILogger<TelegramPollingService>)`.
- `TelegramBotClientFactory(IHttpClientFactory)` — satisfied by the `AddHttpClient` call inside `AddNoofTelegram`.

No Host type appears in any of them, so the layering risk this plan flagged is closed. If you nonetheless find a constructor dependency that reaches into `Noof.Ledger.Host`, **stop** — that is a layering violation and it needs reporting, not a workaround.

Note `TelegramUpdateRouter.ReceiptAcknowledgement` is a `public const` that `Telegram.Tests` asserts against; making the class `internal` keeps it reachable through `InternalsVisibleTo`.

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
Expected: FAIL to compile — `AddNoofTelegram` does not exist.

- [ ] **Step 3: Write the registration**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Chat;

namespace Noof.Ledger.Telegram;

public static class TelegramRegistration
{
    const string HttpClientName = "telegram";

    public static IServiceCollection AddNoofTelegram(this IServiceCollection services)
    {
        // A log-level filter is a suppression a more specific configured category can override at
        // runtime - Logging:LogLevel:System.Net.Http.HttpClient.telegram.LogicalHandler beats a
        // filter on the shorter prefix and puts the full request URI, bot token included, at
        // Information. Removing the logging handlers means there is nothing left to re-enable.
        services.AddHttpClient(HttpClientName).RemoveAllLoggers();

        services.AddSingleton<TelegramClientHandle>();
        services.AddSingleton<ITelegramBotClientFactory, TelegramBotClientFactory>();
        services.AddSingleton<IChatNotifier, TelegramChatNotifier>();
        services.AddScoped<TelegramOwnerGate>();
        services.AddScoped<TelegramUpdateOffsetStore>();
        services.AddScoped<ITelegramUpdateRouter, TelegramUpdateRouter>();
        services.AddHostedService<TelegramPollingService>();

        return services;
    }
}
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Make everything else internal, add `InternalsVisibleTo`, rewrite the Host wiring**

`internal` on: `ITelegramBotClientFactory`, `ITelegramUpdateRouter`, `TelegramBackoff`, `TelegramBotClientFactory`, `TelegramChatNotifier`, `TelegramClientHandle`, `TelegramOwnerGate`, `TelegramPollResult`, `TelegramPollingService`, `TelegramUpdateOffsetStore`, `TelegramUpdateRouter`.

Add to `src/Noof.Ledger.Telegram/Noof.Ledger.Telegram.csproj`:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="Noof.Ledger.Telegram.Tests" />
  </ItemGroup>
```

In `Program.cs`, delete the `AddHttpClient("telegram").RemoveAllLoggers()` line with its comment and the seven Telegram registrations, replacing them with:

```csharp
builder.Services.AddNoofTelegram();
```

- [ ] **Step 6: Un-skip the concrete-service rule from Task 1**

All three infrastructure assemblies are done. In `PublicSurfaceTests`, remove the `Skip` argument so the fact runs:

```csharp
    [Fact]
    public void No_public_concrete_service_crosses_an_infrastructure_boundary()
```

and set the allowlist entry:

```csharp
        ["Noof.Ledger.Telegram"] = ["TelegramRegistration"],
```

- [ ] **Step 7: Watch the un-skipped rule fail before trusting it**

Temporarily change `internal sealed class TelegramChatNotifier` back to `public sealed class`, run the Architecture suite, and confirm `No_public_concrete_service_crosses_an_infrastructure_boundary` fails naming `Noof.Ledger.Telegram/TelegramChatNotifier`. Put it back to `internal` and confirm green.

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`

- [ ] **Step 8: Run the full suite**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: green, and one fewer skipped test than after Task 1.

- [ ] **Step 9: Commit**

```bash
git add src/Noof.Ledger.Telegram src/Noof.Ledger.Host/Program.cs tests
git commit -m "refactor(telegram): one public way in, and the bot token stays behind it"
```

---

## Task 5: The Host's own surface

`Noof.Ledger.Host` is referenced by nothing except its own tests, so **every one of its 14 public types can be internal.** This is the task that most directly answers "не нужно делать публичным на всякий случай".

**Files:**
- Create: `src/Noof.Ledger.Host/Workers/WorkerRegistration.cs`
- Modify: `src/Noof.Ledger.Host/Noof.Ledger.Host.csproj` (`InternalsVisibleTo`)
- Modify: every `src/Noof.Ledger.Host/**/*.cs`
- Modify: `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`

**Interfaces:**
- Consumes: `AddNoofPersistence` (Task 2), `AddNoofAi` (Task 3), `AddNoofTelegram` (Task 4).
- Produces: `internal static IServiceCollection AddNoofWorkers(this IServiceCollection services, CategorizationWorkerOptions options)` in namespace `Noof.Ledger.Host.Workers`.

- [ ] **Step 1: Write the failing test**

The behaviour is "the Host's public surface is empty". Task 1's allowlist test already expresses it — so the failing test here is the allowlist entry itself. In `PublicSurfaceTests.Allowed`:

```csharp
        ["Noof.Ledger.Host"] = [],
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`
Expected: FAIL, listing all 13 remaining public Host types.

- [ ] **Step 3: Make them internal**

`internal` on: `AuthSchemes`, `LocalOwnerHandler`, `PasswordHasherAdapter`, `UserCommand`, `AccountEndpoints`, `CaptureTimeZoneGuard`, `DataProtectionSetup`, `LoopbackGuard`, `CategorizationReply`, `CategorizationTickResult`, `CategorizationWorker`, `CategorizationWorkerOptions`, and the nested `ReplyLine` record inside `CategorizationReply`.

`Program.cs` ends with `public partial class Program;` — change it to:

```csharp
internal partial class Program;
```

`WebApplicationFactory<Program>` works with an internal entry point as long as the test assembly has access, which Step 4 grants. This is the documented pattern.

Add to `src/Noof.Ledger.Host/Noof.Ledger.Host.csproj`:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="Noof.Ledger.Host.Tests" />
    <InternalsVisibleTo Include="Noof.Ledger.E2E.Tests" />
  </ItemGroup>
```

- [ ] **Step 4: Group the worker registration**

Create `src/Noof.Ledger.Host/Workers/WorkerRegistration.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace Noof.Ledger.Host.Workers;

internal static class WorkerRegistration
{
    public static IServiceCollection AddNoofWorkers(
        this IServiceCollection services, CategorizationWorkerOptions options)
    {
        services.AddSingleton(options);
        services.AddHostedService(sp => new CategorizationWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            options,
            CategorizationWorker.CreateWorkerId(),
            sp.GetRequiredService<ILogger<CategorizationWorker>>()));

        return services;
    }
}
```

In `Program.cs`, replace the `AddSingleton(categorizationOptions)` line and the `AddHostedService(sp => new CategorizationWorker(...))` block with:

```csharp
builder.Services.AddNoofWorkers(categorizationOptions);
```

`categorizationOptions` is still built in `Program.cs` because `AddNoofPersistence` needs its `MaxAttempts` — keep the binding where Task 2 left it, above the `AddNoofPersistence` call.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: green. If `Host.Tests` or `E2E.Tests` fails to compile, the cause is a missing `InternalsVisibleTo` or an internal type leaking through a public test helper's signature — fix the test helper's accessibility (`internal` is allowed in tests; requirement 1 says so explicitly), never by re-widening `src`.

- [ ] **Step 6: Run the E2E suite too**

`E2E.Tests` is not in the solution test run. It gained `InternalsVisibleTo` in Step 3 and names `Program`, so it must be run before this task is called done.

Run: `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj`
Expected: 13 passed.

- [ ] **Step 7: Commit**

```bash
git add src/Noof.Ledger.Host tests
git commit -m "refactor(host): nothing references the host, so nothing in it is public"
```

---

## Task 6: The analyzer gate

Requirement 4, the half the compiler can enforce on every build.

**Files:**
- Modify: `.editorconfig`
- Modify: whatever source the newly-enabled rules flag
- Modify: `tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs` if package lists changed in Task 3

**Interfaces:**
- Consumes: the internal-by-default state produced by Tasks 2–5. Running this task earlier would bury the real findings under the ~22 `CA1515` hits those tasks resolve.
- Produces: a build that fails when a type could be internal but is not.

- [ ] **Step 1: Write the rules into `.editorconfig`**

`.editorconfig` today is five lines. Replace it with:

**Corrected 2026-09-22 after the first attempt at this task stopped and reported.** The original version of this step set `CA1515`'s widened `output_kind` globally, which flagged **64 types, 56 of them in `Domain` and `Application`** — the two assemblies whose public types *are* the cross-assembly contract, and which this plan's own state table says are deliberately unchanged. The rule cannot tell a contract from an oversight; the scoping below tells it.

Two glob facts, both verified by that implementer rather than assumed: `[src/Noof.Ledger.Domain/**/*.cs]` matches **nothing**, because `**` here requires at least one intervening directory and `Domain`'s files sit directly in the project root. The working form is `dir/**` with no trailing `/*.cs`. (`[tests/**/*.cs]` does work, because every test file sits one directory below `tests/` — but use the `dir/**` form throughout anyway, so nobody has to know that.)

Severity is scoped per file rather than `output_kind`, deliberately: `dotnet_diagnostic.*.severity` is definitively per-syntax-tree, whereas whether a `dotnet_code_quality.*` option is honoured per-directory is not something to bet a silent rule on.

```ini
root = true

[*.cs]
csharp_style_namespace_declarations = file_scoped
dotnet_diagnostic.IDE0161.severity = error

# Phase 1C requirement 1: "все классы интернал, если не нужны в других сборках".
# CA1515 defaults to executable output kinds only, which would leave Persistence, Ai and Telegram -
# the three assemblies this rule most needs to reach - silently unchecked.
dotnet_diagnostic.CA1515.severity = error
dotnet_code_quality.CA1515.output_kind = ConsoleApplication, DynamicallyLinkedLibrary

# An internal type nobody derives from is sealed; this is free once the types above are internal.
dotnet_diagnostic.CA1852.severity = error

# Cheap correctness rules that the AnalysisMode=All measurement showed cost fewer than a handful of
# fixes each. Enabled by name rather than by turning on the whole rulebook - see docs/BACKLOG.md.
dotnet_diagnostic.CA1862.severity = error
dotnet_diagnostic.CA1861.severity = error
dotnet_diagnostic.CA2263.severity = error

# An unused using is a dependency the file does not have. Task 3's implementer found these are
# NOT currently caught despite EnforceCodeStyleInBuild - see Step 1a below for why.
dotnet_diagnostic.IDE0005.severity = error

# Every narrowing section below this line. A property belongs to whichever section header precedes
# it, so anything added above [*.cs]'s end must stay above this comment or it silently becomes a
# rule about one directory instead of about the repository.

[src/Noof.Ledger.Domain/**]
# Domain and Application exist to be referenced. Their public types are the contract every other
# assembly is written against, so "could this be internal?" has a standing answer of no. The list
# is not unguarded - PublicSurfaceTests locks it, and adding to it is an edit a reviewer sees.
dotnet_diagnostic.CA1515.severity = none

[src/Noof.Ledger.Application/**]
dotnet_diagnostic.CA1515.severity = none

[src/Noof.Ledger.Persistence/Migrations/**]
# EF scaffolds migration classes public, with their own using directives, and rewrites both on the
# next scaffold. CLAUDE.md already exempts their generated companions from code style for the same
# reason; a rule that argues with a code generator is a rule that loses every time it runs.
dotnet_diagnostic.CA1515.severity = none
dotnet_diagnostic.CA1861.severity = none
dotnet_diagnostic.IDE0005.severity = none

[tests/**]
# Test names are sentences: Every_routable_page_declares_its_authorization. That is the convention
# throughout this repository and it is deliberate.
dotnet_diagnostic.CA1707.severity = none
# A test project's fixtures and helpers are reached by the xUnit runner through reflection, and
# several are public because xUnit requires it. Every test project is also OutputType=Exe under
# Microsoft Testing Platform, so without this line CA1515 would fire on all of them by default.
dotnet_diagnostic.CA1515.severity = none
```

**The ordering above is load-bearing and the first two attempts at this task both died on it.** In EditorConfig a property belongs to whichever section header precedes it, until the next header. The previous draft interleaved the narrowing sections into the middle of `[*.cs]`, which quietly moved `CA1852`, `CA1862`, `CA1861`, `CA2263` and `IDE0005` *inside* the migrations glob: `CA1861` contradicted itself three lines apart, and `CA1862`/`CA2263` never ran anywhere except in migration files, so both of the genuine violations Step 3 names produced **zero** errors. A rule scoped to the wrong section looks exactly like a rule with nothing to report. Keep every repository-wide rule above the comment line, and every narrowing below it.

- [ ] **Step 1a: Make `IDE0005` actually fire**

Setting its severity is not enough, and this is a real trap. `IDE0005` is reported by the compiler rather than by an analyzer, and **it is silently inert during a build unless the project generates a documentation file** — a long-standing .NET SDK quirk. Task 3's implementer confirmed the symptom here: two genuinely unused `using` directives in `Program.cs` did not fail the build despite `EnforceCodeStyleInBuild=true`.

Turning the documentation file on brings `CS1591` ("missing XML comment for publicly visible type or member") with it, which under `TreatWarningsAsErrors` would demand XML docs on every public member — exactly what `CLAUDE.md` §3 forbids. So suppress it in the same breath. In `Directory.Build.props`:

```xml
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
```

If this turns out to cost more than it is worth — for instance if the documentation file breaks the Razor or Host build in a way that is not a one-line fix — **drop `IDE0005` and this property, and say so**. It is the least important rule in this task; the accessibility rules are the point.

- [ ] **Step 2: Build and read what it says**

Run: `dotnet build NoofLedger.slnx`
Expected: FAIL with `CA1515` on any `src` type that is still public without needing to be, plus `CA1852`, `CA1862`, `CA1861`, `CA2263` and `IDE0005` findings.

While you are here, delete the dead `<PackageVersion Include="TngTech.ArchUnitNET.xUnitV3" ... />` entry from `Directory.Packages.props` — no project references it, verified by grep. A pinned version for a package nobody uses is a maintenance claim on something that does not exist.

- [ ] **Step 3: Fix every finding, or reject it in writing**

For each diagnostic, exactly one of:
- **Fix it.** Narrow the accessibility, seal the type, use the `StringComparison` overload.
- **Reject it in `.editorconfig`** with a comment giving the reason, in the same voice as the entries above. A rejection with no reason is not allowed.

**The full finding list was measured during the first attempt at this task, so you are not walking in blind.** With the scoping above in place, expect exactly this:

| Rule | Findings | What to do |
|---|---|---|
| `CA1515` | 4: `LedgerConnectionString`, `PersistenceRegistration`, `AiRegistration`, `TelegramRegistration` | Suppress **at the declaration** — `[SuppressMessage("Maintainability", "CA1515", Justification = "...")]` — never globally. All four are genuinely named by another assembly, so the rule is simply wrong about them, and a file-local suppression keeps it live everywhere else. Write a real justification in each; "suppressing CA1515" is not one. |
| `CA1852` | 0 | Nothing to do. |
| `CA1861` | 2, both in `Migrations/20260921065115_AddCaptureModel.cs` | Silenced by the `Migrations/**` section. If any appears **outside** `Migrations/`, fix it. |
| `CA1862` | 1, `src/Noof.Ledger.Persistence/Auth/EfUserStore.cs:10` — `u.Username.ToLower() == username.ToLower()` | A genuine fix: use the `StringComparison` overload. |
| `CA2263` | 1, `tests/Noof.Ledger.Persistence.Tests/CaptureModelTests.cs:41` — `new[] { nameof(Category.Slug) }` into `SequenceEqual` | A genuine fix: use the generic overload. |
| `IDE0005` | **unmeasured outside `Migrations/`** | 4 were seen inside migration files and are now silenced there. Nobody has yet seen what it reports across the rest of the tree, because both previous attempts stopped before that point. Report the real count. If it exceeds ~15, stop and say so — Step 1a's escape hatch exists precisely for this. |

If your build disagrees with this table, **the table is the claim under test** — report the difference rather than quietly adapting. Two failure shapes to name explicitly: a count that *grew* means the narrowing sections are not matching what they were meant to, and a count that *fell to zero for a rule the table says has findings* means that rule is scoped into the wrong section and is silently inert. The second is the more dangerous of the two and has already happened twice on this task.

- [ ] **Step 4: Build clean**

Run: `dotnet build NoofLedger.slnx`
Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 5: Run the full suite and the E2E suite**

Run: `dotnet test --solution NoofLedger.slnx`
Run: `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj`
Expected: green and 13 passed.

- [ ] **Step 6: Commit**

```bash
git add .editorconfig src tests
git commit -m "build: make 'could this be internal?' a build error, not a code review"
```

---

## Task 7: The ReSharper sweep

Requirement 4's explicit question — «Возможно ли добавить решарпер анализатор?» — and the answer is yes, with one honest caveat. No Roslyn analyzer, and none of `Roslynator`, `Meziantou.Analyzer` or `SonarAnalyzer.CSharp`, has a rule for "this public **member** is never called from outside its assembly". That analysis needs a whole-solution view. ReSharper's `MemberCanBePrivate.Global` and `MemberCanBeInternal` have it, and `JetBrains.ReSharper.GlobalTools` exposes them on the command line.

**Files:**
- Create: `NoofLedger.sln.DotSettings`
- Create: `.config/dotnet-tools.json`
- Create: `ops/inspect.ps1`
- Modify: `ops/RUNBOOK.md`, `.gitignore`

**Interfaces:**
- Produces: `ops/inspect.ps1`, which writes `artifacts/inspect/report.xml` and exits non-zero if any ERROR-severity finding is present. Task 8 consumes that report.

- [ ] **Step 1: Pin the tool**

Run: `dotnet new tool-manifest` then `dotnet tool install JetBrains.ReSharper.GlobalTools`

Expected: `.config/dotnet-tools.json` exists and names the tool with a concrete version. Commit the manifest — an unpinned tool is not a reproducible gate.

**Already verified while planning**, so do not re-discover it: version **2026.2.2** installs cleanly and its banner reports `Running on x64 OS ... .NET 10.0.12 under Microsoft Windows 10.0.26200`. Its real flag surface, read from `jb inspectcode --help` on this machine:

| Flag | Note |
|---|---|
| `--output` / `-o` | file path, or `-` for stdout |
| `--format` / `-f` | `Xml, Html, Text, Sarif` — **the default is Sarif, not Xml.** The script below parses XML, so `-f=Xml` is mandatory, not decorative. |
| `--sEverity` / `-e` | `INFO, HINT, SUGGESTION, WARNING, ERROR`, default `SUGGESTION`. Spelled with the odd capital in the help text; **use the `-e` short form** rather than guessing the long form's case handling. |
| `--settings` / `-s` | "default: Use R#'s solution shared settings if exists" — which means the committed `NoofLedger.sln.DotSettings` from Step 2 is picked up **automatically**. Do not pass `-s`. |
| `--no-build` | present; requires the solution to be built already |
| `--caches-home` | present |
| `--swea` / `--no-swea` | solution-wide analysis, on by default. `MemberCanBePrivate.Global` *needs* it — never pass `--no-swea`. |

There is no documented exit code for "findings were found", which is why the report is the gate.

- [ ] **Step 2: Write the team-shared settings layer**

Rider names its settings layer after the solution's **base** name, which is why `NoofLedger.sln.DotSettings.user` already sits next to `NoofLedger.slnx`. The shared layer is the same name without `.user`, and it is committed. Create `NoofLedger.sln.DotSettings`:

```xml
<wpf:ResourceDictionary xml:space="preserve" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:s="clr-namespace:System;assembly=mscorlib" xmlns:ss="urn:shemas-jetbrains-com:settings-storage-xaml" xmlns:wpf="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
	<s:String x:Key="/Default/CodeInspection/Highlighting/InspectionSeverities/=MemberCanBePrivate_002EGlobal/@EntryIndexedValue">ERROR</s:String>
	<s:String x:Key="/Default/CodeInspection/Highlighting/InspectionSeverities/=MemberCanBeInternal/@EntryIndexedValue">ERROR</s:String>
	<s:String x:Key="/Default/CodeInspection/Highlighting/InspectionSeverities/=ClassCanBeSealed_002EGlobal/@EntryIndexedValue">ERROR</s:String>
	<s:String x:Key="/Default/CodeInspection/Highlighting/InspectionSeverities/=UnusedMember_002EGlobal/@EntryIndexedValue">ERROR</s:String>
	<s:String x:Key="/Default/CodeInspection/Highlighting/InspectionSeverities/=UnusedType_002EGlobal/@EntryIndexedValue">ERROR</s:String>
</wpf:ResourceDictionary>
```

Dots inside an inspection id are escaped as `_002E` — `MemberCanBePrivate.Global` becomes `MemberCanBePrivate_002EGlobal`. This file makes the same rules light up in Rider for anyone who opens the solution, which is most of the value even if Step 4 turns out to be infeasible.

The personal `.user` layer is already ignored — `.gitignore` line 6 is `*.user`, verified — so no `.gitignore` change is needed here. The existing `NoofLedger.sln.DotSettings.user` names Rider 2026.2, which is the IDE version the pinned 2026.2.2 tool matches; that alignment is why the shared layer is expected to be understood by both.

- [ ] **Step 3: Write the runner**

Create `ops/inspect.ps1`:

```powershell
#requires -Version 7
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

$reportDir = Join-Path 'artifacts' 'inspect'
$report = Join-Path $reportDir 'report.xml'
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null

dotnet tool restore
dotnet jb inspectcode NoofLedger.slnx -o="$report" -f=Xml -e=WARNING --caches-home="$reportDir/caches" --no-build

if (-not (Test-Path $report)) {
    Write-Error "inspectcode produced no report at $report"
}

# InspectCode's own exit code does not reflect whether it found anything, so the report is the
# gate. Verified by running it against a solution with known findings.
[xml]$xml = Get-Content $report
$errors = $xml.SelectNodes('//Issue[@Severity="ERROR"]')

foreach ($issue in $errors) {
    Write-Host "$($issue.File):$($issue.Line) $($issue.TypeId) $($issue.Message)"
}

if ($errors.Count -gt 0) {
    Write-Error "$($errors.Count) ERROR-severity inspection finding(s). See $report."
}

Write-Host "No ERROR-severity findings."
```

`--no-build` requires the solution to be built first; the script assumes you have run `dotnet build`. Say so in the RUNBOOK entry.

- [ ] **Step 4: Run it, and report honestly if it does not work**

Run: `dotnet build NoofLedger.slnx` then `powershell -ExecutionPolicy Bypass -File ops/inspect.ps1`

Two things remain unverified after the planning checks above, and either may fail: whether `jb inspectcode` accepts a `.slnx` at all, and what its XML element and attribute names actually are (the script assumes `Issue` with `Severity`, `File`, `Line`, `TypeId`, `Message`). The settings-layer question is answered — `--settings` defaults to the solution's shared layer, so Step 2's file is picked up without a flag.

A useful debugging aid if severities look wrong: `--dumpIssuesTypes` (`-it`) prints the issue types the tool knows about, which is the fastest way to confirm an inspection id in `NoofLedger.sln.DotSettings` is spelled the way ReSharper expects.

- **If it runs:** inspect `artifacts/inspect/report.xml` yourself, correct the XPath and attribute names in the script to match what the tool actually emits, and re-run until the script's output agrees with the report.
- **If `.slnx` is rejected:** try pointing it at the individual `.csproj` files instead and adjust the script.
- **If it cannot be made to work at all:** do not fake it. Delete `ops/inspect.ps1` and `.config/dotnet-tools.json`, keep `NoofLedger.sln.DotSettings` (which still gives every Rider user the rules), and record the failure with its exact error in `docs/BACKLOG.md` under a heading naming the tool and version. Then note in your report that Task 8 must be done by reading Rider's own inspection results instead.

Add `artifacts/` to `.gitignore` if not already there — it is, per `CLAUDE.md`, but confirm.

- [ ] **Step 5: Document it in the RUNBOOK**

Append to `ops/RUNBOOK.md` a short section: what `ops/inspect.ps1` does, that it needs a build first, that it is a periodic sweep rather than a per-build gate (because the tool needs a full solution load and its runtime is measured in minutes), and what to do with a finding.

- [ ] **Step 6: Commit**

```bash
git add .config NoofLedger.sln.DotSettings ops .gitignore docs
git commit -m "build: add the solution-wide accessibility sweep Roslyn cannot do"
```

---

## Task 8: The member sweep

Requirement 1's second sentence: «все методы приватные если нет необходимости в обратном». Task 7 produced the list; this task works through it.

**Files:**
- Modify: whatever `src/**/*.cs` the report names.

**Interfaces:**
- Consumes: `artifacts/inspect/report.xml` from Task 7, or Rider's inspection results if Task 7's Step 4 fell back.

- [ ] **Step 1: Produce the list**

Run: `powershell -ExecutionPolicy Bypass -File ops/inspect.ps1`

Collect every `MemberCanBePrivate.Global`, `MemberCanBeInternal`, `UnusedMember.Global` and `UnusedType.Global` finding in `src/`. Ignore findings in `tests/` — requirement 1 exempts tests.

- [ ] **Step 2: Narrow each one, in small commits per assembly**

For each finding, one of:
- **Narrow it** to `private`, `private protected` or `internal`.
- **Leave it and say why** in a one-line `why` comment — for example, a member an EF entity needs for materialisation, or one xUnit reaches by reflection.

Do not delete `UnusedMember.Global` findings on sight. An unused public member may be a real gap in coverage rather than dead code; if a member is genuinely unreferenced and untested, say so in your report and leave the deletion decision to the operator.

Watch for the trap: narrowing a member that a test uses breaks the test's compilation, and the fix is `InternalsVisibleTo` (already granted per assembly by Tasks 2–5) plus `internal` — not reverting to `public`.

- [ ] **Step 3: Build and run everything**

Run: `dotnet build NoofLedger.slnx`
Run: `dotnet test --solution NoofLedger.slnx`
Run: `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj`
Expected: 0 warnings, solution suite green, 13 E2E passed.

- [ ] **Step 4: Re-run the sweep and confirm it is clean**

Run: `powershell -ExecutionPolicy Bypass -File ops/inspect.ps1`
Expected: "No ERROR-severity findings."

- [ ] **Step 5: Commit**

```bash
git add src
git commit -m "refactor: narrow every member the solution-wide sweep could prove private"
```

---

## Task 9: Close the phase

`CLAUDE.md` §6: a phase is finished when the next person can pick it up without rediscovering what it cost.

**Files:**
- Modify: `CLAUDE.md`, `README.md`, `docs/BACKLOG.md`, `ops/RUNBOOK.md`

- [ ] **Step 1: Write the rule that outlived the phase into `CLAUDE.md`**

Add to §4 under **Architecture**, keeping it to three lines — `CLAUDE.md` is read in full every session and length is a tax on all of them:

```markdown
- **Minimum accessibility.** A type is `internal` unless another assembly names it; a member is
  `private` unless something outside its type calls it. Tests reach internals through
  `InternalsVisibleTo`, never by widening `src` — and substituting an internal interface needs
  `InternalsVisibleTo("DynamicProxyGenAssembly2")` on the declaring assembly as well, or
  NSubstitute fails at runtime. `PublicSurfaceTests` holds each assembly's allowlist; widening it
  is an edit to that file, which is the point.
- **Each assembly registers its own services**, exposing one `AddNoofXxx(this IServiceCollection)`
  the Host calls. `Program.cs` names no implementation type.
```

- [ ] **Step 2: Update the status block**

`CLAUDE.md`'s status block says "Phases 0, 0b, 1A and 1B complete" and "452 solution tests". Both are now stale. Say Phase 1C is complete, give the real test count from Task 8's run, and keep the "next is Phase 2" sentence.

- [ ] **Step 3: Update `README.md`**

The README's status block describes what works. Phase 1C changed no behaviour, so most of it stays true — but if it describes the project layout or claims anything about extensibility, check it against the tree. A public README that claims a guarantee the code stopped providing is worse than no README.

- [ ] **Step 4: Bank the measurements in `docs/BACKLOG.md`**

Three entries, each with enough reasoning that nobody re-proposes it as new:

1. **`AnalysisMode=All` was measured and rejected** — 842 warnings across 31 rules, the breakdown from this plan's "Decisions already taken" section, and the note that Task 6 enabled a named list instead.
2. **`Microsoft.CodeAnalysis.PublicApiAnalyzers` was considered and deferred** — what it would buy (member-level build-time surface locking), what it costs (a hand-maintained `PublicAPI.Unshipped.txt` per project), and that `PublicSurfaceTests` covers the type level today.
3. **If Task 7 Step 4 fell back**, the `jb inspectcode` failure with its exact error and version.

Also correct the existing entry "The read model's `currentZone` is never given a zone other than UTC" — Task 2 changed how that zone is supplied.

- [ ] **Step 5: Run everything one last time, on the tree you are about to integrate**

Run: `dotnet build NoofLedger.slnx`
Run: `dotnet test --solution NoofLedger.slnx`
Run: `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj`
Run: `powershell -ExecutionPolicy Bypass -File ops/publish.ps1`

Expected: 0 warnings; solution suite green with the 8 live tests skipped; 13 E2E passed; publish gate passes. State the actual numbers.

- [ ] **Step 6: Commit and leave nothing uncommitted**

```bash
git add CLAUDE.md README.md docs ops
git commit -m "docs: what Phase 1C settled about accessibility and composition"
git status --short
```

`git status --short` must print nothing.

- [ ] **Step 7: Hand the integration decision to the operator**

Use `superpowers:finishing-a-development-branch`. Do not merge without being told which option.

---

## Self-review notes

**Requirement coverage:**

| Requirement | Tasks |
|---|---|
| 1 — minimal accessibility, types | 1 (lock), 2, 3, 4, 5 (the work), 6 (`CA1515` enforces it on every build) |
| 1 — minimal accessibility, members | 7 (tooling), 8 (the sweep) |
| 2 — cross-assembly services are interfaces | 1 (`No_public_concrete_service_crosses_an_infrastructure_boundary`), 2, 3, 4 |
| 3 — each assembly registers itself | 2, 3, 4, 5 |
| 4 — analyzer validation | 6 (Roslyn, every build), 7 (ReSharper, periodic sweep) |

**Known risks, stated rather than hidden:**

- **Task 2 Step 7** — an internal `LedgerDbContext` may break `dotnet ef`. Mitigated by an explicit verification step with a stated fallback rather than a discovery mid-refactor.
- **Task 7 Step 4** — three unverified assumptions about `jb inspectcode`. Mitigated by a written fallback that still delivers the Rider-side value.
- ~~**Task 4 Step 1** — `TelegramUpdateRouter`'s and `TelegramPollingService`'s constructor dependencies were not read while writing this plan.~~ **Closed before execution.** Both were read; neither reaches into the Host. The one real finding was that `TelegramPollingService` takes `IConfiguration`, which a bare `ServiceCollection` does not provide — Task 4's test now registers one, which it would otherwise have failed on for the wrong reason.
- **Task 6 Step 3** — the exact `CA1852`/`CA1862`/`CA1861`/`CA2263` findings were measured in aggregate (fewer than a handful each) but not read individually. The step's instruction is "fix or reject in writing", which is correct either way.
