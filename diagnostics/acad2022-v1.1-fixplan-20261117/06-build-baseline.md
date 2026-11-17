# 06 构建基线核对（评审意见一：源码基线不一致 —— 属实，且比预想严重）

结论先说：**`_review-repo` 不是构建源，它里面的源码不能生成现在正在运行的 2022 插件。**
修复计划必须以构建树为准，否则会出现"代码改了、运行环境没变"。

## 一、三个"同一份"源码，实际是三份不同的东西

| 位置 | 有 .git | `plugin-acad22` csproj 大小 | 是否链接 acad26 / journal / shared | SQLite 等包引用 |
| --- | --- | --- | --- | --- |
| `src\cad-agent-foundation-main\third_party\...`（**实际构建树**） | 否 | 3614 B | 是（`..\plugin-acad26\*.cs`、`..\plugin-journal-net8\*.cs`、`..\shared\**\*.cs`） | 有 |
| `_review-repo\third_party\...`（评审镜像，即本仓库 HEAD） | 是 | **2257 B** | **否** | **无** |
| `_review-repo\diagnostics\archive\2026-11-16-acad2022-v1.1-wall\source-snapshot\...` | 否 | 3689 B | 是 | 有 |

**推论**：用本仓库 HEAD 的 csproj 编译 plugin-acad22，`FoundationExecution` 相关类型根本不会进编译单元
（因为它们在 `plugin-acad26\*.cs` 里）——**评审镜像不可构建**，它只是供审读的快照。

## 二、关键源码文件在构建树与镜像之间就已经不同

| 文件 | 构建树 | 镜像（HEAD） | 是否一致 |
| --- | --- | --- | --- |
| `shared/Execution/FoundationDrawingPlan.cs` | 36777 B，sha256 `7C6EF169…` | 35416 B，sha256 `05839B36…` | **否** |
| `shared/Execution/FoundationPlanExtensions.cs` | 28073 B，sha256 `A129445A…` | 27467 B，sha256 `BF2FADE6…` | **否** |
| `plugin-acad26/FoundationDxfExport.cs` | 4347 B，sha256 `DFF621F6…` | 4383 B，sha256 `1E2FB238…` | **否** |

也就是说：**我在 `01-defects.md` 里引用的行号（`FoundationPlanExtensions.cs:43-45`）来自镜像，不是运行中的那份。**
已复核：`dimension_style` 必填这一条在**构建树里同样成立**（`KeysWithOptional(... "dimension_style" ...); Text(s,"dimension_style",255);`），
所以 D1 的根因判断仍然有效，但**行号引用必须换成构建树路径**（见 `05-review-response.md`）。

## 三、运行中的二进制确实来自构建树（可信）

| 项 | 值 |
| --- | --- |
| `runtime\plugin\Bimwright.Dwg.Plugin.Acad22.dll` | 1,039,872 B，2026-11-16 09:07:36 |
| 该 DLL 的 sha256 | `77FC7C3E…E346` |
| `runtime\manifest.json` → `plugin_sha256` | `77FC7C3E…E346`（一致） |
| `plugin_mvid` | `784f7287-08c2-4a0e-aa9b-e4192db2f327` |
| 构建日志 `build-logs\rc5-plugin22.log` | 编译目标是 `src\cad-agent-foundation-main\...\plugin-acad22\*.csproj`，118 警告 / **0 错误**，耗时 3.42 s |

即：DLL ↔ manifest ↔ 构建树 三者自洽；**镜像不参与**。

## 四、由此修正 D8（评审意见：export_dxf 源码已有实现）

- 源码：`plugin-acad26\FoundationDxfExport.cs`（41 行，构建树 4347 B）确实实现了 `ExportDxf`。
- 二进制实测（对运行中的 2022 插件 DLL 做字符串扫描）：

| 符号 | 插件 DLL 出现次数 | 服务端 DLL |
| --- | --- | --- |
| `export_dxf` | **0** | 1 |
| `FoundationDxfExport` | **0** | 0 |
| `file_output_requires_metres` | **0** | 0 |
| `INSUNITS` | **0** | 1 |

→ **结论不是"没写"，而是"没有编进 2022 插件"**（或这份构建产物来自包含逻辑更少的 csproj 组合）。
所以 D8 应改为"构建/装配缺口 + 一处单位门槛"，不是"功能缺失"。

同时，`FoundationDxfExport.cs` 里有一条硬门槛：

```csharp
int units = Convert.ToInt32(Application.GetSystemVariable("INSUNITS"));
if (units != 0 && units != 6) throw new InvalidOperationException("file_output_requires_metres_or_explicit_unitless_metres");
```

即：**只接受"无单位(0)"或"米(6)"**。我们的图按毫米画（INSUNITS 常规为 4），若真如此，
即使把实现编进去也会被这条直接拒掉。**本轮未实测该图的 INSUNITS**（accoreconsole 导出的 DXF 头部里
没找到 `$INSUNITS` 记录，只有 `$ACADVER=AC1024`、`$DWGCODEPAGE=ANSI_936`），属证据缺口，列为 P0-A 待测项。

## 五、必须先做的动作（P0-A）

1. 确认**唯一构建源**：`src\cad-agent-foundation-main`（建议纳入版本管理或至少生成完整 SHA256 清单随 release 走）。
2. 让 `_review-repo` 与构建树**同源**：要么镜像改为从构建树同步生成，要么在镜像里明确标注"非构建源，仅供参考"。
   否则任何基于本仓库的评审/修复都会落在错的文件上（这次已经发生一次）。
3. 补 `plugin_sha256 / server sha256 / 源码树 sha256` 三者的对照表进 release manifest，
   并在 `foundation doctor` 里输出，做到"一眼看出运行的是哪份代码"。
4. 实测 `INSUNITS`（读 DWG，不写），确认 export_dxf 的单位门槛是否会命中。
