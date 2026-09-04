# 开发目标与跨电脑续接正文

更新：2026-09-04，本机访问范围 LOCAL-1。先按 [换电脑交接](./DEVELOPMENT_HANDOFF.md) 确認最新工作区已迁移。已有目标沿用，不重复创建；本文件不表示项目完成。

## 可直接复制的续接正文

```text
继续当前 UG / 博图本机浏览器控制项目。先检查实际项目目录的 Git 状态和文件完整性，再依次读取：
1. docs/PROJECT_STATUS.md 最新入口及最近记录
2. docs/LOCAL_IMPLEMENTATION_PLAN.md
3. docs/DEVELOPMENT_HANDOFF.md
4. docs/GITHUB_LOCAL_CONTROL_RESEARCH.md
5. docs/CODEX_AUTOMATION_PLAN.md、docs/IMPLEMENTATION_CONTRACTS.md、docs/ACCEPTANCE.md、docs/REFERENCES.md

用户已明确变更：只需要在运行 NX/TIA 的同一台电脑通过浏览器控制，不再要求第二台客户端、LAN、公网/CF或代理兼容。换电脑继续开发不改变该产品范围。远程专属历史验收已被范围变更覆盖，不能标为 PASS；其他真实操作、输入安全、恢复、8小时与发布交付要求保留。

从最近未完成且依赖满足的 LC 任务继续。最新增量已实现 LC-01 分段埋点、LC-02 有序移动合并、LC-03 SPSC物理桥，C#174/JS60与原生夹具有限测试通过；LC-06固定便携依赖、独立本地启动/停止已验证，尚未配对/串流。下一项是 LC-01 剩余全链路基线、LC-02/03 实体复测，再据开销推进 LC-04 原生校验、LC-05 媒体延迟及 LC-06 同机焦点/输入兼容和真实A/B，最后 LC-07/SC07/SC08。读 LOCAL_INPUT_QUEUE_EVIDENCE.md 与 OPEN_SOURCE_COMPARISON.md，不重复实现已通过公共部分。若 PROJECT_STATUS 后续已推进，直接接最新下一项，不重做已完成任务。

参考 Sunshine/noVNC 的输入调度，评估 Sunshine + moonlight-web-stream 浏览器对照。早期研究快照不是固定版本；新的 config/open-source-comparison.lock.json 已固定发行版、commit与哈希，使用已验证脚本并核对许可。不能直接替换架构或把开源项目宣传性能当 NX 实测。默认保留 React/TypeScript、.NET10 Host/DesktopAgent、WGC、H.264硬编 + WS/WebCodecs、JPEG。现有同机入口是实验探针，尚非正式产品。重大路线变更必须有隔离对比证据、设计说明和回退方法。

先测量并解决已记录的输入积压、LOCAL_DEVICE_QUEUE_BUSY、DISPLAY_ACK_TIMEOUT。SCENE_CHANGED 已有暂停/释放/新场景确认修复，不重复重写。合并移动必须尊重租约/目标/epoch/scene/按键边界，不能重放失败编辑；保留前台、命中、窗口身份、输入释放和锁屏保护。不要仅靠放宽超时、增大队列或提高标称帧率宣告解决。

不重复需求访谈。保留原 Demo、未提交/未跟踪文件及用户工程。检查并复用锁定工具链，不把系统 SDK 误作目标版本。跨电脑克隆可能不含最新未提交源码、.tools、artifacts 和工程；先核对，缺历史证据要如实记录。当前同机方案依赖双物理屏幕与 NX 可见，这是实现限制，不是用户新确认的产品限制；网页 NX/TIA 双栏仍须实现。

正常编码、依赖准备、临时副本测试、构建和修复直接推进。缺 NX/TIA/许可证/厂商支持系统/测试工程时，只把相关真实测试子项标 WAITING_EXTERNAL，继续独立工作并写明恢复步骤。涉及费用、系统变更、许可证或真实设备写入先交付可审阅准备材料。不自动购买、不强制关闭或保存未保存工程、不向真实 PLC 下载或运行程序。没有用户明确要求，不启动多智能体。

每完成一个连贯任务更新 PROJECT_STATUS：编号、实现、验证、证据、剩余风险、下一项。上下文压缩或换会话后按该下一项续接。既有 M0–M8 / SC07/SC08 门槛保留；M1 未过不得把正式 UI/服务铺设当主投入。

所有性能上限仍是待验收目标。用协议/状态测试、自有 Windows 夹具、真实 NX/TIA、用户实体操作和8小时耐久分层验证；不使用 mock、截图、ping 或成功返回代替可见响应和真实工作流。只有本机范围下主文档第15节全部有效条件满足，交付源码、Windows发布包、运行说明、哈希、真实报告和回滚方法后才能完成；存在必需真实验收缺项只能交付标明范围的候选版本。沿用已有目标，不因文档交接或暂时缺外部环境而标整个目标完成。
```
