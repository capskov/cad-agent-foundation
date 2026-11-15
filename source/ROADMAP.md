# Roadmap

## 首要任务：为智能 Agent 提供可靠操作，而非编程替代判断

用户于2026-10-16反复明确，本条置于最前，指导后续开发、取舍和验收：

- 底座开发是实战任务的辅助工作。首要目标是提供可靠、可控的基础CAD操作和真实反馈，让Agent能够执行自己的判断并持续向前推进。
- 按Agent会越来越聪明来设计：审美、结构理解、样图学习、规律归纳、复杂取舍和最终看图复查由Agent承担；不要提前把这些能力固化为程序、规则引擎、拟合算法或强制验证流程。
- 现有操作足以执行，且Agent综合判断可以继续时，就推进主线。不要为了代替Agent判断，再写一套程序或反复验证。
- 编程解决明确、小范围的基础操作问题。除用户明确要求增加的小功能外，不主动顺带扩功能；遇到实战阻塞先指出具体基础能力缺口，限定补充范围，不扩大成完整自动化工程。
- 验收关注操作是否可靠、反馈是否真实。画面是否满意由Agent复查判断；抽查或全面复查按用户当前要求，不自行加码。
- 保留图纸身份、正式图保护、未知写入不重放等执行安全边界；它们属于可靠操作，不是审美或工程判断算法。

## Active phase two — approved 2026-10-15

A–P is the accepted development scope: reliability, routine CAD, drawing operations, then diagnostics/compatibility. M1–M5 remain; M6 batch plotting and M7 physical-printer monitoring are separate and deferred. Local 1.2.0-local.1 implements a bounded first slice; remaining A–P items stay active. See [implementation, exact operations, remaining scope and validation boundaries](docs/PHASE2-DEVELOPMENT-20261015.zh-CN.md). No Skill business rules were moved into Foundation.


## Current bounded release

v1.1.0-rc.1 provides a Windows runtime preview and 25 native plan operations, including four DIMENSION types, styles, layer state, boundary/hatch continuation, linked callouts and guarded same-drawing edits. See [catalog](docs/COMMAND-CATALOG-v1.1.0-rc.1.md) and [native validation](docs/VALIDATION-v1.1.0-rc.1.md). Two independently published optional Skills provide domain rules and finite placement proposals.

## Next practical improvements

Prioritize concrete field feedback, clearer diagnostics and installed-font examples. Additional entity coverage needs native association, rollback and saved-DWG evidence. Layout/plotting, xrefs, cross-drawing transfer, complex-hole/Boolean repair and unit handling for file output require separate contracts. AutoCAD 2022 and updated .NET 10 hosts remain unverified.

Historical H1/J10/arc-hatch milestones remain closed. No open-ended fault-matrix work, arbitrary-code route or automatic Skill installer is included. No comparative speed/token-cost advantage is claimed without comparative measurements.


## 2026-10-16 local font update

Local 1.2.0-local.3 adds guarded existing-style SHX font-pair replacement and anchor-preserving DBText metric scaling. See [scope and parameters](docs/FONT-REPLACEMENT-LOCAL3.zh-CN.md). Native evidence is scoped; prior public releases and global MCP registration are unchanged.

## 2026-10-16 unit closeout

Local 1.2.0-local.4 makes raw-mode unit metadata checking optional, preserves legacy metre calls, and ships Agent unit/precision/typography guidance. Ordinary raw writes no longer require physical-unit interpretation. PDF/DXF restrictions and other features are deferred; the next release is a separate user-directed step.
