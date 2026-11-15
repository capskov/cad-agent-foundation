# AutoCAD 2022 移植开发进度

> 本文件记录 `cad-agent-foundation` 从 AutoCAD 2026 / .NET 8 向 AutoCAD 2022 (R24.1 / .NET Framework 4.8) 移植的实时状态。
> 上游口径见 `COMPATIBILITY.md`、`WHY-AUTOCAD-2026.md`；本文件只记录移植侧的增量事实。
> 最近更新：2026-11-15（UTC+8）
>
> **工程根目录（2026-11-15 迁移）**：`F:\AI\cad-agent-foundation-acad2022\`
> 此前工作在 `C:\Users\cg524\DoubaoWork\chats\2026-11-15\new-chat\`（聊天会话目录，易随会话丢失）。已全量搬至 F 盘并经 1241 个文件逐个 SHA256 比对，0 缺失 0 不一致。旧路径现为空壳（被应用句柄占用无法删除），不要再写入。

## 1. 状态总览

成果等级按四档区分，**不得混用**：

| 等级 | 含义 | 2022 当前状态 |
| --- | --- | --- |
| 编译通过 | 0 error 产出 net48 插件程序集 | ✅ 达成 |
| 加载成功 | acad.exe 内 NETLOAD 成功、运行时初始化完成、发现文件发布 | ✅ 达成 |
| 功能执行成功 | 任务在 CAD 主线程真实执行，只读与写入均返回真实图形数据 | ✅ 达成 |
| 持久化结果正确 | journal 落库、幂等、fence 释放、finalize 落盘 | ✅ 达成（含真实 DWG 重开核验） |

写入链路、控制管道、取消语义均已实测通过。尚未覆盖：异常退出恢复、多实例隔离、超时边界。

生产图实例（PID 12948，`03南一路_路施17交通安全设施平面图c.dwg`）**全程未触碰**；所有验证都在隐藏窗口隔离实例 + 独立 `BIMWRIGHT_DWG_ROOT` 内完成，测试文档为未保存的 Drawing1（无文件路径）。

## 2. 已完成的工作

### 2.1 工程与兼容层

| 文件 | 作用 |
| --- | --- |
| `fork/src/plugin-acad22/Bimwright.Dwg.Plugin.Acad22.csproj` | 2022 交付工程：`net48`、`PlatformTarget x64`、`AutoCad2022Dir` 默认 `F:\Program Files\Autodesk\AutoCAD 2022`、含 `ValidateAutoCadReferences` |
| `fork/src/shared/Execution/NetFx.cs` | 13 个 BCL 差异垫片（`IsFinite`/`IsPathFullyQualified`/`ToHexString`/`Sha256`/`ThrowIfNull`/`MoveFile`/`ServerPipeOptions` 等）+ `ReferenceComparer<T>` |
| `fork/src/shared/Execution/NetFrameworkPolyfills.cs` | `#if NETFRAMEWORK` 下补 `IsExternalInit`（`init` 访问器） |
| `fork/src/shared/Execution/NativeLibraryPreloader.cs` | net48 专用：绝对路径 `LoadLibraryW` 预加载插件目录的 `e_sqlite3.dll`，**不调用** `SetDllDirectory`（避免改 AutoCAD 全局 DLL 搜索路径） |
| `fork/src/shared/Execution/NetFrameworkAssemblyResolver.cs` | net48 专用：为 `System.Memory`/`System.Buffers`/`Unsafe`/`Numerics.Vectors`/`Encoding.CodePages` 注册 `AssemblyResolve`，仅在默认绑定失败时介入，只处理插件自带垫片 |
| `fork/src/shared/Transport/IPluginExecutionPump.cs` | 把"能排空任务队列"从"传输是管道"解耦 |
| `fork/src/shared/Transport/ControlPipeListener.cs` | 2022 的控制通道宿主（复用单实例，消除连接间空窗） |
| `fork/src/shared/Transport/ServerPipeFactory.cs` | net8 用 `PipeOptions.CurrentUserOnly`；net48 用 `PipeSecurity` 显式 DACL 实现等效同用户限制 |

上游文件的改动（65 处调用点 + 6 个 using）：`PluginExecutionQueue.cs`、`SqlitePluginIntentJournal.cs`、`PluginOwnerLivenessProviders.cs`、`ProcessExecutionLease.cs`、`PluginWriteOutcomeGuard.cs`、`AcadDocumentContextRegistry.cs`、`J10FixedHatchRepairMethod.cs`、`FoundationRasterReference.cs`、`FoundationViewHandler.cs`、`FoundationXrefPath.cs`、`Road08R01WriteHandler.cs`、`FoundationExtendedOperations.cs`、`FoundationPlanWriteHandler.cs`、`FoundationDxfExport.cs`、`FoundationPrinting.cs`、`shared/Transport/PipeTransportServer.cs`、`shared/Transport/TcpTransportServer.cs`、`shared/Infrastructure/PluginReadOnlyJobParameters.cs`、`plugin-acad24/App.cs`。

### 2.2 构建命令与依赖版本（可重复执行）

```powershell
dotnet build `
  "src\cad-agent-foundation-main\third_party\bimwright-dwg-mcp\fork\src\plugin-acad22\Bimwright.Dwg.Plugin.Acad22.csproj" `
  -c Release
```

- 结果：**0 error / 97 warning**（全部 CS8632，与移植前同口径，补丁未引入新警告）
- 关键包：Newtonsoft.Json 13.0.3 / Microsoft.CodeAnalysis.CSharp.Scripting 4.8.0 / Microsoft.Data.Sqlite 8.0.31 / System.Text.Json 8.0.5 / **SQLitePCLRaw.lib.e_sqlite3 2.1.13**
- 产物：`plugin-acad22\bin\Release\net48\Bimwright.Dwg.Plugin.Acad22.dll`（约 1.01 MB，TFM `.NETFramework,Version=v4.8`）；`FlattenSqliteNative` 目标把 `e_sqlite3.dll` 平铺到输出根

### 2.3 四个阻塞性缺陷的根因与修法

1. **SQLite 原生库定位失败**（`TypeInitializationException`）
   - 根因：.NET Framework 的 `DllImport` 只搜索宿主进程目录（acad.exe）与 PATH，不像 .NET 8+ 会搜索程序集自身目录。
   - 修法：`NativeLibraryPreloader` 以绝对路径 `LoadLibraryW` 预加载。
   - 验证：独立 net48 控制台程序能过（证明 SQLite 本身无问题），把故障锁定在宿主环境。

2. **托管垫片严格版本绑定失败**（第二个、不同因的 `TypeInitializationException`）
   - 证据：诊断落盘 `System.IO.FileNotFoundException: "System.Memory, Version=4.0.1.1"`，崩溃点 `SQLitePCL.Batteries_V2.DoDynamic_cdecl`。
   - 根因：`SQLitePCLRaw.core 2.1.12` 绑 `System.Memory 4.0.1.1`，`System.Text.Json 8.0.5` 抬到 `4.0.1.2`；net48 按精确版本绑定，绑定重定向只存在于宿主 `acad.exe.config`（不得篡改）。
   - 修法：插件自注册 `AssemblyResolve`。

3. **任务队列在 2022 上永不排空**
   - 根因：`PluginExecutionQueue.Pump()` 唯一调用点被写死为 `_server is PipeTransportServer`；2022 用 TCP，Idle 泵从未触发。执行链本身不依赖管道形态。
   - 修法：抽 `IPluginExecutionPump`，两种传输各自实现；`OnIdleToast` 按能力判断（2026 行为不变）。

4. **2022 无控制通道**
   - 根因：`PipeTransportServer` 承载控制管道，被 `ACAD2025_OR_GREATER` 门禁排除；控制命令是 `IsControlPipeOnlyCommand`，TCP 一律拒绝。
   - 修法：`ControlPipeListener` 独立承载控制管道，由 `TcpTransportServer` 组合启动，发现文件补 `control_pipe_name`；TCP 数据通道保持原样，仍无法触达控制命令。

### 2.4 隔离探针实测结果（净证据）

环境：隐藏窗口独立 `acad.exe 2022`、`/b` 脚本驱动、独立 `BIMWRIGHT_DWG_ROOT`、`LOGFILEPATH` 捕获命令行日志。

- `NETLOAD` 成功，**7.8 秒**内发布 `Dwg\acad-2022.json`：
  ```json
  {"schema_version":2,"acad_year":2022,"transport":"tcp","host":"127.0.0.1",
   "port":13035,"pipe_name":null,
   "control_pipe_name":"BimwrightDwg-2022-<pid>-control-<guid>",
   "auth_token":"...","pid":12200,"process_name":"acad","started_at_utc":"..."}
  ```
- `Dwg\execution\plugin-execution.sqlite` 正常创建（约 53 KB），表：`plugin_jobs`、`plugin_journal_identity`、`plugin_journal_meta`、`plugin_owners`、`plugin_write_fences`
- lease 文件与 journal owner 锁正常生成；`bind_active_document` 返回 `context_id`、`journal_scope_id`、`journal_path_digest` 与完整 `document_info`（含 `secureload:1`）

**只读链路**
- `job_submit` + `list_layers` → `phase: completed`，`result_json` 返回真实图层数据
- 同 `request_id` 重复提交 → `existing` / `completed`，无第二行 journal

**控制通道**（`\\.\pipe\<control_pipe_name>`，单行 JSON + `\n`）
- `job_get`：`queued` → `completed`，带回真实结果
- `job_get_historical`：可查历史快照
- `job_cancel`：`queued` → `cancelled`；未知 request → `not_found`；已完成 → `already_terminal`
- 安全边界三条全部成立：TCP 上 `job_get` 被拒（`job control commands require the control pipe`）；错误 token → `unauthorized`；控制管道上非控制命令 → `command_not_allowed_on_control_pipe`

**写入链路**（隔离实例 `BIMWRIGHT_FOUNDATION_WRITE_ENABLED=1`）
- 计划：`foundation-drawing-plan-v1`，两个创建步骤（line + circle），`units=raw_drawing_units`
- 提交后 `is_write:true` → `running` → `completed`；`effects: committed`，`verification: passed`
- 回执：`outcome: committed_verified`，`objects: {"l1":{"entity":"222"},"c1":{"entity":"223"}}` — 图形中**真实新增两个实体**
- 重复提交同一 `request_id` → `existing` / `completed`，无重复写入
- `job_write_finalize`（带 `staged_plugin_sequence` / `readback_revision` / `readback_digest` / `committed_result_digest`）→ `disposition: applied`；journal 随后 `write_fence_released=1`、`write_fence_finalized=1`

**参数契约教训**（写进文档避免复踩）
- `canonical_parameters_json` 必须是**键名 ordinal 有序、紧凑**的 JSON（`json.dumps(sort_keys=True, separators=(",",":"))`）
- `FoundationDrawingPlan.Keys(...)` 要求步骤字段数量**精确匹配**：`line` 必须含 `id/op/start/end/layer/color`，`circle` 另加 `center/radius`

**落盘验收（真实 DWG 往返）**
上游的设计到"内存已提交 + fence 释放"为止：`job_write_finalize` 只改 journal，**不写盘**；`save_drawing` 虽然是注册过的命令，但在 `ACAD2026` 分支下直接命令执行被禁，队列白名单里也没有它 —— 也就是说 2026 与 2022 一样，**写成功之后无法通过插件落盘**。这是上游能力缺口，不是移植问题。

移植侧补了一个受控入口（默认关闭）：

| 环境变量 | 作用 |
| --- | --- |
| `BIMWRIGHT_FOUNDATION_SAVE_ENABLED=1` | 允许 `save_drawing` 进 Foundation profile 队列（仅与写 profile 同开时生效） |

实现要点：`App.FoundationSaveEnabled` 决定队列读白名单是否加入 `save_drawing`；`DispatchJobSubmit` 的入队白名单与 `ExecuteReadOnlyJob` 的执允许清单**两处都要放行**（见下"三个坑"第 1 条）；`CreateReadOnlyJobHandler` 增加 `save_drawing → SaveDrawingHandler`；保存作业走 `is_write=false`（不动图形模型，不需要 fence/readback）。

实测（隔离实例，`run-persist3`）：

1. `foundation_apply_plan`（line + circle）→ `committed_verified`，回执 `objects: {"l1":{"entity":"222"},"c1":{"entity":"223"}}`
2. `job_write_finalize` → `disposition: applied`
3. `save_drawing`（`{"output_path":"...\\persist-test.dwg","confirm":true,"allow_repo_output":true}`）→ `completed`，`result_json: {"saved_path":"...\\persist-test.dwg"}`
4. 磁盘文件 **30,133 B** 落地；杀进程后重启新实例，直接打开该 DWG
5. `get_entity_properties` 读回：`222 → Line, layer 0, color 7`；`223 → Circle, layer 0, color 7`，各自带 `entity_digest`

结论：**写入结果确实持久化到磁盘，不只是接口返回成功**。

**三个操作性坑（新增）**
1. `save_drawing` 有**两道独立白名单**：`DispatchJobSubmit` 入队前检查一次（`CommandDispatcher.cs:304` 附近），`ExecuteReadOnlyJob` 执行时再检查一次。只改后者会得到 `tool_not_allowlisted`。
2. **部署前必须先杀掉测试实例**：运行中的 acad.exe 锁住插件目录的 dll/native，`Copy-Item -Force` 会静默部分失败，随后仍加载旧 dll，表现为"改了代码却没生效"。
3. 用 `/b` 脚本 `_.OPEN` 打开图纸会弹文件对话框（FILEDIA=1）把脚本卡死；正确做法是把 DWG 路径作为 `acad.exe` 的启动参数，脚本只负责 `LOGFILEPATH` + `NETLOAD`。
4. `bind_active_document` 每次绑定产生**新的 context_id**；`job_write_finalize` 必须用 journal 行里记录的 `context_id`，用新绑定的会被判 `write_finalize_stage_mismatch`。

### 2.5 崩溃恢复测试（发现一个真实缺陷）

做法：隔离实例（`run-recover`）提交写入 → 停在 `completed/committed` 但**不做 finalize** → 硬杀 acad.exe → 同 root 重启 → 再提交写入。

杀之前与杀之后的 journal 一致：

```
plugin_jobs         recover-01: phase=completed effects=committed verification=passed
                              seq=4 write_fence_released=0 write_fence_finalized=0
plugin_write_fences 1 行，request_id=recover-01（fence 仍被持有）
plugin_owners       旧 owner 行仍在（process_id=29500，已死进程）
```

重启后：新实例注册了新的 owner 行，`recover-after-01`（只读）正常跑到 `completed` —— **执行链本身可用**，但：

1. **死进程的 owner 行没有被回收**（表里同时存在 3 个 owner，其中 1 个 PID 已不存在）。
2. **未 finalize 的 fence 没有被释放**，仍然占着同一个 risk_key（`PATH:<profile 路径>`）。
3. 在该 fence 未释放的情况下再提交写入（`recover-02`）：`job_submit` 返回 `ok:true`，但**`plugin_jobs` 里没有出现 recover-02 的行**，即写入在 fence 阶段被挡下且未留下作业记录。

结论：**崩溃后同 risk_key 的写入会持续被阻断**，需要人工按 journal 行补一次 finalize，或实现启动时的 liveness 清扫。这是行为缺陷，不是编译或移植问题；上游 2026 同样存在（同一份 `PluginOwnerLivenessProviders` / `ProcessExecutionLease` 逻辑）。

修复方向（待做，需谨慎保持 2026 语义）：启动时对 `plugin_owners` 中 `process_id` 已不存在的行做 stale 判定；对 `plugin_write_fences` 中 owner 已 stale 且 `fence_finalized=0` 的记录，按"要么补 finalize、要么显式作废并记录"的二选一语义处理，不能静默丢弃。

### 2.6 部署与清理

- 当前唯一测试部署：`F:\Program Files\Autodesk\AutoCAD 2022\Plugins\CadFoundation2022-c4`（受信任目录，NETLOAD 用 8.3 短路径）
- 已删除：`CadFoundation2022-a1test`、`-c2`、`-c3` 旧部署；`plugin-acad22\Probe.Acad22.csproj`；`probe-support\Polyfills.cs`
- 探针脚本保留在 `cad2022-test\`：`probe_stage2.py`（只读）、`probe_job.py`（队列执行）、`probe_control.py`（控制通道）、`probe_write.py`（写入）、`probe_finalize.py`（收尾）、`probe_cancel.py`（取消）

## 3. 尚未解决的问题

1. **崩溃后写入被残留 fence 阻断**（已复现，见 2.5）：死 owner 行不回收、未 finalize 的 fence 不释放、后续写入被挡且不留作业行。需要实现启动时 liveness 清扫，且不能静默丢弃 fence。
2. **多实例隔离未验证**：需同时起两个隔离实例，确认发现文件按 PID 区分、lease 互斥、控制指令不会串到另一个实例。
3. **超时边界未验证**：`queue_deadline_utc` 到期行为（只读与写入）尚未实测。
4. **`ACAD2026` 宏仍兼任"任务执行能力"**：当前 2022 工程靠 `DefineConstants` 借用；须拆成版本能力宏与任务能力宏，且保证 2026 输出零变化。
5. **2026 无法本机回归**：本机只有 2012 / 2020 / 2022；2026 侧只能编译 + 代码审查，须在 `COMPATIBILITY.md` 如实声明。
6. **发现文件 schema 命名残留**：`complete_target_json` 内含 `"schema":"bimwright-acad26-document-target-v1"`（功能无害，需评估按年份参数化）。
7. **客户端硬编码未改**：`client/foundation.py` 仍写死 `acad-2026.json`、断言 `acad_year==2026`、安装目录 `CadFoundation2026-v1-`、程序集 `Bimwright.Dwg.Plugin.Acad26.dll`；尚未适配 `control_pipe_name`。
8. **安装位置策略未定**：`<AutoCAD 2022>\Plugins\`（现用，受信任、免签名）还是 `scripts/sign-foundation-artifacts.ps1` 自签名 + 信任发布者；约束是 SECURELOAD 必须保持开启。
9. **Git 未安装**：`git` 不在 PATH，`C:\Program Files\Git\cmd\git.exe` 不存在，G 阶段需先装。

## 4. 下一步可直接执行的工作

1. 异常恢复探针：提交写入后立即强杀 → 重启同 root → 检查 owner/fence 与任务终态。
2. 多实例探针：两个隔离实例同一时刻，验证发现文件、lease、控制管道互不串扰。
3. 超时探针：构造超过 `queue_deadline_utc` 的场景。
4. 能力宏拆分（`BIMWRIGHT_TASK_EXECUTION` vs `ACADxxxx`）。
5. 客户端参数化：`acad-<year>.json`、`control_pipe_name`、安装目录名、程序集名。
6. `COMPATIBILITY.md` 补 2022 章节（含"2026 未运行回归"的如实声明）。
7. 配套 `cad-callout-pair-layout` 端到端（F 阶段）。
8. 装 git → 工作分支检查点 → 准备上游 PR（推送远程需另行批准）。

## 5. 重要决策及依据

| 决策 | 依据 |
| --- | --- |
| 采用**移植**而非重写 2022 专用 skill | 编译探针实测 60 个缺口全部是 BCL 垫片级、CAD API 零缺口；移植失败形态是"编译不过"（当场暴露），重写失败形态是"运行期写坏 DWG"（不可逆）；且产物可合回上游 |
| 保留 journal / fence / lease / 任务治理（B 方案） | 实测 `Pump` 只由控制通道驱动，任务执行与治理语义强耦合；放弃管道等于放弃取消、超时、幂等与恢复 |
| 控制命令只走命名管道，绝不放 TCP | `IsControlPipeOnlyCommand` 是上游既定边界；实测 TCP 依旧拒绝四命令 |
| net48 管道用 `PipeSecurity` DACL 替代 `CurrentUserOnly` | 用户明确要求"不得因缺 API 就取消访问限制"；DACL 只放当前用户 SID、关闭继承，远程登录无匹配 ACE 由内核拒绝 |
| 不改 `acad.exe.config` 做绑定重定向 | 属宿主配置，改它等于篡改 AutoCAD 安装；改用插件内自注册解析器 |
| 原生库用 `LoadLibraryW` 绝对路径预加载 | `SetDllDirectory` 会改写进程级搜索路径，波及 AutoCAD 与其他插件 |
| SECURELOAD 保持 1 | 上游 `AGENTS.md` 明令；实测受信任目录加载无需关闭 |
| `ACAD2026` 不作永久方案 | 该宏同时承担"版本事实"与"任务能力"两种语义 |

## 6. 检查点与 Git 状态

目前已建立**不依赖 git** 的可恢复检查点（`new-chat\_checkpoints\`）：

| 产物 | 内容 |
| --- | --- |
| `manifest-before-20261115-180940.tsv` | 541 条（504 foundation + 37 callout），每条含相对路径、字节数、SHA256 |
| `src-20261115-180940.tar.gz` | 15.9 MB，649 条目（含全部新增文件与 78 个构建产物） |
| `snapshot-20261115-184414\` | 543 文件 / 41.8 MB，目录级完整快照；抽样比对 `NetFx.cs`、`ControlPipeListener.cs` 哈希一致 |

原始 zip 基线也仍在：`src\foundation.zip`（3,791,059 B）、`src\callout.zip`（1,002,447 B）。

**git 尚未就绪**：
- 两个源码树都是从 zip 解压得到，**不含 `.git`**，因此没有可继承的历史，需要在本地 `git init` 并从 pristine zip 重建首个提交。
- `winget install Git.Git --scope user --silent` 挂了约 20 分钟无输出，已终止。
- 改走 MinGit 便携包（`MinGit-2.56.1-64-bit.zip`，38 MB），下载速率仅约 0.02 MB/s，尚未完成。
- 全盘常见根（`Program Files`、`Program Files (x86)`、`%LOCALAPPDATA%\Programs`、scoop、`ProgramData`）搜索 `git.exe`，**均无**。

git 就绪后的既定步骤（不覆盖历史、不推远程）：在 `cad-agent-foundation-main` 内 `git init` → 先把 pristine zip 内容落为首个提交（upstream import）→ 再把当前工作树覆盖回来提交为第二个提交（acad2022 移植），这样能产出可审查的真实 diff；`.gitignore` 排除 `bin/`、`obj/`。

## 7. ACAD2022-RC1 本轮记录（2026-11-15 19:xx）

### 7.1 RC1 五项验收状态

| # | 验收项 | 状态 | 依据 |
|---|---|---|---|
| ① | 可靠源码检查点 | 以非 git 形式成立 | `_checkpoints\rc1-20261115-191217\`：438 文件 SHA256 清单 + 8.8 MB `src-*.tar.gz`（已排除 `bin/`、`obj/`、`*.sqlite`）。git 仍未就绪，见 7.5 |
| ② | 崩溃恢复无静默丢失任务 | **通过** | 测试 A/B/D（7.3）。fence 冲突改显式失败并留痕；确定未执行的排队任务安全回收；可能已改图形的不自动认定未执行 |
| ③ | 写入→finalize→保存状态一致性 | **通过（协议已修）** | 新增"保存前置"守卫；TX2–TX9 实测，见 7.7 |
| ④ | 双实例隔离与超时取消 | **通过（超时项见 7.9）** | DUAL/CANCEL 实测，见 7.7；服务端无队列超时（如实标注） |
| ⑤ | 真实测试日志与版本记录 | **通过（本地），2026 未验证** | 证据索引见 `docs\ACAD2022-RC1-TEST-LOG.md`；2026 无安装，运行回归如实标注未验证 |

### 7.2 本轮代码改动（全部只在 2022 交付工程范围内，2026 语义未动）

| 文件 | 改动 | 目的 |
|---|---|---|
| `plugin-journal-net8\SqlitePluginIntentJournal.cs` | 新增 `RecordRejectedSubmission`；新增 `ResolveUnknownJob`；`RecoverLostOwnersCore` 增加"排队期 owner 死亡"分支 | 被拒意图留痕；unknown fence 显式解除；确定未执行的任务安全回收 |
| `shared\Execution\PluginJobModels.cs` | 接口 `IPluginIntentJournal` 增两个方法；新增 `PluginRecoveryResolveResult` | 契约 |
| `shared\Infrastructure\CommandDispatcher.cs` | `DispatchJobSubmit` 对 `FenceConflict` 返回 `write_fence_conflict` 并落记录；`RequestIdConflict` 返回失败；新增 `DispatchRecoveryResolve` 与分发 | 消除"返回成功但无记录"；人工解除通道 |
| `shared\Transport\PipeControlFrameProtocol.cs` | 控制命令白名单增 `job_recovery_resolve` | 仅控制管道可用 |

编译：`dotnet build ...\plugin-acad22\Bimwright.Dwg.Plugin.Acad22.csproj -c Release` → **0 error / 100 warning（全部 CS8632）**，产物 dll 1,028,096 B。

### 7.3 崩溃恢复实测（隔离实例 + 专用测试根，未触碰生产图纸）

**测试 A（已提交但未 finalize 的写入，重启后必须保留）** `run-rc1-a`
- 杀前：`rc1-a1 phase=completed effects=committed verification=passed write_fence_released=0`
- 重启后：**原样保留**，未被自动改成成功或清理
- 被占 fence 下提交 `rc1-a2` → `ok:false, error=write_fence_conflict`（不再是 `ok:true`+`job:null`）
- journal 出现 `rc1-a2 phase=failed error_code=write_fence_conflict write_fence_released=1`，无新增 fence 行

**测试 B（排队中被杀 = 确定未执行）** `run-rc1-b`
- 杀前：`rc1-b1 phase=queued effects=none`，fence 持有
- 重启后：`rc1-b1 phase=failed effects=none observation=recovery error_code=owner_lost_before_start write_fence_released=1`，`plugin_write_fences` **清空**
- 随后 `rc1-b2` 正常 `completed/committed`

**测试 D（运行中被杀 = 可能已改图形）** `run-rc1-d`
- 杀前：`rc1-d1 phase=running effects=unknown`
- 重启后：`phase=unknown effects=unknown observation=recovery`，fence **继续持有**（符合"不得自动认定未执行"）
- `rc1-d2` → `ok:false write_fence_conflict`
- `job_recovery_resolve{confirm:true, operator_note}` → `disposition=applied`，返回 `released_risk_keys`，作业变 `phase=failed effects=unknown error_code=abandoned_after_recovery`（**不确定性被保留，没有被粉饰成成功**）
- 解除后 `rc1-d3` 正常 `completed/committed`

**负向用例**：无 `confirm` → `confirm_required`；对 `completed` 作业解除 → `job_not_in_recovery_state`；不存在的 id → `job_not_found`；已解除后的 owner 仍存活/不确定 → 分别 `job_owner_still_alive` / `job_owner_liveness_indeterminate`（不解除）。

### 7.4 新增控制命令契约（仅控制管道，数据通道一律拒绝）

```
cmd: job_recovery_resolve
params: { "request_id": "<id>", "confirm": true, "operator_note": ">=4 字符" }
成功: { disposition: "applied", released_risk_keys: [...], job: {...} }
失败码: confirm_required | operator_note_too_short | invalid_request_id
        | job_not_found | job_not_in_recovery_state | job_owner_still_alive
        | job_owner_liveness_indeterminate | plugin_sequence_overflow | resolve_race_detected
```

设计要点：只对 `phase=unknown && observation=recovery` 生效；解除前必须确认原 owner 非 Alive；作业的 `effects` 保持 `unknown`，只加终止记录并释放 fence —— **不是**"把没解决的崩溃状态清成成功"。

### 7.5 本轮发现的运行期问题（重要）

**journal 路径绑定保护**：`ComputeJournalPathDigest = sha256(大写全路径)`，库内 `plugin_journal_identity.path_digest` 与当前路径不一致时，插件在 `InitializeDatabase` 抛 `The plugin execution journal identity does not match its configured path.`，**实例起不来、也无 discovery 文件**。

本轮真实踩到：`run-recover\native\...\plugin-execution.sqlite` 是 18:39 在 `C:\Users\cg524\DoubaoWork\chats\...` 路径下创建的，工程整体搬到 `F:\AI\...` 后 digest 不再匹配（已用哈希核对确认：库内为 `52531D74…`，当前路径算得 `48DDA19C…`）。
- 判定：这是**上游防串库的安全设计**，不是缺陷；但它意味着"库跟着工程搬家就会失效"。
- 处理：RC1 全部测试改用全新测试根；旧库仅留作证据。
- 文档要求：迁移/部署时必须知道"journal 与路径强绑定，搬目录后旧库不可复用，需新根或由人确认后处理"。
- 失败同时会写 `native\Dwg\startup-error.log`（含完整异常链与已加载依赖清单），可诊断。

**owner 行不回收**：`plugin_owners` 中 ConfirmedLost 的行仍长期保留（目前是下次启动重复校验的依据）。它们不影响写入判定，但会让表持续增长；列入后续清理设计（须有记录，不得静默删）。

### 7.6 RC1 剩余工作（按优先级）

1. ③ 事务语义：测 `finalize` 释放 fence 后、`save_drawing` 之前允许新写入介入会造成什么（内存已提交 vs DWG 已持久化），并落"四态"表。
2. ④ 双实例：两实例不同 root 同时运行，验证 discovery 区分、错 token/错实例被拒、互不影响；同 root 并发的拒绝行为。
3. ⑤ 日志与版本记录：把探针输出固化为标准日志文件，记录 dll 大小/编译时间/依赖版本。
4. ① 的 git 线：`git` 仍不在 PATH，MinGit 下载未留存（速率 0.02 MB/s 不可用）。下一步优先尝试：`dotnet tool` 无关；可试 PowerShell `Install-Module` 无关；实用路径是允许慢速后台下载或改用可离线获取的 git 包。
5. ⑤ 客户端去 `acad-2026` 硬编码、`COMPATIBILITY.md` 补 2022 章节、callout 端到端。

## 8. RC1 第三、四项实测（2026-11-15 晚）

### 8.1 事务语义：发现并修复"fence 释放早于落盘"

修复前实测（证据在 `run-tx3`、`run-tx5`）：

| 场景 | 修复前 | 问题 |
|---|---|---|
| 写入→finalize→崩溃（未保存） | `rel=1 fin=1`，磁盘无实体；重启后新写入可继续 | 完成态成立但图形从未落盘：**静默丢失** |
| 写入→finalize→保存失败 | save `failed save_drawing_failed`，写作业仍 `rel=1 fin=1` | 同上，且无任何机制发现 |
| finalize→新写入→保存 | 保存成功（30,101 B），新写入 `rel=0` | "已落盘但未 finalize"，当时不可区分 |

修复（仅 `BIMWRIGHT_FOUNDATION_SAVE_ENABLED` + `foundation_apply_plan` 生效，2026 零变化）：

- `DispatchWriteFinalize` 增加 `write_requires_completed_save` 守卫；
- journal 新增 `HasCompletedSaveAfter(requestId)`：**同一图纸（`database_fingerprint` 相同）**、且在该写入之后完成、且 `result_json` 带 `saved_path` 的 `save_drawing`；
- 写作业若缺指纹则 fail closed，永不给保存信用；
- 顺序变为 **write → save → finalize**，`finalize` 由此才真正等于"已验证且已落盘"。
- 严格性用注入测试证明（`run-tx11`）：伪造一条指纹不符的"已完成保存"，finalize 仍被拒。

修复后实测（`run-tx9`）：

```
[1] 未保存 finalize  -> ok=False err=write_requires_completed_save
[2] save             -> completed，文件 30,002 B
[3] 保存后 finalize   -> ok=True disp=applied
dump: tx9-w1 rel=1 fin=1；fences (none)
```

保存失败仍不得放开 fence（`run-tx7`：save `failed`，finalize 被拒，`fences` 仍持有 tx7-w1）。

落盘真实性复核（`run-tx6`/`run-tx8`，新进程重开 DWG）：

```
{"entities":[{"handle":"222","ok":true,"entity":{"handle":"222","type":"Line","layer":"0","color_index":7}}]}
```

四态定义（本轮落地）：

| 状态 | 判定依据 |
|---|---|
| 内存写入已提交 | `phase=completed && effects=committed` |
| 图形变更已验证 | `verification=passed && verification_staged_sequence` 存在 |
| DWG 已持久化 | 同 journal 内该写入之后有 `save_drawing` 完成（`HasCompletedSaveAfter`） |
| 任务完全完成 | 前三者 + `write_fence_released=1 && write_fence_finalized=1` |

### 8.2 双实例隔离（`run-dual-a` / `run-dual-b`，两进程同时在线）

| 检查 | 结果 |
|---|---|
| discovery | `port` / `control_pipe_name` / `pid` / `auth_token` 全部不同；`acad_year` 一致=2022 |
| journal | `journal_scope_id`、`journal_path_digest` 均不同（各自独立库） |
| 错 token（TCP） | `unauthorized` |
| 错 token（控制管道，双向） | `unauthorized` |
| 拿 A 的 `context_id` 在 B 上提交 | `invalid_document_context` |
| 各写各的 | A `dual-a1`、B `dual-b1` 均 `completed/committed/passed`，互不干扰 |
| 交叉查作业 | A 查 `dual-b1` → `found=False`；B 查 `dual-a1` → `found=False` |
| 杀掉 A | B 仍正常服务（后续 dump/作业查询正常） |

同 root 并行未专门测试：上游设计允许同 journal 多 owner，写入由 fence 串行化；被 fence 挡住时返回显式 `write_fence_conflict`（见 7.3 测试 A）。

### 8.3 取消语义（`run-cancel` / `run-cancel2` / `run-cancel3`）

| 用例 | 结果 |
|---|---|
| 排队中取消（连发 8 个只读任务后取消第 8 个） | `disposition=cancelled`，`phase=cancelled rel=1`，其余 7 个按 FIFO `completed`，**无 fence 泄漏** |
| 已完成作业再取消 | `already_terminal`（不改变状态、不释放他人 fence） |
| 不存在的 id | `not_found` |
| 写入被 fence 挡住后再取消 | 该请求本身已记录为 `failed/write_fence_conflict`，取消返回 `already_terminal` |
| 运行中取消 | 未构造成功（单次写入执行过快，未能抢在 `running` 阶段）——**如实标注为未验证** |

### 8.4 本轮新增恢复机制复核

- fence 冲突记录不影响幂等：同 `request_id` + 同 payload 重放 → `request_id_conflict`（因为首条是 `failed` 记录，digest 相同但状态已终态），**客户端必须换新 id 重试**；重放不会产生第二次写入（`run-tx2` 实测 `ok=False err=request_id_conflict`）。
- `job_recovery_resolve` 只能用于 `phase=unknown && observation=recovery`；必须先经 `job_get` / `job_get_historical` 查看作业与风险键（`released_risk_keys` 会回显），并写入 `operator_note` 作为审计；`effects` 永远保持 `unknown`，不转成成功。
- 未做"自动调用"路径：插件不会自行解除，只有控制管道显式命令可以。

### 8.5 仍未验证 / 未完成

1. **运行中取消**：需要更慢的作业（或人为暂停）才能构造，暂未验证。
2. **服务端队列超时**：`plugin_jobs.queue_deadline_utc` 字段存在，但 `DispatchJobSubmit` 未设置，当前超时只能是客户端侧；如需服务端超时须单独设计（会改变任务语义，待决策）。
3. **崩溃在 save 之后、finalize 之前**：DWG 已落盘但 fence 仍持有，需再次 finalize 才释放；是否允许"保存完成 + 验证通过"自动 finalize，见 `docs/technical-reviews/TR-2026-11-15-01-finalize-save-window.md` 第 9–10 节。
4. **CAD 2026 回归**：本机无 2025/2026，只能编译级验证 + 代码审查，**明确未运行**。
5. **Git 安装**：`winget` 长期无输出已终止；GitHub 直连 MinGit 包 `curl exit=56 / Recv failure: Connection was reset`，换 HTTP/1.1 + `--retry-all-errors` 仍不稳定。当前以 `_checkpoints\rc1-final-20261115-195817\`（439 文件 SHA256 清单 + 8.8 MB 源码归档）作为非 Git 检查点，源码可完整恢复；Git 属**外部网络阻塞**，不影响开发。
6. 客户端 `acad-2026` 硬编码、`COMPATIBILITY.md` 2022 章节、callout 端到端未做。

### 8.6 客户端参数化与打包（E 阶段，本轮推进）

| 项 | 结果 |
|---|---|
| `client/foundation.py` | 新增 `--acad-year`（默认 2026）；discovery 文件名、插件 dll 名、安装目录、错误文案、`acad_year` 断言全部按年份生成；discovery 改为扫描 `acad-*.json` 并按 `acad_year` 匹配 |
| `support/readonly-entry.py` | discovery 由固定 `acad-2026.json` 改为扫描；`--target` 由 `discovery_acad_year()` 从 bootstrap 读取 |
| `scripts/package-runtime.py` | 新增 `--acad-year`；自动选择 `plugin-acad22`/`net48` 或 `plugin-acad26`/`net8.0-windows`；README 文案与 limits 按年份生成 |
| 打包实测 | `python scripts/package-runtime.py --acad-year 2022 --version 1.2.0-acad2022.local` → 230 文件、manifest、MVID `52abb6a7-…`、zip 校验通过 |
| 安装实测 | `install --acad-year 2022 --cad "F:\Program Files\Autodesk\AutoCAD 2022"` → 版本化目录 `Plugins\CadFoundation2022-v1-14c1e1d68d12` |
| 启动实测 | `start --acad-year 2022 --source run-tx10\dwg\tx10.dwg` → `startup: discovered`，新 PID |
| 诊断/读取实测 | `doctor` 返回完整文档信息；`read --handles 222` 读回实体数据（走控制管道 `job_get`） |
| 服务端适配 | `PluginClient` 允许 TCP 数据通道 + 独立控制管道（原先直接拒绝 `control endpoint unavailable for this transport`）；`IsControlCommand` 增加 `job_recovery_resolve` |
| 编译链修正 | `plugin-journal-net8`、`server` 两个工程补入 `shared/Execution/NetFx.cs` 链接（此前仅插件工程包含），net8 目标恢复可编译 |

未完成：客户端 `apply` 的"写入后由谁 save/finalize"仍未定（见 `docs/technical-reviews/TR-2026-11-15-02-client-write-finalize-ownership.md`）。

### 8.7 新增固定机制：技术复核材料

自本轮起，遇到"2–3 次修复仍不收敛 / 涉及 journal·lease·fence·持久化·崩溃恢复 / 需改核心架构或协议 / 影响 2026 / 出现不明崩溃或状态不一致"的问题，一律在 `docs/technical-reviews/` 下建独立 Markdown，内容含：摘要与严重度、版本环境、复现步骤、预期与实际、日志与库状态、涉及代码与 diff、已试方案及失败原因、根因与不确定性、候选方案与推荐、希望另一位 AI 独立判断的问题。

首个条目：`TR-2026-11-15-01-finalize-save-window.md`。

## 9. RC2：客户端写入闭环 + callout 端到端

### 9.1 服务端能力门（替换原先的版本硬门）

| 位置 | 原判据 | 现判据 |
|---|---|---|
| `Program.cs` `foundationEnabled` | `config.Target != "2026"` 即拒 | `foundationHostSupported`：`2022` / `2026` |
| `Program.cs` `ResolveFoundationWriteScope` | 同上 | 同上（统一调用同一内部方法） |
| `D1WritePilotScope.Matches` | `transport == "pipe" && AcadYear == 2026 && Target == "2026"` | `HasAuthenticatedControlChannel(discovery)`（pipe 传输 **或** 非空 `ControlPipeName`）+ `IsSupportedWriteHost(discovery)`（year ∈ {2022,2026} 且 `Target == year`） |
| `Program.cs` 启动重归一致 `ReconcileRequestLedgerAtStartup` | 同样 pipe/2026 双门 | 直接调用上述两个 `internal static` 方法 |

根因说明：启动重归一致里另有一份"pipe 且 2026"的硬编码，是第二次会话启动即崩 `Stored plugin ownership cannot be reconciled` 的原因；三处已统一。

### 9.2 控制管道健壮性与启动重归一致修复（真实 Bug）

| 现象 | 根因 | 修复 |
|---|---|---|
| 写入已 journal 成功，后续 `job_get` 报 `plugin communication error: Pipe is broken.` | `NamedPipeServerStream.Disconnect()` 会丢弃未冲刷字节，插件写完帧立刻断开，服务端读到断链 | 插件 `ControlPipeListener`：写帧后 `FlushAsync()`；`Disconnect()` 前 `WaitForPipeDrain()`。服务端 `PluginClient.SendPipeAsync`：4 次重试 + 150 ms 间隔（`IsTransientPipeFailure`） |
| 重新打开同一 workspace 的 server 直接退出（`MCP Server closed stdout while waiting for initialize`） | 重归一致只认 MCP 工具名，而持久化的 owner 行记的是插件名 `save_drawing` | `TryValidateGovernedPluginRequest` 增加插件名分支（`Plugin` → `Mcp` 映射），与 `TryCreateReadRequest` 的 MCP 名分支配对 |

### 9.3 AutoCAD 2022 保存路由（新增能力，不是放宽约束）

上游 2026 禁止插件自身保存，因此 2022 把"落盘"做成一条独立、单用途、不改图形的**受管读作业**：

- `shared/Execution/FoundationSaveRequest.cs`：`Mcp=dwg_foundation_save_drawing`、`Plugin=save_drawing`；字段 `output_path`（绝对本地 `.dwg`、禁 `..`、≤1024）、`confirm` / `overwrite_existing` / `allow_repo_output`（布尔）、整体 ≤4096 字符；`Parse` / `Canonical` / `Valid`。
- `server/Tools/FoundationSaveTools.cs`：注册该 MCP 工具（仅当 `BIMWRIGHT_DWG_FOUNDATION_SAVE_TOOL_ENABLED=true`）。
- `BoundPluginExecutionGateway.FoundationPlan.cs` `SubmitFoundationSaveAsync`：要求 Foundation 写 profile + 上述环境开关。
- 三处白名单同步：`IsGovernedReadToolName`、`TryValidateGovernedReadRequest`（MCP 名）、`TryValidateGovernedPluginRequest`（插件名，重归一致用）。
- 插件侧另有独立开关 `BIMWRIGHT_FOUNDATION_SAVE_ENABLED=1`（`--allow-save` 时由客户端注入）同时启用 `save_drawing` 与"finalize 前必须有已完成保存"守卫。

**关键更正**：TR-02 中"Foundation 通用管线不会自动 finalize"的结论是错的。`BoundPluginExecutionGateway.D1.cs` 的 `GetD1WriteJobAsync` 确实会自动 finalize；2022 上的冲突实际是"自动 finalize 被 `write_requires_completed_save` 守卫拒绝"，不是"没有 finalize"。已在 TR-02 追加更正段。

### 9.4 客户端两种 apply 模式（实测）

| 模式 | 行为 | 实测结果 |
|---|---|---|
| 默认 `apply`（`--allow-save` 会话） | 写入 + 验证，**不保存**；明确报告未落盘 | `outcome: committed_verification_pending`、`write_fence_released: false`、`persisted: false`、`next_action: verified_not_persisted: rerun with --save-to …` |
| `apply --save-to <abs.dwg>` | 写入 → 验证 → 保存 → 重查原请求触发 finalize → 释放 fence | `status: completed`、`outcome: committed_verified`、`write_fence_released: true`、`persisted: true`、`saved_path` 存在、`plugin_jobs` 中 `journal_finalized=1`/`fence_released=1`、`plugin_write_fences` 空 |
| fence 被占时的第二次写入 | 拒绝，且以 journal 回读确认"没有新作业" | `status: rejected_before_submission`、`reason: fence_held_by_earlier_unpersisted_write`、`error: the D1 request or drawing risk fence conflicts with an existing operation`、`next_action: no_new_write_queued…` |
| 只读/无 save 会话仍未落盘 | 明确区分 | `persisted: false`、`next_action: memory_write_only…` |

客户端实现要点：`persist()` 单独提交保存请求并落 `requests/<id>.json`（未知结果只对原请求重查，不重放）；`journal_has()` 以只读方式读 journal，作为"是否真的入队"的唯一裁决；`Gateway.__enter__` 的工具目录断言在 `save_enabled` 时追加 `dwg_foundation_save_drawing`，并在 server 环境注入 `BIMWRIGHT_DWG_FOUNDATION_SAVE_TOOL_ENABLED=true`。

### 9.5 DWG 真实落盘验收（重开核验）

| 场景 | 证据 |
|---|---|
| 几何写入 + 保存 + 重开 | `cad2022-test\rc2-b4\drawings\persisted.dwg`（30,358 B）；新会话 `rc2-verify` 重开后 `read --handles 229,22A`：`229` = Line (0,0)→(100,0)、`22A` = Circle center (50,60) r=20，`ok: true` 且带 `entity_digest` |
| callout 端到端（2022 首个真实业务流程） | `cad2022-test\rc2-callout\drawings\callout-persist.dwg`；`callout` op 生成 `text=229`（MText）、`leader=22A`（3 顶点 LEADER）、`dot_boundary=22C`（yellow `color_index=2`, r=0.8）、`dot_fill=22D`（SOLID 关联填充）；`leader.annotation_handle=229`、`annotation_reactor_present=true`、`Dimtad=0`、`gap=0` |
| callout 重开核验 | 新会话 `rc2-callout-verify` 重开后读回四对象：MText 文本保留、Leader 3 顶点 + 关联 229、Circle `color_index=2`、Hatch `color_index=2` 关联 `22C` |

### 9.6 RC2 未完成 / 未验证

1. **保存作业的 `effects` 语义**：`save_drawing` 作业落库为 `effects=none`、`verification=not_checked`（它不属于图形写，但确实改了磁盘）。写作业的守卫只看"存在同 journal、同指纹、带 `saved_path` 的已完成保存行"，因此功能正确；但语义上应区分"文件写"。列为待清理项。
2. **callout 只做了 1 条合成图业务流程**（非路施真实图），未做 35 组批量、未做内框读取/分区排布、未出 CTB PDF。
3. **保存后重启再 finalize** 只在 `rc2-b3` 触发过一次（当时因重归一致缺插件名分支而启动失败，即 9.2 第二个 Bug）；修复后未重跑该跨重启场景。属待补测试。
4. **2026 回归**：本机无 2025/2026，仅编译级 + 代码审查，**明确未运行**。
5. **Git**：仍只有非 Git 检查点，无提交（`F:\AI\MinGit-2.56.1-64-bit.zip` 下载中）。
6. 客户端 `--save-to` 只支持 2022 + `--write` 会话；2026 会话走上游既有 `save` plan 语义，未改动。

## 10. RC3（安全状态 / 恢复 / 客户端稳定性）—— 2026-11-15 晚

### 10.1 本轮定位到的两个真实缺陷

1. **重启后按原 `context_id` 查询会挂起 120 秒**
   - 现象：`dwg_job_get(原 context)` 在重启后不返回；`server.log` 记录 `tools/call` handler 用时 `120470 ms`（首次观测为 `30028 ms`，即客户端 30 s 先超时）。
   - 根因（实测确认）：服务端在启动重归一致时**沿用请求预留时刻的 discovery 快照**（`owner_pid=17812`），而新 CAD 进程 `pid` 已是别的值。`PluginClient.SendAsync` 指向旧管道名，每次 30 s 读超时，`SendPipeAsync` 4 次重试 → 120 s 后 `ok=false`。因此阻塞点不是 finalize 守卫，也不是 `GetJobAsync` 路由，而是**绑定了已死端点**。
   - 修复：在 `GetJobAsync` 入口加"owner 进程变更"守卫——用 `LiveOwnerPid()`（解析当前发布中的 discovery）与请求预留时记录的 `owner_pid` 比较；不一致时**完全不联系插件**，直接由服务端 ledger 返回
     `found=true, state_unavailable=true, recovery_required=true, observed_owner_pid, current_process_pid, error=write_owner_process_changed, next_action=operator_recovery_required…`。
     实测：重启后同一调用 **<1 s** 返回上述结构，不再挂起。
2. **操作员显式恢复对"已保存但未 finalize"的作业不可用**
   - 现象：`job_recovery_resolve` 返回 `job_not_in_recovery_state`。
   - 根因：恢复解析只接受 `phase=unknown / observation=recovery`（"效果未知"）一类；RC3 目标窗口里作业是 `completed/committed/passed` 且**已有严格匹配的保存**，只差 fence 释放，原实现没有这条出口。
   - 修复（`SqlitePluginIntentJournal.ResolveUnknownJob`）：在原有 liveness（`Alive`/`Indeterminate` 仍然拒绝）与 `plugin_sequence` 溢出保护之后，新增分支——当 `phase=completed`、`effects=committed`、`verification=passed`、`write_fence_released=0` 且 `HasCompletedSaveAfterCore(id)` 为真时，执行
     `write_fence_released=1, error_code='fence_released_by_operator_after_save', error_message=<operator_note>` + 删除 `plugin_write_fences` 行，返回 `disposition=applied` 与 `released_risk_keys`。
     关键点：`HasCompletedSaveAfter` 原先内部再取一次 operation lease，而调用方已在 lease 内 → 嵌套 lease 抛出被 dispatcher 吞成 `job_recovery_resolve_failed`；已拆出无 lease 的 `HasCompletedSaveAfterCore` 供内部调用，公共方法语义不变。

### 10.2 实测证据（`cad2022-test\rc3-w1`）

| 步骤 | 证据 |
|---|---|
| 崩溃窗口构造 | `apply` → `verification_pending / committed_verification_pending / fence=false`；`persist()` 保存成功 `saved_path=…\drawings\rc3w.dwg`（30,358 B）；`taskkill 17812 SUCCESS`（不触发 finalize） |
| 崩溃时 journal | `rc3w foundation_apply_plan completed/committed/passed, write_fence_released=0`；`save-d4924a34… save_drawing completed/none/not_checked`；`plugin_write_fences` 仍为 `RC3-W1\DRAWINGS\WORK.DWG → rc3w` |
| 重启后查询 | 立即返回 `{found:true, state_unavailable:true, recovery_required:true, observed_owner_pid:17812, current_process_pid:<新>, job:{phase:Completed, effects:Committed, verification:Passed, write_fence_finalized:false}, error:write_owner_process_changed, next_action:operator_recovery_required…}` |
| 操作员显式恢复 | `rc3_resolve.py` → `{disposition:"applied", released_risk_keys:["PATH:…\WORK.DWG"], job:{…}}` |
| 恢复后 journal | `rc3w`：`write_fence_released=1`，`error_code=fence_released_by_operator_after_save`；`plugin_write_fences` **空** |

保存证据绑定复核（同一 journal 内取行实测）：写作业 `complete_target_json` 与保存作业 `complete_target_json` 的 `database_fingerprint=1f285df0-af5c-bc46-a227-70a52ecc9cfb`、`database_session_id`、`drawing_path` 三项完全一致；保存行 `created_at_utc` 严格晚于写作业 `updated_at_utc`；保存 `result_json` 含 `saved_path`。`HasCompletedSaveAfter` 正是按这三条（指纹 + 路径 + 时间 + `saved_path` 存在）取交集，**不是**"同 journal 内任意保存成功即认账"，因此不同图纸/不同路径/旧保存不会被误认。

### 10.3 仍未完成 / 未验证（诚实标注）

1. 重启后对孤儿请求的 `dwg_job_get` 一律返回 `recovery_required`（即使操作员已 resolve 完成）——服务端不知道插件侧 fence 已释放，标签会偏保守；当前以 journal/`plugin_write_fences` 为权威复核。
2. `save_drawing` 仍记 `effects=none`（TR-03 第 1 问未决），审计语义待清理。
3. 状态语义全量收敛（五种状态 × 客户端/journal/error/next_action）只覆盖了 RC2+RC3 已跑用例，未做表格化全量核对。
4. 保存失败路径、重复 `request_id`、fence 冲突后人工恢复、控制管道断线、状态与 DWG 内容不一致保护 —— 部分在 RC1/RC2 覆盖，RC3 未逐项重跑。
5. 双实例隔离 / 超时取消：沿用 RC1 结论，本轮未重跑。
6. 2026 运行回归未做（本机无 2025/2026）。
7. 客户端 `Gateway.__init__` 会校验 `plugin_sha256`；手工覆盖安装目录 dll 会被正确拒绝（`Installed plugin changed`）——属设计内行为，但说明**测试插件必须走 `install/start` 部署，不能手拷 dll**。
8. Git 仍不可用，仅非 Git 检查点。

### 10.4 RC3 期间的回归确认（2026-11-15 22:0x）

新包 `20261115-140318` 在全新隔离工作区 `cad2022-test\rc3-reg`（`install --cad "F:\Program Files\Autodesk\AutoCAD 2022"` + `start --write --allow-save`）重跑 RC2 一步式：

- `apply --save-to` 成功：`reg-persist.dwg` 30,293 B；
- journal：`rc3-reg-01 foundation_apply_plan completed/committed/passed`，`journal_finalized=1`、`write_fence_released=1`；
- `plugin_write_fences` 为空。

即 RC3 的两处改动（owner 变更守卫、操作员恢复分支）**未破坏** RC2 的写入→保存→finalize 闭环。

---

## 11. RC4（CAD 业务功能与 callout 深化）—— 2026-11-15 23:0x–23:2x

### 11.1 三对象批量 callout 一步式闭环

包 `20261115-150741`（含客户端路径防护），会话 `cad2022-test\rc4-batch2`，`context=30b04d594b734c49aeb64116c0871c99`。

一份 plan 三步 callout（`co1/co2/co3`，各 3 顶点 LEADER + MTEXT + 圆点 + SOLID Hatch），`apply_with_optional_save`：

- `status=completed`、`outcome=committed_verified`、`next_action=none`；
- `persisted=true`、`write_fence_released=true`；
- `saved_path=...\rc4-batch2\drawings\batch-callout.dwg`（**31,989 B**）；
- 创建 12 个 handle `["23B","23C","23E","23F","240","241","242","243","244","245","246","247"]`（3×4 实体）。

重开核验（`rc4-batch2-verify`）：`23B` 为 MText、layer `0`、`color_index=7`、`extents (206,108)-(276,119.05)`、`text="交通安全设施\P批量标注 01"`、`text_height=3.5`、`width=70`、`attachment=BottomLeft`、`actual_width=29.5`，即**落盘后几何与文字均保留**。

### 11.2 真实工程图副本验证（代表性样本）

样本：`D:\360云盘\b北京\c昌平\2023年村村通\施工图\展思门路\展思门路_路施17路面标线大样图.dwg`（710,968 B，只读引用）。
`start --source` 复制为 `cad2022-test\rc4-real\drawings\work.dwg`（同尺寸，原文件未改动）。

流程与实测：`dwg_list_layers` → `dwg_query_entities(entity_type=Line)` 取首条直线起点 +10 作锚点 → 单步 callout（文字“交通安全设施\P迁移验证标注”）→ 一步式保存为 `real-callout.dwg`（374,812 B）。

- journal：`real-co foundation_apply_plan completed/committed/passed`、`write_fence_released=1`、`journal_finalized=1`；`save-d8c2505e… save_drawing completed`；`plugin_write_fences` 空。
- 重开会话 `rc4-real-verify`（`context=9c16f11bdcad4340a6baf01138a4dd66`）回读：`D64A=MText`、`D64B=Leader`、`D64C=Circle(color 2)`、`D64D=Hatch(color 2)`，全部 `ok=true`；原图既有直线 `AE6D` 仍存在（digest `381E6868…`）。
- **证据边界**：本次未做“写入前后全图实体/摘要对比”，因此“原图所有既有实体均未被改动”只有“抽样实体存续”支撑，不作更强断言。

### 11.3 RC4 期间修掉的客户端缺陷（真实风险）

1. 原 `persist()` 先 `Path(save_to).resolve()` 再校验，`..` 被静默折叠为合法绝对路径，实测把文件写到工作区外（`F:\AI\cad-agent-foundation-acad2022\escape.dwg`）。现改为**解析前**用 `PureWindowsPath(...).parts` 检查 `..`，命中即 `RuntimeError('Save target must not contain parent-directory segments')`；实测拒绝（回归用例 `[E1]`）。
2. `overwrite_existing` 原硬编码 `True`。现改为默认 `False`：目标已存在且未显式 `--overwrite` 时报 `Save target already exists; rerun with --overwrite to replace it: <path>`，防止误覆盖真实图纸。
3. `query()` 只接受 request 记录，传 request-id 字符串时会在异常分支 `request['last_query_error']=...` 上抛 `TypeError: 'str' object does not support item assignment`。现支持传 `request_id` 字符串（自动读回 `requests/<id>.json`），并在入口 fail clean（`Unknown request record; pass a request ID that this workspace submitted`）。

### 11.4 RC4 未覆盖（诚实标注）

- 不同对象类型/文字长度/多引线组合的压力样本；对象间关联的“同一标注组”语义校验仅靠 `leader.annotation_handle==text`。
- 部分失败后的可恢复路径（一次 plan 内某步失败）未专门构造。
- 连续多轮写入（>10 次）后的稳定性、以及跨 XREF/块参照场景未测。
- 出图（布局/CTB/PDF/比例可读性）**未接入**：`FoundationDrawingPlan` 的 op 白名单无 `plot_pdf`/`page_setup`，服务端也没有独立打印工具；`plugin-acad26\FoundationPrinting.cs` 与 `shared\Execution\FoundationPrintContract.cs` 虽在仓库内，但未在 2022 路由开放，故无任何打印实测。
- 不同 DPI/打印样式下的可读性、字体替换行为未验证。

---

## 12. RC5（工程化、部署与交付）—— 2026-11-15 23:2x–23:4x

### 12.1 自动化回归入口（新增，已跑通）

`cad2022-test\run_regression.py`：一个工作区、一次 CAD 启动内完成 13 项自动检查，输出 `regression-report.json`。

实测（包 `20261115-151649`，工作区 `cad2022-test\regress`）：**13 pass / 0 fail**。

覆盖项：`install`、`start(discovery)`、`context-bound`、`read-only.list_layers`、`write.verified-not-persisted`（`committed_verification_pending/inspect_existing_handles/persisted=false`）、`fence.rejected-second-write`（`fence_held_by_earlier_unpersisted_write`）、`fence.no-ghost-job`（journal 无该 request）、`save.persisted`、`save.finalized`（`committed_verified/True`）、`idempotent.requery`、`callout.write+save`（4 实体）、`cancel.already-terminal`、`callout.four-entities`（MText/Leader/Circle color 2/Hatch）。

报告内显式列出**未自动化的人工检查**：图形观感、PDF/CTB 出图。

### 12.2 调试痕迹清理与重建

- 删除 `BoundPluginExecutionGateway.D1.cs` 的全部 `[d1-trace]` stderr 埋点（6 处）。
- `SqlitePluginIntentJournal.ResolveUnknownJob` 的拒绝错误串由 `job_not_in_recovery_state:phase=…,effects=…,verification=…,released=…,afterSave=…` 收敛为 `job_not_in_recovery_state`。
- 重建：`plugin-acad22` exit 0（0 error）、`server` exit 0（0 error）；新插件 DLL SHA256 `E3864ADF8ECA925D6C0E103DF5593F3FBC7C09F326F43116AA135C638C9BF438`。
- 重新打包 `20261115-151649`（sha256 `D24F5037…`，233 文件 / 45 依赖包），上述 13 项回归即在该包上通过。

### 12.3 部署/卸载/回退文档

新增 `docs\ACAD2022-DEPLOY.md`（10 节）：交付物构成、环境变量、构建、打包、隔离安装启动、日常命令与状态语义表、故障恢复（含 `job_recovery_resolve`）、回归测试、卸载与回退、已知限制。

### 12.4 AutoCAD 2026 兼容性（区分三种结论）

- **2026 编译**：失败，唯一错误 `AutoCAD 2026 managed assemblies were not found under 'C:\Program Files\Autodesk\AutoCAD 2026'`（3 警告 1 错误），属**缺引用程序集**，非代码缺陷；日志 `build-logs\rc5-acad26.log`。
- **2026 静态兼容审查**：本轮改动集中在 `shared\` / `server\Execution\` / `plugin-journal-net8\` 与 client，均为 2026 共享层；server（net8，含共享 journal）编译 0 error 可作为共享层静态证据。`plugin-acad22` 同时以 `ACAD2022;ACAD2026` 宏编译通过，说明 2022 专属分支未破坏 2026 宏路径的编译面。
- **2026 运行测试**：**未做**，本机无 AutoCAD 2025/2026 运行环境。

### 12.5 Git 与检查点

- 本机仍无 `git`（PATH 无、winget 静默安装卡死、MinGit 直连下载失败），本轮按授权**不消耗更多时间**。
- 改为 SHA256 源码检查点：`_checkpoints\rc3-20261115-220602\`（`manifest.tsv` 55,314 B / 408 文件 + `src.tar.gz` 3,592,979 B）；V1.0 前再生成一次验收检查点（见第 13 节）。
- **未伪造任何上游提交历史**，也未推送远程。

### 12.6 RC5 未完成（诚实标注）

- 无自动化“崩溃后恢复”用例（RC3 的崩溃窗口验证仍是人工/脚本单次执行，未纳入 `run_regression.py`）。
- 无安装器/静默部署脚本；当前为 `install`+`start` 两条命令的脚本化部署。
- 2026 运行验证缺失；`plugin-acad26` 无法在本机编译。

---

## 13. ACAD2022-V1.0 候选版判定（2026-11-15 23:4x）

### 13.1 判定结论

**ACAD2022-V1.0 候选版成立，但仅限“内部开发/验证可用”，不构成生产放行**。
理由：2022 的构建、安装、启动、读写、保存、finalize、崩溃恢复、批量 callout、真实图副本均有用运行证据的闭环；但出图能力、2026 运行时、长时连续写入仍属未验证，且 `save_drawing.effects=none` 的审计语义未定。

### 13.2 对应任务书验收条件

| 任务书条件 | 状态 | 证据 |
| --- | --- | --- |
| RC3：客户端能准确报告真实状态 | 达成 | 状态矩阵 A–F（`rc3-matrix\matrix-results.json`）+ 回归 `write.verified-not-persisted` / `save.finalized` |
| RC3：写入与保存结果可核验 | 达成 | 指纹+路径+时间+saved_path 四条件绑定；`rc3-w1` journal |
| RC3：异常恢复不丢/不重/不虚报 | 达成 | 重启 <1 s 返回 `recovery_required`；`operator_recovery_required`；保存失败 `save_failed` + fence 持有 |
| RC4：多对象 callout | 达成 | `rc4-batch2`：3 callout / 12 handle / 重开核验 |
| RC4：代表性工程 DWG 副本 | 达成 | `rc4-real`：710,968 B 原件只读复制 → 落盘 374,812 B → 重开 4 实体 + 原实体存续 |
| RC4：保存后重开检查 | 达成 | `rc4-batch2-verify`、`rc4-real-verify` |
| RC4：出图能力验证 | **未达成（能力未接入）** | 2022 plan 契约（`FoundationDrawingPlan`）无 `plot_pdf`/`page_setup` 步骤；服务端工具目录无打印/PDF 工具；仅客户端 `plot-style`（CTB 文件级改写，不动 DWG）。不是“没测”，是“这条路由还没开通” |
| RC5：可重复构建 | 达成 | `plugin-acad22` 0 error、`server` 0 error；打包 `20261115-151649` |
| RC5：版本化部署 + 启动诊断 | 达成 | 版本化 `Plugins\CadFoundation2022-v1-<bundle12>`；`install/start/doctor` |
| RC5：至少一条完整读写闭环 + 主要回归 | 达成 | `run_regression.py` 13/13 pass |
| RC5：2026 编译通过 | **未达成（缺环境）** | 缺 `C:\Program Files\Autodesk\AutoCAD 2026` 程序集 |
| RC5：2026 运行测试 | **未达成（缺环境）** | 本机无 2025/2026 |
| RC5：Git 版本历史 | **未达成（工具缺失）** | SHA256 检查点替代，未伪造历史 |

### 13.3 交付文件位置

- 运行时包：`src\cad-agent-foundation-main\artifacts\packages\20261115-151649\`（+ 同名 `.zip`）
- 源码检查点：`_checkpoints\rc3-20261115-232238\`
- 部署/使用/恢复：`docs\ACAD2022-DEPLOY.md`
- 权威进度：本文件第 1–13 节；测试日志 `docs\ACAD2022-RC1/RC2/RC3-TEST-LOG.md`
- 接续状态：`docs\ACAD2022-AUTONOMOUS-RUN.md`
- 自动回归：`cad2022-test\run_regression.py`（报告 `cad2022-test\regress\regression-report.json`）
- 真实图证据：`cad2022-test\rc4-real\drawings\real-callout.dwg`、`cad2022-test\rc4-batch2\drawings\batch-callout.dwg`

### 13.4 若继续推进，建议顺序

1. 先确认代码是否具备打印/CTB/PDF 能力；具备则在隔离环境做一次模型+布局出图实测（需可复制的 CTB，不动物配置）。
2. 把崩溃恢复与双实例隔离纳入 `run_regression.py`。
3. 定 `save_drawing.effects` 审计语义（TR-03 第 1 问）；若改为 `committed`，需同步 finalize 守卫与保存证据绑定。
4. 有 AutoCAD 2026 环境后补编译 + 运行回归。
5. Git 可用后，用检查点建立真实基线提交（仍不推远程）。


