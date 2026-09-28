---
id: Q8
title: "OneDrive backup: dump only, or dump + Data Protection key ring?"
status: deferred
phase: Phase 10 (was 8)
---

**Default taken:** Dump only — a restore means re-entering two secrets.

**Decide by:** Phase 10 (was 8).

## Why the default is safe to defer

Phase 8 by your own instruction. The real fork is whether a restore should be complete (key ring in
the cloud) or safe (re-enter two secrets). A third option exists: `ProtectKeysWithCertificate` with
the PFX in a password manager.
