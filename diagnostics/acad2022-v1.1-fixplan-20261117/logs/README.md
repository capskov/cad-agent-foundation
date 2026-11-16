# logs/ 说明

| 文件 | 来源 | 说明 |
| --- | --- | --- |
| `apply-v5-dimension-rejected.json` | `cad2022-test/pilot-loop-08/apply-v5.json` | D1：4 条 `linear_dimension` 提交被拒，`unexpected or missing plan fields` + held fence 提示 |
| `apply-5a-request-id-conflict.json` | `cad2022-test/pilot-loop-09/apply-5a.json` | D3：修正 plan 后沿用同 `request_id`，报 `Request ID already belongs to different parameters` |
| `apply-6a-session-busy.json` | `cad2022-test/pilot-loop-11/apply-6a.json` | D4：`start` 未落盘即 `apply`，报 `This workspace already has a CLI or MCP session. Close it first.` |
| `apply-6d-settext-verification-failed-summary.json` | `cad2022-test/pilot-loop-16/apply-6d.json` | D5：`set_text` 插件侧 passed / 服务端 failed（`foundation_plan_readback_mismatch,plugin_stage_not_passed`），`write_fence_released=false`、`write_blocked=true`；原文件 25967 B，此处保留全部字段，仅把 24 KB 的符号表 `result_json` 折叠为占位说明 |

原始大日志（未上传，避免仓库膨胀）：`cad2022-test/pilot-loop-*/server.log`，
其中 `pilot-loop-15`（197 KB）与 `pilot-loop-16`（342 KB）对应 D5 事故；
`pilot-loop-08`（2.4 KB）与 `pilot-loop-11`（2.0 KB）对应 D1/D4 的会话启动失败日志。
