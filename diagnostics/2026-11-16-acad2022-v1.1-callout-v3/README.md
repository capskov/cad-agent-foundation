# ACAD 2022 砖砌围墙 A3 截面标注 — 第三轮（hztxt_e 字体 + 多组平行布局）诊断包

日期：2026-11-16
仓库：`https://github.com/capskov/cad-agent-foundation`
分支：`acad2022-v1.1-diagnostics`
本目录：`diagnostics/2026-11-16-acad2022-v1.1-callout-v3/`

本包记录“砖砌围墙截面 + A3 图框 + PDF”任务在第三轮收口时暴露的真实问题、根因、修复和仍未闭环的项。不是成功宣传页；`verification=failed` 或 `pending` 的项在下文照实列出。

## 1. 最终交付物

| 文件 | 大小 | SHA256 | 说明 |
| --- | ---: | --- | --- |
| `s11-final4.dwg` | 37,780 B | `1D3E4CD30530B0C64E523F4BDF706AA2E01186F11658E3818A927C4446D6B2D9` | 六组 45° callout、hztxt_e+hztxt、文字在横线之上 |
| `s11-page4.dwg` | 37,745 B | `C7A31A0EA70999F90E281C17D732A6367F0509C8F0AEC680E5056757F5A0A014` | A3 页面设置文件；**页面设置 finalize 仍为 failed** |
| `wall-section.pdf` | 21,721 B | `FB7E3E65385461C027A5A0500533DBE49BD6566B964BEB2E07E969CDE09FBEBC` | A3 1191×842 pt；打印请求 `committed_verified` |
| `v11_wall_pdf_callout_v4.png` | 118,079 B | `E230B06828843FF4EB34E5BC58641FAC9ADF9812B88C095BE543B11B82786DFE` | 3000×2121 PDF 渲染 |
| `v11_wall_pdf_callout_v4_text_zoom.png` | 90,632 B | — | 文字与横线间隙放大 |
| `v11_wall_pdf_callout_v4_base4.png` | 104,469 B | — | 底部 C15/地面平行引线放大 |

最终图面：六组均为 45°，B/C 水平线，折点内角 135°；文字整体在横线之上；底部 `C15混凝土条形基础` 与 `室外地面±0.000` 两条 45° 引线平行、不交叉。

## 2. 前因：为什么会有这一轮

### 2.1 用户对第一版 callout 样例的反馈

用户原话（概括）：样例“快平了、很怪”，要求斜线至少 30°/35°/45°，水平线贴文字底边，折点在文字前，水平段包住文字。

第一版 Skill 把角度写成了接近固定的小角度，且文字/横线关系不够严格。后续用户澄清：

- 30°～80° 是**推荐区间**，不是固化；
- 优先“90+45”或“90+75”；
- 同组尽量平行；
- 没有位置才允许 180° 共线，但必须显式标记；
- **折点必须是钝角**；
- **文字始终在横线之上**，横线贴文字底边并覆盖整个文字宽度。

本轮 Skill 和计划都按这个口径重写，不再把 45° 当成唯一答案。

### 2.2 上一轮 v2 的问题

v2 六组 45° 已交付，PDF 20,570 B。用户看后说“基本上可以”，但截图指出：

- `C15混凝土条形基础` 与 `室外地面±0.000` 两条引线交叉；
- 认为没有做“平行标注布局探索”；
- 提醒原有标注 Skill 里应有布局判断。

根因不是角度，而是**锚点顺序和文字行顺序反了**：

- `co-concrete`：锚点 y=0.45，旧文字行 y=1.00；
- `co-ground`：锚点 y=0.62，旧文字行 y=0.27。

锚点高的文字反而低，两条同斜率 45° 线必然交叉。修正为：

- `co-concrete`：A(3.20,0.45) → B(2.79,0.86) → C(2.1771,0.86)
- `co-ground`：A(3.20,0.62) → B(2.79,1.03) → C(2.258,1.03)

B.x 统一 2.79；六组全部 45°、内角 135°；文字行按锚点从低到高排列。v2 的 DWG/PDF/预览已交付，本轮 v3 在此基础上换字体、按实测字宽重算 C 点。

### 2.3 更早的 12 组叠加事故

中途曾从已经含 6 组 callout 的 `s10.dwg` 再次叠加同一批 6 组，得到 `s11-tight.dwg` 实际 12 组重叠，PDF 文字重影、横线穿字。该中间产物已作废、未交付。

正确流程固定为：

1. 从**没有 callout** 的 `s3.dwg` 起；
2. 先读回 `Leader=0 / MText=0`；
3. 再落六组；
4. 保存后读回 `6/6`；
5. 只有 `verification=passed` 才进入页面/打印。

本轮 `v11_build_final6.py` 仍保留这个前置断言。

## 3. 本轮新增问题：hztxt_e 字体把横线“切进字里”

### 3.1 字体依据

底座资料把 `hztxt_e.shx + hztxt.shx` 作为已认可图纸的字体对：

- `artifacts/packages/20261116-fix12/docs/FONTS.md`
- `docs/FONT-REPLACEMENT-LOCAL3.zh-CN.md`：用户已认可图纸采用 hztxt_e+hztxt，HT 类实体宽度 0.8→1.1
- `Road08R01D1WriteContract.cs` 的 fallback 也是 `hztxt_e.shx + hztxt.shx`

本机 AutoCAD 2022 自带 `Fonts` 只有 `gbenor/gbcbig/txt`，没有 hztxt_e/hztxt。`F:\fonts` 内有：

- `F:\fonts\hztxt_e.shx` 5,143 B
- `F:\fonts\hztxt.shx` 1,207,733 B
- `F:\fonts\gbcbig.shx` 902,841 B
- `F:\fonts\gbenor.shx` 5,186 B
- 该目录共 2,506 个文件

已把 `F:\fonts` 加到 AutoCAD 2022 四套配置的 `ACAD` 支持路径末尾，原值备份在 `evidence/cad-font-path-before-20261116.json`。注册表位置：

`HKCU:\Software\Autodesk\AutoCAD\R24.1\ACAD-5101:804\Profiles\...\General`

生产实例 PID 12948 是修改前启动的，需要重启才会读取新路径；新启动的测试实例已能读到。

### 3.2 样式替换的两个坑

第一次用 `text_style` op 直接更新已有 `WALL_TXT`，失败：

- `error_code=foundation_plan_rollback`
- `stage=precommit_verification`
- `effects=none`
- `write_fence_released=true`

原因：`text_style` 是“创建或复用匹配样式”，不是替换已有样式字体。

改用 `set_text_style_font`，参数必须是**当前绑定目录 `text_styles[name]` 的规范 JSON SHA256**。本图 `WALL_TXT` 的规范 JSON：

```json
{"big_font_file":"hztxt.shx","fixed_height":0.0,"font_file":"txt.shx","handle":"1B8","is_vertical":false,"resolved_font":{"bold":false,"charset":1,"italic":false,"pitch_family":0,"typeface":""},"width_factor":1.0}
```

哈希：`5D32C0BEA7014F48BCB3CCA37493AB21445A460742D3C3D5A64076D3A2FC8164`

结果：`status=completed`、`phase=completed`、`effects=committed`、`verification=passed`、`persisted=true`，输出 `v11-wallAT/s3-hztxt.dwg` 36,374 B。

### 3.3 新问题：横线切字

用 hztxt_e+hztxt 直接落六组后，PDF 放大图显示横线**压在汉字字脚上**，看起来像删除线/下划线切字。不是角度问题，也不是 B/C 位置问题：

- 读回 `MText.extents.min.y` 等于 `leader.C.y`；
- 但 hztxt_e 的汉字字形在 AutoCAD 2022 里会向下探出 `extents` 基线；
- 因此横线画在 `extents.min.y` 时，实际切进字形。

对比 v2 的 `txt.shx + hztxt.shx`，同样 y 关系下横线在字下方，不切字。说明这是**字体度量差异**，不是布局错误。

### 3.4 修复：0.012 的字体下探补偿

只在“横线切字”被放大图确认时使用：

- 横线仍保持在 `C.y`；
- 文字插入点 `position=(C.x, C.y+0.012)`；
- `annotation_offset=(0,-0.012)`；
- 读回必须满足 `MTEXT.extents.min.y - leader.C.y = 0.012`；
- 默认 `text_gap=0`，不得用更大的负 offset 掩盖布局错误。

本图 `text_gap=0.012`，对应 1:20 纸面约 0.24 mm；放大图确认横线在字下方而不是切字。

`validate_callouts.py` 已增加这个窄例外：

- `0 <= text_gap <= 0.02`；
- `position.x == C.x`；
- `annotation_offset=(0,-text_gap)`；
- 横线 y 仍等于 `position.y - text_gap`；
- 超过 0.02 或方向不对直接 fail。

`plan-06g-hztxt-gap.json` 与 `plan-06f-hztxt.json` 均通过 `validate_callouts.py` 和 `validate_layout.py`（6 组，0 issue）。

### 3.5 hztxt_e 实测字宽

用 `v11_measure_hztxt.py` 在隔离副本上创建六条 MTEXT，读回 `actual_width`：

| id | 文字 | actual_width |
| --- | --- | ---: |
| co-wall | 240厚烧结页岩砖墙，M7.5水泥砂浆砌筑 | 1.1141 |
| co-plaster | 20厚1:2水泥砂浆抹面（双面） | 0.8148 |
| co-coping | C25混凝土压顶60厚 | 0.5648 |
| co-footing | 370厚砖砌大放脚 | 0.4779 |
| co-concrete | C15混凝土条形基础 | 0.5372 |
| co-ground | 室外地面±0.000 | 0.4784 |

计划按“实测宽 + 0.04”重算 C = B.x - width，而不是沿用 txt.shx 的旧宽度。这样水平段仍覆盖文字实际宽度，且右侧只留 0.04 的小尾巴。

## 4. 执行链与真实结果

### 4.1 从无 callout 的 s3 起

`v11_build_final6.py`：

1. 复制 `v11-wallAT/s3-hztxt.dwg` 到 `v11-wallAY`；
2. 读回 `before callouts 0 0`；
3. apply `plan-06g-hztxt-gap.json`；
4. apply 回执 `status=verification_pending`、`effects=committed`、`verification=passed`；
5. 保存后 `query-after-save` 变为 `completed / passed / write_fence_released=true`；
6. 读回 `after callouts 6 6`。

注意：不能把 `verification_pending` 当成最终成功；必须查询原请求直到 `completed+passed`。

### 4.2 读回六组几何

`v11_read_final4.py` 从保存后的 `s11-final4.dwg` 独立读回：

- 6 个 Leader、6 个 MText；
- 六组 `vertices_wcs` 均为 A→B→C，B/C 水平；
- `annotation_offset=(0,-0.012)`；
- `dimtad=0`、`has_hook_line=false`；
- MText `attachment=BottomLeft`；
- `MTEXT.extents.min.y - leader.C.y = 0.012`（六组均满足）；
- `MText.extents.min.x = C.x`，`max.x = B.x`。

### 4.3 A3 页面与打印

- `v11_page_from_final4.py` 输出 `s11-page4.dwg` 37,745 B；
- 页面设置请求仍为 `verification=failed` / `save_succeeded_but_finalize_pending`；
- 没有通过重放写入或“文件存在”宣布 finalize 成功；
- `v11_plot_from_final4.py` 独立会话从 `s11-page4.dwg` 打印，回执 `status=completed`、`verification=passed`、`outcome=committed_verified`；
- PDF 21,721 B，A3 1191×842 pt。

**未闭环**：页面设置 finalize 仍 failed。当前只声明“页面文件写出 + 打印请求通过”，不声明页面设置 finalize 通过。

## 5. 本轮发现的其他问题

### 5.1 进程遗留

本轮样式、测量、落图、页面、打印测试累计启动了 10 多个 AutoCAD 测试实例。用户明确要求：干完活关闭不相关测试实例，保存记录后不要一直开着。

已保留：

- 生产 AutoCAD PID 12948（`Autodesk AutoCAD 2022 - [Drawing1.dwg]`）

已强制关闭：

- 2026-11-16 启动的 11 个测试 acad.exe（无可见窗口，均为本轮测试）

关闭后用 `Get-Process acad` 复核，只剩 PID 12948。

### 5.2 字体路径

`F:\fonts` 已写入四套配置；`<<Unnamed Profile>>` 原本没有，已追加到末尾，避免覆盖官方字体路径。生产实例未重启，暂未在 PID 12948 内验证；新测试实例已验证可读到 hztxt_e/hztxt。

### 5.3 早期 V1.1 保存身份问题（背景，不冒充本轮修复）

V1.1 曾出现“保存成功却报 Unknown request record”：

- 旧 V1.0 评审源码 `apply_with_optional_save()` 在无显式 `request_id` 时，`apply()` 内部生成 ID，但后续 final 查询仍使用原始 `None`；
- 已修复请求身份传递与保存后状态收口，并做正反向回归；
- 不以“重放原写入”或“文件存在”代替 finalize。

### 5.4 第三批终止的性质（按审查意见纠正）

第三批是**用户主动终止**，不是 Agent 自主执行失败，也不是 CAD 意外崩溃。诊断材料里不能写成“Agent 自主失败/崩溃”。本轮只把它作为任务边界记录。

## 6. 复现命令（Windows PowerShell）

```powershell
cd F:\AI\cad-agent-foundation-acad2022\cad2022-test
python v11_hash_style.py
python v11_build_hztxt_base2.py        # 仅样式替换，输出 v11-wallAT\s3-hztxt.dwg
python v11_measure_hztxt.py             # 仅测量字宽，不保存
python v11_plan_hztxt.py                # 生成 plan-06f
python v11_plan_hztxt_gap.py            # 生成 plan-06g（0.012 字体间隙）
python v11_build_final6.py              # 从 s3-hztxt 落六组，输出 s11-final4.dwg
python v11_page_from_final4.py          # A3 页面，输出 s11-page4.dwg
python v11_plot_from_final4.py          # 打印，输出 wall-section.pdf

python "C:\Users\cg524\AppData\Local\DoubaoWork\User Data\Default\.doubaowork\agent_mode\workspace\.user_skills\cad-callout-pair-layout\scripts\validate_callouts.py" .\v11-wall\plan-06g-hztxt-gap.json
python "C:\Users\cg524\AppData\Local\DoubaoWork\User Data\Default\.doubaowork\agent_mode\workspace\.user_skills\cad-callout-pair-layout\scripts\validate_layout.py" .\v11-wall\plan-06g-hztxt-gap.json
```

## 7. 仍未闭环 / 不要误读

1. **页面设置 finalize 仍 failed**：只声明页面文件写出和 PDF 打印通过，不声明页面设置闭环。
2. **生产实例未重启**：PID 12948 仍用旧会话字体路径；`F:\fonts` 是否在其内生效未验证。新测试实例已验证。
3. **hztxt_e 字体下探补偿**是经验值 0.012，来自本图/本字体/本字高；换字体、换字高、换 AutoCAD 版本必须重新量，不得直接套。
4. **v3 的 0.012 间隙**改变了 `annotation_offset` 严格零的旧契约；Skill 里已限定为“字体下探 ≤0.02 的唯一例外”，不是通用负 offset。
5. `s11-tight.dwg`（12 组叠加）、`v11-wallAJ\s11-page.dwg`、`v11-wallAK\wall-section.pdf` 仍是作废中间产物，未清理；不要把它们当最终交付。
6. v1/v2 的证据目录仍保留：
   - `diagnostics/2026-11-16-acad2022-v1.1-callout-final/`
   - `diagnostics/2026-11-16-acad2022-v1.1-callout-v2/`

## 8. 结论

本轮修好并实测通过：

- hztxt_e+hztxt 样式替换（`set_text_style_font` + 规范哈希）；
- hztxt_e 下六组实际字宽测量；
- 六组 45°、135° 钝角、文字在横线之上、横线覆盖文字宽度的最终 DWG；
- 底部 C15/地面平行不交叉；
- A3 PDF 打印（`committed_verified`）；
- 测试 AutoCAD 进程清理（仅保留生产 PID 12948）。

仍未闭环：

- A3 页面设置 finalize；
- 生产实例内 `F:\fonts` 生效验证；
- 页面设置路径的 `save_succeeded_but_finalize_pending` 根因。
