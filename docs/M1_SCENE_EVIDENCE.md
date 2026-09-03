# M1 关联窗口 GPU 场景与帧版本证据

2026-09-03 · SC01–SC04 / M1-01、M1-03、M1-04 的局部进展。完整目标仍 active，M1 未放行，网页没有产品键鼠输入。

## 1. 实现范围

- `WindowCatalog` 增加 DWM 可见边界、cloaked、Enabled、Layered、会话与相对 Z 序依据。`OwnedWindowScene` 选择同 PID / 创建时间 / 会话下 owner 链可追溯至根窗口的可见节点，排除无关窗与装饰阴影；重复身份、循环链、失效根窗和预算超限明确失败。
- `WgcOwnedNv12Source` 为节点持有独立 WGC 池及 GPU 缓存。按与实际 WGC 尺寸一致的 DWM/窗口边界合成；不把不同大小的边界强行拉伸。节点最多 8 个、各边最多 8192，累计源纹理像素与画布各不超过 16,777,216。每池 2 帧，编码在途最多 8 帧，历史场景最多 256 条。限制存在不代表已通过资源耐久验收。
- `GpuSceneCompositor` 以 D3D11 shader 和预乘 Alpha source-over 按相对 Z 序绘制，再用视频处理器转 NV12。真实画面不在 CPU 回读；实际测试像素回读仅在合成测试图的 GPU 单测内。
- 每个 MF 输入 sample 记录不可变 `ProbeSceneConfig`，按原始 100 ns 时间戳查回异步编码输出对应版本。浏览器收到 `sceneConfig` 后，以 chunk 时间戳关联实际 decoder output；旧版本回调不冒充当前场景，不更新当前可见场景记录。
- `--owned` 为本机显式实验开关。MediaProbe 仍固定启动时目标，只监听 127.0.0.1，拒绝网页改传 HWND/进程；没有产品鉴权，不可暴露 LAN/公网。

辅助进程识别、WinEvent 正式注册、完整 resize/device-lost 恢复、输入命中/sceneAck、JPEG、双应用、正式 Host/Agent 尚未完成。当前轮询周期为 100 ms；变化期尺寸不一致会中止有限实验，不能宣称已自动恢复。

## 2. 失败观察与修正

最初的合成版本只允许不透明节点。真实 NX 展开“文件”菜单时，浏览器已解码 3 帧后失败，错误为 `Layered window requires alpha compositor; opaque probe will not silently flatten it.`。未产生成功报告；错误保留在当次工具输出。本地后续枚举 `m1-owned-menu-layered.json` 不能代表失败瞬间的 Layered 状态，不据它断言具体瞬时窗口身份。

因此没有静默忽略弹窗或直接拷贝覆盖，而是实现预乘 Alpha 混合，并用真实 GPU 校验透明、半透明、全不透明三种像素及放置边界。该修正沿用设计 1.2，不改变主架构或验收门槛。

CLI `m1-owned-nx-01.h264/.json` 和 `media-20260903-165230-1971581/` 是 Alpha 修正前的历史构建，仅为早期证据，不能替代下面的最终版本回归。

## 3. 真实 NX 浏览器证据

环境为 NX 10.0.0.24、Intel Iris Xe、Windows 11 Home 25H2，本机 Chromium 152。编码尺寸 1280×720，Baseline `avc1.42401F`，解码色彩元数据 BT.709 / 限幅。源 NX 根窗 1632×727，与编码尺寸不同。

以下路径相对于 `artifacts/verification/`。每个报告均含实际二进制 SHA-256、原生场景历史及浏览器计数。

| 实验 | 实际结果 | 证据 |
| --- | --- | --- |
| 文件菜单展开/收起，60 秒 | 8 入/出/收/解码/呈现；场景 1→2→3→4，节点 1→2→2→1；浏览器视觉核对菜单出现及消失 | `media-probe/run-20260903-170016-5363746.json` |
| 主窗口静止，60 秒 | 1 入/出/收/解码；仅根窗，不作为文件框证据 | `media-probe/run-20260903-170133-7948718.json` |
| “打开文件”框已打开，60 秒 | 1 入/出/收/解码/呈现；2 节点，浏览器显示完整原生文件框 | `media-probe/run-20260903-170334-5800552.json` |
| 文件框取消，60 秒 | 17 入/出/收/解码/呈现；节点 2→1，主窗 Enabled false→true；浏览器确认文件框消失 | `media-probe/run-20260903-170749-2870355.json` |
| 主动停止后同进程重连，10 秒 | 停止时 1 帧已解码、页面记 FAIL（用户停止）、health busy=false；原服务未重启，重连 1 帧全量解码 | `media-probe/run-20260903-171022-7938719.json` |

菜单实验本地捕获 11 帧、替代 2 帧、送编码 8 帧；文件框取消实验捕获 26 帧、替代 5 帧、送编码 17 帧。多节点帧到达与合成帧不是同一个计数口径。静止源按变化出帧，不能把配置 FPS=30 写成实际每秒 30 帧。

取消实验的文件框为 `#32770`，owner 指向 NX 根窗。其窗口矩形为 (3082,418,814,626)，实际 WGC 匹配 DWM 可见矩形 (3088,418,802,620)，画布中放置于 (416,50)。保留这两个边界差异，供后续坐标输入测试，不能套单一 DPI 比例。句柄只作历史证据，续接必须重新枚举。独立枚举记录为 `m1-owned-filedialog-windows.json`。

菜单与文件框是 Computer Use 操作真实 NX 后，由项目自己的 WGC/GPU/H264/WS/WebCodecs 路径显示；**不是网页输入触发**。点击模态取消按钮时工具拒绝主窗目标，重新观察焦点后用 Esc 成功取消。未选文件、加载模型、编辑、保存或关闭原工程；NX 仍为只读 070.prt。Ctrl+O 曾未观察到文件框，对应根窗测试没有被误记为文件框通过。

### 构建身份

上述 17:00–17:10 五份报告及最终锁定构建的哈希一致：

- `Workbench.MediaProbe.dll`：`4ED8C8218ABA2B9A4FB236026790535726F122A7215C69B7713DDC4EB4BF12D2`
- `Workbench.Windows.dll`：`457744DF4C589CB8A11B2AC87EB22EDB0CCF5693A58544298D32432EA60C5EEF`

首次浏览器画面约 303–568 ms，属连接/初始化至首个解码呈现的短时观察，不是鼠标输入端到端时延或稳定性能承诺。实际 3D 旋转、细线颜色精度和典型工程仍待测试。

## 4. 最终回归

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| C# 测试 | 44 PASS、0 跳过；包含真实 GPU 的 15 像素 Alpha/放置校验、owner/几何/预算与异步元数据测试 | `media-20260903-171112-5551456/unit-tests.trx` |
| JavaScript 场景关联 | 5 PASS；延迟旧回调、不可变副本、非法几何/代次、时间戳重用与容量限制 | 同次 `verify-media.ps1` 输出；源码 `tests/scene-timeline.test.cjs` 哈希在 report 中 |
| 编码与 CLI | 硬件/软件各 90 入/出、JS 语法、非法请求与禁止覆盖通过 | `media-20260903-171112-5551456/report.json` |
| 四项目 locked restore/build | 0 警告/0 错误、8 冒烟 PASS | `probe-20260903-171145-7050358/report.json` 及 build.binlog |
| HTTP/WS 边界 | 8 PASS；同源握手、错误时长/重复参数/网页改目标被拒绝 | `media-probe/http-20260903-170909-6970533.json` |
| 最终构建测试图浏览器回归 | 300 帧全量解码/呈现，11 次动态像素检查、0 错误；与真实源报告的两个 DLL 哈希一致 | `media-probe/run-20260903-171254-0029141.json` |

人工延迟的逻辑测试证明元数据关联规则，不是实际编码器压力或丢包重建测试。真实测试中 staleOutputs=0，因此不拿它当“实际网络已触发并通过旧帧丢弃”的证据。

## 5. 检查点判定与下一步

| 子项 | 状态 | 仍需完成 |
| --- | --- | --- |
| SC01 关联与几何 | DONE（既定纯逻辑范围） | 正式多进程 app-family 在后续任务中扩展，不能套用同进程证明 |
| SC02 GPU 场景 | IN_PROGRESS | Windows 夹具、受控遮挡/透明源对照和资源趋势 |
| SC03 帧版本 | IN_PROGRESS | 元数据/旧回调单测与真实版本切换已有证据；正式 epoch 恢复与重连旧消息待测 |
| SC04 NX 菜单/弹窗 | IN_PROGRESS | 菜单与文件框显示/消失已有证据；编辑参数框及完整流程未测 |
| SC05–SC08 | NOT_STARTED | 产品完整输入、双窗/JPEG、真实连续动态与 NX 放行报告 |

下一项先补 Windows 场景夹具与 NX 编辑参数框，再将同版本几何连接到最小产品输入链，覆盖模态遮挡命中、右/中键、滚轮、修饰键、中文和主动释放。仅用测试副本，先 720p 再 1080p；真实源十分钟、资源趋势、双应用/JPEG 和完整 M1 门槛均不得省略。TIA、第二客户端、CF 与八小时仍需后续真实验收。A15 和 M1 不因本次画面实验整体 PASS。

实现参考：[D3DCompile](https://learn.microsoft.com/en-us/windows/win32/api/d3dcompiler/nf-d3dcompiler-d3dcompile)、[D3D11 混合状态](https://learn.microsoft.com/en-us/windows/win32/direct3d11/d3d10-graphics-programming-guide-blend-state)。读取的厂商资料用于 API 配置；本项目是否有效以以上真实 GPU 与浏览器证据为限。

收尾：最终测试服务已 Ctrl+C 停止，临时媒体浏览器页已关闭，没有后台实验待轮询。原 Demo 三文件与 legacy 副本的 SHA-256 仍一致；NX 文件框已取消，原只读模型未编辑/保存，未操作 PLC 或改动系统/许可证/CF 配置。
