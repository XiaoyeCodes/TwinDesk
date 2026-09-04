# M1-02 输入核心实现与验证

日期：2026-09-04。状态：IN_PROGRESS。分支 `codex/m1-nx-input`，基于 `7022827` 的未提交增量；运行报告包含源码 SHA-256。这不是完整浏览器输入实现，也不构成 A10–A12/A21 或 P07/P12 通过。

## 本次实际实现

- `InputCommand.cs`：M1 内部输入模型、严格字段组合验证、安全整数序号、有限坐标、三种鼠标按钮、滚轮幅值、物理键表、4096 Unicode 标量上限、非法代理项拒绝和正文脱敏的 ToString。
- `SceneInputCoordinates.cs`：内容内归一化坐标到场景物理像素、负坐标虚拟桌面绝对坐标转换，以及对已取得的真实命中窗口做身份/绑定代次/边界/owner/DPI/启用状态比对。它不自行推断透明窗口命中，也不执行原生输入。
- `InputSession.cs`：固定根实例与单租约、按序接受事件、不可变场景副本、已发送帧账本和显示确认、场景更新前释放、重复按下/键盘重复的区分、旧场景抬键、旧租约拒绝、失败保守账本和显式释放重试。
- 独立 `Tick()` 检查六秒控制心跳及三秒待显示帧期限；静止而没有待确认更新时不要求制造新帧。未确认帧最多 256 条，超限失效并释放。三个时间/数量值是本次 M1 初值，不是性能实测结论。
- `IInputBackend` 没有默认放行实现。每次新动作都要求后端重新检查目标；失败后不能继续输入。释放仅针对本租约已记账的键/按钮，按钮先于修饰键释放。

本次没有接入 SendInput，没有新增 HTTP/WS 控制接口，没有把未鉴权控制暴露到 LAN，也没有进入正式 M2。模型是内部探针契约，不能当作已冻结的 C#/TypeScript 公共协议。

## 已执行的证据

```powershell
./scripts/verify-input.ps1
./scripts/verify-media.ps1
./scripts/verify-probe.ps1
```

| 验证 | 实际结果 | 本机证据 |
| --- | --- | --- |
| 输入核心定向测试 | 49 项通过，包含 47 项新增输入测试及两项既有名称匹配的测试 | `artifacts/verification/input-core-20260904-092509-0089998/` |
| 全量 C# 与 JS | 91 项 C#、5 项 JS 通过 | `artifacts/verification/media-20260904-092624-3928431/` |
| 五项目锁定构建与原生冒烟 | 0 警告/错误，8 项通过 | `artifacts/verification/probe-20260904-092512-5193409/` |

输入测试使用确定性时钟与 fake backend，明确没有调用 Windows 注入。覆盖旧帧/旧 epoch/旧租约、无显示确认、乱序、重复、三种按钮、保守释放、发送/释放异常、焦点失败、静止五分钟、仅媒体更新未确认、1000 组逻辑按下/释放、中文代理对、负坐标和源可见边界。1000 组逻辑测试不能替代 P12 的真实 1000 次 Windows 输入、窗口切换和断线实验。

最终 Windows DLL SHA-256：`45DA86DACDB4D6D772BE3BF0238F7CED3A540A0AFBF70A907B70D49145158806`。图形回归和短时夹具结果见 [场景夹具记录](./M1_FIXTURE_EVIDENCE.md)。后续源码或构建身份改变时须重新记录验证范围。

## 原生 NX 观察及工具限制

Computer Use 重新枚举到 NX 10 的空白工作区，打开了原生“新建”框。对该框目录编辑控件，工具两次返回 `element 103 is not available in cached app state for ugraf.exe`；键盘焦点未能可靠确认。随后通过实际弹窗截图点击取消，已观察主界面恢复、无新建框。

没有继续盲输路径，没有创建/修改/保存零件，没有操作许可证或真实 PLC。为测试准备的 `artifacts/verification/n0-20260904-0918/` 仍为空目录。该限制是本次工具交互的证据，不证明项目自身输入已经失败，也不证明 NX 不可编辑。SC04 参数框仍 NOT_RUN，不能用“新建框可见”替代特征参数框操作。

## 下一实施步骤与必须保留的缺口

1. M1-02：实现受控原生后端和本机专用 InputProbe。原生入口必须重新核对进程创建时间、会话、窗口绑定、桌面可交互性、完整性级别、前台及真实命中；不接受网页 HWND 或屏幕绝对坐标。
2. 接入有界单输入执行队列及独立安全时钟。当前锁内同步后端只是模型边界；后端阻塞、Agent/Host 崩溃或同时终止没有在本类中解决，不能声称六秒实测已通过。
3. 物理键表先覆盖常用字母/数字/符号、左右修饰键、F1–F12、编辑键和数字键盘。Meta、Pause/Break、PrintScreen、媒体键等当前明确拒绝，后续须结合浏览器保留键与原生行为提供明确 UI 提示或经过验证的实现，不能静默忽略。
4. 中文原生适配必须处理 SendInput 部分提交及 UTF-16 单元的临时抬键。本核心只记持久物理键/按钮；未实现文本批次安全释放的后端必须拒绝 Text，不能假设失败就没有插入任何字符。
5. 浏览器的 Pointer Capture、失焦/隐藏释放、composition 去重、CSS/编码留黑边扣除、显示确认绑定尚未接入。不能将这里的坐标单测换成真实 DPI 精度结论。
6. 在 F0 实测后回到安全 N0 模型与 SC04 编辑框；若工具仍不能可靠创建模型，用户可在 NX 的新建框手动创建临时模型到上述目录，再提供一个可取消的特征参数框供观察。这个外部步骤不阻止 M1-02 原生后端继续实现。
7. SC05 浏览器产品输入、SC06 双窗/JPEG、SC07 真实十分钟及 1080p、SC08 NX 放行仍未完成。Host/Agent 故障双向保护留在既定 M2/M6，不删减验收。

## API 依据

Microsoft [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput) 说明实际插入数量与 UIPI 限制，因此未来后端不能把零/部分返回等同于完整成功。[KEYBDINPUT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-keybdinput) 区分扫描码、扩展键和 Unicode 模式；[MOUSEINPUT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-mouseinput) 定义绝对坐标及虚拟桌面标记。这些资料不替代当前机器上真实键位和控件兼容性测试。
