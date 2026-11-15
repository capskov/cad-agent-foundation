# v1.2.0-beta.3.18 — field reliability and explicit PDF printing

This prerelease continues the published beta.3 line. It adds explicit transient window PDF printing and local CTB configuration, with field fixes verified during a private engineering task. Drawings, coordinates, private journals and signing keys are excluded from publication.

## Changes

- `foundation.cmd plot-style` reads/configures compressed CTB v2. All 255 ACI colors can be set to black with explicit widths; unrelated widths remain unchanged. Original files and request/readback records are retained. This is a client file operation, not a DWG/MCP transaction.
- Native `plot_pdf` optionally accepts complete `window_settings`: installed PDF PC3, media, style, rotation, DCS bounds, fixed scale and centering. Plot styles and lineweights are enabled without scaling lineweights. Transient PlotInfo preserves layout settings. Existing PDFs are never overwritten.
- Field fixes preserve associated leader/text relationships during reads, recognize overall paper-space viewports, verify controlled frame copies and distinguish narrowly identified derived display-cache changes. Rectangular wipeouts and viewport dimensions support explicit Agent decisions.
- Save/text/font/xref/block-cache recovery remains tied to independent evidence for the original request. Verification failure is not rollback; no force-unlock or uncertain-write replay was introduced.
- Maintainer binaries use a capskov local self-signed certificate: CurrentUser trust, not public-CA certification. Other machines require their own publisher trust decision.

Usage: [CTB and window PDF](PRINT-STYLES-AND-WINDOW-PDF.zh-CN.md), [save/font recovery](SAVE-FONT-20261028.zh-CN.md), [units and typography](DRAWING-UNITS-AND-TYPOGRAPHY.zh-CN.md).

## Validation and limits

Server build: no warnings. AutoCAD2026 plugin build: three existing warnings (Roslyn conflicts and obsolete WebClient). 144 extension contract checks and 14 Python client tests passed. Earlier field evidence covers native create/edit/save with released fences. The new signed plugin loaded in an owned session; 13 explicit A3 windows printed with passed verification and released fences. All 13 PDFs were rendered and individually reviewed. An earlier batch lacked its frame xref and failed visual acceptance; after preparing the dependency before opening, xref status was Resolved and the entire batch was repeated. Output receipts alone were not treated as visual acceptance.

Independent CTB parsing confirmed that eight entries previously inherited object color and yellow/green were 0.18mm. After configuration all 255 resolve to RGB black, ACI2/3 are 0.15mm, and other widths remain unchanged. Original CTB and non-final outputs remain in private evidence.

The owner explicitly accepted recording professional proxy/legacy overlaps for manual adjustment in the authoring software. Overlaps and dense/solid fills remain in the delivery exception list. This is scoped operation acceptance, not certification of defect-free engineering drawings or universal proxy compatibility. Original SHX fonts are not all restored. Model-space navigation/capture does not establish paper-space support; explicit window PDFs support this workflow. No physical-printer support or aesthetic/layout engine was added.

Requires Windows x64, licensed compatible AutoCAD2026/.NET8, .NET8 Desktop Runtime and Python3.11+. No Autodesk binaries, SHX, engineering inputs or private keys are distributed. Older tags/assets remain unchanged. MCP Registry metadata is a separate target and is not changed by this GitHub prerelease.
