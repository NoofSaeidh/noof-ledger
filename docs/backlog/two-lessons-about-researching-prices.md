---
title: Two lessons about researching prices, kept deliberately
status: done
area: docs
---
**Vendor marketing pages frequently do not render prices to a fetch.** Azure's shows `$-` thirty-six
times; Hetzner's homepage widget shows none at all; Oracle's spec table is injected by JavaScript. This
is exactly how wrong numbers get quoted from memory and then cited to a page that does not contain them.
Azure's real prices are in the **retail prices API** (`prices.azure.com/api/retail/prices`) as plain JSON.

**Check whether a free tier is permanent or a 12-month trial.** That distinction flipped one
recommendation entirely, and it is invisible in every comparison article.
