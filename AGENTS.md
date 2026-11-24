# Working with this project

## AutoCAD 2026 多图会话与默认字体

集中读图时启动一个由底座管理的 AutoCAD 2026 进程，逐张以只读打开，并按完整路径重新绑定当前图纸。读完一张就关闭该文档，保留进程和底座工作图。切图前等当前命令和读请求结束。绘图和查图要区分会话，写入继续绑定授权工作副本。

遇到停顿先检查命令状态和模态提示，特别是缺字体、外参和代理对象。正常等待时逐页读取图层、布局和文字，避免全图逐对象 COM 遍历。确认进程无响应且无法恢复时才重新启动；已提交写入沿原请求核对。任务全部结束后正常退出本任务启动的 CAD 进程。

普通中英混排缺字体时优先用 `hztxt_e.shx` 作为英文数字主字体，用 `hztxt.shx` 作为中文大字体。沿用本机可用映射，保留可读的原字体及专用符号样式。FONTALT 单值只处理备用主字体。字体替换后抽看汉字、数字和标注位置。

## 主动提醒并使用相关 Skill（2026-10-29）

使用底座进入专业任务前，由Agent主动查看当前可用Skill并按任务读取；用户不需要再次点名。已有且适用的Skill直接使用；尊重用户明确不用的选择。缺少可选Skill时，简短说明其作用并提供已核实的安装入口，用户可以跳过；不强制安装、不自动下载、不为此锁住基础CAD操作，同一阶段不反复提醒。

卫星/正射影像描路用`satellite-road-edge-tracing`；引线与文字排布用`cad-callout-pair-layout`；病害/拆除表达用`cad-distress-annotation`（需要其引线表现时同时读取标注Skill）；字体缺失/替代用`cad-font-diagnostics`；复用路面结构样式用`cad-pavement-structure-style`；道路结构成果组织按适用范围用`road-design-delivery-style`；套图、布局、目录按项目适用范围用`luna-cad-sheet-layout`。仅选择当前任务相关者，不为简单基础操作加载全部Skill；项目专用Skill不可套用到其他项目。详见[按任务主动提醒](docs/SKILL-COMPANIONS.zh-CN.md)。

底座提供执行和真实反馈，Skill提供专业方法与排布规则。提醒是Agent指导，不是原生前置校验；安装Skill不会补足缺少的CAD接口，也不改变未知写入保护或替代通道授权。

save_copy 维修（2026-10-29）：新版仅复制已保存且 DBMOD=0 的工作图。有未保存内容时先执行独立 save 并核验，再另存。未知结果仍保留保护，不重放。已提供受限的旧 SaveAs 修改标志故障恢复接口：沿原请求核对原副本及完整现场，原始回执不变，经双侧验证后核销，再恢复同一实例队列；不是通用解锁。见 docs/SAVE-COPY-FIX-20261029.zh-CN.md。

## 报警分级（2026-10-29 用户指令）

同日用户明确接受DXF回读时表格字高2.3与2.299999999999999这一浮点差异，要求不要反复验证。数值末位舍入且不改变工程含义、内容和可读性的同类情况按提示级处理，不逐次重问或重导出。此项属于精度接受，不授权强制清除未知写入保护；原请求、原回执保留，维护时沿原请求处理。现场实例见项目本地交接记录，不将工程文件放入底座发布包。

保存时仅字体替换、文字仍可见可读而影响排版，给 warning 后继续，不以此锁住写入。实际缺字/消失/乱码等由 Agent 看图判断，暂停受影响任务；图纸身份、语义内容、对象丢失、未知提交、哈希/保存验证等执行风险仍必须保护。详见 docs/ALERT-SEVERITY-20261029.zh-CN.md。字体替换无需每次重问；本次用户授权优先于下文历史字体确认指引。

## 首要任务：为智能 Agent 提供可靠操作，而非编程替代判断

用户于2026-10-16反复明确，本条置于最前，指导后续开发、取舍和验收：

- 底座开发是实战任务的辅助工作。首要目标是提供可靠、可控的基础CAD操作和真实反馈，让Agent能够执行自己的判断并持续向前推进。
- 按Agent会越来越聪明来设计：审美、结构理解、样图学习、规律归纳、复杂取舍和最终看图复查由Agent承担；不要提前把这些能力固化为程序、规则引擎、拟合算法或强制验证流程。
- 现有操作足以执行，且Agent综合判断可以继续时，就推进主线。不要为了代替Agent判断，再写一套程序或反复验证。
- 编程解决明确、小范围的基础操作问题。除用户明确要求增加的小功能外，不主动顺带扩功能；遇到实战阻塞先指出具体基础能力缺口，限定补充范围，不扩大成完整自动化工程。
- 验收关注操作是否可靠、反馈是否真实。画面是否满意由Agent复查判断；抽查或全面复查按用户当前要求，不自行加码。
- 保留图纸身份、正式图保护、未知写入不重放等执行安全边界；它们属于可靠操作，不是审美或工程判断算法。

Latest font-work clarification (2026-10-16): prefer an approximate name/use-based catalog, trial substitution and agent visual adjustment. Do not require exact collection, full measurements, anchor capture or fitting algorithms up front. Trial errors are acceptable; note exceptions and continue once the drawing looks satisfactory. Deeper font tests/tools are optional; do not expand Foundation merely for initial collection. This clarification governs the degree of checking described below.

Default CAD typography guidance (user confirmed 2026-10-16): when the task or domain Skill specifies text sizes or printing rules, follow those. Otherwise prefer ordinary Chinese text at 2.5–3.5 mm on the final paper; below 2 mm merits a readability check, not automatic rejection. Derive model text height and print scale from the actual frame and paper (A3 landscape is 420 mm wide). Scale serves readable composition while engineering dimensions and formal scale remain correct. Keep engineering dimensions, raw drawing coordinates, INSUNITS and paper scaling distinct. This is fallback guidance for agents, not a native execution guard.

Font issues use cad-font-diagnostics and the project's 字体资料库. With no explicit task/Skill/user replacement choice, diagnose first and ask whether to substitute existing fonts. A successful replacement preserves glyph semantics, visible height, each text object's length and anchor/position; changing a font name alone is incomplete. Separate original literal question marks from new missing glyphs. Database/FMP entries and cmap coverage do not prove native compatibility. Discuss proposed native interface extensions before implementation; stabilize text before moving to other drawing concerns.

Use the actual local tool catalog and schema. Current writes use a configured AutoCAD 2026 owned-copy profile; no tool name alone proves compatibility or authorization.

Select components by task before execution: Foundation alone for supported basic CAD; add cad-callout-pair-layout for this project's style/layout; prepare both that Skill and cad-distress-annotation for distress/demolition. Check actual versions, resources and tool schemas; installing a Skill does not add native operations. Report missing components using [installation entries](docs/COMPONENTS-AND-INSTALLATION.md), including actual release/installation status, and report missing backend capabilities separately. No automatic Skill installer exists. Reusable native operations and execution constraints belong in Foundation; domain decisions, styles and placement rules belong in Skills.

Bind explicitly and inspect drawing/process/session/module identity. Use fresh entity digests for existing objects and include all required associated members. Submit a write once with a stable request ID. After timeout or an ambiguous reply, query that original request; do not replay, change providers, delete journals or force-release a fence.

Effect state and verification are separate. A later verification failure does not roll back a committed write. Save affects the entire working drawing. Metadata is bind-time observation; do not infer continuing idle/writable state.

Keep source drawings and runtime credentials private. Use authorized working copies and synthetic public reproductions. Respect the documented profile limits and separate engineering Skills. Offline tests or documentation changes do not prove CAD execution; report the exact evidence and missing coverage.

Optional road-image interpretation uses [Agentic Map Tracing](https://github.com/capskov/agentic-map-tracing), loading `satellite-road-edge-tracing`. Domain judgment stays with the Agent; native execution stays with Foundation. Basic CAD does not require any interpretation Skill. Further components require their applicable maintainer review. These Agent instructions do not authorize external publication, configuration changes or tool fallback.

## Units, precision and plotting guidance (local.4, 2026-10-16)

Use `units: raw_drawing_units` for ordinary plans. Execute DWG coordinates and numeric sizes as supplied; do not infer physical units, convert coordinates, scale geometry or change INSUNITS. `expected_insunits` is optional: supply it only when the task needs metadata consistency. Unitless (`INSUNITS=0`) or unknown-unit drawings do not block raw operations. The legacy `metres` interface keeps its prior 0/6 metadata behavior for compatibility.

INSUNITS helps the Agent understand drawing conventions, choose engineering tolerances and plan output; it is auxiliary metadata. Distinguish numerical calculation/readback bounds from professional engineering acceptance. User/Skill/Agent determines the latter. Structure details may suggest millimetres and road plans may suggest metres, but drawing type alone proves neither. For unitless drawings use dimension examples, annotations and frames when physical units matter. Ask only when accurate size conversion, print scale or cross-drawing operations require missing evidence; otherwise proceed with raw operations. Existing drawing identity, request ownership, transactions and unknown-write protections remain required.

User or Skill typography and output rules take precedence. Otherwise derive model text height from the actual frame, paper and print scale: ordinary Chinese paper height preferably 2.5–3.5 mm; below 2 mm deserves inspection, not rejection. Scale serves clear expression while engineering dimensions and formal scale remain correct. The runtime package includes these Agent instructions; no automatic typography/layout or unit-inference engine is implied. See [unit and typography contract](docs/DRAWING-UNITS-AND-TYPOGRAPHY.zh-CN.md).

## Foundation versus Agent (user clarified 2026-10-16)

Release naming (user confirmed 2026-10-16): preserve the published, verified v1.2.0-local.4 tag and assets. Do not rebuild, retag or republish solely to change its name. On the next normal upgrade, consider v1.2.0-beta.1 when features are substantially formed and field testing continues; v1.2.0-rc.1 when functionality is approaching stability and pre-release verification begins; v1.2.0 when the declared supported scope meets formal release requirements. These are maturity-based choices, not scheduled releases or automatic promotion.

Foundation supplies small explicit native primitives and truthful execution/error/readback feedback. The Agent owns broad interpretation, difficult judgments, font choice, style/layout decisions, visual review and acceptance. Do not reimplement or repeatedly test Agent reasoning as Foundation algorithms. Font replacement and user-specified metric scaling are primitives; fitting factors, typography preferences and drawing-wide appearance are Agent/Skill work. Fix current execution blockers and important cases, then return to delivery; sampling/full review follows the task, not an imposed font-layout audit.


## 本机签名与加载授权 2026-10-28

用户已明确要求创建 capskov 本机自签名代码签名证书，并要求 Agent 自行点击信任、加载底座插件及本次维修组件，不再为相同加载操作反复询问。
先核对当前构建产物路径、manifest 哈希、签名指纹和原生插件身份；有签名时要求签名 Valid 且签名者为本机配置指定的 capskov。只处理已核实的底座本体及维护组件，不把这项授权扩展到未知发布者或其他软件。保留 SECURELOAD，不关闭安全校验；对当前已核实发布者的提示，Agent 应按授权处理信任并继续加载。
本机证书私钥保留于 CurrentUser\My 且不可导出；配置在被 Git 忽略的 private/signing/capskov-local.json。打包入口默认读取此配置，先签名再生成哈希与清单；不要对已验收或在用旧运行包原地重签，也不要把自签名描述为公共 CA 身份认证。其他机器需由其用户另行信任该发布者。
