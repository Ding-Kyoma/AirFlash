# AirFlash Sound Glow Test

这是可选的实验功能，默认不发送测试元数据。
目的：验证第三方 AirPlay 2 发送端传入的歌曲元数据和 JPEG 封面，是否会被 HomePod 的 Sound Glow 使用。
不是苹果官方灯光接口，也不保证 HomePod 会亮灯。

## 开始测试

1. 完全退出原版 AirFlash，解压测试包后启动 `AirFlash.exe`。无需安装。
2. 在 iPhone/iPad 的家庭 App 中启用目标 HomePod 的 Sound Glow；需要支持该功能的型号及固件。
3. 连接 HomePod，先正常播放音乐。建议先用单台设备，声音调低；观察原始音频模式的灯光。
4. 点击主面板上的 **Sound Glow Test · 灯效实验**。
5. 点击 **仅歌曲信息**，观察歌曲标题是否出现在家庭 App 的播放信息中，以及灯光是否变化。
6. 点击 **红色**，再点击 **蓝色**，每次留几秒观察实际灯光。
7. 有效果后可启用 **每 15 秒随机换色**。同一颜色不会连续出现。

原版与测试版使用同一个 AirFlash 配置及配对目录；不迁移、不重置设置。不要同时运行两份。
第一次运行无代码签名的实验程序可能出现 Windows SmartScreen 提示。

## 怎样判断结果

| 观察结果 | 含义 |
| --- | --- |
| 引擎已排队 | 尚未收到 HomePod 响应 |
| metadata / artwork：200 接受 | 相应 RTSP 请求成功；不是灯效成功证明 |
| 歌曲信息或封面在家庭 App 更新，但灯光不变 | 元数据路线至少部分有效，Sound Glow 可能还有额外触发条件 |
| 红、蓝封面使实际灯效对应变化 | 该设备、固件、连接模式下，封面驱动灯效路线验证成功 |
| 4xx/5xx 或超时 | 请求被拒绝或连接出错，请保留日志；随机换色会停止 |
| 一对 HomePod 只有一台响应 | 记录两台日志；原生立体声的元数据分发仍待实机验证 |

可点击 **打开诊断日志**。文件位于 `%APPDATA%\AirFlash\logs`，搜索 `Sound Glow`。
日志记录主机、配色、sequence、metadata/artwork、错误；不会写入 JPEG 正文或配对密钥。
反馈时请提供 HomePod 型号、固件、单台/立体声、UDP/TCP、灯光观察与上述日志。

## 回到对照组

- **回到原始音频（停止并重连）** 会重新建立不注入测试元数据的会话，短暂中断音频。
- 这不是远程关闭 HomePod 的 Sound Glow。设备可能暂时缓存旧封面，不能把它当作严格的设备端清空操作。
- “仅歌曲信息”不清除已经发送的封面；测试该条件前先重连。
- 关闭实验窗口会停止随机换色，最后发送的封面可能保留。
- 应用重启/新建会话后默认不注入。设备待机恢复可能在同一引擎会话内重新发送最后一次测试选择。
- 需要彻底关闭灯效时，使用家庭 App 的 Sound Glow 开关。

## 实现范围

- 保留原来的 WASAPI → PCM/ALAC → AirPlay 2 音频路径。
- 实验按钮通过 `set_sound_glow` JSONL 命令传入固定枚举，不接收任意文件路径或无限大小图片。
- 使用 `SET_PARAMETER` + `application/x-dmap-tagged` 发送 `mlit/minm/asal/asar`。
- 使用 `SET_PARAMETER` + `image/jpeg` 发送内嵌 256×256 红/蓝/绿图片。
- 请求包含当前 RTP sequence/timestamp，并在接收端返回 Session 时携带该标识。
- 独立工作线程与已有控制请求共用连接事务锁，不在实时音频循环执行网络请求。
- 默认零元数据请求；只接受递增序列号。每个请求单独回报，不将 `200` 标记为灯效成功。
- 未实现任意 LED 动画、远程 Sound Glow 设置开关、真实播放器封面读取、额外的私有 Now Playing 协议。
- 本版协议假设参考 pyatv 的 `pyatv/support/rtsp.py`（`set_metadata` / `set_artwork`）。贡献者 blessyou617 已报告基于 v0.5.4、使用发送端 PTP 主时钟的测试版可播放并触发灯效；型号、固件和立体声配置待补充。当前主分支上的实机兼容性尚待验证。

## Windows 本地构建

需要 .NET 10 SDK、Rust MSVC、Visual Studio C++/Windows SDK，以及 uv（与上游相同）。

```powershell
uv sync --locked
pwsh scripts/build-native.ps1 -Check
pwsh scripts/dotnet.ps1 test AirFlash.sln
pwsh scripts/dotnet.ps1 publish AirFlash.App/AirFlash.App.csproj -c Release -r win-x64 --self-contained true '-p:IncludeNativeLibrariesForSelfExtract=true' -o ../dist/sound-glow
```

本测试版不代表上游项目已支持 Sound Glow，不会自动发布或提交到上游。
