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

## Packages for reading receipt photos

These packages are referenced through NuGet. None of their code is in this repository, and their
licences ask for nothing here. **A build that is handed to someone else** — the output of
`ops/publish.ps1` — contains their binaries. Anyone giving one away must include:

- the Apache License 2.0 text for ZXing.Net;
- the MIT text for SkiaSharp;
- the `THIRD-PARTY-NOTICES.txt` shipped inside `SkiaSharp.NativeAssets.*`, which covers Skia and the
  libraries it bundles.

| Package | Version | Licence | Project |
|---|---|---|---|
| ZXing.Net | 0.16.11 | Apache-2.0 | https://github.com/micjahn/ZXing.Net |
| ZXing.Net.Bindings.SkiaSharp | 0.16.24 | Apache-2.0 | https://github.com/micjahn/ZXing.Net |
| SkiaSharp (and `SkiaSharp.NativeAssets.*`) | 4.151.1 | MIT | https://github.com/mono/SkiaSharp |

The other NuGet dependencies are listed in `Directory.Packages.props`.
