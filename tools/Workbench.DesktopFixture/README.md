# Windows 场景与 F0 输入目标夹具

这是明确标记的合成测试应用，不是假扮 NX/TIA 的替代实现。它只创建、绘制、关闭自身窗口，不向其他程序注入键鼠，不读取或修改工程。

## 原生输入自测（显式启动）

运行 `./scripts/verify-native-input.ps1`，或 CLI `--verify-input <新的输出目录>`，会显示独立的 OWN WINDOW ONLY 测试窗。点击 Start 后，项目后端会向自身窗口实际调用 SendInput；请暂时不要操作键鼠。未点击则超时退出且不注入。测试结束释放自身记录的按键并关闭测试窗；总运行期限 90 秒。

此模式包含真实键鼠/Unicode、释放与焦点拒绝检查，并通过有界执行器和持续 WGC 捕获绑定执行；显示确认仍为本机自测模拟。它不接收任意目标应用参数，不操作 NX/TIA，也不代表浏览器输入链已完成。证据范围见 `docs/M1_NATIVE_INPUT_EVIDENCE.md` 与 `docs/M1_EXECUTOR_EVIDENCE.md`。

## 自动场景校验

资源隔离另运行 `./scripts/verify-capture-resources.ps1`，三轮各40次实际WGC/GPU/NV12场景、不做CPU像素读回；每次关闭后不调用采集，验证独立销毁通知退休旧输入绑定，末尾单独验证根窗关闭拒绝旧流。`-WindowsOnly`、`-ItemsOnly`、`-ItemsOnly -ItemEvents`、`-ItemsOnly -ItemEvents -NativeEvents` 分别隔离窗口、item、投影事件、原生token路径；后者加`-RawDelegate`绕过CsWinRT委托marshal，加`-AfterClosed`是已记录失败的无session等待实验。仅自身进程句柄类型统计，每轮Dispose后诊断GC不用于生产修复；实际采集用进程过滤的原生销毁通知规避Closed相关Event增长，报告“OBSERVED”仍不代表耐久通过。见 [资源隔离证据](../../docs/M1_RESOURCE_ISOLATION.md)。

在已登录、未锁屏的 Windows 图形会话中运行：

```powershell
./scripts/verify-scene.ps1 -Cycles 40
```

短暂显示根窗、透明分层窗、同进程无关遮挡窗、多级 owner 窗口和实际 ShowDialog 模态框。调用项目 `WgcOwnedNv12Source` 获取真实 WGC 节点并 GPU 合成，显式读回诊断像素与独立预期比较。它不编码或在浏览器解码，不能作为输入、NX/TIA 或媒体端到端验收。

检查内容：

- 蓝色根窗，透明/50% 预乘红/全不透明红三列（BGRA 预期分别为 255,0,0,255；127,0,128,255；0,0,255,255，容差每色通道 2）。
- 无关洋红窗口确实覆盖指定点（WindowFromPoint 核对），但未进入 owner 场景；遮挡后新绘制的绿色标记必须出现在捕获结果，防止用旧缓存假通过。
- 仅本夹具的洋红窗口临时置顶，以建立确定遮挡；期望与实际命中句柄写入报告，不移动或隐藏用户其他窗口，检查不符仍失败。
- 根窗外的多级 owner 绿色窗口扩展联合矩形；关闭后不留下幽灵像素。
- 实际模态框使根窗 Enabled=false；关闭后恢复。
- 1–40 次创建/关闭窗口循环，记录场景版本、身份代次、节点数量、GDI/USER/进程句柄、工作集和私有内存。源 Dispose 后活动节点为 0。

输出为新建的 `artifacts/verification/scene-fixture-*/`，含 report.json、source-identity.json 和少量 PNG。失败也保留报告/失败画面；已有目录拒绝覆盖。所有截图只有本测试应用内容。资源记录标为 OBSERVED_NOT_ENDURANCE，不能把短时波动或托管回收解释为八小时无泄漏。

手工调用 CLI 时参数顺序固定：

```powershell
. ./scripts/environment.ps1
& $Dotnet run --project tools/Workbench.DesktopFixture -- --verify-scene artifacts/verification/new-scene-run --cycles 10
```

## 交互输入目标（待产品输入链联调）

SC03只读帧版本实验另用 `--media-scenes`：自身根窗/owner窗每700ms切换，约119秒自行退出；不注入输入。另一个终端运行 `./scripts/start-scene-version-probe.ps1`（JPEG加 `-Jpeg`），网页选10秒和首解码回调延迟。程序退出后必须重新枚举，不复用句柄。完整步骤、失败与范围见 [帧生命周期证据](../../docs/M1_FRAME_LIFETIME_EVIDENCE.md)。

```powershell
& $Dotnet run --project tools/Workbench.DesktopFixture -- --interactive
```

包含英文/中文文本框、右键子菜单、模态编辑框、只选择而不读取文件的原生 OpenFileDialog，以及 40 像素网格、左/右/中键、拖拽、双击、滚轮和按键状态回显。仅观察该控件收到的事件，无全局键盘记录；内存事件历史最多 128 条，文件路径/文本正文不落盘。

16 位图像标记编码的是夹具自身 event counter，**不是产品 inputSeq**。后续需要把真实输入请求关联到图像回显并做浏览器单时钟测量，才可用于 P04；当前不得写成输入延迟已测。失焦不擅自清空夹具 held-state，避免掩盖缺失 keyup；没有真实输入链时本夹具不证明 A10–A12/A21 已通过。关闭测试窗退出，不影响 NX/TIA。

## 瞬态窗口与资源实验

`./scripts/verify-transient-windows.ps1` 在自身弹窗枚举后、WGC绑定前实际销毁它，验证20次有界恢复、不发布部分场景/输入准入，以及根窗消失不得继续旧流。只控制自身创建的窗口，不注入NX/TIA输入。完整失败与修复证据见 [瞬态窗口](../../docs/M1_TRANSIENT_WINDOW_EVIDENCE.md)。

`./scripts/verify-capture-resources.ps1 -SteadyState` 在同一真实采集源内做600次弹窗开关，无循环内强制GC；验证1201次场景变化、600次异步输入绑定撤销、最近256条历史及945条退休计数。`-Lifetimes` 则做12个独立采集源，每源10次开关，以分离窗口数量和源重建的资源趋势。二者均有超时/自身关闭取消与新目录保护，只记有限资源观察，不能冒充真实NX/TIA动态十分钟、浏览器链或8小时耐久。

原语资源诊断可运行 `./scripts/verify-capture-primitives.ps1`，或以 `-Modes compositor,compositor-clear,compositor-wait,compositor-warp,compositor-no-video,compositor-init,gpu-clear` 选择GPU对照。每模式独立进程60次分配，只诊断自身对象，不显示/采集用户窗口。硬件Draw后Event增长仍未修复，不能把退出码0/OBSERVED_NOT_ENDURANCE当PASS；完整矩阵见[资源隔离](../../docs/M1_RESOURCE_ISOLATION.md)。WARP仅诊断，不改主路径。

共享生命周期验证：`./scripts/verify-shared-capture.ps1` 实际运行两路自身WinForms/WGC颜色变化、设备退休、60次编码租约重取、12次应用窗口关闭/重建。13项行为检查与资源样点分开判断；行为PASS不能代替资源/耐久PASS。证据边界见 `docs/M1_SHARED_CAPTURE_EVIDENCE.md`。原语诊断可用 `./scripts/verify-capture-primitives.ps1 -Modes compositor-shared,manager-shared,nv12-shared,compositor-no-workers`，最后一个只用于对照，不更改主路径。
