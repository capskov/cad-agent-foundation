# 2026-10-29 另存副本接口维修记录

## 根因与修复

原生 save_copy 使用当前 Database.SaveAs 写临时文件。实机出现 DBMOD 从 0 变为 1，虽源文件哈希和已读回内容不变，仍违反源图状态不变的契约。新版仅接受已保存且 DBMOD=0 的图纸，以不覆盖的文件复制生成临时副本，再执行原有磁盘读回和发布校验。有未保存内容时明确拒绝，Agent 应先发出独立 save 请求，核验后再另存；不得偷偷丢弃未保存内容。

原生未知写入会携带诊断 result_json。服务端曾只允许该字段为空，误报为请求身份不一致。现在仅在 terminal unknown 时允许合法 JSON 诊断，身份、提交摘要、序列和保护锁检查保留。诊断不作为成功或解锁证据。

客户端读取遭拒且 job=null 时不再崩溃，保留返回结果。

## 本机验证

- 新运行包 artifacts/save-copy-fix-20261029-b320，插件 MVID f915d57f-3aa6-475d-b848-5fcf7b57cb53；本机 capskov 签名。
- AutoCAD 2026 独立测试副本：save_copy completed / committed / passed，DBMOD 0→0，双侧保护锁释放。测试 CAD 已正常关闭。
- 原生回执及 9 项反向篡改检查：10 项通过；扩展契约 191 项通过；客户端 job=null 回归通过。
- 原请求 plan-a3d7b0d920fe150a4240591a99af6182 使用保留原插件字节与身份的服务端诊断修复包读取成功，真实返回 unknown 及 save_copy 校验错误；未重放，未释放保护锁。
- 较宽的 FoundationLedgerContract 测试工程因已有链接依赖未包含 FoundationDrawingPlan / Newtonsoft 而编译失败，本次没有修改该工程；不将其算作通过。

## 原请求已经恢复

用户再次明确要求先修接口后，增加了仅针对本次旧 SaveAs 导致 DBMOD 0→1 的恢复接口。原图及工作目录交概算副本未被维修改写；临时文件以原请求指定的名字发布到原工作图同目录。没有重放 SaveAs，没有换请求、换 CAD 实例或删除日志。

恢复流程为：只读核对全部模型实体、图层、样式、布局、单位和源文件哈希 → 发布原临时副本且不覆盖目标 → 保存原始失败回执及诊断的审计记录 → 将已核实结果记录为已提交，原 result_json 字节保持不变 → 通过既有 journal API 暂存新验证 → 服务端独立验证 → 再次检查当前图和文件 → 既有 journal API 核销原生保护锁 → 服务端核销同一请求保护锁。

另补两处恢复收尾缺口：最终核销清除服务端不可用状态时，同时恢复独立验证标记；启动兼容已经完整核销、哈希与序列仍有效的旧恢复终态。旧版原生队列的永久暂停，由已签名维护接口在确认双侧完成、无任何原生保护锁或未处理写入、同一主线程/日志/处理器/白名单后恢复。

原请求当前状态 completed / committed / passed，序列 5；双侧保护锁均为 0。同一 CAD 实例的普通读取已成功返回两个对象，重新连接通过。恢复契约 35 项通过；启动及反向篡改检查 15 项通过。原始回执保留在原请求以及追加的 plugin_copy_recovery_audit 审计表中。

本机默认运行包：artifacts/save-copy-fixed-20261029-b321。保留已实测 b320 的原生插件字节与 MVID，仅更新服务端收尾修复并附维护组件。当前旧实例专用兼容入口：artifacts/save-copy-recovery-reconnect-20261029，其原生插件身份保持 c928260b-8313-42b8-92be-8b619722a09a。不能把新包强行绑定到不同原生插件身份的旧实例。

## 维护接口（受限于本次旧故障）

源码 tools/UnknownCopyRecovery；组件 Foundation.UnknownCopyRecovery3.dll，本机 capskov 签名。命令 CFCOPYRECONCILE3 读取原工作目录的 session.json、copy-recovery-input.json。输入必须明确原 request_id、context_id、expected_sequence 和原回执的 Foundation 哈希。

- inspect：只读检查，保留保护锁，不发布。
- stage：检查通过后仅发布原临时副本，记录带审计的已知提交，再通过原生 journal API 暂存验证，保留保护锁。需要查询原请求，让兼容服务端独立验证。
- finalize：检查服务端已验证同一阶段及原生/文件仍一致后，使用 journal 原有核销 API。然后查询原请求，让服务端核销。
- resume：仅在上述同一请求双侧核销且无未处理原生工作后，恢复旧版暂停队列；不重放任何作业。

这不是通用解锁接口。源文件变化、实体/图层/样式/布局变化、原请求或插件身份变化、原副本缺失或不同、已有目标与临时文件同时存在、阶段不匹配等均拒绝继续。仍禁止手工修改执行数据库来放行，以及删除日志或绕过未知请求保护。

证据：artifacts/save-copy-native-test-20261029；项目概算成果工作目录中的 copy-recovery-inspection.json、copy-recovery-proof.json、copy-recovery-completed.json、copy-recovery-final-read.json。恢复过程中提前被暂停队列拒绝的读取保持真实记录，不当作成功。没有发布公共版本。
