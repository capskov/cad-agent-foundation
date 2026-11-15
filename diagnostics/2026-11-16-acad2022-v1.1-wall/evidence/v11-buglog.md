# ACAD2022-V1.1 Bug 与修复记录

## BUG-01 callout 创建不支持 `text_layer` / `annotation_offset`

- 现象：创建 callout 时只能落在引线层，无法指定文字层；C 在创建后塌到文字插入点，需要二次 `set_callout` 修补，批量场景下非常脆弱。
- 修复：`FoundationPlanWriteHandler.Add()` 增加 layerOverride；callout 创建时按 `text_layer` 建/用文字层，设置顶点与文字位置后写 `annotation_offset` 并 `EvaluateLeader()`；计划校验与 `VerifyReceipt` 同步校验 text_layer/offset/dimtad/has_hook_line。
- 证据：三批 35 组创建后读回 `annotation_offset` 与 `C − 插入点` 一致（35/35）。

## BUG-02 `annotation_offset` 传 3 维数组被计划校验拒绝

- 现象：`apply` 返回 `rejected_before_submission / 2D point required`。
- 原因：计划层 `Point()` 仅接受 2 个分量（除 bulge 外）。
- 修复：计划统一输出 2 维偏移（Z 由代码补 0）；35 个 step 全部修正。
- 证据：修正后第一批正常提交。

## BUG-03 net8 服务端计划校验器未同步共享 schema

- 现象：插件已支持新字段，但 `apply` 被 `unexpected or missing plan fields` 拒绝。
- 原因：只重编了 2022 插件，`server/Bimwright.Dwg.Server.exe`（net8）仍用旧版 `FoundationDrawingPlan` 校验。
- 修复：`dotnet build server\Bimwright.Dwg.Server.csproj -c Release` 后重打包（20261115-170238）。
- 证据：同一计划换包后正常提交。

## BUG-04 `Number(step,"gap")` 编译错误

- 现象：共享源码编译失败（`Number` 只接受 JToken）。
- 修复：改为 `Number(step["gap"])`。
- 证据：`dotnet build` 0 error。

## BUG-05 `--save-to` 到新路径后会话绑定失效

- 现象：保存到 `after-a1.dwg` 后，同一会话的 `status` / 下一次 `apply` 报 `drawing_path_mismatch`。
- 原因：保存后文档路径变更；服务端进程的环境变量 `BIMWRIGHT_DWG_FOUNDATION_EXPECTED_DRAWING_PATH` 与绑定快照仍固定为旧路径。
- 已做：客户端在保存成功后把 `session.json` 的 drawing 更新为 `saved_path`（源码与包内两份）。
- 未根治：绑定快照与服务端环境仍指向旧路径，进程内无法安全改写。当前约束为“保存到新路径后必须用新会话从保存文件继续”，已在报告中列为限制。建议下一轮实现“保存后自动重启服务端并重建绑定”，或提供 `rebind --drawing`。
- 影响：35 组落图分 3 个会话完成，未影响正确性。

## BUG-06 ED04 密集区原方案弯曲/穿字候选

- 现象：初版阶梯架在 ED04 顶部密集区出现 2 组折点钝角不成立、4 条引线穿字候选。
- 处理：调整行序与文字侧别，最终 ED08/ED02 零冲突，ED04 剩 2 条 `leader_text_candidate`（1D7DA↔1EB26、1EB26↔1D811），无文字重叠、无引线交叉。
- 状态：保留为图面人工判读项，未伪称清零。

## 附：P1-A 运行时验证记录

- 计划：`v11-p1a-test\plan-p1a-attach.json`，对 `1EF71`/`1EF70` 强制原生附着（`repair_association=true` + `force_native_attach=true`）。
- 结果：`apply` 提交成功，job `verification=passed`，读回 `annotation_reactor_present=true`、`annotation_handle=1EF70`、offset 保持 `-0.375,-0.375`。
- 说明：证明 2022 上可解析到 `attachAnnotation` 入口并安全执行；失败分支（入口不可用、attach 返回非 0）未构造。
