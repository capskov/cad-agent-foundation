# 构建产物与源码对应关系（ACAD2022 V1.1 第二轮）

生成时间：2026-11-16
源码根（本机）：`F:\AI\cad-agent-foundation-acad2022\src\cad-agent-foundation-main`
快照目录：`source-snapshot/`（本文件同级，276 个文件，约 2.21 MB，已排除 bin/obj/运行时包/测试工作区）

## 1. 三个运行产物分别由哪些源码构建

| 产物 | 构建入口 | 实际编译的源码 | 目标框架 | 说明 |
| --- | --- | --- | --- | --- |
| `client/foundation.py` | 无需编译 | `client/foundation.py`、`client/plot_style.py` | Python 3 | 命令行客户端；保存身份修复在这里 |
| `plugin/Bimwright.Dwg.Plugin.Acad22.dll` | `third_party/bimwright-dwg-mcp/fork/src/plugin-acad22/Bimwright.Dwg.Plugin.Acad22.csproj` | `plugin-acad24/App.cs` + `plugin-acad26/*.cs` + `plugin-journal-net8/*.cs` + `shared/**/*.cs` | net48 / AutoCAD 2022 R24.1 | 所有 AutoCAD 原生写入、读回、plot_pdf、dimension_style 都在这里 |
| `server/Bimwright.Dwg.Server.exe` / `.dll` | `third_party/bimwright-dwg-mcp/fork/src/server/Bimwright.Dwg.Server.csproj` | `server/**/*.cs` + `shared/Execution/FoundationDrawingPlan.cs` + `FoundationPlanExtensions.cs` + 其他 `shared` 子集 | net8.0 | 计划字段校验、MCP 服务、journal 记录；**它也有独立校验器** |

关键结论：`FoundationPlanExtensions.cs` 与 `FoundationDrawingPlan.cs` 同时被 acad22 插件与 net8 服务端编译。只重编插件、不重编服务端，带新字段的计划仍会在服务端被 `unexpected or missing plan fields` 拒绝（V1.1 BUG-03 的原样重演，fix10 即此状态）。

## 2. 本机实际构建命令（fix13）

```powershell
# 插件（net48，需要 AutoCAD 2022 的 accoremgd/acdbmgd/acmgd）
Push-Location F:\AI\cad-agent-foundation-acad2022\src\cad-agent-foundation-main\third_party\bimwright-dwg-mcp\fork\src
dotnet build plugin-acad22\Bimwright.Dwg.Plugin.Acad22.csproj -c Release `
  -o F:\AI\cad-agent-foundation-acad2022\cad2022-test\build-fix13 --nologo
Pop-Location

# 服务端（net8）
Push-Location F:\AI\cad-agent-foundation-acad2022\src\cad-agent-foundation-main\third_party\bimwright-dwg-mcp\fork\src
dotnet build server\Bimwright.Dwg.Server.csproj -c Release `
  -o F:\AI\cad-agent-foundation-acad2022\cad2022-test\build-server-fix13 --nologo
Pop-Location

# 打包（复制 fix12 基底，替换插件 + 服务端，重算 manifest）
python F:\AI\cad-agent-foundation-acad2022\cad2022-test\v11_make_fix13_package.py
```

## 3. fix13 产物指纹

| 项 | 值 |
| --- | --- |
| 包目录 | `artifacts/packages/20261116-fix13` |
| 插件 SHA256 | `77FC7C3EFA6079C63D1F87FCF725A9D37A89AB2E8B7951FA725347CF5D46E346` |
| 插件 bundle SHA256 | `BA928511F56684C5F83223EE6202730DCBCAB68FE276488C035480C7A3F9C024` |
| 插件 MVID | `784f7287-08c2-4a0e-aa9b-e4192db2f327` |
| 服务端 exe SHA256 | `DB84EF21104B8367B8D79CE73FB26BBB2041FE7BF2957DC77FEC6D31529585B9` |
| 服务端 dll SHA256（fix13） | `23FC6BB48A6E1D465EFA17D19D052ED347ABF20EB058AD14C98D3520343FD5A5` |

注意：服务端 `.exe` 是启动器，实际校验逻辑在 `Bimwright.Dwg.Server.dll`；审阅时应以 dll 的 SHA256 与 `manifest.json` 对照，不要只看 exe。

## 4. 源码改动与本轮问题的对应

| 问题 | 源码位置 | fix 包 |
| --- | --- | --- |
| 保存成功却报 `Unknown request record` | `client/foundation.py` → `apply_with_optional_save()` 的 `write_id` | fix1 |
| 扩展字段被拒（服务端旧校验器） | `server/Bimwright.Dwg.Server.csproj` 重新编译 | fix11 |
| 尺寸样式事实缺 `linear_scale_factor` | `plugin-acad26/FoundationExtendedOperations.cs` → `SymbolFacts()` | fix12 |
| 已存在尺寸样式修改被判失败 | `shared/Execution/FoundationPlanExtensions.cs` → `VerifySymbols` + `VerifyDimensionStyleChange` | fix13 |
| `Dimlfac` 写入 | `plugin-acad26/FoundationExtendedOperations.cs` → `dimension_style` 分支 | fix10/fix12/fix13 |
| page_setup 单位 | `shared/Execution/FoundationDrawingPlan.cs`、`plugin-acad26/FoundationPrinting.cs`、`FoundationPlanWriteHandler.cs` | fix9 |

## 5. 未包含在快照中的内容

- `artifacts/packages/*`：运行时包，体积大，按 fix 号单独索引；fix13 关键指纹见上表。
- `cad2022-test/*`：测试工作区、DWG、PDF、请求记录；作为 `evidence/` 单独提供。
- `bin/`、`obj/`：编译中间产物，不进入源码审查。
- 生产 AutoCAD 实例与用户原始工程图：不在本分支，不应被审查者下载或写入。
