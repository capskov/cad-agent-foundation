# 砖砌围墙 A3 / 平行 callout v2 证据（2026-11-16）

## 1. 本轮修正

用户截图指出最下面两组引线交叉。根因不是角度，而是**锚点顺序和文字行顺序反了**：

- `co-concrete` 锚点 y=0.45，旧文字行 y=1.00（高锚点的 `co-ground` 反而在 y=0.27）。
- 因此 `co-ground` 的线从 y=0.62 往下压到 0.27，`co-concrete` 的线从 y=0.45 往上抬到 1.00，两条 45° 线必然交叉。

修正后：

| id | A | B | C | 文字行 y |
| --- | --- | --- | --- | ---: |
| co-concrete | 3.20, 0.45 | 2.79, 0.86 | 2.1771, 0.86 | 0.86 |
| co-ground | 3.20, 0.62 | 2.79, 1.03 | 2.258, 1.03 | 1.03 |

两组 B.x 统一 2.79；六组斜线全部 45°、内角 135°，文字行按锚点从低到高排列，底部不再交叉。

## 2. 最终文件

| 文件 | 大小 (B) | SHA256 |
| --- | ---: | --- |
| `s11-final2.dwg` | 38,133 | `968AB026A6D26CD9EDA66FF95E700BFE11761B6C317E289FCBCE05A9F32B07E9` |
| `s11-page2.dwg` | 37,876 | `C6CE41207FF548243705D5519E5496A7BBCD4DD449A7E5BC2FAA67D395CD2BE1` |
| `wall-section.pdf` | 20,570 | `5EA69067008FD616F0560BADE99AC271328C4A361FBE698BCFB8204887DD3BA2` |
| `wall-section-preview.png` | 116,918 | `B531144BE4769FD917408A667B3E5A1DA1C5A05B469EBAE3AA9B4A4B9716291E` |

PDF 为 A3 1191×842 pt，1 页；渲染图 3000×2121。

## 3. 读回与校验

- `s11-final2.dwg` 从没有 callout 的 `s3.dwg` 起；apply 前 `Leader=0, MText=0`。
- apply 后 `Leader=6, MText=6`。
- 六组 MTEXT 均 `BottomLeft`，文字框 `min.y` 等于横线 y，`max.y` 在横线上方：
  - co-concrete extents `min=(2.1771,0.86)`、`max=(2.79,0.92)`
  - co-ground extents `min=(2.258,1.03)`、`max=(2.79,1.09)`
- `scripts/validate_callouts.py plan-06e-parallel.json`：6 组，`status=pass`。
- 新增 `scripts/validate_layout.py plan-06e-parallel.json`：检查引线交叉、引线穿别组文字、文字框重叠、同侧锚点/文字行顺序，6 组 `status=pass`。
- apply 保存后 query：`verification=passed`、`write_fence_released=true`。
- PDF `plot_pdf`：`verification=passed`、`outcome=committed_verified`、fence released。

## 4. 页面设置真实状态

`s11-page2.dwg` 已写出，但页面设置请求返回 `verification=failed`、`persisted=false`、`next_action=save_succeeded_but_finalize_pending`。因此只声明“A3 页面设置文件写出”，不声明页面设置 finalize 通过；PDF 打印请求本身是 passed。

## 5. 字体核查（本轮未改字体）

当前 `WALL_TXT` 实际为 `font_file=txt.shx`、`big_font_file=hztxt.shx`。底座资料：

- `docs/FONT-REPLACEMENT-LOCAL3.zh-CN.md`：用户已认可图纸采用 **hztxt_e + hztxt**，正文保留原字高字宽，HT 类实体宽度 0.8→1.1；这是项目资料库的工程选择，不是所有 TTF 替换的默认倍率。
- `Road08R01D1WriteContract.cs`：fallback 字体对就是 `hztxt_e.shx + hztxt.shx`。
- `docs/FONTS.md`：公开候选包不包含 `hztxt.shx` 和 `hztxt_e.shx` 二进制；下载站存在不等于有再分发许可，provenance 未解决。

本机 AutoCAD 2022 `Fonts` 目录实际只有 `gbenor.shx`、`gbcbig.shx`、`txt.shx` 等，**没有 `hztxt_e.shx` / `hztxt.shx`**。所以不能把样式改成 `hztxt_e.shx` 并宣称已验证；那会是缺字体替代。本机可用、Autodesk 文档明确提到的中文组合是 `gbenor.shx + gbcbig.shx`。

下一步三选一：

1. 用户提供有许可的 `hztxt_e.shx` / `hztxt.shx`，安装进 AutoCAD 搜索路径后切 hztxt_e + hztxt。
2. 先用本机可用的 `gbenor.shx + gbcbig.shx`，按新字宽重新量文字并排一次。
3. 保持当前 `txt.shx + hztxt.shx`，只作为现状，不宣称 hztxt 已验证。

## 6. Skill 已补布局判断

`cad-callout-pair-layout/SKILL.md` 新增“多组布局判断”：

- 同侧先按锚点顺序排文字行，低锚点→低文字行，高锚点→高文字行；顺序反了斜线必交叉。
- 同侧尽量同一角度、同一 B.x 的平行带；用共同基准投影排序，不直接比较折点 x。
- 相邻组、固定旧组一起检查引线-引线、引线-别组文字、文字框重叠、引线压尺寸/图框。
- 处理顺序：换文字行/顺序 → 换开口 → 换角度 → 最后 180° 直线退路；不得用压盖或负 offset 掩盖交叉。
- 新增 `scripts/validate_layout.py`，把上述交叉/重叠/顺序检查变成可执行校验。
