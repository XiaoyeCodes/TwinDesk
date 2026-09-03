# M1 真实窗口 GPU 编码与浏览器证据

2026-09-03 · M1-01 / M1-03 局部进展。M1 仍为 IN_PROGRESS，完整目标继续 active。

## 1. 本次实现

- WGC 的 IDirect3DSurface 经 IDirect3DDxgiInterfaceAccess 获取真实 D3D11 纹理；D3D11 视频处理器将 BGRA 等比缩放、加黑边并转换为 NV12。
- 每个提交 sample 持有独立 GPU 输出纹理，使用 MFCreateDXGISurfaceBuffer 和 IMFDXGIDeviceManager 交给声明 D3D11Aware 的编码器。视频像素不在 CPU 回读；压缩码流复制到网络缓冲区不属于像素回读。
- 设置 GPU BT.709 / NV12 限幅及相应 MF 色彩元数据；设置 MF_LOW_LATENCY。编码在途限制 8 帧，原始帧池 2 帧，丢弃被更新替代的原始帧而不丢编码参考帧。
- CLI 新增 `--encode-window`；MediaProbe 只接受本机启动时固定的 process + HWND。浏览器不能通过参数更换窗口。原测试图路径保留，真实源返回 null 时明确失败，禁止偷偷改播测试图。
- 真实源按观察时长运行，静止时无新捕获不自动失败；有待编码输出且停滞则有界失败。窗口身份改变、尺寸变化或最小化需重绑，不声称已经实现正式恢复。
- 临时 `--inspect-graphics-api` 分支已经移除。

主要源码：`WgcNv12Source.cs`、`IProbeFrameSource.cs`、`GraphicsCaptureInterop.cs`、`H264Probe.cs`、两个 Probe 的 Program、MediaProbe 页面；扩充媒体与握手验证脚本及测试。

## 2. 真实环境与操作边界

沿用当前 NX 10.0.0.24 / Intel Iris Xe / Windows 11 Home 25H2。开始时 NX 最小化，经 Computer Use 恢复。当前打开的 070.prt 为只读，加工界面，模型主要位于视口边缘；本轮观察主界面、菜单及光标变化，未用它冒充完整 3D 旋转或建模测试。

测试工具展开并收起一次“文件”菜单，没有执行打开、编辑、保存、关闭工程或 PLC 操作。这是工具辅助的原生观察，**不是项目浏览器输入链的证据**。网页目前不注入任何键鼠。

所有路径下述均相对于 `artifacts/verification/`，原报告不覆盖。

## 3. 已有结果

| 实验 | 结果与范围 | 证据 |
| --- | --- | --- |
| NX GPU 初次编码，未启用低延迟 | 1 输入 / 1 输出；首个输出约 3032 ms，在排空阶段才返回 | `m1-nx-gpu-01.json/.h264` |
| 同源启用 MF_LOW_LATENCY 后 | 1 输入 / 1 输出；首个输出约 75 ms，在排空前返回 | `m1-nx-gpu-02.json/.h264` |
| NX 真实窗口至浏览器，10 秒 | 1 收 / 1 解码；已视觉核对 NX 菜单栏、树和视口；浏览器首帧约 681 ms | `media-probe/run-20260903-161712-7946424.json` |
| 真实源 60 秒，含原生界面变化 | 捕获 10 帧，替代 3 帧，7 入 / 出 / 收 / 解码；浏览器首帧约 368 ms | `media-probe/run-20260903-161832-0371436.json` |
| NX 文件菜单单独 WGC | 445×614 真实截图，已视觉核对菜单内容 | `m1-nx-filemenu-wgc.png/.json` |
| 文件菜单单独 GPU 编码 | 1 入 / 出；首个输出约 55 ms；尚不是合成或远程操作 | `m1-nx-filemenu-gpu.h264/.json` |
| 逻辑与边界回归 | 23 PASS；其中 null 源保护测试仅调用 MF 初始化，不编码真实内容 | `media-20260903-162115-0808334/unit-tests.trx` |
| 编码及 CLI 回归 | 硬件/软件各 90 入 / 出、JS 语法、5 类无效窗口请求在写产物前拒绝、旧证据保护通过 | 同目录 `report.json` |
| 四项目锁定构建与冒烟 | 0 错误 / 0 警告，8 项冒烟 PASS，不重复 NX 截图 | `probe-20260903-162216-3486557/` |
| HTTP + 真正 WS Upgrade 请求 | 8 PASS，包括非法 Origin、时长、重复参数和浏览器更换 HWND 参数被拒绝 | `media-probe/http-20260903-162253-3316582.json` |

实际浏览器解码色彩元数据为 fullRange=false、matrix/primaries/transfer=bt709。元数据匹配不等于色板数值、细线或 HDR 验收通过。1280×720 是编码尺寸，不是源窗口尺寸或实际显示 FPS。上述时延分别属于编码段或连接至首次解码绘制，均不代表用户输入至可见响应。

### 构建身份

每个浏览器报告记录 MediaProbe 与 Windows DLL 的 SHA-256；回归 report.json 记录相关源码哈希。16:17 / 16:18 报告属于边界保护更新前的构建，不能套用到后续任意代码。

最终构建复测：

- `media-probe/run-20260903-162317-7840727.json`：真实 NX 10 秒，1 入 / 出 / 收 / 解码；首个编码输出约 67 ms，浏览器首帧约 584 ms。
- 之后开始 60 秒实验并在约 24.6 秒主动取消，1 帧已解码；页面显示“用户停止；未记为通过”，服务 busy=false。核实仍为 PID 38908 / 创建时间 16:22:42.933474，没有重启服务。
- `media-probe/run-20260903-162438-1209415.json`：上述同进程重新连接完成真实源 10 秒，1 入 / 出 / 收 / 解码；浏览器首帧约 357 ms。该取消不涉及产品输入释放，因为探针没有输入。
- `media-probe/run-20260903-162645-9858695.json`：同一最终二进制以默认测试图模式重启后，300 帧全量解码，11 次像素检查、0 错误。

上述三个最终报告的二进制 SHA-256 一致：MediaProbe `87FBAB058839555B7C17EC263B850507813F37932EA70BB149E3DBAB8E1CEFA9`，Windows `2DBA7E876D325C9F5F35BF1B277A6B34AA8F906C31D8B93BD4030015B29628C2`。

16:28 收尾：测试服务均已结束，相关进程枚举为空，临时浏览器页关闭；没有后台实验待续接。原 Demo 三份哈希仍与基线一致。NX 保持已恢复状态，文件菜单已收起，原工程没有编辑或保存。

## 4. 已证实的缺口：主窗口不包含文件菜单

同一时段，原生窗口快照显示文件菜单仍打开，浏览器主窗口流未显示该菜单。重新枚举得到菜单为独立 Afx owned 窗口，owner 指向 NX 主窗口；旁边的 SysShadow 不作为实际菜单内容。枚举证据为 `m1-nx-menu-windows.json`，其中 HWND 仅为当时身份，后续不得硬编码。

对菜单自身 HWND 的 WGC 与 GPU 编码均成功，说明下一步应实现按 owner/窗口身份关联的多节点捕获与合成。该观察支持现有设计，不需要把方案改成无限制整桌面远控。

**判定：主窗口媒体链路通过局部检查，关联菜单完整性未通过，A15 不得 PASS。** 页面中的媒体 PASS 只表示该选定窗口的收发/解码计数一致，不检测场景是否包含全部应用弹窗。

## 5. 下一步与复跑

1. 构建有限 Windows 夹具和真实窗口场景探针；先覆盖 owner 菜单与模态窗口，再与 NX 文件框、参数框逐项对照。
2. 将几何、层级、源纹理与坐标命中按同一场景版本组织；超出支持场景必须明确失败，不静默裁掉弹窗。
3. 补项目完整输入链、NX 实际视图运动与建模、JPEG 对照、双路、1080p，以及真实主线十分钟门槛。
4. TIA / 第二电脑 / CF / 八小时耐久仍待相应真实环境与实现；本轮不升级任何正式验收项。

复跑命令及安全边界见 `tools/Workbench.MediaProbe/README.md`。当前有限探针不是正式 Host/Agent；没有产品鉴权、控制租约或完整媒体健康/重建机制，不能公开暴露。

实现依据：[D3D11 色彩空间](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ns-d3d11-d3d11_video_processor_color_space)、[D3D11 范围枚举](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_video_processor_nominal_range)、[MF 色彩范围](https://learn.microsoft.com/en-us/windows/win32/medfound/mf-mt-video-nominal-range-attribute)、[MF 低延迟属性](https://learn.microsoft.com/en-us/windows/win32/medfound/mf-low-latency)、[H.264 编码器](https://learn.microsoft.com/en-us/windows/win32/medfound/h-264-video-encoder)。这些资料是配置依据，运行结论来自上述本机证据。
