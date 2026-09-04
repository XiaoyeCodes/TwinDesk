# LC-01 / LC-02 / LC-03 输入队列候选记录

2026-09-04，基于已推送提交 e93ad38af2a4a72a3a149553993b49b9825a6b64 的工作区增量。状态为 IN_PROGRESS：逻辑回归与有限原生夹具通过，实体设备、真实 NX 可见响应尚待复测。

## 本次实现

- 本机模式网页采用一个未应答普通操作，连续未发送 Move 只保留最新绝对位置；按键、按钮、滚轮形成边界。每个客户端实例固定租约/目标，合并标识还包含 host/stream/epoch/scene 与当前按钮/键状态。序号在实际发送时分配，按钮之前的位置仍按序发送。普通 F0 输入路径不变。
- ReleaseAll 清除尚未发送的动作并立即发送；旧应答不会带出已取消手势。场景变化、失焦和失败清除队列。64 条网页待发上限、250ms 旧普通操作保护不能被当作会话使用时限；松键继续允许，过期编辑不重放。没有恢复 7 秒或固定体验时长。
- LocalConsoleBridge 从 List + Monitor.TryEnter 改为容量 128 的单生产者/单消费者队列。两个原生钩子均在同一消息线程生产；控制监视器消费，生产者不因消费端短暂持锁退出。槽位不可变，发布顺序使用 Volatile，满队列仍停止/释放。只在消费批内安全累加连续同 scene 的相对移动；键、按钮、滚轮及 scene 边界不合并。实际物理设备高负载能力尚待测。
- 物理事件带接收时 scene，浏览器不把旧 scene 的移动/按下重新标成新场景；已持有输入的松开可以释放。
- 原生身份、前台、交互桌面、命中、绑定世代和发送前校验没有删除或缓存。本次没有把输入改为后台 PostMessage，也没有更换媒体架构。

## 埋点解释

LoopbackInputProbe.Diagnostics 中新增执行器 queueWait/nativeDispatch/nativeChecks 和 localConsole.queue.age/maximumDepth。C# 每个分布仅留最近 256 个样本，另有累计 Total；Samples=0 表示未采到，不是 0ms 性能结论。原生队列年龄在消费端统计，不给物理钩子增加统计锁。

网页 summary.inputScheduling 包含 merged/dropped/maxDepth/pending/browserQueueWait，最多保留 256 个等待样本。已有 inputRoundTrips 留最近 64 项，界面显示输入往返 P50/P95 和当次派发时间；这是提交往返，**不包含 NX 绘制和视频呈现**。browserQueueWait 与往返用网页时钟，执行器/桥/原生耗时用进程内单调时钟，各域之间不直接相减；不同统计可能包含同一段耗时，不能相加当端到端。

LC-01 未完成：仍需物理事件到浏览器、原生 SendInput 单独耗时、捕获/编码/解码/呈现完整链路及可见响应基线。LC-04 要先看 nativeChecks 与 nativeDispatch 的实测占比，再决定昂贵查询的优化边界。

## 已取得证据

| 层次 | 结果 | 原始文件与范围 |
| --- | --- | --- |
| L0 C# | PASS，174 项 | artifacts/verification/local-input-queue-20260904-155829-9253685/unit-tests.trx；包括环形回绕/容量、相对移动边界与溢出、过期、十万条事件并发顺序、分布窗口 |
| L0 JS | PASS，60 项 | 同目录 javascript.txt；含真实客户端类的本机 hello/应答/场景/blur 集成逻辑；无真实浏览器/native 响应认定 |
| 构建 | PASS，0 警告/0 错误 | MediaProbe；同目录 report.json 有源码哈希。仓库基提交不代表未提交增量已被推送 |
| L1 原生桥 | PASS，有限范围 | artifacts/verification/local-queue-native-20260904-154832-3878619/report.json；自有窗口前台，SendInput 2 条、钩子识别注入 3 条、物理计数 0、转发 0、观察 down 1/up 2、最终释放；watchdog 停止。不是实体鼠标测试，也不是 NX 工作流 |
| L2/L3 NX/实体键鼠 | NOT_RUN | 本轮未对 NX 注入操作；观察到 NX 当前为 070-test.prt（只读），未擅自切换、保存或关闭。旧版均值44～54ms输入派发不是新版基线 |

原生夹具当次 Workbench.Windows.dll SHA256：2B75CE665CA2C406E49623EF3ECE79187986EFA48AD637EA964661CD880DDC24；夹具 DLL：E42CEF603AB3D220E005206348B78B7913D1A0F4566651D63C684DCCD5FE6172。之后仅移除一个未使用 using 并补充网页测试，最新源码哈希以最终 L0 report.json 为准；不混称同一个二进制。

## 复现与下一项

```powershell
./scripts/verify-local-input-queue.ps1
# 以下原生夹具需要可交互 Windows；出现窗口后将自有夹具置前台。
. ./scripts/environment.ps1
& $Dotnet build tools/Workbench.DesktopFixture
$queueNativeEvidence = Join-Path $RepoRoot ('artifacts/verification/local-queue-native-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
& $Dotnet tools/Workbench.DesktopFixture/bin/Debug/net10.0-windows10.0.19041.0/Workbench.DesktopFixture.dll --verify-local-console $queueNativeEvidence
```

真实复测：先在 NX 原生界面核对可写隔离副本完整路径，再运行 start-local-nx-console.ps1 -VerifiedCopy <该副本>。本轮为构建已停止 8091，未擅自切换当前只读工程或自动恢复接管。当前候选仍需 NX 可见、浏览器与 NX 分处物理两屏，点击连接后橙色十字接管，F12 退出。

用户实体测试依次覆盖快速移动、滚轮、中键旋转/平移、连续按下松开、原生右键/快捷键、场景/菜单变化、F12/断线/失焦释放。记录失败码、队列峰值/合并量/年龄/输入提交往返，以及真实画面响应；可用同时看见物理动作和网页响应的高速视频逐帧计时，注明帧率/误差与样本数。P04 仍需 ≥200 个可见响应样本、P95≤200ms，完整建模/保存/中文和 8 小时仍未通过。不能把橙色十字移动或成功应答计为 NX 响应。

回退需先停止当前探针并确认释放，再在独立目录检出 e93ad38 构建旧版；不要 reset/clean 当前工作区。原件和现有工程不参与回滚。
