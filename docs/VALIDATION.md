# Local validation summary

This is a sanitized summary of existing evidence, not a new test run. No customer drawing, source coordinate set, machine path, token, PID or journal is included here.

Two different source drawings were copied into owned workspaces. Each workflow created ten new model objects: line, closed polyline, hatch, single-line text, multiline text, circle and the four-member linked callout. The hatch patterns differed. Each workflow independently read the objects, changed linked text using old-value protection, moved the full group, archived it, saved and closed the drawing. Independent fresh CAD sessions then read the saved objects with matching native digests.

The CLI and MCP wrapper were exercised, including identity/module checks, the restricted eight-tool catalog, original-request queries after service restarts, and repeated-plan queries returning the original handles. The local ZIP was extracted; its hashes and command installation entry were checked. Source drawing hashes remained unchanged.

Focused offline checks: 19 Foundation plan/catalog/readback-tampering cases and 5 CLI protection cases passed in the recorded run. Earlier overlapping shared-contract runs are not added to these counts. Offline checks do not replace the native and disk workflows above.

A historical save returned successfully but its following hash read was blocked by an inappropriate file-sharing mode. The new save path corrected the sharing mode and passed on a fresh copy. The original request remains committed with unknown verification and a retained fence; it was not replayed or retrospectively reclassified as passed. This illustrates the separation between effects and verification rather than universal recovery.

This evidence does not establish total geometric preservation of every original object, all drawings/fonts/proxies, fault-matrix completeness, arbitrary client/platform support, or comparative speed/cost savings. Public packaging changes need new artifact/hash checks; behavioural/runtime/font changes need only the checks directly affected by those changes, not another unlimited verification campaign.


## v1.1.0-rc.1 scoped native evidence

See [the new acceptance summary](VALIDATION-v1.1.0-rc.1.md). The prior evidence above remains historical. Native DIMENSION, named style creation/reuse, layer states, closed-surface/callout continuation and same-drawing transforms are new scoped operations, not proof of every parameter/entity combination.
