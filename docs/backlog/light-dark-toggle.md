---
title: A light/dark toggle — deferred 2026-09-22
status: deferred
area: web
since: 2026-09-22
related: [mudblazor-features-needing-render-mode-decision]
---
The theme is fixed dark, set as a parameter on `MudThemeProvider`. A toggle needs interactivity plus
somewhere to persist the choice, and the layout is static (see above). Switching the whole app to
light is a one-line change to `NoofTheme`; offering the user the choice is not.
