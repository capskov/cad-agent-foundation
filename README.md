# ACAD2022 V1.0 候选版 — 代码/证据评审包

本目录是 `cad-agent-foundation` 从 AutoCAD 2026（.NET 8）移植到 **AutoCAD 2022（R24.1 / .NET Framework 4.8）** 的 V1.0 候选版评审材料。
目的：让其他开发者或智能体在没有本机环境的情况下，也能读懂"做到哪、证据是什么、哪里还没做"。

## 1. 先读哪些

| 顺序 | 文件 | 作用 |
| --- | --- | --- |
| 0 | `source/` | **完整源码快照**（410 个文件，`source/BUILD.md` 是审查入口，`source/MANIFEST.sha256` 可逐文件校验） |
| 1 | `docs/ACAD2022-MIGRATION-STATUS.md` | 权威进度（第 1–13 节，含 RC1/RC2/RC3/RC4/RC5 与 V1.0 判定） |
| 2 | `docs/ACAD2022-DEPLOY.md` | 构建/打包/安装/启动/状态语义/故障恢复/卸载回退 |
| 3 | `docs/ACAD2022-AUTONOMOUS-RUN.md` | 接续状态文件（下一任接手者从这里开始） |
| 4 | `docs/ACAD2022-RC1/RC2/RC3-TEST-LOG.md` | 三个节点的实测原始日志 |
| 5 | `artifacts/regression-report.json` | 自动化回归结果（13 项，全通过） |
| 6 | `checkpoints/manifest.tsv` | 409 个源码文件的 SHA256 清单（无 Git 环境时的等价检查点） |
| 7 | `tests-manual/` | RC3/RC4 的人工/半自动验证脚本与 plan 文件（崩溃窗口、状态矩阵、保存失败、真实图副本） |

## 2. 交付物

- `artifacts/package/20261115-151649.zip`：运行时包（插件 DLL net48、服务端 net8、客户端 `foundation.py`、support、docs）。sha256 `D24F5037D8D200CB241AF838F1BEEC4FDFACAE8F52C25F3E2CC25B5C731C7CC3`。
- `artifacts/plugin-sha256.txt`：插件 DLL 与关键二进制哈希。

## 3. 评审时建议重点核查的 5 处

1. **owner 变更守卫**：`server/Execution/BoundPluginExecutionGateway.cs` 的 `GetJobAsync`、`BoundPluginExecutionGateway.D1.cs` 的 `GetD1WriteJobAsync`。重启后不联系新进程，直接由 ledger 返回 `recovery_required`，避免 120 s 挂起。
2. **操作员恢复分支**：`plugin-journal-net8/SqlitePluginIntentJournal.cs` 的 `ResolveUnknownJob`。只允许"completed + committed + passed + fence 未释放 + 该作业之后存在指纹/路径/时间绑定同一的已完成保存"这一种窗口释放 fence，其余一律拒绝；注意 `HasCompletedSaveAfterCore` 与公共 `HasCompletedSaveAfter` 的 lease 嵌套问题。
3. **保存证据绑定**：同一文件内 `HasCompletedSaveAfter`，四条件（`database_fingerprint` 相同、`drawing_path` 相同、保存时间严格晚于写作业、`result_json.saved_path` 非空）。
4. **2022 保存路由**：`shared/Execution/FoundationSaveRequest.cs`（绝对路径/`..`/flag 白名单）、`server/Tools/FoundationSaveTools.cs`、客户端 `persist()`（`..` 拒绝、已存在目标默认拒绝覆盖）。
5. **客户端不确定性语义**：`client/foundation.py` 的 `apply()`（提交前落 `requests/<rid>.json`，不确定只查询不重放）、`query()`（接受 request_id、失败即 fail clean）、`apply_with_optional_save()`。

## 4. 复现（需要 Windows + AutoCAD 2022）

```powershell
$pkg='...\20261115-151649'
$cl="$pkg\client\foundation.py"
python $cl install --workspace <ws> --acad-year 2022 --cad "F:\Program Files\Autodesk\AutoCAD 2022"
python $cl start   --workspace <ws> --acad-year 2022 --write --allow-save `
  --cad "F:\Program Files\Autodesk\AutoCAD 2022" --source <某个.dwg> --headless
# 13 项自动回归
python cad2022-test\run_regression.py --package $pkg --workspace <ws>
```

## 5. 已知未完成（评审请按此判断，不要当作缺陷漏报）

- **出图未接入**：`FoundationDrawingPlan` 的 op 白名单无 `plot_pdf`/`page_setup`，服务端无独立打印工具；仓库内的 `plugin-acad26/FoundationPrinting.cs`、`shared/Execution/FoundationPrintContract.cs` 尚未在 2022 路由开放，因此 CTB/PDF/比例可读性无任何实测。
- **2026 未验证**：本机无 AutoCAD 2026，`plugin-acad26` 编译仅因缺 `C:\Program Files\Autodesk\AutoCAD 2026` 引用程序集失败（非代码缺陷）；2026 运行测试未做。
- `save_drawing` 仍记 `effects=none`，文件副作用靠 journal 绑定表达（TR-03 第 1 问未定）。
- fence 无自动超时；`--save-to` 允许任意绝对路径（仅拦 `..` 与默认拒绝覆盖）。
- 崩溃恢复、双实例隔离尚未纳入 `run_regression.py`；10+ 轮连续写入、XREF/块参照场景未测。
- 无 Git 提交历史（原本机无 git）；本仓库的源码快照用 SHA256 清单表达。

## 6. 安全边界（评审时不要建议放宽）

不修改生产图纸、不操作用户正在工作的 CAD 实例、不关闭 SECURELOAD、不清库/删 fence/重置任务"通过测试"、不把未解决状态自动改判为成功、保存后崩溃不自动 finalize。
