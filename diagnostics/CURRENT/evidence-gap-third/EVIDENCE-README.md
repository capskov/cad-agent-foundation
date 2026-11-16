# 引线标注新规则：文字底边与水平线空 1/3 字高

日期：2026-11-16
状态：**已随 `acad2022-v1.1-diagnostics` 分支上传，位于 `diagnostics/CURRENT/evidence-gap-third/`。** 上一版 `diagnostics/archive/2026-11-16-acad2022-v1.1-callout-v3/` 仍是旧规则（0.012 字体补偿），已归档为过时资料。

## 1. 规则变更

用户新规定：

> 文字底部和引线水平线不能挨着，必须空 **1/3 字高**。

因此几何契约从原来的“横线贴文字底边”改为：

- `B`、`C` 仍在同一水平线上；
- 横线在文字底边下方 `text_gap = height / 3`；
- 文字插入点 `position = (C.x, C.y + height/3)`；
- `annotation_offset = (0, -height/3)`；
- 默认就留这个间隙，不是字体特例，也不允许贴线；
- 读回必须满足 `MTEXT.extents.min.y - leader.C.y = height/3`；
- PDF 放大图必须能看到文字底部与横线之间的空隙。

字高 0.06 时 `text_gap = 0.02`（1:20 约 0.4 mm）；本图六组均按 0.02 落图。

## 2. 最终交付

| 文件 | 大小 | SHA256 |
| --- | ---: | --- |
| `s11-final5.dwg` | 37,748 B | `5E5ADE22CE55C98A0EA16F6092134D9739896D5DE2BF4625556A47011A2B5D45` |
| `s11-page5.dwg` | 37,681 B | `64BAAF6C37091CC3A8DD61706D588A049AE2D58690137C51329769467CA2B46E` |
| `wall-section.pdf` | 21,709 B | `09FB0546D19E2E61B46C9EBFC86FCF510157EA29E6F2D8A9CEF43B5490674DA8` |
| `v11_wall_pdf_callout_v5.png` | 118,441 B | `5BDA295210DF3F3E16763025E8DF9B61F429A4DF91E096F09B2815BE7BE2A88A` |
| `v11_wall_pdf_callout_v5_text_zoom.png` | 92,974 B | — |
| `v11_wall_pdf_callout_v5_base.png` | 106,069 B | — |

## 3. 实际验证

- 从 `v11-wallAT/s3-hztxt.dwg` 起，apply 前 `Leader=0 / MText=0`；
- apply `plan-06h-gap-third.json`：`effects=committed`、`verification=passed`、`status=verification_pending`；
- 保存后查询原请求：`completed / passed / write_fence_released=true`；
- 读回 `Leader=6 / MText=6`；
- 六组 `annotation_offset=(0,-0.02)`；
- 六组 `MTEXT.min.y - leader.C.y = 0.02`；
- `validate_callouts.py`（新规则）`status=pass`，6 组 0 issue；
- `validate_layout.py` `status=pass`；
- PDF 打印 `committed_verified`，A3 1191×842 pt；
- PDF 放大图可见文字底边与横线之间的空隙，文字不碰线；
- 底部 `C15` 与 `室外地面` 仍平行不交叉。

## 4. 本轮仍失败/未闭环

- `s11-page5.dwg` 页面设置请求仍是 `verification=failed / save_succeeded_but_finalize_pending`；只声明页面文件写出和 PDF 打印通过，不声明页面设置 finalize 通过。
- 生产 AutoCAD PID 12948 未重启，`F:\fonts` 在其会话内是否生效未验证。
- 本轮测试 AutoCAD 已清理，只保留生产 PID 12948。

## 5. 复现

```powershell
cd F:\AI\cad-agent-foundation-acad2022\cad2022-test
python v11_plan_gap_third.py
python v11_build_final7.py
python v11_page_from_final5.py
python v11_plot_from_final5.py

python "C:\Users\cg524\AppData\Local\DoubaoWork\User Data\Default\.doubaowork\agent_mode\workspace\.user_skills\cad-callout-pair-layout\scripts\validate_callouts.py" .\v11-wall\plan-06h-gap-third.json
python "C:\Users\cg524\AppData\Local\DoubaoWork\User Data\Default\.doubaowork\agent_mode\workspace\.user_skills\cad-callout-pair-layout\scripts\validate_layout.py" .\v11-wall\plan-06h-gap-third.json
```

## 6. Skill 分支状态

- 已在 `cad-callout-pair-layout` 的 2022 版 `SKILL.md` 中把 `1/3 字高` 间隙作为默认规则；
- `text_gap` 已从“字体下探例外”提升为通用字段；
- `validate_callouts.py` 已校验 `gap ≈ height/3`，`validate_layout.py` 继续检查交叉/穿字；
- hztxt_e / txt+hztxt / 其他 SHX 字体的实际字高差异仍需继续收集，见 `CURRENT/README.md` 的 OPEN-12；
- 本次已随 `acad2022-v1.1-diagnostics` 分支上传，旧 `callout-v3` 目录已归档为过时资料。
