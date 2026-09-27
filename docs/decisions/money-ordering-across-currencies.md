---
title: "`Money` ordering across currencies"
status: decided
---

**Answer:** Throws. Comparing 10 EUR to 10 USD has no correct answer. Callers sort mixed lists
explicitly: `OrderBy(m => m.Currency).ThenBy(m => m.Amount)`.
