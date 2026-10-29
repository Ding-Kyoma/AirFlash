# 三项暂缓项的设计（未实现，待硬件/抓包）

> 本文件只写「要做成什么样、边界在哪、接口长什么样」，不写实现。截至本文档落笔，
> 仓库里**没有** P1-10 / P3-4 / P3-5 的任何生产代码：没有 `calibration.rs` /
> `mrp.rs` / `raop.rs`，没有对应的 IPC 命令，也没有为它们新增依赖。文中出现的
> 命令名、字段名、状态名全部是**占位设计**，不是现有行为；在代码里搜不到它们是
> 预期结果。
>
> 为什么这么处理：这三项的核心输入分别是「真实接收器的容忍度数据」、「MRP 的
> 消息编号与加密封装」和「FairPlay 的密钥调度」。缺了输入就写实现，产出会编译、
> 会跑、会打日志，但**不可能在真机上成立**——它把「没做」藏进了日志里，比明确
> 不做更糟。所以本轮只交付设计，代码等输入。

读法标记：

| 标记 | 含义 |
| --- | --- |
| 〔公开〕 | 有公开规范或公开实现可对照，设计可以写死；引用来源类型，不复制别人的代码 |
| 〔需真机〕 | 必须在一台具体型号/固件的接收器上观察，抓包替代不了 |
| 〔需抓包〕 | 需要一次能解出明文的抓包；被动 pcap 只能看到密文 |
| 〔未知〕 | 本文件明确不猜；实现前必须补数据，写成「未知，需抓包确定」 |

需要区分证据强度时还会用几个细化标记：〔已有〕= 仓库里已经能用的机制、
〔公开字符串〕= 公开字符串池里的字面量（只有名字，没有语义）、〔推断〕= 由证据
推出来但未证实的结论、〔部分〕= 一部分可写一部分不可写。字面即含义，不单独展开。

## 状态表

| 项目 | 状态 | 解锁条件 |
| --- | --- | --- |
| P1-10 延迟标定 | 未实现，待硬件/抓包 | 至少一台单机接收器与一对同型立体声对（各记录型号/固件）+ 可复现的会话遥测；判据阈值和推荐值必须在真机上标定，本文件不给默认数 |
| P3-4 MRP 隧道 | 未实现，待硬件/抓包 | 一份**能解出明文**的 MRP 数据流抓包（含会话密钥或发送端明文 dump）+ 一台 Apple TV/HomePod；被动 pcap 只能看到密文 |
| P3-5 经典 RAOP | 未实现，待硬件/抓包 | 一台 AirPort Express（或 AirPlay 1 第三方设备）+ 一份 ANNOUNCE/SETUP/RECORD 抓包；FairPlay 路径另需强制 `fpaeskey` 的目标设备与许可评估 |

**一句话边界**（先看这张，再看每节）：

| 项目 | 今天能写进设计的 | 今天写不了的 |
| --- | --- | --- |
| P1-10 | 仪器：试听音、按阶梯套候选、逐段取遥测快照、存 profile、状态机 | 结论：什么算「紧」、推荐几毫秒、归因规则、可重复性 |
| P3-4 | 通道结构、密钥字段名、角色名、11+3 个字段名、生命周期、失败隔离 | protobuf 的消息号/字段号、帧封装、DataStream `type` 与 `/info` 端点、加密封装 |
| P3-5 | RTSP/RAOP 控制面、SDP、ALAC 帧格式、RSA-OAEP 包装的 AES 密钥、AES-CBC、DACP 形状 | FairPlay `fp-setup`（SAP）、`fpaeskey`、目标设备是否强制 FairPlay |

三项共同的实现纪律（写实现时先读这一段）：

1. **功能门默认关。** 未实现的能力不得出现在 `hello` 的 `capabilities` / `commands`
   里；桌面端不得出现「能点但连不上」的入口。
2. **新通道失败一律降级。** MRP 隧道、音量隧道、标定失败都只能产生 `warning`
   与状态事件，不得结束音频会话；音频路径的异常优先级永远最高。
3. **明确拒绝优于静默失败。** 例如 `raop_fairplay_required`、
   `mrp_unsupported`、`calibration_attribution_missing`——宁可让用户看到一句
   人话，也不要出现「连上了但什么都不发」。
4. **新依赖只在输入确定后引入。** protobuf / RSA 库都要先回答「没有它能不能做」，
   引入时在提交信息里写清用途与来源。
5. **不改已发布行为。** `set_now_playing`、`set_lead`、`set_device_volume`、
   `SET_PARAMETER` 元数据、事件通道的 `mrCommand` 解析、`LatencyPolicy` 的现有
   模式一律不动；新能力用新命令、新字段、新文件。

---

## P1-10 延迟标定流程

### 1. 做成之后用户看到什么

用户不再从 30/50/120/200 这些数里猜一个填进设置。点开设置页的「测量这台设备」，
应用先放一段很轻的试听音，按拓扑的候选阶梯从低到高逐档套用，每档停一个固定窗口
并收一次会话遥测；结束时给出一句能读的结论，例如「这台 HomePod（单机，ALAC+PTP）
推荐 60 ms；低于 45 ms 时窗口内出现 2 次欠载」（数字仅为示意，不是测量结果），
并把它存成「这台设备 + 这套编解码/时钟组合」的 profile。下次连同一台设备直接
复用，换一台同型号重新测；同一天跑两次，两次推荐值相差不超过 10 ms。用户感到的
是「调延迟从玄学变成测量」，而不是「延迟被消灭了」——标定量化的是接收器的容忍度，
不是声学延迟。

### 2. 阻塞边界：仪器可写，结论不可写

| 部分 | 状态 | 说明 |
| --- | --- | --- |
| 逐段套用候选 lead 并读回「已应用」 | 〔公开/已有〕 | 现有 `set_lead` + `request_lead` 能在运行中改 lead；`lead_changed` 事件已经带 `requested_ms` / `applied_ms` / `ceiling_ms`，标定据此等回执，**不需要新的读接口** |
| 30 s 窗口的遥测快照 | 〔已有〕 | `telemetry::snapshot()` 已含 `packets`/`late_packets`/`underruns_active`/`timeline_reanchored`/`stall_rtx_drained`/`lateness`/`servo`/`lead_extra_ms` 等；CSV 侧有 33 列 `session_summary` |
| 候选阶梯 | 〔已有〕 | `LatencyPolicy.For()` 已按拓扑给出 `Initial/Floor/Ladder`，标定只是按它遍历 |
| 试听音的合成与播放 | 〔公开〕 | 由桌面端播放 440 Hz 正弦 WAV，走现有采集路径；引擎的音频路径**一行不改**。代价：标定只在 `loopback` 捕获模式下可用，`endpoint` / `application` 模式必须明确拒绝 |
| profile 持久化与复用 | 〔公开〕 | 新增独立文件；不动 `LatencyMode` / `CustomBufferMs` 的现有语义 |
| **「紧」的判据** | 〔需真机〕 | 哪个计数先动、动多少算坏、窗口内几次算坏——只有真机能给标签 |
| **推荐值本身** | 〔需真机〕 | 没有任何公开资料能替代实测；本文件不给数 |
| **归因**（本机/网络/接收器/伺服） | 〔需真机〕 | 现有计数器是输入，不是结论；输入到原因的映射必须用真机数据拟合 |
| **可重复性** | 〔需真机〕 | 报告 §P1-10 的验收是「两次差 ≤10 ms」，这本身就是待测命题 |
| **绝对延迟（毫秒数）** | 〔需真机〕 | 若要「真实声学延迟」，需要麦克风/回环与时间戳对齐；本文件不假设方法 |

一句话：**能写的是仪器，不是结论。** 实现时 `recommended_ms` 与 `verdict` 必须
先是 `null` / `"unknown"`，带一个明确的 `reason`；任何「先填一个看起来合理的数」
都是把未验证的猜测伪装成测量。

另外两处容易混进来的东西，明确排除：

- 这不是 P0-2 的「自适应提前量」。P0-2 在运行中按迟到/欠载微调，方向是「不够就
  加」；标定的产物是一个**起始值/下限**，不替代运行中的阶梯。
- 这不是 `LatencyMode = "custom"`。custom 是用户手填；标定要回答的是「手填多少
  才合理」，两者可以共存，但 store 必须是独立的。

### 3. 接口占位（全部未实现）

> 这一节没有任何现有代码对应。`hello` 现在返回的 `commands` 是
> `["hello","get_receiver_capabilities","list_applications","start","probe",
> "stop","set_gain","set_equalizer","set_standby","set_device_volume","set_lead",
> "set_now_playing","pair","pair_pin"]`，里面没有 `calibration_*`；实现时再追加，
> 并且 `capabilities` 只能在实际可用后才报告 `calibration`。

**会话前置**：标定必须在**活着的会话**上做（和 `set_standby` 一样要求会话还在），
这样测的是真实链路，不需要第二套协商。捕获模式必须是 `loopback`：`endpoint` 模式
下试听音可能播到另一个端点、`application` 模式只抓单个进程，两者都会让「测到的
是静音」——静音在遥测里看起来非常健康（没有欠载、没有重锚），这是最危险的假阳性。
两种模式都必须明确拒绝并说明原因，不能“跑了但测不到”。

**试听音在桌面端**：由向导播放一段 440 Hz 正弦 WAV（电平见前置清单），捕获路径
和正常播放完全一样。这样引擎不需要新的音频通路，也不需要改 `session.rs`；代价是
标定期间用户不能同时放别的系统声音（见前置清单）。

**每段开始前确认能听到**：向导在放音 1 s 后问一次「能听到吗」，用户答「听不到」
就立即中止整轮，并把原因记成 `tone_not_audible`。这条不是形式主义：系统静音、
端点选错、接收器没连上，都会让遥测看起来完美，只有人耳能区分「很稳」和「根本没响」。

**IPC 命令族**（命名沿用现有小写下划线）：

| 命令 | 参数 | 响应事件 | 说明 |
| --- | --- | --- | --- |
| `calibration_begin` | `candidates_ms:[u32]`、`segment_ms:u32`（默认 30000）、`settle_ms:u32`（默认 1000） | `calibration_ready {plan_id, steps:[{index, candidate_ms, state:"pending"}]}` | 只登记计划，不改 lead；会话不活 ⇒ `calibration_requires_session`，捕获模式不是 `loopback` ⇒ `calibration_unsupported_capture_mode` |
| `calibration_candidate` | `plan_id`、`candidate_ms` | `calibration_applied {plan_id, candidate_ms, applied_lead_ms, elapsed_ms}` | 内部走现有 `request_lead`；桌面端等 `lead_changed` 的 `applied_ms == candidate_ms`（4 s 超时 ⇒ `calibration_apply_timeout`），然后等 `settle_ms` 再放音 |
| `calibration_snapshot` | `plan_id` | `calibration_snapshot {plan_id, candidate_ms, applied_lead_ms, elapsed_ms, verdict:"ok" / "tight" / "unknown", reason, metrics:{…}}` | 桌面端在试听音放完 `segment_ms` 后调用；`metrics` 复用 `telemetry::snapshot()` 的字段形状，**不加新计数器**；未标定前 `verdict` 恒 `"unknown"`、`reason:"attribution_not_calibrated"` |
| `calibration_finish` | `plan_id` | `calibration_result {plan_id, recommended_ms:null, floor_ms:null, verdict:"unknown", reason, runs:[…]}` | 第一版必须返回 `null`；等真机数据回来再让 `recommended_ms` 有值 |
| `calibration_abort` | `plan_id` | `calibration_aborted {plan_id, restored_lead_ms}` | 停音 + 恢复开始前的 lead + 不写 profile |

引擎侧不做「试听音开/停」命令，是因为音源在桌面端：多一个引擎命令只会让两边
各持一半状态，反而容易在 abort 时留下一个停不掉的音。

**被天花板夹住的候选**：`set_lead` 现在会把请求夹到 `latency_ms + 100 ms`
（`lead_changed` 里报 `ceiling_ms`）。如果 `applied_ms != candidate_ms`，这个候选
记为 `clamped` 并跳过，**不能**当成测量结果——否则标定会把「对端不接受的延迟」
记成「用户听感不好」。

`calibration_snapshot` 的 `metrics` 至少包含：`applied_lead_ms`、`packets`、
`late_packets`、`underruns_active`、`underruns_idle`、`timeline_reanchored`、
`last_timeline_reset_reason`、`stall_rtx_drained`、`lead_shift_count`、
`lateness.p99_ms`。这些字段今天都有来源，不需要新埋点。

**模块边界**：

| 文件 | 角色 | 现状 |
| --- | --- | --- |
| `native/airflash-engine/src/calibration.rs`（新增） | 计划状态机、快照组装、结果 JSON | 不存在 |
| `native/airflash-engine/src/main.rs` | 在 `match` 里分发 `calibration_*`，并把名字加进 `hello.commands` | 只加分支，不动现有命令 |
| `native/airflash-engine/src/telemetry.rs` | 快照唯一来源 | 不加计数器 |
| `desktop/AirFlash.Core/CalibrationSession.cs`（新增） | 驱动 IPC、等 `lead_changed`、超时、进度 | 不存在 |
| `desktop/AirFlash.Core/CalibrationTone.cs`（新增） | 播放/停止 440 Hz 试听音（WAV） | 不存在 |
| `desktop/AirFlash.Core/CalibrationProfileStore.cs`（新增） | profile 的读写与版本迁移 | 不存在 |
| `desktop/AirFlash.App/Ui/SettingsWindow.xaml` + `SettingsViewModel` | 「测量这台设备」段落 | 新增段落，不动现有控件 |

`session.rs` **不需要改**：已应用值从 `lead_changed` / `lead_extra_ms` 读，试听音
走桌面端与现有采集路径。这是这项设计里最重要的减法——标定不应该为了「测一下」
去碰已经稳定的时间线代码。

**状态迁移**（每一步都有超时，失败必须先停音）：

| 状态 | 进入条件 | 超时 | 退出 |
| --- | --- | --- | --- |
| `idle` | — | — | `begin` |
| `planned` | `calibration_begin` 成功 | 无 | 第一个 `candidate` |
| `applying` | 收到 `candidate_ms` | 4 s | `settling` / `calibration_apply_timeout` |
| `settling` | `lead_changed` 的 `applied_ms` 等于候选 | `settle_ms` | `confirming` |
| `confirming` | 桌面开始放音 1 s 后询问「能听到吗」 | 10 s | `measuring` / `aborted`（`tone_not_audible`） |
| `measuring` | 用户确认能听到 | `segment_ms` | `snapshot` |
| `snapshotted` | 收到快照 | 无 | 下一个 `candidate` / `finishing` |
| `finishing` | 最后一个快照 | 2 s | `done` / `failed` |
| `aborted` / `failed` | `abort` 或任一错误 | — | 停音、恢复 lead、不写 profile |

**profile 形状**（新增文件，不碰现有设置键）：

```json
{"schema_version":1,
 "receiver_id":"<stable id>","codec":"alac","timing":"ptp","sample_rate":44100,
 "recommended_ms":null,"floor_ms":null,
 "candidates_ms":[45,50,60,80,120],
 "runs":[{"at":"<iso8601>","candidate_ms":60,"applied_lead_ms":60,
          "verdict":"unknown","reason":"attribution_not_calibrated",
          "metrics":{"underruns_active":0,"timeline_reanchored":0,"p99_lateness_ms":null}}],
 "attribution_version":null,
 "engine_version":"<version>"}
```

键的设计要点：`receiver_id + codec + timing + sample_rate` 才是一个完整键（同一个
HomePod 换到 NTP 或 PCM 是另一组容忍度）；`recommended_ms:null` 是刻意的——第一版
只采集，不推荐。**不改**现有 `Settings.LatencyMode` / `ReceiverOptions.CustomBufferMs`
的读写路径；用户启用标定值时，UI 只是把 store 里的值喂给启动参数，标定文件损坏
时回落到现有 `LatencyPolicy`。

### 4. 真机验收（开始动工的门槛）

1. **可重复性**：同一台设备、同一 codec/timing，连续两次完整标定
   `|Δrecommended_ms| ≤ 10 ms`；隔天再跑一次仍在 10 ms 内。这是报告 §P1-10 的
   原验收，必须原样满足。
2. **推荐值下的稳定性**：用推荐值正常播放（用户自己的音乐，不是测试音）10 分钟，
   会话摘要 `underruns_active == 0`、`timeline_reanchored == 0`（允许 ≤1 次且
   原因是 `latency_change`），没有 `confirmed_transport_failure`。
3. **仪器能看见「紧」**：把 lead 调到推荐值 −15 ms，同一会话 30 s 内出现可重复的
   欠载或 p99 迟到上升；恢复推荐值后归零。这条证明的是「测量方式有效」，不是
   「数值正确」。**注意**：30 s 的测试音与 AGENTS.md 的「真机 E2E 最长 5 s」冲突，
   见前置清单最后一条。
4. **立体声对**：两台成员的推荐值都不得低于各自的 floor，组 lead ≥ 120 ms；
   只测其中一台时不得把组值改小，也不得给两台算出不同的 timing。
5. **中止干净**：任意时刻 `calibration_abort`，试听音在 100 ms 内停止，lead 回到
   开始前的值，会话继续，不产生 `latency_change` 以外的 timeline reset。
6. **失败隔离**：接收器在标定中掉线 ⇒ `calibration_failed` 一次，不自动重试，
   不写 profile，音频会话按既有的掉线路径处理。
7. **主观标签可复现**：同一个人两次听同一档，结论一致（这是归因数据的前提；
   如果人耳判断本身不可重复，标定就该换客观判据或放弃）。

### 5. 前置清单

- [ ] 一台 solo HomePod（记录型号 + 固件）作为 `alac/ptp` 基准。
- [ ] 两台同型做立体声对，能验证组内一致性与 120 ms 地板。
- [ ] 一台 NTP 设备（AirPort Express 2 或 Apple TV）覆盖 `alac/ntp`、`pcm/ntp`
      两种候选——否则那两行阶梯永远没有数据。
- [ ] 安静环境 + 用户在场：归因需要「卡 / 不卡」的人工标签，无人值守跑不出来。
- [ ] 捕获模式是 `loopback`（试听音要能被采集到）；`endpoint` / `application`
      模式的结构性限制先接受，并在 UI 里说清「此模式不能标定」。
- [ ] 试听音电平先定下来并经用户确认。AGENTS.md 的幅度 0.05 / gain 0.1 是
      **自动化 E2E 的上限**，不等于标定音的电平；标定音要「能听出断续但不能
      吓人」，这个值只能在真机上定。
- [ ] 30 s 段与「真机 E2E 最长 5 s」的政策冲突先解决：要么用户批准一次例外
      （标定是人工在场的交互流程，不是 `scripts/e2e_stream.py` 的自动化 E2E），
      要么把段长改成 5 s × N 次并接受统计噪声。**没解决前不要写实现。**
- [ ] 若要给出「绝对延迟」而不是「容忍度」：麦克风/回环 + 时间戳对齐方案；
      本文件不假设。
- [ ] `session_summary` CSV 可收集、可与桌面日志按 session id 对上（已有）。

拿到数据后的第一步：先只做仪器（无判据、`recommended_ms:null`），跑 5 个候选 ×
3 轮，把「候选 → 计数」的散点图拿出来；阈值和归因规则从图里定，不从这份文档里定。

---

## P3-4 MRP 隧道

### 1. 做成之后用户看到什么

Apple TV 和 HomePod 的屏幕上出现正在播放的歌名、歌手、专辑和封面；暂停时进度条
停住，恢复后从正确的位置继续；停止后不残留上一首的封面。Apple TV 遥控器上
「下一首」在最后一首变灰、进度条能拖、随机/循环状态正确；音量走一条独立隧道，
设备上调音量电脑能看到、电脑上调设备立刻响。用户感到的是「这台设备认识我在放
什么」，而不是一块只出声的黑屏。

### 2. 阻塞边界：结构可写，编号与封装不可写

先把两件常被混在一起的事分开：

- **事件通道**（`eventPort`，会话密钥派生的加密，body 是 plist）上的 `mrCommand`——
  AirFlash 已实现解析（`events.rs`，`transport.rs` 的 `EventWorker`），它管的是
  「设备把按键发给我」。
- **MRP 数据流**（`DataStream`）——完全没有，它管的是「我把 Now Playing 发给
  设备、设备把命令能力告诉我」。**MRP 是另一条通道，用 protobuf，不是 plist。**

| 部分 | 状态 | 说明 |
| --- | --- | --- |
| SETUP 里追加 `streams` 项的结构 | 〔已有形状〕 | 现有代码已经会发 `streams:[{…}]`（音频流）；追加第二项是结构上的加法 |
| 三个密钥字段名 `DataStream-Salt` / `DataStream-Output-Encryption-Key` / `DataStream-Input-Encryption-Key` | 〔公开字符串〕 | 来自 WAC 字符串池；语义（输出=我发，输入=我收）是推断 |
| 客户端身份 `com.apple.TVMusic` / `com.apple.TVRemote` | 〔公开字符串〕 | 两种控制角色；**怎么声明**（header / plist / protobuf 字段）〔未知〕 |
| Now Playing 11 个字段名 + 封面 3 个字段名 | 〔公开字符串〕 | 字段**名**可抄；字段**号**不可抄 |
| `updateMRNowPlayingInfo` / `updateMRPlaybackState` / `updateMRSupportedCommands` / `clearNowPlaying` | 〔公开字符串〕 | 消息**名**已知；消息**编号/字段号**〔未知〕 |
| 编码族是 protobuf | 〔推断〕 | 到字段级未证实；实现前必须以抓包为准 |
| DataStream 的 `type` 数值 | 〔未知〕 | 需抓包确定 |
| SETUP / SETUP 响应里承载端口与密钥的 plist 键 | 〔未知〕 | 需抓包确定 |
| `/info` 里的 MRP 端点（键名与取值形态） | 〔未知〕 | 需抓包确定；现有解析只读 `model`/`name`/`sourceVersion`/`features`/`supportedFormats`/`supportedAudioFormatsExtended`/`deviceID`/`initialVolume`，新键必须按可选处理 |
| UDP/TCP、是否复用 RTSP 的 `interleaved` | 〔未知〕 | 需抓包确定 |
| 帧封装：长度前缀 / 压缩 / 类型标记 | 〔未知〕 | 需抓包确定 |
| 哪个密钥加密哪个方向、nonce/AAD 构造 | 〔未知〕 | 需抓包确定 |
| 必需消息集合、tvOS/HomePod 固件差异 | 〔未知〕 | 需真机 + 多固件对照 |
| 封面的尺寸/格式/`ArtworkIdentifier` 语义 | 〔部分〕 | 字段名已知；WAC 的做法是 4 MiB 上限 + 一律重编码（观察值），设备实际接受范围〔需真机〕 |

**必须写清的一条**：事件通道的 `mrCommand` 是 **plist**，MRP 数据流是
**protobuf**。把两者当成同一个编码，是这项最容易犯的错误；现有 `events.rs`
的解析器一行都不能复用到 MRP 上。

### 3. 接口占位（全部未实现）

**模块**：`native/airflash-engine/src/mrp.rs`（新增）。

| 职责 | 说明 |
| --- | --- |
| 在 SETUP 里协商 DataStream | 追加第二个 `streams` 项（`type` 未知）；响应给出端口与密钥 |
| 持有两条加密方向 | 与 `EventWorker` 同构：HKDF(salt, info) 派生，`Cipher` 记录方向计数，但**盐/信息字符串与 nonce 规则必须来自抓包** |
| 发布 Now Playing / 播放状态 / 命令能力 | 编码器在 schema 拿到前只有一个「未实现」分支，返回 `Unsupported` |
| 独立音量隧道 | 单独一条连接，双向读写；不复用控制流 |
| 生命周期 | 停止时必须发 `clearNowPlaying`；会话结束前关闭并等对端确认或超时 |
| 失败隔离 | 任何错误 → `mrp_state=degraded` + 一条 `warning`；不碰音频 `Packetizer`，不结束会话 |

占位函数族（签名在实现时定，语义先定）：

| 占位 | 语义 | schema 缺失时的行为 |
| --- | --- | --- |
| `open()` | 协商并建连 | 返回 `Unsupported`，状态 `unsupported` |
| `publish_now_playing(now)` | 11 字段 + 封面三件套 | 返回 `Unsupported` |
| `publish_playback_state(state)` | 播放/暂停/速率 | 返回 `Unsupported` |
| `publish_commands(caps)` | 能力声明 | 返回 `Unsupported` |
| `clear()` | 停止时清屏 | 返回 `Unsupported` |
| `set_volume(host, value)` / `poll_volume()` | 音量隧道 | 返回 `Unsupported` |
| `close()` | 正常关闭，区分「超时」与「对端关闭」 | 幂等 |

**IPC**（新增命令；现有命令不动）：

| 命令/事件 | 形状 | 说明 |
| --- | --- | --- |
| `set_artwork`（新） | `{track_id, mime:"image/jpeg" / "image/png", sha256, total_bytes, chunk_index, chunk_total, data_base64}` | 封面按块传输（每块解出 ≤48 KiB），引擎按 `track_id + sha256` 重组；整张图超过上限（初值 4 MiB，需真机确认）⇒ `artwork_too_large`，只记 warning，不影响音频 |
| `artwork_accepted`（新） | `{track_id, sequence, bytes}` | 与 `metadata_accepted` 同构 |
| `set_remote_capabilities`（新） | `{sequence, commands:[{name, enabled, scrub, reference_position, preferred_intervals_ms}]}` | `name` 复用 `events.rs::RemoteCommand::name()` 的词表；`enabled` 是「当前是否可选」 |
| `remote_capabilities_accepted`（新） | `{sequence}` | |
| `mrp_state`（新事件） | `{host, state:"disabled" / "negotiating" / "open" / "degraded" / "closed" / "unsupported", reason}` | 状态变化才发，不刷屏 |
| `mrp_volume`（新事件） | `{host, volume, source:"tunnel" / "rtsp", sequence}` | 隧道的读数与 RTSP 读数分开报，避免覆盖 |
| `set_device_volume`（现有） | 不变 | RTSP 路径继续是默认；隧道可用后也只是「额外一条」，不替换 |

**与桌面端的边界**：

| 文件 | 角色 | 现状 |
| --- | --- | --- |
| `desktop/AirFlash.App/Metadata/SmtcNowPlayingSource.cs` | 已能读 title/artist/album/时长/位置 | **不读封面**（`properties.Thumbnail` 未用）；这是 P0-4 的遗留，MRP 依赖它 |
| `desktop/AirFlash.Core/NowPlaying.cs` | 现有七字段 | 不动；封面/能力走新类型 |
| `desktop/AirFlash.Core/ArtworkStore.cs`（新增） | 取图、限长、算 `sha256`、缓存 | 不存在 |
| `desktop/AirFlash.Core/MrpCapabilities.cs`（新增） | 由播放器状态推导可用键 | 不存在 |

**状态迁移**：

| 状态 | 进入 | 退出 |
| --- | --- | --- |
| `disabled` | 默认；设置里 MRP 未开 | 用户开启 |
| `negotiating` | SETUP 期间追加 DataStream | 响应成功 → `open`；拒绝/缺键 → `unsupported` |
| `open` | 密钥、端口、身份都就绪 | 任一编码/解码/超时错误 → `degraded`；正常结束 → `closed` |
| `degraded` | 隧道坏但音频健康 | 重连成功 → `open`；会话结束 → `closed` |
| `unsupported` | `/info` 无端点或 SETUP 拒绝 | 终止（本会话内不再重试） |
| `closed` | 停止/超时 | 终止 |

`degraded` 是这一节的核心设计：**隧道坏了不是会话坏了**。重连策略最多每 30 s
一次、最多 3 次，之后停在 `degraded` 直到下一次会话。

### 4. 真机验收（开始动工的门槛）

1. **Now Playing**：Apple TV 上开始播放后 2 s 内出现歌名/歌手/专辑；换曲后封面
   跟着换（JPEG 与 PNG 各一张，均 < 4 MiB），旧封面不残留；停止后 2 s 内清空。
2. **进度**：暂停后进度条停在原位；恢复后从正确位置继续（外推，不是从 0 跳）。
3. **能力**：在最后一首时遥控器「下一首」变灰；第一首时「上一首」变灰；
   支持拖动的设备上进度条可拖。这条**必须**由 `mrSupportedCommands` 生效，
   不是靠本地 UI 猜。
4. **音量**：从隧道写一个值，设备实际音量变化；在设备上改音量，`mrp_volume`
   在 5 s 内到达桌面；同时 RTSP `SET_PARAMETER volume:` 路径仍然可用。
5. **隔离**：把 MRP 端口用防火墙阻断，音频继续（放正常的音乐，不是测试音）≥5 分钟，
   会话不结束，`mrp_state` 只发一次 `degraded`（+ 一条 warning），不刷日志。
6. **抓包证据**：SETUP 响应里有第二条 stream；隧道建立后能看到加密帧；用导出的
   会话密钥能按设计解出 protobuf 消息。
7. **不回归**：MRP 关闭（默认）时，HomePod 现有的 `SET_PARAMETER` 元数据与
   事件通道 `mrCommand` 行为逐字节不变（可用现有 Python/native 测试对比）。

### 5. 前置清单

- [ ] 一台 Apple TV（记录型号 + tvOS 版本）与一台 HomePod（记录固件）。
- [ ] 一份**能解出明文**的 MRP 抓包。被动 pcap 只能拿到密文，不够；需要以下
      之一：从发送端 dump 明文（instrumented sender），或同时导出会话密钥。
      抓包记录发送端 OS/版本，否则 schema 无法归因到版本。
- [ ] 抓包场景脚本：开始播放 → 换曲（封面变化）→ 暂停 → 恢复 → 停止（clear）→
      设备端音量 ±1 → 电脑端音量 ±1。缺一个场景就缺一类消息。
- [ ] 完整的 `/info` 响应与 SETUP/SETUP 响应 plist（含 DataStream 项、端口、密钥）。
- [ ] 明文解包工具链；密钥用后即销毁，**不得**写进仓库、日志或诊断导出。
- [ ] 一批封面样本（不同尺寸/格式/文件大小）与设备接受范围的记录。
- [ ] 依赖与许可审查：protobuf 运行时、schema 的来源与许可。
- [ ] 一个「无 MRP 也应正常」的基线：现有 HomePod 路径的元数据/遥控行为录制。

拿到抓包后的第一步：先把 schema 写成 `.proto`（只写结构、不写业务），用离线样本
验证解码；再拿一个最小的 `publish_playback_state` 打通连接。**不要**先写业务层。

---

## P3-5 经典 RAOP / 初代 AirPlay

### 1. 做成之后用户看到什么

老的 AirPort Express、只支持 AirPlay 1 的第三方功放/音箱也能被 AirFlash 发现并
出声；暂停、恢复、停止、音量、进度这些控制可用；老设备自带的屏幕上能显示歌名和
歌手（走 DACP）。用户感到的是「这台老设备也能用」，而不是「列表里看得见、点下去
连不上」。

### 2. 阻塞边界：控制面公开，FairPlay 黑盒

这一项最容易被笼统地说成「被 FairPlay 阻塞」。按公开资料拆开看，边界是这样的：

| 部分 | 状态 | 说明 |
| --- | --- | --- |
| RTSP 方法集 `OPTIONS`/`ANNOUNCE`/`SETUP`/`RECORD`/`SET_PARAMETER`/`FLUSH`/`TEARDOWN`/`GET_PARAMETER` | 〔公开〕 | 非官方 AirPlay 规范与多个公开实现一致；与 AirPlay 2 的差别是顺序与参数，不是方法名 |
| SDP：`m=audio 0 RTP/AVP 96`、`a=rtpmap:96 AppleLossless`、`a=fmtp:96 352 0 16 40 10 14 2 255 0 0 44100` | 〔公开〕 | 352 帧/包、16 bit、44.1 kHz；`fmtp` 的十进制串就是 ALAC magic cookie |
| `a=rsaaeskey` + `a=aesiv` | 〔公开〕 | 128-bit AES key 用接收端 RSA 公钥做 OAEP 包装；AirPort Express 的私钥 2011 年已公开，公钥可用于发送端 |
| `Apple-Challenge` / `Apple-Response` | 〔公开〕 | challenge 16 字节；发送端**是否必须**校验 response〔需真机〕 |
| AES-CBC 加密的 RTP | 〔公开〕 | 与 AirPlay 2 的 ChaCha20-Poly1305 完全不同，不能复用 `crypto::Cipher` |
| 控制通道上的 sync / retransmit | 〔公开〕 | 与 AirPlay 2 的 RTP 内嵌 sync 不同；具体包类型实现时对照公开源码，**本文件不凭记忆写数值** |
| DACP / DMAP：`_dacp._tcp` 注册、`DACP-ID`+`Active-Remote`、`minm/asar/asal` | 〔部分公开〕 | 形状可写；老设备实际发哪些请求、多久拉一次〔需真机〕 |
| ALAC 编码 | 〔公开/已有〕 | 现有 `alac_encoder` + `FRAMES=352` 可复用，不需要新编码器 |
| FairPlay `fp-setup`（SAP）与 `a=fpaeskey` | 〔未知〕 | 没有规范；只有逆向实现，**不能当规范用**，也不能内置其 white-box 表 |
| 目标设备是否强制 FairPlay | 〔需真机〕 | 决定「只做 RSA 路径」是否够；见 `QUESTIONS.md` Q49 |
| `et` 的取值与「SDP 里怎么选 FairPlay」 | 〔需抓包〕 | 不从记忆填数值 |

结论：**RSA 路径公开、FairPlay 黑盒。** P3-5 的「不可重建」只对后者成立；是否
只需要 RSA，取决于目标设备——而这必须先拿到设备看它的 ANNOUNCE/SDP 与响应。
这也是它与 MRP 不同的地方：MRP 缺的是**全部**消息语义，RAOP 缺的只是一段
（虽然关键）密钥交换。

### 3. 接口占位（全部未实现）

**模块**：`native/airflash-engine/src/raop.rs`（新增）+ `raop_crypto.rs`（新增）。

| 职责 | 说明 |
| --- | --- |
| ANNOUNCE + SDP 组装/解析 | `a=rsaaeskey` / `a=aesiv` / `fmtp`；解析回包的 SDP 可选 |
| RSA-OAEP 包装 | 用目标设备公钥包装 128-bit AES key；公钥按型号查表，查不到就明确拒绝 |
| AES-CBC RTP | 与现有 `Packetizer` 分开实现；序号/时间戳规则相同，加密不同 |
| 控制通道 sync / retransmit | 独立于 RTP 的一条 UDP；具体包类型对照公开实现核对 |
| NTP 计时 | 经典 RAOP 没有 PTP、没有 `SETPEERS`；timing 走 NTP，方向与端口在实现时对照公开实现核对 |
| DACP | 引擎监听 HTTP、回 DMAP TLV、复用 `metadata.rs` 的编码器；mDNS 注册由桌面完成（见下） |
| FairPlay 占位 | trait 返回 `Unsupported`，**永不**被默认路径调用 |

DACP 的 mDNS 注册是一个容易漏掉的工程量：现有
`desktop/AirFlash.App/Services/WindowsDnsSdApi.cs` 只有 `Browse` / `Resolve`，
**没有 `Register`**；而 DACP 要求发射端把自己注册成
`iTunes_Ctrl_<DACP-ID>._dacp._tcp`，让接收端来拉。实现时要么补
`DnsServiceRegister` 的 P/Invoke，要么引入一个 mDNS 发布依赖——两者都要单独评估，
不是 `raop.rs` 里顺手写完的事。

**会话选择**：`profile.rs` 的协商增加一个传输族 `classic_raop`（仅在
`SupportsAirPlay2 == false` 且发现到 `_raop._tcp` 时选中）；`session.rs` 在会话
开始时一次性分支，不做成员级混用。现有 `auto|realtime|buffered` 三个取值与语义
不动，`Profile.transport` 只是**追加**一个值。

**IPC**：

| 位置 | 形状 | 说明 |
| --- | --- | --- |
| `Peer.profile.transport` | 追加 `"raop"` | 现有取值不变；`Profile` 是 `deny_unknown_fields`，旧版本引擎收到 `raop` 会拒绝，所以只在两端一起升级的版本里启用 |
| `start` / `probe` | 参数不变 | 不需要新命令；RAOP 是同一 `start` 的另一种传输 |
| 错误码（新） | `raop_fairplay_required`、`raop_unsupported_receiver`、`raop_key_exchange_failed` | 一律带 `host` 与 `channel:"raop"`，可重试性为 false 或 true 要分开 |
| `hello.capabilities` | 实现后才加 `classic_raop` | 未实现不报告 |

**发现层**：`desktop/AirFlash.Core/Receiver.cs` 现在只从 `_raop._tcp` 读 `cn`
（编码列表）。实现时需要追加 `et`（加密类型）并记录 `md`/`sf`；**具体 TXT 键的
取值表在实现时对照公开的 RAOP TXT 说明核对，本文件不凭记忆列值**。

**状态迁移**：

| 状态 | 进入 | 退出 |
| --- | --- | --- |
| `idle` | 选中 classic 设备 | `announced` |
| `announced` | ANNOUNCE 200 | `setup` |
| `setup` | SETUP 200，拿到 data/control/timing 端口 | `recorded` |
| `recorded` | RECORD 200 | `streaming` |
| `streaming` | 首包发出 | `flushed`（FLUSH）/ `stopped`（TEARDOWN） |
| `unsupported` | 设备要求 FairPlay、或公钥缺失 | 终止，报 `raop_fairplay_required` / `raop_unsupported_receiver` |
| `failed` | 任一步非 200 或超时 | 终止，错误码区分「设备拒绝」与「网络」 |

RAOP 没有 PTP、没有 buffered（type 103）、没有 `SETPEERS`；不要在 RAOP 会话里
复用这三段代码路径，也不要把它们「顺便」打开。

### 4. 真机验收（开始动工的门槛）

1. **出声**：AirPort Express 2 上能听到 5 s / 440 Hz / 幅度 0.05 / gain 0.1 的
   测试音（沿用 AGENTS.md 的电平上限），重复 3 次都出声，`underruns` 为 0。
   注意 `scripts/e2e_stream.py` 现在只走 AirPlay 2，让它能对 RAOP 发测试音本身
   就是实现的一部分，不是现成工具。
2. **控制**：暂停 / 恢复 / 停止 / 音量 各一次，设备行为与命令一致；停止后
   没有残留连接（TEARDOWN 后端口可再连）。
3. **音量与进度**：`SET_PARAMETER volume:` 与 `progress:` 在老设备上生效
   （这是 RAOP 的文本参数格式，不是 AirPlay 2 的 plist）。
4. **元数据**：设备屏幕显示 title/artist（DACP 拉取），停止后清空；不显示时
   日志能指出是「没注册 DACP」还是「设备没来拉」。
5. **明确拒绝**：强制 FairPlay 的设备在 1 s 内返回 `raop_fairplay_required`，
   不挂起、不重试、不影响其他会话。
6. **抓包证据**：ANNOUNCE 的 SDP 含 `rsaaeskey`/`aesiv`；SETUP/RECORD 成功；
   RTP 能用我们持有的 key 以 AES-CBC 解出，且解出的 PCM 与发送端一致。
7. **不回归**：同一台 HomePod 仍走现有 AirPlay 2 路径；混组门禁仍拒绝
   classic + AP2（`GroupMembership` 与引擎 `mixed_timing_group` 都不动）。

### 5. 前置清单

- [ ] 一台 AirPort Express 2（记录固件），并核对它的 RSA 公钥（公钥按型号，
      2011 年公开的那把要先确认适用）。
- [ ] 一份 iTunes/Music（macOS）或 iOS 向该设备发送的 ANNOUNCE/SETUP/RECORD 抓包，
      用来对照 SDP 与 timing 参数；没有这份对照，SDP 只能靠公开示例猜版本差异。
- [ ] 一台 AirPlay 1 第三方设备（可选），用来验证不是只为 Apple 一家适配。
- [ ] 一台强制 FairPlay 的设备（只有要走 FairPlay 才需要）；需要先做许可/法务
      评估，逆向实现不是规范。
- [ ] 目标设备的 `_raop._tcp` TXT 原文（含 `et`/`cn`/`md`），以及它是否广播
      `_airplay._tcp`。
- [ ] 测试音合规：只用 `scripts/e2e_stream.py`，≤5 s，gain ≤0.1；AirPort Express
      不是 HomePod，但沿用同一上限。
- [ ] 若要验证 DACP：能抓到**设备发起**的 HTTP 请求（方向是设备来拉，不是我们推）。
- [ ] 一个「连接失败时 UI 说什么」的文案决定：`raop_fairplay_required` 必须
      可翻译（现有 L 机制），不能是英文异常串。

拿到设备后的第一步：先用 macOS/iOS 向目标设备发一次并抓包，确认它走的是
`rsaaeskey` 还是 `fpaeskey`；这一步决定整项是「可做」还是「被 FairPlay 挡住」，
比写任何代码都先。

---

## 三项共同的「不要做」

- 不要为了「看起来完整」给 `recommended_ms` 填默认值，或给 MRP 编码器填猜测的
  消息号——那就是本文档开头说的「看起来完成了」。
- 不要把 `LatencyPolicy` 的现有档位改掉来「顺便」支持标定值。
- 不要把 MRP 的 protobuf 解析写成「先按 plist 试试」——两者编码不同，试不出来。
- 不要把 FairPlay 的逆向实现拷进仓库；需要用时先解决许可，再谈技术。
- 不要在 MRP/RAOP 失败时结束会话：音频路径的健壮性优先级高于任何新功能。

## 相关文档

- [`PROGRESS.md`](PROGRESS.md)：三项的「暂缓」说明与其余条目的进度。
- [`report/10-改造路线图.md`](report/10-改造路线图.md)：§P1-10、§P3-4、§P3-5 的
  原始计划与验收口径。
- [`report/14-控制面-MRP与双向控制.md`](report/14-控制面-MRP与双向控制.md)：
  MRP/Events/DACP 的证据与字段清单。
- [`report/15-设备兼容矩阵.md`](report/15-设备兼容矩阵.md)：设备型号与能力矩阵。
- [`QUESTIONS.md`](QUESTIONS.md)：Q49（RAOP 阻塞边界的拆分）。
