# LC-01 分段计时与有限媒体实测（2026-09-24）

状态：**IN_PROGRESS**。本轮补齐原生输入 SendInput 调用耗时、WGC 源轮询、MFT 输入调用与输入到编码输出驻留，以及浏览器接包到解码回调、Canvas drawImage 调用耗时。所有分布只保留最近 256 条样本并报告总数；不同进程时钟不相减。没有引入跨进程同步时钟或把分段之和称为端到端延迟。

浏览器接包时间在 WebSocket onmessage 入口记录，因此 receiveToDecodeMs 包含本页消息处理队列和浏览器解码等待。encoderResidenceMs 从 MFT ProcessInput 前到匹配时间戳编码输出被读取，包含编码器内部排队；不等同纯 GPU 执行时间。SourcePollMs 包含无新帧的 WGC 轮询。drawCallMs 只计同步 drawImage 调用，**不包括浏览器合成/显示器扫描**。inputDiagnostics.nativeSends 只计传输调用，成功返回也不证明 NX 已响应。帧时间戳仅用于同流关联，不跨主机/网页时钟计算延迟。

| 分层 | 原始证据 | 有效结果 | 范围 |
| --- | --- | --- | --- |
| C# / JS 与构建 | artifacts/verification/local-input-queue-20260924-095414-0583094 | 175 C#、63 JS 通过；MediaProbe 构建 0 警告/错误 | 协议、队列、计时关联；不能当真实 NX 验收 |
| 硬编视频测试图 | artifacts/verification/media-probe/run-20260924-095128-0494040.json，SHA256 51D6B8DC4665DCD92D9CD4FC385A36A3104B869360391D2D09847A96BE614788 | 300 帧接收/解码/呈现；动态像素 11 次匹配。Intel QSV H.264；最近 256 帧编码器驻留 P95 13.9986 ms，浏览器接包到解码 P95 0.9 ms | 1280×720、约10秒、生成图，非窗口/WGC/NX/输入 |
| 自有 Windows 窗口及 owner 弹窗 | artifacts/verification/media-probe/run-20260924-095322-9195289.json，SHA256 3296A0E43AB97F621FD823A7E02D8B450E5FEC7BCE893646EA9AF96B43A8E254 | 31 帧接收/解码/呈现；场景 1～15；源轮询 P95 8.5883 ms（213次，含无新帧轮询），编码器驻留 P95 23.9506 ms（31帧），浏览器接包到解码 P95 1.2 ms（31帧） | WGC/GPU合成/硬编/WS/WebCodecs 有限实际链路，不是 NX 或真实操作 |
| 生成 H.264 文件独立核验 | artifacts/verification/lc01-media-timing-20260924-094606-4959000/generated-result.json | 90 输入/输出帧、Intel QSV 实际激活；驻留 P95 14.2708 ms | 无浏览器、无 NX；仅验证新编码计时账本随真实 MFT 跑通 |
| 完整媒体回归 | artifacts/verification/media-20260924-095541-1709235/report.json | 175 C#、63 JS，硬编与软编各30帧，负向 CLI 检查通过 | 回归范围，不是实体 NX 性能证明 |

自有窗口的帧到达间隔 P95 约752 ms，因为夹具每约700 ms 切换一次 owner 弹窗，静止画面不强制重复帧。这个数字不是操控延迟。两次浏览器运行均由实际页面报告 PASS，但仅按上述有限媒体范围记账。网页的 0.2～0.5 ms drawImage 只说明同步绘制调用耗时短，不代表屏幕已显示新画面。

本次检查时 NX 进程 30044 显示 `NX 10 - 加工 - [070.prt （只读） ]`，原生枚举判定最小化；未恢复、注入、保存或关闭。尚无新版物理鼠标与 NX 响应、原生菜单/文件对话框、连续建模、P04 的不少于200个可见响应样本，也未完成8小时及 TIA 真实验证。用户已澄清仍为**运行 NX 的同一台电脑打开浏览器**，没有恢复异机局域网验收。

下一步先在可写隔离 NX 副本上测完整闭环：观察目标前台/scene，实体鼠标快速移动、中键/滚轮/右键/快捷键，记录新增 nativeChecks、nativeSends、执行器和网页队列统计，同时逐帧测可见 NX 响应。用相同分辨率/动作比较优化前后；如果输入检查占主导，再推进 LC-04 的身份/命中查询优化；如果视频积压占主导，再推进 LC-05。当前数据不足以决定替换主路线或估算“还差多少毫秒”。

复现媒体部分：构建 `Workbench.MediaProbe` 后无参数启动，打开 `http://127.0.0.1:8091/` 并运行10秒测试图；自有夹具用 `Workbench.DesktopFixture --media-scenes` 启动，再枚举唯一可见的 `TwinDesk SC03 media scene fixture — NOT NX / TIA` 句柄，以 `Workbench.MediaProbe --process dotnet --window <句柄> --owned` 启动只读 WGC 探针。每次记录报告、源码和运行 DLL 哈希。若要测 NX 只读视频，先让 NX 窗口可见，再枚举当次 ugraf 句柄并以 `--process ugraf --window <句柄> --owned` 启动；此命令不启用输入。
