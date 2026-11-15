# CTB打印样式与窗口PDF

1.2.0-beta.3.18新增两项明确基础操作，逐幅取景和图面判断仍由Agent负责。

## 本地CTB配置

`foundation.cmd plot-style --workspace C:\CAD-work\demo --arguments style.json`

此命令是底座Python客户端的本地文件操作，不是原生DWG事务或MCP操作；不改图、不提交打印任务。仅支持压缩CTB v2、完整255色和既有线宽表。无第三方Python依赖。读操作示例：

```json
{"action":"read","path":"C:/CAD-config/PM.ctb"}
```

先读取SHA256，再提交配置；文件路径须由调用者明确选择：

```json
{
  "action":"configure",
  "path":"C:/CAD-config/PM.ctb",
  "expected_sha256":"上次读取的64位大写SHA256",
  "all_colors_black":true,
  "lineweights_mm":{"2":0.15,"3":0.15},
  "request_id":"pm-black-yellow-green-015"
}
```

ACI 2为黄色、3为绿色。255种索引色均设为黑色，指定颜色改变线宽，其他颜色的线宽及其他参数保留。此配置不保证TrueColor或专业代理对象服从CTB。原始文件、目标文件和回执保存在工作区`plot-style-requests/<request_id>`。同ID只返回原回执；未完成请求报告unknown，不重新执行。摘要不匹配、未知格式、未在表内的线宽均拒绝。CTB不是DWG的一部分，交付时需随图提供并安装到目标CAD的打印样式搜索目录。

## 原生单幅窗口PDF

`plot_pdf`新增可选`window_settings`，全部字段必须明确给出：

```json
{
  "id":"pdf","op":"plot_pdf","layout":"Layout1",
  "expected_sha256":"当前layout_digests中的64位摘要",
  "file_name":"sheet-01.pdf",
  "window_settings":{
    "device":"AutoCAD PDF (General Documentation).pc3",
    "media":"ISO_full_bleed_A3_(420.00_x_297.00_MM)",
    "plot_style":"PM.ctb","rotation":0,
    "min":[0,0],"max":[420,297],
    "scale_numerator":1,"scale_denominator":1,"center":true
  }
}
```

设备、纸张、样式须从实际目录选取；坐标为该布局的打印DCS原数值，不能把模型WCS、纸空间坐标与比例混用。仅当前布局、PDF PC3、单独一步；输出到自有DWG旁`outputs`，不覆盖已有PDF。启用打印样式和线宽，不缩放线宽。设置仅用于此次原生PlotInfo，不写回布局，不改变模型单位。打印前确认外参均已加载；文件输出成功和哈希校验不代表视觉合格，PDF须实际看图。

原有不带`window_settings`的调用继续使用布局原有设置，原`page_setup`范围仍为layout/extents。纸空间截图/导航仍不在模型导航支持范围内，可用窗口PDF复核。

## 验证边界

144项离线契约检查、3项CTB配置测试通过。实际项目PM文件经独立解析确认255色为黑色、黄色及绿色0.15mm。原生窗口PDF验收和最终发布状态以版本验收记录为准；候选包不能仅凭构建或离线测试称已验收。
