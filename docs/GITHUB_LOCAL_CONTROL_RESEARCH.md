# GitHub 本机浏览器控制方案调研

核查日期：2026-09-04。结论来自上游说明及部分源码阅读，未安装第三方服务、未测第三方 NX 工作流，不能据此承诺时延或稳定性。

优先参考 Sunshine 的输入队列设计，使用 moonlight-web-stream 作为浏览器串流对照候选；noVNC 的事件限流与按键边界处理可直接用于设计审查。是否替换媒体架构，要由本机 NX 隔离副本的对比结果决定。

| 项目 | 已核查能力 / 代码位置 | 与本项目的关系 |
| --- | --- | --- |
| [LizardByte/Sunshine](https://github.com/LizardByte/Sunshine) | Windows 硬编串流主机；[src/input.cpp](https://github.com/LizardByte/Sunshine/blob/master/src/input.cpp) 的绝对鼠标 batch 分支保留最新位置，并要求参考宽高相同；处理线程先合并队列再调用平台输入 | 首选输入调度参考。完整桌面串流不能代替本项目应用级场景、命中与原生弹窗保护。Sunshine 自带网页是管理入口 |
| [MrCreativ3001/moonlight-web-stream](https://github.com/MrCreativ3001/moonlight-web-stream) | 非官方 Moonlight 浏览器客户端，把 Sunshine 流送进浏览器。README 提供 WebRTC 和 WebSocket/WebCodecs 模式；web/stream 下分 input、keyboard、transport、pipeline、video、stats | 功能形态最接近，值得独立对比。上游明确 master 为开发版，阅读时 README 指向 v2.10.0 发布文档；不能混用版本或声称此版本在本机稳定 |
| [novnc/noVNC](https://github.com/novnc/noVNC) | [core/rfb.js](https://github.com/novnc/noVNC/blob/master/core/rfb.js)：MOUSE_MOVE_DELAY=17ms，移动保存最新位置；按钮事件前先 flush 待发移动；README 提供本地光标、缩放、多种编码及 WebSocket 接入 | 适合参考鼠标事件顺序和高频限流。17ms 是其代码常量，不是本项目推荐值或输入延迟测量；VNC 服务端实际 NX/OpenGL 性能待测 |
| [rustdesk/rustdesk](https://github.com/rustdesk/rustdesk) | README 说明 libs/scrap 采集、libs/enigo 平台键鼠、src/server 输入/视频服务、src/platform 平台实现 | 适合查 Windows 输入/采集与释放机制，整体集成较大。本轮只核查结构说明，未完成这些实现的源码审计 |
| [selkies-project/selkies](https://github.com/selkies-project/selkies) | Linux 浏览器桌面流；当前 docs/start.md 描述默认 WebSocket、WebRTC 可选 | 可参考浏览器媒体结构，不能直接承载本机 Windows NX/TIA，不建议为此迁移系统 |

## 具体可借鉴的设计

1. 在场景版本与按钮状态不变的一段连续绝对移动中保留最新位置。按下/松开/滚轮/快捷键及场景切换是顺序边界，不跨边界合并。Sunshine 绝对移动分支与 noVNC 按钮前 flush 提供了可核查参考；不照搬其整桌面输入信任假设。
2. 原生钩子回调只入队，不做网络/场景扫描或长时间等待；现有 LOCAL_DEVICE_QUEUE_BUSY 表明本项目桥队列竞争需要独立修复。不能单纯加大队列把停顿变成更长延迟。
3. 引入从物理接收、队列等待、原生校验/提交到解码呈现的分段统计。浏览器本地光标移动不能作为 NX 已响应证明。
4. 同场景移动合并必须先有顺序/释放回归和真实 NX 拖动测试；视频丢帧须尊重 H.264 参考帧与恢复规则，不能随意丢 P 帧。

## 适用边界与对比计划

这些项目的浏览器/串流能力不等于同一 Windows 会话下的焦点隔离能力。README 中将 Sunshine 主机填为 localhost，描述的是网关到主机的连接，不能证明浏览器与 NX 同机操作兼容。没有查到足以宣称“安装后即可满足本项目全部 NX/TIA 本机验收”的证据。

当前 .NET/WGC/H.264/WS 路线保留。下一步先按现有证据解决移动排队、钩子队列锁争用与重复原生查询，再考虑隔离对比 Sunshine + moonlight-web-stream。对比需固定应用、模型、窗口位置、分辨率、编码器和真实动作，先确认同机焦点与原生命中；之后比较可见响应、输入释放、菜单/弹窗/快捷键。不用项目宣传帧率、star 数或 localhost ping 宣告性能通过。

本轮阅读开发分支源码仅用于参考，没有复制到产品实现。实际引入代码时固定版本并逐文件记录来源和许可；仓库可见标签中 moonlight-web-stream 为 GPL-3.0、RustDesk 为 AGPL-3.0、noVNC 主要为 MPL-2.0，具体以选定版本的 LICENSE 和文件头为准。

## 证据与限制

原始源码快照与 SHA256 位于 artifacts/research/github-local-control-20260904-152823。GitHub REST API 触发限流，commit/tree 查询未完成；目录中的早期仓库元数据 JSON 不可用作版本或维护状态证据，以 research-manifest.json 的失败标记为准。此次未取得固定 commit，不作最近提交日期、最新发布版本或活跃度排名结论。

后续实施已编入 [LOCAL-1 本机任务计划](./LOCAL_IMPLEMENTATION_PLAN.md)，下一项 LC-01；该文档中的参考方向不能视为第三方方案已部署。
