# ACAD2022-RC1 实测日志（2026-11-15）

本文件是 RC1 验收的证据索引，按"命令 → 原始输出 → 结论"记录。所有测试只在隔离实例（独立 `BIMWRIGHT_DWG_ROOT`、独立 `acad.exe` 进程）中进行；生产实例 PID 12948 全程未触碰。

## 0. 环境与产物

| 项 | 值 |
|---|---|
| AutoCAD | `F:\Program Files\Autodesk\AutoCAD 2022\acad.exe` 24.1.173.0.0（R24.1） |
| 部署目录 | `F:\Program Files\Autodesk\AutoCAD 2022\Plugins\CadFoundation2022-c4` |
| 部署 dll | `Bimwright.Dwg.Plugin.Acad22.dll` 1,028,608 B，2026-11-15 19:53:37，与源哈希一致 |
| 编译 | `dotnet build ...\plugin-acad22\Bimwright.Dwg.Plugin.Acad22.csproj -c Release` → **0 error / 100 warning（全 CS8632）** |
| 构建日志 | `build-logs\rc1-f.log` |
| 环境变量 | `BIMWRIGHT_DWG_ROOT=<run>\native`、`BIMWRIGHT_FOUNDATION_WRITE_ENABLED=1`、`BIMWRIGHT_FOUNDATION_SAVE_ENABLED=1` |

启动方式（隔离范式）：`acad.exe [/b load.scr]`，`.scr` 内 `FILEDIA=0`、`LOGFILEPATH`、`_.NETLOAD <8.3 短路径>`；`ProcessStartInfo.EnvironmentVariables` 注入开关；`WindowStyle=Hidden`。

探针：`cad2022-test\rc1_tx.py`（`w/f/s/wq/rq/cancel/rburst/dump/handles/recheck`）、`rc1_dual.py`、`rc1_recover.py`、`rc1_fence.py`。

## 1. 编译

```
$ dotnet build ...Acad22.csproj -c Release
    100 个警告
    0 个错误
已用时间 00:00:02.69
```

结论：编译通过（100 条全部 CS8632 可空注解告警，非空引用风险）。

## 2. 崩溃恢复 A（finalize 后崩溃 + fence 冲突留痕）

```
# run-rc1-a
rc1-a1 杀前: phase=completed effects=committed verification=passed rel=0
重启后:      rc1-a1 状态原样保留（不重新判为失败）
提交 rc1-a2（fence 仍被 a1 持有）:
    ok=False error=write_fence_conflict
journal: rc1-a2 phase=failed err=write_fence_conflict rel=1
plugin_write_fences: 无新增行
```

## 3. 崩溃恢复 B（排队期 owner 死亡）

```
# run-rc1-b
rc1-b1 杀前: phase=queued effects=none（fence 持有）
重启后:      phase=failed effects=none observation=recovery
             error_code=owner_lost_before_start rel=1
plugin_write_fences: 清空
随后 rc1-b2: completed/committed
```

## 4. 崩溃恢复 D（运行中被杀 + 人工解除）

```
# run-rc1-d
rc1-d1 杀前: phase=running effects=unknown
重启后:      phase=unknown effects=unknown observation=recovery，fence 继续持有
rc1-d2 提交: ok=False error=write_fence_conflict
job_recovery_resolve{confirm:true, operator_note:"..."}:
    disposition=applied
    released_risk_keys=["PATH:C:\USERS\CG524\APPDATA\LOCAL\AUTODESK\AUTOCAD 2022\R24.1\CHS\TEMPLATE\ACADISO.DWT"]
作业: failed / abandoned_after_recovery，effects 仍 unknown
rc1-d3: committed（解除后可继续写）
负向: 无 confirm → confirm_required；对 completed 解除 → job_not_in_recovery_state；
      不存在 → job_not_found；已完成 cancel → already_terminal；未知 id cancel → not_found
```

## 5. 事务语义（修复前基线）

```
# run-tx3：finalize 后不保存直接崩溃
tx3-w1: completed eff=committed ver=passed rel=1 fin=1；fences (none)
重启后: tx3-w2 直接写入成功并提交
=> 该写入已"完成"，但从未落盘；静默丢失

# run-tx5：保存到不存在目录
tx5-w1: rel=1 fin=1；save 作业 failed save_drawing_failed；无文件

# run-tx4：finalize → 再写入 → 保存
tx4-w1 rel=1 fin=1；tx4-w2 completed rel=0 fin=0；tx4.dwg 30,101 B
```

## 6. 事务语义（修复后验证）

```
# run-tx8
[1] 未保存 finalize -> ok=False err=write_requires_completed_save
[2] save            -> completed，29975 B
[3] 保存后 finalize  -> ok=False err=write_requires_completed_save   ← 口径缺陷（序列号比较）
                    （改时间戳口径后见 TX9）

# run-tx9
[1] 未保存 finalize -> ok=False err=write_requires_completed_save
[2] save            -> completed，文件 30002 B
[3] 保存后 finalize  -> ok=True disp=applied
dump: tx9-w1 phase=completed eff=committed ver=passed rel=1 fin=1；fences (none)
```

## 6.1 判据严格化：只认同一图纸的保存（TX10 / TX12）

```
# run-tx10（回归）
未保存 finalize -> write_requires_completed_save
save            -> completed，29975 B
保存后 finalize  -> applied；tx10-w1 rel=1 fin=1；fences (none)

# run-tx11（注入：删掉真实保存行，只留指纹被改成全零的伪造行）
tx12-w1        completed fp=1d5750a6-94fe-d24d-a5bd-0ddfb927c9d0
fake-save-bad  completed fp=00000000-0000-0000-0000-000000000000（时间戳更晚、带 saved_path）
finalize tx12-w1 -> ok=False err=write_requires_completed_save
真实保存 tx12-save2 -> finalize applied；fences (none)
```

判据：`tool_name='save_drawing' AND phase='completed' AND created_at_utc>写作业.updated_at_utc AND database_fingerprint=写作业指纹 AND result_json 含 saved_path`；写作业缺指纹 → fail closed。

## 6.2 双目标编译

```
net48：0 error / 118 warning（全部 CS8632）
net8 （plugin-journal-net8）：0 error / 0 warning
```

net8 工程此前缺 `NetFx.cs` 链接，已补入 csproj（`shared\Execution\NetFx.cs`），并把 `NetFx`/`ReferenceComparer` 的可空标注调整为两种目标都可编译。

## 7. 真正落盘核验（新进程重开 DWG）

```
# run-tx6 / run-tx8，保存后用另一实例打开 DWG 读实体
tx-recheck: phase=completed
result: {"entities":[{"handle":"222","ok":true,
        "entity":{"handle":"222","type":"Line","layer":"0","color_index":7},
        "entity_digest":"A27B2B50F995042C9791F778E4A95646047B84DA4B03F4965F87036168288A6D"}]}
```

```powershell
PS> (Get-Item "run-tx9\dwg\tx9.dwg").Length
30002
```

## 8. 双实例隔离

```
# run-dual-a / run-dual-b 同时在线（rc1_dual.py）
acad_year            同   A=2022            B=2022
transport            同   A=tcp             B=tcp
port                 不同 A=1766            B=3460
pipe_name            同   A=None            B=None
control_pipe_name    不同 A=BimwrightDwg-2022-30852-control-2222dad7…
                        B=BimwrightDwg-2022-21336-control-145dc7f5…
pid                  不同 A=30852           B=21336
auth_token           不同 A=5845f11aa2c9…   B=38f974b735e9…

A bind ok=True  scope=ca3244348ef84d6b  path_digest=CC3F42FFA3486975
B bind ok=True  scope=44b203c7b8d44df4  path_digest=62A4F1C60CCE315B

A 用 B 的 token（TCP）            -> unauthorized
A 控制管道用 B 的 token           -> unauthorized
B 控制管道用 A 的 token           -> unauthorized
B 用 A 的 context_id 提交         -> invalid_document_context
dual-a1 -> completed effects=committed ver=passed
dual-b1 -> completed effects=committed ver=passed
A 查 dual-b1 -> found=False
B 查 dual-a1 -> found=False
[杀掉 A 后] B dump 正常、作业查询正常
```

## 9. 取消语义

```
# run-cancel（单写作业，太快完成）
cancel cx-w1 -> already_terminal phase=completed
cx-w2 -> failed write_fence_conflict（fence 仍被 cx-w1 持有，符合预期）
cancel cx-nope -> not_found

# run-cancel3（连发 8 个只读后取消第 8 个）
cb-1..cb-7 queued -> completed
cb-8 queued -> cancel -> disposition=cancelled, phase=cancelled, rel=1
fences: (none)
```

## 10. 未验证项（如实标注）

| 项 | 状态 | 原因 |
|---|---|---|
| 运行中取消（`running` 阶段 cancel） | **未验证** | 单次写入执行过快，无法抢在 running 窗口内发 cancel |
| 服务端队列超时 | **不存在** | `queue_deadline_utc` 列存在但 dispatcher 未设置；当前只能客户端超时 |
| 崩溃在 save 之后、finalize 之前 | **已知残余** | DWG 已落盘、fence 仍持有；需再 finalize 释放。是否自动 finalize 见 TR-2026-11-15-01 |
| CAD 2026 运行回归 | **未验证** | 本机无 2025/2026，仅编译级 + 代码审查 |
| net48 控制管道 ACL 跨用户拒绝 | **未验证** | 只验证了错 token 拒绝，未做跨用户会话实测 |
| 同 root 双实例并发写入 | **未验证** | 上游设计允许，靠 fence 串行；被挡时返回显式 `write_fence_conflict` |
