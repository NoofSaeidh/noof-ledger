---
title: The dashboard's reading width — deferred 2026-09-22
status: deferred
area: web
since: 2026-09-22
---
`MudContainer MaxWidth="MaxWidth.Large"` caps the content at 1280px. On a 2552px monitor that leaves
wide empty margins, which looks under-filled for a dashboard; on a laptop it is exactly right. The
honest fix is not a bigger number but a layout that uses the extra width — three cards across
instead of two — and that is worth doing when there are more than two currencies to show.
