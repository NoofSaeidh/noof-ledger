---
title: 2. A tunnel for phone access — free, and it changes the hosting maths
status: deferred
area: hosting
related: [useforwardedheaders-missing-cookiesecurepolicy]
---
**Cloudflare Tunnel or Tailscale.** The dashboard becomes reachable from a phone with the application
staying at home and **no inbound port open**. This is the finding that removed hosting's main advantage,
and it is worth remembering before anyone re-opens that argument: remote access does not require moving
the application.

Note `UseForwardedHeaders` (recorded separately above) becomes load-bearing the moment a tunnel
terminates TLS in front of the app.
