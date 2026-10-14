# Source-preview verification

Exported source builds: server passed with zero warnings; AutoCAD 2026 native plugin and journal passed with three known inherited warnings (two Roslyn reference conflicts, one WebClient deprecation). Target net8.0 / net8.0-windows, local SDK 10.0.401. SDK build success does not certify the AutoCAD 2026.1.2 .NET 10 host.

Selected exported offline evidence: Python entry 5/5; FoundationLedgerContract 20/20 scenarios; PluginJobContract 67/67 scenarios. These check local contracts, not live CAD. Only exported-file build/contracts were rerun; no full native acceptance or case annotation workflow was repeated.

Public-source content and relative documentation links were checked; original source/Skill hashes and private Git state remain unchanged. No runtime credentials, original DWGs/native receipts, SHX binaries or build outputs are part of the source export. The case's previously reviewed display assets are reused unchanged.

Known limits and packaging gaps are in BUILD.md, COMPATIBILITY.md and PUBLICATION-SCOPE.md. Original local native workflow evidence is summarized separately in VALIDATION.md. No newly built binary is claimed installed or accepted.
