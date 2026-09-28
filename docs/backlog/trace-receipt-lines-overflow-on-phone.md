---
title: Receipt lines table overflows the trace page on a phone
status: deferred
area: web
since: 2026-09-28
---
Seen in `docs/screenshots/app/trace-receipt-phone.png`, taken at 390 px wide. The six-column receipt
lines table on `TransactionTrace.razor` (#, Name, Quantity, Unit price, Total, Category) is wider
than the screen. The Category column is cut off ("Groce…", "Persor…") and the page does not scroll
sideways to reach it. The "Line items" row above it already shows each line's category, so nothing is
lost, only harder to read. Possible fixes: let the table scroll horizontally inside its own box, or
drop the Category and Unit price columns below a breakpoint.
