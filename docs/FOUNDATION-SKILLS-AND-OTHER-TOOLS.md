# Foundation, Skills and other CAD tools

| Component | Responsibility | Current boundary |
|---|---|---|
| CAD Agent Foundation | Supported native CAD operations, drawing identity, durable requests, guarded changes and readback | A configured local execution route; not a universal wrapper for every CAD backend |
| cad-callout-pair-layout | Annotation style, anchor/side choices and finite placement rules | Existing road-derived Skill; full Foundation-only compatibility is not certified |
| cad-distress-annotation | Evidence-based distress/demolition scope, boundary and quantity rules | Existing road workflow; not generic engineering judgment supplied by the core |
| Another MCP, COM or CAD tool | Its own operations and execution behavior | Separate backend; no automatic Foundation transaction/journal/fence coverage |

An agent or Skill can choose among available tools. For a supported operation routed through Foundation, the current governed contract applies. A direct call to another backend does not enter that contract simply because Foundation is also installed. Reading an externally created object afterward can inspect its present facts; it does not retroactively govern the write or establish its request ownership.

Avoid conflicting writes to the same working drawing. An unresolved Foundation write is not permission to rerun the operation through another tool. Unknown outcomes and execution fences remain binding. Explicit backend integration would be future work, not an existing transparent adapter.

## Native vocabulary versus domain workflow

The current plan vocabulary is `layer`, `line`, `circle`, `polyline`, `text`, `mtext`, `callout`, `hatch`, `set_text`, `move`, `archive`, and separate whole-document `save`. Each has bounded inputs and supported object semantics. It is not every command from the integrated upstream server.

Creating descriptive LEADER/MText/dot callouts is different from creating AutoCAD DIMENSION or MLeader objects. New polyline creation is different from arbitrary reshaping of existing boundaries. Associative hatch from a new boundary is different from repairing any old hatch. Layer creation/reuse is different from unrestricted layer edits.

Future reusable native primitives belong in the core with their execution and verification contract. Domain selection, engineering interpretation, naming, placement and style rules belong in Skills. A Skill may contain planning or geometry helpers as well as instructions; those do not make it a replacement for a native execution backend. Adding a Skill does not automatically add a missing core operation.

## Coordinates, units and potential applications

WCS defines the CAD reference frame. A map projection defines how geographic positions become planar coordinates. Drawing units define what a numeric length means. These are separate concepts. Foundation accepts already prepared drawing coordinates and performs no projection, CRS inference or unit conversion.

The existing civil workflows use metre-based coordinates. The current implementation enforces `units: metres` and accepts metre drawings or explicitly metre-based unitless drawings. Other units must not be advertised as supported until the gate, unit-dependent checks and necessary workflow validation have been adapted. Merely changing a unit label is insufficient.

The intended core is useful beyond roads. For example, a future planting Skill could select species labels, hatch categories and callout positions, then request supported native drawing operations. Millimetre drawings would also require appropriate text heights, dot radii, fill scales and checks. This is a potential extension, not a completed landscape or millimetre workflow in v1.0.0-local.

## 中文说明

底座负责受支持操作的原生执行与证据；Skill 负责专业任务的判断、组织和排布。两者在同一条工作链上配合，职责不同。Skill 可以选择工具和携带规划算法，但不能仅靠升级规则就让底座多出一个尚未实现的尺寸标注命令。

其他 MCP/COM 可以并用，但外部直接写入不自动进入底座的事务、请求记录和写后核验。事后读到一个对象，只能检查当时的事实，不能补成对先前写入全过程的治理。不能换工具绕过未知写入，也应避免在同一工作图上产生相互冲突的写入。

图纸按自身单位表达坐标和尺寸；WCS 不代表米制，高斯投影也不负责设定标注尺寸。当前首版实际限制米制，其他单位尚待适配。园林植物等分类标注可作为以后扩展方向，现有道路 Skill 的默认尺寸和道路侧规则不能直接当作园林验收结果。

## Independent component development / 独立开发与接口边界

Foundation and Skills can have independent repositories, development cycles and releases. Foundation owns reusable native operations, execution safety/status and object-management contracts. Skills own domain rules, style parameters, placement algorithms and business workflows. Skills invoke the actual stable, versioned Foundation interfaces; they must check schema/version/operation coverage, not assume every AutoCAD operation is wrapped. Current interfaces and evidence are listed in the [v1.0.0-rc.1 catalog](COMMAND-CATALOG-v1.0.0-rc.1.md). Complete migration of existing Skills is not certified.

底座与Skill可独立开发、建仓、发布。底座维护可复用原生操作、执行安全／状态和对象管理契约；Skill维护行业规则、样式参数、排布算法与业务流程，通过实际开放的稳定版本接口调用，并核对schema／版本／覆盖范围。缺接口不等于Skill已经实现该原生能力；现有Skill全流程底座化仍未认证。见[版本清单](COMMAND-CATALOG-v1.0.0-rc.1.zh-CN.md)。


## v1.1.0-rc.1 scoped native evidence

See [the new acceptance summary](VALIDATION-v1.1.0-rc.1.md). The prior evidence above remains historical. Native DIMENSION, named style creation/reuse, layer states, closed-surface/callout continuation and same-drawing transforms are new scoped operations, not proof of every parameter/entity combination.
