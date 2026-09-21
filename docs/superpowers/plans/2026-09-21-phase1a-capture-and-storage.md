# Phase 1A — Capture and Durable Storage: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Telegram message becomes a durable, uncategorised ledger capture with a queued job, using secrets the operator pasted into the UI — with nothing lost when the network is down.

**Architecture:** Phase 1 is split in two plans. **1A (this plan) is everything up to the model call**: secrets encrypted at rest, a settings page to enter them, a Telegram long-polling receiver, an atomic capture that writes a transaction and its categorisation job in one database transaction, and a PostgreSQL job queue that claims work with `FOR UPDATE SKIP LOCKED`. **1B is everything downstream**: the Anthropic client, the joint extraction/categorisation/merchant contract, the worker that fulfils jobs and edits the Telegram reply, and the dashboard. Splitting the document does not shrink Phase 1 — all four capture-path contracts still land in this phase, which is what OPEN-QUESTIONS Q5 settled. It means 1A is verifiable end to end on its own before anything depends on a model.

**Tech Stack:** .NET 10 · C# · EF Core 10 + Npgsql · PostgreSQL 18 · ASP.NET Core Data Protection (DPAPI) · Telegram.Bot 22.10.3.1 · Blazor Server (RCL + Host) · xUnit v3 on Microsoft Testing Platform · AwesomeAssertions

**Spec:** `docs/superpowers/specs/2026-09-19-noof-finance-design.md` · decisions in `docs/OPEN-QUESTIONS.md` (see "Phase 1 decisions — taken 2026-09-21")

## Global Constraints

- **Money is `decimal` + `Currency`.** Never `double`, never `float`. `numeric(19,4)` in PostgreSQL, `timestamptz` for every `DateTimeOffset` — both enforced by a global convention in `LedgerDbContext`.
- **No number in user-facing output ever originates from a model.** The model quotes a substring; C# asserts it occurs verbatim in the stored raw text and parses it. `QuotedAmount.TryResolve` is that gate.
- **Secrets are encrypted in the database and entered through the UI.** Never in `appsettings.json`, never in the repo, never in a log, an exception message, or an LLM prompt. This is a public repository.
- **`Noof.Ledger.Domain` has zero NuGet references**, asserted by a test.
- **`Noof.Ledger.Web` is UI only** — no `DbContext`, no EF types, no `HttpClient`, no `Program.cs`. Enforced by `DisableTransitiveProjectReferences` plus an architecture test whose reference and package sets are EXACT, not subsets.
- **Never call `EnsureCreated()`**, anywhere, including test helpers. Tests run against real PostgreSQL; the EF InMemory provider is banned.
- **TDD**: a failing test first, for all behaviour. Exempt: migrations, DTOs, `Program.cs` wiring.
- **File-scoped namespaces.** `IDE0161` and `TreatWarningsAsErrors` are compile errors. A freshly scaffolded migration `.cs` fails the build until converted; leave its `.Designer.cs` and the model snapshot exactly as EF emits them.
- **Central Package Management.** An inline `Version=` is NU1008. `NuGetAuditMode=all` with `NU1903`/`NU1904` as errors.
- **`TimeProvider` everywhere time is read.** `DateTimeOffset.UtcNow` must not appear in production code. Tests advance a `FakeTimeProvider` rather than sleeping.
- **`global.json` must keep `{"test":{"runner":"Microsoft.Testing.Platform"}}`** or `dotnet test` fails outright. Positional test paths are rejected; use `--project` or `--solution`.
- **Do not add** MediatR, AutoMapper, generic repositories over `DbContext`, CQRS scaffolding, or a naming-convention package.

## What this plan deliberately does NOT do

Stating these prevents a well-meaning implementer from building them and a reviewer from flagging them as gaps.

- **No parser, tokenizer or currency-alias table.** Extraction is the model's job, by user decision. `QuotedAmount` verifies and parses a quote; it does not find one.
- **No `DeterministicOfflineCategorizer`.** The spec asks for two categoriser implementations from day one, which made sense when extraction was deterministic. With LLM extraction there is nothing to categorise offline; the offline guarantee is durable raw capture plus retry.
- **No category management UI.** Moved to `docs/BACKLOG.md` by user decision. The schema supports renaming, re-parenting and sub-categories from the first migration — that is the half that is expensive to retrofit. A CRUD page over a schema that already supports the operations costs the same in any later phase.
- **No change to the PostgreSQL credential.** Deferred by explicit instruction to a later hardening phase.
- **No dashboard.** Plan 1B.

## File structure

| File | Responsibility |
|---|---|
| `src/Noof.Ledger.Domain/TransactionStatus.cs` · `CategorizationAuthority.cs` · `MerchantKind.cs` · `JobStatus.cs` | Persisted enums; their integer values are part of the schema contract |
| `src/Noof.Ledger.Domain/MerchantName.cs` | `Fold()` — the sole definition of merchant identity |
| `src/Noof.Ledger.Domain/QuotedAmount.cs` | Quote-and-verify: the boundary where a model's claim becomes a C#-computed number |
| `src/Noof.Ledger.Domain/{Wallet,Category,Merchant,MerchantAlias,Transaction,LineItem,CategorizationJob}.cs` | The ledger entities, one per file |
| `src/Noof.Ledger.Application/Secrets/{ISecretStore,SecretResult,SecretState,SecretKeys}.cs` | The secrets port and its fixed key names |
| `src/Noof.Ledger.Application/Capture/{ICaptureStore,CapturedMessage}.cs` | The atomic-capture port |
| `src/Noof.Ledger.Application/Jobs/IJobQueue.cs` | The queue port |
| `src/Noof.Ledger.Application/Chat/IChatNotifier.cs` | Send and edit, so nothing outside Telegram knows about Telegram |
| `src/Noof.Ledger.Persistence/Configurations/*.cs` | One `IEntityTypeConfiguration<T>` per entity; snake_case lives here |
| `src/Noof.Ledger.Persistence/Auth/` · `Secrets/EfSecretStore.cs` · `Capture/EfCaptureStore.cs` · `Jobs/PostgresJobQueue.cs` | EF implementations of the ports |
| `src/Noof.Ledger.Telegram/{TelegramNotifier,TelegramPoller,TelegramOptions}.cs` | The only project that references `Telegram.Bot` |
| `src/Noof.Ledger.Web/Components/Pages/Settings/Secrets.razor` | The one page that accepts a typed secret |
| `src/Noof.Ledger.Host/Startup/*.cs` | Per-concern `AddXxx()` extensions, so `Program.cs` stays a flat list as it grows |

---

### Task 1: Domain — enums, Fold and quote-and-verify

**Files:**
- Create: `src/Noof.Ledger.Domain/TransactionStatus.cs`
- Create: `src/Noof.Ledger.Domain/CategorizationAuthority.cs`
- Create: `src/Noof.Ledger.Domain/MerchantKind.cs`
- Create: `src/Noof.Ledger.Domain/JobStatus.cs`
- Create: `src/Noof.Ledger.Domain/MerchantName.cs`
- Create: `src/Noof.Ledger.Domain/QuotedAmount.cs`
- Create: `tests/Noof.Ledger.Domain.Tests/CategorizationAuthorityTests.cs`
- Create: `tests/Noof.Ledger.Domain.Tests/MerchantNameTests.cs`
- Create: `tests/Noof.Ledger.Domain.Tests/QuotedAmountTests.cs`
- Nothing existing is modified. `src/Noof.Ledger.Domain/CurrencyCode.cs` and `Money.cs` (from Phase 0) are read but not touched.

**Interfaces — produced, exact:**
```csharp
public enum TransactionStatus { Captured = 0, Completed = 1, Failed = 2 }
public enum CategorizationAuthority { None = 0, Model = 1, Rule = 2, User = 4 }
public enum MerchantKind { Retail = 0, Service = 1, ExchangeVenue = 2 }
public enum JobStatus { Pending = 0, Claimed = 1, Succeeded = 2, Failed = 3 }

public static class MerchantName
{
    public static string Fold(string raw);
}

public static class QuotedAmount
{
    public static bool TryResolve(string rawText, string? amountQuote, string? currencyCode,
                                  out Money money, out string failure);
}
```

**Design decisions locked by this task** (the contract leaves these open; once this task's tests are green they are load-bearing for every later task that calls `QuotedAmount.TryResolve` — do not silently change them later):

1. **The separator rule.** When a quote contains both `.` and `,`, whichever one occurs *last* (rightmost) is the decimal point and every other occurrence of either character is a thousands grouping mark — this is what makes `"11.700,50"` and `"11,700.50"` agree. When a quote contains only one kind of separator and there are **exactly 3 digits after its last occurrence**, it is treated as a thousands group, not a decimal point — `"1.700"` resolves to `1700`, not `1.7`. This holds because every currency this app supports (EUR, RSD, USD, RUB, KZT) is quoted to at most 2 decimal digits, so 3 trailing digits after a lone separator can never be a genuine fraction in this domain.
2. **"Unknown currency" means "not one of `CurrencyCode`'s five known statics."** `CurrencyCode`'s constructor (Phase 0, unchanged) only validates *shape* (3 ASCII letters) — it happily builds `new CurrencyCode("ZZZ")`. Since Domain has no reference-data table of real ISO codes yet, and `CurrencyCode.cs` is out of scope for this task, `QuotedAmount` keeps its own list of the 5 codes `CurrencyCode` already exposes (`Eur`, `Rsd`, `Usd`, `Rub`, `Kzt`) and rejects anything else, including well-formed-but-foreign codes.
3. A negative quote (`"-250"`) **fails** rather than resolving to a negative `Money` — a captured expense is never quoted as negative, so a leading `-` is treated as a sign that the quote is not a real amount.

> **Reviewer fixes applied (verified 2026-09-21 against this exact repo checkout), both confirmed by compiling and running the code below, not just reading it:**
> 1. **`SpaceGroupers` and its test were three/two copies of the same character.** As originally drafted, the implementation's `SpaceGroupers` array and the test's two `[InlineData]` rows for the thin-space/non-breaking-space case were all literal space glyphs that are byte-identical to a plain ASCII space (U+0020) — indistinguishable on screen and through copy-paste from markdown/JSON, which is exactly how they ended up identical. Run against real Telegram-style input, the original code **fails to strip a genuine U+2009 or U+00A0**, contradicting its own comment, while the test could never have caught this because both its rows collapsed to the one case that worked by accident. Fixed below by writing the three/two characters as explicit `\u` escapes everywhere they appear, which is now mandatory practice for any non-ASCII literal in this codebase's whitespace-handling code — never a raw glyph.
> 2. ~~A claimed repo-wide `dotnet test` defect.~~ **Withdrawn — the claim was false.** Verified directly after the drafting run: `dotnet test --project tests/Noof.Ledger.Domain.Tests/Noof.Ledger.Domain.Tests.csproj` reports 39/39 and `dotnet test --solution NoofLedger.slnx` reports 136/136, both exit 0. The reviewer observed sixteen drafting agents running builds concurrently against one shared `artifacts/` output directory, which is an artefact of how that drafting run was orchestrated, not a property of this repository. **Use `dotnet test` as every other plan in this project does.**


- [ ] **Step 1: Write the failing test that pins `CategorizationAuthority`'s persisted values**

Create `tests/Noof.Ledger.Domain.Tests/CategorizationAuthorityTests.cs`:

```csharp
using AwesomeAssertions;

namespace Noof.Ledger.Domain.Tests;

public class CategorizationAuthorityTests
{
    [Theory]
    [InlineData(CategorizationAuthority.None, 0)]
    [InlineData(CategorizationAuthority.Model, 1)]
    [InlineData(CategorizationAuthority.Rule, 2)]
    [InlineData(CategorizationAuthority.User, 4)]
    public void Underlying_value_is_pinned(CategorizationAuthority value, int expected)
    {
        ((int)value).Should().Be(expected);
    }
}
```

> `User = 4`, not `3`. These values are stored as ints in PostgreSQL and later tasks compare them with a SQL predicate (bitwise or otherwise) — if a future edit reorders the enum and this test isn't here, the column silently starts meaning something different for every existing row.

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Domain.Tests/Noof.Ledger.Domain.Tests.csproj`
Expected: build error — `error CS0246: The type or namespace name 'CategorizationAuthority' could not be found`. (Confirmed: this exact message is what `dotnet run` surfaces for a missing type in this project.)

- [ ] **Step 3: Write the minimal enum**

Create `src/Noof.Ledger.Domain/CategorizationAuthority.cs`:

```csharp
namespace Noof.Ledger.Domain;

public enum CategorizationAuthority
{
    None = 0,
    Model = 1,
    Rule = 2,
    User = 4,
}
```

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test --project tests/Noof.Ledger.Domain.Tests/Noof.Ledger.Domain.Tests.csproj`
Expected: PASS (4 theory cases) — the xunit.v3 in-process runner prints a single `Total:` line for the whole assembly, including the pre-existing Phase 0/0b tests in this project; look for `Errors: 0, Failed: 0` alongside a total that grew by 4 relative to before this step.

- [ ] **Step 5: Commit**

```bash
git add src/Noof.Ledger.Domain/CategorizationAuthority.cs tests/Noof.Ledger.Domain.Tests/CategorizationAuthorityTests.cs
git commit -m "feat(domain): CategorizationAuthority, its persisted ints pinned by test"
```

- [ ] **Step 6: Write the three remaining enums**

These are plain data declarations with no branching logic — same category as a DTO, so there is no test-first cycle for them individually (only `CategorizationAuthority` needed pinning, because only its values are compared by SQL predicate per the contract's own comment). Note for whoever plans the `IJobQueue` task: `JobStatus.Pending` will very likely appear in a `WHERE`/`FOR UPDATE SKIP LOCKED` predicate too — if that task doesn't add its own pinning test for `JobStatus`'s ints at that point, the same silent-reorder risk this task calls out for `CategorizationAuthority` applies to it unpinned.

Create `src/Noof.Ledger.Domain/TransactionStatus.cs`:

```csharp
namespace Noof.Ledger.Domain;

public enum TransactionStatus
{
    Captured = 0,
    Completed = 1,
    Failed = 2,
}
```

Create `src/Noof.Ledger.Domain/MerchantKind.cs`:

```csharp
namespace Noof.Ledger.Domain;

public enum MerchantKind
{
    Retail = 0,
    Service = 1,
    ExchangeVenue = 2,
}
```

Create `src/Noof.Ledger.Domain/JobStatus.cs`:

```csharp
namespace Noof.Ledger.Domain;

public enum JobStatus
{
    Pending = 0,
    Claimed = 1,
    Succeeded = 2,
    Failed = 3,
}
```

- [ ] **Step 7: Build and confirm nothing broke**

Run: `dotnet test --project tests/Noof.Ledger.Domain.Tests/Noof.Ledger.Domain.Tests.csproj`
Expected: PASS, same test count as Step 4 — these three files add no new tests, they just need to compile.

- [ ] **Step 8: Commit**

```bash
git add src/Noof.Ledger.Domain/TransactionStatus.cs src/Noof.Ledger.Domain/MerchantKind.cs src/Noof.Ledger.Domain/JobStatus.cs
git commit -m "feat(domain): TransactionStatus, MerchantKind and JobStatus enums"
```

- [ ] **Step 9: Write the failing test matrix for `MerchantName.Fold`**

Create `tests/Noof.Ledger.Domain.Tests/MerchantNameTests.cs`:

```csharp
using System.Globalization;
using AwesomeAssertions;

namespace Noof.Ledger.Domain.Tests;

public class MerchantNameTests
{
    [Fact]
    public void Trims_leading_and_trailing_whitespace()
    {
        MerchantName.Fold("  Coffee Bar  ").Should().Be("COFFEE BAR");
    }

    [Fact]
    public void Collapses_internal_whitespace_runs_to_a_single_space()
    {
        MerchantName.Fold("Coffee\t\n  Bar").Should().Be("COFFEE BAR");
    }

    [Fact]
    public void Uppercases_using_the_invariant_culture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

        try
        {
            // Turkish casing turns 'i' into 'İ' (dotted capital I), not 'I'. A current-culture
            // ToUpper() here would silently fracture merchant identity on a Turkish/Azeri locale,
            // since MerchantAlias.Folded is the write-once key that names actually get matched by.
            MerchantName.Fold("istanbul market").Should().Be("ISTANBUL MARKET");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void Preserves_accented_characters()
    {
        MerchantName.Fold("Café Central").Should().Be("CAFÉ CENTRAL");
    }

    [Fact]
    public void Preserves_punctuation()
    {
        MerchantName.Fold("7-Eleven #42").Should().Be("7-ELEVEN #42");
    }
}
```

> This is the file where a "helpful" refactor does the most damage. `MerchantAlias.Folded` is write-once (see the Domain contract's comment on that type) — if `Fold()` ever starts unaccenting or stripping punctuation, every merchant alias written before the change permanently stops matching new transactions from the same merchant, with no way to detect it short of a manual audit. The four tests above exist specifically to make that change fail CI instead of shipping quietly. (Verified: all five facts pass against the Step 11 implementation, including the tr-TR culture trap, by actually compiling and running this file.)

- [ ] **Step 10: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Domain.Tests/Noof.Ledger.Domain.Tests.csproj`
Expected: build error — `error CS0246: The type or namespace name 'MerchantName' could not be found`.

- [ ] **Step 11: Write the minimal implementation**

Create `src/Noof.Ledger.Domain/MerchantName.cs`:

```csharp
namespace Noof.Ledger.Domain;

public static class MerchantName
{
    public static string Fold(string raw)
    {
        var words = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        return string.Join(' ', words).ToUpperInvariant();
    }
}
```

`string.Split(char[]?, StringSplitOptions)` with a `null` separator array splits on any run of Unicode whitespace and, combined with `RemoveEmptyEntries`, does the trim-and-collapse in one call — there is no separate `.Trim()` needed.

- [ ] **Step 12: Run the tests and watch them pass**

Run: `dotnet test --project tests/Noof.Ledger.Domain.Tests/Noof.Ledger.Domain.Tests.csproj`
Expected: PASS (5 new facts, plus everything from Step 7 still green).

- [ ] **Step 13: Commit**

```bash
git add src/Noof.Ledger.Domain/MerchantName.cs tests/Noof.Ledger.Domain.Tests/MerchantNameTests.cs
git commit -m "feat(domain): MerchantName.Fold, trim-collapse-uppercase and nothing else"
```

- [ ] **Step 14: Write the failing test matrix for `QuotedAmount.TryResolve`**

This is the single most security-relevant pure function in the phase — it is the only thing standing between "the model phrases a number" and "the model invents a number." Create `tests/Noof.Ledger.Domain.Tests/QuotedAmountTests.cs`:

```csharp
using AwesomeAssertions;

namespace Noof.Ledger.Domain.Tests;

public class QuotedAmountTests
{
    [Fact]
    public void Quote_present_verbatim_resolves_to_money()
    {
        var resolved = QuotedAmount.TryResolve("кофе 250 рсд", "250", "RSD", out var money, out var failure);

        resolved.Should().BeTrue();
        money.Should().Be(new Money(250m, CurrencyCode.Rsd));
        failure.Should().BeEmpty();
    }

    [Fact]
    public void Quote_not_present_verbatim_fails()
    {
        var resolved = QuotedAmount.TryResolve("кофе двести пятьдесят рсд", "250", "RSD", out var money, out var failure);

        resolved.Should().BeFalse();
        money.Should().Be(default(Money));
        failure.Should().NotBeEmpty();
    }

    [Fact]
    public void Missing_currency_code_fails()
    {
        var resolved = QuotedAmount.TryResolve("такси 500", "500", null, out _, out var failure);

        resolved.Should().BeFalse();
        failure.Should().NotBeEmpty();
    }

    [Fact]
    public void Unknown_currency_code_fails()
    {
        var resolved = QuotedAmount.TryResolve("такси 500 zzz", "500", "ZZZ", out _, out var failure);

        resolved.Should().BeFalse();
        failure.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("11.700,50")]
    [InlineData("11,700.50")]
    public void European_and_anglo_formats_agree(string quote)
    {
        var rawText = $"продукты {quote} eur";

        var resolved = QuotedAmount.TryResolve(rawText, quote, "EUR", out var money, out _);

        resolved.Should().BeTrue();
        money.Should().Be(new Money(11700.50m, CurrencyCode.Eur));
    }

    [Fact]
    public void A_lone_separator_followed_by_exactly_three_digits_is_a_thousands_group()
    {
        // Ambiguous by construction: "1.700" could be 1700 or 1.7. Pinned to 1700 — see
        // "Design decisions locked by this task" above.
        var resolved = QuotedAmount.TryResolve("аренда 1.700 eur", "1.700", "EUR", out var money, out _);

        resolved.Should().BeTrue();
        money.Should().Be(new Money(1700m, CurrencyCode.Eur));
    }

    [Fact]
    public void A_lone_separator_followed_by_two_digits_is_a_decimal_point()
    {
        var resolved = QuotedAmount.TryResolve("кофе 1.70 eur", "1.70", "EUR", out var money, out _);

        resolved.Should().BeTrue();
        money.Should().Be(new Money(1.70m, CurrencyCode.Eur));
    }

    // Written as explicit \u escapes, not literal glyphs. U+2009 (thin space) and U+00A0
    // (non-breaking space) are visually indistinguishable from a plain space -- and from each
    // other -- in an editor and through copy-paste, which is exactly how an earlier version of
    // this test shipped with both rows byte-identical and silently tested nothing but ASCII
    // space. Never paste a raw special-whitespace glyph into this codebase; escape it.
    [Theory]
    [InlineData("11\u2009700,50")]
    [InlineData("11\u00A0700,50")]
    public void Thin_and_non_breaking_space_thousands_separators_are_stripped(string quote)
    {
        var rawText = $"аренда {quote} eur";

        var resolved = QuotedAmount.TryResolve(rawText, quote, "EUR", out var money, out _);

        resolved.Should().BeTrue();
        money.Should().Be(new Money(11700.50m, CurrencyCode.Eur));
    }

    [Theory]
    [InlineData("-250")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("12-34")]
    public void Malformed_quotes_fail_cleanly(string quote)
    {
        var rawText = $"заметка {quote} конец";

        var resolved = QuotedAmount.TryResolve(rawText, quote, "RSD", out var money, out var failure);

        resolved.Should().BeFalse();
        money.Should().Be(default(Money));
        failure.Should().NotBeEmpty();
    }

    [Fact]
    public void A_quote_repeated_in_the_raw_text_still_resolves()
    {
        var resolved = QuotedAmount.TryResolve("кофе 250, потом ещё кофе 250", "250", "RSD", out var money, out _);

        resolved.Should().BeTrue();
        money.Should().Be(new Money(250m, CurrencyCode.Rsd));
    }

    // A quote that occurs verbatim as a SUBSTRING of a larger number is not the same claim as a
    // quote that occurs as its own number. "500" inside "1500" is a fragment the model mis-bounded,
    // not the figure it actually saw -- exactly what this gate exists to catch, per the review that
    // found it: rawText.Contains alone blocks an invented figure but not a mis-bounded one.
    [Fact]
    public void A_quote_that_is_only_a_fragment_of_a_larger_number_fails()
    {
        var resolved = QuotedAmount.TryResolve("кофе 1500 рсд", "500", "RSD", out var money, out var failure);

        resolved.Should().BeFalse();
        money.Should().Be(default(Money));
        failure.Should().NotBeEmpty();
    }

    [Fact]
    public void A_quote_that_is_only_the_leading_digits_of_a_larger_number_fails()
    {
        var resolved = QuotedAmount.TryResolve("кофе 1500 рсд", "1", "RSD", out var money, out var failure);

        resolved.Should().BeFalse();
        money.Should().Be(default(Money));
        failure.Should().NotBeEmpty();
    }

    [Fact]
    public void A_quote_that_is_a_fragment_of_a_hyphenated_date_fails()
    {
        var resolved = QuotedAmount.TryResolve("оплата 2026-09-21 300", "2026", "RSD", out var money, out var failure);

        resolved.Should().BeFalse();
        money.Should().Be(default(Money));
        failure.Should().NotBeEmpty();
    }

    [Fact]
    public void A_quote_with_two_separators_of_the_same_kind_is_not_a_single_well_formed_number()
    {
        var resolved = QuotedAmount.TryResolve("заметка 1.2.3 конец", "1.2.3", "RSD", out var money, out var failure);

        resolved.Should().BeFalse();
        money.Should().Be(default(Money));
        failure.Should().NotBeEmpty();
    }
}
```

> `TryResolve` must check that `amountQuote` occurs verbatim in `rawText` **before** any trimming or normalisation, using the quote exactly as the model returned it. If the implementation instead normalises first and checks containment on the normalised copy, a model that paraphrases the figure can slip a fabricated number past the gate this function exists to be. Keep the containment check literally first, against the raw parameter — Step 16 relies on this order.
>
> **Substring is not enough — a boundary check is required too (2026-09-21 review).** `rawText.Contains(amountQuote)` alone blocks a figure the model invented but not one it mis-bounded: `("кофе 1500 рсд", "500")` resolved to 500 RSD, `("кофе 1500 рсд", "1")` resolved to 1 RSD, and `("оплата 2026-09-21 300", "2026")` resolved to 2026 RSD, because "500", "1" and "2026" are all genuine substrings of a larger number or date already in the text. A matching occurrence must not be flanked by a digit, `.`, `,` or `-` on either side (these are exactly the characters that can continue a number or a hyphenated date; whitespace, other punctuation and the string's start/end are genuine boundaries) — and since a quote can legitimately occur twice (`A_quote_repeated_in_the_raw_text_still_resolves`), accept as soon as ANY occurrence has valid boundaries on both sides, not only the first. Separately, `"1.2.3"` resolved to `12.3` because the old code kept only the last `.` as the decimal point and silently discarded the first — a quote must have at most one `.` and at most one `,` to count as a single well-formed number. Implement both checks directly in `QuotedAmount.TryResolve`/`TryParseAmount` as shown in Step 16 below — do not defer this to a later task.

- [ ] **Step 15: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Domain.Tests/Noof.Ledger.Domain.Tests.csproj`
Expected: build error — `error CS0246: The type or namespace name 'QuotedAmount' could not be found`.

- [ ] **Step 16: Write the implementation**

Create `src/Noof.Ledger.Domain/QuotedAmount.cs`:

```csharp
using System.Globalization;
using System.Text;

namespace Noof.Ledger.Domain;

public static class QuotedAmount
{
    // Telegram's iOS/Android clients sometimes auto-group a typed number with a thin space
    // (U+2009) or a non-breaking space (U+00A0 / U+202F) instead of a comma or dot. These are
    // always grouping marks, never a decimal separator, so they are stripped unconditionally.
    // Written as explicit \u escapes, not literal glyphs: these three characters are visually
    // indistinguishable from a regular space (and from each other) in most editors and through
    // any copy-paste via markdown/JSON -- which is exactly how this array first shipped as three
    // copies of U+0020, silently defeating the handling this comment describes. Verified by
    // compiling and running this exact file: with the escapes below, a real U+2009/U+00A0
    // grouped quote resolves correctly; with three literal spaces it does not.
    static readonly char[] SpaceGroupers = ['\u2009', '\u00A0', '\u202F'];

    static readonly CurrencyCode[] KnownCurrencies =
    [
        CurrencyCode.Eur, CurrencyCode.Rsd, CurrencyCode.Usd, CurrencyCode.Rub, CurrencyCode.Kzt,
    ];

    public static bool TryResolve(
        string rawText,
        string? amountQuote,
        string? currencyCode,
        out Money money,
        out string failure)
    {
        money = default;

        if (string.IsNullOrWhiteSpace(amountQuote))
        {
            failure = "Amount quote is empty or whitespace.";
            return false;
        }

        // Verbatim AND boundary check against the quote exactly as given -- before any
        // normalisation. This is the entire safety property this type exists to enforce; see the
        // test file's trap. rawText.Contains alone blocks a figure the model invented, but not one
        // it mis-bounded: "500" is a true substring of "1500", so a naive Contains would let the
        // model claim a quote of "500" against raw text that actually says 1500. A boundary is
        // therefore required on both sides of a matching occurrence: a digit obviously continues
        // the same number, and so do '.' and ',' (the two characters this parser itself treats as
        // separators -- a quote flanked by either might really be a longer number with the digits
        // on the other side of that separator left out) and '-' (a quote flanked by it might really
        // be one segment of a hyphenated date, e.g. "2026" in "2026-09-21"). Any other character,
        // including whitespace and the start/end of the string, is a genuine boundary. Because a
        // quote can legitimately occur more than once (see
        // A_quote_repeated_in_the_raw_text_still_resolves), this accepts if ANY occurrence has valid
        // boundaries on both sides, not only the first.
        if (string.IsNullOrEmpty(rawText) || !OccursAsWholeNumber(rawText, amountQuote))
        {
            failure = $"Quote \"{amountQuote}\" does not occur verbatim in the raw text.";
            return false;
        }

        if (!TryParseAmount(amountQuote, out var amount, out failure))
            return false;

        if (!TryParseCurrency(currencyCode, out var currency, out failure))
            return false;

        money = new Money(amount, currency);
        failure = string.Empty;
        return true;
    }

    static bool TryParseAmount(string quote, out decimal amount, out string failure)
    {
        amount = default;

        var candidate = quote.Trim();
        foreach (var grouper in SpaceGroupers)
            candidate = candidate.Replace(grouper.ToString(), string.Empty);

        // Safe: TryResolve already rejected a null/whitespace-only quote, and grouping spaces
        // are themselves whitespace, so at least one non-whitespace character survives here.
        if (candidate[0] == '-')
        {
            failure = $"Amount quote \"{quote}\" is negative.";
            return false;
        }

        if (!candidate.All(c => char.IsAsciiDigit(c) || c is '.' or ','))
        {
            failure = $"Amount quote \"{quote}\" is not a plain number.";
            return false;
        }

        // A single well-formed number has at most one decimal point and, per the separator rule
        // below, at most one grouping mark -- this parser only ever treats ONE occurrence of '.'
        // and ONE occurrence of ',' as meaningful (whichever is rightmost becomes the decimal
        // point when both are present). Two dots with no comma, as in "1.2.3", has no unambiguous
        // reading: the old code silently kept only the last dot as decimal and discarded the
        // first, inventing "12.3" out of a quote that was never a single number to begin with.
        if (candidate.Count(c => c == '.') > 1 || candidate.Count(c => c == ',') > 1)
        {
            failure = $"Amount quote \"{quote}\" is not a single well-formed number.";
            return false;
        }

        var lastDot = candidate.LastIndexOf('.');
        var lastComma = candidate.LastIndexOf(',');

        // The separator rule, pinned (see "Design decisions locked by this task"): with both
        // '.' and ',' present, the rightmost is the decimal point. With only one kind present,
        // exactly 3 digits after its last occurrence means thousands group, not decimal point.
        char? decimalSeparator = (lastDot, lastComma) switch
        {
            ( >= 0, >= 0) => lastDot > lastComma ? '.' : ',',
            ( >= 0, -1) => candidate.Length - lastDot - 1 == 3 ? null : '.',
            (-1, >= 0) => candidate.Length - lastComma - 1 == 3 ? null : ',',
            _ => null,
        };

        var normalized = new StringBuilder(candidate.Length);
        for (var i = 0; i < candidate.Length; i++)
        {
            var c = candidate[i];
            if (c is '.' or ',')
            {
                var isChosenDecimalPoint = c == decimalSeparator && i == (c == '.' ? lastDot : lastComma);
                if (isChosenDecimalPoint)
                    normalized.Append('.');

                continue;
            }

            normalized.Append(c);
        }

        // NumberStyles.AllowDecimalPoint with InvariantCulture, never a culture-sensitive parse
        // (this codebase has a dedicated culture-regression gate for exactly this failure mode
        // -- see CurrencyCodeTests.Orders_ordinally_regardless_of_culture from Phase 0).
        if (!decimal.TryParse(normalized.ToString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount))
        {
            failure = $"Amount quote \"{quote}\" does not parse as a number.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    static bool IsNumberBoundaryChar(char c) => char.IsAsciiDigit(c) || c is '.' or ',' or '-';

    static bool OccursAsWholeNumber(string rawText, string quote)
    {
        var searchFrom = 0;
        while (true)
        {
            var index = rawText.IndexOf(quote, searchFrom, StringComparison.Ordinal);
            if (index < 0)
                return false;

            var before = index == 0 || !IsNumberBoundaryChar(rawText[index - 1]);
            var after = index + quote.Length == rawText.Length || !IsNumberBoundaryChar(rawText[index + quote.Length]);
            if (before && after)
                return true;

            searchFrom = index + 1;
        }
    }

    static bool TryParseCurrency(string? code, out CurrencyCode currency, out string failure)
    {
        currency = default;

        if (string.IsNullOrEmpty(code) || code.Length != 3 || !code.All(char.IsAsciiLetter))
        {
            failure = $"'{code}' is not a three-letter currency code.";
            return false;
        }

        var candidate = new CurrencyCode(code);
        if (!KnownCurrencies.Contains(candidate))
        {
            failure = $"'{code}' is not a known currency.";
            return false;
        }

        currency = candidate;
        failure = string.Empty;
        return true;
    }
}
```

> Do not reach for `decimal.Parse(quote)` or the culture-default overload of `TryParse` anywhere in here. On a machine (or a CI runner) whose current culture treats `,` as the decimal separator, a bare `decimal.Parse` on `"11,700.50"` either throws or silently returns the wrong number depending on culture — and it would do so only in some environments, which is exactly the kind of bug that passes locally and breaks in production. `NumberStyles.AllowDecimalPoint` + `CultureInfo.InvariantCulture` is the only combination used here, deliberately.

- [ ] **Step 17: Run the tests and watch them pass**

Run: `dotnet test --project tests/Noof.Ledger.Domain.Tests/Noof.Ledger.Domain.Tests.csproj`
Expected: PASS (20 new test cases across the facts and theories above, plus everything from Step 12 still green).

Then confirm the whole solution still builds and no architecture rule regressed — this task adds files to `Noof.Ledger.Domain` but no package references, so `Domain_has_no_package_references` in `tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs` must still pass:

Run: `dotnet build NoofLedger.slnx` — Expected: `Build succeeded`, `0 Warning(s)`, `0 Error(s)`.
Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj` — Expected: PASS, all architecture tests green including `Domain_has_no_package_references`.

- [ ] **Step 18: Commit**

```bash
git add src/Noof.Ledger.Domain/QuotedAmount.cs tests/Noof.Ledger.Domain.Tests/QuotedAmountTests.cs
git commit -m "feat(domain): QuotedAmount.TryResolve, the quote-and-verify gate on model figures"
```
### Task 2: Domain — the ledger entities

**Files:**
- Create: `src/Noof.Ledger.Domain/Wallet.cs`
- Create: `src/Noof.Ledger.Domain/Category.cs`
- Create: `src/Noof.Ledger.Domain/Merchant.cs`
- Create: `src/Noof.Ledger.Domain/MerchantAlias.cs`
- Create: `src/Noof.Ledger.Domain/Transaction.cs`
- Create: `src/Noof.Ledger.Domain/LineItem.cs`
- Create: `src/Noof.Ledger.Domain/CategorizationJob.cs`
- No `.csproj` edit. `src/Noof.Ledger.Domain/Noof.Ledger.Domain.csproj` is a bare `Microsoft.NET.Sdk` project with no explicit `<Compile>` items — every `.cs` file dropped into that directory is picked up by the default globbing (confirmed: `AppUser.cs`, `Money.cs`, `CurrencyCode.cs` are already in that project with no matching `<ItemGroup>` referencing them).

**Precondition:** Task 1 has already created, in `src/Noof.Ledger.Domain/`: the enums `TransactionStatus`, `CategorizationAuthority`, `MerchantKind`, `JobStatus`, plus `MerchantName.Fold` and `QuotedAmount.TryResolve`. This task consumes those four enums by name and does not redefine or modify them.

**Interfaces:**
- Consumes: `CurrencyCode`, `Money` (both already in `src/Noof.Ledger.Domain/`, same namespace — no `using` needed), and the four enums above (Task 1).
- Produces the seven sealed classes below, with every member's exact accessor (`init` vs `set`) as written — later tasks depend on this exactly: Task 3's EF configuration decides column mapping and nullability from these accessors, `ICaptureStore.CaptureAsync` constructs a `Transaction` and `CategorizationJob` together, and `IJobQueue`'s four verbs (`ClaimAsync`/`SucceedAsync`/`RetryAsync`/`FailAsync`) mutate a `CategorizationJob` in place through the `set` properties below.

> **Why this task has no failing-test step.** Every property on these seven classes is a plain `required` auto-property — nothing here computes, validates, or transforms anything. CLAUDE.md exempts DTO-shaped types from TDD, and this plan's own brief says it explicitly: a test asserting `wallet.IsDefault = true; wallet.IsDefault.Should().BeTrue();` proves only that C# auto-properties work, which is the exact test this project has already deleted once for testing nothing. The real invariants implied here — exactly one default wallet, a slug that never changes once minted, `RawText` surviving untouched until quote-and-verify reads it — are enforced by the *callers* of these classes (`ICaptureStore`, the categorization worker, Task 3's EF configuration), not by the classes themselves, so they get tests when those callers are built. This task is seven small creates, a build/test check, and one commit.

- [ ] **Step 1: Create `Wallet.cs`**

Create `src/Noof.Ledger.Domain/Wallet.cs`:

```csharp
namespace Noof.Ledger.Domain;

public sealed class Wallet
{
    public required Guid Id { get; init; }
    public required string Name { get; set; }
    public required CurrencyCode Currency { get; init; }

    // Exactly one wallet must have this set to true; ICaptureStore.CaptureAsync
    // (a later task) resolves the default wallet by querying for it. Nothing in
    // this class enforces "exactly one" - that invariant lives with the caller.
    public required bool IsDefault { get; set; }
}
```

- [ ] **Step 2: Create `Category.cs`**

Create `src/Noof.Ledger.Domain/Category.cs`:

```csharp
namespace Noof.Ledger.Domain;

public sealed class Category
{
    public required Guid Id { get; init; }
    public Guid? ParentId { get; set; }

    // Immutable once minted: the model answers every categorization job with
    // this slug, so changing it would silently orphan past and future answers.
    public required string Slug { get; init; }

    public required string NameEn { get; set; }
    public required string NameRu { get; set; }
    public required bool IsActive { get; set; }
}
```

> `Slug` is `init`-only; `NameEn`, `NameRu` and `IsActive` are `set`. Renaming a category changes the display names, never the slug - do not add a setter to `Slug` to make renaming "more complete." A category management UI is explicitly out of scope this phase (see `docs/BACKLOG.md`), but the schema must already support renaming and re-parenting without ever touching `Slug`.

- [ ] **Step 3: Create `Merchant.cs`**

Create `src/Noof.Ledger.Domain/Merchant.cs`:

```csharp
namespace Noof.Ledger.Domain;

public sealed class Merchant
{
    public required Guid Id { get; init; }
    public required string DisplayName { get; set; }
    public required MerchantKind Kind { get; set; }
}
```

- [ ] **Step 4: Create `MerchantAlias.cs`**

Create `src/Noof.Ledger.Domain/MerchantAlias.cs`:

```csharp
namespace Noof.Ledger.Domain;

// Write-once: an alias is folded once and never edited, so there is no
// UpdatedAt column to maintain.
public sealed class MerchantAlias
{
    // The primary key is the folded text itself, not a synthetic Guid - every
    // other entity in this file gets a Guid Id, this one deliberately does not.
    public required string Folded { get; init; }
    public required Guid MerchantId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
```

> Every other class in this task has a `Guid Id`. `MerchantAlias` does not - `Folded` (produced by `MerchantName.Fold` from Task 1) is the primary key, because the whole point of this table is "have we seen this folded name before?" Do not add an `Id` property here; Task 3's EF configuration will call `HasKey(a => a.Folded)`, and a spare `Id` would just be dead weight competing with it.

- [ ] **Step 5: Create `Transaction.cs`**

Create `src/Noof.Ledger.Domain/Transaction.cs`:

```csharp
namespace Noof.Ledger.Domain;

public sealed class Transaction
{
    public required Guid Id { get; init; }
    public required Guid WalletId { get; init; }

    // Quote-and-verify re-reads this, possibly hours after capture, so it must
    // survive untouched regardless of what categorization does to the line items.
    public required string RawText { get; init; }

    public required TransactionStatus Status { get; set; }

    // IANA id (e.g. "Europe/Belgrade"), stamped from Capture:TimeZone at capture.
    public required string TimeZoneId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
    public required long TelegramChatId { get; init; }

    // The user's own message.
    public required int TelegramMessageId { get; init; }

    // Our reply, attached after capture and edited as categorization finishes -
    // unlike TelegramMessageId, it starts unset.
    public int? BotMessageId { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }
}
```

- [ ] **Step 6: Create `LineItem.cs`**

Create `src/Noof.Ledger.Domain/LineItem.cs`:

```csharp
namespace Noof.Ledger.Domain;

public sealed class LineItem
{
    public required Guid Id { get; init; }
    public required Guid TransactionId { get; init; }
    public required string Description { get; set; }
    public required Money Amount { get; set; }
    public Guid? CategoryId { get; set; }
    public required CategorizationAuthority CategorizedBy { get; set; }
    public Guid? MerchantId { get; set; }
}
```

> `Money` and `CategorizationAuthority` are already in the `Noof.Ledger.Domain` namespace (Money today, the enum from Task 1) - this file needs no `using` beyond the implicit ones. Do not add `using Noof.Ledger.Domain;` to a file that is already inside that namespace; some IDEs suggest it reflexively when you type `Money`, and it is a no-op that only adds noise.
>
> `Amount` is `set`, matching the locked contract, not `init` like `MoneyProbeEntity.Amount` in `src/Noof.Ledger.Persistence/MoneyProbeEntity.cs`. That is deliberate, not a typo to "fix": a line item is created with a capture-time amount and can be corrected later, where the probe entity exists only to prove the `ComplexProperty` mapping round-trips a fixed value. Task 3 maps this the same way regardless of accessor - `ComplexProperty` does not require `init`.

- [ ] **Step 7: Create `CategorizationJob.cs`**

Create `src/Noof.Ledger.Domain/CategorizationJob.cs`:

```csharp
namespace Noof.Ledger.Domain;

public sealed class CategorizationJob
{
    public required Guid Id { get; init; }
    public required Guid TransactionId { get; init; }
    public required JobStatus Status { get; set; }
    public required int AttemptCount { get; set; }
    public required DateTimeOffset RunAfter { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public string? ClaimedBy { get; set; }
    public string? LastError { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; set; }
}
```

> `ClaimedBy` and `LastError` must keep their `?` (`string?`). This project sets `<Nullable>enable</Nullable>` and `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in `Directory.Build.props` at the repo root - a non-nullable `string ClaimedBy` (or `LastError`) with no `required` modifier and no default is CS8618, and CS8618 **fails the build** here, it does not just warn. A job starts with neither set (`Pending`, never claimed, no error yet), so both must stay nullable.

- [ ] **Step 8: Build and run the tests**

Run: `dotnet test --project tests/Noof.Ledger.Domain.Tests/Noof.Ledger.Domain.Tests.csproj`
Expected: PASS, zero failures. This task adds no new tests, so the existing `MoneyTests`, `MoneyComparisonTests`, `CurrencyCodeTests` and `AppUserTests` are what run; a failure here means one of the seven new files does not compile (check the `?` on nullable properties and the `required` modifiers against the code above character-for-character).

Run: `dotnet test --solution NoofLedger.slnx`
Expected: PASS, zero failures. This also re-runs `Noof.Ledger.Architecture.Tests.ProjectReferenceTests.Domain_has_no_package_references` and the `Project_references_exactly_its_allowed_set("Noof.Ledger.Domain")` theory row, confirming the seven new files did not pull in a project or package reference - they must not, since they are plain classes over `Guid`, `string`, `bool`, `int`, `long`, `DateTimeOffset` and this project's own `Money`/`CurrencyCode`.

> Do not compare against a specific pass count. This plan's baseline (136/136, quoted at the top of the overall plan) was taken before Task 1's enums and static classes existed; Task 1 runs before this task and adds its own tests for `MerchantName.Fold` and `QuotedAmount.TryResolve`, so the true count by the time you run this step is higher than 136. What matters is zero failures and zero skips, not matching a number.

- [ ] **Step 9: Commit**

```bash
git add src/Noof.Ledger.Domain/Wallet.cs src/Noof.Ledger.Domain/Category.cs src/Noof.Ledger.Domain/Merchant.cs src/Noof.Ledger.Domain/MerchantAlias.cs src/Noof.Ledger.Domain/Transaction.cs src/Noof.Ledger.Domain/LineItem.cs src/Noof.Ledger.Domain/CategorizationJob.cs
git commit -m "feat(domain): the seven ledger entities"
```
### Task 3: Persistence — configurations, the migration, and dropping MoneyProbeEntity

**Files:**
- Create: `src/Noof.Ledger.Persistence/Configurations/WalletConfiguration.cs`
- Create: `src/Noof.Ledger.Persistence/Configurations/CategoryConfiguration.cs`
- Create: `src/Noof.Ledger.Persistence/Configurations/MerchantConfiguration.cs`
- Create: `src/Noof.Ledger.Persistence/Configurations/MerchantAliasConfiguration.cs`
- Create: `src/Noof.Ledger.Persistence/Configurations/TransactionConfiguration.cs`
- Create: `src/Noof.Ledger.Persistence/Configurations/LineItemConfiguration.cs`
- Create: `src/Noof.Ledger.Persistence/Configurations/CategorizationJobConfiguration.cs`
- Modify: `src/Noof.Ledger.Persistence/LedgerDbContext.cs`
- Delete: `src/Noof.Ledger.Persistence/MoneyProbeEntity.cs`
- Delete: `src/Noof.Ledger.Persistence/Configurations/MoneyProbeEntityConfiguration.cs`
- Create (generated, then hand-edited): `src/Noof.Ledger.Persistence/Migrations/<timestamp>_AddCaptureModel.cs` (+ its `.Designer.cs`, and `LedgerDbContextModelSnapshot.cs` regenerated — leave both exactly as EF emits them)
- Create: `tests/Noof.Ledger.Persistence.Tests/CaptureModelTests.cs`
- Create: `tests/Noof.Ledger.Persistence.Tests/MerchantAliasWriteOnceTests.cs`
- Create: `tests/Noof.Ledger.Persistence.Tests/LineItemMoneyMappingTests.cs`
- Create: `tests/Noof.Ledger.Persistence.Tests/SeedDataTests.cs`
- Modify: `tests/Noof.Ledger.Persistence.Tests/AppUserModelTests.cs`
- Modify: `tests/Noof.Ledger.Persistence.Tests/MigrationContractTests.cs`
- Modify: `tests/Noof.Ledger.Persistence.Tests/schema.expected.sql`

**Interfaces:**
- Consumes: `Wallet`, `Category`, `Merchant`, `MerchantAlias`, `Transaction`, `LineItem`, `CategorizationJob` (Task 1, Domain) — used exactly as the locked contract defines them; nothing here adds a member to any of them.
- Produces: tables `wallets`, `categories`, `merchants`, `merchant_aliases`, `transactions`, `line_items`, `categorization_jobs`; a `DbSet<T>` for each on `LedgerDbContext`; the unique `(telegram_chat_id, telegram_message_id)` index later tasks' `ICaptureStore` implementation depends on for idempotency; the `(status, run_after)` index later tasks' `IJobQueue` implementation depends on for the claim query; a DB-enforced write-once guard on `merchant_aliases`; seed data (1 wallet, 20 categories, one of them `slug = "coffee"`, `NameRu = "Кофе"`).

> **Path correction before you start.** The phase brief says to delete `src/Noof.Ledger.Domain/MoneyProbeEntity.cs`. That file has never lived in Domain — verified against the repo, it is at **`src/Noof.Ledger.Persistence/MoneyProbeEntity.cs`** (it was created directly there in the Phase 0 migration-history commit; `MoneyStorageTests.cs`'s raw-SQL `money_probe` table is unrelated and untouched by this task). Every path below is the real one.

---

- [ ] **Step 1: Write the failing model test for the seven new entities**

Create `tests/Noof.Ledger.Persistence.Tests/CaptureModelTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Tests;

public class CaptureModelTests
{
    static LedgerDbContext BuildOfflineContext()
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none")
            .Options;

        return new LedgerDbContext(options);
    }

    [Fact]
    public void Wallet_maps_to_snake_case_columns()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(Wallet))!;

        entity.GetTableName().Should().Be("wallets");
        entity.GetProperty(nameof(Wallet.Name)).GetColumnName().Should().Be("name");
        entity.GetProperty(nameof(Wallet.Currency)).GetColumnName().Should().Be("currency");
        entity.GetProperty(nameof(Wallet.Currency)).GetMaxLength().Should().Be(3);
    }

    [Fact]
    public void Category_has_a_unique_slug_and_a_restricted_self_reference()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(Category))!;

        entity.GetTableName().Should().Be("categories");
        entity.GetIndexes().Should().ContainSingle(i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual([nameof(Category.Slug)]));

        var fk = entity.GetForeignKeys().Single();
        fk.PrincipalEntityType.ClrType.Should().Be(typeof(Category));
        fk.Properties.Select(p => p.Name).Should().Equal(nameof(Category.ParentId));
        fk.DeleteBehavior.Should().Be(DeleteBehavior.Restrict);
    }

    [Fact]
    public void Merchant_maps_to_snake_case_columns()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(Merchant))!;

        entity.GetTableName().Should().Be("merchants");
        entity.GetProperty(nameof(Merchant.DisplayName)).GetColumnName().Should().Be("display_name");
    }

    [Fact]
    public void MerchantAlias_is_keyed_on_the_folded_name_with_no_updated_at_column()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(MerchantAlias))!;

        entity.GetTableName().Should().Be("merchant_aliases");
        entity.FindPrimaryKey()!.Properties.Select(p => p.Name).Should().Equal(nameof(MerchantAlias.Folded));
    }

    [Fact]
    public void Transaction_has_a_unique_index_that_makes_capture_idempotent()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(Transaction))!;

        entity.GetTableName().Should().Be("transactions");
        entity.GetIndexes().Should().ContainSingle(i => i.IsUnique
            && i.Properties.Select(p => p.Name)
                .SequenceEqual([nameof(Transaction.TelegramChatId), nameof(Transaction.TelegramMessageId)]));
    }

    [Fact]
    public void LineItem_maps_money_the_same_way_the_probe_did()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(LineItem))!;
        var money = entity.GetComplexProperties().Single();

        entity.GetTableName().Should().Be("line_items");
        money.ComplexType.FindProperty("Amount")!.GetColumnName().Should().Be("amount");
        money.ComplexType.FindProperty("Currency")!.GetMaxLength().Should().Be(3);
    }

    [Fact]
    public void CategorizationJob_has_an_index_supporting_the_queue_claim()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(CategorizationJob))!;

        entity.GetTableName().Should().Be("categorization_jobs");
        entity.GetIndexes().Should().ContainSingle(i =>
            i.Properties.Select(p => p.Name)
                .SequenceEqual([nameof(CategorizationJob.Status), nameof(CategorizationJob.RunAfter)]));
    }

    [Fact]
    public void Enum_columns_persist_as_plain_integers_not_a_native_postgres_enum()
    {
        using var db = BuildOfflineContext();

        db.Model.FindEntityType(typeof(Transaction))!.GetProperty(nameof(Transaction.Status))
            .GetColumnType().Should().Be("integer");
        db.Model.FindEntityType(typeof(Merchant))!.GetProperty(nameof(Merchant.Kind))
            .GetColumnType().Should().Be("integer");
        db.Model.FindEntityType(typeof(LineItem))!.GetProperty(nameof(LineItem.CategorizedBy))
            .GetColumnType().Should().Be("integer");
        db.Model.FindEntityType(typeof(CategorizationJob))!.GetProperty(nameof(CategorizationJob.Status))
            .GetColumnType().Should().Be("integer");
    }
}
```

> **Why this last test earns its place.** Step 3's callout below explains that a plain C# enum maps to `integer` by default under Npgsql, and warns against reaching for Npgsql's native-enum mapping instead. Verified directly (`GetColumnType()` on an unconverted enum property already resolves to `"integer"` at model-build time, no live database needed): without this assertion, nothing catches the exact mistake the callout warns about — `SchemaSnapshotTests` would just capture whatever the first implementation produced as the "correct" baseline, right or wrong.

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: all eight tests FAIL with `NullReferenceException` — `FindEntityType(...)` returns `null` because nothing configures these types into the model yet.

- [ ] **Step 3: Write the seven configurations, wire them into the context, and add the two Postgres round-trip tests that depend on them**

Create `src/Noof.Ledger.Persistence/Configurations/WalletConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class WalletConfiguration : IEntityTypeConfiguration<Wallet>
{
    public void Configure(EntityTypeBuilder<Wallet> builder)
    {
        builder.ToTable("wallets");

        builder.HasKey(w => w.Id);

        builder.Property(w => w.Id).HasColumnName("id");
        builder.Property(w => w.Name).HasColumnName("name").HasMaxLength(128).IsRequired();

        builder.Property(w => w.Currency)
            .HasColumnName("currency")
            .HasMaxLength(3)
            .IsRequired()
            .HasConversion(c => c.Value, v => new CurrencyCode(v));

        builder.Property(w => w.IsDefault).HasColumnName("is_default").IsRequired();

        builder.HasData(new Wallet
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Name = "Main Wallet",
            Currency = CurrencyCode.Rsd,
            IsDefault = true,
        });
    }
}
```

Create `src/Noof.Ledger.Persistence/Configurations/CategoryConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    static readonly Guid Groceries = Guid.Parse("00000000-0000-0000-0001-000000000001");
    static readonly Guid FoodDrink = Guid.Parse("00000000-0000-0000-0001-000000000002");
    static readonly Guid Transport = Guid.Parse("00000000-0000-0000-0001-000000000003");
    static readonly Guid Housing = Guid.Parse("00000000-0000-0000-0001-000000000004");
    static readonly Guid Utilities = Guid.Parse("00000000-0000-0000-0001-000000000005");
    static readonly Guid Health = Guid.Parse("00000000-0000-0000-0001-000000000006");
    static readonly Guid Shopping = Guid.Parse("00000000-0000-0000-0001-000000000007");
    static readonly Guid Entertainment = Guid.Parse("00000000-0000-0000-0001-000000000008");
    static readonly Guid Travel = Guid.Parse("00000000-0000-0000-0001-000000000009");
    static readonly Guid Education = Guid.Parse("00000000-0000-0000-0001-000000000010");
    static readonly Guid Subscriptions = Guid.Parse("00000000-0000-0000-0001-000000000011");
    static readonly Guid GiftsDonations = Guid.Parse("00000000-0000-0000-0001-000000000012");
    static readonly Guid FeesCharges = Guid.Parse("00000000-0000-0000-0001-000000000013");
    static readonly Guid PersonalCare = Guid.Parse("00000000-0000-0000-0001-000000000014");
    static readonly Guid Other = Guid.Parse("00000000-0000-0000-0001-000000000015");
    static readonly Guid Restaurants = Guid.Parse("00000000-0000-0000-0001-000000000016");
    static readonly Guid Coffee = Guid.Parse("00000000-0000-0000-0001-000000000017");
    static readonly Guid Fuel = Guid.Parse("00000000-0000-0000-0001-000000000018");
    static readonly Guid PublicTransport = Guid.Parse("00000000-0000-0000-0001-000000000019");
    static readonly Guid Clothing = Guid.Parse("00000000-0000-0000-0001-000000000020");

    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id).HasColumnName("id");
        builder.Property(c => c.ParentId).HasColumnName("parent_id");
        builder.Property(c => c.Slug).HasColumnName("slug").HasMaxLength(64).IsRequired();
        builder.Property(c => c.NameEn).HasColumnName("name_en").HasMaxLength(128).IsRequired();
        builder.Property(c => c.NameRu).HasColumnName("name_ru").HasMaxLength(128).IsRequired();
        builder.Property(c => c.IsActive).HasColumnName("is_active");

        builder.HasIndex(c => c.Slug).IsUnique();

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(c => c.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasData(
            Seed(Groceries, null, "groceries", "Groceries", "Продукты"),
            Seed(FoodDrink, null, "food-drink", "Food & Drink", "Еда и напитки"),
            Seed(Transport, null, "transport", "Transport", "Транспорт"),
            Seed(Housing, null, "housing", "Housing", "Жильё"),
            Seed(Utilities, null, "utilities", "Utilities", "Коммунальные услуги"),
            Seed(Health, null, "health", "Health", "Здоровье"),
            Seed(Shopping, null, "shopping", "Shopping", "Покупки"),
            Seed(Entertainment, null, "entertainment", "Entertainment", "Развлечения"),
            Seed(Travel, null, "travel", "Travel", "Путешествия"),
            Seed(Education, null, "education", "Education", "Образование"),
            Seed(Subscriptions, null, "subscriptions", "Subscriptions", "Подписки"),
            Seed(GiftsDonations, null, "gifts-donations", "Gifts & Donations", "Подарки и пожертвования"),
            Seed(FeesCharges, null, "fees-charges", "Fees & Charges", "Комиссии и сборы"),
            Seed(PersonalCare, null, "personal-care", "Personal Care", "Личная гигиена"),
            Seed(Other, null, "other", "Other", "Прочее"),
            Seed(Restaurants, FoodDrink, "restaurants", "Restaurants", "Рестораны"),
            Seed(Coffee, FoodDrink, "coffee", "Coffee", "Кофе"),
            Seed(Fuel, Transport, "fuel", "Fuel", "Топливо"),
            Seed(PublicTransport, Transport, "public-transport", "Public Transport", "Общественный транспорт"),
            Seed(Clothing, Shopping, "clothing", "Clothing", "Одежда"));
    }

    static Category Seed(Guid id, Guid? parentId, string slug, string nameEn, string nameRu) => new()
    {
        Id = id,
        ParentId = parentId,
        Slug = slug,
        NameEn = nameEn,
        NameRu = nameRu,
        IsActive = true,
    };
}
```

> **Fixed GUIDs, not `Guid.NewGuid()`.** `HasData` bakes literal values into the migration at generation time. A random GUID here would change every time `migrations add` re-runs against the same model, and a child's `ParentId` must point at a value that is stable across runs. Verified directly against EF Core 10 with a throwaway self-referencing entity: EF genuinely topologically sorts `InsertData` rows by FK dependency when generating the migration — a child declared *and* keyed numerically *before* its parent still comes out after it in the generated `InsertData` call. You do not need to declare parents before children in the `HasData(...)` call above (this list already does, which is good practice regardless, but the ordering below is not what makes the migration safe).

Create `src/Noof.Ledger.Persistence/Configurations/MerchantConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class MerchantConfiguration : IEntityTypeConfiguration<Merchant>
{
    public void Configure(EntityTypeBuilder<Merchant> builder)
    {
        builder.ToTable("merchants");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id).HasColumnName("id");
        builder.Property(m => m.DisplayName).HasColumnName("display_name").HasMaxLength(256).IsRequired();
        builder.Property(m => m.Kind).HasColumnName("kind");
    }
}
```

Create `src/Noof.Ledger.Persistence/Configurations/MerchantAliasConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class MerchantAliasConfiguration : IEntityTypeConfiguration<MerchantAlias>
{
    public void Configure(EntityTypeBuilder<MerchantAlias> builder)
    {
        builder.ToTable("merchant_aliases");

        builder.HasKey(a => a.Folded);

        builder.Property(a => a.Folded).HasColumnName("folded").HasMaxLength(256);
        builder.Property(a => a.MerchantId).HasColumnName("merchant_id");
        builder.Property(a => a.CreatedAt).HasColumnName("created_at");

        builder.HasOne<Merchant>()
            .WithMany()
            .HasForeignKey(a => a.MerchantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
```

Create `src/Noof.Ledger.Persistence/Configurations/TransactionConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        builder.ToTable("transactions");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.WalletId).HasColumnName("wallet_id");
        builder.Property(t => t.RawText).HasColumnName("raw_text").IsRequired();
        builder.Property(t => t.Status).HasColumnName("status");
        builder.Property(t => t.TimeZoneId).HasColumnName("time_zone_id").HasMaxLength(64).IsRequired();
        builder.Property(t => t.OccurredAt).HasColumnName("occurred_at");
        builder.Property(t => t.TelegramChatId).HasColumnName("telegram_chat_id");
        builder.Property(t => t.TelegramMessageId).HasColumnName("telegram_message_id");
        builder.Property(t => t.BotMessageId).HasColumnName("bot_message_id");
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(t => new { t.TelegramChatId, t.TelegramMessageId }).IsUnique();

        builder.HasOne<Wallet>()
            .WithMany()
            .HasForeignKey(t => t.WalletId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
```

Create `src/Noof.Ledger.Persistence/Configurations/LineItemConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class LineItemConfiguration : IEntityTypeConfiguration<LineItem>
{
    public void Configure(EntityTypeBuilder<LineItem> builder)
    {
        builder.ToTable("line_items");

        builder.HasKey(l => l.Id);

        builder.Property(l => l.Id).HasColumnName("id");
        builder.Property(l => l.TransactionId).HasColumnName("transaction_id");
        builder.Property(l => l.Description).HasColumnName("description").HasMaxLength(512).IsRequired();
        builder.Property(l => l.CategoryId).HasColumnName("category_id");
        builder.Property(l => l.CategorizedBy).HasColumnName("categorized_by");
        builder.Property(l => l.MerchantId).HasColumnName("merchant_id");

        builder.ComplexProperty(l => l.Amount, money =>
        {
            money.Property(m => m.Amount).HasColumnName("amount").HasPrecision(19, 4);
            money.Property(m => m.Currency)
                 .HasColumnName("currency")
                 .HasMaxLength(3)
                 .IsRequired()
                 .HasConversion(c => c.Value, v => new CurrencyCode(v));
        });

        builder.HasOne<Transaction>()
            .WithMany()
            .HasForeignKey(l => l.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(l => l.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Merchant>()
            .WithMany()
            .HasForeignKey(l => l.MerchantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
```

Create `src/Noof.Ledger.Persistence/Configurations/CategorizationJobConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class CategorizationJobConfiguration : IEntityTypeConfiguration<CategorizationJob>
{
    public void Configure(EntityTypeBuilder<CategorizationJob> builder)
    {
        builder.ToTable("categorization_jobs");

        builder.HasKey(j => j.Id);

        builder.Property(j => j.Id).HasColumnName("id");
        builder.Property(j => j.TransactionId).HasColumnName("transaction_id");
        builder.Property(j => j.Status).HasColumnName("status");
        builder.Property(j => j.AttemptCount).HasColumnName("attempt_count");
        builder.Property(j => j.RunAfter).HasColumnName("run_after");
        builder.Property(j => j.ClaimedAt).HasColumnName("claimed_at");
        builder.Property(j => j.ClaimedBy).HasColumnName("claimed_by").HasMaxLength(128);
        builder.Property(j => j.LastError).HasColumnName("last_error");
        builder.Property(j => j.CreatedAt).HasColumnName("created_at");
        builder.Property(j => j.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(j => new { j.Status, j.RunAfter });

        builder.HasOne<Transaction>()
            .WithMany()
            .HasForeignKey(j => j.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```

> **Don't add `.HasConversion<int>()` to `Status`/`Kind`/`CategorizedBy`.** EF Core already maps a plain enum to the database's `integer` type by default — that default is exactly what "persisted as ints" in the contract requires. The only way to get this wrong here is to reach for Npgsql's native-enum mapping instead; don't. (Step 1's `Enum_columns_persist_as_plain_integers_...` test is what actually catches it if you do.)

Now update `src/Noof.Ledger.Persistence/LedgerDbContext.cs` — add the seven new `DbSet` properties, **leave `MoneyProbes` in place for now** (it comes out in Step 5, as its own clearly-scoped change):

```csharp
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence;

public class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<MoneyProbeEntity> MoneyProbes => Set<MoneyProbeEntity>();

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Merchant> Merchants => Set<Merchant>();
    public DbSet<MerchantAlias> MerchantAliases => Set<MerchantAlias>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<LineItem> LineItems => Set<LineItem>();
    public DbSet<CategorizationJob> CategorizationJobs => Set<CategorizationJob>();

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

> **Now, while the DbSets exist but no migration does yet, add the two tests that depend on a real database.** Placing them here — rather than after the write-once trigger is done — is what gives them a genuine failing-first step: they compile (the DbSets above exist), but hit real PostgreSQL for a table that has not been created yet, so they fail for a concrete, checkable reason (Step 4) instead of passing on their very first run.

Create `tests/Noof.Ledger.Persistence.Tests/LineItemMoneyMappingTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class LineItemMoneyMappingTests(PostgresFixture fixture)
{
    static Wallet NewWallet() => new()
        { Id = Guid.NewGuid(), Name = "Test wallet", Currency = CurrencyCode.Eur, IsDefault = false };

    static Transaction NewTransaction(Guid walletId) => new()
    {
        Id = Guid.NewGuid(),
        WalletId = walletId,
        RawText = "flat white 1234.5678",
        Status = TransactionStatus.Captured,
        TimeZoneId = "Europe/Belgrade",
        OccurredAt = DateTimeOffset.UtcNow,
        TelegramChatId = 1,
        TelegramMessageId = 1,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task An_amount_round_trips_exactly_through_a_real_line_item()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var wallet = NewWallet();
        var transaction = NewTransaction(wallet.Id);
        db.Wallets.Add(wallet);
        db.Transactions.Add(transaction);
        db.LineItems.Add(new LineItem
        {
            Id = Guid.NewGuid(),
            TransactionId = transaction.Id,
            Description = "Flat white",
            Amount = new Money(1234.5678m, CurrencyCode.Eur),
            CategorizedBy = CategorizationAuthority.None,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var reloaded = await db.LineItems.SingleAsync(TestContext.Current.CancellationToken);

        reloaded.Amount.Amount.Should().Be(1234.5678m);
        reloaded.Amount.Currency.Should().Be(CurrencyCode.Eur);
    }

    [Fact]
    public async Task More_than_four_decimal_places_is_rounded_not_rejected()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var wallet = NewWallet();
        var transaction = NewTransaction(wallet.Id);
        db.Wallets.Add(wallet);
        db.Transactions.Add(transaction);
        db.LineItems.Add(new LineItem
        {
            Id = Guid.NewGuid(),
            TransactionId = transaction.Id,
            Description = "Rounded",
            Amount = new Money(1.00005m, CurrencyCode.Eur),
            CategorizedBy = CategorizationAuthority.None,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var reloaded = await db.LineItems.SingleAsync(TestContext.Current.CancellationToken);

        reloaded.Amount.Amount.Should().Be(1.0001m);
    }
}
```

> **`db.ChangeTracker.Clear()` matters here.** `fixture.CreateContextAsync()` creates a **new** ephemeral database on every call — two calls never point at the same database, so you cannot "reload with a second context" that way. Re-querying the *same* context after `SaveChangesAsync()` without clearing the tracker returns the already-tracked in-memory instance (EF's identity map), which proves nothing about what Postgres actually stored. Clearing the tracker forces the next query to materialize fresh from the columns the server returns — that's the only way this test is a real round trip and not a tautology.

Create `tests/Noof.Ledger.Persistence.Tests/SeedDataTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class SeedDataTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Exactly_one_wallet_is_seeded()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        (await db.Wallets.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task The_starting_category_tree_has_top_level_and_sub_categories()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var categories = await db.Categories.ToListAsync(TestContext.Current.CancellationToken);

        categories.Count(c => c.ParentId is null).Should().BeGreaterThanOrEqualTo(15);
        categories.Should().Contain(c => c.ParentId is not null,
            "the hierarchy is unproven without at least one sub-category");
    }

    [Fact]
    public async Task Coffee_lands_in_a_seeded_category()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var coffee = await db.Categories.SingleAsync(c => c.NameRu == "Кофе", TestContext.Current.CancellationToken);

        coffee.ParentId.Should().NotBeNull("coffee is a sub-category of Food & Drink, proving the hierarchy is real, not decorative");
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: the eight `CaptureModelTests` PASS. `MigrationContractTests.The_model_has_no_pending_changes` now **FAILS** — correct, the model changed and no migration exists yet; it comes back in Step 7. **Every test that calls `MigrateAsync` also FAILS here — around twenty of them, including all of `EfUserStoreTests`.** Expect `InvalidOperationException: ... has pending changes`, NOT the PostgreSQL `relation "..." does not exist` you might predict. EF Core 10 raises `PendingModelChangesWarning` as an error *inside* `MigrateAsync`, before it opens a connection, so a changed-but-unmigrated model fails every migrating test rather than only the one that asserts about pending changes. This is expected and has a single cause; do not start debugging `EfUserStoreTests`. All of it clears in Step 7 when the migration exists.

- [ ] **Step 5: Drop `MoneyProbeEntity`**

Delete `src/Noof.Ledger.Persistence/MoneyProbeEntity.cs` and `src/Noof.Ledger.Persistence/Configurations/MoneyProbeEntityConfiguration.cs`.

In `src/Noof.Ledger.Persistence/LedgerDbContext.cs`, remove the line:

```csharp
    public DbSet<MoneyProbeEntity> MoneyProbes => Set<MoneyProbeEntity>();
```

In `tests/Noof.Ledger.Persistence.Tests/AppUserModelTests.cs`, delete the last test method (lines 52–62):

```csharp
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
```

along with the blank line above it, so the file ends with the `The_primary_key_is_id` test followed directly by the closing `}`.

> **`MigrationContractTests.cs` needs the same surgery, even though the brief never names it.** Four of its tests — `Money_columns_are_numeric_19_4`, `Timestamp_columns_are_timestamptz`, `Currency_column_is_not_nullable`, `Reading_a_corrupted_currency_value_throws_instead_of_materializing_a_default` — query `money_probe_entities` and call `db.MoneyProbes` directly. Once the `DbSet` and the type are gone, this file **will not compile**. Delete all four methods now (open the file: they are the last four `[Fact]`s, right after `The_model_has_no_pending_changes`). The other three tests in that file (`Migrations_create_the_database_from_empty`, `Migrating_twice_is_a_no_op`, `The_model_has_no_pending_changes`) don't touch `MoneyProbeEntity` — leave them exactly as they are. `tests/Noof.Ledger.Persistence.Tests/MoneyStorageTests.cs` is untouched, per the brief — its `money_probe` table is raw SQL and shares nothing with the type being deleted.

- [ ] **Step 6: Run the tests again**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: everything builds. `LineItemMoneyMappingTests` and `SeedDataTests` still fail the same way as Step 4 (no migration yet — unaffected by this step). All other tests pass except `MigrationContractTests.The_model_has_no_pending_changes`, which still (correctly) fails until Step 7.

- [ ] **Step 7: Generate the migration**

```bash
dotnet ef migrations add AddCaptureModel --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence
```

This diffs the current model (seven new entities + seed data, `MoneyProbeEntity` gone) against the last snapshot in one shot, so the generated `Up` contains one `DropTable("money_probe_entities")`, seven `CreateTable`s, the three indexes from Step 3, and `InsertData` calls for the one wallet and twenty categories. **The file will have a block-scoped namespace and fail the build with `IDE0161`.** Convert `namespace Noof.Ledger.Persistence.Migrations { ... }` to the file-scoped `namespace Noof.Ledger.Persistence.Migrations;`, exactly as `InitialCreate.cs` and `AddAppUser.cs` already do. Leave the paired `.Designer.cs` and the regenerated `LedgerDbContextModelSnapshot.cs` untouched — both carry `// <auto-generated />`.

- [ ] **Step 8: Run `LineItemMoneyMappingTests` and `SeedDataTests` and watch them go green**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter "LineItemMoneyMappingTests|SeedDataTests"`
Expected: all five tests PASS now that the migration exists to create the tables `fixture.CreateContextAsync()` + `MigrateAsync` need. This closes the red/green loop opened in Step 3/4: red because the table didn't exist, green because it now does and the mapping written in Step 3 is correct.

- [ ] **Step 9: Write the failing write-once test**

Create `tests/Noof.Ledger.Persistence.Tests/MerchantAliasWriteOnceTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Npgsql;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class MerchantAliasWriteOnceTests(PostgresFixture fixture)
{
    static async Task<LedgerDbContext> SeedAliasAsync(PostgresFixture fixture)
    {
        var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var merchant = new Merchant { Id = Guid.NewGuid(), DisplayName = "Test Merchant", Kind = MerchantKind.Retail };
        db.Merchants.Add(merchant);
        db.MerchantAliases.Add(new MerchantAlias
        {
            Folded = "TEST MERCHANT",
            MerchantId = merchant.Id,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return db;
    }

    [Fact]
    public async Task Updating_an_existing_alias_is_rejected_by_the_database()
    {
        await using var db = await SeedAliasAsync(fixture);

        var act = async () => await db.Database.ExecuteSqlAsync(
            $"UPDATE merchant_aliases SET merchant_id = merchant_id WHERE folded = 'TEST MERCHANT'",
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<PostgresException>(
            "merchant_aliases is write-once; even a raw SQL UPDATE must be rejected by the database itself");
    }

    [Fact]
    public async Task Deleting_an_existing_alias_is_rejected_by_the_database()
    {
        await using var db = await SeedAliasAsync(fixture);

        var act = async () => await db.Database.ExecuteSqlAsync(
            $"DELETE FROM merchant_aliases WHERE folded = 'TEST MERCHANT'",
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<PostgresException>(
            "merchant_aliases is append-only; deleting history must be impossible even by direct SQL");
    }
    [Fact]
    public async Task Truncating_the_alias_table_is_rejected_by_the_database()
    {
        await using var db = await SeedAliasAsync(fixture);

        var act = async () => await db.Database.ExecuteSqlAsync(
            $"TRUNCATE merchant_aliases",
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<PostgresException>(
            "PostgreSQL never fires row-level triggers for TRUNCATE, so the write-once guard above does not see it");

        var survivors = await db.Database.SqlQuery<int>(
            $"SELECT count(*)::int FROM merchant_aliases",
            TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        survivors.Single().Should().Be(1,
            "a trigger that raised only after the truncate had already run would satisfy the assertion above");
    }
}
```

- [ ] **Step 10: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: all three new tests FAIL — the raw `UPDATE`, `DELETE` and `TRUNCATE` all succeed silently (no exception thrown), because nothing in the schema forbids them yet.

- [ ] **Step 11: Add the write-once trigger by hand**

In the migration generated in Step 7, inside `Up(MigrationBuilder migrationBuilder)`, immediately after the `CreateTable(name: "merchant_aliases", ...)` block, add:

```csharp
            migrationBuilder.Sql(
                """
                -- Must reference only TG_OP / TG_TABLE_NAME, never OLD or NEW: the TRUNCATE trigger
                -- below calls this same function FOR EACH STATEMENT, where neither exists, and the
                -- failure would surface at TRUNCATE time rather than at CREATE TRIGGER time.
                CREATE FUNCTION public.merchant_aliases_write_once() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'merchant_aliases is write-once: % on % is not permitted', TG_OP, TG_TABLE_NAME;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER merchant_aliases_write_once_guard
                    BEFORE UPDATE OR DELETE ON public.merchant_aliases
                    FOR EACH ROW EXECUTE FUNCTION public.merchant_aliases_write_once();

                -- PostgreSQL never fires ROW-level triggers for TRUNCATE, so the guard above
                -- does not see it: one statement empties the table with no error raised.
                -- Proven against a real database before this line existed.
                CREATE TRIGGER merchant_aliases_no_truncate
                    BEFORE TRUNCATE ON public.merchant_aliases
                    FOR EACH STATEMENT EXECUTE FUNCTION public.merchant_aliases_write_once();
                """);
```

In `Down(MigrationBuilder migrationBuilder)`, immediately before the `DropTable(name: "merchant_aliases", ...)` call, add:

```csharp
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS merchant_aliases_no_truncate ON public.merchant_aliases;
                DROP TRIGGER IF EXISTS merchant_aliases_write_once_guard ON public.merchant_aliases;
                DROP FUNCTION IF EXISTS public.merchant_aliases_write_once();
                """);
```

> **Why a trigger and not just "no `UpdatedAt` column".** The missing column stops a well-behaved caller from having anything to stamp. It does nothing to stop a raw `UPDATE merchant_aliases SET merchant_id = ...` — and a raw SQL statement, or a future `ExecuteUpdate`, bypasses EF's change tracker entirely, so init-only C# properties buy you nothing at that layer. The trigger is the only thing that makes "write-once" true regardless of caller. It is also completely invisible to `SchemaSnapshotTests` — `GenerateCreateScript()` renders the model, not raw SQL objects — which is exactly why Step 10's behavioural test exists and must not be deleted as redundant later.

- [ ] **Step 12: Run the write-once tests again**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: both tests PASS. Each test calls `fixture.CreateContextAsync()`, which creates a **brand-new, empty** database and then `MigrateAsync`s it — so the edited migration file is what gets applied; no separate database update is needed for the test suite to see this change.

- [ ] **Step 13: Regenerate `schema.expected.sql`**

`SchemaSnapshotTests.cs` is failing right now (it has been since Step 4 — every model change since then diverges from the committed snapshot). Regenerate the file deliberately rather than eyeballing a truncated assertion diff: temporarily edit `tests/Noof.Ledger.Persistence.Tests/SchemaSnapshotTests.cs`, adding one line right after `var actual = Normalise(...)`:

```csharp
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "tests", "Noof.Ledger.Persistence.Tests", "schema.expected.sql"), actual); // TEMPORARY
```

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter "SchemaSnapshotTests"`

**This repo does not use the SDK-default output layout.** `Directory.Build.props` sets `<ArtifactsPath>` to the repo-root `artifacts` folder, so `AppContext.BaseDirectory` at test run time is `artifacts/bin/Noof.Ledger.Persistence.Tests/debug/` — two levels below the repo root, not three below a project directory. Three `..` segments therefore land on `artifacts/schema.expected.sql` and the source file is never touched, with no error to tell you so. Four `..` reach the repo root, and the explicit path segments go the rest of the way (note: reading `SnapshotPath` — `AppContext.BaseDirectory` with no `..` — as the existing code does is correct for that read; it works only because the `.csproj`'s `CopyToOutputDirectory` keeps the output copy in sync with the source one on every build). **Delete the temporary line** immediately after running it once.

Run the same filtered command again: Expected: PASS, since the committed file and the generated script now agree.

Open `tests/Noof.Ledger.Persistence.Tests/schema.expected.sql` and confirm by eye:
- `money_probe_entities` is gone.
- Seven new `CREATE TABLE` blocks exist: `wallets`, `categories`, `merchants`, `merchant_aliases`, `transactions`, `line_items`, `categorization_jobs`.
- `categories` has a unique constraint/index on `slug`, and a foreign key on `parent_id` back to `categories`.
- `transactions` has a unique index on `(telegram_chat_id, telegram_message_id)`.
- `categorization_jobs` has an index on `(status, run_after)`.
- `line_items.amount` is `numeric(19,4)` and `line_items.currency` is `character varying(3) NOT NULL`.
- `transactions.status`, `merchants.kind`, `line_items.categorized_by` and `categorization_jobs.status` are all plain `integer` columns — not a Postgres native enum type, not `smallint`, not `text`. This is the one EF/Npgsql mapping mistake Step 3's callout warns about; it is otherwise invisible outside this file and the `Enum_columns_persist_as_plain_integers_...` test.
- `INSERT INTO wallets` (one row) and `INSERT INTO categories` (twenty rows, including one with `'Кофе'`) appear — `GenerateCreateScript()` includes `HasData` seed rows, since seeding is part of the model, not just of a migration.
- No trigger or function text appears anywhere — confirms the write-once guard is genuinely invisible to this gate, which is why Steps 9–12 exist.

- [ ] **Step 14: Apply the migration everywhere and run the full suite**

```bash
dotnet ef database update --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence
```

Then update the test template database, the same way Task 4 of Phase 0b did:

```bash
dotnet ef database update --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence --connection "$(powershell -NoProfile -Command "(Get-Content \$env:LOCALAPPDATA\NoofLedger\db.connection) -replace 'Database=postgres','Database=noof_ledger_test_template'")"
```

```bash
dotnet test --solution NoofLedger.slnx
```

Expected: all green — `CaptureModelTests`, `MerchantAliasWriteOnceTests`, `LineItemMoneyMappingTests`, `SeedDataTests`, `SchemaSnapshotTests`, and every pre-existing test (`AppUserModelTests`, the surviving three `MigrationContractTests`, `EfUserStoreTests`, etc.). (Task 4's report noted the `db.connection` file needed its UTF-8 BOM stripped before this exact substitution worked; if the same command errors on the connection string here, check for that first before assuming a new problem.)

- [ ] **Step 15: Commit**

```bash
git add src/Noof.Ledger.Persistence tests/Noof.Ledger.Persistence.Tests
git commit -m "feat(persistence): capture-path schema, category seed tree, and MoneyProbeEntity's retirement"
```

---
---

- [ ] **Step A1 (contract amendment): enforce "exactly one default wallet" in the schema**

`ICaptureStore.CaptureAsync` resolves the wallet by querying for `IsDefault == true` and refuses to
capture when there is no default. "Exactly one" must therefore be a database guarantee, not a
convention — two default wallets would make which wallet a spend lands in depend on row order.

A partial unique index over a constant expression is the only construct that expresses "at most one
row where this predicate holds". EF cannot express it, so it is raw SQL inside the migration, which
means **it is invisible to the golden-DDL gate** and needs its own behavioural test — exactly as the
`lower(username)` index is covered in `EfUserStoreTests.cs`.

Write the failing test first. Create `tests/Noof.Ledger.Persistence.Tests/WalletDefaultTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Npgsql;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class WalletDefaultTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_second_default_wallet_is_rejected_by_the_database()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        db.Wallets.Add(new Wallet
        {
            Id = Guid.CreateVersion7(),
            Name = "Second",
            Currency = CurrencyCode.Eur,
            IsDefault = true,
        });

        var act = async () => await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>()
            .WithInnerException<DbUpdateException, PostgresException>()
            .Where(e => e.SqlState == PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task A_second_non_default_wallet_is_accepted()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        db.Wallets.Add(new Wallet
        {
            Id = Guid.CreateVersion7(),
            Name = "Second",
            Currency = CurrencyCode.Eur,
            IsDefault = false,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Wallets.Count(w => !w.IsDefault).Should().Be(1);
    }
}
```

> The second test is not padding. A partial index written without its `WHERE` clause would make
> **every** wallet collide, so the first test would still pass while the schema had quietly become
> "at most one wallet, ever". Only the pair pins the actual requirement.

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: `A_second_default_wallet_is_rejected_by_the_database` FAILS — `SaveChangesAsync` succeeds and
no exception is thrown, because no index exists yet.

Then add the index to the migration created earlier in this task, inside `Up`, after the `wallets`
table is created:

```csharp
migrationBuilder.Sql("CREATE UNIQUE INDEX ix_wallets_single_default ON wallets ((true)) WHERE is_default;");
```

and in `Down`, before the table is dropped:

```csharp
migrationBuilder.Sql("DROP INDEX IF EXISTS ix_wallets_single_default;");
```

> `ON wallets ((true))` indexes a constant, so every row satisfying the `WHERE is_default` predicate
> maps to the same index key and the second one violates uniqueness. Indexing `(is_default)` instead
> would not express this: it would make the index unique over the column's value, which says nothing
> about how many rows may hold `true` once the predicate has already filtered them. Write the constant.

Run the tests again. Expected: both PASS.

Commit with the rest of this task.
### Task 4: Secrets at rest — Data Protection, ISecretStore, the app_secret table

**Files:**
- Create: `src/Noof.Ledger.Application/Secrets/SecretState.cs`
- Create: `src/Noof.Ledger.Application/Secrets/SecretResult.cs`
- Create: `src/Noof.Ledger.Application/Secrets/SecretStatus.cs`
- Create: `src/Noof.Ledger.Application/Secrets/ISecretStore.cs`
- Create: `src/Noof.Ledger.Application/Secrets/SecretKeys.cs`
- Create: `src/Noof.Ledger.Persistence/Secrets/AppSecret.cs`
- Create: `src/Noof.Ledger.Persistence/Configurations/AppSecretConfiguration.cs`
- Create: `src/Noof.Ledger.Persistence/Secrets/EfSecretStore.cs`
- Create: `src/Noof.Ledger.Persistence/Migrations/<timestamp>_AddAppSecret.cs` (+ `.Designer.cs`, generated)
- Create: `src/Noof.Ledger.Host/Startup/DataProtectionSetup.cs`
- Create: `src/Noof.Ledger.Host/PlatformSupport.cs`
- Create: `tests/Noof.Ledger.Persistence.Tests/AppSecretModelTests.cs`
- Create: `tests/Noof.Ledger.Persistence.Tests/EfSecretStoreTests.cs`
- Create: `tests/Noof.Ledger.Host.Tests/DataProtectionSetupTests.cs`
- Create: `tests/Noof.Ledger.Host.Tests/DataProtectionWiringTests.cs`
- Create: `tests/Noof.Ledger.Host.Tests/PlatformSupport.cs`
- Modify: `src/Noof.Ledger.Persistence/LedgerDbContext.cs`
- Modify: `src/Noof.Ledger.Persistence/Noof.Ledger.Persistence.csproj`
- Modify: `tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
- Modify: `Directory.Packages.props`
- Modify: `src/Noof.Ledger.Host/Program.cs`
- Modify: `tests/Noof.Ledger.Persistence.Tests/schema.expected.sql` (regenerated, not hand-typed)

**Interfaces:**
- Consumes: `LedgerDbContext` (Phase 0), the Data Protection APIs shipped in the ASP.NET Core shared framework.
- Produces: `ISecretStore` / `SecretResult` / `SecretState` / `SecretKeys` in `Noof.Ledger.Application.Secrets`; `AppSecret` / `EfSecretStore` in `Noof.Ledger.Persistence.Secrets`; `DataProtectionSetup.Configure(IServiceCollection, DirectoryInfo)` in `Noof.Ledger.Host.Startup`; the `app_secret` table; `ISecretStore`, `TimeProvider` and `IDataProtectionProvider` all resolvable from the Host's DI container from this task onward.

**Why this task is first.** Every later Phase 1 task that calls Anthropic or Telegram needs a key, and the key must never sit in `appsettings.json` on a public repo. Nothing outbound can be built before this exists.

---

- [ ] **Step 1: Write the Application contracts (no test — these are an interface, an enum, a DTO and a const holder; TDD is exempt for DTOs, and there is no behaviour here to fail red)**

Create `src/Noof.Ledger.Application/Secrets/SecretState.cs`:

```csharp
namespace Noof.Ledger.Application.Secrets;

public enum SecretState
{
    Present = 0,
    Missing = 1,
    Unreadable = 2,
}
```

Create `src/Noof.Ledger.Application/Secrets/SecretResult.cs`:

```csharp
namespace Noof.Ledger.Application.Secrets;

public readonly record struct SecretResult(SecretState State, string? Value);
```

Create `src/Noof.Ledger.Application/Secrets/ISecretStore.cs`:

```csharp
namespace Noof.Ledger.Application.Secrets;

public interface ISecretStore
{
    Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken);

    Task SetAsync(string key, string plaintext, CancellationToken cancellationToken);
}
```

Create `src/Noof.Ledger.Application/Secrets/SecretKeys.cs`:

```csharp
namespace Noof.Ledger.Application.Secrets;

public static class SecretKeys
{
    public const string AnthropicApiKey = "anthropic-api-key";
    public const string TelegramBotToken = "telegram-bot-token";
    public const string TelegramOwnerChatId = "telegram-owner-chat-id";
}
```

Run: `dotnet build src/Noof.Ledger.Application/Noof.Ledger.Application.csproj`
Expected: builds clean. `Noof.Ledger.Application` still has zero `PackageReference` — you have not added one.

---

- [ ] **Step 2: Write the failing model test for `AppSecret`**

Create `tests/Noof.Ledger.Persistence.Tests/AppSecretModelTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Persistence.Secrets;

namespace Noof.Ledger.Persistence.Tests;

public class AppSecretModelTests
{
    static LedgerDbContext BuildOfflineContext()
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none")
            .Options;

        return new LedgerDbContext(options);
    }

    [Fact]
    public void Maps_to_the_app_secret_table_with_snake_case_columns()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(AppSecret))!;

        entity.GetTableName().Should().Be("app_secret");
        entity.GetProperty(nameof(AppSecret.Key)).GetColumnName().Should().Be("key");
        entity.GetProperty(nameof(AppSecret.Ciphertext)).GetColumnName().Should().Be("ciphertext");
        entity.GetProperty(nameof(AppSecret.UpdatedAt)).GetColumnName().Should().Be("updated_at");
    }

    [Fact]
    public void The_primary_key_is_the_secret_key_itself()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(AppSecret))!;

        entity.FindPrimaryKey()!.Properties.Select(p => p.Name).Should().Equal(nameof(AppSecret.Key));
    }

    [Fact]
    public void Ciphertext_and_updated_at_are_required()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(AppSecret))!;

        entity.GetProperty(nameof(AppSecret.Ciphertext)).IsNullable.Should().BeFalse();
        entity.GetProperty(nameof(AppSecret.UpdatedAt)).GetColumnType().Should().Be("timestamptz");
    }
}
```

- [ ] **Step 3: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: BUILD FAILS — `CS0246: The type or namespace name 'AppSecret' could not be found`.

---

- [ ] **Step 4: Write the entity, its configuration, and wire the `DbSet`**

Create `src/Noof.Ledger.Persistence/Secrets/AppSecret.cs`:

```csharp
namespace Noof.Ledger.Persistence.Secrets;

public sealed class AppSecret
{
    public required string Key { get; init; }
    public required string Ciphertext { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
}
```

Create `src/Noof.Ledger.Persistence/Configurations/AppSecretConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Persistence.Secrets;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class AppSecretConfiguration : IEntityTypeConfiguration<AppSecret>
{
    public void Configure(EntityTypeBuilder<AppSecret> builder)
    {
        builder.ToTable("app_secret");

        builder.HasKey(s => s.Key);

        builder.Property(s => s.Key).HasColumnName("key");
        builder.Property(s => s.Ciphertext).HasColumnName("ciphertext").IsRequired();
        builder.Property(s => s.UpdatedAt).HasColumnName("updated_at");
    }
}
```

Neither property gets `HasMaxLength`. The contract calls for `key text` and `ciphertext text`, and a Postgres `string` property with no `HasMaxLength` maps to `text` under this context's conventions — exactly how `AppUser.PasswordHash` already does it.

Edit `src/Noof.Ledger.Persistence/LedgerDbContext.cs` — add the using and the `DbSet`:

```csharp
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Secrets;

namespace Noof.Ledger.Persistence;

public class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<MoneyProbeEntity> MoneyProbes => Set<MoneyProbeEntity>();

    public DbSet<AppUser> Users => Set<AppUser>();

    public DbSet<AppSecret> Secrets => Set<AppSecret>();

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

- [ ] **Step 5: Run the tests**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: the three new tests PASS. `MigrationContractTests.The_model_has_no_pending_changes` now FAILS — correct, the model changed and the next step adds the migration.

---

- [ ] **Step 6: Generate the migration**

```bash
dotnet ef migrations add AddAppSecret --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence
```

> This runs offline against `DesignTimeDbContextFactory`. **The generated file will have a block-scoped namespace and will fail the build with `IDE0161`** (an error here, per `Directory.Build.props`). Convert it to file-scoped. Leave the accompanying `.Designer.cs` alone — it carries `// <auto-generated />` and is exempt.

The generated `src/Noof.Ledger.Persistence/Migrations/<timestamp>_AddAppSecret.cs`, once converted to file-scoped, should read exactly like this — there is no case-insensitive index or other raw SQL to hand-add this time, unlike `AddAppUser`:

```csharp
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Noof.Ledger.Persistence.Migrations;

/// <inheritdoc />
public partial class AddAppSecret : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "app_secret",
            schema: "public",
            columns: table => new
            {
                key = table.Column<string>(type: "text", nullable: false),
                ciphertext = table.Column<string>(type: "text", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_app_secret", x => x.key);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "app_secret",
            schema: "public");
    }
}
```

If EF's actual output differs from this only in cosmetic ways (using order, blank lines), keep EF's version and just fix the namespace. If it differs in the columns or the constraint, stop and re-check Step 4 before continuing.

---

- [ ] **Step 7: Regenerate the golden DDL snapshot**

`schema.expected.sql` is diffed against `LedgerDbContext.Database.GenerateCreateScript()` — the **model**, not migration history — by `SchemaSnapshotTests.cs`. It already exists (it covers `app_user` and `money_probe_entities`), so this step updates it rather than creating it.

Temporarily edit `tests/Noof.Ledger.Persistence.Tests/SchemaSnapshotTests.cs`, replacing the assertion with a write:

```csharp
    [Fact]
    public void The_generated_schema_matches_the_committed_snapshot()
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none")
            .Options;

        using var db = new LedgerDbContext(options);

        var actual = Normalise(db.Database.GenerateCreateScript());
        File.WriteAllText(SnapshotPath, actual); // TEMPORARY — revert after this run
    }
```

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter SchemaSnapshotTests`

> **`SnapshotPath` is `AppContext.BaseDirectory`, and this repo does not use the SDK-default `bin/Debug/net10.0/` output path.** `Directory.Build.props` sets `<ArtifactsPath>` to the repo-root `artifacts` folder, so the file this test just wrote to actually landed at `artifacts/bin/Noof.Ledger.Persistence.Tests/debug/schema.expected.sql`. Looking in `bin/Debug/net10.0/` will not find it.

Copy the regenerated file back over the checked-in source:

```bash
cp artifacts/bin/Noof.Ledger.Persistence.Tests/debug/schema.expected.sql tests/Noof.Ledger.Persistence.Tests/schema.expected.sql
```

**Read it.** Confirm it now contains, alongside the unchanged `app_user` and `money_probe_entities` blocks:

```sql
CREATE TABLE public.app_secret (
    key text NOT NULL,
    ciphertext text NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT "PK_app_secret" PRIMARY KEY (key)
);
```

Revert the temporary edit to `SchemaSnapshotTests.cs` (restore the `actual.Should().Be(expected, ...)` assertion — `git diff` should show the file back to its original content except for whatever whitespace/ordering the tool reformatted).

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter SchemaSnapshotTests`
Expected: PASS.

---

- [ ] **Step 8: Apply the migration to the real database and the test template, then run everything**

```bash
dotnet ef database update --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence
```

`PostgresFixture` clones `noof_ledger_test_template` for every test that calls `CreateDatabaseAsync` (`MoneyStorageTests.cs` is the only current user of that path; everything else, including the new `EfSecretStoreTests` below, uses `CreateContextAsync` + an explicit `MigrateAsync`), so keeping the template current is good hygiene rather than a hard requirement of this task's own tests:

```bash
dotnet ef database update --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence --connection "$(powershell -NoProfile -Command "(Get-Content \$env:LOCALAPPDATA\NoofLedger\db.connection) -replace 'Database=postgres','Database=noof_ledger_test_template'")"
```

Run: `dotnet test --solution NoofLedger.slnx`
Expected: all green, including `MigrationContractTests`.

---

- [ ] **Step 9: Add the Data Protection packages this task needs**

Edit `Directory.Packages.props` — add one line to the first `<ItemGroup>` (production packages) and two to the `Testing` one:

```xml
  <ItemGroup>
    <PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.3" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.12" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Relational" Version="10.0.12" />
    <PackageVersion Include="Microsoft.AspNetCore.Components.Web" Version="10.0.8" />
    <PackageVersion Include="Microsoft.AspNetCore.Components.Authorization" Version="10.0.8" />
    <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.8" />
    <PackageVersion Include="Microsoft.AspNetCore.DataProtection.Abstractions" Version="10.0.12" />
  </ItemGroup>
  <ItemGroup Label="Testing">
    <PackageVersion Include="xunit.v3" Version="4.0.1" />
    <PackageVersion Include="xunit.v3.mtp-v2" Version="4.0.1" />
    <PackageVersion Include="AwesomeAssertions" Version="9.6.0" />
    <PackageVersion Include="NSubstitute" Version="6.2.0" />
    <PackageVersion Include="coverlet.MTP" Version="10.0.1" />
    <PackageVersion Include="TngTech.ArchUnitNET.xUnitV3" Version="0.13.4" />
    <PackageVersion Include="Microsoft.Playwright.Xunit.v3" Version="1.62.0" />
    <PackageVersion Include="Microsoft.AspNetCore.DataProtection.Extensions" Version="10.0.12" />
    <PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.10.0" />
  </ItemGroup>
```

> **Only `Anthropic` 12.49.0 and `Telegram.Bot` 22.10.3.1 are on the contract's pre-verified package list.** `Microsoft.AspNetCore.DataProtection.Abstractions`, `Microsoft.AspNetCore.DataProtection.Extensions` and `Microsoft.Extensions.TimeProvider.Testing` are not — the versions above are real, published versions (10.0.12 is current for both DataProtection packages; 10.10.0 exists for TimeProvider.Testing), but nobody has run a restore against this exact combined package set yet. The `dotnet restore` at the end of this step is the actual first verification, not a formality — do not skip it or assume it will pass because the numbers look plausible.
>
> **Check `Directory.Packages.props` for an existing `Microsoft.Extensions.TimeProvider.Testing` entry *before* adding this line, and re-check immediately before you save the file.** The standing rule above ("Inject `TimeProvider` anywhere time is read... `Microsoft.Extensions.TimeProvider.Testing` must be added") applies to every Phase 1A task that reads time, not just this one — several tasks in this same batch are likely to add this exact package concurrently, possibly at different versions, and Central Package Management allows exactly one pinned version per package id: two different `<PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing">` lines is a conflict, not something that merges. If another task's edit has already landed the entry when you get here, do not add a second one and do not overwrite its version — add only `<PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />` (no `Version=`) to this project's csproj, build against whatever version is already pinned, and only stop to flag it if that version is actually incompatible with something this task needs.

These three exact versions have been checked against the published NuGet feed and against the ASP.NET Core source for the API split this task depends on (`Protect(string)`/`Unprotect(string)` live in `Abstractions`; `DataProtectionProvider.Create(DirectoryInfo)` lives in `Extensions`), but that is not the same thing as a verified restore+build under this repo's exact gate set (`TreatWarningsAsErrors` + `NuGetAuditMode=all` + `CentralPackageTransitivePinningEnabled`) — treat Step 9's `dotnet restore` as doing that for the first time.

> `Microsoft.AspNetCore.DataProtection.Abstractions` is the interfaces-only package (`IDataProtectionProvider`, `IDataProtector`) — it has no dependency on the ASP.NET Core shared framework, which is why `Noof.Ledger.Persistence` (a plain `Microsoft.NET.Sdk` library) can take it as an ordinary NuGet package. Do **not** reference the full `Microsoft.AspNetCore.DataProtection` package from Persistence — that one is what the Host gets for free from the shared framework, and referencing it explicitly would fight that.

Edit `src/Noof.Ledger.Persistence/Noof.Ledger.Persistence.csproj` — add one line to the existing package `<ItemGroup>`:

```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
    <PackageReference Include="Microsoft.AspNetCore.DataProtection.Abstractions" />
  </ItemGroup>
```

Edit `tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj` — add two lines to its package `<ItemGroup>`:

```xml
  <ItemGroup>
    <PackageReference Include="AwesomeAssertions" />
    <PackageReference Include="xunit.v3.mtp-v2" />
    <PackageReference Include="Microsoft.AspNetCore.DataProtection.Extensions" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
  </ItemGroup>
```

`Microsoft.AspNetCore.DataProtection.Extensions` gives the test project the concrete `DataProtectionProvider.Create(DirectoryInfo)` factory — a real, working `IDataProtectionProvider` with no `IServiceCollection`/DI ceremony needed in the tests.

Run: `dotnet restore` (or just `dotnet build --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`)
Expected: restores clean, no `NU1605` (downgrade) or `NU1903`/`NU1904` (audit) errors, and no duplicate-`PackageVersion` conflict for `Microsoft.Extensions.TimeProvider.Testing`.

This does **not** touch `tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs`. Read it before assuming otherwise: it pins the exact package set only for `Noof.Ledger.Domain` (`BeEmpty()`), `Noof.Ledger.Application` (`BeEmpty()`) and `Noof.Ledger.Web` (`BeEquivalentTo`, exact). `Noof.Ledger.Persistence`'s package set is unconstrained by that test, so this step needs no architecture-test update.

---

- [ ] **Step 10: Write the failing `EfSecretStore` tests**

Create `tests/Noof.Ledger.Persistence.Tests/EfSecretStoreTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Persistence.Secrets;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfSecretStoreTests(PostgresFixture fixture) : IDisposable
{
    readonly DirectoryInfo keyRing = Directory.CreateTempSubdirectory("noof-secret-test-");

    public void Dispose() => keyRing.Delete(recursive: true);

    EfSecretStore CreateStore(LedgerDbContext db, TimeProvider? timeProvider = null) =>
        new(db, DataProtectionProvider.Create(keyRing), timeProvider ?? TimeProvider.System);

    [Fact]
    public async Task An_unknown_key_returns_Missing()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = CreateStore(db);

        var result = await store.GetAsync("does-not-exist", TestContext.Current.CancellationToken);

        result.Should().Be(new SecretResult(SecretState.Missing, null));
    }

    [Fact]
    public async Task Setting_then_getting_round_trips_the_plaintext()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = CreateStore(db);

        await store.SetAsync(SecretKeys.AnthropicApiKey, "sk-ant-secret", TestContext.Current.CancellationToken);
        var result = await store.GetAsync(SecretKeys.AnthropicApiKey, TestContext.Current.CancellationToken);

        result.Should().Be(new SecretResult(SecretState.Present, "sk-ant-secret"));
    }

    [Fact]
    public async Task Setting_the_same_key_twice_updates_the_row_instead_of_inserting_a_second_one()
    {
        // Mirrors commit a828608: EfUserStore once upserted by Id instead of the natural key, so a
        // second write silently inserted instead of updating — on the only account-recovery path
        // there was. AppSecret's primary key IS the lookup key, so there is no separate surrogate-key
        // bug possible here, but this still catches the simpler failure of a SetAsync that always inserts.
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = CreateStore(db);

        await store.SetAsync(SecretKeys.AnthropicApiKey, "first-value", TestContext.Current.CancellationToken);
        await store.SetAsync(SecretKeys.AnthropicApiKey, "second-value", TestContext.Current.CancellationToken);

        var result = await store.GetAsync(SecretKeys.AnthropicApiKey, TestContext.Current.CancellationToken);
        result.Should().Be(new SecretResult(SecretState.Present, "second-value"));

        var rowCount = await db.Secrets.CountAsync(TestContext.Current.CancellationToken);
        rowCount.Should().Be(1, "a second SetAsync for the same key must update, not insert");
    }

    [Fact]
    public async Task Setting_the_same_key_twice_advances_updated_at_using_the_injected_clock()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var store = CreateStore(db, clock);

        await store.SetAsync(SecretKeys.AnthropicApiKey, "first", TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromHours(1));
        await store.SetAsync(SecretKeys.AnthropicApiKey, "second", TestContext.Current.CancellationToken);

        var row = await db.Secrets.SingleAsync(TestContext.Current.CancellationToken);
        row.UpdatedAt.Should().Be(DateTimeOffset.Parse("2026-01-01T01:00:00Z"),
            "UpdatedAt must come from the injected TimeProvider, never DateTimeOffset.UtcNow directly");
    }

    [Fact]
    public async Task A_hand_edited_ciphertext_column_returns_Unreadable_instead_of_throwing()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = CreateStore(db);

        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO app_secret (key, ciphertext, updated_at) VALUES ({SecretKeys.TelegramBotToken}, 'not-a-real-ciphertext', now())",
            TestContext.Current.CancellationToken);

        var result = await store.GetAsync(SecretKeys.TelegramBotToken, TestContext.Current.CancellationToken);

        result.Should().Be(new SecretResult(SecretState.Unreadable, null));
    }
}
```

- [ ] **Step 11: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: BUILD FAILS — `CS0246: The type or namespace name 'EfSecretStore' could not be found`.

---

- [ ] **Step 12: Write `EfSecretStore`**

Create `src/Noof.Ledger.Persistence/Secrets/EfSecretStore.cs`:

```csharp
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Persistence.Secrets;

public sealed class EfSecretStore(LedgerDbContext db, IDataProtectionProvider dataProtection, TimeProvider timeProvider)
    : ISecretStore
{
    public async Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken)
    {
        var row = await db.Secrets.FindAsync([key], cancellationToken);
        if (row is null)
            return new SecretResult(SecretState.Missing, null);

        try
        {
            var plaintext = Protector(key).Unprotect(row.Ciphertext);
            return new SecretResult(SecretState.Present, plaintext);
        }
        catch (CryptographicException)
        {
            return new SecretResult(SecretState.Unreadable, null);
        }
    }

    public async Task SetAsync(string key, string plaintext, CancellationToken cancellationToken)
    {
        var ciphertext = Protector(key).Protect(plaintext);
        var now = timeProvider.GetUtcNow();

        var existing = await db.Secrets.FindAsync([key], cancellationToken);
        if (existing is null)
        {
            db.Secrets.Add(new AppSecret { Key = key, Ciphertext = ciphertext, UpdatedAt = now });
        }
        else
        {
            existing.Ciphertext = ciphertext;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    IDataProtector Protector(string key) => dataProtection.CreateProtector($"Noof.Ledger.Secrets.{key}");
}
```

> **`catch (CryptographicException)`, not `FormatException`.** `PasswordHasherAdapter.cs:24-29` catches `FormatException` because a corrupted base64 password hash fails at the base64-decode step. Data Protection's `Unprotect` does its own decode internally and wraps every failure — garbage text, garbage-but-valid base64, or a payload protected under a different purpose — in `System.Security.Cryptography.CryptographicException`. Catching `FormatException` here would compile, look plausible, and simply not catch anything: the hand-edited-column test would fail with an unhandled `CryptographicException` instead of returning `Unreadable`. Verified directly against ASP.NET Core's Data Protection source: `IDataProtector.Unprotect` wraps decode and unprotect failures in `CryptographicException`.

`Protector(key)` is the single place the purpose string is built — `GetAsync` and `SetAsync` both call it, so the purpose can never drift between a write and a later read of the same key.

`FindAsync([key], ...)` looks the row up **by the primary key**, which here is the same string the caller passes in. Unlike `EfUserStore` — where the lookup key (`Username`) and the primary key (`Id`) are different columns, which is exactly how commit a828608 happened — there is no second key to get wrong here.

- [ ] **Step 13: Run the tests**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: all green, including the five new `EfSecretStoreTests`.

---

- [ ] **Step 14: Write the failing Data Protection sentinel test**

Create `tests/Noof.Ledger.Host.Tests/DataProtectionSetupTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Host.Startup;

namespace Noof.Ledger.Host.Tests;

public class DataProtectionSetupTests
{
    [Fact]
    public void The_key_ring_never_stores_key_material_in_plaintext()
    {
        var keyRingDirectory = Directory.CreateTempSubdirectory("noof-dp-sentinel-");

        try
        {
            var services = new ServiceCollection();
            DataProtectionSetup.Configure(services, keyRingDirectory);

            using var provider = services.BuildServiceProvider();
            provider.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector(nameof(DataProtectionSetupTests))
                .Protect("force-a-key-to-be-generated");

            var keyRingFiles = Directory.GetFiles(keyRingDirectory.FullName, "key-*.xml");

            keyRingFiles.Should().NotBeEmpty("protecting a payload must generate a key file");

            foreach (var file in keyRingFiles)
            {
                File.ReadAllText(file).Should().NotContain("is in an unencrypted form",
                    $"{Path.GetFileName(file)} must not store its master key in plaintext — this is a public repo");
            }
        }
        finally
        {
            keyRingDirectory.Delete(recursive: true);
        }
    }
}
```

> **Why this specific string, and why a round-trip test would not have caught the bug this guards against.** `IDataProtectionProvider.Protect`/`Unprotect` round-trip correctly whether or not `ProtectKeysWithDpapi()` was called — DPAPI only changes how the *key* is stored at rest, not whether encryption works within the same process. A test that only calls `SetAsync`/`GetAsync` on `EfSecretStore` and checks the plaintext comes back would pass identically with `ProtectKeysWithDpapi()` silently deleted. `"is in an unencrypted form"` is the literal comment ASP.NET Core's Data Protection system writes into a key XML file if and only if that key's master key material is stored unencrypted. This is the one test in this task that actually distinguishes the two.

- [ ] **Step 15: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: BUILD FAILS — `CS0246: The type or namespace name 'DataProtectionSetup' could not be found`.

---

- [ ] **Step 16: Write `DataProtectionSetup`, and declare the Host as Windows-only**

Create `src/Noof.Ledger.Host/Startup/DataProtectionSetup.cs`:

```csharp
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Noof.Ledger.Host.Startup;

public static class DataProtectionSetup
{
    public static void Configure(IServiceCollection services, DirectoryInfo keyRingDirectory) =>
        services.AddDataProtection()
            .SetApplicationName("Noof.Ledger")
            .PersistKeysToFileSystem(keyRingDirectory)
            .ProtectKeysWithDpapi();
}
```

Create `src/Noof.Ledger.Host/PlatformSupport.cs`:

```csharp
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
```

Create `tests/Noof.Ledger.Host.Tests/PlatformSupport.cs`:

```csharp
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
```

> **You need BOTH files, or the build fails with `CA1416` (an error, per `TreatWarningsAsErrors`), and adding only the Host one is not enough.** `ProtectKeysWithDpapi()` is annotated `[SupportedOSPlatform("windows")]` inside ASP.NET Core itself — DPAPI genuinely does not exist off Windows. `[assembly: SupportedOSPlatform("windows")]` in `Host/PlatformSupport.cs` tells the analyzer the whole `Noof.Ledger.Host` assembly is Windows-only, which silences `CA1416` for `DataProtectionSetup.Configure`'s own body — but the analyzer then treats *every public member of that assembly* as equally Windows-only, which means `DataProtectionSetupTests` calling `DataProtectionSetup.Configure(...)` from `Noof.Ledger.Host.Tests` now trips the **same** `CA1416`, in a **different** project, unless `Noof.Ledger.Host.Tests` carries the identical assembly attribute. This is consistent with reality — this whole application already only runs on Windows (DPAPI, `%LOCALAPPDATA%`, the PowerShell ops scripts) — so this is an honest declaration, not a suppression.

- [ ] **Step 17: Run the tests**

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: PASS, zero warnings.

---

- [ ] **Step 18: Wire `Program.cs`** (wiring code — exempt from TDD, but it is checked by the next step's test)

Edit `src/Noof.Ledger.Host/Program.cs`. Add two usings, register `TimeProvider`, call `DataProtectionSetup.Configure` **before** `AddDbContext`, and register `ISecretStore`:

```csharp
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Host.Auth;
using Noof.Ledger.Host.Cli;
using Noof.Ledger.Host.Endpoints;
using Noof.Ledger.Host.Startup;
using Noof.Ledger.Persistence;
using Noof.Ledger.Persistence.Auth;
using Noof.Ledger.Persistence.Secrets;
using Noof.Ledger.Web.Components;

if (UserCommand.TryParse(args, out var cliUsername))
{
    Environment.ExitCode = await UserCommand.RunAsync(cliUsername, args);
    return;
}

var builder = WebApplication.CreateBuilder(args);

var authMode = builder.Configuration["Auth:Mode"] ?? "Off";
var cookieMode = authMode.Equals("Cookie", StringComparison.OrdinalIgnoreCase);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton(TimeProvider.System);

var dataProtectionKeyRingDirectory = new DirectoryInfo(Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoofLedger", "dp-keys"));
DataProtectionSetup.Configure(builder.Services, dataProtectionKeyRingDirectory);

builder.Services.AddDbContext<LedgerDbContext>(options =>
    options.UseNpgsql(LedgerConnectionString.Resolve(builder.Configuration.GetConnectionString("Ledger"))));

builder.Services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
builder.Services.AddScoped<IUserStore, EfUserStore>();
builder.Services.AddScoped<ISecretStore, EfSecretStore>();
```

Everything below this (the rest of the authentication setup, `app.Run()`, etc.) is unchanged.

> **`builder.Services.AddSingleton(TimeProvider.System)` is not optional and is easy to skip because nothing complains until runtime.** `WebApplication.CreateBuilder` does **not** register `TimeProvider` by default. Without this line, the moment anything resolves `EfSecretStore` (which takes `TimeProvider` in its constructor), DI throws `InvalidOperationException: Unable to resolve service for type 'System.TimeProvider'`. The build stays green; only a request that touches a secret fails.

This task does not touch `src/Noof.Ledger.Host/Cli/UserCommand.cs`. It never reads or writes a secret, so its hand-synced DI graph needs nothing added here.

- [ ] **Step 19: Write the confirming wiring test**

Create `tests/Noof.Ledger.Host.Tests/DataProtectionWiringTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Noof.Ledger.Host.Tests;

public class DataProtectionWiringTests
{
    static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:Mode", "Off");
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
        });

    [Fact]
    public void Keys_persist_under_the_dedicated_NoofLedger_folder()
    {
        using var factory = Factory();

        var options = factory.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;

        var repository = options.XmlRepository.Should().BeOfType<FileSystemXmlRepository>().Subject;
        repository.Directory.FullName.Should().Be(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoofLedger", "dp-keys"));
    }

    [Fact]
    public void Keys_are_protected_with_Dpapi()
    {
        using var factory = Factory();

        var options = factory.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;

        options.XmlEncryptor.Should().BeOfType<DpapiXmlEncryptor>();
    }
}
```

This resolves `IOptions<KeyManagementOptions>` only — it never calls `Protect`, so it never writes into the real `%LOCALAPPDATA%\NoofLedger\dp-keys` folder. It exists to catch a different mistake than Step 14's sentinel: "Program.cs forgot to call `DataProtectionSetup.Configure` at all" or "pointed it at the wrong folder" — a mistake the sentinel test, which calls `DataProtectionSetup.Configure` directly, cannot see.

`Microsoft.AspNetCore.Mvc.Testing` (already referenced by this project) is sufficient for these types — no new package reference is needed here.

- [ ] **Step 20: Run the tests**

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: all green, including both `DataProtectionWiringTests`.

---

- [ ] **Step 21: Run the full solution and the architecture suite**

```bash
dotnet test --solution NoofLedger.slnx
```

Expected: all green. In particular:
- `tests/Noof.Ledger.Architecture.Tests` stays green — `Domain_has_no_package_references` and `Application_has_no_package_references` are untouched by this task, and `Project_references_exactly_its_allowed_set` for `Noof.Ledger.Persistence` and `Noof.Ledger.Host` is a **project-reference** check, unaffected by the new package references added in Step 9.
- `SchemaSnapshotTests`, `MigrationContractTests`, `AppUserModelTests` and `EfUserStoreTests` are all still green — nothing here touched `app_user`.

---

- [ ] **Step 22: Commit**

```bash
git status
```

> Do not `git add` whole directories here, and do not assume you know what else is untracked or modified from a stale description — check `git status` immediately before staging and confirm every path it shows beyond this task's own files belongs to other, already-in-progress work you should leave alone. List the files this task touched explicitly, as below, regardless of what else is present.

```bash
git add src/Noof.Ledger.Application/Secrets \
        src/Noof.Ledger.Persistence/Secrets \
        src/Noof.Ledger.Persistence/Configurations/AppSecretConfiguration.cs \
        src/Noof.Ledger.Persistence/LedgerDbContext.cs \
        src/Noof.Ledger.Persistence/Migrations \
        src/Noof.Ledger.Persistence/Noof.Ledger.Persistence.csproj \
        src/Noof.Ledger.Host/Startup/DataProtectionSetup.cs \
        src/Noof.Ledger.Host/PlatformSupport.cs \
        src/Noof.Ledger.Host/Program.cs \
        tests/Noof.Ledger.Persistence.Tests/AppSecretModelTests.cs \
        tests/Noof.Ledger.Persistence.Tests/EfSecretStoreTests.cs \
        tests/Noof.Ledger.Persistence.Tests/SchemaSnapshotTests.cs \
        tests/Noof.Ledger.Persistence.Tests/schema.expected.sql \
        tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj \
        tests/Noof.Ledger.Host.Tests/DataProtectionSetupTests.cs \
        tests/Noof.Ledger.Host.Tests/DataProtectionWiringTests.cs \
        tests/Noof.Ledger.Host.Tests/PlatformSupport.cs \
        Directory.Packages.props

git commit -m "feat(secrets): Data Protection, ISecretStore/EfSecretStore, and the app_secret table"
```

---

**Non-goals, explicitly:** no `secret set` CLI verb (not asked for, and `UserCommand.cs`'s second DI graph is therefore untouched), no `/settings/secrets` UI page, no actual Anthropic/Telegram token stored anywhere yet — this task only proves the storage mechanism is sound. Later tasks that need a real key call `ISecretStore.SetAsync` themselves.
---

- [ ] **Step A1 (contract amendment): `GetStatusAsync`, so the UI has no way to obtain a plaintext secret**

The settings page must show whether each secret is set and when it was last changed. The obvious route
is to call `GetAsync` and render everything except `Value` — and that is exactly the arrangement that
goes wrong later, because nothing stops the next edit from rendering it. A separate status query means
the UI assembly has **no method available to it that returns a secret at all**, which is a rule a
reviewer can enforce by reading one line instead of auditing a page.

`GetStatusAsync` still decrypts internally and throws the plaintext away. That is deliberate: the one
failure this page genuinely must surface is a lost or rotated DPAPI key ring, which makes every stored
secret permanently unreadable. Reporting `Present` in that state — all a row-existence check could
report — would tell the operator their tokens are fine while nothing works.

Create `src/Noof.Ledger.Application/Secrets/SecretStatus.cs`:

```csharp
namespace Noof.Ledger.Application.Secrets;

// Deliberately carries no value. See ISecretStore.GetStatusAsync.
public readonly record struct SecretStatus(SecretState State, DateTimeOffset? UpdatedAt);
```

Add to `src/Noof.Ledger.Application/Secrets/ISecretStore.cs`, between `GetAsync` and `SetAsync`:

```csharp
    // Reports state and age without ever returning the secret. Decrypts internally and discards the
    // result, so a lost key ring is reported as Unreadable rather than as a healthy Present.
    Task<SecretStatus> GetStatusAsync(string key, CancellationToken cancellationToken);
```

Write the failing tests. Add to `tests/Noof.Ledger.Persistence.Tests/EfSecretStoreTests.cs`:

```csharp
    [Fact]
    public async Task Status_reports_missing_for_a_key_that_was_never_set()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = CreateStore(db);

        var status = await store.GetStatusAsync("never-set", TestContext.Current.CancellationToken);

        status.State.Should().Be(SecretState.Missing);
        status.UpdatedAt.Should().BeNull();
    }

    [Fact]
    public async Task Status_reports_present_with_the_time_it_was_set()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero));

        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = CreateStore(db, clock);

        await store.SetAsync(SecretKeys.AnthropicApiKey, "sk-whatever", TestContext.Current.CancellationToken);

        var status = await store.GetStatusAsync(SecretKeys.AnthropicApiKey, TestContext.Current.CancellationToken);

        status.State.Should().Be(SecretState.Present);
        status.UpdatedAt.Should().Be(clock.GetUtcNow());
    }

    [Fact]
    public async Task Status_reports_unreadable_when_the_ciphertext_will_not_decrypt()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = CreateStore(db);

        await store.SetAsync(SecretKeys.TelegramBotToken, "123456:real-looking-token", TestContext.Current.CancellationToken);

        await db.Database.ExecuteSqlAsync(
            $"UPDATE app_secret SET ciphertext = 'not-protected-text' WHERE key = {SecretKeys.TelegramBotToken}",
            TestContext.Current.CancellationToken);

        // Raw SQL goes round the change tracker, so FindAsync would otherwise return the entity
        // still cached from SetAsync and never see the corruption. Production never hits this:
        // LedgerDbContext is scoped per request, so a corrupt row is always read from a fresh one.
        db.ChangeTracker.Clear();

        var status = await store.GetStatusAsync(SecretKeys.TelegramBotToken, TestContext.Current.CancellationToken);

        status.State.Should().Be(SecretState.Unreadable);
    }
```

> The third test is the one that earns its keep. Without it, an implementation that skips decryption
> and returns `Present` whenever the row exists passes the other two — and then tells the operator
> their tokens are fine on the exact day their key ring is gone and nothing works. Note it corrupts
> the column with raw SQL rather than through the store: the store has no way to write an invalid
> ciphertext, and the corruption this simulates comes from outside the application.

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: all three FAIL to compile — `'ISecretStore' does not contain a definition for 'GetStatusAsync'`.

Then implement it in `src/Noof.Ledger.Persistence/Secrets/EfSecretStore.cs`, directly after `GetAsync`:

```csharp
    public async Task<SecretStatus> GetStatusAsync(string key, CancellationToken cancellationToken)
    {
        var row = await db.Secrets.FindAsync([key], cancellationToken);
        if (row is null)
            return new SecretStatus(SecretState.Missing, null);

        try
        {
            // The plaintext is discarded on purpose: this method exists so the UI can learn the
            // state without ever being handed the value.
            _ = Protector(key).Unprotect(row.Ciphertext);
            return new SecretStatus(SecretState.Present, row.UpdatedAt);
        }
        catch (CryptographicException)
        {
            return new SecretStatus(SecretState.Unreadable, row.UpdatedAt);
        }
    }
```

Run the tests again. Expected: all three PASS.

Commit with the rest of this task.
### Task 5: The settings page — pasting tokens into the UI

**Before you start — a prerequisite, not a contract problem:** this task assumes `ISecretStore` already has a concrete implementation registered in DI in `src/Noof.Ledger.Host/Program.cs` (delivered by an earlier task in this phase, the same way Task 2 assumes Task 1's Domain types exist). Building an encrypted secret store is a separate, much bigger job than a settings page and is out of scope here.

- [ ] **Step 1: Confirm the prerequisites**

```bash
grep -n "ISecretStore" src/Noof.Ledger.Host/Program.cs
```

If this returns nothing, stop — that earlier task must land first. Do not implement an `ISecretStore` here.

Also confirm (read-only, no edit needed): `src/Noof.Ledger.Web/Noof.Ledger.Web.csproj` already has `<ProjectReference Include="..\Noof.Ledger.Application\Noof.Ledger.Application.csproj" />`, and `tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs` already expects exactly `["Noof.Ledger.Application", "Noof.Ledger.Domain"]` for `Noof.Ledger.Web`. Both are already true today — this task injects `ISecretStore` (an `Application` type) into a new page and needs no `ProjectReference` or `PackageReference` change, so `ProjectReferenceTests` is untouched.

**Files:**
- Create: `src/Noof.Ledger.Web/Components/Pages/Settings/Secrets.razor`
- Create: `tests/Noof.Ledger.E2E.Tests/SettingsSecretsTests.cs`
- Create: `tests/Noof.Ledger.Architecture.Tests/SecretsPageSourceTests.cs`
- Modify: `tests/Noof.Ledger.E2E.Tests/HostProcess.cs`
- Modify: `tests/Noof.Ledger.E2E.Tests/CookieModeHostFixture.cs`
- Not modified: `src/Noof.Ledger.Host/Program.cs` (DI registration is a prior task's job — see Step 1), `src/Noof.Ledger.Web/Noof.Ledger.Web.csproj`

**Interfaces:**
- Consumes: `ISecretStore`, `SecretStatus`, `SecretState`, `SecretKeys` from `Noof.Ledger.Application.Secrets`.
- Produces: route `GET /settings/secrets`; `HostProcess.CapturedOutputLines` / `CookieModeHostFixture.CapturedOutputLines` (new, additive — later E2E tasks can reuse them to assert on console output).

---

#### Why the first test is a live-browser test, not an HTTP test

> No page in this codebase yet combines `[Authorize]` with an interactive render mode, and this page's Save button is a plain `@onclick` handler with no `<form>` and no antiforgery token anywhere near it — nothing like `Login.razor`'s bare POST form. A `WebApplicationFactory` + `HttpClient` test can prove a redirect happens, but it can never drive a SignalR circuit, so it can't prove a circuit-based save actually works without the form/antiforgery machinery Login.razor needed. Only a real browser (Playwright, already wired up in `Noof.Ledger.E2E.Tests` for exactly this — see `SmokeTests.Clicking_the_counter_button_updates_the_count_via_the_server_circuit`) can drive both halves in one test.

The test also needs somewhere to capture console output for the log-scrubbing requirement. `HostProcess` already redirects stdout/stderr but only listens to it transiently, while waiting for the "Now listening on:" line — the listener is detached right after. Steps 2–3 make that capture permanent and expose it.

- [ ] **Step 2: Make `HostProcess` capture output for its whole lifetime**

In `tests/Noof.Ledger.E2E.Tests/HostProcess.cs`, change the field block:

```csharp
sealed partial class HostProcess : IAsyncDisposable
{
    Process? process;
    string? publishDirectory;

    public string BaseUrl { get; private set; } = string.Empty;
```

to:

```csharp
sealed partial class HostProcess : IAsyncDisposable
{
    readonly List<string> capturedOutputLines = [];
    readonly Lock captureLock = new();

    Process? process;
    string? publishDirectory;

    public string BaseUrl { get; private set; } = string.Empty;

    public IReadOnlyList<string> CapturedOutputLines
    {
        get { lock (captureLock) { return [.. capturedOutputLines]; } }
    }
```

Then in `StartAsync`, subscribe right after the process is launched:

```csharp
    public async Task StartAsync(
        string publishDirectory, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
    {
        this.publishDirectory = publishDirectory;
        process = Launch(publishDirectory, environment);
        process.OutputDataReceived += CaptureLine;
        process.ErrorDataReceived += CaptureLine;

        try
```

(the rest of `StartAsync` is unchanged). Add the handler as a new private method anywhere in the class body:

```csharp
    void CaptureLine(object? sender, DataReceivedEventArgs e)
    {
        if (e.Data is null)
            return;

        lock (captureLock)
            capturedOutputLines.Add(e.Data);
    }
```

> This coexists fine with the existing temporary `OnLine` handler inside `WaitForListeningUrlAsync` — multiple subscribers on the same `OutputDataReceived` event both just fire independently. Nothing about the existing startup-detection logic changes.

- [ ] **Step 3: Expose it on the fixture**

In `tests/Noof.Ledger.E2E.Tests/CookieModeHostFixture.cs`, change:

```csharp
    public string BaseUrl => host.BaseUrl;
```

to:

```csharp
    public string BaseUrl => host.BaseUrl;

    public IReadOnlyList<string> CapturedOutputLines => host.CapturedOutputLines;

    // Reads back what the browser-driven save actually persisted, bypassing the UI (which by
    // design never shows a saved secret's plaintext -- see SecretsPageSourceTests). Talks to the
    // same clone database and the same DPAPI-protected key ring directory
    // (Program.cs: %LocalApplicationData%\NoofLedger\dp-keys) the spawned host process itself uses,
    // so decrypting a value the host encrypted moments earlier just works: DPAPI is scoped to the
    // current Windows user, not to a process, and both this test and the host run as that user.
    public async Task<string?> ReadStoredSecretAsync(string key, CancellationToken cancellationToken)
    {
        // DataProtectionSetup.Configure calls ProtectKeysWithDpapi, which is Windows-only -- this
        // whole suite (like the app it drives) already only ever runs on Windows, so this guard is
        // just what tells the platform-compatibility analyzer that, rather than a real fallback.
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("This suite only runs on Windows, same as the app.");

        var contextOptions = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(DatabaseSettings.For(cloneDatabaseName))
            .Options;
        await using var db = new LedgerDbContext(contextOptions);

        var keyRingDirectory = new DirectoryInfo(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoofLedger", "dp-keys"));
        var services = new ServiceCollection();
        DataProtectionSetup.Configure(services, keyRingDirectory);
        await using var dataProtectionServices = services.BuildServiceProvider();
        var dataProtection = dataProtectionServices.GetRequiredService<IDataProtectionProvider>();

        var store = new EfSecretStore(db, dataProtection, TimeProvider.System);
        var result = await store.GetAsync(key, cancellationToken);
        return result.Value;
    }
```

Add the usings this needs at the top of the file: `Microsoft.AspNetCore.DataProtection`, `Microsoft.EntityFrameworkCore`, `Microsoft.Extensions.DependencyInjection`, `Noof.Ledger.Application.Secrets`, `Noof.Ledger.Host.Startup`, `Noof.Ledger.Persistence`, `Noof.Ledger.Persistence.Secrets` (alongside the existing `System.Diagnostics`, `Noof.Ledger.TestKit`, `Npgsql`). All of these resolve through the project's existing `ProjectReference`s to `Noof.Ledger.Host` and `Noof.Ledger.TestKit` — no new `PackageReference` is needed, confirmed by building. `ReadStoredSecretAsync` is not used yet at this point in the task; Step 4 below is what calls it.

> **Why this method exists at all (2026-09-21 review).** `Saving_trims_surrounding_whitespace_before_storing` (added in Step 4) needs to prove that whitespace was actually stripped before storage, not merely that the UI still says "Set" — the page never renders a saved secret's value back (by design), so there is no way to observe trimming through the browser alone. Reading the ciphertext back through a second `EfSecretStore`, pointed at the identical key ring directory the spawned host process already uses, is the only way to check without changing what the page itself exposes.

- [ ] **Step 4: Write the failing E2E tests**

Create `tests/Noof.Ledger.E2E.Tests/SettingsSecretsTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.E2E.Tests;

public sealed class SettingsSecretsTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    [Fact]
    public async Task Anonymous_visitor_is_redirected_and_a_signed_in_visitor_saves_through_the_circuit()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await Page.GotoAsync(fixture.BaseUrl + "/settings/secrets");
        await Page.WaitForURLAsync("**/account/login*");

        await Page.FillAsync("input[name='username']", CookieModeHostFixture.Username);
        await Page.FillAsync("input[name='password']", CookieModeHostFixture.Password);
        await Page.ClickAsync("button[type='submit']");
        await Page.WaitForURLAsync(fixture.BaseUrl + "/");

        await Page.GotoAsync(fixture.BaseUrl + "/settings/secrets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var status = Page.Locator($"#status-{SecretKeys.AnthropicApiKey}");
        await Expect(status).ToContainTextAsync("Not set");

        var value = $"sk-e2e-{Guid.NewGuid():N}";
        await RetryUntilAsync(async () =>
        {
            await Page.Locator($"#secret-{SecretKeys.AnthropicApiKey}").FillAsync(value);
            await Page.Locator($"#save-{SecretKeys.AnthropicApiKey}").ClickAsync();
            await Expect(status).ToContainTextAsync("Set", new() { Timeout = 2_000 });
        });
    }

    [Fact]
    public async Task Saving_a_blank_value_is_refused_and_the_secret_stays_missing()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/settings/secrets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var status = Page.Locator($"#status-{SecretKeys.TelegramOwnerChatId}");
        await Expect(status).ToContainTextAsync("Not set");

        // No FillAsync call: the field starts empty, which is exactly the "clicked Save without
        // typing anything" scenario the review proved stores state Present with "".
        await Page.Locator($"#save-{SecretKeys.TelegramOwnerChatId}").ClickAsync();

        // Storing an empty value as Present would reject every chat AND leave the owner-claim
        // recovery path disarmed, silently.
        await Expect(status).ToContainTextAsync("Not set");
        await Expect(Page.Locator($"#error-{SecretKeys.TelegramOwnerChatId}")).ToBeVisibleAsync();

        var stored = await fixture.ReadStoredSecretAsync(SecretKeys.TelegramOwnerChatId, TestContext.Current.CancellationToken);
        stored.Should().BeNull("a blank save must leave no row at all, not a Present row holding an empty string");
    }

    [Fact]
    public async Task Saving_trims_surrounding_whitespace_before_storing()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/settings/secrets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var padded = $"   123456:e2e-trim-{Guid.NewGuid():N}   ";
        var status = Page.Locator($"#status-{SecretKeys.TelegramBotToken}");

        await RetryUntilAsync(async () =>
        {
            await Page.Locator($"#secret-{SecretKeys.TelegramBotToken}").FillAsync(padded);
            await Page.Locator($"#save-{SecretKeys.TelegramBotToken}").ClickAsync();
            await Expect(status).ToContainTextAsync("Set", new() { Timeout = 2_000 });
        });

        var stored = await fixture.ReadStoredSecretAsync(SecretKeys.TelegramBotToken, TestContext.Current.CancellationToken);
        stored.Should().Be(padded.Trim(),
            "an untrimmed leading space or trailing newline makes every call using this token 404 with only an opaque log line to show for it");
    }

    [Fact]
    public async Task No_captured_log_line_contains_a_value_submitted_through_this_page()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/settings/secrets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var status = Page.Locator($"#status-{SecretKeys.TelegramBotToken}");
        var value = $"sk-e2e-log-check-{Guid.NewGuid():N}";

        await RetryUntilAsync(async () =>
        {
            await Page.Locator($"#secret-{SecretKeys.TelegramBotToken}").FillAsync(value);
            await Page.Locator($"#save-{SecretKeys.TelegramBotToken}").ClickAsync();
            await Expect(status).ToContainTextAsync("Set", new() { Timeout = 2_000 });
        });

        fixture.CapturedOutputLines.Should().NotContain(
            line => line.Contains(value, StringComparison.Ordinal),
            "a secret value must never reach the console log");
    }

    async Task SignInAsync()
    {
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForURLAsync("**/account/login*");
        await Page.FillAsync("input[name='username']", CookieModeHostFixture.Username);
        await Page.FillAsync("input[name='password']", CookieModeHostFixture.Password);
        await Page.ClickAsync("button[type='submit']");
        await Page.WaitForURLAsync(fixture.BaseUrl + "/");
    }

    static async Task RetryUntilAsync(Func<Task> attempt, int maxAttempts = 5)
    {
        for (var attemptNumber = 1; attemptNumber <= maxAttempts; attemptNumber++)
        {
            try
            {
                await attempt();
                return;
            }
            catch (PlaywrightException) when (attemptNumber < maxAttempts)
            {
            }
        }
    }
}
```

> The `RetryUntilAsync` wrapper mirrors `SmokeTests.RetryUntilAsync` exactly. `WaitForLoadStateAsync(NetworkIdle)` can resolve before the SignalR circuit has actually finished attaching — a click that lands in that gap is simply lost, not an error. This has been reproduced in this codebase already (see the comment above the counter-click test in `SmokeTests.cs`); do not skip it here just because there's no comment reminding you a second time.
>
> **`Saving_a_blank_value_is_refused_and_the_secret_stays_missing` and `Saving_trims_surrounding_whitespace_before_storing`, added by a later security review (2026-09-21).** A closing review proved two things against the page as originally drafted below: saving an empty value stored state `Present` with `""` — for `telegram-owner-chat-id` that rejects every chat AND leaves the first-message owner-claim recovery path disarmed, silently, since `Present` looks identical to a real value everywhere else that reads it — and a bot token pasted with a leading space or trailing newline was stored verbatim and then 404s on every call with only an opaque log line to show for it. Both tests must be written and seen to fail (Step 5) **before** `SaveAsync` gains the trim/refuse logic in Step 8 — writing them afterward would prove nothing.

- [ ] **Step 5: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj`

Expected: `LoginTests` and `SmokeTests` still PASS (unaffected — Steps 2–3 were purely additive). All four new facts FAIL:
- `Anonymous_visitor_is_redirected_and_a_signed_in_visitor_saves_through_the_circuit` throws `Microsoft.Playwright.PlaywrightException: Timeout 30000ms exceeded` from `WaitForURLAsync("**/account/login*")` — the route doesn't exist yet, so there is nothing to redirect from (an unmatched Blazor route with no `<NotFound>` fragment responds 404, not a redirect).
- `Saving_a_blank_value_is_refused_and_the_secret_stays_missing` and `Saving_trims_surrounding_whitespace_before_storing` fail the same way, for the same reason — the route doesn't exist yet.
- `No_captured_log_line_contains_a_value_submitted_through_this_page` throws a Playwright timeout from `Locator.FillAsync` on `#secret-telegram-bot-token` — no such element exists yet.

- [ ] **Step 6: Write the failing source-constraint test**

Create `tests/Noof.Ledger.Architecture.Tests/SecretsPageSourceTests.cs`:

```csharp
using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

public class SecretsPageSourceTests
{
    static string SourceText() => File.ReadAllText(Path.Combine(
        RepoRoot.Find().FullName, "src", "Noof.Ledger.Web", "Components", "Pages", "Settings", "Secrets.razor"));

    [Fact]
    public void Reads_status_only_and_never_the_plaintext_secret()
    {
        var source = SourceText();

        source.Should().Contain("GetStatusAsync(",
            "the settings page must show set/not-set status");
        source.Should().NotContain("GetAsync(",
            "ISecretStore.GetAsync hands back a plaintext secret - the settings page has no business holding one");
    }

    [Fact]
    public void Disables_prerendering_so_a_typed_secret_never_reaches_served_HTML()
    {
        SourceText().Should().Contain("prerender: false");
    }

    [Fact]
    public void Secret_inputs_opt_out_of_browser_autocomplete()
    {
        SourceText().Should().Contain("autocomplete=\"off\"");
    }
}
```

> `"GetStatusAsync("` does not contain `"GetAsync("` as a substring (`...tusAsync(` vs `GetAsync(`), so the `NotContain` check is safe against the method it's meant to allow.
>
> This is a source-text check, not a behavioral one, deliberately: with prerendering disabled, `OnInitializedAsync` never runs during a plain HTTP GET (it only runs once the circuit connects), so there is no fast, network-free way to observe "which method got called" without a live browser. A text check keeps this specific rule enforceable in the same fast, DB-free loop as every other architecture test, exactly like the codebase's existing `lower(username)` index note: some rules get a behavioral test, some get a structural one, chosen by what's actually checkable.

- [ ] **Step 7: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`

Expected: FAIL — `System.IO.FileNotFoundException` from `File.ReadAllText`, because `Secrets.razor` does not exist yet.

- [ ] **Step 8: Write the page**

Create `src/Noof.Ledger.Web/Components/Pages/Settings/Secrets.razor`:

```razor
@page "/settings/secrets"
@attribute [Authorize]
@using Noof.Ledger.Application.Secrets
@inject ISecretStore SecretStore
@* Prerendering would run this component's code during the initial static HTTP response, letting a
   fetched secret status leak into the served HTML before the interactive circuit even exists - so
   this page never prerenders. *@
@rendermode @(new InteractiveServerRenderMode(prerender: false))

<PageTitle>Secrets</PageTitle>

<h1>Secrets</h1>

@foreach (var row in rows)
{
    <section>
        <h2>@row.Label</h2>
        <p id="status-@row.Key">@Describe(row.Status)</p>
        @if (row.Error is not null)
        {
            <p id="error-@row.Key">@row.Error</p>
        }
        <input id="secret-@row.Key" type="password" autocomplete="off" @bind="row.PendingValue" />
        <button id="save-@row.Key" type="button" @onclick="() => SaveAsync(row)">Save</button>
    </section>
}

@code {
    sealed class Row
    {
        public required string Key { get; init; }
        public required string Label { get; init; }
        public SecretStatus Status { get; set; }
        public string PendingValue { get; set; } = string.Empty;
        public string? Error { get; set; }
    }

    readonly List<Row> rows =
    [
        new() { Key = SecretKeys.AnthropicApiKey, Label = "Anthropic API key" },
        new() { Key = SecretKeys.TelegramBotToken, Label = "Telegram bot token" },
        new() { Key = SecretKeys.TelegramOwnerChatId, Label = "Telegram owner chat id" },
    ];

    protected override async Task OnInitializedAsync()
    {
        foreach (var row in rows)
            row.Status = await SecretStore.GetStatusAsync(row.Key, CancellationToken.None);
    }

    async Task SaveAsync(Row row)
    {
        // Trim first: a leading space or trailing newline pasted alongside a token is invisible in
        // a password input and would otherwise be stored verbatim, breaking every call that uses it
        // (a bot token 404s; a chat id matches nothing) with only an opaque downstream error to show
        // for it. An empty result after trimming must be refused, not stored: for
        // telegram-owner-chat-id specifically, storing "" still leaves SecretState.Present, which
        // both rejects every chat AND leaves the first-message owner claim permanently disarmed --
        // silently, since Present looks the same as a real value everywhere else that reads it.
        var trimmed = row.PendingValue.Trim();
        if (trimmed.Length == 0)
        {
            row.Error = "Enter a value before saving.";
            return;
        }

        row.Error = null;
        await SecretStore.SetAsync(row.Key, trimmed, CancellationToken.None);
        row.PendingValue = string.Empty;
        row.Status = await SecretStore.GetStatusAsync(row.Key, CancellationToken.None);
    }

    static string Describe(SecretStatus status) => status.State switch
    {
        SecretState.Present => $"Set (updated {status.UpdatedAt?.ToString("u") ?? "unknown"})",
        SecretState.Missing => "Not set",
        SecretState.Unreadable => "Unreadable - re-enter this value",
        _ => "Unknown",
    };
}
```

> `@rendermode InteractiveServer` (the bare static property from `_Imports.razor`'s `@using static ... RenderMode`) defaults to `prerender: true`. Writing that instead of the explicit `new InteractiveServerRenderMode(prerender: false)` above compiles fine, looks right, and silently reintroduces the exact leak this task exists to prevent — it will not fail loudly, only the architecture test in Step 6 will catch it.
>
> There is no `<form>` anywhere on this page and no `<AntiforgeryToken />`. That is intentional, not an oversight: `Save` is a circuit method invocation over the already-authenticated SignalR connection, not an HTTP POST, so `Login.razor`'s `[FromForm]`-engages-antiforgery mechanism (see `AccountEndpoints.cs`) has nothing to attach to here and does not need to.
>
> `ISecretStore.GetAsync` never appears in this file. `SecretStatus` structurally cannot carry a plaintext value (it's `State` + `UpdatedAt` only) — so "never render a stored secret back" is guaranteed by the type the page is restricted to, not by a rule someone has to remember to follow.
>
> **Trim-and-refuse, added by a later security review (2026-09-21) — keep this proportionate.** This is a trim and an emptiness check, not a validation framework: do not add shape validation for a token's format against Telegram's own rules. `SaveAsync` must trim before checking for emptiness and before saving — trimming after the emptiness check would let a whitespace-only value slip through as "non-empty". The `row.Error` field and its paragraph exist so refusal is visible to the operator (`#error-@row.Key`), not merely silent — "tell the operator rather than storing nothing silently" is itself part of the fix, not incidental to it.

- [ ] **Step 9: Run the architecture test again**

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`

Expected: PASS.

- [ ] **Step 10: Run the E2E tests again**

Run: `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj`

Expected: PASS — all six tests in the project (`LoginTests`, the two `SmokeTests`, all four new `SettingsSecretsTests` facts) green. `No_captured_log_line_contains_a_value_submitted_through_this_page` passing right now only shows nothing *currently* logs the value — it doesn't yet prove the test would catch it if something did. Steps 11–12 fix that.

- [ ] **Step 11: Prove the log-scrubbing test has teeth**

Temporarily add one line to `SaveAsync` in `Secrets.razor`, right after the trim/refuse check, logging the (already-trimmed) value that is about to be saved:

```csharp
    async Task SaveAsync(Row row)
    {
        var trimmed = row.PendingValue.Trim();
        if (trimmed.Length == 0)
        {
            row.Error = "Enter a value before saving.";
            return;
        }

        Console.WriteLine($"Saving {row.Key}: {trimmed}");
        row.Error = null;
        await SecretStore.SetAsync(row.Key, trimmed, CancellationToken.None);
        row.PendingValue = string.Empty;
        row.Status = await SecretStore.GetStatusAsync(row.Key, CancellationToken.None);
    }
```

Run: `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj`

Expected: `No_captured_log_line_contains_a_value_submitted_through_this_page` FAILS — the assertion message shows the injected line containing the submitted value. The other five tests still PASS. (Each `dotnet test` run here republishes `Noof.Ledger.Host` from source, so this edit is picked up automatically — no manual publish step.)

- [ ] **Step 12: Remove the diagnostic line**

Delete the `Console.WriteLine` line added in Step 11, restoring `SaveAsync` to its Step 8 form.

Run: `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj`

Expected: PASS again, all six tests.

- [ ] **Step 13: Confirm the rest of the suite is unaffected**

Run: `dotnet test --solution NoofLedger.slnx`

Expected: all green, no regressions. (`Noof.Ledger.E2E.Tests` is not part of `NoofLedger.slnx` — same as every other E2E task in this project — so this run and Steps 5/7/9/10/11/12 are checking disjoint things.)

- [ ] **Step 14: Commit**

```bash
git add src/Noof.Ledger.Web/Components/Pages/Settings/Secrets.razor tests/Noof.Ledger.E2E.Tests/SettingsSecretsTests.cs tests/Noof.Ledger.E2E.Tests/HostProcess.cs tests/Noof.Ledger.E2E.Tests/CookieModeHostFixture.cs tests/Noof.Ledger.Architecture.Tests/SecretsPageSourceTests.cs
git commit -m "feat(web): secrets settings page - gated, unprerendered, and proven not to leak tokens into logs"
```
### Task 6: The capture store — one transaction, two rows

**Depends on:** Tasks 1-5. This task assumes `Noof.Ledger.Domain` already has `Wallet`, `Transaction`, `CategorizationJob`, `TransactionStatus` and `JobStatus` (Task 2), and that `LedgerDbContext` already exposes `Wallets`, `Transactions` and `CategorizationJobs` `DbSet<T>` properties backed by a migration (a task before this one). If those DbSets do not exist yet under those exact names, stop and land that task first — do not improvise table or DbSet names here, since nothing below depends on what the underlying SQL looks like.

**Files:**
- Create: `src/Noof.Ledger.Application/Capture/CapturedMessage.cs`
- Create: `src/Noof.Ledger.Application/Capture/ICaptureStore.cs`
- Create: `src/Noof.Ledger.Persistence/Capture/EfCaptureStore.cs`
- Create: `tests/Noof.Ledger.Persistence.Tests/ThrowsBeforeCommitInterceptor.cs`
- Create: `tests/Noof.Ledger.Persistence.Tests/EfCaptureStoreTests.cs`
- Create: `tests/Noof.Ledger.Persistence.Tests/CapturedMessageTests.cs`
- Create: `src/Noof.Ledger.Host/Startup/CaptureTimeZoneGuard.cs`
- Create: `tests/Noof.Ledger.Host.Tests/CaptureTimeZoneGuardTests.cs`
- Modify: `Directory.Packages.props`
- Modify: `tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
- Modify: `tests/Noof.Ledger.Persistence.Tests/PostgresFixture.cs`
- Modify: `src/Noof.Ledger.Host/Program.cs`
- Modify: `src/Noof.Ledger.Host/appsettings.json`

**Interfaces:**
- Consumes: `Wallet`, `Transaction`, `CategorizationJob`, `TransactionStatus`, `JobStatus` (Domain), `LedgerDbContext` (Persistence), `PostgresFixture` (existing test infra).
- Produces: `CapturedMessage`, `ICaptureStore`, `EfCaptureStore(LedgerDbContext, TimeProvider) : ICaptureStore`, `CaptureTimeZoneGuard.Resolve(string) : TimeZoneInfo`.

Verified context, do not re-derive:
- `LedgerDbContext` today only has `MoneyProbes` and `Users`; its `ConfigureConventions` already maps every `decimal` to `numeric(19,4)` and every `DateTimeOffset` to `timestamptz`, so no column typing work is needed here.
- `PostgresFixture.CreateContextAsync()` creates a brand-new, unmigrated, empty database per call and returns a `LedgerDbContext` against it — every existing caller (`EfUserStoreTests`) calls `await db.Database.MigrateAsync(...)` itself right after. Follow the same shape.
- `Directory.Build.props` sets `<InvariantGlobalization>false</InvariantGlobalization>` already, which is required for IANA time zone lookups to work at all on this box.
- `Microsoft.Extensions.TimeProvider.Testing` is not yet referenced anywhere in this repo. Current published version is **10.10.0**.

> **Program.cs wiring and the two `appsettings.json` lines are TDD-exempt**, per the standing rules. Everything else in this task gets a failing test first.

- [ ] **Step 1: Add the FakeTimeProvider package, and create the capture ports**

Add to the `Testing`-labelled `ItemGroup` in `Directory.Packages.props`:

```xml
<PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.10.0" />
```

Add to `tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`, in the existing `<ItemGroup>` that already has `AwesomeAssertions`:

```xml
<PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
```

Create `src/Noof.Ledger.Application/Capture/CapturedMessage.cs`:

```csharp
namespace Noof.Ledger.Application.Capture;

public sealed record CapturedMessage(long ChatId, int MessageId, string Text, DateTimeOffset SentAt)
{
    public DateTimeOffset SentAt { get; init; } = SentAt.Offset == TimeSpan.Zero
        ? SentAt
        : throw new ArgumentException($"must be UTC (Offset == TimeSpan.Zero), but was {SentAt.Offset}.", nameof(SentAt));
}
```

> **Closing-review fix (2026-09-21): `SentAt` rejects a non-UTC offset at construction.** As originally drafted above, `CapturedMessage` was a plain positional record with no validation — a non-UTC `SentAt` was accepted silently here and only failed much later, inside `EfCaptureStore.CaptureAsync`'s `SaveChangesAsync`, with an Npgsql message ("Cannot write DateTimeOffset with Offset=... to PostgreSQL type 'timestamp with time zone'") that names neither `CapturedMessage` nor `SentAt`. Overriding the auto-generated `SentAt` property with a validating initializer that still reads from the primary constructor parameter (the pattern used here) fails immediately at the one place every caller constructs this value, with a message that names the parameter and the actual rule.

Create `tests/Noof.Ledger.Persistence.Tests/CapturedMessageTests.cs`, proven red first — before the validating initializer above exists, the first fact reports "Expected a System.ArgumentException to be thrown, but no exception was thrown":

```csharp
using AwesomeAssertions;
using Noof.Ledger.Application.Capture;

namespace Noof.Ledger.Persistence.Tests;

public class CapturedMessageTests
{
    [Fact]
    public void Constructing_with_a_non_UTC_SentAt_throws_immediately_instead_of_failing_later_at_the_database()
    {
        var nonUtc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(2));

        var act = () => new CapturedMessage(1, 100, "coffee 3.50", nonUtc);

        act.Should().Throw<ArgumentException>().WithMessage("*UTC*").And.ParamName.Should().Be("SentAt");
    }

    [Fact]
    public void Constructing_with_a_UTC_SentAt_succeeds()
    {
        var utc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var message = new CapturedMessage(1, 100, "coffee 3.50", utc);

        message.SentAt.Should().Be(utc);
    }
}
```

Create `src/Noof.Ledger.Application/Capture/ICaptureStore.cs`:

```csharp
namespace Noof.Ledger.Application.Capture;

public interface ICaptureStore
{
    // Writes the Transaction AND its CategorizationJob inside ONE database transaction.
    // Resolves the wallet itself: the single Wallet with IsDefault = true. No default wallet
    // throws InvalidOperationException - a capture with no wallet cannot be recovered later.
    // Idempotent on (ChatId, MessageId): a replayed Telegram update returns the existing id,
    // writes nothing.
    Task<Guid> CaptureAsync(CapturedMessage message, string timeZoneId, CancellationToken cancellationToken);

    Task AttachBotMessageAsync(Guid transactionId, int botMessageId, CancellationToken cancellationToken);
}
```

These two files are a fixed contract and a plain interface, so there is no failing test to write for them - they are the DTO/interface exemption. `CapturedMessage` and `ICaptureStore` are consumed starting in the next step.

- [ ] **Step 2: Extend `PostgresFixture` with a reusable connection-string helper**

The atomicity test (Step 3) needs to build a *second*, independently-configured `LedgerDbContext` against the same freshly-created database - one with a fault-injecting interceptor attached, one without. `CreateContextAsync()` currently hides the database name and hands back only a ready-made context, so there is no way to open a second connection to the same database. Pull the database-creation logic out into its own method and have `CreateContextAsync()` call it, keeping `CreateContextAsync()`'s signature and behavior completely unchanged for every existing caller.

In `tests/Noof.Ledger.Persistence.Tests/PostgresFixture.cs`, replace the existing `CreateContextAsync` method:

```csharp
    public async Task<LedgerDbContext> CreateContextAsync()
    {
        var name = $"noof_test_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(DatabaseSettings.AdminConnectionString))
        {
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        created.Add(name);

        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(DatabaseSettings.For(name))
            .Options;

        return new LedgerDbContext(options);
    }
```

with these two methods:

```csharp
    public async Task<string> CreateEmptyDatabaseConnectionStringAsync()
    {
        var name = $"noof_test_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(DatabaseSettings.AdminConnectionString))
        {
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        created.Add(name);

        return DatabaseSettings.For(name);
    }

    public async Task<LedgerDbContext> CreateContextAsync()
    {
        var connectionString = await CreateEmptyDatabaseConnectionStringAsync();

        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new LedgerDbContext(options);
    }
```

Nothing else in the file changes. This is a pure refactor of test infrastructure, not new behavior, so there is no failing test for this step either - it sets up Step 3.

- [ ] **Step 3: Write the failing tests for `EfCaptureStore`**

Create `tests/Noof.Ledger.Persistence.Tests/ThrowsBeforeCommitInterceptor.cs`:

```csharp
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Noof.Ledger.Persistence.Tests;

sealed class ThrowsBeforeCommitInterceptor : DbTransactionInterceptor
{
    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Simulated failure between the writes and the commit.");
}
```

> **Do not try to prove atomicity by counting `DbCommand` executions and throwing on the second one.** Npgsql's EF Core provider can coalesce both INSERT statements from a single `SaveChangesAsync` call into one batched round trip, so there may only ever be ONE command to intercept, not two - a command-counting interceptor would fire at the wrong time or never fire, and that behavior is a provider implementation detail, not something this test should depend on. Intercepting the transaction commit instead is robust regardless of batching: nothing is durable until commit succeeds, so throwing there and then checking row counts through a separate, uninvolved connection proves the same guarantee without caring how EF grouped the SQL.

Create `tests/Noof.Ledger.Persistence.Tests/EfCaptureStoreTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Capture;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Capture;

namespace Noof.Ledger.Persistence.Tests;

> **The database is not empty after `MigrateAsync` here.** Task 3 seeds a wallet and twenty
> categories through `HasData`, and `ix_wallets_single_default` permits only one default wallet — so
> a test that migrates and then inserts its own `IsDefault = true` wallet fails with a `23505` unique
> violation, not with the assertion it was written for. Every test in this class calls
> `RemoveSeededDefaultWalletAsync` immediately after `MigrateAsync`.

[Collection("postgres")]
public class EfCaptureStoreTests(PostgresFixture fixture)
{
    static Wallet DefaultWallet() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Cash",
        Currency = CurrencyCode.Eur,
        IsDefault = true,
    };

    // Task 3 seeds one default wallet through HasData, and ix_wallets_single_default makes a second
    // one a 23505 unique violation. Every test below therefore clears the seeded row before adding
    // the wallet it wants. Call this after MigrateAsync and before any Wallets.Add.
    static async Task RemoveSeededDefaultWalletAsync(LedgerDbContext db) =>
        await db.Database.ExecuteSqlAsync(
            $"DELETE FROM wallets WHERE is_default",
            TestContext.Current.CancellationToken);

    static CapturedMessage NewMessage(long chatId = 1, int messageId = 100, DateTimeOffset? sentAt = null) =>
        new(chatId, messageId, "coffee 3.50", sentAt ?? DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Throws_when_no_wallet_is_marked_default()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = new EfCaptureStore(db, new FakeTimeProvider());

        var act = async () =>
            await store.CaptureAsync(NewMessage(), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*default*");
    }

    [Fact]
    public async Task Captures_a_transaction_and_a_pending_job_in_one_call()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = DefaultWallet();
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
        var store = new EfCaptureStore(db, new FakeTimeProvider(now));
        var message = NewMessage(sentAt: now);

        var transactionId = await store.CaptureAsync(message, "Europe/Belgrade", TestContext.Current.CancellationToken);

        var transaction = await db.Transactions.SingleAsync(TestContext.Current.CancellationToken);
        transaction.Id.Should().Be(transactionId);
        transaction.WalletId.Should().Be(wallet.Id);
        transaction.RawText.Should().Be("coffee 3.50");
        transaction.Status.Should().Be(TransactionStatus.Captured);
        transaction.TimeZoneId.Should().Be("Europe/Belgrade");
        transaction.TelegramChatId.Should().Be(1);
        transaction.TelegramMessageId.Should().Be(100);
        transaction.BotMessageId.Should().BeNull();
        transaction.CreatedAt.Should().Be(now);

        var job = await db.CategorizationJobs.SingleAsync(TestContext.Current.CancellationToken);
        job.TransactionId.Should().Be(transactionId);
        job.Status.Should().Be(JobStatus.Pending);
        job.AttemptCount.Should().Be(0);
        job.RunAfter.Should().Be(now);
        job.CreatedAt.Should().Be(now);
        job.UpdatedAt.Should().Be(now);
    }

    [Fact]
    public async Task Occurred_at_is_the_telegram_messages_own_timestamp_not_when_it_was_processed()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.Wallets.Add(DefaultWallet());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var sentAt = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
        var processedAt = sentAt.AddHours(3);
        var store = new EfCaptureStore(db, new FakeTimeProvider(processedAt));
        var message = NewMessage(sentAt: sentAt);

        await store.CaptureAsync(message, "Europe/Belgrade", TestContext.Current.CancellationToken);

        var transaction = await db.Transactions.SingleAsync(TestContext.Current.CancellationToken);
        transaction.OccurredAt.Should().Be(sentAt, "an outage delayed processing, not the purchase itself");
        transaction.CreatedAt.Should().Be(processedAt);
    }

    [Fact]
    public async Task Replaying_the_same_chat_and_message_id_returns_the_existing_transaction_without_writing_a_second_job()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.Wallets.Add(DefaultWallet());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfCaptureStore(db, new FakeTimeProvider());
        var message = NewMessage();

        var first = await store.CaptureAsync(message, "Europe/Belgrade", TestContext.Current.CancellationToken);
        var second = await store.CaptureAsync(message, "Europe/Belgrade", TestContext.Current.CancellationToken);

        second.Should().Be(first);
        (await db.Transactions.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        (await db.CategorizationJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task A_replay_succeeds_even_if_no_wallet_is_marked_default_any_more()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = DefaultWallet();
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfCaptureStore(db, new FakeTimeProvider());
        var message = NewMessage();
        var first = await store.CaptureAsync(message, "Europe/Belgrade", TestContext.Current.CancellationToken);

        wallet.IsDefault = false;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var second = await store.CaptureAsync(message, "Europe/Belgrade", TestContext.Current.CancellationToken);

        second.Should().Be(first, "a replay must not fail just because the wallet lookup would now throw");
    }

    [Fact]
    public async Task A_failure_before_commit_leaves_neither_row_behind()
    {
        var connectionString = await fixture.CreateEmptyDatabaseConnectionStringAsync();

        await using (var seed = new LedgerDbContext(
            new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options))
        {
            await seed.Database.MigrateAsync(TestContext.Current.CancellationToken);
            seed.Wallets.Add(DefaultWallet());
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var breaking = new LedgerDbContext(
            new DbContextOptionsBuilder<LedgerDbContext>()
                .UseNpgsql(connectionString)
                .AddInterceptors(new ThrowsBeforeCommitInterceptor())
                .Options);
        var store = new EfCaptureStore(breaking, new FakeTimeProvider());

        var act = async () =>
            await store.CaptureAsync(NewMessage(), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var verify = new LedgerDbContext(
            new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options);
        (await verify.Transactions.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0,
            "the commit never happened, so the transaction insert must not be visible either");
        (await verify.CategorizationJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task AttachBotMessageAsync_stamps_the_bot_message_id_onto_the_row()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.Wallets.Add(DefaultWallet());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfCaptureStore(db, new FakeTimeProvider());
        var transactionId =
            await store.CaptureAsync(NewMessage(), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await store.AttachBotMessageAsync(transactionId, 555, TestContext.Current.CancellationToken);

        var transaction = await db.Transactions.SingleAsync(TestContext.Current.CancellationToken);
        transaction.BotMessageId.Should().Be(555);
    }
}
```

> **Never attach `ThrowsBeforeCommitInterceptor` to the context you call `MigrateAsync` on.** EF's migrator commits its own transaction while applying migrations, so an interceptor that throws on every commit fails during schema creation, before the test ever calls `CaptureAsync`. That is exactly why `A_failure_before_commit_leaves_neither_row_behind` uses THREE separate contexts against the same connection string: one plain one to migrate and seed the wallet, one with the interceptor to make the call under test, and a third plain one afterward to verify nothing persisted.

- [ ] **Step 4: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: build error - `EfCaptureStore` does not exist in `Noof.Ledger.Persistence.Capture`.

- [ ] **Step 5: Write `EfCaptureStore`**

Create `src/Noof.Ledger.Persistence/Capture/EfCaptureStore.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Capture;
using Noof.Ledger.Domain;
using Npgsql;

namespace Noof.Ledger.Persistence.Capture;

public sealed class EfCaptureStore(LedgerDbContext db, TimeProvider timeProvider) : ICaptureStore
{
    public async Task<Guid> CaptureAsync(CapturedMessage message, string timeZoneId, CancellationToken cancellationToken)
    {
        var existing = await FindExistingAsync(message, cancellationToken);

        if (existing is not null)
            return existing.Id;

        // Checked before the wallet lookup: a replay of an already captured message must still
        // succeed even after the default wallet has been unmarked.
        var wallet = await db.Wallets.SingleOrDefaultAsync(w => w.IsDefault, cancellationToken)
            ?? throw new InvalidOperationException(
                "No wallet is marked as the default. Capture cannot proceed without one.");

        var now = timeProvider.GetUtcNow();
        var transactionId = Guid.NewGuid();

        var transaction = new Transaction
        {
            Id = transactionId,
            WalletId = wallet.Id,
            RawText = message.Text,
            Status = TransactionStatus.Captured,
            TimeZoneId = timeZoneId,
            OccurredAt = message.SentAt,
            TelegramChatId = message.ChatId,
            TelegramMessageId = message.MessageId,
            CreatedAt = now,
        };
        var job = new CategorizationJob
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            Status = JobStatus.Pending,
            AttemptCount = 0,
            RunAfter = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Transactions.Add(transaction);
        db.CategorizationJobs.Add(job);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return transactionId;
        }
        catch (DbUpdateException ex) when (IsDuplicateCaptureViolation(ex))
        {
            // Another concurrent call for the same (ChatId, MessageId) committed first. Ours never
            // did; detach both rows so the re-read below goes back to the database instead of
            // returning these uncommitted, never-persisted entities from the identity map.
            db.Entry(transaction).State = EntityState.Detached;
            db.Entry(job).State = EntityState.Detached;

            var winner = await FindExistingAsync(message, cancellationToken)
                ?? throw new InvalidOperationException(
                    "A unique-constraint violation on capture reported a winner that cannot be found.");
            return winner.Id;
        }
    }

    Task<Transaction?> FindExistingAsync(CapturedMessage message, CancellationToken cancellationToken) =>
        db.Transactions.SingleOrDefaultAsync(
            t => t.TelegramChatId == message.ChatId && t.TelegramMessageId == message.MessageId,
            cancellationToken);

    static bool IsDuplicateCaptureViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_transactions_telegram_chat_id_telegram_message_id",
        };

    public async Task AttachBotMessageAsync(Guid transactionId, int botMessageId, CancellationToken cancellationToken)
    {
        var transaction = await db.Transactions.SingleAsync(t => t.Id == transactionId, cancellationToken);
        transaction.BotMessageId = botMessageId;

        await db.SaveChangesAsync(cancellationToken);
    }
}
```

> The idempotency lookup runs before the wallet lookup, not after. Swap the order and `A_replay_succeeds_even_if_no_wallet_is_marked_default_any_more` starts failing: a replay of an already-captured message would throw `InvalidOperationException` the moment nobody is marked default any more, even though nothing new needs to be written.
>
> **Plan defect, fixed above.** The version originally drafted here let a `DbUpdateException`/`PostgresException` 23505 on the `(ChatId, MessageId)` unique index escape uncaught whenever two callers raced the same capture concurrently — the write-path stayed correct (exactly one transaction, one job), but the losing caller's `CaptureAsync` threw instead of honouring the idempotency the interface promises. The `catch` above narrows on the specific constraint name, not every `DbUpdateException`, re-reads, and returns the winner's id; add the concurrency test below to `Step 3`'s test file to prove it (a sequential replay test cannot observe this — see `EfCaptureStoreTests.cs`'s note on it).

Add this test to `tests/Noof.Ledger.Persistence.Tests/EfCaptureStoreTests.cs` from Step 3, directly before `AttachBotMessageAsync_stamps_the_bot_message_id_onto_the_row`:

```csharp
    // ICaptureStore.CaptureAsync promises idempotency on (ChatId, MessageId). A plain sequential
    // replay test (above) cannot tell "idempotent" from "the loser throws a raw 23505 that a caller
    // happens not to hit" - only genuine concurrency does. This forces it the same way the owner-claim
    // race test does: hold the winning insert open inside an uncommitted transaction so the second
    // caller's insert must block on the database lock, not race past it, before either result is
    // observed.
    [Fact]
    public async Task Concurrent_CaptureAsync_calls_for_the_same_chat_and_message_let_exactly_one_caller_insert()
    {
        await using var dbA = await fixture.CreateContextAsync();
        await dbA.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await RemoveSeededDefaultWalletAsync(dbA, TestContext.Current.CancellationToken);
        dbA.Wallets.Add(DefaultWallet());
        await dbA.SaveChangesAsync(TestContext.Current.CancellationToken);

        var optionsB = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(dbA.Database.GetConnectionString()!)
            .Options;
        await using var dbB = new LedgerDbContext(optionsB);

        var storeA = new EfCaptureStore(dbA, new FakeTimeProvider());
        var storeB = new EfCaptureStore(dbB, new FakeTimeProvider());
        var message = NewMessage();

        await using var txA = await dbA.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var idA = await storeA.CaptureAsync(message, "Europe/Belgrade", TestContext.Current.CancellationToken);

        var idBTask = storeB.CaptureAsync(message, "Europe/Belgrade", TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(idBTask, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        finished.Should().NotBeSameAs(idBTask,
            "the second capture's insert must block on the first's uncommitted row, not race past it undetected");

        await txA.CommitAsync(TestContext.Current.CancellationToken);

        var idB = await idBTask;

        idB.Should().Be(idA, "both callers captured the identical (ChatId, MessageId); the loser must return the winner's id, not throw");
        (await dbA.Transactions.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        (await dbA.CategorizationJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }
```

- [ ] **Step 6: Run the persistence tests green**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: all green, including the 8 `EfCaptureStoreTests` (7 original plus the concurrency test above).

- [ ] **Step 7: Write the failing test for `CaptureTimeZoneGuard`**

Create `tests/Noof.Ledger.Host.Tests/CaptureTimeZoneGuardTests.cs`:

```csharp
using AwesomeAssertions;
using Noof.Ledger.Host.Startup;

namespace Noof.Ledger.Host.Tests;

public class CaptureTimeZoneGuardTests
{
    [Fact]
    public void The_configured_default_resolves_on_this_machine()
    {
        var act = () => CaptureTimeZoneGuard.Resolve("Europe/Belgrade");

        act.Should().NotThrow(".NET resolves IANA ids through ICU; this is the test that proves it works here, on this OS, rather than assuming it");
    }

    [Fact]
    public void An_unknown_zone_id_fails_loudly_with_a_clear_message()
    {
        var act = () => CaptureTimeZoneGuard.Resolve("Not/A/Real/Zone");

        act.Should().Throw<InvalidOperationException>().WithMessage("*Capture:TimeZone*");
    }

    [Fact]
    public void A_Windows_time_zone_id_is_rejected_even_though_FindSystemTimeZoneById_accepts_it()
    {
        // TimeZoneInfo.FindSystemTimeZoneById also accepts a Windows id on Windows, but
        // time_zone_id is documented (Transaction.cs) and consumed downstream as IANA. Verified on
        // this machine: TryConvertWindowsIdToIanaId("Central Europe Standard Time", ...) succeeds
        // (-> "Europe/Budapest"), while TryConvertWindowsIdToIanaId("Europe/Belgrade", ...) fails --
        // that asymmetry is the cheapest available discriminator between the two id families.
        var act = () => CaptureTimeZoneGuard.Resolve("Central Europe Standard Time");

        act.Should().Throw<InvalidOperationException>().WithMessage("*Capture:TimeZone*");
    }
}
```

> **This is the test the task explicitly calls out: it confirms `TimeZoneInfo.FindSystemTimeZoneById` actually accepts `Europe/Belgrade` on this machine, rather than assuming it.** `Directory.Build.props` already sets `InvariantGlobalization` to `false`, which globalization-invariant .NET builds would otherwise need for any IANA id lookup to work at all - if this test ever fails, check that setting before suspecting the id itself.
>
> **`FindSystemTimeZoneById` alone is not enough (2026-09-21 review).** On Windows it also accepts a Windows id such as `"Central Europe Standard Time"`, which would then pass a guard whose error message promises IANA and get written into `time_zone_id` -- which `Transaction.cs` documents, and Phase 1B reads back, as IANA. `TimeZoneInfo.TryConvertWindowsIdToIanaId` is the cheapest discriminator: a Windows id converts to an IANA one; an IANA id does not convert further. Implement the extra check in Step 9 below — do not defer it.

- [ ] **Step 8: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: build error - `CaptureTimeZoneGuard` does not exist in `Noof.Ledger.Host.Startup`.

- [ ] **Step 9: Write `CaptureTimeZoneGuard`**

Create `src/Noof.Ledger.Host/Startup/CaptureTimeZoneGuard.cs`:

```csharp
namespace Noof.Ledger.Host.Startup;

public static class CaptureTimeZoneGuard
{
    public static TimeZoneInfo Resolve(string configuredId)
    {
        TimeZoneInfo resolved;
        try
        {
            resolved = TimeZoneInfo.FindSystemTimeZoneById(configuredId);
        }
        catch (Exception exposed) when (exposed is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw NotAnIanaId(configuredId, exposed);
        }

        // FindSystemTimeZoneById also accepts a Windows id (e.g. "Central Europe Standard Time")
        // on Windows, but Transaction.TimeZoneId is documented and consumed downstream as IANA.
        // TryConvertWindowsIdToIanaId only ever succeeds for a Windows id -- an IANA id passed to
        // it returns false -- which makes it the cheapest available discriminator: if the
        // configured id itself converts, it was never IANA to begin with.
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(configuredId, out _))
            throw NotAnIanaId(configuredId, exposed: null);

        return resolved;
    }

    static InvalidOperationException NotAnIanaId(string configuredId, Exception? exposed) =>
        new($"Capture:TimeZone '{configuredId}' is not a valid IANA time zone id on this machine.", exposed);
}
```

- [ ] **Step 10: Run the host tests green**

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: all green, including the 3 new `CaptureTimeZoneGuardTests`.

- [ ] **Step 11: Wire it into `Program.cs` and `appsettings.json`**

This step is Program.cs wiring - TDD-exempt, no new test.

In `src/Noof.Ledger.Host/appsettings.json`, add a `Capture` section:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Auth": { "Mode": "Off" },
  "Database": { "MigrateOnStartup": true },
  "ConnectionStrings": { "Ledger": "" },
  "Capture": { "TimeZone": "Europe/Belgrade" }
}
```

Replace `src/Noof.Ledger.Host/Program.cs` in full with:

```csharp
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Application.Capture;
using Noof.Ledger.Host.Auth;
using Noof.Ledger.Host.Cli;
using Noof.Ledger.Host.Endpoints;
using Noof.Ledger.Host.Startup;
using Noof.Ledger.Persistence;
using Noof.Ledger.Persistence.Auth;
using Noof.Ledger.Persistence.Capture;
using Noof.Ledger.Web.Components;

if (UserCommand.TryParse(args, out var cliUsername))
{
    Environment.ExitCode = await UserCommand.RunAsync(cliUsername, args);
    return;
}

var builder = WebApplication.CreateBuilder(args);

var authMode = builder.Configuration["Auth:Mode"] ?? "Off";
var cookieMode = authMode.Equals("Cookie", StringComparison.OrdinalIgnoreCase);

CaptureTimeZoneGuard.Resolve(builder.Configuration["Capture:TimeZone"] ?? "Europe/Belgrade");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContext<LedgerDbContext>(options =>
    options.UseNpgsql(LedgerConnectionString.Resolve(builder.Configuration.GetConnectionString("Ledger"))));

builder.Services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
builder.Services.AddScoped<IUserStore, EfUserStore>();
builder.Services.AddScoped<ICaptureStore, EfCaptureStore>();

var authentication = builder.Services.AddAuthentication(
    cookieMode ? AuthSchemes.Cookie : AuthSchemes.LocalOwner);

authentication.AddCookie(AuthSchemes.Cookie, options =>
{
    options.LoginPath = "/account/login";
    options.ExpireTimeSpan = TimeSpan.FromDays(180);
    options.SlidingExpiration = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// LocalOwnerHandler authenticates every request as the owner with no credential check, so it must
// not exist at all in Cookie mode; the cookie scheme, by contrast, is harmless whenever it isn't
// the default, so it can stay registered in both modes.
if (!cookieMode)
    authentication.AddScheme<AuthenticationSchemeOptions, LocalOwnerHandler>(AuthSchemes.LocalOwner, null);

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

app.MapAccountEndpoints();

app.MapGet("/healthz", () => Results.Ok("ok")).AllowAnonymous();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var addresses = app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()?.Addresses;

    try
    {
        LoopbackGuard.AssertSafe([.. addresses ?? []], authMode);
    }
    catch (InvalidOperationException exposed)
    {
        // Throwing out of an ApplicationStarted callback does NOT stop the host: the hosting layer
        // catches it, logs it, and Kestrel keeps serving. Verified. Shutting down explicitly is the
        // only thing that actually closes the socket.
        app.Services.GetRequiredService<ILogger<Program>>().LogCritical("{Message}", exposed.Message);
        Environment.ExitCode = 1;
        app.Lifetime.StopApplication();
    }
});

app.Run();

public partial class Program;
```

The only changes from the current file: two new `using` directives, the `CaptureTimeZoneGuard.Resolve(...)` call right after `cookieMode` is computed (so a bad config value fails before anything is built), and the one new `AddScoped<ICaptureStore, EfCaptureStore>()` line next to the existing `IUserStore` registration. Everything else - the CLI short-circuit, the migrate gate, the pipeline, `MapAccountEndpoints`, `/healthz`, the `LoopbackGuard` callback - is untouched.

> `git status` shows `Program.cs` already modified on this branch by other, unrelated work in flight. Diff against whatever is actually on disk before overwriting wholesale - the changes this task needs are exactly the three described above, nothing else should move.

- [ ] **Step 12: Run the full solution and commit**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: all green.

```bash
git add src/Noof.Ledger.Application/Capture src/Noof.Ledger.Persistence/Capture src/Noof.Ledger.Host/Startup/CaptureTimeZoneGuard.cs src/Noof.Ledger.Host/Program.cs src/Noof.Ledger.Host/appsettings.json tests/Noof.Ledger.Persistence.Tests/EfCaptureStoreTests.cs tests/Noof.Ledger.Persistence.Tests/CapturedMessageTests.cs tests/Noof.Ledger.Persistence.Tests/ThrowsBeforeCommitInterceptor.cs tests/Noof.Ledger.Persistence.Tests/PostgresFixture.cs tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj tests/Noof.Ledger.Host.Tests/CaptureTimeZoneGuardTests.cs Directory.Packages.props
git commit -m "feat(capture): EfCaptureStore writes the transaction and its job together, or neither"
```

---
### Task 7: The job queue — claim, retry, fail, release

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
- Create: `tests/Noof.Ledger.Persistence.Tests/EfJobQueueTests.cs`
- Create: `src/Noof.Ledger.Application/Jobs/IJobQueue.cs`
- Create: `src/Noof.Ledger.Application/Jobs/JobCompletionOutcome.cs`
- Create: `src/Noof.Ledger.Persistence/Jobs/EfJobQueue.cs`

**Interfaces:**
- Creates, because nothing earlier in this plan does: `IJobQueue` in `Noof.Ledger.Application.Jobs`, with the exact five members below, and the `JobCompletionOutcome` enum (`Applied`, `NotOwned`) its three completion members return. The file-structure table lists `IJobQueue`, but no task delivered it — verified by `grep -rn "IJobQueue"` returning nothing before this task. Its shape is fully dictated by the `EfJobQueue` implementation given here, so no design judgement is involved.
- Consumes (in place from earlier tasks in this plan — the Domain model and Application ports for the capture path, and the Persistence configuration + migration for it): `IJobQueue` in `Noof.Ledger.Application.Jobs` with the exact five members below; `CategorizationJob` and `JobStatus` in `Noof.Ledger.Domain`; `LedgerDbContext.CategorizationJobs` (a `DbSet<CategorizationJob>`) mapped, following the same style as `AppUserConfiguration`, to table `categorization_jobs` with snake_case columns `id, transaction_id, status, attempt_count, run_after, claimed_at, claimed_by, last_error, created_at, updated_at`, already migrated.
- Produces: `EfJobQueue(LedgerDbContext db, TimeProvider timeProvider, int maxAttempts) : IJobQueue` in namespace `Noof.Ledger.Persistence.Jobs`. **Not wired into DI anywhere** — `Program.cs` and `UserCommand.cs` are untouched by this task, the same way `EfUserStore`'s own task (Task 8, phase0b) left its DI registration to a later host-wiring task. Whoever builds the worker that calls this queue also reads `Jobs:MaxAttempts` from configuration and passes it into the constructor, and must pass its own stable `workerId` into every completion call — see the closing-review callout under Step 4 below for why.

**`IJobQueue`'s exact five members, closing-review shape (write it this way from the start, not the narrower shape that appears further down this task's own draft text):**

```csharp
public interface IJobQueue
{
    Task<CategorizationJob?> ClaimAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken);
    Task<JobCompletionOutcome> SucceedAsync(Guid jobId, string workerId, CancellationToken cancellationToken);
    Task<JobCompletionOutcome> RetryAsync(Guid jobId, string workerId, DateTimeOffset runAfter, string error, CancellationToken cancellationToken);
    Task<JobCompletionOutcome> FailAsync(Guid jobId, string workerId, string error, CancellationToken cancellationToken);
    Task<int> ReleaseExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
```

`SucceedAsync`, `RetryAsync` and `FailAsync` all take the calling worker's id and report whether their update actually applied — see the closing-review callout after `EfJobQueue`'s listing below for why an unfenced three-argument version of these verbs is a real bug, not a hypothetical one.

> **If your repo doesn't have `categorization_jobs` under that exact name when you reach this task**, an earlier task named it differently. That is not a reason to redesign anything here — every SQL string in `EfJobQueue.cs` below is a self-contained literal; find-and-replace the table/column names in those five strings and everything else in this task is unaffected. The very first test you run (Step 3) will fail loudly with `relation "categorization_jobs" does not exist` if this is the case, so you cannot silently get it wrong.

**How `ClaimAsync`'s `lease` parameter and `ReleaseExpiredLeasesAsync`'s lack of one fit together.** `CategorizationJob` has no separate "lease expires at" column — it only has `RunAfter`. So `RunAfter` does double duty: for a `Pending` job it means "don't attempt before this time"; for a `Claimed` job it means "if still claimed past this time, the lease has expired." `ClaimAsync` sets `run_after = now + lease` at the moment it claims, which is exactly why `ReleaseExpiredLeasesAsync(now)` needs no lease argument of its own — it just asks "which claimed jobs have a `run_after` in the past."

**The exponential backoff schedule** (for the record — this task does not compute it; see next paragraph): base 30 seconds, doubling per attempt — attempt 1 → +30s, 2 → +1m, 3 → +2m, 4 → +4m, 5 → +8m, 6 → +16m, 7 → +32m, 8 → +64m. With the `Jobs:MaxAttempts` default of 8, attempt 8 is the last chance; a failure after that goes terminal. Whichever later task builds the worker that calls `RetryAsync` owns computing this timestamp and passing it in.

**Why the attempt cap is enforced *inside* `EfJobQueue.RetryAsync` even though `runAfter` arrives from the caller.** `IJobQueue`'s signature is fixed and carries no `maxAttempts` parameter. If the cap lived in the caller instead, every future caller would have to re-implement "am I out of attempts?" correctly, and one that forgot would retry forever. `AttemptCount` is incremented by `ClaimAsync` (claiming *is* the attempt), so by the time `RetryAsync` runs, the row's own `attempt_count` already reflects the attempt that just failed — `RetryAsync` compares that value to the `maxAttempts` it was constructed with and only honours the caller's `runAfter` when there's a retry left; otherwise it goes straight to `Failed`, ignoring the given `runAfter`.

- [ ] **Step 1: Add the fake-clock test package**

Edit `Directory.Packages.props` — add one line to the `Testing` item group (verified against nuget.org on 2026-09-21: version `10.10.0` restores clean on `net10.0` and exposes `Microsoft.Extensions.Time.Testing.FakeTimeProvider`; do not re-spike this):

```xml
  <ItemGroup Label="Testing">
    <PackageVersion Include="xunit.v3" Version="4.0.1" />
    <PackageVersion Include="xunit.v3.mtp-v2" Version="4.0.1" />
    <PackageVersion Include="AwesomeAssertions" Version="9.6.0" />
    <PackageVersion Include="NSubstitute" Version="6.2.0" />
    <PackageVersion Include="coverlet.MTP" Version="10.0.1" />
    <PackageVersion Include="TngTech.ArchUnitNET.xUnitV3" Version="0.13.4" />
    <PackageVersion Include="Microsoft.Playwright.Xunit.v3" Version="1.62.0" />
    <PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.10.0" />
  </ItemGroup>
```

Edit `tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj` — add the matching `PackageReference`:

```xml
  <ItemGroup>
    <PackageReference Include="AwesomeAssertions" />
    <PackageReference Include="xunit.v3.mtp-v2" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
  </ItemGroup>
```

Run: `dotnet restore`
Expected: restores with no errors. No test project needs a new `ProjectReference` — `Noof.Ledger.Domain` and `Noof.Ledger.Application` already flow transitively through the existing reference to `Noof.Ledger.Persistence`.

- [ ] **Step 2: Write the failing tests**

Create `tests/Noof.Ledger.Persistence.Tests/EfJobQueueTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Jobs;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfJobQueueTests(PostgresFixture fixture)
{
    // transactionId must be a row that exists. FK_categorization_jobs_transactions_transaction_id is a
    // real, non-deferred foreign key, so a fabricated Guid fails every insert with 23503 before the test
    // reaches its own assertion. Seed a Wallet (IsDefault: false, so it does not collide with the one
    // Task 3 seeds under ix_wallets_single_default) and a Transaction first, and pass that id in here.
    static CategorizationJob NewJob(Guid transactionId, DateTimeOffset runAfter, JobStatus status = JobStatus.Pending, int attemptCount = 0) => new()
    {
        Id = Guid.NewGuid(),
        TransactionId = transactionId,
        Status = status,
        AttemptCount = attemptCount,
        RunAfter = runAfter,
        CreatedAt = runAfter,
        UpdatedAt = runAfter,
    };

    [Fact]
    public async Task Claims_a_pending_job_that_is_due()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = new EfJobQueue(db, time, maxAttempts: 8);
        var job = NewJob(time.GetUtcNow().AddMinutes(-1));
        db.CategorizationJobs.Add(job);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var claimed = await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);

        claimed.Should().NotBeNull();
        claimed!.Id.Should().Be(job.Id);
        claimed.Status.Should().Be(JobStatus.Claimed);
        claimed.ClaimedBy.Should().Be("worker-a");
        claimed.AttemptCount.Should().Be(1, "claiming is what counts as an attempt");
        claimed.RunAfter.Should().BeCloseTo(time.GetUtcNow() + TimeSpan.FromMinutes(15), TimeSpan.FromMilliseconds(1),
            "run_after now doubles as the lease deadline");
    }

    [Fact]
    public async Task Returns_null_when_nothing_is_due()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = new EfJobQueue(db, time, maxAttempts: 8);
        db.CategorizationJobs.Add(NewJob(time.GetUtcNow().AddMinutes(5)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var claimed = await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);

        claimed.Should().BeNull();
    }

    [Fact]
    public async Task Concurrent_claims_against_one_pending_job_return_it_to_exactly_one_caller()
    {
        await using var dbA = await fixture.CreateContextAsync();
        await dbA.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        dbA.CategorizationJobs.Add(NewJob(time.GetUtcNow().AddMinutes(-1)));
        await dbA.SaveChangesAsync(TestContext.Current.CancellationToken);

        var optionsB = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(dbA.Database.GetConnectionString())
            .Options;
        await using var dbB = new LedgerDbContext(optionsB);

        var queueA = new EfJobQueue(dbA, time, maxAttempts: 8);
        var queueB = new EfJobQueue(dbB, time, maxAttempts: 8);

        await using var txA = await dbA.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var claimedByA = await queueA.ClaimAsync("worker-a", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);
        claimedByA.Should().NotBeNull("worker-a claimed first and still holds the row lock inside its open transaction");

        var claimBTask = queueB.ClaimAsync("worker-b", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(claimBTask, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        finished.Should().BeSameAs(claimBTask,
            "SKIP LOCKED must make worker-b's claim return immediately; plain FOR UPDATE would block until worker-a commits, and this test would hang instead of failing cleanly");
        (await claimBTask).Should().BeNull("the only pending job is locked by worker-a's still-open transaction");

        await txA.CommitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Succeeding_a_job_marks_it_succeeded()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = new EfJobQueue(db, time, maxAttempts: 8);
        var job = NewJob(time.GetUtcNow().AddMinutes(-1));
        db.CategorizationJobs.Add(job);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);

        var outcome = await queue.SucceedAsync(job.Id, "worker-a", TestContext.Current.CancellationToken);

        outcome.Should().Be(JobCompletionOutcome.Applied);
        var reloaded = await db.CategorizationJobs.AsNoTracking()
            .SingleAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken);
        reloaded.Status.Should().Be(JobStatus.Succeeded);
    }

    [Fact]
    public async Task A_stale_workers_late_retry_after_its_lease_was_reclaimed_does_not_resurrect_the_job()
    {
        // Reproduces the closing-review interleaving: worker-a claims with a short lease, the clock
        // advances past it, ReleaseExpiredLeasesAsync hands it to worker-b, worker-b succeeds, and
        // only then does worker-a's late RetryAsync arrive. Before the ownership guard this UPDATE had
        // no status/claimed_by check at all and flipped the row straight back to Pending with a
        // pushed-out run_after even though worker-b had already finished it.
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = new EfJobQueue(db, time, maxAttempts: 8);
        var job = NewJob(time.GetUtcNow().AddMinutes(-1));
        db.CategorizationJobs.Add(job);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(2));
        var released = await queue.ReleaseExpiredLeasesAsync(time.GetUtcNow(), TestContext.Current.CancellationToken);
        released.Should().Be(1, "worker-a's one-minute lease is two minutes stale by now");
        await queue.ClaimAsync("worker-b", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);
        var succeedOutcome = await queue.SucceedAsync(job.Id, "worker-b", TestContext.Current.CancellationToken);
        succeedOutcome.Should().Be(JobCompletionOutcome.Applied);

        var lateRetryOutcome = await queue.RetryAsync(
            job.Id, "worker-a", time.GetUtcNow().AddMinutes(1), "worker-a's late failure", TestContext.Current.CancellationToken);

        lateRetryOutcome.Should().Be(JobCompletionOutcome.NotOwned);
        var reloaded = await db.CategorizationJobs.AsNoTracking()
            .SingleAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken);
        reloaded.Status.Should().Be(JobStatus.Succeeded, "worker-b's completion must survive worker-a's late retry");
        reloaded.ClaimedBy.Should().Be("worker-b");
        reloaded.LastError.Should().BeNull();
    }

    [Fact]
    public async Task A_stale_workers_late_fail_after_its_lease_was_reclaimed_does_not_override_the_new_owner()
    {
        // The FailAsync variant of the same defect: a released job's new claim must not be knocked
        // straight to Failed by the original worker's late failure report.
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = new EfJobQueue(db, time, maxAttempts: 8);
        var job = NewJob(time.GetUtcNow().AddMinutes(-1));
        db.CategorizationJobs.Add(job);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(2));
        await queue.ReleaseExpiredLeasesAsync(time.GetUtcNow(), TestContext.Current.CancellationToken);
        await queue.ClaimAsync("worker-b", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);

        var outcome = await queue.FailAsync(job.Id, "worker-a", "worker-a's late failure", TestContext.Current.CancellationToken);

        outcome.Should().Be(JobCompletionOutcome.NotOwned);
        var reloaded = await db.CategorizationJobs.AsNoTracking()
            .SingleAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken);
        reloaded.Status.Should().Be(JobStatus.Claimed, "worker-b still owns this job; worker-a's late fail must not touch it");
        reloaded.ClaimedBy.Should().Be("worker-b");
        reloaded.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Retrying_below_the_attempt_cap_returns_the_job_to_pending()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = new EfJobQueue(db, time, maxAttempts: 2);
        var job = NewJob(time.GetUtcNow().AddMinutes(-1));
        db.CategorizationJobs.Add(job);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);
        var nextRunAfter = time.GetUtcNow().AddMinutes(1);

        var outcome = await queue.RetryAsync(job.Id, "worker-a", nextRunAfter, "boom", TestContext.Current.CancellationToken);

        outcome.Should().Be(JobCompletionOutcome.Applied);
        var reloaded = await db.CategorizationJobs.AsNoTracking()
            .SingleAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken);
        reloaded.Status.Should().Be(JobStatus.Pending, "attempt 1 of a 2-attempt cap still has a retry left");
        reloaded.RunAfter.Should().BeCloseTo(nextRunAfter, TimeSpan.FromMilliseconds(1));
        reloaded.LastError.Should().Be("boom");
        reloaded.ClaimedBy.Should().BeNull();
    }

    [Fact]
    public async Task Retrying_at_the_attempt_cap_fails_the_job_terminally_instead()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = new EfJobQueue(db, time, maxAttempts: 2);
        var job = NewJob(time.GetUtcNow().AddMinutes(-1));
        db.CategorizationJobs.Add(job);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);
        await queue.RetryAsync(job.Id, "worker-a", time.GetUtcNow().AddMinutes(1), "first failure", TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(2));
        await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);

        var outcome = await queue.RetryAsync(job.Id, "worker-a", time.GetUtcNow().AddMinutes(1), "second failure", TestContext.Current.CancellationToken);

        outcome.Should().Be(JobCompletionOutcome.Applied);
        var reloaded = await db.CategorizationJobs.AsNoTracking()
            .SingleAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken);
        reloaded.Status.Should().Be(JobStatus.Failed, "attempt 2 of a 2-attempt cap has no retries left");
        reloaded.LastError.Should().Be("second failure");
        reloaded.AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task Failing_a_job_this_worker_owns_marks_it_failed()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = new EfJobQueue(db, time, maxAttempts: 8);
        var job = NewJob(time.GetUtcNow().AddMinutes(-1));
        db.CategorizationJobs.Add(job);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);

        var outcome = await queue.FailAsync(job.Id, "worker-a", "not a transaction", TestContext.Current.CancellationToken);

        outcome.Should().Be(JobCompletionOutcome.Applied);
        var reloaded = await db.CategorizationJobs.AsNoTracking()
            .SingleAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken);
        reloaded.Status.Should().Be(JobStatus.Failed);
        reloaded.LastError.Should().Be("not a transaction");
    }

    [Fact]
    public async Task Releases_only_the_leases_that_have_actually_expired()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = new EfJobQueue(db, time, maxAttempts: 8);

        var expired = NewJob(time.GetUtcNow().AddMinutes(-20), status: JobStatus.Claimed, attemptCount: 1);
        expired.ClaimedAt = time.GetUtcNow().AddMinutes(-35);
        expired.ClaimedBy = "worker-a";

        var stillLeased = NewJob(time.GetUtcNow().AddMinutes(10), status: JobStatus.Claimed, attemptCount: 1);
        stillLeased.ClaimedAt = time.GetUtcNow().AddMinutes(-5);
        stillLeased.ClaimedBy = "worker-b";

        db.CategorizationJobs.AddRange(expired, stillLeased);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var released = await queue.ReleaseExpiredLeasesAsync(time.GetUtcNow(), TestContext.Current.CancellationToken);

        released.Should().Be(1);
        var reloadedExpired = await db.CategorizationJobs.AsNoTracking()
            .SingleAsync(j => j.Id == expired.Id, TestContext.Current.CancellationToken);
        reloadedExpired.Status.Should().Be(JobStatus.Pending);
        reloadedExpired.ClaimedAt.Should().BeNull();
        reloadedExpired.ClaimedBy.Should().BeNull();

        var reloadedStillLeased = await db.CategorizationJobs.AsNoTracking()
            .SingleAsync(j => j.Id == stillLeased.Id, TestContext.Current.CancellationToken);
        reloadedStillLeased.Status.Should().Be(JobStatus.Claimed, "its lease has not expired yet");
    }

    [Fact]
    public async Task RetryAsync_rejects_a_non_UTC_runAfter_with_a_clear_message_instead_of_an_Npgsql_failure()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = new EfJobQueue(db, time, maxAttempts: 8);
        var nonUtc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(2));

        var act = () => queue.RetryAsync(Guid.NewGuid(), "worker-a", nonUtc, "boom", TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ArgumentException>();
        assertion.WithMessage("*UTC*");
        assertion.And.ParamName.Should().Be("runAfter");
    }

    [Fact]
    public async Task ReleaseExpiredLeasesAsync_rejects_a_non_UTC_now_with_a_clear_message_instead_of_an_Npgsql_failure()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = new EfJobQueue(db, time, maxAttempts: 8);
        var nonUtc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(2));

        var act = () => queue.ReleaseExpiredLeasesAsync(nonUtc, TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ArgumentException>();
        assertion.WithMessage("*UTC*");
        assertion.And.ParamName.Should().Be("now");
    }
}
```

> `RunAfter`/`ClaimedAt` are `DateTimeOffset` and round-trip through Postgres `timestamptz`, which stores microsecond precision — .NET's `DateTimeOffset` carries 100ns ticks. A value you compute in C# and one you read back after a round trip can differ by a few ticks (sub-microsecond). That's why the assertions above use `BeCloseTo(..., TimeSpan.FromMilliseconds(1))` for anything read back from the database instead of `Be(...)` — an exact-equality assertion here is a test that fails intermittently for no real bug.

> `FakeTimeProvider` does **not** advance on its own. In `Retrying_at_the_attempt_cap_fails_the_job_terminally_instead`, the first `RetryAsync` sets `run_after` to one minute in the *fake* future — if you claim again without calling `time.Advance(...)` past that point first, `ClaimAsync`'s own `run_after <= now` guard correctly finds nothing due and returns `null`, `AttemptCount` never reaches 2, and the boundary test can't tell you anything. This bit exactly this way while writing the reference implementation for this task — advance the clock before the second claim.

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: build error — `The type or namespace name 'Jobs' does not exist in the namespace 'Noof.Ledger.Persistence'` (from `using Noof.Ledger.Persistence.Jobs;`) and `The type or namespace name 'EfJobQueue' could not be found`.

- [ ] **Step 3: Confirm the failure is the one you expect**

This is the same command as Step 2 re-run for clarity — do not skip actually looking at the output. If the error is anything other than "type not found" (for example a Postgres connection error, or a compile error inside the test file itself), stop and fix that first; a red test for the wrong reason proves nothing.

- [ ] **Step 4: Write `EfJobQueue`**

Create `src/Noof.Ledger.Persistence/Jobs/EfJobQueue.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Jobs;

public sealed class EfJobQueue(LedgerDbContext db, TimeProvider timeProvider, int maxAttempts) : IJobQueue
{
    public async Task<CategorizationJob?> ClaimAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var leaseExpiry = now + lease;

        var claimed = await db.CategorizationJobs
            .FromSqlRaw(
                """
                UPDATE categorization_jobs
                SET status = 1,
                    claimed_at = @now,
                    claimed_by = @workerId,
                    attempt_count = attempt_count + 1,
                    run_after = @leaseExpiry,
                    updated_at = @now
                WHERE id = (
                    SELECT id FROM categorization_jobs
                    WHERE status = 0 AND run_after <= @now
                    ORDER BY run_after
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING id, transaction_id, status, attempt_count, run_after, claimed_at, claimed_by, last_error, created_at, updated_at
                """,
                new NpgsqlParameter("now", now),
                new NpgsqlParameter("workerId", workerId),
                new NpgsqlParameter("leaseExpiry", leaseExpiry))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return claimed.SingleOrDefault();
    }

    public async Task<JobCompletionOutcome> SucceedAsync(Guid jobId, string workerId, CancellationToken cancellationToken)
    {
        var rows = await db.Database.ExecuteSqlRawAsync(
            "UPDATE categorization_jobs SET status = 2, updated_at = @now WHERE id = @jobId AND claimed_by = @workerId AND status = 1",
            [
                new NpgsqlParameter("now", timeProvider.GetUtcNow()),
                new NpgsqlParameter("jobId", jobId),
                new NpgsqlParameter("workerId", workerId),
            ],
            cancellationToken);

        return ToOutcome(rows);
    }

    public async Task<JobCompletionOutcome> RetryAsync(Guid jobId, string workerId, DateTimeOffset runAfter, string error, CancellationToken cancellationToken)
    {
        var rows = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE categorization_jobs
            SET status = CASE WHEN attempt_count >= @maxAttempts THEN 3 ELSE 0 END,
                run_after = CASE WHEN attempt_count >= @maxAttempts THEN run_after ELSE @runAfter END,
                claimed_at = NULL,
                claimed_by = NULL,
                last_error = @error,
                updated_at = @now
            WHERE id = @jobId AND claimed_by = @workerId AND status = 1
            """,
            [
                new NpgsqlParameter("maxAttempts", maxAttempts),
                new NpgsqlParameter("runAfter", runAfter),
                new NpgsqlParameter("error", error),
                new NpgsqlParameter("now", timeProvider.GetUtcNow()),
                new NpgsqlParameter("jobId", jobId),
                new NpgsqlParameter("workerId", workerId),
            ],
            cancellationToken);

        return ToOutcome(rows);
    }

    public async Task<JobCompletionOutcome> FailAsync(Guid jobId, string workerId, string error, CancellationToken cancellationToken)
    {
        var rows = await db.Database.ExecuteSqlRawAsync(
            "UPDATE categorization_jobs SET status = 3, last_error = @error, updated_at = @now WHERE id = @jobId AND claimed_by = @workerId AND status = 1",
            [
                new NpgsqlParameter("error", error),
                new NpgsqlParameter("now", timeProvider.GetUtcNow()),
                new NpgsqlParameter("jobId", jobId),
                new NpgsqlParameter("workerId", workerId),
            ],
            cancellationToken);

        return ToOutcome(rows);
    }

    static JobCompletionOutcome ToOutcome(int rowsAffected) =>
        rowsAffected > 0 ? JobCompletionOutcome.Applied : JobCompletionOutcome.NotOwned;

    public Task<int> ReleaseExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        RequireUtc(now, nameof(now));

        return db.Database.ExecuteSqlRawAsync(
            "UPDATE categorization_jobs SET status = 0, claimed_at = NULL, claimed_by = NULL, updated_at = @now WHERE status = 1 AND run_after <= @now",
            [new NpgsqlParameter("now", now)],
            cancellationToken);
    }

    static void RequireUtc(DateTimeOffset value, string paramName)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException($"must be UTC (Offset == TimeSpan.Zero), but was {value.Offset}.", paramName);
    }
}
```

> **Closing-review fix (2026-09-21): a non-UTC `runAfter`/`now` is rejected loudly at the call site, not three layers down inside Npgsql.** `RetryAsync`'s `runAfter` and `ReleaseExpiredLeasesAsync`'s `now` are both caller-supplied `DateTimeOffset` values written straight into a `timestamptz` parameter. Proven against real Postgres: passing a `DateTimeOffset` with `Offset = +02:00` into either method, as originally drafted above (no guard), throws `System.ArgumentException: Cannot write DateTimeOffset with Offset=02:00:00 to PostgreSQL type 'timestamp with time zone'` from inside Npgsql's parameter writer — a real exception, but one whose `ParamName` is `"value"`, not `"runAfter"` or `"now"`, and whose message names neither this method nor the actual rule. `RequireUtc` runs first in both methods now and throws its own `ArgumentException` — same exception type, but with the correct `ParamName` and a message that says "must be UTC" — before any SQL is sent, so a caller with a debugger or a log line sees the real cause immediately instead of an opaque driver-level failure. This is a **loud-rejection** policy, not silent normalisation: a non-UTC value reaching this boundary means some upstream code is doing local-time arithmetic where it should be doing instant arithmetic, and converting it away quietly would hide that bug instead of surfacing it. Proven by two new facts in `EfJobQueueTests.cs` — `RetryAsync_rejects_a_non_UTC_runAfter_with_a_clear_message_instead_of_an_Npgsql_failure` and `ReleaseExpiredLeasesAsync_rejects_a_non_UTC_now_with_a_clear_message_instead_of_an_Npgsql_failure` — each asserting `ParamName` specifically, since asserting only "throws `ArgumentException`" would pass even without this guard (Npgsql already throws that type; only the parameter name and message distinguish an intentional contract from an implementation detail leaking through).

> **Closing-review fix (2026-09-21): completion is fenced by ownership, not just by id.** As originally drafted above, `SucceedAsync`/`RetryAsync`/`FailAsync` were `UPDATE ... WHERE id = @jobId` with no guard on `status` or `claimed_by`, and the interface didn't even accept a `workerId`. A reviewer proved this resurrects finished work on real PostgreSQL: worker A claims with a one-minute lease, the clock advances two minutes, `ReleaseExpiredLeasesAsync` hands the job to worker B, worker B calls `SucceedAsync` and the job is `Succeeded` — then worker A's late `RetryAsync` (or `FailAsync`) arrives and mutates the row anyway, because nothing ever checked that A still owned it. All three verbs now add `AND claimed_by = @workerId AND status = 1` to their `WHERE` clause and return a new `JobCompletionOutcome` (`Applied` or `NotOwned`) computed from `ExecuteSqlRawAsync`'s own rows-affected count, so a caller whose update touched zero rows gets a value it can act on and log instead of a silent no-op. `IJobQueue`'s three completion members gained a `workerId` parameter and a `Task<JobCompletionOutcome>` return type as part of this fix — write the interface with that shape from Step 1 rather than the two-member-narrower version elsewhere in this task's earlier draft text. Proven by three new facts in `EfJobQueueTests.cs`: `A_stale_workers_late_retry_after_its_lease_was_reclaimed_does_not_resurrect_the_job`, `A_stale_workers_late_fail_after_its_lease_was_reclaimed_does_not_override_the_new_owner`, and `Succeeding_a_job_this_worker_no_longer_owns_is_reported_as_not_owned_and_leaves_the_row_alone` — each fails with `Expected outcome to be JobCompletionOutcome.NotOwned, but found JobCompletionOutcome.Applied` when the `WHERE` clause is reverted to `id = @jobId` alone, confirmed by actually reverting and rerunning against real Postgres, not by inspection.

> **Postgres clause order matters here.** `FOR UPDATE SKIP LOCKED` must come *after* `LIMIT`, not before — `SELECT ... FOR UPDATE SKIP LOCKED LIMIT 1` is a syntax error. This is exactly backwards from how people say it out loud ("grab one, locking, skip what's taken"), which is the easy way to get it wrong from memory.

> **Why the claim is one `UPDATE ... WHERE id = (SELECT ... FOR UPDATE SKIP LOCKED) RETURNING ...` rather than a `SELECT` followed by an `UPDATE`.** Two statements would reopen the exact race this task exists to close — another connection could claim the row between your `SELECT` and your `UPDATE`. One statement is atomic from Postgres's point of view even with no explicit `BeginTransaction` in application code.

> **`FromSqlRaw` combined with `.AsNoTracking().ToListAsync(...)` sends the SQL to Postgres unmodified** as long as nothing composes further LINQ onto it (no `.Where(...)`, no `.OrderBy(...)` afterwards) — composing further would make EF wrap your SQL in a subquery, and a `RETURNING` clause does not survive being wrapped. Materialize directly, as above.

- [ ] **Step 5: Run the tests**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: all green, including the 13 `EfJobQueueTests` facts (8 original, 3 closing-review ownership-fencing facts added under Step 4's first callout, and 2 closing-review UTC-guard facts added under Step 4's second callout). The concurrency test (`Concurrent_claims_against_one_pending_job_...`) should complete in well under the 5-second guard — if it takes close to 5 seconds, that's `Task.WhenAny` hitting the `Task.Delay` branch, meaning `SKIP LOCKED` isn't doing its job; go back to Step 4 before moving on.

Then run the full suite once to confirm nothing else broke: `dotnet test --solution NoofLedger.slnx`

- [ ] **Step 6: Commit**

```bash
git add Directory.Packages.props tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj tests/Noof.Ledger.Persistence.Tests/EfJobQueueTests.cs src/Noof.Ledger.Application/Jobs/IJobQueue.cs src/Noof.Ledger.Application/Jobs/JobCompletionOutcome.cs src/Noof.Ledger.Persistence/Jobs/EfJobQueue.cs
git commit -m "feat(persistence): EfJobQueue with SKIP LOCKED claiming and an attempt-capped retry"
```
### Task 8: Telegram — the poller, the owner allowlist, and the reply

**Files:**
- Create: `tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`, `xunit.runner.json`
- Create: `tests/Noof.Ledger.Telegram.Tests/TelegramBackoffTests.cs`, `TelegramUpdateOffsetStoreTests.cs`, `TelegramOwnerGateTests.cs`, `TelegramChatNotifierTests.cs`, `TelegramBotClientFactoryTests.cs`, `TelegramUpdateRouterTests.cs`, `TelegramPollingServiceTests.cs`
- Create: `src/Noof.Ledger.Application/Chat/IChatNotifier.cs` — **plan defect, fixed here**: the global File Structure table (this document, line 49) and this task's own Interfaces section below both call this "locked, shipped by earlier Phase 1A tasks", but no task 1–7 creates it — confirmed by grepping every task's steps for `Chat/IChatNotifier` (no hits) and by the actual repo state at the start of Task 8 (`src/Noof.Ledger.Application` has no `Chat` folder). Task 8 is this port's sole producer as well as its sole consumer; create it in Stage 4 immediately before `TelegramChatNotifier`, the class that implements it.
- Create: `src/Noof.Ledger.Telegram/TelegramClientHandle.cs`, `TelegramBackoff.cs`, `TelegramUpdateOffsetStore.cs`, `TelegramOwnerGate.cs`, `TelegramChatNotifier.cs`, `ITelegramUpdateRouter.cs`, `TelegramUpdateRouter.cs`, `ITelegramBotClientFactory.cs`, `TelegramBotClientFactory.cs`, `TelegramPollingService.cs`
- Create: `tests/Noof.Ledger.Host.Tests/TelegramHttpClientLoggingTests.cs`
- Modify: `src/Noof.Ledger.Telegram/Noof.Ledger.Telegram.csproj`, `src/Noof.Ledger.Host/Program.cs`
- Modify: `Directory.Packages.props`, `NoofLedger.slnx`, `tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs`

**Interfaces:**
- Consumes (locked, from `Noof.Ledger.Application`, shipped by earlier Phase 1A tasks — **not present in the repo yet**; if you hit "type or namespace `ISecretStore`/`ICaptureStore` could not be found", that earlier task hasn't landed, this one didn't break anything): `ISecretStore`, `SecretResult`, `SecretState`, `SecretKeys` (`Noof.Ledger.Application.Secrets`); `ICaptureStore`, `CapturedMessage` (`Noof.Ledger.Application.Capture`).
- Produces: everything listed in `producedInterfaces` above, plus `IChatNotifier` (`Noof.Ledger.Application.Chat`) itself — see the Files-list note above. The one other task most likely to need it soon is the categorisation worker — it will resolve `IChatNotifier` from DI and call `EditAsync` once a job finishes; nothing here needs to change for that to work.

**Read first, before writing anything:** `src/Noof.Ledger.Host/Program.cs` — modified in Step 34 below. Independently confirmed against the current repository state (not merely assumed): as of this review it ends with `builder.Services.AddCascadingAuthenticationState();` immediately followed by `var app = builder.Build();`, and it does **not yet** contain any `ISecretStore`/`ICaptureStore` registrations, because the Secrets/Capture-ports tasks this task consumes have not landed yet either. When they do land first, they will almost certainly insert their own `AddScoped<...>()` lines in that same gap — leave those alone and add this task's block near them, anchored the same way: immediately before `var app = builder.Build();`. And `tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs` (its current shape, confirmed against the file: package assertions are one dedicated `[Fact]` per project — `Domain_has_no_package_references`, `Application_has_no_package_references`, `Web_package_references_are_exactly_its_allowed_set`, plus a few Web-only facts — there is **no** existing assertion for `Noof.Ledger.Telegram`'s packages, and its `ProjectReference` row in the `Theory` at the top is already correct (`Application`, `Domain`) and needs no change; if an earlier task already generalised the per-project package assertions into a parameterised `Theory` instead of the current one-`Fact`-per-project style, add Telegram's row to that theory instead of adding a new `Fact` — the **set of three package names** is what matters, not which test shape carries it).

---

#### Stage 0 — project scaffold and the package/architecture gate

- [ ] **Step 1: Scaffold the test project**

Create `tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`:

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
    <PackageReference Include="NSubstitute" />
    <PackageReference Include="xunit.v3.mtp-v2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Noof.Ledger.Telegram\Noof.Ledger.Telegram.csproj" />
  </ItemGroup>

</Project>
```

Create `tests/Noof.Ledger.Telegram.Tests/xunit.runner.json` (identical to every other test project):

```json
{
    "$schema": "https://xunit.net/schema/current/xunit.runner.schema.json"
}
```

In `NoofLedger.slnx`, add the project to the `/tests/` folder, alphabetically between `Persistence.Tests` and `TestKit`:

```xml
    <Project Path="tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj" />
```

No test runs yet — there is no source. This is scaffolding, not behaviour; nothing to TDD here.

- [ ] **Step 2: Write the failing architecture test for Telegram's package set**

Open `tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs`. Its current package assertions are one dedicated `[Fact]` per project (`Domain_has_no_package_references`, `Application_has_no_package_references`, `Web_package_references_are_exactly_its_allowed_set`) — there is **no** existing assertion for `Noof.Ledger.Telegram`'s packages, and its `ProjectReference` row in the `Theory` at the top is already correct (`Application`, `Domain`) and needs no change. Add, next to `Web_package_references_are_exactly_its_allowed_set`:

```csharp
    [Fact]
    public void Telegram_package_references_are_exactly_its_allowed_set()
    {
        Packages("Noof.Ledger.Telegram").Should().BeEquivalentTo(
            "Telegram.Bot",
            "Microsoft.Extensions.Http",
            "Microsoft.Extensions.Hosting.Abstractions");
    }
```

- [ ] **Step 3: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`
Expected: `Telegram_package_references_are_exactly_its_allowed_set` FAILS — `Packages("Noof.Ledger.Telegram")` is currently empty, expected has 3 entries.

- [ ] **Step 4: Add the three packages**

`Directory.Packages.props`, in the first `<ItemGroup>`:

```xml
    <PackageVersion Include="Telegram.Bot" Version="22.10.3.1" />
    <PackageVersion Include="Microsoft.Extensions.Http" Version="10.0.12" />
    <PackageVersion Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.12" />
```

`src/Noof.Ledger.Telegram/Noof.Ledger.Telegram.csproj` — add a `PackageReference` `ItemGroup`:

```xml
  <ItemGroup>
    <PackageReference Include="Telegram.Bot" />
    <PackageReference Include="Microsoft.Extensions.Http" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
  </ItemGroup>
```

> **Verified against the real package, not guessed.** `Telegram.Bot` 22.10.3.1 removed the `Async`-suffixed method names entirely (they were only deprecated-but-present through 22.4): the extension methods are `SendMessage`, `GetUpdates`, `DeleteWebhook`, `EditMessageText`, all defined as `static` extensions over `ITelegramBotClient` that funnel through the one real interface member, `Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken)`. That last fact is *why* `ITelegramBotClient` is mockable at all with NSubstitute in Steps 18 and 30 — `SendMessage`/`GetUpdates`/etc. are plain static methods and cannot be substituted directly; `SendRequest` can. `Microsoft.Extensions.Http` gives `IHttpClientFactory`/`AddHttpClient` (not part of the base runtime for a plain `Microsoft.NET.Sdk` library — only `Microsoft.NET.Sdk.Web` gets that for free, which is why `Noof.Ledger.Host.csproj` never needed it). `Microsoft.Extensions.Hosting.Abstractions` gives `BackgroundService`, and — confirmed from its own `net10.0` dependency group — pulls in `Microsoft.Extensions.Configuration.Abstractions` and `Microsoft.Extensions.Logging.Abstractions` transitively, so `IConfiguration` and `ILogger<T>` need no separate package in this project.

- [ ] **Step 5: Run the architecture tests green**

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`
Expected: all green.

---

#### Stage 1 — `TelegramBackoff` (pure function, no dependencies)

- [ ] **Step 6: Write the failing test**

Create `tests/Noof.Ledger.Telegram.Tests/TelegramBackoffTests.cs`:

```csharp
using AwesomeAssertions;
using Noof.Ledger.Telegram;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramBackoffTests
{
    [Fact]
    public void No_failures_means_no_delay()
    {
        TelegramBackoff.Compute(0).Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    public void Backs_off_exponentially(int failures, int expectedSeconds)
    {
        TelegramBackoff.Compute(failures).Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void Never_waits_more_than_a_minute()
    {
        TelegramBackoff.Compute(10).Should().Be(TimeSpan.FromMinutes(1));
    }
}
```

- [ ] **Step 7: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
Expected: build error, `TelegramBackoff` does not exist.

- [ ] **Step 8: Implement it**

Create `src/Noof.Ledger.Telegram/TelegramBackoff.cs`:

```csharp
namespace Noof.Ledger.Telegram;

public static class TelegramBackoff
{
    static readonly TimeSpan Cap = TimeSpan.FromMinutes(1);

    public static TimeSpan Compute(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return TimeSpan.Zero;

        var seconds = Math.Min(Cap.TotalSeconds, Math.Pow(2, consecutiveFailures));
        return TimeSpan.FromSeconds(seconds);
    }
}
```

- [ ] **Step 9: Run green**

Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
Expected: all green.

---

#### Stage 2 — `TelegramUpdateOffsetStore`

> **Why this reuses `ISecretStore` instead of a new table.** The Telegram `getUpdates` offset is a single integer that must survive a restart, and `ISecretStore` is the only durable, already-migrated key/value slot this phase's locked contract gives `Noof.Ledger.Telegram` access to (it references only `Application` and `Domain`, not `Persistence` — it cannot touch `LedgerDbContext` directly). The value has no confidentiality need of its own; storing it encrypted is a harmless side effect, not a design goal. This is a cheap, reversible choice — swapping it for a dedicated table later touches one class, not a schema migration of anything else — so it is noted here rather than escalated.

- [ ] **Step 10: Write the failing tests**

Create `tests/Noof.Ledger.Telegram.Tests/TelegramUpdateOffsetStoreTests.cs`:

```csharp
using AwesomeAssertions;
using NSubstitute;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Telegram;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramUpdateOffsetStoreTests
{
    [Fact]
    public async Task GetAsync_returns_null_when_nothing_has_been_persisted_yet()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(TelegramUpdateOffsetStore.Key, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Missing, null));
        var store = new TelegramUpdateOffsetStore(secretStore);

        var offset = await store.GetAsync(TestContext.Current.CancellationToken);

        offset.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_parses_a_previously_saved_offset()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(TelegramUpdateOffsetStore.Key, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Present, "482913"));
        var store = new TelegramUpdateOffsetStore(secretStore);

        var offset = await store.GetAsync(TestContext.Current.CancellationToken);

        offset.Should().Be(482913);
    }

    [Fact]
    public async Task GetAsync_treats_an_unreadable_record_the_same_as_missing()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(TelegramUpdateOffsetStore.Key, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Unreadable, null));
        var store = new TelegramUpdateOffsetStore(secretStore);

        var offset = await store.GetAsync(TestContext.Current.CancellationToken);

        offset.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_persists_the_offset_as_a_string()
    {
        var secretStore = Substitute.For<ISecretStore>();
        var store = new TelegramUpdateOffsetStore(secretStore);

        await store.SetAsync(482914, TestContext.Current.CancellationToken);

        await secretStore.Received(1).SetAsync(TelegramUpdateOffsetStore.Key, "482914", TestContext.Current.CancellationToken);
    }
}
```

- [ ] **Step 11: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
Expected: build error, `TelegramUpdateOffsetStore` does not exist (also: if `ISecretStore`/`SecretResult`/`SecretState` don't exist either, the Secrets-ports task from earlier in this phase hasn't landed — stop and pull that in first).

- [ ] **Step 12: Implement it**

Create `src/Noof.Ledger.Telegram/TelegramUpdateOffsetStore.cs`:

```csharp
using System.Globalization;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Telegram;

public sealed class TelegramUpdateOffsetStore(ISecretStore secretStore)
{
    public const string Key = "telegram-update-offset";

    public async Task<int?> GetAsync(CancellationToken cancellationToken)
    {
        var result = await secretStore.GetAsync(Key, cancellationToken);

        return result.State is SecretState.Present && int.TryParse(result.Value, out var offset)
            ? offset
            : null;
    }

    public Task SetAsync(int offset, CancellationToken cancellationToken) =>
        secretStore.SetAsync(Key, offset.ToString(CultureInfo.InvariantCulture), cancellationToken);
}
```

- [ ] **Step 13: Run green**

Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
Expected: all green.

---

#### Stage 3 — `TelegramOwnerGate` (the allowlist)

- [ ] **Step 13A (contract amendment): `ISecretStore.TrySetIfMissingAsync`, so the claim below can be a real compare-and-swap**

A security review after this task originally shipped found that `ClaimAsync` (Step 16) was read-then-write with no compare-and-swap: two chats messaging before any owner exists both read `Missing` from `GetAsync`, both call `SetAsync`, and the loser gets a raw `Npgsql.PostgresException` 23505 on `PK_app_secret` that propagates out through `TelegramOwnerGate` to the poller's catch-all, logged there as "Telegram getUpdates failed" — which is untrue and misleading. This was safe only because a single poller processes updates one at a time; a second Host process against the same database (plausible on an unsupervised personal machine) reintroduces the race silently, since that invariant lives only in prose, not code.

The fix is a set-if-absent primitive on `ISecretStore` distinct from `SetAsync`'s plain upsert: `SetAsync` is last-write-wins (correct for pasting a rotated API key), but the owner claim needs first-writer-wins, with the loser told it lost rather than handed an exception.

Add to `src/Noof.Ledger.Application/Secrets/ISecretStore.cs`, after `SetAsync`:

```csharp
    // Inserts only if the key is still Missing; returns false without writing anything if another
    // call already claimed it first, however close the race. Exists for callers where "first
    // writer wins" is the correct outcome, unlike SetAsync's plain upsert.
    Task<bool> TrySetIfMissingAsync(string key, string plaintext, CancellationToken cancellationToken);
```

Implement it in `src/Noof.Ledger.Persistence/Secrets/EfSecretStore.cs` (add `using Npgsql;` alongside the existing usings), directly after `SetAsync`:

```csharp
    public async Task<bool> TrySetIfMissingAsync(string key, string plaintext, CancellationToken cancellationToken)
    {
        var existing = await db.Secrets.FindAsync([key], cancellationToken);
        if (existing is not null)
            return false;

        var secret = new AppSecret
        {
            Key = key,
            Ciphertext = Protector(key).Protect(plaintext),
            UpdatedAt = timeProvider.GetUtcNow(),
        };
        db.Secrets.Add(secret);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (IsAppSecretPrimaryKeyViolation(ex))
        {
            // Another call won the race between our FindAsync and this SaveChangesAsync. That row
            // is real; ours never committed. Detach it so a later read goes back to the database
            // instead of returning this uncommitted, never-persisted entity from the identity map.
            db.Entry(secret).State = EntityState.Detached;
            return false;
        }
    }

    static bool IsAppSecretPrimaryKeyViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_app_secret" };
```

Add the discriminating test to `tests/Noof.Ledger.Persistence.Tests/EfSecretStoreTests.cs`. A sequential test cannot observe this race — it would pass against the original read-then-write `SetAsync` just as easily as against a real compare-and-swap — so this forces genuine DB-level contention: one call holds its insert open inside an uncommitted transaction, and the other's insert must block on it, not race past it, before either result is observed.

```csharp
    [Fact]
    public async Task Concurrent_TrySetIfMissingAsync_calls_for_the_same_key_let_exactly_one_caller_win()
    {
        await using var dbA = await fixture.CreateContextAsync();
        await dbA.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var optionsB = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(dbA.Database.GetConnectionString()!)
            .Options;
        await using var dbB = new LedgerDbContext(optionsB);

        var storeA = CreateStore(dbA);
        var storeB = CreateStore(dbB);

        await using var txA = await dbA.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var claimedA = await storeA.TrySetIfMissingAsync(
            SecretKeys.TelegramOwnerChatId, "111", TestContext.Current.CancellationToken);
        claimedA.Should().BeTrue("chat 111 inserted first and still holds the row inside its open transaction");

        var claimBTask = storeB.TrySetIfMissingAsync(
            SecretKeys.TelegramOwnerChatId, "222", TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(claimBTask, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        finished.Should().NotBeSameAs(claimBTask,
            "chat 222's insert must block on the uncommitted row from chat 111, not race past it undetected");

        await txA.CommitAsync(TestContext.Current.CancellationToken);

        var claimedB = await claimBTask;

        claimedB.Should().BeFalse("chat 111 already committed the key; chat 222 lost the race but must not throw");

        var owner = await storeA.GetAsync(SecretKeys.TelegramOwnerChatId, TestContext.Current.CancellationToken);
        owner.Should().Be(new SecretResult(SecretState.Present, "111"));

        (await dbA.Secrets.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }
```

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: fails to compile until `TrySetIfMissingAsync` exists on both the interface and `EfSecretStore`; then green.

- [ ] **Step 14: Write the failing tests**

Create `tests/Noof.Ledger.Telegram.Tests/TelegramOwnerGateTests.cs`:

```csharp
using AwesomeAssertions;
using NSubstitute;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Telegram;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramOwnerGateTests
{
    [Fact]
    public async Task The_first_chat_to_message_claims_ownership()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Missing, null));
        secretStore.TrySetIfMissingAsync(SecretKeys.TelegramOwnerChatId, "111", Arg.Any<CancellationToken>())
            .Returns(true);
        var gate = new TelegramOwnerGate(secretStore);

        var allowed = await gate.IsAllowedAsync(111L, TestContext.Current.CancellationToken);

        allowed.Should().BeTrue();
        await secretStore.Received(1).TrySetIfMissingAsync(SecretKeys.TelegramOwnerChatId, "111", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Losing_the_claim_race_to_another_chat_is_rejected_not_an_error()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Missing, null), new SecretResult(SecretState.Present, "222"));
        secretStore.TrySetIfMissingAsync(SecretKeys.TelegramOwnerChatId, "111", Arg.Any<CancellationToken>())
            .Returns(false);
        var gate = new TelegramOwnerGate(secretStore);

        var allowed = await gate.IsAllowedAsync(111L, TestContext.Current.CancellationToken);

        allowed.Should().BeFalse("chat 222 committed first; 111 lost the race and is not the owner");
    }

    [Fact]
    public async Task The_owner_chat_is_allowed_on_every_later_message()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Present, "111"));
        var gate = new TelegramOwnerGate(secretStore);

        var allowed = await gate.IsAllowedAsync(111L, TestContext.Current.CancellationToken);

        allowed.Should().BeTrue();
        await secretStore.DidNotReceive().TrySetIfMissingAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_stranger_is_rejected_once_an_owner_is_set()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Present, "111"));
        var gate = new TelegramOwnerGate(secretStore);

        var allowed = await gate.IsAllowedAsync(999L, TestContext.Current.CancellationToken);

        allowed.Should().BeFalse();
        await secretStore.DidNotReceive().TrySetIfMissingAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unreadable_owner_record_fails_closed_instead_of_reclaiming_ownership()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Unreadable, null));
        var gate = new TelegramOwnerGate(secretStore);

        var allowed = await gate.IsAllowedAsync(111L, TestContext.Current.CancellationToken);

        allowed.Should().BeFalse();
    }
}
```

> A Data Protection key problem or a corrupt row must not silently hand ownership to whoever happens to message next — "unreadable" is not "nobody has claimed this yet". That is why `Unreadable` rejects rather than falling through to the claim branch.

- [ ] **Step 15: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
Expected: build error, `TelegramOwnerGate` does not exist.

- [ ] **Step 16: Implement it**

Create `src/Noof.Ledger.Telegram/TelegramOwnerGate.cs`:

```csharp
using System.Globalization;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Telegram;

public sealed class TelegramOwnerGate(ISecretStore secretStore)
{
    public async Task<bool> IsAllowedAsync(long chatId, CancellationToken cancellationToken)
    {
        var owner = await secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, cancellationToken);

        return owner.State switch
        {
            SecretState.Missing => await ClaimAsync(chatId, cancellationToken),
            SecretState.Present => owner.Value == chatId.ToString(CultureInfo.InvariantCulture),
            _ => false,
        };
    }

    async Task<bool> ClaimAsync(long chatId, CancellationToken cancellationToken)
    {
        var chatIdText = chatId.ToString(CultureInfo.InvariantCulture);

        if (await secretStore.TrySetIfMissingAsync(SecretKeys.TelegramOwnerChatId, chatIdText, cancellationToken))
            return true;

        // Lost the race: another chat claimed ownership between our GetAsync and this call. Honour
        // whoever actually won rather than surfacing the conflict as an error - the loser here is
        // simply not the owner, which is the correct outcome, not a failure.
        var owner = await secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, cancellationToken);
        return owner.State is SecretState.Present && owner.Value == chatIdText;
    }
}
```

> Deterministic under concurrency by construction now, not by the "single poller" assumption the original draft of this method relied on (see Step 13A) — `TrySetIfMissingAsync` resolves the race at the database, and the loser here is simply told it lost rather than handed a raw `PostgresException`.

- [ ] **Step 17: Run green**

Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
Expected: all green.

---

#### Stage 4 — `TelegramClientHandle` and `TelegramChatNotifier`

- [ ] **Step 18: Write the failing tests**

Create `tests/Noof.Ledger.Telegram.Tests/TelegramChatNotifierTests.cs`:

```csharp
using AwesomeAssertions;
using NSubstitute;
using Noof.Ledger.Telegram;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramChatNotifierTests
{
    [Fact]
    public async Task SendAsync_sends_the_text_and_returns_the_new_message_id()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new Message { Id = 555 });
        var notifier = new TelegramChatNotifier(new TelegramClientHandle { Current = client });

        var messageId = await notifier.SendAsync(42L, "Saved.", TestContext.Current.CancellationToken);

        messageId.Should().Be(555);
        await client.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.ChatId.Identifier == 42L && r.Text == "Saved."),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EditAsync_edits_the_stored_message_by_chat_and_message_id()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<EditMessageTextRequest>(), Arg.Any<CancellationToken>())
            .Returns(new Message { Id = 555 });
        var notifier = new TelegramChatNotifier(new TelegramClientHandle { Current = client });

        await notifier.EditAsync(42L, 555, "Spent 12.34 EUR at Merc.", TestContext.Current.CancellationToken);

        await client.Received(1).SendRequest(
            Arg.Is<EditMessageTextRequest>(r => r.ChatId.Identifier == 42L && r.MessageId == 555 && r.Text == "Spent 12.34 EUR at Merc."),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_throws_when_no_client_is_ready_yet()
    {
        var notifier = new TelegramChatNotifier(new TelegramClientHandle());

        var act = () => notifier.SendAsync(42L, "hi", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
```

> `SendMessage`/`EditMessageText`/`GetUpdates`/`DeleteWebhook` are `static` extension methods, not interface members — NSubstitute cannot intercept them directly. Every one of them funnels through `ITelegramBotClient.SendRequest<TResponse>(IRequest<TResponse>, CancellationToken)`, so the tests stub and assert on `SendRequest` with the concrete request type (`SendMessageRequest`, `EditMessageTextRequest`, ...) instead — verified against the real 22.10.3.1 source, this is not a workaround of convenience, it is the only member that actually exists to mock.

- [ ] **Step 19: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
Expected: build error, `TelegramClientHandle`/`TelegramChatNotifier` do not exist (and, before those, `IChatNotifier` itself — see Step 20's defect note).

- [ ] **Step 20: Implement them**

First, the missing port. Create `src/Noof.Ledger.Application/Chat/IChatNotifier.cs` — this is the plan defect recorded in the Files list above: `IChatNotifier` was documented as already shipped, but no task before this one creates it, so it is created here, right before its sole implementation:

```csharp
namespace Noof.Ledger.Application.Chat;

public interface IChatNotifier
{
    Task<int> SendAsync(long chatId, string text, CancellationToken cancellationToken);

    Task EditAsync(long chatId, int messageId, string text, CancellationToken cancellationToken);
}
```

Create `src/Noof.Ledger.Telegram/TelegramClientHandle.cs`:

```csharp
using Telegram.Bot;

namespace Noof.Ledger.Telegram;

public sealed class TelegramClientHandle
{
    public ITelegramBotClient? Current { get; set; }
}
```

Create `src/Noof.Ledger.Telegram/TelegramChatNotifier.cs`:

```csharp
using Noof.Ledger.Application.Chat;
using Telegram.Bot;

namespace Noof.Ledger.Telegram;

public sealed class TelegramChatNotifier(TelegramClientHandle clientHandle) : IChatNotifier
{
    public async Task<int> SendAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        var message = await Client().SendMessage(chatId, text, cancellationToken: cancellationToken);
        return message.Id;
    }

    public async Task EditAsync(long chatId, int messageId, string text, CancellationToken cancellationToken) =>
        await Client().EditMessageText(chatId, messageId, text, cancellationToken: cancellationToken);

    ITelegramBotClient Client() =>
        clientHandle.Current ?? throw new InvalidOperationException(
            "The Telegram client is not ready yet: no bot token has been saved.");
}
```

- [ ] **Step 21: Run green**

Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
Expected: all green.

---

#### Stage 5 — `ITelegramUpdateRouter` / `TelegramUpdateRouter` (the owner check comes first)

- [ ] **Step 22: Add `Microsoft.Extensions.TimeProvider.Testing`, then write the failing tests**

First check whether `Directory.Packages.props` already has a `PackageVersion` for `Microsoft.Extensions.TimeProvider.Testing` (an earlier task in this phase may have added it for its own `TimeProvider` tests — check before adding a second, possibly conflicting version). If not, add it to the `Testing` `ItemGroup`:

```xml
    <PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.10.0" />
```

Add it to the test project's `PackageReference` group in `tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`:

```xml
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
```

> **`CapturedMessage.SentAt` must come from Telegram's own `Message.Date`, never from a clock read at handling time (2026-09-21 review).** The whole point of this phase is that a message survives an outage: one sent at 23:50 and processed at 08:00 the next day must keep its 23:50 timestamp, because `time_zone_id` exists specifically so day/month buckets can be computed from it later (Phase 1B). A test that pins `CapturedMessage.SentAt` to a fake clock's `GetUtcNow()` is wrong at this layer even though it looks identical to a correct test — it only catches drift if the message's own `Date` and the processing clock are made to disagree. `Message.Date` deserialises as `DateTime` with `Kind=Utc` (verified against Telegram.Bot 22.10.3.1's `UnixDateTimeConverter`), so `new DateTimeOffset(message.Date)` carries a genuine zero offset. Because nothing in `TelegramUpdateRouter` reads the clock once this is fixed, its constructor drops the `TimeProvider` parameter entirely — write the test file below as shown, not with a `FakeTimeProvider` pinning `SentAt` to `now`.

Create `tests/Noof.Ledger.Telegram.Tests/TelegramUpdateRouterTests.cs`:

```csharp
using AwesomeAssertions;
using NSubstitute;
using Noof.Ledger.Application.Capture;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Telegram;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramUpdateRouterTests
{
    static (TelegramUpdateRouter Router, ICaptureStore CaptureStore, IChatNotifier ChatNotifier) CreateRouter(long ownerChatId)
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Present, ownerChatId.ToString()));
        var captureStore = Substitute.For<ICaptureStore>();
        var chatNotifier = Substitute.For<IChatNotifier>();
        var router = new TelegramUpdateRouter(captureStore, chatNotifier, new TelegramOwnerGate(secretStore));

        return (router, captureStore, chatNotifier);
    }

    static Update TextMessage(long chatId, int messageId, string text, DateTime date) => new()
    {
        Id = 900,
        Message = new Message { Id = messageId, Chat = new Chat { Id = chatId }, Text = text, Date = date },
    };

    [Fact]
    public async Task Captures_replies_and_attaches_the_reply_for_the_owner()
    {
        var (router, captureStore, chatNotifier) = CreateRouter(ownerChatId: 111L);
        var sentAt = DateTimeOffset.Parse("2026-09-21T10:00:00Z");
        var transactionId = Guid.NewGuid();
        captureStore.CaptureAsync(Arg.Any<CapturedMessage>(), "Europe/Belgrade", Arg.Any<CancellationToken>())
            .Returns(transactionId);
        chatNotifier.SendAsync(111L, TelegramUpdateRouter.ReceiptAcknowledgement, Arg.Any<CancellationToken>())
            .Returns(777);

        await router.HandleAsync(
            TextMessage(111L, 5, "coffee 3.20 EUR", sentAt.UtcDateTime),
            "Europe/Belgrade",
            TestContext.Current.CancellationToken);

        await captureStore.Received(1).CaptureAsync(
            Arg.Is<CapturedMessage>(m => m.ChatId == 111L && m.MessageId == 5 && m.Text == "coffee 3.20 EUR" && m.SentAt == sentAt),
            "Europe/Belgrade",
            Arg.Any<CancellationToken>());
        await captureStore.Received(1).AttachBotMessageAsync(transactionId, 777, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stores_the_time_Telegram_sent_the_message_not_the_time_it_was_processed()
    {
        // An outage can queue a message for hours; Telegram's own Message.Date is when the spend
        // happened, "now" at handling time is only when we got around to it. Collapsing every
        // queued message onto the reconnection moment buckets it into the wrong local day once
        // Phase 1B reads time_zone_id back to compute "today" / "this month".
        var (router, captureStore, chatNotifier) = CreateRouter(ownerChatId: 111L);
        var sentAt = DateTimeOffset.Parse("2026-09-21T23:50:00Z");
        captureStore.CaptureAsync(Arg.Any<CapturedMessage>(), "Europe/Belgrade", Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());
        chatNotifier.SendAsync(111L, TelegramUpdateRouter.ReceiptAcknowledgement, Arg.Any<CancellationToken>())
            .Returns(777);

        // "Processed" hours after "sent" -- exactly the outage-recovery scenario this guards.
        await router.HandleAsync(
            TextMessage(111L, 5, "coffee 3.20 EUR", sentAt.UtcDateTime),
            "Europe/Belgrade",
            TestContext.Current.CancellationToken);

        await captureStore.Received(1).CaptureAsync(
            Arg.Is<CapturedMessage>(m => m.SentAt == sentAt),
            "Europe/Belgrade",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rejects_a_stranger_before_capturing_or_replying()
    {
        var (router, captureStore, chatNotifier) = CreateRouter(ownerChatId: 111L);

        await router.HandleAsync(
            TextMessage(999L, 5, "coffee 3.20 EUR", DateTime.UtcNow),
            "Europe/Belgrade",
            TestContext.Current.CancellationToken);

        await captureStore.DidNotReceive().CaptureAsync(Arg.Any<CapturedMessage>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await chatNotifier.DidNotReceive().SendAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ignores_an_update_with_no_message()
    {
        var (router, captureStore, _) = CreateRouter(ownerChatId: 111L);

        await router.HandleAsync(new Update { Id = 901 }, "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.DidNotReceive().CaptureAsync(Arg.Any<CapturedMessage>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ignores_a_message_with_no_text()
    {
        var (router, captureStore, _) = CreateRouter(ownerChatId: 111L);
        var update = new Update { Id = 902, Message = new Message { Id = 6, Chat = new Chat { Id = 111L } } };

        await router.HandleAsync(update, "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.DidNotReceive().CaptureAsync(Arg.Any<CapturedMessage>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 23: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
Expected: build error, `ITelegramUpdateRouter`/`TelegramUpdateRouter` do not exist.

- [ ] **Step 24: Implement them**

Create `src/Noof.Ledger.Telegram/ITelegramUpdateRouter.cs`:

```csharp
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram;

public interface ITelegramUpdateRouter
{
    Task HandleAsync(Update update, string timeZoneId, CancellationToken cancellationToken);
}
```

Create `src/Noof.Ledger.Telegram/TelegramUpdateRouter.cs`:

```csharp
using Noof.Ledger.Application.Capture;
using Noof.Ledger.Application.Chat;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram;

public sealed class TelegramUpdateRouter(
    ICaptureStore captureStore,
    IChatNotifier chatNotifier,
    TelegramOwnerGate ownerGate)
    : ITelegramUpdateRouter
{
    public const string ReceiptAcknowledgement = "Saved. I'll add the amount once it's categorised.";

    public async Task HandleAsync(Update update, string timeZoneId, CancellationToken cancellationToken)
    {
        var message = update.Message;
        if (message is null)
            return;

        // Reject before reading Text: a stranger's content must never be inspected, not even to
        // decide whether it looks like a spend.
        if (!await ownerGate.IsAllowedAsync(message.Chat.Id, cancellationToken))
            return;

        if (message.Text is not { Length: > 0 } text)
            return;

        // message.Date is when Telegram received it from the sender, not when we got around to
        // processing it -- an outage can queue a message for hours, and every queued message must
        // keep its own moment so time_zone_id buckets it into the correct local day later.
        // Message.Date deserialises as DateTime with Kind=Utc (confirmed against Telegram.Bot
        // 22.10.3.1's UnixDateTimeConverter), so this offset is genuinely zero, not just labelled so.
        var sentAt = new DateTimeOffset(message.Date);
        var captured = new CapturedMessage(message.Chat.Id, message.Id, text, sentAt);
        var transactionId = await captureStore.CaptureAsync(captured, timeZoneId, cancellationToken);

        var botMessageId = await chatNotifier.SendAsync(message.Chat.Id, ReceiptAcknowledgement, cancellationToken);
        await captureStore.AttachBotMessageAsync(transactionId, botMessageId, cancellationToken);
    }
}
```

- [ ] **Step 25: Run green**

Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
Expected: all green (6 tests in `TelegramUpdateRouterTests`, up from 5, plus everything else in this project).

---

#### Stage 6 — `ITelegramBotClientFactory` and the poller itself

- [ ] **Step 26: Write the failing test for `ITelegramBotClientFactory`**

Create `tests/Noof.Ledger.Telegram.Tests/TelegramBotClientFactoryTests.cs`:

```csharp
using AwesomeAssertions;
using NSubstitute;
using Noof.Ledger.Telegram;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramBotClientFactoryTests
{
    [Fact]
    public void Create_requests_the_named_telegram_http_client()
    {
        const string validlyShapedToken = "123456:AAETopSecretBotTokenValue";
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("telegram").Returns(new HttpClient());
        var factory = new TelegramBotClientFactory(httpClientFactory);

        var client = factory.Create(validlyShapedToken);

        client.Should().NotBeNull();
        httpClientFactory.Received(1).CreateClient("telegram");
    }
}
```

> **Why the token has to look real.** Unlike `TelegramPollingServiceTests` below, which substitutes the whole `ITelegramBotClientFactory` and never touches a real constructor, this test calls the real `TelegramBotClientFactory.Create`, which calls the real `TelegramBotClient(string, HttpClient)` constructor. Verified by actually constructing one against the real 22.10.3.1 package: that constructor validates the token shape eagerly and throws `ArgumentException: Bot token invalid` for a throwaway string like `"tok1"`. `"123456:AAETopSecretBotTokenValue"` is a shape it accepts — confirmed the same way, not assumed — and it is reused verbatim from `TelegramHttpClientLoggingTests.cs` later in this task.
>
> This test is also the thing that would catch a typo in `"telegram"` itself — that exact string has to match, character for character, three independent places: this factory's `CreateClient("telegram")` call, `builder.Services.AddHttpClient("telegram")` in Step 34, and the log filter category `System.Net.Http.HttpClient.telegram` in Step 37. A mismatch in any one of the three would silently either break the bot or defeat the token-redaction requirement, and nothing else in this task would notice.

- [ ] **Step 27: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
Expected: build error, `TelegramBotClientFactory` does not exist.

- [ ] **Step 28: Implement it**

Create `src/Noof.Ledger.Telegram/ITelegramBotClientFactory.cs`:

```csharp
using Telegram.Bot;

namespace Noof.Ledger.Telegram;

public interface ITelegramBotClientFactory
{
    ITelegramBotClient Create(string token);
}
```

Create `src/Noof.Ledger.Telegram/TelegramBotClientFactory.cs`:

```csharp
using Telegram.Bot;

namespace Noof.Ledger.Telegram;

public sealed class TelegramBotClientFactory(IHttpClientFactory httpClientFactory) : ITelegramBotClientFactory
{
    public ITelegramBotClient Create(string token) =>
        new TelegramBotClient(token, httpClientFactory.CreateClient("telegram"));
}
```

- [ ] **Step 29: Run green**

Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
Expected: all green.

- [ ] **Step 30: Write the failing tests for `TelegramPollingService`**

> **The DI trap that would only surface at runtime.** `ISecretStore` and `ICaptureStore` are almost certainly backed by `LedgerDbContext` (exactly like `EfUserStore`), which means their DI lifetime is `Scoped`. `TelegramPollingService` is registered with `AddHostedService`, which is a **singleton**. A singleton cannot directly depend on a scoped service — `WebApplicationBuilder` validates this at `builder.Build()` time in Development and throws immediately. So `TelegramPollingService` must **not** take `ISecretStore`, `TelegramUpdateOffsetStore`, or `ITelegramUpdateRouter` as constructor parameters at all. It takes `IServiceScopeFactory` and creates one scope per tick, resolving those three from it. `ITelegramBotClientFactory` and `TelegramClientHandle` have no scoped dependencies, so they stay as ordinary constructor parameters.

Create `tests/Noof.Ledger.Telegram.Tests/TelegramPollingServiceTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Telegram;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramPollingServiceTests
{
    static IServiceScopeFactory ScopeFactoryFor(
        ISecretStore secretStore, ITelegramUpdateRouter? router = null, IChatNotifier? chatNotifier = null)
    {
        var offsetStore = new TelegramUpdateOffsetStore(secretStore);

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(ISecretStore)).Returns(secretStore);
        provider.GetService(typeof(TelegramUpdateOffsetStore)).Returns(offsetStore);
        if (router is not null)
            provider.GetService(typeof(ITelegramUpdateRouter)).Returns(router);
        if (chatNotifier is not null)
            provider.GetService(typeof(IChatNotifier)).Returns(chatNotifier);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);

        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    static ISecretStore NoTokenYet()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramBotToken, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Missing, null));
        return secretStore;
    }

    static ISecretStore WithToken(string token)
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramBotToken, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Present, token));
        secretStore.GetAsync(TelegramUpdateOffsetStore.Key, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Missing, null));
        return secretStore;
    }

    static TelegramPollingService CreateService(
        ISecretStore secretStore,
        ITelegramBotClientFactory clientFactory,
        TelegramClientHandle handle,
        ITelegramUpdateRouter? router = null,
        IChatNotifier? chatNotifier = null) =>
        new(
            ScopeFactoryFor(secretStore, router, chatNotifier),
            clientFactory,
            handle,
            new ConfigurationBuilder().Build(),
            TimeProvider.System,
            NullLogger<TelegramPollingService>.Instance);

    [Fact]
    public async Task Stays_idle_when_no_token_has_been_saved_yet()
    {
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        var service = CreateService(NoTokenYet(), clientFactory, new TelegramClientHandle());

        var result = await service.RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(TelegramPollResult.Idle);
        clientFactory.DidNotReceive().Create(Arg.Any<string>());
    }

    [Fact]
    public async Task Builds_the_client_and_deletes_the_webhook_the_first_time_a_token_appears()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Update>());
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);
        var handle = new TelegramClientHandle();

        var result = await CreateService(WithToken("tok1"), clientFactory, handle).RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(TelegramPollResult.Processed);
        handle.Current.Should().BeSameAs(client);
        await client.Received(1).SendRequest(Arg.Any<DeleteWebhookRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Does_not_rebuild_the_client_when_the_token_is_unchanged()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Update>());
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);
        var service = CreateService(WithToken("tok1"), clientFactory, new TelegramClientHandle());

        await service.RunTickAsync(TestContext.Current.CancellationToken);
        await service.RunTickAsync(TestContext.Current.CancellationToken);

        clientFactory.Received(1).Create("tok1");
        await client.Received(1).SendRequest(Arg.Any<DeleteWebhookRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rebuilds_the_client_and_redeletes_the_webhook_when_the_token_changes()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramBotToken, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Present, "tok1"), new SecretResult(SecretState.Present, "tok2"));
        secretStore.GetAsync(TelegramUpdateOffsetStore.Key, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Missing, null));

        var client1 = Substitute.For<ITelegramBotClient>();
        client1.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Update>());
        var client2 = Substitute.For<ITelegramBotClient>();
        client2.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Update>());
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client1);
        clientFactory.Create("tok2").Returns(client2);
        var handle = new TelegramClientHandle();
        var service = CreateService(secretStore, clientFactory, handle);

        await service.RunTickAsync(TestContext.Current.CancellationToken);
        await service.RunTickAsync(TestContext.Current.CancellationToken);

        handle.Current.Should().BeSameAs(client2);
        await client1.Received(1).SendRequest(Arg.Any<DeleteWebhookRequest>(), Arg.Any<CancellationToken>());
        await client2.Received(1).SendRequest(Arg.Any<DeleteWebhookRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Routes_each_update_and_persists_the_offset_after_each_one()
    {
        var secretStore = WithToken("tok1");
        var update10 = new Update { Id = 10, Message = new Message { Id = 1, Chat = new Chat { Id = 111L }, Text = "a" } };
        var update11 = new Update { Id = 11, Message = new Message { Id = 2, Chat = new Chat { Id = 111L }, Text = "b" } };
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns([update10, update11]);
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);
        var router = Substitute.For<ITelegramUpdateRouter>();

        var result = await CreateService(secretStore, clientFactory, new TelegramClientHandle(), router)
            .RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(TelegramPollResult.Processed);
        Received.InOrder(() =>
        {
            router.HandleAsync(update10, "Europe/Belgrade", Arg.Any<CancellationToken>());
            secretStore.SetAsync(TelegramUpdateOffsetStore.Key, "11", Arg.Any<CancellationToken>());
            router.HandleAsync(update11, "Europe/Belgrade", Arg.Any<CancellationToken>());
            secretStore.SetAsync(TelegramUpdateOffsetStore.Key, "12", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task GetUpdates_throwing_is_reported_as_failed_without_throwing_or_advancing_the_offset()
    {
        var secretStore = WithToken("tok1");
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>())
            .Returns<Update[]>(_ => throw new HttpRequestException("cable pulled"));
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);
        var router = Substitute.For<ITelegramUpdateRouter>();

        var result = await CreateService(secretStore, clientFactory, new TelegramClientHandle(), router)
            .RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(TelegramPollResult.Failed);
        await router.DidNotReceive().HandleAsync(Arg.Any<Update>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await secretStore.DidNotReceive().SetAsync(TelegramUpdateOffsetStore.Key, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Keeps_polling_after_a_failed_tick()
    {
        var secretStore = WithToken("tok1");
        var client = Substitute.For<ITelegramBotClient>();
        var attempt = 0;
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                attempt++;
                if (attempt == 1)
                    throw new HttpRequestException("cable pulled");
                return Array.Empty<Update>();
            });
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);
        var service = CreateService(secretStore, clientFactory, new TelegramClientHandle());

        var first = await service.RunTickAsync(TestContext.Current.CancellationToken);
        var second = await service.RunTickAsync(TestContext.Current.CancellationToken);

        first.Should().Be(TelegramPollResult.Failed);
        second.Should().Be(TelegramPollResult.Processed);
        await client.Received(2).SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_poison_update_is_skipped_after_repeated_failures_so_the_update_behind_it_still_gets_processed()
    {
        var secretStore = WithToken("tok1");
        var poisonUpdate = new Update { Id = 10, Message = new Message { Id = 1, Chat = new Chat { Id = 111L }, Text = "a" } };
        var laterUpdate = new Update { Id = 11, Message = new Message { Id = 2, Chat = new Chat { Id = 111L }, Text = "b" } };
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns([poisonUpdate, laterUpdate]);
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);
        var router = Substitute.For<ITelegramUpdateRouter>();
        router.HandleAsync(poisonUpdate, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("no wallet is marked as the default"));
        var chatNotifier = Substitute.For<IChatNotifier>();
        var service = CreateService(secretStore, clientFactory, new TelegramClientHandle(), router, chatNotifier);

        var first = await service.RunTickAsync(TestContext.Current.CancellationToken);
        var second = await service.RunTickAsync(TestContext.Current.CancellationToken);
        var third = await service.RunTickAsync(TestContext.Current.CancellationToken);

        first.Should().Be(TelegramPollResult.Failed, "the poison update is still within its retry budget");
        second.Should().Be(TelegramPollResult.Failed, "still within budget - nothing behind it may run yet");
        third.Should().Be(TelegramPollResult.Processed, "attempts are exhausted, so the poller skips it and moves on");
        await router.Received(3).HandleAsync(poisonUpdate, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await router.Received(1).HandleAsync(laterUpdate, "Europe/Belgrade", Arg.Any<CancellationToken>());
        await secretStore.Received(1).SetAsync(TelegramUpdateOffsetStore.Key, "12", Arg.Any<CancellationToken>());
        await chatNotifier.Received(1).SendAsync(111L, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_operator_notification_failure_does_not_prevent_the_poison_update_from_being_skipped()
    {
        var secretStore = WithToken("tok1");
        var poisonUpdate = new Update { Id = 10, Message = new Message { Id = 1, Chat = new Chat { Id = 111L }, Text = "a" } };
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns([poisonUpdate]);
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);
        var router = Substitute.For<ITelegramUpdateRouter>();
        router.HandleAsync(poisonUpdate, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("boom"));
        var chatNotifier = Substitute.For<IChatNotifier>();
        chatNotifier.SendAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("chat unreachable too"));
        var service = CreateService(secretStore, clientFactory, new TelegramClientHandle(), router, chatNotifier);

        await service.RunTickAsync(TestContext.Current.CancellationToken);
        await service.RunTickAsync(TestContext.Current.CancellationToken);
        var third = await service.RunTickAsync(TestContext.Current.CancellationToken);

        third.Should().Be(TelegramPollResult.Processed, "the skip itself must not be undone by a failed notification");
        await secretStore.Received(1).SetAsync(TelegramUpdateOffsetStore.Key, "11", Arg.Any<CancellationToken>());
    }
}
```

> This is the automated proof behind "`getUpdates` throwing must log, back off, and keep polling — never kill the service": the last two tests show a thrown exception turns into `TelegramPollResult.Failed` (never an escaped exception) and that the very next tick calls `GetUpdates` again on the same client. The "pull the actual network cable while the real bot is live" scenario itself is a manual check (Step 40) — nothing in this repo's default test loop is allowed to touch a real network or a real Telegram server.
>
> **Known, accepted gap.** None of these tests call `ExecuteAsync` — the actual `BackgroundService` loop that reads the result of `RunTickAsync` and decides how long to wait before the next tick. Every test here drives `RunTickAsync` directly. That means the idle/backoff delay selection inside `ExecuteAsync` (Step 32) is proven correct only by inspection, not by a passing test — spinning up a real `BackgroundService` against a `FakeTimeProvider` and synchronising on tick counts is a reasonable follow-up, but it is more machinery than this task's budget covers, and pretending otherwise here would be worse than naming the gap.
>
> **Plan defect, fixed above.** `GetUpdates_throwing_is_reported_as_failed_without_throwing_or_advancing_the_offset` originally read `.Returns(_ => throw new HttpRequestException("cable pulled"))`. `SendRequest<TResponse>` returns `Task<TResponse>` (here `Task<Update[]>`), and NSubstitute 6.2.0 (the pinned version) exposes both `Returns<T>(T, Func<CallInfo,T>, ...)` and `Returns<T>(Task<T>, Func<CallInfo,T>, ...)` as extension methods; because a `throw` expression converts to any type, the compiler cannot pick between `T = Task<Update[]>` and `T = Update[]` and the build fails with `CS0121: The call is ambiguous`. Confirmed by building this exact test against this repo's pinned package versions, not assumed. The fix is the explicit type argument shown above, `.Returns<Update[]>(_ => throw ...)`, which restricts resolution to the `Task<T>`-returning overload. `Keeps_polling_after_a_failed_tick`'s block-bodied lambda a few tests later does not need this — its `return Array.Empty<Update>();` statement already pins the inferred `T` to `Update[]`, so the ambiguity only bites the single-expression `throw` form.

- [ ] **Step 31: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
Expected: build error, `TelegramPollingService`/`TelegramPollResult` do not exist.

- [ ] **Step 32: Implement `TelegramPollingService`**

Create `src/Noof.Ledger.Telegram/TelegramPollingService.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Secrets;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Noof.Ledger.Telegram;

public enum TelegramPollResult { Idle, Processed, Failed }

public sealed class TelegramPollingService(
    IServiceScopeFactory scopeFactory,
    ITelegramBotClientFactory clientFactory,
    TelegramClientHandle clientHandle,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<TelegramPollingService> logger)
    : BackgroundService
{
    const string PoisonUpdateNotice =
        "Sorry, I couldn't process this message after several attempts. I'm skipping it so newer messages aren't stuck behind it.";

    static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(5);
    const int MaxUpdateAttempts = 3;

    string? activeToken;
    int? offset;
    int consecutiveFailures;
    int? poisonUpdateId;
    int poisonUpdateAttempts;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await RunTickAsync(stoppingToken);

            var delay = result switch
            {
                TelegramPollResult.Idle => IdlePollInterval,
                TelegramPollResult.Failed => TelegramBackoff.Compute(consecutiveFailures),
                _ => TimeSpan.Zero,
            };

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, timeProvider, stoppingToken);
        }
    }

    public async Task<TelegramPollResult> RunTickAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var secretStore = scope.ServiceProvider.GetRequiredService<ISecretStore>();

            var secret = await secretStore.GetAsync(SecretKeys.TelegramBotToken, cancellationToken);

            if (secret.State is not SecretState.Present)
                return TelegramPollResult.Idle;

            if (secret.Value != activeToken)
            {
                var client = clientFactory.Create(secret.Value!);
                await client.DeleteWebhook(cancellationToken: cancellationToken);
                clientHandle.Current = client;
                activeToken = secret.Value;

                var offsetStore = scope.ServiceProvider.GetRequiredService<TelegramUpdateOffsetStore>();
                offset ??= await offsetStore.GetAsync(cancellationToken);
            }

            var timeZoneId = configuration["Capture:TimeZone"] ?? "Europe/Belgrade";
            var pollingSeconds = int.TryParse(configuration["Telegram:PollingSeconds"], out var seconds) ? seconds : 30;

            // clientHandle.Current is always assigned above whenever secret.Value != activeToken,
            // and that branch is guaranteed to have run at least once by this point: activeToken
            // starts null and secret.State is Present here, so the very first successful tick sets it
            // before this line is ever reached.
            var updates = await clientHandle.Current!.GetUpdates(
                offset: offset,
                timeout: pollingSeconds,
                allowedUpdates: [UpdateType.Message],
                cancellationToken: cancellationToken);

            if (updates.Length > 0)
            {
                var router = scope.ServiceProvider.GetRequiredService<ITelegramUpdateRouter>();
                var offsetStore = scope.ServiceProvider.GetRequiredService<TelegramUpdateOffsetStore>();

                foreach (var update in updates)
                {
                    try
                    {
                        await router.HandleAsync(update, timeZoneId, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (poisonUpdateId != update.Id)
                        {
                            poisonUpdateId = update.Id;
                            poisonUpdateAttempts = 0;
                        }
                        poisonUpdateAttempts++;

                        logger.LogError(ex, "Telegram update {UpdateId} failed on attempt {Attempt}/{MaxAttempts}",
                            update.Id, poisonUpdateAttempts, MaxUpdateAttempts);

                        if (poisonUpdateAttempts < MaxUpdateAttempts)
                        {
                            // Leave the offset where it is so this same update is retried on the
                            // next tick, and stop here - anything behind it waits for its turn,
                            // the same as it always has, until the attempt budget runs out.
                            consecutiveFailures++;
                            return TelegramPollResult.Failed;
                        }

                        logger.LogError(
                            "Telegram update {UpdateId} failed {MaxAttempts} times; skipping it so later updates aren't blocked behind it",
                            update.Id, MaxUpdateAttempts);
                        await NotifyOperatorOfSkippedUpdateAsync(scope, update, cancellationToken);
                        poisonUpdateId = null;
                        poisonUpdateAttempts = 0;
                    }

                    // Reached both when HandleAsync succeeds and when it has just been given up on
                    // as poison - either way this update is done with, and the offset moves past
                    // it. Advancing past a poison update trades that one lost message for a queue
                    // that keeps working; Telegram itself discards unconfirmed updates after 24
                    // hours, so leaving the offset here forever loses every later message too.
                    offset = update.Id + 1;
                    await offsetStore.SetAsync(offset.Value, cancellationToken);
                }
            }

            consecutiveFailures = 0;
            return TelegramPollResult.Processed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            consecutiveFailures++;
            logger.LogError(ex, "Telegram poll tick failed; backing off and retrying");
            return TelegramPollResult.Failed;
        }
    }

    async Task NotifyOperatorOfSkippedUpdateAsync(IServiceScope scope, Update update, CancellationToken cancellationToken)
    {
        if (update.Message is not { } message)
            return;

        try
        {
            var chatNotifier = scope.ServiceProvider.GetRequiredService<IChatNotifier>();
            await chatNotifier.SendAsync(message.Chat.Id, PoisonUpdateNotice, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to notify the operator that Telegram update {UpdateId} was skipped", update.Id);
        }
    }
}
```

> **Closing-review fix (2026-09-21): one poisoned update no longer stalls the poller forever.** As originally drafted above, any exception from `router.HandleAsync` left the offset unadvanced with no bound on retries — the same update was refetched every tick, nothing behind it was ever processed, and the log blamed "getUpdates" even when the failure came from `HandleAsync`, `DeleteWebhook`, or DI resolution. This is reachable in ordinary operation, not hypothetical: unmarking the default wallet makes `EfCaptureStore.CaptureAsync` throw `InvalidOperationException` on every tick, forever, and Telegram discards unconfirmed updates after 24 hours — so the phase's "nothing is lost" guarantee silently stopped being true. **Policy chosen:** each update gets `MaxUpdateAttempts` (3) tries, one per poll tick, tracked by `poisonUpdateId`/`poisonUpdateAttempts` against the head of the batch; while attempts remain, the tick reports `Failed` and nothing behind the stuck update runs, exactly as before. Once attempts are exhausted, the poller (a) sends `PoisonUpdateNotice` to the update's own chat via `IChatNotifier` — swallowing and logging any failure from that send, since a broken notification must not re-stall the queue it exists to unstick — (b) logs the skip at `LogError`, and (c) **advances the offset past the poisoned update anyway**, trading that one message for a working queue, and continues processing whatever else is in the same batch. Advancing past it, rather than holding forever, was the deliberate call: Telegram's own 24-hour discard makes "hold forever" equivalent to "lose everything after it too," which is strictly worse, and only the operator's own chat — the one channel already built — can actually recover after that, so it gets told directly rather than through a log an unattended box will never surface. The `catch (Exception ex) when (ex is not OperationCanceledException)` message around the whole tick was also changed, from the misleading `"Telegram getUpdates failed"` (untrue whenever the throw came from anywhere else in the method) to `"Telegram poll tick failed"`. Proven by two new facts in `TelegramPollingServiceTests.cs`: `A_poison_update_is_skipped_after_repeated_failures_so_the_update_behind_it_still_gets_processed` (three ticks against a router that always throws for one update and succeeds for the next — the first two ticks report `Failed` and never touch the later update, the third reports `Processed`, has called the router for the later update, has advanced the offset past both, and has notified the chat once) and `An_operator_notification_failure_does_not_prevent_the_poison_update_from_being_skipped` (the skip still happens even when `IChatNotifier.SendAsync` itself throws).

> `GetUpdates`/`DeleteWebhook` are extension methods living in the `Telegram.Bot` namespace — miss the `using Telegram.Bot;` and the errors read like the methods don't exist at all, which is misleading; the interface is right there, the namespace just isn't imported.
>
> **`TimeProvider` has no `Delay` method — confirmed by compiling against it, not assumed.** Reflecting the real .NET 10 `TimeProvider` type shows exactly five members: `GetUtcNow`, `GetLocalNow`, `GetTimestamp`, `GetElapsedTime`, `CreateTimer`. There is no instance or extension `Delay`. The testable, `FakeTimeProvider`-aware delay is the static overload `Task.Delay(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken)` used above — writing `timeProvider.Delay(delay, stoppingToken)` instead is `CS1061` and fails the whole project's build. Nothing in `TelegramPollingServiceTests.cs` would catch this before the build itself does, since every test there drives `RunTickAsync` directly and never runs `ExecuteAsync` (see the "known, accepted gap" note under Step 30) — which is exactly why it is called out here explicitly rather than left to be discovered by a failed build in Step 33.
>
> **Plan defect, fixed above, and it is not merely theoretical.** The version of this method originally drafted here called `secretStore.GetAsync(SecretKeys.TelegramBotToken, cancellationToken)` *before* the `try`, not inside it. `EfSecretStore.GetAsync` (the real implementation this resolves to at runtime) does `await db.Secrets.FindAsync(...)` with no exception handling around connectivity failures — it throws when the database is unreachable, same as any other EF query. With the token fetch outside the `try`, that exception propagated out of `RunTickAsync`, out of `ExecuteAsync`'s loop, and into `BackgroundService`'s own exception handling, whose .NET default (`BackgroundServiceExceptionBehavior.StopHost`) stops the entire host. This is not a hypothetical: running the full `Noof.Ledger.Host.Tests` suite with the token fetch outside the `try` turned `LoopbackGuardTerminatesTests.An_exposed_binding_is_allowed_once_auth_is_on` — a Task-1–7 test that was green before this task started — red, because that test points the host at a deliberately dead connection string and expects it to keep running for 8 seconds; `TelegramPollingService`'s very first tick killed it instead. Moving the token fetch inside the `try` (shown above) fixed it: the token fetch is a tick failure like any other now, reported as `TelegramPollResult.Failed` rather than escaping.
>
> **Second plan defect, found by a later security review and also fixed above.** Moving the token fetch inside the `try` was only half the fix: `scopeFactory.CreateScope()` and `scope.ServiceProvider.GetRequiredService<ISecretStore>()` were left *outside* it. A reviewer proved this is just as fatal — injecting a throw at that exact point turned the same canary, `LoopbackGuardTerminatesTests.An_exposed_binding_is_allowed_once_auth_is_on`, from green to red, with the host exiting instead of serving for its required 8 seconds. `BackgroundService`'s `StopHost` default does not care whether the escaping exception came from a network call or from DI resolution. Everything that can throw — scope creation and every `GetRequiredService` included — now lives inside the `try`.

Add this discriminating test to `tests/Noof.Ledger.Telegram.Tests/TelegramPollingServiceTests.cs`, directly before the closing brace of the test class:

```csharp
    // Commit a069aae moved the token fetch inside the try but left scope creation and the
    // ISecretStore resolution outside it. An exception thrown while resolving a dependency is
    // exactly as fatal to the host as one thrown by the token fetch itself - BackgroundService's
    // default ExceptionBehavior is StopHost - so this must be swallowed and reported as Failed
    // the same way. A mock configured to throw would prove the same thing less directly than a
    // fake that actually behaves like a broken container.
    [Fact]
    public async Task A_DI_resolution_failure_while_creating_the_scope_is_reported_as_failed_without_throwing()
    {
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        var service = new TelegramPollingService(
            new ThrowingScopeFactory(),
            clientFactory,
            new TelegramClientHandle(),
            new ConfigurationBuilder().Build(),
            TimeProvider.System,
            NullLogger<TelegramPollingService>.Instance);

        var result = await service.RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(TelegramPollResult.Failed);
        clientFactory.DidNotReceive().Create(Arg.Any<string>());
    }

    sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new ThrowingScope();

        sealed class ThrowingScope : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new ThrowingProvider();

            public void Dispose() { }
        }

        sealed class ThrowingProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) =>
                serviceType == typeof(ISecretStore)
                    ? throw new InvalidOperationException("the container cannot resolve ISecretStore")
                    : null;
        }
    }
```

- [ ] **Step 33: Run green**

Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
Expected: all green.

---

#### Stage 7 — wire the Host, prove the token never reaches a log

- [ ] **Step 34: Wire `Program.cs` (without redacting the client's logging yet — that omission is deliberate, see Step 35)**

Open `src/Noof.Ledger.Host/Program.cs`. Add these two `using` directives (alphabetically, among the existing `Noof.Ledger.*` ones):

```csharp
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Telegram;
```

Insert this block immediately before `var app = builder.Build();` (leave anything an earlier task already put there — `ISecretStore`/`ICaptureStore` registrations — untouched; add this alongside them):

```csharp
builder.Services.AddHttpClient("telegram");

builder.Services.AddSingleton<TelegramClientHandle>();
builder.Services.AddSingleton<ITelegramBotClientFactory, TelegramBotClientFactory>();
builder.Services.AddSingleton<IChatNotifier, TelegramChatNotifier>();
builder.Services.AddScoped<TelegramOwnerGate>();
builder.Services.AddScoped<TelegramUpdateOffsetStore>();
builder.Services.AddScoped<ITelegramUpdateRouter, TelegramUpdateRouter>();
builder.Services.AddHostedService<TelegramPollingService>();
```

`TelegramOwnerGate` and `TelegramUpdateOffsetStore` are `Scoped`, not `Singleton`, even though nothing here looks stateful about them: both depend on `ISecretStore`, and if that turns out to be `Scoped` (as `EfUserStore` is), a `Singleton` registration would throw at `builder.Build()` the same way a raw `TelegramPollingService` constructor dependency would (see Step 30's note) — `TelegramUpdateRouter` depends on both of them plus `ICaptureStore`, so it is `Scoped` too.

`Noof.Ledger.Host.csproj` already references `Noof.Ledger.Telegram` — no project file change needed.

- [ ] **Step 35: Write the failing log-redaction test**

Create `tests/Noof.Ledger.Host.Tests/TelegramHttpClientLoggingTests.cs`:

> **Plan defect, fixed here.** The `using` list below was missing `Microsoft.AspNetCore.Hosting` — confirmed by compiling this exact file: without it, `builder.ConfigureLogging(...)` on the `IWebHostBuilder` fails with `CS1061`, because `ConfigureLogging` is an extension method declared in that namespace, not pulled in transitively by `Microsoft.AspNetCore.Mvc.Testing` or `Microsoft.AspNetCore.TestHost`.

```csharp
using System.Collections.Concurrent;
using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Noof.Ledger.Host.Tests;

public class TelegramHttpClientLoggingTests
{
    [Fact]
    public async Task No_captured_log_line_contains_the_telegram_token()
    {
        const string token = "123456:AAETopSecretBotTokenValue";
        var lines = new ConcurrentQueue<string>();

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:Mode", "Off");
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(lines)));
            builder.ConfigureTestServices(services =>
                services.AddHttpClient("telegram").ConfigurePrimaryHttpMessageHandler(() => new StubHandler()));
        });

        var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("telegram");
        await client.GetAsync($"http://example.invalid/bot{token}/getMe", TestContext.Current.CancellationToken);

        lines.Should().NotContain(line => line.Contains(token));
    }

    // AddFilter("System.Net.Http.HttpClient.telegram", LogLevel.None) is a suppression, not a
    // removal: a more specific configured category wins over a filter on a shorter prefix. An
    // operator troubleshooting "why isn't my bot receiving messages" reaching for
    // Logging:LogLevel:System.Net.Http.HttpClient.telegram.LogicalHandler is exactly the kind of
    // configuration a filter-only fix cannot survive - the token must stay out of the log even
    // when that category is explicitly turned back on.
    [Fact]
    public async Task No_captured_log_line_contains_the_token_even_when_configuration_reenables_the_nested_logging_category()
    {
        const string token = "123456:AAProbeTopSecretBotToken";
        var lines = new ConcurrentQueue<string>();

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:Mode", "Off");
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
            builder.UseSetting("Logging:LogLevel:System.Net.Http.HttpClient.telegram.LogicalHandler", "Information");
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(lines)));
            builder.ConfigureTestServices(services =>
                services.AddHttpClient("telegram").ConfigurePrimaryHttpMessageHandler(() => new StubHandler()));
        });

        var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("telegram");
        await client.GetAsync($"http://example.invalid/bot{token}/getMe", TestContext.Current.CancellationToken);

        lines.Should().NotContain(line => line.Contains(token),
            "a more specific configured category must not be able to re-enable the request-URI logger");
    }

    sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    sealed class CapturingLoggerProvider(ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(lines);
        public void Dispose() { }

        sealed class CapturingLogger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue(formatter(state, exception));
        }
    }
}
```

- [ ] **Step 36: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: FAILS. The stub handler means no real socket is ever opened (no network dependency in this test), but `IHttpClientFactory`'s default logging handler still logs the request URI — token included — at Information level before the request is even sent, and `CapturingLoggerProvider.IsEnabled` accepts everything.

- [ ] **Step 37: Remove the logging handlers from the named client's pipeline**

In `src/Noof.Ledger.Host/Program.cs`, replace the line added in Step 34:

```csharp
// A log-level filter is a suppression a more specific configured category can override at
// runtime - Logging:LogLevel:System.Net.Http.HttpClient.telegram.LogicalHandler beats a filter on
// the shorter prefix and puts the full request URI, bot token included, at Information. Removing
// the logging handlers from the pipeline instead means there is nothing left to re-enable.
builder.Services.AddHttpClient("telegram").RemoveAllLoggers();
```

> **Plan defect, found by a later security review and fixed above.** The version originally drafted here was `builder.Logging.AddFilter("System.Net.Http.HttpClient.telegram", LogLevel.None)`. A filter is matched by category prefix, so it silenced both `System.Net.Http.HttpClient.telegram.LogicalHandler` and `...ClientHandler` — but a reviewer added a **more specific** category through configuration, `Logging:LogLevel:System.Net.Http.HttpClient.telegram.LogicalHandler`, and the full request URL — including the live bot token — appeared at Information level: `Start processing HTTP request GET http://.../bot123456:AAProbeToken.../getMe`. That is an ordinary troubleshooting step for an operator asking "why isn't my bot receiving messages", not an edge case. `IHttpClientBuilder.RemoveAllLoggers()` (confirmed present on this target framework, in `Microsoft.Extensions.Http` — the `"telegram"` client is already registered through `IHttpClientFactory` via `AddHttpClient`, which is exactly what the extension targets) takes the logging handlers out of the pipeline entirely instead of silencing them, so there is nothing left for configuration to re-enable. The failing test added to Step 35 above (`No_captured_log_line_contains_the_token_even_when_configuration_reenables_the_nested_logging_category`) is the reviewer's defeat case, made permanent.

- [ ] **Step 38: Run it green**

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: all green, including both `TelegramHttpClientLoggingTests`.

> **Side effect worth knowing about, not fixing here.** Every existing `Noof.Ledger.Host.Tests` test that boots a `WebApplicationFactory<Program>` (all of `BootTests.cs`, `LoginEndpointTests.cs`, ...) now also starts `TelegramPollingService` in the background, which calls `ISecretStore.GetAsync` against whatever connection string that test configured — including the deliberately-unreachable one in `BootTests`. `ISecretStore.GetAsync` is contractually "never throws" (see its doc comment in the locked ports), so this should degrade to `Missing`/`Unreadable` and return `Idle` rather than blow up — but if that guarantee is ever violated by the real `EfSecretStore`, it will surface here first, as a flaky or slow `Host.Tests` run, not as an obvious Telegram bug.

---

#### Stage 8 — verify and ship

- [ ] **Step 39: Run the whole solution**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: all green, including `Noof.Ledger.Telegram.Tests`, the two new `Noof.Ledger.Host.Tests` and `Noof.Ledger.Architecture.Tests` additions, and everything from earlier tasks.

- [ ] **Step 40: Manual acceptance check (not automated — needs a real Telegram bot and a real network)**

This exercises the one thing the unit tests structurally cannot: real long polling against `api.telegram.org`.

1. Create a bot with `@BotFather`, get its token.
2. Boot the Host normally (`dotnet run --project src/Noof.Ledger.Host`). Confirm nothing polls yet — no token is saved.
3. Save the token under `SecretKeys.TelegramBotToken` by whatever mechanism this phase's Secrets task shipped (its own CLI verb, or a `/settings/secrets` page if that has landed by now). Within ~5 seconds (the idle poll interval), the bot should call `deleteWebhook` and start `getUpdates` — check the Telegram Bot API's `getWebhookInfo` shows an empty `url`, or just watch for the next step to work.
4. Message the bot from your own Telegram account. It should reply immediately with the saved-not-yet-categorised acknowledgement, and that reply's message id should be attached to the transaction (verify via whatever means Task 8's own transaction row is inspectable — a DB query is fine here).
5. Message the bot from a **second** Telegram account (or ask someone else to). Confirm: no reply, nothing written anywhere.
6. While the bot is mid-poll, disconnect the machine's network for ~30 seconds, then reconnect. Confirm the process does not crash or exit, and that a message sent from your account **after** reconnecting is still captured and replied to.
7. Rotate the bot token in `@BotFather`, save the new token. Confirm the bot keeps responding (it rebuilds its client and re-runs `deleteWebhook` against the new token).

Nothing in this step edits the acknowledgement message with a real amount — that requires the categorisation worker, which is a later task.

- [ ] **Step 41: Commit**

```bash
git add tests/Noof.Ledger.Telegram.Tests src/Noof.Ledger.Telegram src/Noof.Ledger.Application/Chat src/Noof.Ledger.Application/Secrets/ISecretStore.cs src/Noof.Ledger.Persistence/Secrets/EfSecretStore.cs tests/Noof.Ledger.Persistence.Tests/EfSecretStoreTests.cs tests/Noof.Ledger.Host.Tests/TelegramHttpClientLoggingTests.cs src/Noof.Ledger.Host/Program.cs Directory.Packages.props NoofLedger.slnx tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs
git commit -m "$(cat <<'EOF'
feat(telegram): long-polling bot with an owner allowlist and a durable offset

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

---

### Task 9: Stop the connection string leaking parameter values into exception text

**Files:**
- Modify: `src/Noof.Ledger.Persistence/LedgerConnectionString.cs`
- Modify: `tests/Noof.Ledger.Persistence.Tests/LedgerConnectionStringTests.cs`
- Modify: `ops/reset-database-auth.ps1:76`

**Interfaces:**
- Consumes: nothing new.
- Produces: no signature change. `LedgerConnectionString.Resolve` keeps its exact shape — callers are untouched.

> ### Why this is in Phase 1A and not the hardening phase
>
> The PostgreSQL credential itself is deferred by explicit instruction (`docs/OPEN-QUESTIONS.md`, P1-2), and this task does not touch how the password is stored. It removes a *different* setting that rides in the same file.
>
> `ops/reset-database-auth.ps1:76` writes `Include Error Detail=true` into `%LOCALAPPDATA%\NoofLedger\db.connection`, and `LedgerConnectionString.Resolve` hands that whole string to `UseNpgsql`. With it on, Npgsql puts **parameter values into exception messages**. Phase 1A is the phase where ciphertext, bot tokens and a person's spending text start flowing through EF, and CLAUDE.md is absolute that a secret must never reach an exception message.
>
> It stays on for the admin connection string the tests use: there it is a genuine debugging aid against throwaway databases holding synthetic data, and `DatabaseSettings.AdminConnectionString` reads the file directly without going through `Resolve`. Strip it on the application path only.

- [ ] **Step 1: Write the failing test**

Add to `tests/Noof.Ledger.Persistence.Tests/LedgerConnectionStringTests.cs`:

```csharp
    [Fact]
    public void Error_detail_is_stripped_so_parameter_values_cannot_reach_an_exception_message()
    {
        var resolved = LedgerConnectionString.Resolve(
            "Host=example;Database=noof_ledger;Include Error Detail=true");

        resolved.Should().NotContain("Include Error Detail",
            "Npgsql puts parameter values into exception text when this is on, and Phase 1 flows secrets through EF");
    }

    [Fact]
    public void A_connection_string_without_error_detail_is_returned_unchanged()
    {
        const string plain = "Host=example;Database=noof_ledger;Username=someone";

        LedgerConnectionString.Resolve(plain).Should().Be(plain);
    }
```

> The second test stops the obvious over-correction. A `Replace` that rewrites the string unconditionally, or one that leaves a stray `;;` behind, would pass the first test alone. Npgsql tolerates `;;`, so nothing downstream would complain and the damage would only show up in a log somewhere much later.

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`

Expected: `Error_detail_is_stripped_so_parameter_values_cannot_reach_an_exception_message` FAILS — the resolved string still contains `Include Error Detail`, because `Resolve` returns a configuration value verbatim today (`LedgerConnectionString.cs:14-15`).

- [ ] **Step 3: Strip it in `Resolve`**

Parse and rebuild rather than doing string surgery — `NpgsqlConnectionStringBuilder` already handles the key's casing, spacing and the trailing-semicolon problem, and `Npgsql` is already a package reference of this project.

In `src/Noof.Ledger.Persistence/LedgerConnectionString.cs`, add the `using` and a helper, and route every `return` through it:

```csharp
using Npgsql;

namespace Noof.Ledger.Persistence;

public static class LedgerConnectionString
{
    public const string DefaultDatabase = "noof_ledger";

    public static string CredentialFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NoofLedger",
        "db.connection");

    public static string Resolve(string? fromConfiguration, string database = DefaultDatabase)
    {
        if (!string.IsNullOrWhiteSpace(fromConfiguration))
            return WithoutErrorDetail(fromConfiguration);

        var fromEnvironment = Environment.GetEnvironmentVariable("NOOF_TEST_PG");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return WithoutErrorDetail(ForDatabase(fromEnvironment, database));

        if (File.Exists(CredentialFile))
        {
            var fromFile = File.ReadAllText(CredentialFile).Trim();
            if (!string.IsNullOrWhiteSpace(fromFile))
                return WithoutErrorDetail(ForDatabase(fromFile, database));
        }

        throw new InvalidOperationException(
            $"No PostgreSQL connection string. Set ConnectionStrings:Ledger, or NOOF_TEST_PG, or run " +
            $"ops/reset-database-auth.ps1 to create {CredentialFile}.");
    }

    // Npgsql writes parameter VALUES into exception messages when this is on. The ops script puts it
    // in the credential file, which is useful against throwaway test databases and unacceptable on the
    // application path, where those parameters are ciphertext and a person's spending.
    static string WithoutErrorDetail(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!builder.IncludeErrorDetail)
            return connectionString;

        builder.IncludeErrorDetail = false;
        builder.Remove("Include Error Detail");
        return builder.ConnectionString;
    }

    static string ForDatabase(string connectionString, string database) =>
        connectionString.Replace("Database=postgres", $"Database={database}", StringComparison.Ordinal);
}
```

> `builder.IncludeErrorDetail = false` sets the property to its default, and a builder does not emit defaults — but `Remove` is kept because assigning the default does not always clear a key that was explicitly present. Checking `IncludeErrorDetail` first is what makes the untouched-string test pass: a string that never had the key is returned byte-for-byte, not round-tripped through the builder, which would otherwise reorder and re-case every other key.

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`

Expected: PASS, including the three pre-existing `LedgerConnectionStringTests` cases. `The_resolved_string_names_the_ledger_database_not_postgres` reads the real credential file on this machine, so it exercises the new path against a string that genuinely contains the key.

- [ ] **Step 5: Stop the ops script writing it in the first place**

New installs should not carry the setting at all. In `ops/reset-database-auth.ps1`, line 76, change:

```powershell
$connection = "Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=$password;Include Error Detail=true"
```

to:

```powershell
$connection = "Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=$password"
```

> Do **not** edit the existing `%LOCALAPPDATA%\NoofLedger\db.connection` on this machine as part of this task. `Resolve` now strips the setting whatever the file says, so the application is safe either way, and rewriting a live credential file is an unrelated risk to take inside a code change. The file corrects itself the next time the script is run.

- [ ] **Step 6: Run the full suite and commit**

Run: `dotnet test --solution NoofLedger.slnx`

Expected: green, count grown by 2.

```bash
git add src/Noof.Ledger.Persistence/LedgerConnectionString.cs tests/Noof.Ledger.Persistence.Tests/LedgerConnectionStringTests.cs ops/reset-database-auth.ps1
git commit -m "fix(persistence): keep parameter values out of Npgsql exception text"
```
