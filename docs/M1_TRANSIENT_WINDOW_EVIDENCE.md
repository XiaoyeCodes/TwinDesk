# SC02 瞬态窗口绑定竞态

2026-09-04。本项是自身真实 Windows 窗口的 WGC/GPU 局部验证，不是 NX/TIA 工作流或耐久验收。

## 失败与修复

新增 `TransientWindowVerification`，只销毁自身创建的弹窗。内部调度接口在枚举后、节点创建前关闭真实 HWND；并未模拟 WGC 的返回值、帧或 GPU。公开入口和网络客户端不能设置此接口。

- 修复前 `artifacts/verification/transient-windows-20260904-114541-5117259/report.json` 为 FAIL：实际 `GraphicsCaptureInterop.ForWindow` 的 `CreateForWindow` 返回 `0x80070057`，产生 `ArgumentException`。完整调用栈已保留。
- 修复只包围该原生创建调用，且仅当 HRESULT 为 E_INVALIDARG、随后 `GetWindowThreadProcessId` 确认 HWND 无效时分类为窗口消失。仍存活窗口的参数异常、GPU 错误和其他异常继续失败，不做泛化吞错。
- 关联弹窗消失进入已有冻结/有界恢复流程，100ms 后重新枚举，最多8次/2秒，只有完整场景可以重新发布输入绑定。根窗口消失拒绝继续旧流。
- 最终竞态报告 `transient-windows-20260904-114701-0879379/report.json`：20次真实弹窗销毁、20次恢复，过程中无部分帧和输入准入；另一次根窗口在绑定前关闭被拒绝，Dispose后活动捕获0。

复现命令 `./scripts/verify-transient-windows.ps1`。关闭自身夹具或两分钟超时可终止实验；不注入键鼠，不操作 NX/TIA，不覆盖旧报告。

依据 [CreateForWindow](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow) 和 [GetWindowThreadProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowthreadprocessid)。官方资料说明接口和无效句柄返回值；E_INVALIDARG 的具体复现来自本机实验，不宣称这是所有参数异常的根因。

## 验证边界

`media-20260904-114722-9716418` 为152项C#/32项JS及硬软H264各90帧回归；`scene-fixture-20260904-114742-2754343` 为91项场景/几何/透明/遮挡检查。均通过各自范围。

真实 NX 的历史 `failed-20260904-113522-4766319.json` 没有调用栈，不能把本次夹具复现等同于那次故障的已证实根因。新构建的两次60秒NX媒体复测没有复现参数异常，且 `transientBindingRetries=0`，因此不能声称在NX中实际触发了该恢复分支。真实记录见 [NX证据](./M1_NX_MODEL_EVIDENCE.md)。

## 诊断历史的持续运行修正

后续资源观察前，将场景历史从第257条主动失败改为保留最近256条，并报告 `sceneHistoryDropped`。场景版本仍持续递增，输入/帧映射不变；只退休诊断快照，不丢视频帧、不放宽节点或纹理预算。此增量的构建身份不同于上面的NX复测，须由后续600循环夹具与最终构建检查验证。被退休历史有明确数量，不把末尾快照当全程完整记录。

600循环Windows夹具已实际验证此历史上限修正，保留256/退休945、1201场景，原始目录与DLL身份见[资源隔离](./M1_RESOURCE_ISOLATION.md)。未改变帧版本或输入许可规则。
