---
id: B3
title: Should the loopback guard also reject a non-loopback *configured* URL pre-bind, as an early check?
status: decided
phase: Phase 0b, task 9
---

**Default taken:** No — the post-bind `IServerAddressesFeature` check is authoritative.
Re-deriving Kestrel's URL precedence by hand is a bug source.

**Decide by:** Phase 0b, task 9.
