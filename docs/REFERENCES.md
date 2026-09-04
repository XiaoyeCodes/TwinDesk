# 环境基线、原文件指纹与资料依据

> **范围变更（用户确认，2026-09-04）：本版仅要求同一台电脑通过浏览器控制真实 NX / TIA。第二台客户端、LAN 访问、公网/CF 及代理兼容不再是本版交付门槛，旧文中对应远程专属要求由本条覆盖，不能计为 PASS。真实建模/工程操作、菜单/弹窗/中文/保存、单应用与双应用布局、输入安全、恢复、诊断、8 小时耐久、打包及回滚要求继续有效。本机双屏是当前实验实现条件，不代表用户新确认了双显示器产品限制。主技术路线尚未变更；详见 PROJECT_STATUS.md 最新范围记录。**

核对日期：2026-09-03。外部资料证明 API 或产品能力，不证明本项目已经实现或在当前机器运行成功。

## 1. 用户提供的材料

- 原 PPT：`C:/Users/happy/xwechat_files/wxid_trd1nwpuybgj22_1dc1/msg/file/2026-09/数字孪生软件平台_Web系统功能说明框架_专业详细版.pptx`，共 16 页。
- 已读取实际第 5、6、7 页的完整文本，并对照用户提供的三张截图。第 5 页“界面嵌入”，第 6 页“技术路线”，第 7 页“实施步骤”。
- PPT 中“UG 功能受控”和安全限制，按用户后续确认修正为允许完整建模编辑、使用被控端原生文件操作；不据此擅自增加功能白名单或文件沙箱。
- 原 Demo 来自 `files.7z`，已解压在项目根目录。只读检查了 `server.py`、`index.html`、`README.md`。

主项目目录：`C:/Users/happy/Documents/ChatGPT/UG浏览器项目`。

## 2. Demo 原文件 SHA-256

| 文件 | SHA-256 |
| --- | --- |
| server.py | E1D3E2911540F4312D06A98565C359689DDD72412E72E2E3136A055F5B3A52B0 |
| index.html | 939D96149884F9F66389BE96D9963CC9522CBD852660BF27F2F7A9594CBE6AB9 |
| README.md | 091C1AE68192FF393A82684DF0B5B09FE75C5E43100C567FAFEE423361F9D6D9 |

基线取得时间为本次设计期间，不表示原 Demo 经过运行验收。项目中的 `.idea`、`.git` 和先前下载的 360 安装包不属于本次软件实现范围，不删除、不顺手加入交付包。

## 3. 本机只读检查结果

| 项目 | 检查结果 | 说明 |
| --- | --- | --- |
| 操作系统 | Windows 11 家庭版中文版，25H2 | CIM 与注册表联合核对；旧 ProductName 字段仍显示 Windows 10，不能单独据此判定系统 |
| 系统版本 | 10.0.26200.9168，64 位 | 当前实验环境，不等于 Siemens 官方认证配置 |
| CPU | Intel Core i9-13900H | 无压力测试 |
| 内存 | 可见约 31.7 GiB | 通常对应标称 32 GB |
| 图形 | Intel Iris Xe Graphics | 驱动 32.0.101.7076；已有真实 WGC/D3D/Intel H264/本机浏览器短时链路与测试图证据；完整场景、1080p、双路待验证 |
| 显示模式 | 主屏 2560 × 1440 / 120 DPI；第二屏 1920 × 1080 / 96 DPI | 来自 m0-displays.json；未改变显示设置，NX 窗口 DPI 与第二屏不同 |
| 其他显示设备 | OrayIddDriver Device | 未调用/改动，不作为本项目的依赖 |
| NX | Siemens NX 10.0.0.24 | 注册表和 exe 版本一致 |
| NX 程序 | `D:/Program Files/Siemens/NX 10.0/UGII/ugraf.exe` | 不保证直接调用 exe 就等价于官方启动快捷方式 |
| TIA | 本次未确认可用安装 | 用户表示使用最新版本；方案暂以已核实 V21 为基线 |
| .NET SDK | 系统既有 9.0.109、9.0.304；项目本地 10.0.400 | 本轮 .tools/dotnet/dotnet.exe --version 确认 10.0.400 |
| Node | 项目本地 v24.20.0 | 本轮版本命令确认；不是前端已实现的证据 |
| 原生程序验收 | 尚无完整通过报告 | 已有真实 NX 首帧及测试图实际编码/浏览器十分钟报告；真实建模、动态/弹窗、输入和八小时耐久仍待验证 |

## 4. 技术来源与用于本方案的结论

| 来源 | 核实的有限结论 | 方案位置 |
| --- | --- | --- |
| [Cloudflare Tunnel FAQ](https://developers.cloudflare.com/cloudflare-one/faq/cloudflare-tunnels-faq/) | Tunnel 支持 WebSocket | 选择同源 WS/WSS 作为 CF 兼容通道 |
| [CF 公网大文件与流媒体说明](https://developers.cloudflare.com/cloudflare-one/faq/cloudflare-tunnels-faq/#large-file-and-streaming-traffic-through-tunnel) | 公网域名路由的视频/大文件有服务使用条件，不能从 WS 支持推断免费无限媒体用途 | 新增 E10；确认适用性前不承诺长期公网方案，不规避条件 |
| [Cloudflare WebSockets](https://developers.cloudflare.com/network/websockets/) | 长连接可因边缘更新/空闲中断，需心跳及恢复 | 重连、心跳、幂等与按键释放 |
| [Windows Screen Capture](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture) | WGC 提供显示器/窗口捕获，需检查支持、处理尺寸和设备变化 | 采集主路径及环境探针 |
| [CreateForWindow](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow) | 可通过 HWND 创建目标窗口采集对象 | 窗口绑定，不据此推断全部弹窗自动包含 |
| [GetWindowRect 边界语义](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowrect) | 窗口矩形可含不可见边框，存在 DPI 虚拟化；DWM 可见边界另有语义 | 1.2 场景几何必须与实际 WGC 尺寸对应，不强行拉伸 |
| [DWM 窗口属性](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute) | 提供可见边界及 cloaked 状态查询 | 关联窗口有效性和捕获边界核对 |
| [D3D11 CopySubresourceRegion](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-copysubresourceregion) | GPU 区域拷贝不提供缩放/混合，越界参数行为未定义 | 透明合成单独实现与测试，拷贝前检查边界 |
| [Desktop Duplication API](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api) | DXGI 复制显示输出并提供更新区域 | 受限的屏幕捕获后备，不混称后台窗口采集 |
| [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput) | 可模拟输入，受 UIPI 和现有键盘状态影响 | 同权限、单输入状态机和主动释放 |
| [MFTEnumEx](https://learn.microsoft.com/en-us/windows/win32/api/mfapi/nf-mfapi-mftenumex) | 可枚举同步/异步/硬件 MFT；硬件类型采用异步处理 | 真实枚举与运行硬件编码探针 |
| [H.264 Video Encoder](https://learn.microsoft.com/en-us/windows/win32/medfound/h-264-video-encoder) | Windows 提供 H.264 编码接口与参数说明 | 编码方案；不是“Intel 编码器已经通过”的证据 |
| [低延迟属性](https://learn.microsoft.com/en-us/windows/win32/medfound/codecapi-avlowlatencymode) | 可请求低延迟模式，质量与功能受实现影响 | 按实际编码器能力配置，不保证统一行为 |
| [W3C WebCodecs VideoDecoder](https://www.w3.org/TR/webcodecs/#videodecoder-interface) | 需要安全上下文，可用 Worker，提供支持检测和解码队列信息 | HTTPS、解码 Worker、能力检测；以原始规范为依据 |
| [W3C AVC WebCodecs 注册说明](https://www.w3.org/TR/webcodecs-avc-codec-registration/) | Annex B/AVC 数据和参数集有不同表示要求；并非所有实现必须支持 H.264 | access unit、SPS/PPS/IDR、码流协商；所读版本为草案说明 |
| [.NET 支持说明](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support) | .NET 10 为 LTS，支持到 2028 年 11 月 | 目标运行时，实施时锁定实际 SDK 和补丁 |
| [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) | Windows 图形互操作绑定，含现代 .NET 支持 | 候选原生绑定；NuGet 版本以 M0/M1 验证为准 |
| [Microsoft RDP 主机要求](https://learn.microsoft.com/en-us/windows-server/remote/remote-desktop-services/remotepc/remote-desktop-allow-access) | Windows Home 不能作为原生 RDP 主机 | 不选择当前机器上的 RDP 作为第一版主线 |
| [TIA Portal V21 官方发布信息](https://press.siemens.com/global/en/pressrelease/tia-portal-v21-combines-engineering-efficiency-higher-plant-availability) | 西门子已发布/介绍 V21 | 当前可核实的版本基线，不能推断不存在后续补丁 |
| [V21 软件安装必要条件](https://www.ad.siemens.com.cn/download/materialaggregation_3824.html) | Home 24H2 支持带基础版限制；说明软硬件和工程版本兼容条件 | 当前 Home 25H2 与所需工程组件需专项核对 |
| [TIA V21 安装系统要求](https://docs.tia.siemens.cloud/r/es-es/v21/instalacion/requisitos-del-sistema-para-la-instalacion/requisitos-generales-de-software-y-hardware) | 西门子官方 V21 安装支持列表 | 与中文官方安装资料交叉核对 |
| [OpenAI 目标工作流](https://learn.chatgpt.com/zh-Hans/use-cases/follow-goals) | `/goal` 用于有明确完成条件的持续工作，应提供计划与验证依据 | 目标启动文本、状态记录和可验证停止条件 |

## 5. 证据边界

资料支持 API 的存在和能力描述，不保证它们组合后在 NX 10、当前驱动和 TIA 中达到规划性能。具体组合必须经过 M1 与 M7。

30 FPS、延迟阈值、编码码率、资源上限、超时和测试时长均为本项目设计的目标或初始参数，不引用成官方保证。Host/Agent 双进程、应用场景合成和任务拆分是本项目的工程设计。

OpenAI Docs 的目标工作流影响了本次交付形式：将架构、实现契约、验收和状态文件与目标正文配套，明确阶段检查点与完成定义。本轮未新建/完成目标，未修改应用设置或创建自动化任务。当前目标实际状态应由工具读取，不应从早期文档“尚未启动”字样推断。

## 6. 本轮 1.1 复核新增记录

- 再次读取 PPT 第 5、6、7 页完整文本并对照用户截图；PPT 保持原样。
- 根目录与 legacy/demo-v3 的三份 Demo 文件重新核对 SHA-256 一致；本轮不修改源码。
- 已核对 .NET 10.0.400、Node v24.20.0 的实际版本输出。旧 artifacts/verification/environment.json 生成时的 ready=false 是历史快照，不代表当前工具仍缺失；下一次实施应刷新报告，不改写历史证据。
- 新增故障安全账本、视频代次、静止/失效判定、任务依赖矩阵及 CF 使用条件门槛。这些为设计复核结果，未声称现有代码已实现。

## 7. 设计交付校订

- 只读复核 `artifacts/verification/probe-20260903-151616-2305538/report.json`、`m0-displays.json`、`m1-encoder-activation.json`，校正主规划书及本表早期“只有探针雏形”的表述。未重跑真实软件测试，未改写历史报告。
- 重新读取 PPT 第 5–7 页及原 Demo；原文件与 legacy 副本 SHA-256 一致。
- 再次检索并打开 OpenAI 目标工作流、Microsoft 捕获/.NET 支持、W3C AVC 注册规范、Siemens V21 发布及安装条件、Cloudflare Tunnel FAQ。方案保持 1.1 的协议和验收标准，不因文档校订改变架构。
- 下次实施仍以 PROJECT_STATUS 最新的逐项状态为准。早期复核段落中的“下一次刷新报告”等是历史记录，不应据此重复已经完成的准备工作。

## 8. 后续媒体实施证据

实际编码、异步格式重新协商、本机 WebCodecs、取消重连和采样结果见 [M1_MEDIA_EVIDENCE.md](./M1_MEDIA_EVIDENCE.md)。其中严格区分测试图与 NX、局部与完整、十分钟旧构建与最终短时复测构建。浏览器报告含最终二进制哈希，测试报告含源码指纹；以上设计资料本身不能替代这些运行证据。

## 9. 完整规划书交付复核

再次核对 PPT 第 5–7 页、三份原 Demo 及副本哈希、六份设计/状态文档和媒体证据边界。重新打开 OpenAI 目标工作流、CF Tunnel FAQ、Siemens V21 安装必要条件及 W3C AVC 注册规范，相关边界未改变。Presentations 技能用于读取源 PPT，未编辑幻灯片；OpenAI Docs 用于核实目标执行入口与可验证检查点，未修改 Codex 设置或重复创建目标。

当前工作树中新出现的 WGC/NV12 源接入只记为待验证增量，不能沿用历史构建的 PASS。主书第 14.1 节与 PROJECT_STATUS 已明确后续入口。本次没有重新执行真实采集、输入或耐久实验，不增加功能验收 PASS。

## 10. 后续真实窗口实施证据

设计交付后已继续实现并测试 WGC/D3D 真实源，见 [M1_WGC_GPU_EVIDENCE.md](./M1_WGC_GPU_EVIDENCE.md)。该记录包含新增配置的 Microsoft 依据、真实浏览器报告、源类型/构建身份区分及菜单缺失的失败观察。第 9 节是之前设计轮的历史状态，不再代表源接入完全未测；正式菜单合成、输入及工作流仍未通过。

## 11. 1.2 关联窗口设计依据

本次只读核对 PPT 实际第 5–7 页、三份 Demo 及副本哈希、M1_WGC_GPU_EVIDENCE、菜单窗口快照与当前媒体入口/解码代码。确认独立菜单问题有历史实测依据，固定场景版本仍是当前单窗口探针的局限；没有重新执行软件操作或媒体测试。

重新读取 Microsoft 窗口矩形与 GPU 拷贝说明，补充实现契约第 4.4 节的边界、透明合成和资源生命周期要求；不可变帧版本、SC01–SC08、预算与失败策略是项目设计，不是厂商保证。复核官方目标工作流、Cloudflare Tunnel FAQ、Siemens V21 安装条件及 W3C AVC 注册说明，原有产品范围与外部依赖不变。

Presentations 技能只用于源 PPT 内容核对；OpenAI Docs 影响目标正文的检查点和完成条件。未编辑 PPT、安装软件、改变 Codex 设置、新建或完成目标。

## 12. 后续关联窗口实施证据

设计 1.2 后已实现有界同进程 owner 场景、D3D11 预乘 Alpha 合成和异步帧版本关联，真实 NX 文件菜单及原生文件框可在同一流显示/消失。证据、失败观察、最终构建哈希与 SC 状态见 [M1_SCENE_EVIDENCE.md](./M1_SCENE_EVIDENCE.md)。先前关于“固定场景版本、没有合成”的段落是历史记录；当前仍无产品输入与完整工作流验收。

本次新增实现参考为 Microsoft [D3DCompile](https://learn.microsoft.com/en-us/windows/win32/api/d3dcompiler/nf-d3dcompiler-d3dcompile) 和 [D3D11 混合状态](https://learn.microsoft.com/en-us/windows/win32/direct3d11/d3d10-graphics-programming-guide-blend-state)。它们说明 shader 编译和混合配置，不保证 NX/TIA 的所有弹窗行为。44 项 C#、5 项 JS 及实际浏览器短时结果按各自范围记证据。Computer Use 只用于真实 NX 菜单/文件框的观察和打开/取消，不能冒充项目输入链；未编辑/保存模型、未改变系统/许可或公网设置。

## 13. 2026-09-04 场景夹具与输入核心

当前结果见 [场景夹具复测](./M1_FIXTURE_EVIDENCE.md) 和 [M1-02 输入核心](./M1_INPUT_CORE_EVIDENCE.md)。重新核对 Microsoft SendInput、KEYBDINPUT、MOUSEINPUT 的原始文档，落实部分提交不能假定未执行、Unicode 与扫描码分离、虚拟桌面物理坐标等实现约束。原生后端尚未接入，本次 L0 测试不证明实际注入成功。

Computer Use 技能影响本轮真实 NX 验证：新建框索引/焦点不可靠时停止文本操作并取消，明确保留 N0/编辑参数框未验证。没有用其他 UI 自动化绕过这个限制，仍继续独立输入核心实现；没有把工具操作或测试计数冒充网页远控验收。

## 14. 2026-09-04 JPEG 兼容增量

依据 Microsoft [BitmapEncoder](https://learn.microsoft.com/zh-cn/uwp/api/windows.graphics.imaging.bitmapencoder?view=winrt-26100) 提供的内置 JPEG 编码器与 SetPixelData 接口，增加显式 BGRA 读回兼容分支；API 文档仅支持接口选择，不证明 NX 兼容或性能。实际 Windows 编解码往返、真实 NX 同流菜单和浏览器测试分别记于 [M1_JPEG_EVIDENCE.md](./M1_JPEG_EVIDENCE.md)。H.264 主路径仍不调用CPU像素读回。

本轮 Computer Use 能打开/取消 NX 新建框，但字段索引不可用且键盘选择没有可靠可见结果，因此取消，没有创建或保存N0；之后只展开/收起文件菜单以核对实际JPEG媒体。工具点击不计为NX产品输入成功。

## 15. 2026-09-04 真实副本、1080p与尺寸恢复

用户提供样本后，通过Computer Use原生文件框/建模命令完成隔离副本的移动面编辑、取消、保存重开；真实同流参数证据见 [M1_NX_MODEL_EVIDENCE.md](./M1_NX_MODEL_EVIDENCE.md)。技能约束使字段索引失效时改用观察过的原生编辑菜单，未绕过工具注入；原件不改。

重新核对Microsoft [屏幕采集与尺寸变化](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture)：帧池尺寸变化需要重建，帧ContentSize与表面尺寸应分开处理。项目采用退役整个有界节点并重新枚举、分配新输入代次的实现，保持既有身份/几何契约；8次/2秒恢复预算是项目初始限制，不是官方性能保证。该修复由真实NX尺寸变化失败驱动，没有改动WGC/H264主路线或删验收项。

## 16. 2026-09-04 SC03 帧生命周期

[SC03增量](./M1_FRAME_LIFETIME_EVIDENCE.md) 区分L0合成回调、真实Windows夹具经WGC/硬编/浏览器的延迟实验，以及JPEG取消重连。两个失败原始报告保留；浏览器计时器调用接收者与图像所有权修正有对应回归。没有把最初的后台节流猜测写成唯一实测根因，没有将夹具当成真实NX/TIA或十分钟/八小时证据。本轮未做NX原生UI输入。

## 17. 2026-09-04 Closed资源隔离

[资源隔离报告](./M1_RESOURCE_ISOLATION.md) 记录当前进程句柄类型原生查询、Window/item/Closed订阅与原生token的对照，链接Microsoft SDK ABI、NtQueryObject与phnt定义。实际结果不支持显式释放item主引用能修复，已撤回；仍复用WinRT委托marshal的token试验也增长，不能据此断言唯一平台根因。没有改主技术路线或取消关闭安全检测。

后续自建COM sink对照排除CsWinRT委托marshal为必要条件；实际采集改用按目标PID过滤的窗口销毁通知。依据 [Microsoft SetWinEventHook](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook)，out-of-context事件在注册线程的消息循环异步送达，因此独立线程接收、显式保活委托并在同线程卸载hook。120次实际窗口销毁和根窗关闭有分层证据；窗口快速复用/锁屏/正式Agent故障仍需要后续压力矩阵，不能引用API文档代替验收。

## 18. 2026-09-04 瞬态窗口竞态

实际CreateForWindow对已消失HWND返回E_INVALIDARG的证据来自本机确定性原生夹具；官方[CreateForWindow](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow)只规定接口/HRESULT，[GetWindowThreadProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowthreadprocessid)规定无效HWND返回0。恢复同时核对两项，不把所有参数异常视为可重试。历史NX错误未证实同源，详见[竞态报告](./M1_TRANSIENT_WINDOW_EVIDENCE.md)。

## 18. 2026-09-04 共享设备与捕获生命周期

[共享捕获证据](./M1_SHARED_CAPTURE_EVIDENCE.md)记录每设备Draw、同设备MF管理器/NV12原语、两路WGC并发颜色及60次重新取流。微软[ID3D11Multithread](https://learn.microsoft.com/en-us/windows/win32/api/d3d11_4/nn-d3d11_4-id3d11multithread)说明上下文保护和Enter/Leave；原生锁仅围绕渲染，不跨WGC StartCapture。微软[设备创建标志](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_create_device_flag)说明诊断线程选项；实际对照未改善Event斜率，主路径未改标志。应用重建ALPC增长及设备销毁后未回收仍保留为未通过项。

## 19. 2026-09-04 前端依赖与资源结论更新

通过官方npm registry元数据选择精确版本并保存完整完整性锁文件。来源为 https://registry.npmjs.org/react/19.2.8 、 https://registry.npmjs.org/react-dom/19.2.8 、 https://registry.npmjs.org/typescript/7.0.2 、 https://registry.npmjs.org/vite/8.2.2 和 https://registry.npmjs.org/@vitejs%2fplugin-react/6.1.1 。实际隔离安装/类型检查/构建证据在 artifacts/verification/web-dependencies-20260904-124409-1977090/report.json；包可安装不证明产品UI已实现。

资源结论以M1_RESOURCE_ISOLATION的12:53记录为准：约130秒后ALPC回收、180次真实应用重建资源自行回落；上节短时“未回收”是历史观察，不能再称已证实泄漏或厂商根因。有限SC02通过不替代8小时或真实GPU故障验收。

### 2026-09-04 SC06 Unicode 原始消息诊断

- [KEYBDINPUT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-keybdinput)：UNICODE/KEYUP生成VK_PACKET消息；文档说明不是本机已收到消息的证据，实际原始计数另见报告。
- [Control.ProcessKeyEventArgs（.NET 10）](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.control.processkeyeventargs?view=windowsdesktop-10.0)：WinForms键消息到托管事件的处理边界。两字符自身窗口20项验证与真实F0长文本诊断分开记录，不由文档推断已修复。

同机实验新增依据：参见 [LOCAL_NX_CONSOLE.md](./LOCAL_NX_CONSOLE.md) 中的 Microsoft 低级鼠标/键盘钩子及注入标记官方文档；短回调、消息线程和静默卸钩限制不得省略。

## 本机范围调研（2026-09-04）

- noVNC 官方：https://novnc.com/info.html 。浏览器 VNC 客户端，需 VNC 服务和 WebSocket 接入。
- Guacamole 官方架构：https://guacamole.apache.org/doc/gug/guacamole-architecture.html 。浏览器、网关及 RDP/VNC 目标；不是本机 NX 嵌入证明。
- Sunshine 官方：https://docs.lizardbyte.dev/projects/sunshine/latest/ 。硬编串流与 Moonlight；Web UI 描述为配置/配对。
- Moonlight 官方：https://moonlight-stream.org/ 。客户端串流，不能把 PC/ChromeOS 客户端等同于普通 Windows 浏览器网页。
- Microsoft WebView2：https://learn.microsoft.com/en-us/microsoft-edge/webview2/landing/ 。网页嵌入原生应用，不能据此承诺 NX 原生窗口嵌入兼容。
- Microsoft RDP：https://learn.microsoft.com/en-us/windows-server/remote/remote-desktop-services/remotepc/remote-desktop-allow-access 。Windows Home 不提供受支持的 RDP 主机能力；不自动升级或绕过授权。

## GitHub 源码调研补充（2026-09-04）

见 [本机控制开源参考](./GITHUB_LOCAL_CONTROL_RESEARCH.md)。新增 Sunshine + 非官方 moonlight-web-stream 浏览器候选；读取 Sunshine/noVNC 输入处理源码，保留原始快照与哈希。只有调研证据，没有第三方 NX 实测结论。
