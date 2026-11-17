# 声桥 SoundBridge

让 Windows 电脑接收手机和平板的蓝牙媒体音频，并通过电脑当前的音频输出播放。声桥使用 Windows 自带的 A2DP Sink，无需在手机上安装应用。

## 使用

1. 下载自包含 Windows x64 压缩包，完整解压，在解压目录运行 `SoundBridge.exe`。
2. 在 Windows 的「蓝牙和设备」设置中配对手机或平板。
3. 点击任务栏通知区域的声桥图标，刷新设备列表，然后点击「连接」。在手机或平板上开始播放。

托盘右键菜单可打开设备列表、开关「自动恢复声音」和「开机自启动」，也可退出程序。两个开关首次运行时默认关闭。自启动会在登录当前 Windows 用户后运行声桥，便携版建议放到固定目录后再开启。

## 系统要求

- 推荐 Windows 11 x64，电脑需有支持 Bluetooth Classic 的蓝牙适配器。Windows 10 需同时满足 A2DP Sink 的最低版本（内部版本 19041）和 [.NET 11 的 Windows 支持范围](https://learn.microsoft.com/dotnet/core/install/windows#supported-versions)。
- 自包含 x64 包内置 .NET 11 桌面运行时。依赖运行时的版本需自行安装 [.NET 11 Desktop Runtime（x64）](https://dotnet.microsoft.com/download/dotnet/11.0)。

## 从源码构建

在 Windows 上安装 .NET 11 SDK，然后在仓库根目录运行：

```powershell
dotnet build src/SoundBridge.App/SoundBridge.App.csproj -c Release
dotnet run --project src/SoundBridge.App -c Release -- --show
```

发布自包含 x64 ZIP：

```powershell
dotnet publish src/SoundBridge.App/SoundBridge.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist/publish
pwsh -File tools/pack.ps1
```

产物是 `dist/SoundBridge-win-x64-portable.zip`。exe 与三份 WPF DLL、`Assets` 文件夹一起分发。要生成安装程序，可安装 NSIS 3.x 后运行以下命令：

```powershell
pwsh -File tools/build-installer.ps1 -Portable -OutName SoundBridge-Setup-1.0.0-portable.exe
```

## 排查

- **看不到托盘图标**：先展开任务栏通知区域的隐藏图标，也可用 `SoundBridge.exe --show` 打开设备窗口。
- **连接成功却没声音**：先确认源设备正在播放，且输出切到电脑。可以在托盘打开「自动恢复声音」并重连。程序会等待对应的输出会话，然后短暂静音并恢复。
- **两台设备同时播放时一路断续**：在共用同一蓝牙适配器的环境中已复现。本版本支持连接多台设备，主要验证单路播放。双播时实测有一路在输入端供帧不足，初步指向 Windows A2DP 接收链路，具体原因尚未确认。

日志默认在 `%LOCALAPPDATA%\SoundBridge\log.txt`。提交 Issue 时，请附上 Windows 版本、蓝牙适配器型号、复现步骤和相关日志。分享前可以删掉自定义设备名称。

## 项目结构

- `src/SoundBridge.App`：WPF 界面、托盘、启动选项和应用生命周期。
- `src/SoundBridge.Core`：WinRT A2DP 连接及 Core Audio 端点、会话操作。
- [开发说明](docs/声桥-开发设计.md)

## 许可证

声桥采用 [MIT 许可证](LICENSE)。
