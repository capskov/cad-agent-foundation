# 砖砌围墙 A3 与 callout 最终证据（2026-11-16）

## 1. 这次交付的最终文件

| 文件 | 大小 (B) | SHA256 |
| --- | ---: | --- |
| `s11-final.dwg`（6 组 callout + Dimlfac，模型空间） | 37,812 | `A3E034803084F1EB68A0EF9077E97B8DB3AEDD7B9EBC4A43205C872765C6467A` |
| `s11-page.dwg`（A3 页面设置） | 38,356 | `66A6E19695CA8EE88F5CE3EE8B0C3C8BE928F90BC4C0DD7C83107B19C19E3514` |
| `wall-section.pdf`（A3，1191×842 pt） | 20,542 | `97378DE0742CAB0DF0D3DCA383588C46ECB9A6D49E0B811B5A876DDA208095C5` |
| `wall-section-preview.png`（3000×2121 渲染） | 118,701 | `412FDD4D14399303006E04DAA0F0A2518DCEEB230E480AD290CEA292F8DC21FC` |

PDF 为 1 页，页面 1191×842 pt（A3 420×297 mm）。

## 2. callout 几何

六组全部为 45° 斜线，折点内角 135°（钝角，满足“90+45”），B→C 水平，C 为文字远底角/插入点，`annotation_offset=(0,0)`。

| id | 文字 | 实测字宽 | 计划宽度 | A | B | C |
| --- | --- | ---: | ---: | --- | --- | --- |
| co-wall | 240厚烧结页岩砖墙，M7.5水泥砂浆砌筑 | 1.2034 | 1.2434 | 3.50, 2.00 | 2.80, 2.70 | 1.5566, 2.70 |
| co-plaster | 20厚1:2水泥砂浆抹面（双面） | 0.8583 | 0.8983 | 3.48, 1.50 | 2.88, 2.10 | 1.9817, 2.10 |
| co-coping | C25混凝土压顶60厚 | 0.6448 | 0.6848 | 3.50, 3.05 | 2.80, 3.75 | 2.1152, 3.75 |
| co-footing | 370厚砖砌大放脚 | 0.5257 | 0.5657 | 3.435, 0.80 | 2.885, 1.35 | 2.3193, 1.35 |
| co-concrete | C15混凝土条形基础 | 0.5729 | 0.6129 | 3.20, 0.45 | 2.65, 1.00 | 2.0371, 1.00 |
| co-ground | 室外地面±0.000 | 0.4920 | 0.5320 | 3.20, 0.62 | 2.85, 0.27 | 2.318, 0.27 |

计划宽度 = 实测字宽 + 0.04 余量。水平段 B→C 长度等于计划宽度，文字实际宽度小于水平段 0.04，右侧只剩 0.04 的收口余量，不是 0.24–0.27 的空尾巴。

## 3. 读回证据

`s11-final.dwg` 的读回结果：

- apply 前：`Leader=0, MText=0`，确认源图没有旧 callout，避免叠加。
- apply 后：`Leader=6, MText=6`，恰好六组。
- 六组 MTEXT 均为 `BottomLeft`，`min.y` 等于对应 B/C 横线 y，`max.y` 在横线上方；即文字整体在横线之上。
- 例：`co-wall` 的 extents 为 `min=(1.5566, 2.70)`、`max=(2.80, 2.7685)`，横线 y=2.70。
- apply 请求：`verification=passed`、`effects=committed`；保存后 query 原请求：`verification=passed`、`write_fence_released=true`。

## 4. 这次踩到的真实坑

`s11-tight.dwg`（未交付）是从 `s10.dwg` 起的，而 `s10.dwg` 里已有 6 组 callout。然后再次 apply 同一批 6 组，得到 **12 组重叠**，PDF 中文字出现重影、横线穿字。该中间产物已作废。

正确做法：从没有 callout 的 `s3.dwg`（四批砖墙几何）起，先查询 `Leader/MText` 为 0，再 apply 收紧后的 6 组，保存为 `s11-final.dwg`。以后同类返工必须先检查源图 callout 数量，禁止在已有 callout 的图上重复 apply。

## 5. 保存/页面设置的真实状态

- `s11-final.dwg` 的 callout apply + 保存 + query 闭环：`verification=passed`。
- A3 页面设置保存 `s11-page.dwg`：保存文件成功，但请求返回 `verification=failed`，`next_action=save_succeeded_but_finalize_pending`。因此这里只声明“页面设置文件已写出”，不声明页面设置 finalize 通过。
- PDF 打印请求：`verification=passed`、`outcome_summary.outcome=committed_verified`、`write_fence_released=true`。
- PDF 目视：6 组单线、无重影、无 12 组叠加；A3 页面；文字在横线上方。

## 6. Skill 规则已同步

`cad-callout-pair-layout/SKILL.md` 已改为：

- 角度是推荐值，30°–80°，常用 35°–45°，优先 45°（“90+45”）；60°/75°/80° 可用（“90+75”一类）。
- 折点内角必须 >90°；45°→135°，75°→105°，80°→100°。
- 同组/同侧尽量平行、角度统一。
- 180° 共线只在无位置时作最后退路，必须 `leader_straight=true`，不能伪装成斜线。
- **文字整体始终在横线之上**；横线只托文字底边，文字落到横线下方或与之相交直接判失败。
- 保留 `annotation_offset=(0,0)`、`position=C`、单行、覆盖实际文字宽度。

## 7. 进程清理

- 早上遗留的 44 个 AutoCAD 测试实例已关闭；随后又把剩余 12 个测试实例、页面设置/打印实例逐个关闭。
- 最终只保留生产实例 PID 12948，未对它执行 save/bind/write。
- 本轮没有仍在执行的 Python 构建脚本；`wallAH` 的 `v11_build_final2.py` 已在 11:06 完成并退出，不是“仍在跑”。

## 8. 复现文件

- `plan-06d-tight.json`：六组 45° 收紧计划（含 Dimlfac=1000）。
- `v11_build_final3.py`：从 `s3.dwg` 起，先断言 0 组旧 callout，再 apply 六组，读回 6/6，保存。
- `v11_page_from_final.py`：从 `s11-final.dwg` 做 A3 页面设置。
- `v11_plot_from_final.py`：从 `s11-page.dwg` 独立会话打印 A3 PDF。
