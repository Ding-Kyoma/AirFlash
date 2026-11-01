# 主面板延迟入口计划（已归档，v0.5.4 已发布）

归档日期：2026-11-01。来源：原本地 `.zcode/plans/` 计划。

本计划中的自定义延迟数值显示和延迟设置入口已经在 v0.5.4 实现并发布，
参见 [该版本发布说明](https://github.com/Ding-Kyoma/AirFlash/blob/v0.5.4/docs/RELEASE-NOTES.md)。
以下保留原实现设计作为历史记录；当前主面板还包含后续新增的自适应模式，不能据此重新覆盖现有实现。
验证命令和产物位置已按当前开发构建流程更新，避免继续引用已移除的 `scripts/build.ps1` 或旧 MSI 文件名。

---

## 目标

1. 主面板“全局延迟模式”下拉框中，“自定义”一项显示当前配置的延迟值，例如 `自定义 · 1000 ms`。
2. 下拉框右侧新增一个小图标按钮，点击直接打开设置中的延迟页面：
   - 正常情况下进入“AirFlash streaming”（第 1 页，延迟卡片在顶部）。
   - 当当前设备使用接收器独立延迟、全局下拉被禁用时，进入“接收器”（第 4 页），与现有提示 `Settings → Receivers` 保持一致。

## 实现方案

### 1. 动态“自定义”标签

**`desktop/AirFlash.App/ViewModels/Commands.cs`**

- 新增 `LatenciesFor(int? customMs)`，保留 `Latencies` 属性作为无自定义值的默认版本：
  - `realtime` / `normal` / `buffered` 标签不变。
  - `custom` 使用 `L.Format("Custom · {0} ms", customMs)`，无值时仍为 `L.Get("Custom")`。
- `ReceiverLatencies` 继续使用默认版本，接收器设置页行为不变。

**`desktop/AirFlash.App/ViewModels/AppViewModel.cs`**

- 新增 `public ObservableCollection<StreamChoice> LatencyChoices { get; } = [];`
- 新增 `internal void RefreshLatencyChoices() => StreamChoice.Update(LatencyChoices, Choices.LatenciesFor(_settings.CustomBufferMs));`
- 复用现有 `StreamChoice.Update` 原地更新标签，不替换 `ItemsSource`，避免 ComboBox 选择丢失。
- 调用时机：
  - 构造函数加载设置后。
  - `ApplyAsync` 合并设置成功后。
  - `ResetAsync` 恢复默认设置后。
  - 语言切换重建窗口时（由 `App.xaml.cs` 调用）。

### 2. 主面板下拉框与跳转按钮

**`desktop/AirFlash.App/Ui/ControlPanel.xaml`**

- 下拉框 `ItemsSource` 从 `{x:Static vm:Choices.Latencies}` 改为 `{Binding LatencyChoices}`，并移除不再使用的 `xmlns:vm`。
- 在延迟行增加第三列，放置一个 34×34 的 `IconButton`，位于下拉框右侧。
- 新增 `LatencyIconGeometry`（滑杆图标），按钮使用现有 `IconButton` 样式。
- 按钮属性：
  - `ToolTip="{loc:Text 'Latency settings'}"`
  - `AutomationProperties.Name="{loc:Text 'Latency settings'}"`
  - `Click="OnLatencySettings"`

**`desktop/AirFlash.App/Ui/ControlPanel.xaml.cs`**

- 新增 `public event Action<int>? LatencySettingsRequested;`
- 点击处理：
  - 可编辑全局延迟时传 `SettingsViewModel.StreamingPage`。
  - 否则传 `SettingsViewModel.ReceiversPage`。

### 3. 设置窗口导航

**`desktop/AirFlash.App/ViewModels/SettingsViewModel.cs`**

- 在现有页面常量中加入 `StreamingPage = 1`。

**`desktop/AirFlash.App/App.xaml.cs`**

- 两处创建 `ControlPanel` 的地方都订阅 `LatencySettingsRequested`，连接到新的页面导航方法。
- 增加重载：
  - `ShowSettings()` 保持原有行为。
  - `ShowSettings(int? page)`：打开或激活设置窗口后设置 `ViewModel.SelectedPage`。
- 语言切换重建窗口时，在 `L.Initialize` 后调用 `_viewModel.RefreshLatencyChoices()`，确保标签随之切换语言。

### 4. 本地化

在以下三个文件加入两个新键：

- `desktop/AirFlash.Core/Strings.zh.json`
- `desktop/AirFlash.Core/Strings.zh-TW.json`
- `desktop/AirFlash.Core/Strings.de.json`

新增键：

- `"Custom · {0} ms"`：`自定义 · {0} ms` / `自訂 · {0} ms` / `Benutzerdefiniert · {0} ms`
- `"Latency settings"`：`延迟设置` / `延遲設定` / `Latenzeinstellungen`

保持占位符数量一致，满足 `LocalizationTests` 的键集与格式参数校验。

### 5. 验证与回归测试

**`desktop/AirFlash.App/Verification/UiSmoke.cs`**

- 断言主面板“自定义”项标签等于 `L.Format("Custom · {0} ms", app.Settings.CustomBufferMs)`。
- 断言新按钮可聚焦、具有正确 ToolTip，并在空闲时请求 `SettingsViewModel.StreamingPage`。
- 在设置窗口应用 `CustomBufferMs = 777` 后，断言下拉框中的自定义标签原地更新为 `Custom · 777 ms`。

**`desktop/AirFlash.App/Verification/ReceiverSettingsRegression.cs`**

- 在全局延迟被接收器独立延迟禁用时，断言按钮请求 `SettingsViewModel.ReceiversPage`。

## 验证步骤

1. 在开发分支运行：
   - `pwsh scripts/dotnet.ps1 test AirFlash.sln`
   - `uv run pytest`
   - `uv run ruff check .`
2. 检查 `git status` / `git diff`，只提交本次相关文件，创建简洁 commit。
3. 合并到 `D:\win2airplay4homepod` 的 `main`，确认工作区干净。
4. 在干净的 main 运行 `pwsh scripts/build-dev.ps1`，确认 `dist/dev/AirFlash.exe` 和 `dist/dev/AirFlash-<基线版本>-dev.<提交>.msi` 已更新。
5. 运行构建产物 UI 验证：
   - `dist\dev\AirFlash.exe --ui-smoke --ui-language zh-CN --output artifacts\latency-ui-smoke\report.json`
   - `dist\dev\AirFlash.exe --ui-smoke --receiver-settings-regression --ui-language zh-CN --output artifacts\latency-receiver-smoke\report.json`

原计划的实施范围只包括主面板的全局延迟下拉；接收器设置页中的自定义延迟输入框保持原样。