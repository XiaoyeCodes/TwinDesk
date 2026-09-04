# Windows 场景与 F0 输入目标夹具

这是明确标记的合成测试应用，不是假扮 NX/TIA 的替代实现。它只创建、绘制、关闭自身窗口，不向其他程序注入键鼠，不读取或修改工程。

## 自动场景校验

在已登录、未锁屏的 Windows 图形会话中运行：

```powershell
./scripts/verify-scene.ps1 -Cycles 40
```

短暂显示根窗、透明分层窗、同进程无关遮挡窗、多级 owner 窗口和实际 ShowDialog 模态框。调用项目 `WgcOwnedNv12Source` 获取真实 WGC 节点并 GPU 合成，显式读回诊断像素与独立预期比较。它不编码或在浏览器解码，不能作为输入、NX/TIA 或媒体端到端验收。

检查内容：

- 蓝色根窗，透明/50% 预乘红/全不透明红三列（BGRA 预期分别为 255,0,0,255；127,0,128,255；0,0,255,255，容差每色通道 2）。
- 无关洋红窗口确实覆盖指定点（WindowFromPoint 核对），但未进入 owner 场景；遮挡后新绘制的绿色标记必须出现在捕获结果，防止用旧缓存假通过。
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

```powershell
& $Dotnet run --project tools/Workbench.DesktopFixture -- --interactive
```

包含英文/中文文本框、右键子菜单、模态编辑框、只选择而不读取文件的原生 OpenFileDialog，以及 40 像素网格、左/右/中键、拖拽、双击、滚轮和按键状态回显。仅观察该控件收到的事件，无全局键盘记录；内存事件历史最多 128 条，文件路径/文本正文不落盘。

16 位图像标记编码的是夹具自身 event counter，**不是产品 inputSeq**。后续需要把真实输入请求关联到图像回显并做浏览器单时钟测量，才可用于 P04；当前不得写成输入延迟已测。失焦不擅自清空夹具 held-state，避免掩盖缺失 keyup；没有真实输入链时本夹具不证明 A10–A12/A21 已通过。关闭测试窗退出，不影响 NX/TIA。
