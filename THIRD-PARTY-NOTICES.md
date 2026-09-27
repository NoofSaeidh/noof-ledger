# Third-party notices

This repository has no licence of its own (see `README.md`). The third-party work below keeps its own
licence.

## Code derived from other projects

### serbian-fiscal-receipts-parser

- Source: https://github.com/turanjanin/serbian-fiscal-receipts-parser
- Used in: `src/Noof.Ledger.Receipts/FiscalQr/FiscalQrDecoder.cs`, whose byte layout of the fiscal QR
  `vl` payload is ported from it, and `src/Noof.Ledger.Receipts/Journal/FiscalJournalParser.cs`, whose
  section shape was verified against its `Parser.php`.
- Licence: MIT

```
The MIT License (MIT)

Copyright (c) Jovan Turanjanin

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

## NuGet packages

Packages added from Phase 6 on are listed here (`.claude/rules/dependencies.md`). Older ones are listed
only in `Directory.Packages.props`. All of them come in through NuGet. None of their code is in this
repository, and their licences ask for nothing here. **A build that is handed to someone else** — the
output of `ops/publish.ps1` — contains the app's package binaries. Anyone giving one away must include:

- the licence text of every package in that build: the app's rows below, and the older ones in
  `Directory.Packages.props`;
- every notices file a row names. Today that is the `THIRD-PARTY-NOTICES.txt` inside
  `SkiaSharp.NativeAssets.*`, which covers Skia and the libraries it bundles.

| Package | Version | Licence | Used in | Project |
|---|---|---|---|---|
| ZXing.Net | 0.16.11 | Apache-2.0 | app (Receipts) | https://github.com/micjahn/ZXing.Net |
| ZXing.Net.Bindings.SkiaSharp | 0.16.24 | Apache-2.0 | app (Receipts), Receipts tests | https://github.com/micjahn/ZXing.Net |
| SkiaSharp (and `SkiaSharp.NativeAssets.*`, which ship their own `THIRD-PARTY-NOTICES.txt`) | 4.151.1 | MIT | app (Receipts, via the binding) | https://github.com/mono/SkiaSharp |
| Npgsql | 10.0.3 | PostgreSQL | tests (TestKit, as a direct reference); the app has had it since Phase 0 through `Npgsql.EntityFrameworkCore.PostgreSQL` | https://github.com/npgsql/npgsql |
