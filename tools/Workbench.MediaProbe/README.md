# M1 实时媒体探针

只监听 `http://127.0.0.1:8091`。默认编码生成的 NV12 灰阶与移动方块；可通过本机启动参数选择一个已运行的真实窗口。两种模式均不注入输入、不启动 NX/TIA。它没有正式 Host 鉴权，严禁开放 LAN/公网；真实窗口模式也只用于用户授权的本机诊断。

```powershell
. ./scripts/environment.ps1
& $Dotnet restore tools/Workbench.MediaProbe --locked-mode
& $Dotnet run --project tools/Workbench.MediaProbe --no-restore
```

用本机支持 WebCodecs 的浏览器打开地址，选 10 秒、60 秒或 10 分钟，再点击开始。输出使用真实 Intel/当前首个可用硬件 H264 MFT；没有硬编会明确失败，不静默软件替代。一次只允许一个编码连接，Origin/Host 严格限制为上述入口。

真实窗口模式先用 `Workbench.Probe --process ugraf --list` 枚举，选择当时有效的 HWND；不要复制历史报告里的句柄。停止已有 MediaProbe 后启动：

```powershell
& $Dotnet run --project tools/Workbench.MediaProbe --no-restore -- --process ugraf --window <当前句柄>
```

窗口必须未最小化。启动时固定 PID、创建时间及 HWND 身份；网页不能传入进程或句柄更换目标。窗口尺寸变化、身份失效或最小化时本探针明确失败，需重新绑定；正式产品的自动恢复尚未实现。真实源持续时间是观察时长上限，不是承诺每秒产生 30 个新帧；静止 WGC 窗口可能只产生一帧。

关联窗口实验使用同一入口，将 `--owned` 放在最后：

```powershell
& $Dotnet run --project tools/Workbench.MediaProbe --no-restore -- --process ugraf --window <当前句柄> --owned
```

仅收纳同进程、同进程创建时间、同会话且 owner 链可追溯至所选根窗口的可见窗口，排除无关窗和装饰阴影。每节点独立 WGC 采集，按真实捕获边界及相对 Z 序使用预乘 Alpha GPU 混合，然后整幅转 NV12；节点最多 8 个，源纹理累计像素及合成画布分别限制为 16,777,216。辅助进程和未知归属不自动放行，不能假定所有 TIA 弹窗都已覆盖。

原生快照每 100 ms 更新；场景变化产生递增版本。采集时保存版本，通过 MF sample 时间戳关联到异步编码输出，再用浏览器 chunk 时间戳关联解码回调；旧回调不会改贴新版本。`sceneConfig` 先于该版本帧，网页记录真正呈现的 `sceneDisplays`。这是无输入探针的关联验证，还没有产品 sceneAck/输入命中与 epoch 恢复状态机。

传输 v1 的 40 字节头及 Annex B access unit；编码器首次协商出实际 SPS 后发送 codecString 和源类型。浏览器检查协议字段、连续序号、解码帧数；仅测试图模式检查移动方块像素，真实窗口模式将这两项记为 null，另外记录实际解码色彩信息。完整结束时浏览器发送 JSON 报告，服务器保存在项目工作目录下 `artifacts/verification/media-probe/`，含实际二进制哈希。这些报告不证明完整 NX/TIA 工作流、输入时延、生产 CPU 指标或异机能力。

点击停止/关闭标签页会取消实验，服务释放编码器；可以重新开始。服务按 Ctrl+C 退出。失败时不保留成功报告，终端保留错误；浏览器失败原因也显示在页面上。

## 当前边界

- 测试图是 CPU 生成 NV12；真实源为 WGC BGRA → D3D11 视频处理 → NV12 GPU sample → MFT，不进行 CPU 像素回读。压缩码流仍需拷贝到网络缓冲区。
- 每个提交帧持有独立 NV12 纹理，编码在途最多 8 帧；当前未使用正式纹理池。设置低延迟 MFT 属性后，真实静止窗口首帧已实测可在排空前输出。
- 真实源显式配置 BT.709、NV12 限幅与黑边等比缩放；浏览器已读到匹配色彩元数据，但完整色板/细线精度验收仍待执行，不承诺 HDR 支持。
- 不带 `--owned` 仍只采指定窗口；带该参数已有真实 NX 文件菜单展开/收起和“打开文件”对话框显示/取消的同流证据。完整编辑参数框、跨进程辅助窗口、透明区输入命中、遮挡和尺寸恢复仍待验证。不能把页面媒体链路 PASS 解释为菜单可操作。
- 编码器支持异步硬件和同步软件；此页面只走硬件，CLI 可以明确测试软件。
- 编码流固定 1280×720、30 FPS、Baseline；实际协商结果记录在报告。
- 这是有限长探针，使用有界轮询接收 MFT 事件；正式 Agent 需事件回调、图形线程、Worker、静止健康判断和 epoch/IDR 重建。
- 目前背压超过上限直接失败，不把丢失 P 帧后的花屏当作恢复成功。
- 浏览器测试图首帧时间不代表用户输入至真实工程可见回显的时间；像素检查验证内容，没有测显示器物理扫描。
- 单 SPS/PPS 编码会话规范化器只处理本探针固定编码配置，不作为完整通用 AVC 语法解析器。

CLI、C#（含合成测试像素的真实 GPU 校验）与浏览器场景关联单测回归：`./scripts/verify-media.ps1`。资源采样：`./scripts/sample-media-probe.ps1 -ProcessId <此工具的 dotnet PID> -Seconds 600`。采样脚本核实进程命令和创建时间，只读取 CPU、内存、句柄和线程数。真实场景实测见 `docs/M1_SCENE_EVIDENCE.md`。
