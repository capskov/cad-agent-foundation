# 源码快照说明（ACAD2022 V1.1 第二轮）

本目录是 2026-11-16 第二轮收敛时，本机 `F:\AI\cad-agent-foundation-acad2022\src\cad-agent-foundation-main` 的真实源码快照，不是旧 `main` 分支代码，也不是运行时压缩包。

## 包含内容

- `client/foundation.py`、`client/plot_style.py`
- `third_party/bimwright-dwg-mcp/fork/src/shared/`
- `third_party/bimwright-dwg-mcp/fork/src/plugin-acad22/`
- `third_party/bimwright-dwg-mcp/fork/src/plugin-acad24/`（acad22 复用其 `App.cs`）
- `third_party/bimwright-dwg-mcp/fork/src/plugin-acad26/`
- `third_party/bimwright-dwg-mcp/fork/src/plugin-journal-net8/`
- `third_party/bimwright-dwg-mcp/fork/src/server/`
- `tools/assembly-info/`

## 不包含

- `bin/`、`obj/`
- `artifacts/packages/`（运行时包，fix13 指纹见 `../BUILD-MAPPING.md`）
- `cad2022-test/` 测试工作区、DWG、PDF、请求记录（作为 `../evidence/` 单独提供）
- 生产 AutoCAD 实例、用户原始工程图

## 完整性校验

- `../MANIFEST.json` 列出每个文件的相对路径、字节数、SHA256；`file_count` 与 `total_bytes` 可用于核对。
- 关键文件哈希：
  - `client/foundation.py`：`B99B5473E7A0E6B3F516F9AA1DFF65C3C59A6ECBCA1CCB1AE746C582084790DB`
  - `shared/Execution/FoundationPlanExtensions.cs`：`A129445AFDF86959DFEB6A4C6AB385559F9EEE764E3F139A4734DDFBC32F0936`
  - `plugin-acad26/FoundationExtendedOperations.cs`：`56F7A616B4034967201EEBEF28B2004EEFE6952C499748A688CF81053C7D6F62`
  - `shared/Execution/FoundationDrawingPlan.cs`：`7C6EF1690151841E51BB9EEDF7F23FDDD5218739C79D17527325124DFEA1140B`
  - `plugin-acad26/FoundationPlanWriteHandler.cs`：`14F28C0FA016232B938AF25325FD1811A5FD39E8A773FBDDFDD65FE377C06308`

## 审查重点

1. `client/foundation.py` 的 `apply_with_optional_save()`：`write_id` 是否在所有 finalize/query 路径上保持同一请求身份。
2. `shared/Execution/FoundationPlanExtensions.cs`：
   - `dimension_style` 的 `linear_scale_factor` 可选字段；
   - `VerifySymbols` 对已存在尺寸样式修改的豁免（`VerifyDimensionStyleChange`）。
3. `plugin-acad26/FoundationExtendedOperations.cs`：`Dimlfac` 写入与 `SymbolFacts()` 中 `linear_scale_factor` 读回。
4. `server/Bimwright.Dwg.Server.csproj`：确认服务端确实编译了同一份 `FoundationPlanExtensions.cs`；这是“只重编插件仍被拒”的根因。
5. `plugin-acad26/FoundationPrinting.cs` 与 `FoundationPlanWriteHandler.cs`：`INSUNITS` 守卫与 page_setup 后的 `INSUNITS=6`。

## 边界

- 本快照对应 fix13 的运行时代码；fix13 包指纹见 `../BUILD-MAPPING.md`。
- 快照不证明“保存链路完整验收”：反向回归仍未执行。
- 快照不证明“plot_pdf 链路 passed”：PDF 已产出，但服务端最终判定仍 failed。
