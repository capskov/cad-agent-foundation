# CAD2022 引线标注 Skill 变更记录

## 2026-11-16 — 1/3 字高间隙成为默认规则

**用户口径**：文字底部不能和引线水平线挨着，必须空 1/3 字高。

**新几何**：
- B、C 同一水平线；
- 水平线 y = C.y；
- 文字插入点 `position=(C.x, C.y + height/3)`；
- `annotation_offset=(0, -height/3)`；
- 文字整体在横线之上，`MTEXT.min.y - C.y = height/3`。

**影响**：
- 原 `annotation_offset` 严格零的契约被替换；负 offset 现在用于正常间隙，不再需要“字体下探特例”。
- `validate_callouts.py` 改为校验 `text_gap == height/3`（容差 0.003），不再接受贴线或 0.012 的经验值。
- 当前围墙图：字高 0.06，gap 0.02；六组 45°，底部 C15/地面平行不交叉；PDF 打印 `committed_verified`。

**仍遗留**：
- A3 页面设置 finalize 仍 `save_succeeded_but_finalize_pending`；
- 生产 AutoCAD 未重启，`F:\fonts` 在生产会话内未验证。

## 2026-11-16 — hztxt_e 字体下探（已并入 1/3 间隙规则）

**现象**：`hztxt_e+hztxt` 下，MText `extents.min.y` 在基线上，汉字字形还会向下探；横线画在 `C.y` 会切字脚。

**当时的临时修复**：`text_gap=0.012`，`position=C+(0,0.012)`，`annotation_offset=(0,-0.012)`。

**后来结论**：用户把“1/3 字高”定为通用规则，0.012 特例被 0.02（height/3）取代；不再单独保留字体补偿。

**教训**：
- 先看 PDF 放大图，不能只看 `extents` 数值；
- 字体度量差异必须通过实际读回 + 渲染确认；
- 不能把“文件存在/写入成功”当视觉通过。

## 2026-11-16 — 多组平行布局 / 底线交叉

**现象**：`co-concrete` 锚点 y=0.45、文字行 y=1.00；`co-ground` 锚点 y=0.62、文字行 y=0.27。锚点高的文字反而低，两条 45° 线交叉。

**修复**：按锚点顺序重排文字行，统一 B.x=2.79，六组全 45°；`co-concrete` 文字 y=0.86，`co-ground` 文字 y=1.03。

**规则**：同侧同开口先按锚点排序，文字行保持同序；优先平行带；检查引线-引线、引线-别组文字、文字框重叠。

## 2026-11-16 — 12 组重复叠加

**现象**：从已含 6 组 callout 的 `s10.dwg` 再加 6 组，`s11-tight.dwg` 实际 12 组，PDF 重影、横线穿字。

**修复**：从无 callout 的 `s3.dwg` 起，apply 前断言 `Leader=0/MText=0`，apply 后读回 `6/6`。该中间产物作废。

**规则**：任何 callout 计划落图前必须先读回旧 callout 数量为 0；不能“接着上一版继续加”。

## 2026-11-16 — 样式替换：`text_style` 不是改字体

**现象**：`text_style` 更新已有 `WALL_TXT` 失败，`foundation_plan_rollback`、`precommit_verification_failed`、`effects=none`。

**原因**：`text_style` 是创建/复用匹配样式，不是替换已有样式字体。

**正确入口**：`set_text_style_font`，要求当前目录 `text_styles[name]` 的规范 JSON SHA256；本图哈希 `5D32C0BEA7014F48BCB3CCA37493AB21445A460742D3C3D5A64076D3A2FC8164`。

## 2026-11-16 — 页面设置 finalize

**现象**：A3 页面设置保存文件成功，但回执 `verification=failed / save_succeeded_but_finalize_pending`；PDF 打印本身可 `committed_verified`。

**处理**：不通过重放写入或“文件存在”宣布 finalize 成功；在交付里单列“页面设置 finalize 未闭环”。

## 2026-11-16 — 测试进程清理

用户要求：干完活关闭无关测试 AutoCAD，保存记录后不要一直开。

**执行**：每轮结束 `Get-Process acad`，只保留生产 PID 12948，其余测试实例强制关闭并复核。
