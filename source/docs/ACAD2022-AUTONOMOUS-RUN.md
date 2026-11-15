# ACAD2022 长程自主开发 — 运行状态文件

> 目的：任何一位接手者（人或其他 AI）只读本文件即可知道"做到哪、怎么继续"。
> 权威细节在 `docs/ACAD2022-MIGRATION-STATUS.md`（第 1–13 节）、`docs/ACAD2022-RC1/RC2/RC3-TEST-LOG.md`、`docs/ACAD2022-DEPLOY.md`、`docs/technical-reviews/`。

最后更新：2026-11-15 23:4x（Asia/Shanghai）
当前阶段：**ACAD2022-V1.0 候选版（RC3/RC4/RC5 本轮全部推进，剩余为明确未验证项）**
当前交付包：`artifacts/packages/20261115-151649`（sha256 `D24F5037D8D200CB241AF838F1BEEC4FDFACAE8F52C25F3E2CC25B5C731C7CC3`）
当前插件 DLL sha256：`E3864ADF8ECA925D6C0E103DF5593F3FBC7C09F326F43116AA135C638C9BF438`
源码检查点：`_checkpoints/rc3-20261115-232238`（409 文件，`manifest.tsv` 55,409 B + `src.tar.gz` 3,600,510 B）

## 1. 一句话现状

AutoCAD 2022（net48）的写链路（写入 → 验证 → 保存 → finalize）已闭环；崩溃窗口不自动 finalize、查询秒级返回、需操作员显式 resolve；批量 callout 与真实工程图副本均落盘并重开核验；13 项自动回归全绿；部署/卸载/回退文档齐全。**未验证**：出图/PDF/CTB、AutoCAD 2026 运行、真实 10+ 轮连续写入、崩溃恢复自动化。

## 2. 已通过验收

| 节点 | 结论 | 证据 |
|---|---|---|
| RC1 | 通过（五项） | `docs/ACAD2022-RC1-TEST-LOG.md` |
| RC2 | 通过（用户认可） | `docs/ACAD2022-RC2-TEST-LOG.md`、`cad2022-test/rc2-*` |
| RC3 | 通过（核心 + 回归） | `docs/ACAD2022-RC3-TEST-LOG.md`、`cad2022-test/rc3-w1`、`rc3-matrix` |
| RC4 | 通过（批量 + 真实图副本） | `cad2022-test/rc4-batch2`、`rc4-real`、`rc4-real-verify` |
| RC5 | 通过（构建/打包/回归/文档） | `cad2022-test/regress/regression-report.json`（13/13） |

RC3/RC4/RC5 关键实测（详见 STATUS 第 10–13 节）：
1. 崩溃后查询 `<1 s` 返回 `recovery_required`（`write_owner_process_changed`），操作员 `job_recovery_resolve` → `applied` + fence 释放。
2. 三对象批量 callout：`committed_verified`，`batch-callout.dwg` 31,989 B，12 个 handle，重开 `23B` 文字/几何一致。
3. 真实工程图副本（710,968 B 原件只读复制）：callout 落盘 `real-callout.dwg` 374,812 B，重开 `D64A~D64D`（MText/Leader/Circle color 2/Hatch）齐全，原图直线 `AE6D` 存续。
4. 保存失败路径：`save_phase=failed`、`persisted=false`、`save_failed`、fence 仍持有；改存有效路径后 `committed_verified` + fence 释放。
5. 客户端防护：`..` 路径拒绝；已存在目标默认拒绝覆盖（需 `--overwrite`）；`query(request_id)` 字符串输入不再崩溃。

## 3. 未完成 / 下一步（按优先级）

P1：
- 出图能力（模型/布局、CTB、PDF、不同比例可读性）**零验证**，且未纳入自动回归；需先确认代码是否具备该能力。
- `save_drawing` 记 `effects=none` 的审计语义（TR-03 第 1 问）仍未定。
- `--save-to` 允许任意绝对路径（客户端只拦 `..` 与默认拒绝覆盖）；是否限制到会话 `drawings/` 属未决产品决策。

P2：
- 状态语义全量收敛表（五状态 × client/journal/error/next_action）。
- 崩溃恢复 / 双实例隔离纳入 `run_regression.py`。
- 连续多轮写入（>10 次）稳定性、XREF/块参照场景。
- 版本宏与能力宏拆分（`plugin-acad22` 仍借 `ACAD2026` 宏）。
- 重启后即使已 resolve，`dwg_job_get` 仍报偏保守的 `recovery_required`（权威状态在 journal）。

外部阻塞：
- Git 不可用（PATH 无 git、winget 静默卡死、MinGit 直连 `curl 56`）；本轮按授权不再耗时，保持 SHA256 检查点。
- 本机无 AutoCAD 2025/2026：`plugin-acad26` 编译报缺引用程序集（非代码缺陷），2026 运行验证无法进行。

## 4. 构建 / 部署 / 测试（可直接照抄）

```
# 编译
dotnet build <fork>\src\plugin-acad22\Bimwright.Dwg.Plugin.Acad22.csproj -c Release
dotnet build <fork>\src\server\Bimwright.Dwg.Server.csproj -c Release
# 打包
python <main>\scripts\package-runtime.py --acad-year 2022 --version 1.3.0-acad2022.local
# 部署（必须走 client，手拷 dll 会被 manifest 校验拒绝）
python <pkg>\client\foundation.py install --workspace <ws> --acad-year 2022 --cad "F:\Program Files\Autodesk\AutoCAD 2022"
python <pkg>\client\foundation.py start   --workspace <ws> --acad-year 2022 --write --allow-save --cad "F:\Program Files\Autodesk\AutoCAD 2022" --source <dwg> --headless
# 回归（13 项，写 regression-report.json）
python cad2022-test\run_regression.py --package <pkg> --workspace cad2022-test\regress
```

插件必须部署到 `F:\Program Files\Autodesk\AutoCAD 2022\Plugins\CadFoundation2022-*`（受信任区，SECURELOAD 保持开启）。
测试前先 `Get-Process acad`，**只终止非 12948 的实例**（12948 是生产图纸实例，禁止 save/绑定/写入）。

## 5. 关键约束（安全边界，任何接手者不得放宽）

- 不修改生产图纸、不操作用户正在工作的实例、不关闭 SECURELOAD。
- 不清理数据库 / 不删 fence / 不重置任务来"通过测试"。
- 不把未解决状态的崩溃自动改判为成功。
- 不推送远程 Git 仓库、不伪造提交历史。
- 保存后崩溃：**不自动 finalize**，保留显式、有审计的人工恢复入口。
- 只有 owner 确认死亡才允许释放；不确定一律拒绝。

## 6. 最近一次验证的原始命令

```
# 全量自动回归（13/13）
python cad2022-test\run_regression.py --package artifacts\packages\20261115-151649 --workspace cad2022-test\regress
# 真实工程图副本（写入 + 重开核验）
python cad2022-test\rc4_real.py <ws> <client.py> "<源图>.dwg" "F:\Program Files\Autodesk\AutoCAD 2022"
python cad2022-test\rc4_real_verify.py <ws> <client.py>
# 保存失败路径
python cad2022-test\rc3_savefail.py cad2022-test\rc3-savefail <client.py>
# 崩溃窗口 / 操作员恢复（RC3）
python cad2022-test\rc3_window.py crash cad2022-test\rc3-w1 cad2022-test\rc2-plan-write.json rc3w cad2022-test\rc3-w1\drawings\rc3w.dwg
python cad2022-test\rc3_resolve.py cad2022-test\rc3-w1\native\Dwg\acad-2022.json rc3w
python cad2022-test\dump_journal.py cad2022-test\rc3-w1\native\Dwg\execution\plugin-execution.sqlite
```
