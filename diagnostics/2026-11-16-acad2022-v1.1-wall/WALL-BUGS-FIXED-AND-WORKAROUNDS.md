# 砖墙截面任务：我实际“弄了”什么、还剩什么 Bug

先澄清：本轮**没有改插件 C# 代码**。所谓“弄了”，是三类动作：①按底座既有契约重写绘图计划；②用客户端/工作区流程绕过保存链路缺陷；③把新发现写成可审查的诊断项。下面逐条对应。

## A. 已解决并能拿出证据的

### A-1 步骤 ID 不能含中文 → 改用 ASCII 计划 ID
- 现象：`layer-图框` 等 ID 被 `step IDs must be unique identifiers` 整批拒绝。
- 处理：图层显示名仍为中文（图框/墙体/填充/标注-引线/标注-文字/尺寸/地面），计划 ID 改为 `layer-01..07`；规则来自 `FoundationDrawingPlan.cs:43` 的 `^[A-Za-z][A-Za-z0-9_-]*$`。
- 证据：`plan-01-frame.json`；首批 15 步提交 `verification=passed`。

### A-2 单计划 16 步上限 → 拆成 4 批，且保证填充边界不跨批
- 现象：39 步计划被 `1..16 steps required` 拒绝。
- 处理：拆为 15 / 9 / 14 / 6 步；`hatch.boundary` 必须引用**同一计划内**更早的闭合 polyline，所以墙体/基础/压顶与各自 hatch 放在同一批；土体 hatch 与地面线放在第三批。
- 证据：四份计划 + 第一批回执（15 步、创建 1BA–1BF）。

### A-3 空白图单位必须是 unitless/米，否则 PDF 出图会被拒
- 处理：选 `acad.dwt`（INSUNITS=0），按米建模：A3 图框 8.4×5.94 m，1:20 出图正好 420×297 mm；没有用 `acadISO.dwt`（毫米，`plot_pdf` 要求 INSUNITS∈{0,6}）。
- 证据：`environment.txt`、`plan-01-frame.json`（外框 8.4×5.94）。

### A-4 原生尺寸标注能力可用 → 建样式并落 6 道尺寸
- 处理：`text_style` = `WALL_TXT`（txt.shx + hztxt.shx，字高 0.045/0.05）；`dimension_style` = `WALL_DIM`（text_height 0.045、arrow 0.04、gap 0.015、decimal 0）；`linear_dimension` 用 rotation 0/90 分别测水平/垂直：2400 墙高、240 墙厚、800 基础宽、370 放脚宽、300 埋深、60 压顶厚。
- 证据：`plan-03-ground-dim.json`；第三批 `verification=passed`（但保存前进程退出，见 C-3）。

### A-5 引线标注参数按 V1.1 的经验预置
- 处理：callout 明确 `text_layer`、`annotation_offset`(2 维)、`dimtad=0`、`has_hook_line=false`、BottomRight、圆点 r=0.0125、文字层/引线层分离；6 组文字（240 砖墙、20 抹面、C25 压顶、370 放脚、C15 基础、室外地面）。
- 状态：**计划完成，未落图**（第四批未执行）。

### A-6 保存链路：找到了“同一次调用必须带 --save-to”的正确姿势
- 现象：不带 save_to 的 apply 会留下 held fence；随后任何 apply 都被挡。
- 处理：改为 `apply --plan <批> --save-to <dwg>` 一次完成提交+保存；保存成功后换新工作区、从保存文件继续（因为 BUG-05 仍在）。
- 证据：第 1 批 → wall-section.dwg 31,246 B；第 2 批 → s2.dwg 32,021 B。

## B. 已定位但没修（属于底座 Bug，不是画图问题）

### B-1 保存成功却报 `Unknown request record`
- 现象：save 作业实际写出 `saved_path/size/sha256`，CLI 仍报 Unknown request record，退出码 1。
- 根因判断：save 用新的 `save-<uuid>` 请求，finalize 又用原写请求；两者 context/记录归属不一致。
- 影响：脚本会把“已保存”当失败；第 2 批的 s2.dwg 就是这样“报错但存在”。

### B-2 `drawing_path_mismatch` 未根治
- 现象：保存到新路径后，同一会话 `stop`/`apply` 均报 mismatch。
- 根因：服务端环境变量与绑定快照固定旧 `work.dwg`，客户端只同步了 session.json。
- 影响：每批一个会话；任何进程异常退出，未保存批次直接丢失。

### B-3 未保存批次不可恢复
- 现象：第三批 14 步 `verification=passed` 后，补保存时 CAD 已退出，`s3.dwg` 未生成。
- 影响：内存成果随进程消失；当前没有“按 journal 恢复未保存已验证批次”的安全路径（也不允许盲目重放）。

### B-4 长任务 15 秒前台等待
- 现象：`apply --save-to` 超 15s 被转后台，随后任务被 stop，CAD 退出。
- 影响：第三批丢失的直接触发条件；属于自动化流程风险，不是插件逻辑错误。

### B-5 PDF 尚未验证成功
- 现状：已确认 `page_setup`/`plot_pdf` 协议与 A3 媒体名，但没走到实际出图；本轮**没有 PDF**。
- 下一步：最小图先验证 `page_setup(Model, A3, monochrome.ctb, extents, 1/20) → 重新 bind 取 digest → plot_pdf`，再回砖墙图。

## C. 当前砖墙图的真实进度

| 批次 | 内容 | 状态 | 证据 |
| --- | --- | --- | --- |
| 1 | 7 图层、2 样式、A3 双矩形图框、3 个图签文字 | 已保存 | wall-section.dwg 31,246 B |
| 2 | 800 基础、370 放脚、240 砖墙、双面 20 抹面、60 压顶、3 个填充 | 已保存 | s2.dwg 32,021 B |
| 3 | 地面线、左右土体填充、6 道原生尺寸 | 已提交校验，保存前丢失 | 请求记录 `verification=passed`；无 s3.dwg |
| 4 | 6 组工艺 callout | 未执行 | `plan-04-callouts.json` |
| 出图 | page_setup + plot_pdf | 未执行 | — |

所以：**不能说“砖墙图画完了”，也不能说“PDF 已交付”**。现有可复用的是 `s2.dwg`（前两批）与四份计划；继续时应从 s2 起，先补第三批并同调用保存，再补 callout，最后出 PDF。
