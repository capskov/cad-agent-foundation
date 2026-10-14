# Build the source preview

[中文](BUILD.zh-CN.md) · [Quick start](QUICKSTART.md)

No runnable binary asset accompanies v1.0.0-rc.1. Do not run the raw client as an installed package: its entry requires a hash/MVID manifest. Windows x64, Python 3.11+, .NET 8 SDK and a legitimate supported AutoCAD 2026 installation are prerequisites. The recorded .NET 8 host baseline is R25.1.74.0.0; the .NET 10 transition in 2026.1.2 is not certified here. Check COMPATIBILITY.md before building or loading.

From this repository root:

```powershell
dotnet build third_party/bimwright-dwg-mcp/fork/src/server/Bimwright.Dwg.Server.csproj -c Release
dotnet build third_party/bimwright-dwg-mcp/fork/src/plugin-acad26/Bimwright.Dwg.Plugin.Acad26.csproj -c Release -p:AutoCad2026Dir="C:\Program Files\Autodesk\AutoCAD 2026"
python -m unittest discover -s tests
dotnet run --project third_party/bimwright-dwg-mcp/fork/tests/FoundationLedgerContract -c Release
dotnet run --project third_party/bimwright-dwg-mcp/fork/tests/PluginJobContract -c Release
```

The native project references installed Autodesk SDK/API assemblies with `Private=false`; do not copy them into a release. This source release retains selected offline tests, not all private historical native fixtures. Passing a build/offline contract is not CAD acceptance.

Build the server, plugin and tools/assembly-info, then run `python scripts/package-runtime.py --version 1.1.0-rc.1`. The builder selects Windows outputs, resolves dependency license texts, excludes Autodesk/SHX/PDB assets and creates file hashes/plugin MVID. Native acceptance remains separate; a build alone is a candidate. Use the reviewed Release runtime ZIP for normal installation, preserving older local packages.
