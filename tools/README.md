# tools/ — build and regression gate

Everything here is part of the repository so a clean checkout can reproduce the
build and the hash regression that guards the ACAD2022 V1.1 port.

## Run

```powershell
# AutoCAD 2022 must be installed; point at it explicitly when it is not in the default place
pwsh -File tools\gate.ps1 -AutoCadDir "F:\Program Files\Autodesk\AutoCAD 2022"
```

The gate, in order:

1. builds `plugin-acad22` (net48, AutoCAD 2022 managed assemblies),
2. builds `server` (net8),
3. builds and runs `tools/hash-regress`, which compiles `shared/Execution` on net8 and
   checks the canonical JSON + SHA256 rules against values the net48 plugin really
   produced (`entity_digest`, `plan_sha256`, receipt `before` snapshot, number
   formatting, escaping, and a full byte-for-byte canonical text oracle),
4. prints the SHA256 of the plugin DLL, the server DLL and `client/foundation.py`,
   and writes them to `tools/last-gate-artefacts.json` (git-ignored) so a release
   record can quote them.

A non-zero exit means the gate failed; do not ship a build that has not passed it.

## Why this exists

net48 and net8 do not format every `double` the same way. The plugin signs a plan with
one text form and the server re-derives it with another, which silently broke
`expected_sha256` verification (D5). The regression pins the net48 rule:
`G15` when it round-trips, otherwise `G17`, plus `.0` when the text has neither a
decimal point nor an exponent. Keeping that rule under test is the only thing that
stops a net8-only change from drifting away from what the plugin actually signs.

## Layout

```
tools/gate.ps1                  build + regression gate
tools/hash-regress/*.csproj     net8 console project compiling shared/Execution
tools/hash-regress/Program.cs   the checks
tools/hash-regress/samples/     net48-produced fixtures (entity, plan, receipt, canonical text)
```

`bin/`, `obj/` and `tools/last-gate-artefacts.json` are not committed.
