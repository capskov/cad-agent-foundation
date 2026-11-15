# AutoCAD 2022 部署、使用与故障恢复

本文是 `cad-agent-foundation` 在 **AutoCAD 2022（R24.1 / .NET Framework 4.8）** 上的交付说明。
面向对象：下一位开发者、实施人员或接管的 AI。
权威进度与实测日志见 `ACAD2022-MIGRATION-STATUS.md`、`ACAD2022-RC1/RC2/RC3-TEST-LOG.md`。

---

## 1. 交付物构成

| 组成 | 路径（相对工程根 `F:\AI\cad-agent-foundation-acad2022`） | 说明 |
| --- | --- | --- |
| 源码 | `src\cad-agent-foundation-main\` | 服务端 `server\`、插件 `plugin-acad22\`（net48）、共享 `plugin-journal-net8\`、`shared\`、客户端 `client\foundation.py` |
| 运行时包 | `src\cad-agent-foundation-main\artifacts\packages\<时间戳>\` | 由 `scripts\package-runtime.py` 生成，含 `manifest.json`（逐文件 SHA256） |
| 插件 DLL | 包内 `plugin\Bimwright.Dwg.Plugin.Acad22.dll` | net48 / x64，`ACAD2022;ACAD2026` 宏 |
| 客户端 | 包内 `client\foundation.py` | 命令行入口，负责 install/start/apply/save/query/stop |
| 测试脚本 | `cad2022-test\` | `run_regression.py`、`rc3_*.py`、`rc4_*.py` 等 |
| 检查点 | `_checkpoints\<时间戳>\` | `manifest.tsv`（源码 SHA256）+ `src.tar.gz` |

构建产物落在 `src\...\plugin-acad22\bin\Release\net48\` 与 `server\bin\Release\net8.0\`。

---

## 2. 环境要求

- AutoCAD 2022（`F:\Program Files\Autodesk\AutoCAD 2022\acad.exe`，R24.1）。
- .NET Framework 4.8（系统自带）；服务端需 .NET 8 运行时（`dotnet --version` 可用）。
- Windows x64；插件只加载到 2022 进程，不覆盖其它版本安装目录。
- 环境变量（由客户端 `start` 自动设置，手工启动服务端时需自行设置）：

```
BIMWRIGHT_DWG_ROOT=<workspace>
BIMWRIGHT_FOUNDATION_WRITE_ENABLED=1
BIMWRIGHT_FOUNDATION_SAVE_ENABLED=1
BIMWRIGHT_DWG_FOUNDATION_SAVE_TOOL_ENABLED=true
BIMWRIGHT_D1_WRITE_PILOT_ENABLED=1
BIMWRIGHT_H1_WRITE_PILOT_ENABLED=1
BIMWRIGHT_D1_PREFLIGHT_PILOT_ENABLED=1
BIMWRIGHT_H1_PREFLIGHT_PILOT_ENABLED=1
BIMWRIGHT_DWG_ENABLE_FOUNDATION_WRITE=1
```

安全限制保持不变：不关闭 `SECURELOAD`，插件装在 `F:\Program Files\Autodesk\AutoCAD 2022\Plugins\<versioned>\` 受信任区，通过 `start.scr` 的 `NETLOAD` 加载。

---

## 3. 构建

```powershell
$src='F:\AI\cad-agent-foundation-acad2022\src\cad-agent-foundation-main\third_party\bimwright-dwg-mcp\fork\src'
dotnet build "$src\plugin-acad22\Bimwright.Dwg.Plugin.Acad22.csproj" -c Release
dotnet build "$src\server\Bimwright.Dwg.Server.csproj" -c Release
```

两者均应 0 error；`plugin-acad26` 在本机 **无法编译**（缺 `C:\Program Files\Autodesk\AutoCAD 2026` 引用程序集），属环境限制，与代码无关。

## 4. 打包

```powershell
python F:\AI\cad-agent-foundation-acad2022\src\cad-agent-foundation-main\scripts\package-runtime.py `
  --acad-year 2022 --version 1.4.0-acad2022.local
```

输出 `...\artifacts\packages\<时间戳>\` 与同名 `.zip`，打印 `sha256` / `plugin_mvid`。
**不要手改包内文件**：`manifest.json` 逐文件校验，改动会触发 `Package file changed`。

## 5. 安装与启动（隔离工作区）

```powershell
$pkg='...\artifacts\packages\<时间戳>'
$cl="$pkg\client\foundation.py"
$ws='F:\AI\cad-agent-foundation-acad2022\cad2022-test\<会话名>'
python $cl install --workspace $ws --acad-year 2022 --cad 'F:\Program Files\Autodesk\AutoCAD 2022'
python $cl start --workspace $ws --acad-year 2022 --write --allow-save `
  --cad 'F:\Program Files\Autodesk\AutoCAD 2022' --source '<某个 .dwg>' --headless
```

- `install` 创建版本化安装目录 `Plugins\CadFoundation2022-v1-<bundle12>`；同内容重装复用，不同内容拒绝覆盖。
- `start` 把 `--source` 复制为工作区 `drawings\work.dwg`（原文件只读、不改动），以隐藏窗口 `/b start.scr` 启动 CAD 并 `NETLOAD` 插件，写入 discovery 供服务端连接。
- `--write` 打开写入能力；`--allow-save` 才允许插件侧保存（2022 专属）。
- **换包测试必须重新 `install`**：`start.scr` 的 `NETLOAD` 指向安装目录，不重装会加载旧 DLL。

## 6. 日常命令

```powershell
python $cl doctor  --workspace $ws --acad-year 2022          # 绑定状态与插件版本
python $cl read    --workspace $ws --acad-year 2022 --handles '23B,243'
python $cl apply   --workspace $ws --acad-year 2022 --plan plan.json --request-id my-01
python $cl apply   --workspace $ws --acad-year 2022 --plan plan.json --request-id my-01 `
                   --save-to "$ws\drawings\out.dwg" [--overwrite]
python $cl save    --workspace $ws --acad-year 2022 --save-to "$ws\drawings\out.dwg" [--overwrite]
python $cl stop    --workspace $ws --acad-year 2022
```

语义（务必按此判断，不要凭“接口返回成功”下结论）：

| 场景 | 客户端报告 | fence |
| --- | --- | --- |
| 只 `apply`（无 `--save-to`） | `committed_verification_pending`，`next_action=inspect_existing_handles` | 仍持有（设计如此） |
| `apply --save-to` 全链路 | `committed_verified`，`persisted=true`，`next_action=none` | 已释放 |
| 保存后、finalize 前崩溃 | 重启后查询 `recovery_required`，`error=write_owner_process_changed` | 仍持有，需显式恢复 |
| 同一 `request_id` 重发 | 返回同一作业状态（幂等），不会重复写入 | 依原作业 |

保存目标保护：含 `..` 的路径直接拒绝；已存在的 `.dwg` 默认拒绝，需显式 `--overwrite`。

## 7. 故障恢复

1. **文件是否真的落盘**：看 `query` 返回的 `saved_path` 与磁盘文件，再看 journal 中 `save_drawing` 行是否 `completed`。
2. **保存完成但 fence 未释放**（保存后崩溃窗口）：
   ```powershell
   python F:\AI\cad-agent-foundation-acad2022\cad2022-test\rc3_resolve.py <workspace> <request_id>
   ```
   底层控制命令 `job_recovery_resolve {"request_id","confirm":true,"operator_note":"≥4 字符"}`；
   成功返回 `disposition=applied`，journal 记 `error_code=fence_released_by_operator_after_save`。
   仅当“作业 completed+committed+passed、fence 未释放、且在作业之后存在指纹与路径绑定同一的已完成保存”时才允许；其余状态一律拒绝。
3. **owner 进程已更换**：查询会在 1 秒内返回 `recovery_required`（不联系新插件进程），不会挂起；按上一步显式恢复。
4. **journal 身份不匹配**（搬目录后）：`The plugin execution journal identity does not match its configured path.`，旧库不可复用，需在新工作区重建。
5. **DLL 被锁**：部署/换包前先 `stop` 或结束非生产 `acad` 实例。

## 8. 回归测试

```powershell
python F:\AI\cad-agent-foundation-acad2022\cad2022-test\run_regression.py `
  --package '...\artifacts\packages\<时间戳>' --workspace '...\cad2022-test\regress'
```

覆盖：install / start-discovery / 只读调用 / 写入（验证未落盘语义）/ fence 冲突拒绝且无幽灵作业 / 保存与 finalize / 幂等重查 / 批量 callout 落盘 / 已完成作业 cancel / callout 四实体回读。
报告写入 `<workspace>\regression-report.json`（含 `manual_not_automated` 清单：图形观感、PDF/CTB 出图仍未自动化）。

真实工程图验证：`rc4_real.py` + `rc4_real_verify.py`（只对副本操作，原图只读）。

## 9. 卸载与回退

```powershell
python $cl stop --workspace $ws --acad-year 2022
```

1. 结束该工作区对应的 `acad.exe`（先确认不是生产实例）。
2. 删除版本化安装目录 `F:\Program Files\Autodesk\AutoCAD 2022\Plugins\CadFoundation2022-v1-<bundle12>`。
3. 工作区（`drawings\`、`requests\`、`native\Dwg\execution\plugin-execution.sqlite`、`server.log`）整体保留或归档，不再引用即可。
4. 回退到旧版本：把旧包重新 `install` 到同一工作区并 `start`；旧 journal 若与新版本 schema 不兼容，按第 7 节第 4 条在新工作区重建。

## 10. 已知限制

- 2026 运行时未验证（本机无 AutoCAD 2026）；仅共享代码编译与静态审查。
- `save_drawing` 的 `effects` 仍记 `none`，文件副作用靠 `saved_path` 与 journal 时间/指纹绑定表达（TR-03 第 1 问未决）。
- fence 无自动超时；长期持有需操作员显式释放。
- `--save-to` 允许任意绝对路径（客户端只拦 `..` 与默认拒绝覆盖）；是否限制到会话 `drawings\` 属未决产品决策。
- 出图（布局/CTB/PDF）能力尚未验证，且未纳入自动回归。
