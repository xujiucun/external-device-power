# 外设电量 (DevicePower)

Windows 托盘小工具：直接在任务栏里显示无线键盘 / 鼠标的电量，**不需要任何厂商客户端和驱动**。

装 G HUB 或迈从驱动只为了瞄一眼电量，这件事本身不该这么重。这个程序直接读 USB 接收器暴露的 HID 报表，双击就能在任务栏看到实时读数。

## 下载

到仓库的 [Releases](../../releases) 页面下载压缩包，解压后双击 `DevicePower.exe`。

| 包 | 大小 | 说明 |
| --- | --- | --- |
| `DevicePower-win-x64.zip` | 124 KB（解压后 475 KB） | 体积小，但**要先装 .NET 桌面运行时**（见下面「运行要求」） |
| `DevicePower-win-x64-selfcontained.zip` | 66.8 MB（解压后 160 MB） | 运行时打包在内，解压即用，什么都不用装 |

两个包里都有一层同名目录，解压后进那个目录双击 `DevicePower.exe`。

两点注意：

- **不是单文件程序**，`DevicePower.exe` 旁边的 `DevicePower.dll`、`Assets\` 等必须留在同一目录里，别只拷 exe
- 程序没有主窗口：启动后看任务栏右侧的电量条，托盘图标可能被收在 `^` 折叠区里；右键托盘图标可以打开设置和退出

> 仓库地址：<https://github.com/xujiucun/external-device-power>，当前版本 V0.0.1。

## 运行要求

**要先装 .NET 桌面运行时**，否则双击 `DevicePower.exe` 会弹出“需要 .NET”的提示，或者毫无反应。

- 下载地址：<https://dotnet.microsoft.com/zh-cn/download/dotnet> → 找 **Windows 的 “.NET Desktop Runtime”**，选 **x64**
- **8 / 9 / 10 任一版本都行**（程序设了 `RollForward=LatestMajor`，会自动向前匹配）。注意 .NET 8 和 9 已于 2026-11-10 停止支持，建议直接装 .NET 10 LTS
- 只是注意区分：要的是 **Desktop Runtime**（带 WPF），不是 ASP.NET Core Runtime，也不是单独的 SDK
- 不想装运行时的话，直接下载上面那个 `selfcontained` 压缩包（66.8 MB），或者自己出一份：见下面“构建与运行”
- 自己编译的话需要 .NET 8 SDK（同一个页面下载）

## 特性

- **任务栏内嵌电量条**（Win11）：顶层透明窗口贴在托盘左侧，也可放到开始按钮左侧；一列最多两台、超出向左加列。列宽按实测内容算，所以读数在 `87%` 和 `100%` 之间变化时**不会有任何位移**；改名会让条子伸缩，左边缘用 180ms 缓入缓出滑到位
- **浮动面板**：固定在任务栏上方右下角，列表式，可同时显示多台
- **读不到就移除**：设备休眠、没电或拔掉接收器后，下一轮读不到电量就从电量条和面板里消失（读数和充电标记一起清零）；重新读到电量再出现。全部设备都读不到时整条电量条隐藏，不会留空壳
- **按设备显示 / 隐藏**：设置里每台设备一个「显示」勾选。取消勾选后它不再出现在电量条 / 面板上，后台也不再读取它的电量；重新勾选后下一次刷新恢复读数
- **首次启动提示**：第一次运行弹一次说明——设备休眠时固件不回包、读不到电量，唤醒设备后一个刷新间隔内出读数；标记存进配置，之后不再弹
- **全屏自动隐藏**：有窗口铺满整块屏幕（全屏游戏、无边框全屏）时，电量条和浮动面板都消失，退出后自动回来；设置里可以关掉
- **托盘图标** + 自绘深色右键菜单（设置 / 显示·隐藏浮动面板 / 立即刷新 / 关于 / 退出）
- **低电量提醒**：两档可调阈值，跌破时右下角系统通知 + 百分比变黄/变红 + 托盘图标右下角加黄/红点；充电中不提醒，同一档只提醒一次
- **设备上线通知**：每次启动程序后，每台设备在第一次读到电量时推一条系统通知，带设备名和当前电量；之后休眠唤醒、读数抖动都不再重复推。设置「读数与提醒」里可关，默认开。隐藏的设备不轮询，重新勾选也不会因此补推。通知正文图片使用程序自己的 `hlogo.ico`
- **外观可定制**：文字颜色（色板 + 系统取色器）、大小（80–160%）、名称粗细、排版（名称在前 / 图标在前 / 隐藏名称）、电量图形（电池 / 条形 / 圆环 / 分段 / 不显示）、充电状态样式（绿色填充 / 绿色描边 / 数字变绿 / 不区分）、可选 200ms 宽度补间
- **设置页**：自绘深色界面，Switch 开关、分组标题和固定底部操作栏；内容超过一屏时中间区域滚动，按钮始终可见
- **读数不做平滑**：屏幕上就是设备固件给的原始值。不做低通滤波，因为电压法电量在末期会突降，滤波只会让你看到偏高的数字

## 支持的设备

| 设备 | 接收器 | 协议 | 状态 |
| --- | --- | --- | --- |
| 迈从 MCHOSE K99 V2 | 2.4G，VID `0x05AC` / PID `0x024F` | 厂商自定义 HID Feature Report：ID `0x13`、命令 `0x4A`、末字节校验和；`report[5]` 为百分比，`report[6]` 高半字节为充电标志 | 已实测 |
| 罗技 G304 / GPW2 等 Lightspeed、Unifying | VID `0x046D`，Usage Page `0xFF00` / Usage `0x0002` | HID++ 2.0：优先 Feature `0x1000`（直接给百分比），无该 feature 时退回 `0x1004` function 1 | GPW2 已实测可读出电量；其余型号按同一路径实现，未逐款验证 |

只走 2.4G 接收器，不支持蓝牙。读 HID 不需要管理员权限，也不需要装驱动。

## 已知行为

- **充电时读数偏高**：K99 V2 的百分比是固件按电压查表算的，插上线电压被抬高，同一块电实测充电时约 +3%。程序不做修正。
- **全屏隐藏不靠 QUNS**：`SHQueryUserNotificationState` 只在 DX 独占全屏时报 3，英雄联盟这类“无边框全屏”始终停在 5（家用机上它还会在 2 和 5 之间抖，所以 2 也不采信）。现在的判定是直接量前台窗口矩形：铺满它所在整块显示器 = 全屏，桌面 / 任务栏一类外壳窗口按窗口类名排除，副屏全屏不影响主屏。判定结果会写进 `dock.log`（`reason=<窗口类名 宽x高 进程名>`、`panel hidden by ...`），出问题时把这行贴过来就能定位。要连续 5 秒判定成立才真隐藏，一闪而过的窗口不会把条子藏掉。
- **截图时不隐藏、也不位移**：系统截图和 Snipaste 的选区层带 `WS_EX_TOOLWINDOW`，微信 / QQ / PixPin 的画布是普通窗口、几何上和无边框全屏游戏一模一样，只能按进程名区分（`Weixin.exe`、`WeChat.exe`、`QQ.exe`、`Snipaste.exe`、`PixPin.exe`）。这两类铺满屏幕时只**冻结位置**、不隐藏：覆盖层一出现，托盘区会自己窄掉一个图标位（实测 28px），此时跟着重算就会看到条子往左滑一下再滑回来。日志里对应 `position frozen by ...`。
- **任务栏层级与开始菜单**：Win11 的任务栏自身也是 topmost，被激活时会盖住电量条约几十毫秒；程序在焦点变化后以 4ms 粒度重新压层级。打开开始菜单 / 搜索时电量条会主动隐藏；关闭后如果任务栏仍停留在更高窗口层（band 6，实测不会自行恢复），电量条继续保持隐藏，直到焦点切到其他窗口、任务栏回到正常层，再在**原位置**出现（不会浮到任务栏上方）。
- **未代码签名**：首次运行可能撞 SmartScreen（“更多信息 → 仍要运行”）。
- **配置按机器独立**：`%APPDATA%\DevicePower\settings.json`；停靠诊断日志 `%APPDATA%\DevicePower\dock.log`（只在判定变化时写一行）。

## 已知问题

- **开始菜单关闭后电量条不会立刻回来**：Win11 关闭开始菜单后任务栏可能仍停在 band 6，只有焦点离开任务栏（点一下别的窗口）才降回正常层，电量条随后在原位恢复。这是 WPF 顶层窗口受任务栏层级限制导致的；要让它在关闭瞬间就出现，需要像 TrafficMonitor 那样改用 DirectComposition 原生渲染，0.0.1 不做。
- **罗技设备不区分键盘 / 鼠标**：默认名是「接收器产品名 + 槽位号」，不是真实设备类型。已扫描过的接收器不会重新扫描槽位，后配对的设备可能需要重插接收器或重启程序才会出现。
- **不支持蓝牙**：只读 2.4G 接收器的 HID 报表。
- **设置页需要滚动**：字号放大后内容高度超过一屏，中间区域滚动，底部按钮固定。
- **当前仓库代码比 V0.0.1 发布包新**：下载区的两个 zip 还是旧包，重新发版前需要重新构建 `dist`。

## 构建与运行

```bash
dotnet build src/DevicePower/DevicePower.csproj
dotnet run --project src/DevicePower/DevicePower.csproj
```

免安装分发（框架依赖，约 475KB；**目标机需按上面“运行要求”先装 .NET 桌面运行时**）：

```bash
dotnet publish src/DevicePower/DevicePower.csproj -c Release -r win-x64 --self-contained false -o publish
```

想彻底免装运行时（解压后 160 MB，压成 zip 约 67 MB，直接双击就能跑）：

```bash
dotnet publish src/DevicePower/DevicePower.csproj -c Release -r win-x64 --self-contained true -o publish-selfcontained
```

## 技术栈

.NET 8 / WPF（控件、菜单、设置页、关于页全部自绘）+ WinForms `NotifyIcon` + Win32 HID API（P/Invoke）。**没有第三方依赖。**

## 目录结构

```
src/DevicePower/
├── DevicePower.csproj         工程（net8.0-windows，WPF+WinForms，x64，RollForward=LatestMajor）
├── App.xaml(.cs)              启动、托盘/面板/停靠的装配、全屏看门狗定时器
├── BatteryGlyph.cs            自绘电量图形（电池/条形/圆环/分段）
├── BatteryPanel.xaml(.cs)     浮动面板（右下角列表，右键开设置）
├── SettingsWindow.xaml(.cs)   设置页
├── WakeTipWindow.xaml(.cs)    首次启动的一次性唤醒提示弹窗
├── AboutWindow.xaml(.cs)      关于页（版本、功能、仓库地址）
├── TrayHost.cs                托盘图标、悬浮提示、低电量提醒、设备上线通知（图片使用 hlogo）
├── TrayMenuWindow.xaml(.cs)   自绘右键菜单
├── DiagLog.cs                 诊断日志（%APPDATA%\DevicePower\dock.log，只在变化时写一行）
├── Assets/                    hlogo.ico（程序图标）、tray.ico（托盘默认图标）
├── Devices/                   设备抽象、轮询、外观换算
│   ├── DeviceMonitor.cs       轮询循环 + 读数发布 + 读不到移除 + 显示/隐藏 + 每进程只推一次上线
│   ├── DeviceConnection.cs    IDeviceConnection 接口 + DeviceDescriptor + BatteryReading
│   ├── MchoseConnection.cs    迈从接收器私有 HID 协议
│   ├── LogitechConnection.cs  HID++ 2.0（Lightspeed / Unifying）
│   ├── DeviceRow.cs           单台设备的可绑定视图模型
│   ├── DeviceColumn.cs        任务栏一列（最多两台）
│   └── DisplayLook.cs         外观设置 → 可绑定值（含文字测量、列宽）
├── Taskbar/                   任务栏电量条与停靠
│   ├── TaskbarStrip.xaml(.cs) 电量条窗口
│   ├── TaskbarDock.cs         跟随任务栏定位、开始菜单/band 等待、层级压制、位置缓动、截图冻结
│   └── FullscreenWatcher.cs   前台窗口是否铺满整屏（全屏隐藏 + 截图覆盖层 + 开始菜单检测）
├── Hid/HidNative.cs           HID 枚举（CM_* + HidD_*/HidP_GetCaps）与重叠读写（WriteFile/ReadFile）
└── Settings/                  配置与系统集成
    ├── AppSettings.cs         配置读写与规范化
    └── AutoStart.cs           开机自启（写 HKCU …\CurrentVersion\Run）
```

## 版本

V0.0.1

## 许可

尚未选择许可证。
