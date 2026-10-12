# 音频连续性与软件立体声（schema v4）

本次实现增加按接收端协商的 PCM/ALAC、实时 UDP/缓冲 TCP、PTP/NTP，
以及两台独立接收端的软件左右声道组。新增接收端路径与软件组均为实验性支持；
模拟接收端可以验证协议与样本，不能证明三星、Mac 或混搭设备实际出声与声学同步。

## 设置与操作

在“设置 > 接收器”的设备高级选项中选择编码、采样率、传输和时钟。
编码自动模式仅在当前采样率和流类型明确广播 ALAC 格式时优先 ALAC；
能力未知时保留旧版兼容选择。强制 PCM/ALAC 失败会报告错误，不静默换编码。
采样率限 44.1/48 kHz、16-bit；ALAC 的无损是编码前后样本一致，
采集重采样、均衡器与主增益会改变原始应用样本。PCM 也是无损传输。

“软件立体声”区域可创建、命名、选左右成员、交换、替换成员或拆分。
每组恰好两台独立接收端；原生立体声对成员、嵌套组和重复物理设备不能参与。
软件组使用共同采样率，覆盖两个成员的独立采样率设置；编码、传输和时钟仍独立协商。
每成员同步偏移为 -500…500 ms：正值延后该成员的播放，负值提前。
交换左右时，偏移跟随物理成员。组内左成员接收 `[L,L]`，右成员接收 `[R,R]`；
原生 HomePod 对接收完整立体声，由 HomePod 决定左右声道。操作不会修改家庭 App 配对。

共享采集、重采样、EQ 和 PCM 主增益后，各成员分别映射声道、编码、加密及发送。
创建组不会写入硬件音量；播放后读取两边真实音量，并提供独立音量滑块。
主音量作用于共同 PCM，设备音量作用于各自硬件。

现有“缓冲 / 500 ms”延迟档位仍表示缓冲目标；它和高级选项中的
“Buffered TCP”传输不同。自动传输优先实时 UDP，在明确不支持当前实时格式、
而支持对应 ALAC 缓冲格式时选择 TCP。首版 TCP 限 ALAC 与 PTP。

## 连续性、时间线与恢复

- 在启动、欠载与恢复、丢弃旧队列、WASAPI 断续、至少一秒精确零静音后的恢复、
  主增益变化时实施约 5 ms 过渡。连续稳定区间不增加平滑；不能识别所有应用切歌语义。
- WASAPI 断续清理旧采集队列和重采样残留。发送时钟使用固定的样本位置与播放时间映射。
  有效缺口最多补 8 个编码静音包，保持 RTP 连续；过期或超限则重建会话。
- 原生对与软件组都等待需要 PTP 的成员完成有效交换后共同启动。
  每成员显示交换数、时效、就绪状态；跟随模式还检查成对序列、来源、延迟响应及偏移。
  跟随的单播授权检查目标、来源、序列、间隔和有界租期。授权或收到包本身不表示同步成功。
- 对有公开行为依据的独立 HomePod OS 27 使用接收端 PTP 跟随，亦可手动选择。
  本实现是有限的 AirPlay PTP 模式，不含完整 BMCA；软件时间戳不证明微秒声学同步。
- 换成员、交换、拆分按整组停止与重建；正常停止发送共同淡出并等待渲染期限。
  真实传输故障或时钟失效关闭两边，按已有次数与退避整组重试，耗尽后恢复本地音频。
  广播短暂缺失保持正常传输；已确认成员加入原生对则停止软件组。
- 活动软件组保持发现监听，即使面板和接收器设置页均隐藏。
- 采集和发送线程注册 `Pro Audio` 并设 MMCSS `CRITICAL`，PTP 线程设 `HIGH`。
  失败时继续运行并报告注册/优先级结果，不设置进程级实时优先级。

实时 RTSP 顺序为会话 `SETUP → RECORD → SETPEERS → stream SETUP`，
NTP 跳过 `SETPEERS`，不再无条件启动 `FLUSH`。TCP 使用 type 103 的长度前缀、
`SETRATEANCHORTIME` 与有界非阻塞发送；保留短写尾部，处理背压与播放期限。
持久 HAP 配对继续可用，另支持接收端密码的 transient SRP 分支；
密码单独用用户绑定 DPAPI 保存，不写入配置或日志。其他接收端认证行为仍需实机验收。

## 配置与 JSONL v1

首次保存旧配置时写 `.pre-v4.bak`，保留已有延迟、音量与其他设置。
schema v3 新增 `software_groups`，接收端选项增加 `codec`、`sample_rate`、`transport`、`timing`。
当前 schema v4 删除全局 `stream_sample_rate`；设备 `sample_rate` 为空表示自动，优先选择共同支持的 44.1 kHz，其次 48 kHz。
从 v1–v3 迁移时，原先继承全局采样率的设备改为自动，设备和软件组已有显式值保留。
稳定设备别名迁移同步更新左右 ID。软件组配置示例：

```json
{
  "schema_version": 4,
  "software_groups": [{
    "id": "software:0123456789abcdef0123456789abcdef",
    "name": "Office",
    "left_id": "aabbccddee01",
    "right_id": "aabbccddee02",
    "sample_rate": "48000",
    "left_offset_ms": -80,
    "right_offset_ms": 25
  }],
  "receivers": {
    "aabbccddee01": {"codec": "alac", "transport": "auto", "timing": "auto"},
    "aabbccddee02": {"codec": "pcm", "transport": "realtime", "timing": "ntp"}
  }
}
```

原生 IPC 仍为 v1，先 `hello` 检查 `receiver_profiles`、`software_stereo`、`continuity`、
`buffered_audio`、`member_volume`、`receiver_password` 能力。
新增 `receiver_capabilities` 能力及 `get_receiver_capabilities` 查询命令：参数为带稳定
`receiver_id` 的 `peers`，只读取 `/info`，不会配对或启动音频会话；返回 `members` 中各成员的
能力矩阵，每项包含请求的编码、传输、时钟、采样率，以及 `selected_codec`、
`selected_transport`、`selected_timing` 预计结果。筛选与播放共用 Rust 协商规则。
`negotiated` 事件增加 `receiver_id`、`port` 和 `clock_role`（`follow`、`sender` 或 `ntp`），
用于显示播放时实际方式。组成员结果不同时分别显示。
旧调用省略新字段时保留旧编码与传输语义；新桌面端发送显式策略。
`start` 参数示例（外层请求仍需 `version`、`id`、`session_id`、`command`）：

```json
{
  "peers": [
    {"host":"192.0.2.10","port":7000,"profile":{"codec":"auto","transport":"auto","timing":"ptp","channel":"left","sync_offset_ms":-80,"latency_ms":200}},
    {"host":"192.0.2.11","port":7000,"profile":{"codec":"pcm","transport":"realtime","timing":"ntp","channel":"right","sync_offset_ms":25,"latency_ms":200}}
  ],
  "group_mode":"software_stereo",
  "source":"loopback","duration_ms":0,"sample_rate":48000,"latency_ms":200,"gain":0.5
}
```

`set_device_volume` 可指定成员 `host`，省略时保留原生组统一硬件音量语义。
`pair` 可指定 `password_required:true`；收到 `password_required` 后用 `pair_pin` 的
`pin` 字段传密码，仍沿用 v1。`negotiated`、`clock_status`、`scheduler_status`、
`capture_metrics` 和 `transport_metrics` 提供协商、过渡、静音、断续与队列诊断，
桌面端“监视”显示最终策略与实验性状态。

## 验证与硬件验收

自动测试包括独立 ALAC 解码逐样本一致（两个采样率）、左右映射、无额外过渡峰值、
连续区间不变、能力缺失与冲突、强制策略失败、连接顺序、真实加密双成员 TCP/UDP、
PTP/NTP、共同起点及 105 ms 相对偏移、取消第二成员握手时双方 TEARDOWN、
TCP 短写与重复背压、过期恢复、重传和跨 RTP 序列回绕的 nonce 唯一性。
.NET/WPF 回归覆盖迁移、交换、成员替换、独立音量、整组重试、广播缺失与拆分。

本轮验证结果：Rust 70 项、.NET 214 项、Python 96 项及隔离 WPF 88 项通过；
Clippy、Ruff 和桌面构建通过。Windows 兼容性静态审查通过，但仍有 141 项未确认导入，
不能据此声明已完成 Windows 10 实机兼容验收。完整系统托盘测试在当前运行环境无法注册图标；
隔离 WPF 报告只覆盖界面与模拟服务。实机录音测试和默认 30 分钟 soak 保持 opt-in。

2026-10-11 的 localhost 压力测试使用四个忙线程运行 600 秒，
发送/接收 150,344 包（两成员合计），最大迟到 2,007 μs，无丢包。
该测试检查媒体编码与 UDP 调度，完整控制/时钟链路由较短的加密会话测试验证；
不替代网络干扰、真实设备和 30 分钟默认 soak。

可运行 `scripts/build-native.ps1 -Check`、.NET tests、Python pytest/Ruff。
隔离 WPF 回归使用 `AirFlash.exe --ui-smoke --ui-regression --output <report.json>`，
全部网络、音频、开机启动服务为模拟；完整 `--ui-smoke` 另包含真实系统托盘检查。
生成报告与渲染图位于指定报告目录。高负载 soak 可设
`AIRFLASH_SOAK_SECONDS=600`、`AIRFLASH_SOAK_LOAD_THREADS=4` 后运行 ignored `soak` test。

待硬件验收：单 HomePod、原生 HomePod 对、三星 Q990、Mac Receiver、跨品牌软件组，
分别确认实际出声、恢复爆音、长期稳定性和声学左右同步。暂不承诺正式三星/Mac 兼容、
微秒声学同步或更低端到端延迟。本轮不含校准向导、Atmos、24-bit 或三台以上分组。

## 协议依据

- [airplay-cli 发送端研究](https://github.com/music-assistant/airplay-cli/blob/main/DESIGN.md) 与其 type 103、格式列表、锚点实现。
- [OwnTone AirPlay 连接顺序](https://github.com/owntone/owntone-server/blob/master/src/outputs/airplay.c)。
- [Shairport Sync AirPlay 2](https://github.com/mikebrady/shairport-sync/blob/master/AIRPLAY2.md)。
- [libairptp](https://github.com/owntone/libairptp)。
- [Microsoft MMCSS](https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service)。

公开 AirPlay 文档主要来自逆向研究；引用提供实现依据，不代表协议认证或硬件验收。
