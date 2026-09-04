# SC02 资源隔离诊断

2026-09-04。结论：Closed 订阅相关的每窗口 Event 增长已有实测规避方案，改用独立原生窗口销毁通知及同步身份检查；120次真实销毁回调和91项场景通过。SC02仍保留最终根窗/真实NX复测与较长资源观察，不把短时结果提升为耐久验收。

## 隔离设计与实测

新增 `CaptureResourceVerification`，每进程三轮、每轮40次自身 owner 窗口创建/销毁。完整采集模式实际运行WGC/GPU/NV12，等待81次完整场景和有效输入绑定，**不读回CPU像素、不编码、不注入输入**。每轮源Dispose立即记录资源，500ms后再次记录，然后诊断GC并再记录。GC仅用于归因，未加入产品采集循环。

`OwnHandleTypes` 只查询当前进程的句柄类型计数，不读取对象名、文件内容、其他进程或整个系统句柄表；x64，缓冲区1MiB，失败明确中止诊断。类型快照与总句柄数不是同一原子时刻，瞬时小差异不能当作泄漏。

所有目录均在 `artifacts/verification/`，以下是各轮诊断GC后的数据，原始报告还包含GC前数据和完整构建哈希。

| 目录 | 条件 | 三轮Event数 / 总句柄数 | 判定 |
| --- | --- | --- | --- |
| capture-resources-20260904-110020-8558007 | 完整采集、无CPU读回，初版无类型计数 | 总558/599/642 | 仍增长，不能归咎于诊断像素 |
| capture-resources-20260904-110145-5667446 | 仅自身窗口，无capture item | 总371/371/371 | 排除窗口生成器的每窗口递增 |
| capture-resources-20260904-110245-2557883 | 完整采集、含自身句柄类型计数 | Event168/209/251；总536/577/620 | 主要增长为Event；私有内存约61–62MB，不能用内存稳定掩盖句柄增长 |
| capture-resources-20260904-110449-4361093 | 临时尝试显式Dispose item的NativeObject | Event166/207/249；总531/572/615 | **无效，已撤回该修改** |
| capture-resources-20260904-110614-8955856 | 仅创建item并读Size，不订阅Closed、不创建帧池 | Event120/120/120；总412/415/418 | 不存在每窗口一个Event的增长；总数仍有小波动 |
| capture-resources-20260904-110837-9415749 | item + C# Closed订阅/取消，无帧池 | Event161/201/241；总454/494/534 | 每40轮增加40 Event，最小复现成立 |
| capture-resources-20260904-111018-6441605 | 原生token订阅初稿 | FAIL InvalidCastException，未完成轮次 | Runtime class不能按MarshalInterface泛型接口导出；失败保留 |
| capture-resources-20260904-111029-7294292 | QI取得SDK接口，原生add/remove token，保留同一WinRT委托marshal | Event162/202/242；总456/496/536 | 同样递增，不能简单归因于C#事件语法包装 |
| capture-resources-20260904-111448-6117656 | 仅item、等待真正Closed后再撤销 | FAIL，首轮3秒未收到回调 | 未启动capture session的条件不足以验证通知；失败保留 |
| capture-resources-20260904-111819-4503749 | 自建IUnknown/IAgileObject/TypedEventHandler COM sink，完全绕过CsWinRT委托marshal | Event160/200/240；总450/490/530；sink每轮0 | 自建sink原生引用已归零，Event仍每窗口递增，排除委托封送为必要条件 |
| capture-resources-20260904-112404-3378418 | 临时仅HWND/PID同步存活检测，完整WGC/GPU | 总478/481/484 | 去除订阅后每窗口递增消失，但单纯轮询不足以保护快速HWND复用；未作为最终方案 |
| capture-resources-20260904-112542-9948795 | 独立原生销毁通知 + 同步存活检查，完整WGC/GPU | Event129/132/133；总502/508/511 | 120次销毁均在不调用采集/重枚举时撤销旧输入绑定；每窗口一个Event的斜率消失，仍有小幅源重建变化 |

上面观测报告的 `OBSERVED_NOT_ENDURANCE` 仅表示采样完成，**不表示资源项PASS**。完整采集每轮活动捕获都归零，仍不能据此宣称底层资源归零。初版items-only报告scope字符串遗漏细分，`itemsOnly=true`、实际命令和源码哈希表明没有GPU/帧池；已修正文案，不重写历史JSON。

曾在中间沟通中将item原生引用释放称为根因，随后同矩阵推翻；正确结论仅是 **Closed订阅路径相关**。原生token实验仍复用CsWinRT的TypedEventHandler marshaling，所以目前不能断言是操作系统、C#/WinRT或委托marshal中的哪一层泄漏。

## 保留的实现边界

- `CaptureClosedSubscription` 和 `NativeClosedCallback` 仅供自身诊断A/B，不接入实际采集。自建COM sink的释放没有解决事件源增长，因此不是修复方案。
- 实际采集不再订阅存在增长的WinRT Closed事件，关闭检测替换为下述原生销毁通知，没有删除关闭安全要求。未降低节点预算、强制GC作为生产补丁、改OS/SDK目标、关闭NX工程或切换到整桌面采集。
- 原生token初稿的class/interface转换错误已修正。一次诊断辅助方法中namespace解析错误也在编译时发现并修正；没有把未构建版本当作实测。

## 复现

```powershell
./scripts/verify-capture-resources.ps1
./scripts/verify-capture-resources.ps1 -LongRun
./scripts/verify-capture-resources.ps1 -WindowsOnly
./scripts/verify-capture-resources.ps1 -ItemsOnly
./scripts/verify-capture-resources.ps1 -ItemsOnly -ItemEvents
./scripts/verify-capture-resources.ps1 -ItemsOnly -ItemEvents -NativeEvents
./scripts/verify-capture-resources.ps1 -ItemsOnly -ItemEvents -NativeEvents -RawDelegate
./scripts/verify-capture-resources.ps1 -ItemsOnly -ItemEvents -NativeEvents -AfterClosed
```

每条创建新目录，不覆盖历史。程序只创建自身测试窗，关闭这些窗口或三分钟超时取消本次诊断，不涉及用户软件。比较固定轮数和GC前后数据；不能把运行退出码0解释为泄漏已消除。

最终常规回归 `media-20260904-111128-4662707`：152项C#/32项JS、硬软H264各90帧及负向检查通过；`probe-20260904-111142-3703396`：五项目锁定构建0警告/错误、8冒烟通过。这些结果不覆盖未修复的Event增长。

## 下一项

完成最终根窗口关闭、最终构建真实NX媒体及较长资源观察后汇总SC02/SC04，再推进SC05受限NX输入。不要继续重复无效COM释放方案，也不重复准备用户已提供的070模型。8小时、真实TIA、第二客户端等必需项保持待验收。

## 11:27 设计变更与证据边界

`WindowLifetimeMonitor` 每个采集源只持有一个按目标PID过滤的 `EVENT_OBJECT_DESTROY` out-of-context hook，独立消息线程接收事件。仅接受 `OBJID_WINDOW/CHILDID_SELF`，不获取用户键盘、文本或其他进程事件。回调退休对应输入绑定，晚到销毁事件只允许保守撤销，不可能恢复旧代次。监听失败使绑定失效并阻止下一次采样；Dispose撤销注册、卸载hook并有界结束线程。

每次采样另检查 HWND/PID 存活，现有100ms场景重枚举继续核对进程启动时间、会话、几何和代次。没有用慢轮询替代全部异步通知，也没有更改WGC/GPU/H264主路线。仍需正式Agent故障/锁屏/句柄复用压力矩阵，不能由这些局部检查替代。

最终机制初次矩阵三轮各40个真实销毁通知、81次完整场景，输入撤销观测最大10.2727/2.6820/2.8359ms，计时包含本地UI关闭调用；不是P04浏览器输入时延。每轮活动捕获归零，私有内存诊断GC后61,370,368/63,307,776/63,504,384字节；总句柄502/508/511仍有小幅变化，故仅称原先每窗口一个Event递增已规避，不称8小时无泄漏。

对应DLL SHA256：DesktopFixture `5414483AEC1E16110103209FD8322660D406E9FC50BD52D75415F017FD5828B0`；Windows `89FBF9B34E1086755502B8C2464CABA9BC66C99B9F162D3C2CD6D1B9CA120142`。`scene-fixture-20260904-112615-1959983`同版91项通过。随后增加Alive保守检查与回调异常边界，`media-20260904-112701-7466394`152C#/32JS及硬软90帧通过；不把旧DLL实测冒充新增边界版本的最终回归。

11:29 增量：`capture-resources-20260904-112740-4266343`完整三轮及独立根窗关闭PASS；120次通知的各轮最大10.7737/1.9485/2.2124ms，根窗销毁2.176ms后旧流被明确拒绝。Event129/130/131、总503/506/509、私有61,820,928/62,627,840/62,181,376字节。Windows DLL `E2626925685455632802D4B70488A112EBC96C92F257447FE4CC5F03F09FF740`，夹具 `950625E346B061402543D4B89C363A88AD0562D07B9FC15EA6D901D0B6F3B336`。随后只修复夹具nullable注解，最终五项目构建0警告/错误及8冒烟见 `probe-20260904-112827-2606527`；未修改运行逻辑。

原生销毁hook的线程/异步语义依据 [Microsoft SetWinEventHook](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook)。这些证据不把通知延迟等同浏览器操作时延，也不把原始WinRT问题断言为某一OS版本的已知厂商缺陷。

## 11:39 扩展有限观察

`-LongRun`在每个源内做120次开关（241次完整场景），三轮共360次，无采集循环内强制GC；末尾另做根窗销毁检查，仍是有限观察，不是十分钟真实源或8小时。新增参数未提高生产探针256条场景历史上限，单源241条在既有预算内。

`capture-resources-20260904-112955-0232178`三轮各120个实际通知，最大5.2656/4.3107/4.6564ms，根关闭2.5472ms后旧流拒绝，活动捕获归零。Event129/143/147，总503/553/556，私有内存58,683,392/60,878,848/62,607,360字节。第二轮中途多类句柄阶跃（含ETW、ALPC、线程、Event），不是原先每窗口一个Event的固定斜率，但**归因未完成，不能据此放行全部资源项**。该轮同期启动了本机桌面观察和另一个NX探针；记录共现，不断言它们是增长根因。

Windows DLL仍为`E2626925685455632802D4B70488A112EBC96C92F257447FE4CC5F03F09FF740`，夹具`9E1036BF7F523F56FA7DE38EE3AAAE3591B3D298288E1E9B729824256053BC9A`。下一步在无并行媒体/桌面观测条件下核对阶跃是否复现；同时定位真实NX新增ArgumentException，见真实模型页。SC02/04继续IN_PROGRESS，不无限重复小测试取代真实验收。

## 接口依据

自身句柄快照布局依据 [System Informer维护的phnt原始定义](https://github.com/winsiderss/phnt/blob/master/ntpsapi.h)，类型查询依据 [Microsoft NtQueryObject](https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntqueryobject)。此诊断依赖原生查询接口，仅用于本机排查，不作为发布Host公共协议。

事件token签名和槽位依据 [Microsoft Windows SDK capture头文件](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/winrt/windows.graphics.capture.h)。[帧池Dispose文档](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool?view=winrt-26100) 说明帧池资源释放接口，但不证明当前组合已无泄漏；本机对照结果优先于推测。

## 12:03 单源与重建隔离结论

本轮没有并行运行其他媒体/桌面观察来解释采样。所有模式只操作自身夹具或自身D3D设备，不关闭NX或改变驱动、操作系统、许可证。报告的OBSERVED_NOT_ENDURANCE仍不是资源PASS。

- `capture-resources-20260904-114352-3369867`：3×120窗口，Event129/131/135，总514/519/525。没有复现之前ETW等大阶跃，但仍有小幅变化。
- `capture-resources-20260904-115208-8602302`：同源600开关、1201场景、601绑定、600次实际销毁撤销，活动捕获归零；根关闭拒绝旧流。历史保留256/退休945，确认超过原256条上限仍持续合成。Windows DLL为490E917910B52CE1D330202256FE71A37DBDD724C6ABDFC429143F2FBB5DD887，夹具9BD9EC0403648550E1A5A93DCA28897D011799173C2F97A98EDD70E3E9E73913。
- 上述前300/后300循环各30个样点，句柄均值633.7/632.3、范围615–655/614–649；私有内存均值120,779,162/120,706,253字节，Event均值186.93/187.37。没有按每个弹窗继续增长；无循环内强制GC，Dispose后另记诊断GC，不能用该清理后的数字冒充运行资源。
- `capture-resources-20260904-115514-5925165`：12独立源×10开关，Event诊断GC后146→159；线程27、ALPC17稳定，各轮活动捕获0。约每源一个Event持续增加，故**SC02仍IN_PROGRESS**。

### 无捕获的最小原语对照

运行 `./scripts/verify-capture-primitives.ps1 -Modes <模式列表>`，每模式独立进程，3×20次分配/释放。该工具不建立WGC窗口或帧池，也不编码、输入或访问用户工程。下面数字为每轮诊断GC后Event计数。

| 模式 | artifacts/verification 中的目录 | Event三轮 | 有限结论 |
| --- | --- | --- | --- |
| hook | capture-primitive-hook-20260904-115729-9976104 | 101/101/101 | 独立窗口销毁监听自身无该斜率 |
| d3d | capture-primitive-d3d-20260904-115730-9266048 | 89/89/89 | 设备/上下文创建销毁无该斜率 |
| manager | capture-primitive-manager-20260904-115733-6200314 | 89/89/89 | MF设备管理器无该斜率 |
| winrt-device | capture-primitive-winrt-device-20260904-115736-2152609 | 89/89/89 | WinRT设备包装无该斜率 |
| compositor-init | capture-primitive-compositor-init-20260904-115911-4264376 | 89/89/89 | 仅shader/状态初始化无该斜率 |
| gpu-clear | capture-primitive-gpu-clear-20260904-115914-3176942 | 89/89/89 | 仅GPU纹理清色/Flush无该斜率 |
| compositor | capture-primitive-compositor-20260904-115835-1812371 | 109/129/149 | 无WGC也可复现每设备一次Draw后增长 |
| compositor-clear | capture-primitive-compositor-clear-20260904-115838-2874859 | 109/129/149 | ClearState+Flush无效，未接入生产作修复 |
| compositor-wait | capture-primitive-compositor-wait-20260904-120023-3756333 | 109/129/149 | 实际Event query等待GPU完成后仍增长 |
| compositor（引用计数版） | capture-primitive-compositor-20260904-120124-2993730 | 109/129/149 | 60次设备最终原生Release均返回0，仍有句柄增长 |
| compositor-warp | capture-primitive-compositor-warp-20260904-120143-5329668 | 88/88/88 | WARP仅诊断；未替换真实采集/硬编路线 |
| compositor-no-video | capture-primitive-compositor-no-video-20260904-120257-8425432 | 109/129/149 | 与WARP同为BGRA标志，硬件路径仍增长；排除仅VideoSupport标志差异 |

WARP最初带VideoSupport失败DXGI_ERROR_UNSUPPORTED，原始失败目录 `capture-primitive-compositor-warp-20260904-120127-3507349` 保留；随后仅诊断模式去掉该标志，再补硬件相同标志对照。两处诊断程序重载/枚举名编译错误已修正，无未构建版本被记为实测。

引用计数只对自身设备使用一对AddRef/Release保留到所有包装释放后，最后返回值作为诊断；不手动释放借用指针，不是生产补丁。该结果支持“硬件Draw生命周期相关”，不证明驱动或D3D运行时哪一层具有唯一缺陷。

微软[Flush说明](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-flush)说明异步提交和延迟销毁，[Event query](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_query)说明GetData完成语义。本次ClearState及GPU等待对照均未修复；不能因官方建议而改写失败结果。

下一项优先共享硬件设备生命周期的实际A/B：同一设备上多轮合成/源重建是否稳定，以及设备故障时如何整体退休。只在有证据后接入源；必须保留复合GPU命令串行化、双路隔离、输入绑定/场景代次与有界退出，不改驱动/OS，不以WARP或重启服务掩盖资源增长。随后复核SC02/04，才推进SC05受限NX产品输入。真实十分钟、双路、TIA、异机与8小时仍缺。

## 12:25 共享设备与应用捕获生命周期

共享硬件设备已接入有限探针，完整实现、13项夹具检查、正常60次取流稳定及失败边界见 [共享捕获证据](./M1_SHARED_CAPTURE_EVIDENCE.md)。同一设备60次Draw、MF管理器和NV12转换的独立对照稳定；两路真实WGC并发颜色变化无串源，显式设备退休会撤销两路输入。浏览器H264两次完整连接、一次取消后的恢复连接，以及JPEG连接已实际解码呈现。

这没有消除所有资源风险。反复创建完整源以及12次实际关闭/重建应用均约每生命周期新增2个ALPC句柄，设备最终销毁后仍未回落。禁用内部线程优化的硬件Draw诊断也未改善原Event斜率，因此未修改生产设备标志。SC02保持IN_PROGRESS，正常浏览器重连资源稳定不能代替应用重建、GPU故障和耐久验收。

## 2026-09-04 12:39 capture item 分层定位（进行中）

本轮新增只操作自身窗口的 `verify-window-monitors.ps1` 和 `verify-capture-sessions.ps1`。前者60次真实窗口销毁，每次独立监听线程；后者分开item创建、帧池、实际会话首帧及GPU复制。原有共享夹具增加 `-Bgra`，保持13项行为检查和同一资源采样方法。不是实际NX/TIA工作流验收。

| artifacts/verification 下目录 | 观测 |
| --- | --- |
| shared-capture-20260904-123105-9832834 | BGRA模式13检查通过；60次取流561/166/17（总/Event/ALPC）稳定，12应用重建ALPC17→39；排除NV12/MF表面封装为必要条件 |
| window-monitors-20260904-123214-8551871 | 60实际销毁，10次后至60次ALPC13、Event114、线程20稳定，总363–364；不能用先前仅注册/撤销hook的测试替代这个检查 |
| capture-session-session-20260904-123331-2981860 | 60实际WGC首帧；每10轮诊断GC后ALPC16/17/18/19/20/21 |
| capture-session-copy-20260904-123333-4110918 | 60实际WGC首帧和GPU复制，ALPC同上 |
| capture-session-item-20260904-123355-6028992 | 不创建帧池/会话，ALPC仍16→21 |
| capture-session-pool-20260904-123356-3395067 | 仅item+帧池，ALPC16→21；额外帧池句柄在最终设备销毁后下降，不能混作同一种泄漏 |
| capture-session-item-raw-20260904-123502-3927566 | 直接原生工厂调用，仍将item投影为WinRT对象；ALPC16→21 |
| capture-session-item-factory-20260904-123503-2279702 | 保持一个工厂引用跨60轮，ALPC16→21；工厂缓存未修复 |
| capture-session-item-native-20260904-123553-1977700 | 完全不创建item托管投影，每次CreateForWindow/Release；10/20/30/40/50/60轮ALPC25/35/45/55/65/75 |
| capture-session-item-roinit-20260904-123555-7720761 | 显式RoInitialize并平衡RoUninitialize，投影item仍ALPC16→21 |

原生item立即释放与托管对象批量GC呈现不同增长节奏，提示item完整销毁/重建的生命周期值得继续隔离；这不证明唯一的Windows/驱动/运行时根因。原生工厂只为诊断新增，线程约束、所有权和Dispose明确，没有接入主路径或借用指针强制释放。首次监视器构建有一个nullable警告，随后修正；后续构建0警告。

原生item静置150秒测试 `capture-session-item-native-20260904-123659-9310523` 正在运行，报告完成前不作最终结论。一次提前构建因该测试进程占用DLL而出现MSB3026/MSB3027，未启动新测试，保留终端记录；后续应串行构建或使用隔离输出，不能终止真实观察来掩盖失败。

## 12:53 完成观察与SC02有限结论

原生item静置实验现已完成，`capture-session-item-native-20260904-123659-9310523`：全部设备/item释放后2.39秒ALPC75；62.47秒72；132.60秒降为16；152.63秒仍16，最终总句柄421。等待阶段未创建新item或强制GC。先前短时“未回收”判断保留为当时观测，不能继续称为已证实持续泄漏。暂拟的保留item引用对照未实际运行，已移除该模式，没有将锚定对象或工厂缓存接入主路径。

完整应用捕获的 `shared-capture-20260904-124059-0381043` 运行180次真实原生窗口创建、WGC/GPU/NV12首帧、关闭和旧流拒绝，共201.48秒。重建段不调用GC.Collect；每次间隔1秒，每10次记录资源。另有前置60次租约重连诊断GC和最后设备清理GC，三段明确标记。

| 重建循环 | 总句柄 | Event | ALPC | 私有内存MiB |
| --- | --- | --- | --- | --- |
| 10 | 619 | 193 | 17 | 106.60 |
| 60 | 863 | 341 | 14 | 117.63 |
| 70 | 598 | 182 | 14 | 114.48 |
| 120 | 846 | 331 | 14 | 113.22 |
| 130 | 576 | 170 | 14 | 112.41 |
| 180 | 824 | 319 | 13 | 116.64 |
| 最终设备销毁及诊断GC后 | 512 | 133 | 13 | 64.05 |

重建段Gen2计数始终25；不能把两次句柄回落归因于手工GC，也不凭现象断言具体系统清理计时器实现。所测速率下峰值/谷值未逐轮抬升，ALPC反而回落。结合既有600窗口开关、91项原生场景、20次真实竞态、双路40颜色变化及60次连接租约，**SC02有限GPU场景检查记DONE**，原标准未删除；仅证明当前探针负载的资源观察与有限预算，不证明任意速率、GPU故障、真实双路编码或8小时。持续真实负载与P09等正式验收仍在M1后续/M7。

本轮MediaProbe主采集行为未因原生工厂诊断改变。原生工厂ABI依据微软[CreateForWindow](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow)，其回收时序是本机实测，不是文档保证。下一项转入SC04共享版真实回归/SC05输入；不要再把132秒内缓存残留当成持续泄漏重做无效修补。
