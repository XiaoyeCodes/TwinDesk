# M1 实时媒体探针

只监听 `http://127.0.0.1:8091`。默认编码生成的 NV12 灰阶与移动方块；可通过本机启动参数选择一个已运行的真实窗口。这两种默认模式均不注入输入、不启动 NX/TIA。另有显式 F0 自身夹具输入实验（见下文），不能用于 NX/TIA 控制。它没有正式 Host 鉴权，严禁开放 LAN/公网；真实窗口模式也只用于用户授权的本机诊断。

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

点击停止/关闭标签页会取消实验，服务释放编码器；可以重新开始。服务按 Ctrl+C 退出。失败落盘 `failed-*.json`，不伪造成功报告；终端与浏览器同时报告错误。

## F0 浏览器输入实验

先运行已构建的 `Workbench.DesktopFixture.dll --interactive`，另开 PowerShell 执行 `./scripts/start-input-probe.ps1`。脚本用本地枚举器选择唯一当前 F0，显式传入 `--input-fixture --owned`，普通媒体入口不会暗中开放输入。页面用独立控制 WS、真实视频显示 ACK 与有界执行器发送键鼠/一次性中文；仅处理实际绑定的 WGC 节点。保持自身夹具为原生前台，本机浏览器会争抢前台，不能当作异机体验。

空闲服务器上的 `./scripts/verify-input-http.ps1` 只验证7项WS边界，不发送真实按下或文本，不证明产品鉴权。完整局部证据与边界见 [F0输入证据](../../docs/M1_BROWSER_INPUT_EVIDENCE.md)。仍无正式 Host/Agent 故障隔离、生产鉴权、NX/TIA 输入支持与完整验收。

## JPEG 兼容对照

只读 NX 对照运行 `./scripts/start-nx-scene-probe.ps1 -Jpeg`；F0 输入实验用 `./scripts/start-input-probe.ps1 -Jpeg`。不加 `-Jpeg` 仍为硬编主路径。底层 `--jpeg` 仅接受显式选择的真实 `--owned` 窗口，拒绝无源测试图与重复开关；不允许网页更换被控目标。页面标题沿用媒体探针名称，模式提示、开始按钮和报告的 codec 字段标明 JPEG。

JPEG 复用窗口级 WGC 和关联窗口 GPU 合成，单独缩放至所选720p/1080p BGRA并显式读回CPU，再交给 Windows BitmapEncoder。没有调用H.264编码器，也不要求浏览器 VideoDecoder；浏览器使用 createImageBitmap，及时关闭已呈现或过期图像。每帧继续使用40字节协议头（codec=2）、真实场景版本及显示确认。此兼容分支的读回字节数单独记录，不计入H.264零读回主路径。

当前是有限探针：质量0.85、采集轮询上限10Hz、编码超时2秒、载荷上限8MiB、解码队列上限2帧。无变化时不重复旧图来充当新画面。没有实现正式自适应画质/帧率或自动降级；实际帧率、CPU和字形精度须继续测。JPEG仍限本机入口，不作为放开不安全LAN HTTP或CF使用条件的办法。证据见 [JPEG增量](../../docs/M1_JPEG_EVIDENCE.md)。

## 输出档位与尺寸恢复

默认1280×720；显式 `--1080p` 或两个启动脚本的 `-FullHd` 选择1920×1080，可与JPEG组合。页面、协议、解码帧、场景和报告都严格核对档位；输出1080p不代表源窗口有1080p细节，也不是性能达标声明。

`--owned` 遇到WGC帧尺寸与绑定不符时立即退役该输入绑定，不复制不匹配帧；100ms后重新枚举/重建节点，最多8次且2秒内必须恢复完整新场景。仅完整合成后重开输入准入；旧场景、旧代次不能复用。`captureGeometryRetries` 记录实际次数。身份失效、根捕获Closed、设备丢失等仍明确失败，不包含正式Host/Agent恢复。单窗非owned模式仍维持原有尺寸变化失败策略。

## 当前边界

页面默认不开启故障注入。勾选“延迟首个解码回调3秒”会最多保留一帧，其他帧继续解码；旧场景及同场景落后序号都不再呈现，关闭时释放保留帧，JPEG图像也按相同所有权处理。报告的 `presentation` 区分实际回调、呈现、旧场景/旧序号退休；此模式不用于性能达标结论。真实两格式故障注入、失败记录与恢复步骤见 [SC03证据](../../docs/M1_FRAME_LIFETIME_EVIDENCE.md)。

SC03夹具可用 `start-scene-version-probe.ps1 -DelayEncoded` 启用实际硬编输出后的发送延迟。限制为只读SC03/H264，不允许NX、输入模式或JPEG；最多16帧/8MiB，新场景到达后保持原顺序与原元数据发送。检查报告 `encodedDelay.releaseReason=new-captured-scene`，否则普通媒体PASS不算覆盖该故障。不是MFT内部故障注入或正式弱网恢复。

- 测试图是 CPU 生成 NV12；真实源为 WGC BGRA → D3D11 视频处理 → NV12 GPU sample → MFT，不进行 CPU 像素回读。压缩码流仍需拷贝到网络缓冲区。
- 每个提交帧持有独立 NV12 纹理，编码在途最多 8 帧；当前未使用正式纹理池。设置低延迟 MFT 属性后，真实静止窗口首帧已实测可在排空前输出。
- 真实源显式配置 BT.709、NV12 限幅与黑边等比缩放；浏览器已读到匹配色彩元数据，但完整色板/细线精度验收仍待执行，不承诺 HDR 支持。
- 不带 `--owned` 仍只采指定窗口；带该参数已有真实 NX 文件菜单、文件框和移动面编辑参数框同流证据，见 [真实模型](../../docs/M1_NX_MODEL_EVIDENCE.md)。跨进程辅助窗口、完整输入与恢复矩阵仍待验证。不能把页面媒体链路 PASS 解释为产品菜单可操作。
- 编码器支持异步硬件和同步软件；此页面只走硬件，CLI 可以明确测试软件。
- 编码流选择720p/1080p、配置30 FPS、Baseline；实际协商及实际产生的帧数记录在报告。
- 这是有限长探针，使用有界轮询接收 MFT 事件；正式 Agent 需事件回调、图形线程、Worker、静止健康判断和 epoch/IDR 重建。
- 目前背压超过上限直接失败，不把丢失 P 帧后的花屏当作恢复成功。
- 浏览器测试图首帧时间不代表用户输入至真实工程可见回显的时间；像素检查验证内容，没有测显示器物理扫描。
- 单 SPS/PPS 编码会话规范化器只处理本探针固定编码配置，不作为完整通用 AVC 语法解析器。

CLI、C#（含合成测试像素的真实 GPU 校验）与浏览器场景关联单测回归：`./scripts/verify-media.ps1`。资源采样：`./scripts/sample-media-probe.ps1 -ProcessId <此工具的 dotnet PID> -Seconds 600`。采样脚本核实进程命令和创建时间，只读取 CPU、内存、句柄和线程数。真实场景实测见 `docs/M1_SCENE_EVIDENCE.md`。

关联弹窗在枚举后、WGC创建前消失时，仅在原生E_INVALIDARG且HWND已无效的情况下进入现有8次/2秒有界恢复。根窗消失仍拒绝旧流，存活窗口的参数异常不吞掉。报告新增 `transientBindingRetries`，失败栈保留内部异常；诊断场景历史只保留最近256条，并记录 `sceneHistoryDropped`，不再因正常第257次场景变化终止媒体。版本和视频帧不因此丢弃；长期报告需结合退休计数，不能将末尾历史说成全程快照。

关联窗口模式现在显式持有服务级GPU设备和应用级捕获；正常浏览器取流不重建WGC源，只重建当前编码租约。一次仅一个编码租约，结束后冻结输入；应用真正关闭后旧源拒绝续流，重新选择应用仍需要重启这个有限诊断服务。不要把这当成正式产品恢复实现。

报告的 `capturedFrames`、`scenes` 和源恢复计数是应用捕获生命周期累计值，`result` 与 `browser` 是本次连接值，`graphicsDeviceIdentity` 供同服务关联。应用重建仍有ALPC增长，详见 `docs/M1_SHARED_CAPTURE_EVIDENCE.md`。复现使用自身 `--media-scenes` 和 `scripts/start-scene-version-probe.ps1`，连续两次10秒测试，再停止一次并重新取流；JPEG用 `-Jpeg` 重启同样流程。

## 2026-09-04 SC05 NX输入实验（尚未完整通过）

新增 ./scripts/start-nx-input-probe.ps1 -VerifiedCopy <已在NX原生界面核对的隔离prt路径> [-FullHd] [-Jpeg] [-PrepareOnly]。只接受artifacts/verification中的可写非空prt，拒绝reparse路径，选择唯一显示该副本名的NX根窗。标题不证明打开路径，也不是文件沙箱；不要用同名原件代替已核对副本，不从实验文件框打开其他工程或另存至原件。

NX模式和F0开关互斥，仍仅127.0.0.1:8091。原生输入前重新检查进程/root/副本标题、捕获绑定、完整场景、DPI、前台、模态命中、交互桌面和权限。实际NX10中文版的修改标记为“ （修改的） ”，有限准入允许这个已观察后缀，不用任意标题包含匹配。其他语言/版本未验证的标题拒绝后需本机取消并记录再扩展，不通过删除校验来放行。

首次实际实验通过项目页面完成文件菜单/文件框打开、原生文件名字段中文输入、取消；右键菜单和编辑参数框也真实显示。后续取消被未识别的中文修改标记拒绝，控制连接关闭，原始失败报告 failed-20260904-130643-3591692.json 保留，heldCount=0。已用Computer Use本机取消未修改参数的预览，该取消不计为项目输入通过。标记修复及L0回归89项、浏览器输入逻辑8项通过，真实修复回归进行中。此处不能宣告SC05完成。

新增3分钟测试选项方便真实UI回归；10秒/60秒/10分钟均保留。短时UI、静止连接和成功提交数不能充当连续动态10分钟、8小时或输入至可见响应延迟证据。

## SC06同源双路探针

在已核对的NX副本及一个F0原生窗口都打开时，给start-nx-input-probe.ps1追加-DualFixture。两路固定streamId=1/2并共享GPU，仍仅127.0.0.1:8091；-Jpeg显式切兼容，-FullHd选择1080p。根页是两个真实流的等宽诊断视图，不是正式React产品页面。

两侧默认只读，各自开始；勾选“本次启用该侧输入”后才创建控制。先结束旧控制流，再勾选另一侧输入并开始；若旧执行器未确认释放，新的控制连接被拒绝。探针不自动抢原生前台，仍须本机激活目标。正式统一控制通道及平滑切换留在既定M2/M3。无控制的只读媒体使用明确observe=1，只在双路模式允许，不能改变本地登记目标。视频/输入每层核对固定流身份；正常结束状态会清除过时READY提示。

运行verify-dual-input-http.ps1时不得有实际控制会话，该测试只获取/关闭空控制租约，不发送原生down/text。不要把7项边界、MediaProbe媒体PASS或提交应答计为真实工作流。实际双路成功/失败、误点击、JPEG及F0诊断差异见docs/M1_DUAL_EVIDENCE.md。

F0专用标定复选框只量测实际解码橙点到发送目标的源像素误差；不画替代标记，不验证100目标/完整DPI矩阵，也不度量视觉输入时延。夹具网格截获Tab，不可根据UIA的文本框焦点回退信息认定文本框已接收文字；必须核对真实可见文本。历史“SC05尚未完成”和资源增长段是当时记录，当前有限门槛与资源结论以PROJECT_STATUS最新状态为准。
