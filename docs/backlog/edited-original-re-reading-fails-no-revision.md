---
title: An edited original whose re-reading fails leaves no revision of the new text
status: deferred
area: telegram
---
`ReplaceRawTextAsync` changes `raw_text` and queues a re-reading; the Edit revision is written when that
reading is applied. If it fails, the new text lives only on the transaction row. The previous state is
still in the history.
