# 安装与第一次操作

本次提供源码，不附可运行二进制下载。[构建前置条件与当前打包缺项](BUILD.zh-CN.md)。下列命令描述已验收的本地包接口，必须有完整的带清单安装包才能使用。

## 先按任务选择组件

| 任务 | 所需组件 | 版本与限制 |
| --- | --- | --- |
| 已支持的基础 CAD 操作 | 仅 `cad-agent-foundation` | 本地基线 `v1.0.0-local`；公开源码预览`v1.0.0-rc.2`（运行源码不变），不附可运行二进制资产。有界二维操作，当前写入限米制，仅有记录的 Windows／AutoCAD 2026 宿主实测。无需两个 Skill。 |
| 本项目的引线标注样式与排布 | 底座 + `cad-callout-pair-layout` | 引线公开预览v0.3.0-rc.1（沿用v0.3规则）。默认与验证来自米制道路图；完整样式、排布尚未通过底座首版单独执行验收，写入前须核对后端覆盖。 |
| 本项目的病害／拆除标注工作流 | 底座 + `cad-distress-annotation` + `cad-callout-pair-layout` | 病害规则公开预览v0.1.0-rc.1。需要工程依据、分类规则及标注规则；完整流程仍需首版配置之外的能力，尚未验证仅靠这三个组件即可全部复现。 |

本页只安装底座。两个Skill已有独立公开ZIP，底座二进制仍待独立完成，没有自动安装 Skill 机制。先核对[组件与工具检查](COMPONENTS-AND-INSTALLATION.zh-CN.md)。

下文说明现有已验收本地接口。公开包与校验值待许可和隐私审查后补齐；源码目录本身不是已安装的可运行包。

准备 Windows x64、合法独立安装的 AutoCAD（匹配已验收宿主）、当前基线所需 .NET 8 Desktop Runtime、Python 3.11或以上。新 .NET 10 宿主未在本基线验证，不由年份号推断兼容。

```bat
foundation.cmd install --workspace "C:\CAD-work\demo01"
foundation.cmd start --workspace "C:\CAD-work\demo01" --source "C:\drawings\input.dwg" --write
foundation.cmd doctor --workspace "C:\CAD-work\demo01"
foundation.cmd apply --workspace "C:\CAD-work\demo01" --plan "plan.json"
foundation.cmd read --workspace "C:\CAD-work\demo01" --handles 实际返回句柄列表
foundation.cmd save --workspace "C:\CAD-work\demo01" --handles 实际返回句柄列表
foundation.cmd stop --workspace "C:\CAD-work\demo01"
```

plan.json 的完整合成例在[英文入门](QUICKSTART.md)。坐标由实际任务确认，不能拿示例代替工程定位；单位必须明确为米。省略 `--write` 时只读。安装使用权限允许的 AutoCAD 受信目录并核哈希，不降低可信加载设置。来源图只复制到工作目录 drawings/work.dwg，不自动覆盖来源。

CLI返回应同时核对 effects、verification、write_fence_released；进程退出码或请求已接受不是图纸操作成功的全部证据。相同计划重复调用会查询原请求，未知回应只能按原ID查询，不用新ID、删目录或更换提供者绕过。

修改既有对象需刚读到的 entity_digest；关联文字、引线与点组成员完整才可相应编辑。保存作用于整个工作图，包括该CAD实例其他未保存修改；最多64对象接受指定磁盘核对，不是全图工程正确性认证。停止要求空闲、已保存、自有单文档及无未决栅栏；不强杀CAD。关闭后如需再开，当前入口使用新工作目录，以保存后的图作新来源副本。

MCP配置片段在工作目录 mcp-config.json，按客户端要求自行配置本地stdio入口；安装不改全局MCP。CLI/MCP共用工作目录账本且互斥。MCP先绑定、核身份和加载版本，再传context_id/request_id调用；外部工具调用不自动纳入本底座事务。

工作目录包含连接凭据、工程几何与路径，保留为私有。反馈时使用最小合成例并去除具体工程信息，不能把整张工程图或完整运行目录上传公开Issue。
