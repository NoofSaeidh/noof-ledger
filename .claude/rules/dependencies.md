---
paths:
  - "Directory.Packages.props"
  - "**/*.csproj"
  - ".config/dotnet-tools.json"
  - "THIRD-PARTY-NOTICES.md"
---

## Dependencies and third-party notices

- **A new NuGet package, or code ported from another project, updates `THIRD-PARTY-NOTICES.md` in the
  same commit.** This covers a `PackageVersion` added to `Directory.Packages.props`, a
  `PackageReference` to a package no project used before, and a tool in `.config/dotnet-tools.json`.
  Add a row to the packages table with the package, version, licence, project URL and where it is used:
  the app (`src/**`), tests only, or tooling only. A version bump updates its row.
- **Read the licence from the package itself, never from memory.** Use the `license` element of
  `%USERPROFILE%\.nuget\packages\<id>\<version>\<id>.nuspec`, or its `licenseUrl` when there is no SPDX
  expression. Check the native-asset and transitive packages it pulls in too. `SkiaSharp.NativeAssets.*`
  ships its own `THIRD-PARTY-NOTICES.txt`, and a package can carry `LICENSE`/`NOTICE` files; name them
  in the row's notes.
- **Ported code gets the full licence text**, with its copyright line copied from the source repository,
  in the file's "Code derived from other projects" section. The ported file's header comment names the
  source and points at `THIRD-PARTY-NOTICES.md`.
- **Stop and ask the operator before adding** a package whose licence is copyleft (GPL, AGPL, LGPL,
  MPL), source-available, dual or commercial, or has revenue or usage thresholds (Six Labors Split
  Licence, BSL, SSPL), or is missing. This repo deliberately has no licence of its own (`README.md`),
  so "free for OSI-approved open source" cannot be claimed on its behalf — `docs/decisions/p6-1-receipts-decisions.md`
  records how that ruled out `ZXing.Net.Bindings.ImageSharp` in Phase 6.
- **Removing a package removes its row.**
