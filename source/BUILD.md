# 源码快照（供逐文件审查）

本目录是 `ACAD2022 V1.0 候选版` 的**完整源码快照**（AutoCAD 2022 移植分支），与同分支的 `docs/`、`artifacts/` 互为对应：`artifacts/package/20261115-151649.zip` 里的插件 DLL / 服务端二进制，就是由这份源码编译出来的。

## 1. 目录对应关系

```
source/
├── client/            # 客户端 foundation.py（命令行入口）
├── scripts/           # package-runtime.py 等打包脚本
├── docs/              # 源码树内的原始文档（与顶层 docs/ 内容一致）
├── tests/             # 仓库原有测试
└── third_party/bimwright-dwg-mcp/fork/src/     ← 核心源码在这里
    ├── server/            # 服务端（net8），Execution/ 为执行治理核心
    ├── plugin-acad22/     # AutoCAD 2022 插件工程（net48 / x64）
    ├── plugin-acad26/     # AutoCAD 2026 插件工程 + 打印/扩展操作（共享源码）
    ├── plugin-journal-net8/  # journal / fence / 恢复逻辑（net8 + net48 双编译）
    ├── shared/            # 共享契约：计划契约、保存请求、发现协议
    ├── support/           # 只读入口（MCP stdio 客户端桥）
    └── tools/             # 辅助工具
```

## 2. 审查建议的入口文件（按风险排序）

| 主题 | 文件 | 看点 |
| --- | --- | --- |
| 重启后不联系新进程 | `server/Execution/BoundPluginExecutionGateway.cs`（`GetJobAsync`）| `LiveOwnerPid()` 与 `StoredOwnerPid` 比对，不一致时完全绕过插件，返回 `recovery_required` |
| 写路由 owner 守卫 | `server/Execution/BoundPluginExecutionGateway.D1.cs`（`GetD1WriteJobAsync`）| 守卫在 `CaptureD1LedgerSequence` 之前；`D1Pending` 结构 |
| 操作员恢复出口 | `plugin-journal-net8/SqlitePluginIntentJournal.cs`（`ResolveUnknownJob`）| 仅允许 completed+committed+passed+`write_fence_released=0`+`HasCompletedSaveAfter` 为真时释放；`HasCompletedSaveAfterCore` 拆出以避开非可重入 lease |
| 保存证据绑定 | 同文件 `HasCompletedSaveAfter` | 指纹、`drawing_path`、时间严格晚于写作业、`saved_path` 非空，四条件全满足才认账 |
| 2022 保存契约 | `shared/Execution/FoundationSaveRequest.cs` | 键白名单、长度、绝对路径、拒 `..` |
| 保存路由注册 | `server/Tools/FoundationSaveTools.cs`、`shared/Infrastructure/CommandDispatcher.cs` | 白名单、finalize 前必须有已完成保存 |
| 客户端不确定性语义 | `client/foundation.py`（`apply` / `persist` / `apply_with_optional_save` / `query`）| 提交前落盘 request 记录；不确定时只查询不重放；`..` 与已存在目标保护 |
| 2022 插件适配 | `plugin-acad22/**`、`plugin-acad22/NetFx.cs` | BCL 垫片、SQLite 预加载、`System.Memory` 绑定兜底、`IPluginExecutionPump` |

## 3. 复现编译

```powershell
$src='source\third_party\bimwright-dwg-mcp\fork\src'
dotnet build "$src\plugin-acad22\Bimwright.Dwg.Plugin.Acad22.csproj" -c Release   # 期望 0 error
dotnet build "$src\server\Bimwright.Dwg.Server.csproj" -c Release                 # 期望 0 error
```

- 2022 插件目标框架 `net48` / `x64`，工程宏 `ACAD2022;ACAD2026`。
- 产物 `plugin-acad22\bin\Release\net48\Bimwright.Dwg.Plugin.Acad22.dll`，其 SHA256 应为
  `E3864ADF8ECA925D6C0E103DF5593F3FBC7C09F326F43116AA135C638C9BF438`（见 `../artifacts/plugin-sha256.txt`）。
- `plugin-acad26` 在本机**无法编译**：缺 `C:\Program Files\Autodesk\AutoCAD 2026` 引用程序集（环境限制，不是代码缺陷）。
- 需要 .NET SDK 8（服务端）与 MSBuild/VS 的 .NET Framework 4.8 目标包（插件）。

## 4. 未包含的内容与原因

- `bin/ obj/ artifacts/ native/ logs/ requests/ drawings/`：构建产物与运行数据，体积大且不可复现，不入库。
- `*.sqlite`、`*.dwg`、`*.log`、`*.zip`、`*.tar.gz`：测试证据与二进制，改为在顶层 `artifacts/`、`checkpoints/` 记录哈希。
- 运行时包本身（含预编译 DLL）在 `../artifacts/package/20261115-151649.zip`。

## 5. 完整性校验

```bash
cd source && sha256sum -c MANIFEST.sha256 --quiet
```

`MANIFEST.sha256` 是**按本快照实际内容**生成的（410 条，含本说明与自身之外的全部文件）；`../checkpoints/manifest.tsv` 是生成检查点那一刻的原始清单，两者仅在 `docs/` 两份文档上有差异（检查点之后又补充了 RC4/RC5 章节），其余逐字节一致。
