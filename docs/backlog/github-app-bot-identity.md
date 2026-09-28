---
title: Agents post to GitHub as their own bot (a self-owned GitHub App)
status: deferred
area: ops
since: 2026-09-28
---
**Wanted.** Agents open PRs, comment, reply to and resolve review threads and switch PRs between
draft and ready through `gh`, which is authenticated as the operator — so all of it appears as the
operator's own activity. Until this lands, CLAUDE.md requires a `🤖 Written by Claude Code (<model>)`
line on everything an agent writes. A bot identity (`noof-ledger-bot[bot]`) makes the distinction
structural, lets the operator *approve* agent PRs (GitHub never lets an author approve their own
PR), and makes GitHub notify the operator about agent activity, which it does not do for their own.

**Why a self-owned GitHub App.** Compared on 2026-09-28 against the alternatives:
- A **machine user** needs a second account and email, and its token would have to be a classic
  `public_repo` PAT — fine-grained PATs cannot write to a public repo the user merely collaborates
  on — which is broad and long-lived.
- The **official Claude GitHub App** (`/install-github-app`, `claude-code-action`) only acts inside
  GitHub Actions runs; Anthropic holds its key, so local CLI sessions cannot use it.
- A **self-owned app** is free, installed on this repository only, and hands out installation
  tokens that expire after an hour.

**Why it is not done yet.** Registering the app and generating its private key is a step in the
GitHub UI that only the operator can do.

**One-time setup (operator).**
1. GitHub → Settings → Developer settings → GitHub Apps → New GitHub App: name `noof-ledger-bot`,
   homepage = the repo URL, webhook **Active** unticked, "Only on this account".
2. Repository permissions: Pull requests — read & write; Issues — read & write; Checks, Commit
   statuses, Actions, Contents — read-only. Nothing else.
3. Create, note the **Client ID**, generate a private key (a PEM file — a secret, never in the repo).
4. Install App → only this repository.
5. Encrypt the key with DPAPI into `%LOCALAPPDATA%\NoofLedger\github-app\key.dpapi`
   (`Get-Content <pem> -Raw | ConvertTo-SecureString -AsPlainText -Force | ConvertFrom-SecureString`)
   and delete the PEM. Rotation: generate a new key, re-import, delete the old key on the app page.

**Integration (one PR, after setup).**
- `ops/gh-bot-token.mjs` — Node, no dependencies: reads the PEM on stdin, signs an RS256 JWT
  (`iat` 60 s in the past, `exp` under 10 minutes, `iss` = Client ID), exchanges it at
  `POST /app/installations/{id}/access_tokens` scoped to this repository, prints only the token.
- `ops/gh-bot.ps1 <gh args>` — decrypts the key, mints a token, sets `GH_TOKEN` for that one `gh`
  process only (never an argument, never printed, never a file).
- Verify on a real PR that `gh pr create`, `pr ready` / `--undo`, `pr comment`, review-thread
  replies and the GraphQL `resolveReviewThread` mutation all work with the installation token; the
  permission the GraphQL mutations need is not documented — "Pull requests: write" is expected.
- Pushes stay on the operator's git credentials (no Contents write needed), so commits keep their
  authorship.
- CLAUDE.md: every agent write to GitHub goes through `gh-bot.ps1`; the signature line rule is then
  dropped. A hook can refuse bare `gh pr create|comment|ready|review` and `gh api graphql` mutations.
- `ops/RUNBOOK.md`: key rotation.

**Open question to test at setup.** GitHub bills automatic Copilot reviews of a bot-authored PR to
"the organization"; this is a personal account and the behaviour is not documented. Copilot is
already optional here, and a review can still be requested under the operator's identity with
`gh pr edit <n> --add-reviewer "@copilot"`.
