---
title: Pressing Изменить twice forgets the first prompt
status: deferred
area: telegram
---
`transactions.prompt_message_id` holds one prompt. A reply to an older, superseded prompt is not
recognised and is captured as a new message. Rare, visible in the chat, and fixed by a small table of
prompts if it ever matters.
