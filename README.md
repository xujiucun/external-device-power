# 外设电量 (DevicePower)

Windows 托盘小工具：直接在任务栏里显示无线键盘 / 鼠标的电量，**不需要任何厂商客户端和驱动**。

装 G HUB 或迈从驱动只为了瞄一眼电量，这件事本身不该这么重。这个程序直接读 USB 接收器暴露的 HID 报表，双击就能在任务栏看到实时读数。

## 下载

到仓库的 [Releases](../../releases) 页面下载压缩包，解压后双击 `DevicePower.exe`。

| 包 | 大小 | 说明 |
| --- | --- | --- |
| `DevicePower-win-x64.zip` | 117 KB（解压后 470 KB） | 体积小，但**要先装 .NET 桌面运行时**（见下面「运行要求」） |
| `DevicePower-win-x64-selfcontained.zip` | 66.8 MB（解压后 162 MB） | 运行时打包在内，解压即用，什么都不用装 |

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
- **托盘图标** + 自绘深色右键菜单（设置 / 显示·隐藏浮动面板 / 立即刷新 / 关于 / 退出）
- **低电量提醒**：两档可调阈值，跌破时右下角系统通知 + 百分比变黄/变红 + 托盘图标右下角加黄/红点；充电中不提醒，同一档只提醒一次
- **外观可定制**：文字颜色（色板 + 系统取色器）、大小（80–160%）、名称粗细、排版（名称在前 / 图标在前 / 隐藏名称）、电量图形（电池 / 条形 / 圆环 / 分段 / 不显示）、充电状态样式（绿色填充 / 绿色描边 / 数字变绿 / 不区分）、可选 200ms 宽度补间
- **读数不做平滑**：屏幕上就是设备固件给的原始值。不做低通滤波，因为电压法电量在末期会突降，滤波只会让你看到偏高的数字

## 支持的设备

| 设备 | 接收器 | 协议 | 状态 |
| --- | --- | --- | --- |
| 迈从 MCHOSE K99 V2 | 2.4G，VID `0x05AC` / PID `0x024F` | 厂商自定义 HID Feature Report：ID `0x13`、命令 `0x4A`、末字节校验和；`report[5]` 为百分比，`report[6]` 高半字节为充电标志 | 已实测 |
| 罗技 G304 / GPW2 等 Lightspeed、Unifying | VID `0x046D`，Usage Page `0xFF00` / Usage `0x0002` | HID++ 2.0：优先 Feature `0x1000`（直接给百分比），无该 feature 时退回 `0x1004` function 1 | GPW2 已实测可读出电量；其余型号按同一路径实现，未逐款验证 |

只走 2.4G 接收器，不支持蓝牙。读 HID 不需要管理员权限，也不需要装驱动。

## 已知行为

- **充电时读数偏高**：K99 V2 的百分比是固件按电压查表算的，插上线电压被抬高，同一块电实测充电时约 +3%。程序不做修正。
- **任务栏层级**：Win11 的任务栏自身也是 topmost，被激活时会盖住电量条约几十毫秒；程序在焦点变化后以 4ms 粒度重新压层级。
- **不要用 QUNS_BUSY 判断是否隐藏**：家用机上系统通知状态会在 2 和 5 之间反复抖动，用它判断会让电量条每两秒消失一次。现在只在独占全屏（3）和演示模式（4）时才隐藏。
- **未代码签名**：首次运行可能撞 SmartScreen（“更多信息 → 仍要运行”）。
- **配置按机器独立**：`%APPDATA%\DevicePower\settings.json`；停靠诊断日志 `%APPDATA%\DevicePower\dock.log`（只在判定变化时写一行）。

## 构建与运行

```bash
dotnet build src/DevicePower/DevicePower.csproj
dotnet run --project src/DevicePower/DevicePower.csproj
```

免安装分发（框架依赖，约 470KB；**目标机需按上面“运行要求”先装 .NET 桌面运行时**）：

```bash
dotnet publish src/DevicePower/DevicePower.csproj -c Release -r win-x64 --self-contained false -o publish
```

想彻底免装运行时（解压后 162 MB，压成 zip 约 67 MB，直接双击就能跑）：

```bash
dotnet publish src/DevicePower/DevicePower.csproj -c Release -r win-x64 --self-contained true -o publish-selfcontained
```

开发时预览“多台设备同时显示”的排版，不用真的插两只鼠标：

```bash
set DEVICEPOWER_SIM_GPW=42c    # 数字 = 电量，末尾 c 表示充电中；不带这个变量就不会出现假设备
```

## 技术栈

.NET 8 / WPF（控件、菜单、设置页、关于页全部自绘）+ WinForms `NotifyIcon` + Win32 HID API（P/Invoke）。**没有第三方依赖。**

## 目录结构

```
src/DevicePower/
├── App.xaml(.cs)              启动、托盘/面板/停靠的装配
├── BatteryGlyph.cs            自绘电量图形（电池/条形/圆环/分段）
├── BatteryPanel.xaml(.cs)     浮动面板
├── SettingsWindow.xaml(.cs)   设置页
├── AboutWindow.xaml(.cs)      关于页（版本、功能、仓库地址）
├── TrayHost.cs                托盘图标、提示、低电量判定
├── TrayMenuWindow.xaml(.cs)   自绘右键菜单
├── Devices/                   设备抽象、轮询、外观换算
│   ├── DeviceMonitor.cs       轮询循环 + 读数发布
│   ├── MchoseConnection.cs    迈从接收器协议
│   ├── LogitechConnection.cs  HID++ 2.0
│   └── DisplayLook.cs         外观设置 → 可绑定值（含文字测量）
├── Taskbar/                   任务栏电量条与停靠
│   ├── TaskbarStrip.xaml(.cs) 电量条窗口
│   └── TaskbarDock.cs         跟随任务栏定位、层级压制、位置缓动
├── Hid/HidNative.cs           HID 枚举与读写
└── Settings/AppSettings.cs    配置读写与规范化
```

## 版本

V0.0.1

## 许可

尚未选择许可证。
