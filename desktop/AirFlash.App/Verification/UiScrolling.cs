using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AirFlash.App.Services;
using AirFlash.App.Ui;
using AirFlash.App.ViewModels;
using AirFlash.Core;
namespace AirFlash.App.Verification;

// Uses only in-memory settings and simulated discovery/audio/engine services.
internal static class UiScrolling
{
    public static async Task RunAsync(List<string> checks, string directory, bool interactive = false)
    {
        Directory.CreateDirectory(directory);
        await VerifyPanelAsync(checks, directory);
        var failures = new List<string>();
        var measurements = new List<object>();
        var store = new UiSmoke.MemoryStore();
        var engine = new UiSmoke.MockFactory();
        var discovery = new UiSmoke.MockDiscovery
        {
            Items = Enumerable.Range(1, 6).Select(i => new Receiver($"scroll-{i}", $"Scroll receiver {i}", $"127.0.0.{i}") { Model = "HomePod mini" }).ToArray()
        };
        var audio = new PanelAudio
        {
            Endpoints = Enumerable.Range(1, 24).Select(i => new AudioEndpoint($"endpoint-{i}", $"Synthetic audio endpoint {i}")).ToArray()
        };
        await using var app = new AppViewModel(store, discovery, new UiSmoke.MockAutostart(), audio, engine,
            Application.Current.Dispatcher, applications: new ScrollApplications(), isolatedVerification: true);
        // Finish receiver initialization before measuring scrolling-only settings changes.
        app.SetPanelVisible(true);
        app.Start(); await app.Startup; await Pump();
        app.SetPanelVisible(false);
        var window = new SettingsWindow(app); window.Show(); await Pump();
        try
        {
            // Keep an edited curve so accidental slider changes and persistence are observable.
            window.ViewModel.Equalizer.Enabled = true;
            window.ViewModel.Equalizer.Bands[0].Gain = 7.5;
            await Pump();
            var draftBefore = Fingerprint(window.ViewModel.Draft);
            var savedBefore = Fingerprint(store.Saved);
            var savesBefore = store.Saves;
            var startsBefore = engine.Commands.Count(c => c.Text("command") == "start");

            foreach (var dark in new[] { false, true })
            {
                ThemeService.SetForVerification(dark); await Pump();
                foreach (var (width, height, size) in new[] { (880d, 640d, "default"), (640d, 440d, "minimum") })
                {
                    window.Width = width; window.Height = height; await Pump();
                    var listLabel = $"settings lists {(dark ? "dark" : "light")} {size}";
                    await VerifyNavigationAsync(window, size == "minimum", listLabel, checks, failures);
                    if (size == "minimum")
                        await VerifyCaptureListsAsync(window, listLabel, checks, failures);
                    for (var pageIndex = 0; pageIndex < window.ViewModel.Pages.Length; pageIndex++)
                    {
                        window.ViewModel.SelectedPage = pageIndex;
                        window.PageScroll.ScrollToTop(); await Pump();
                        var scroll = window.PageScroll;
                        var page = window.PageContainer.Children.OfType<FrameworkElement>().Single(p => p.Visibility == Visibility.Visible);
                        var label = $"scroll {(dark ? "dark" : "light")} {size} page {pageIndex}";
                        if (pageIndex == SettingsViewModel.SoundGlowPage)
                        {
                            var log = UiSmoke.Descendants(page).OfType<TextBox>().Single(t => t.Name == "SoundGlowLog");
                            Observe(log.Height <= 180 && log.VerticalScrollBarVisibility == ScrollBarVisibility.Auto, $"{label}: Sound Glow log keeps a fixed scrolling height", checks, failures);
                        }
                        var overflowing = scroll.ScrollableHeight > 1;
                        var vertical = scroll.Template.FindName("PART_VerticalScrollBar", scroll) as ScrollBar;
                        var contentBounds = Bounds(page, scroll);
                        double? gutter = null;
                        if (overflowing && vertical is { IsVisible: true })
                        {
                            gutter = Bounds(vertical, scroll).Left - contentBounds.Right;
                            Observe(gutter >= 8 - .5, $"{label}: content clears vertical scrollbar by at least 8 DIP (actual {gutter:F2})", checks, failures);
                        }
                        Observe(overflowing ? vertical is { IsVisible: true, ActualWidth: > 0 } : vertical is { IsVisible: false }, $"{label}: scrollbar visibility matches page overflow", checks, failures);
                        if (pageIndex is SettingsViewModel.EqualizerPage or SettingsViewModel.MonitorPage)
                            UiSmoke.Render(window, Path.Combine(directory, $"settings-scroll-{(dark ? "dark" : "light")}-{size}-page-{pageIndex}-top.png"), 1);

                        var referenceWheelOffset = 0d;
                        if (overflowing)
                        {
                            Wheel(page, -120); await Pump();
                            referenceWheelOffset = scroll.VerticalOffset;
                            Observe(scroll.VerticalOffset > 0, $"{label}: ordinary wheel moves page content downward", checks, failures);
                            scroll.ScrollToBottom(); await Pump();
                            var bottom = Bounds(page, scroll).Bottom;
                            Observe(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) <= 1, $"{label}: final scroll position reaches content end", checks, failures);
                            Observe(bottom <= scroll.ActualHeight - scroll.Padding.Bottom + 1 && bottom > 0, $"{label}: final content is inside the viewport", checks, failures);
                            if (pageIndex == SettingsViewModel.MonitorPage)
                                UiSmoke.Render(window, Path.Combine(directory, $"settings-scroll-{(dark ? "dark" : "light")}-{size}-monitor-bottom.png"), 1);
                            Wheel(page, 120); await Pump();
                            Observe(scroll.VerticalOffset < scroll.ScrollableHeight, $"{label}: ordinary wheel returns from page bottom", checks, failures);
                        }
                        else
                        {
                            Wheel(page, -120); await Pump();
                            Observe(scroll.VerticalOffset == 0 && contentBounds.Bottom <= scroll.ActualHeight - scroll.Padding.Bottom + 1, $"{label}: fitting content stays fully accessible", checks, failures);
                        }

                        measurements.Add(new
                        {
                            theme = dark ? "dark" : "light", size, page = pageIndex,
                            window_width = window.ActualWidth, window_height = window.ActualHeight,
                            extent_height = scroll.ExtentHeight, viewport_height = scroll.ViewportHeight,
                            scrollable_height = scroll.ScrollableHeight, gutter_dip = gutter
                        });

                        if (pageIndex == SettingsViewModel.EqualizerPage)
                        {
                            var bands = UiSmoke.Descendants(page).OfType<ScrollViewer>().Single(s => s.Name == "EqualizerBandScroll");
                            bands.ScrollToHorizontalOffset(Math.Min(72, bands.ScrollableWidth)); await Pump();
                            var horizontalBeforeWheel = bands.HorizontalOffset;
                            var curveBeforeWheel = JsonSerializer.Serialize(window.ViewModel.Draft.Equalizer);
                            var slider = UiSmoke.Descendants(bands).OfType<Slider>().First(s => IsHorizontallyVisible(s, bands));
                            var bandLabel = UiSmoke.Descendants(bands).OfType<TextBlock>().First(t => IsHorizontallyVisible(t, bands));
                            foreach (var (source, name) in new (UIElement, string)[] { (bandLabel, "band label"), (slider, "band slider") })
                            {
                                scroll.ScrollToTop(); await Pump();
                                Wheel(source, -120); await Pump();
                                Observe(referenceWheelOffset > 0 && Math.Abs(scroll.VerticalOffset - referenceWheelOffset) <= 1, $"{label}: wheel over {name} scrolls surrounding page exactly once", checks, failures);
                                // Establish a positive offset independently of the downward assertion.
                                scroll.ScrollToVerticalOffset(referenceWheelOffset); await Pump();
                                var beforeUpwardWheel = scroll.VerticalOffset;
                                Wheel(source, 120); await Pump();
                                Observe(beforeUpwardWheel > 0 && scroll.VerticalOffset <= 1, $"{label}: upward wheel over {name} returns surrounding page to top", checks, failures);
                                Observe(Math.Abs(bands.HorizontalOffset - horizontalBeforeWheel) <= 1 && JsonSerializer.Serialize(window.ViewModel.Draft.Equalizer) == curveBeforeWheel, $"{label}: wheel over {name} preserves band offset and gains", checks, failures);
                            }
                            if (size == "minimum")
                            {
                                Observe(bands.ScrollableWidth > 0, $"{label}: frequency bands expose horizontal overflow", checks, failures);
                                bands.ScrollToRightEnd(); await Pump();
                                var lastBand = UiSmoke.Descendants(bands).OfType<Slider>().Last();
                                var lastBounds = Bounds(lastBand, bands);
                                Observe(bands.HorizontalOffset > 0 && lastBounds.Left >= 0 && lastBounds.Right <= bands.ActualWidth + 1, $"{label}: final frequency slider is reachable horizontally", checks, failures);
                                var horizontalBar = bands.Template.FindName("PART_HorizontalScrollBar", bands) as ScrollBar;
                                var bandContent = (ItemsControl)bands.Content;
                                // Scrollbar arrow glyphs are also TextBlocks; select the real frequency label.
                                var lastLabel = UiSmoke.Descendants(bandContent).OfType<TextBlock>().Single(t => t.Text == window.ViewModel.Equalizer.Bands.Last().Label);
                                var horizontalGutter = horizontalBar is { IsVisible: true } ? Bounds(horizontalBar, bands).Top - Bounds(lastLabel, bands).Bottom : double.NaN;
                                Observe(horizontalGutter >= 8 - .5, $"{label}: frequency labels clear horizontal scrollbar by at least 8 DIP (actual {horizontalGutter:F2})", checks, failures);
                                measurements.Add(new
                                {
                                    theme = dark ? "dark" : "light", size, page = pageIndex, kind = "frequency labels",
                                    horizontal_gutter_dip = double.IsFinite(horizontalGutter) ? horizontalGutter : (double?)null,
                                    padding = bands.Padding.ToString(), content_margin = bandContent.Margin.ToString(),
                                    viewer_height = bands.ActualHeight, content_bounds = Coordinates(Bounds(bandContent, bands)),
                                    label_text = lastLabel.Text, label_bounds = Coordinates(Bounds(lastLabel, bands)),
                                    scrollbar_bounds = horizontalBar is null ? null : Coordinates(Bounds(horizontalBar, bands))
                                });
                                scroll.ScrollToBottom(); await Pump();
                                UiSmoke.Render(window, Path.Combine(directory, $"settings-scroll-{(dark ? "dark" : "light")}-{size}-equalizer-bottom.png"), 1);
                                bands.ScrollToLeftEnd(); await Pump();
                            }
                        }
                    }
                }
            }

            if (interactive)
            {
                ThemeService.SetForVerification(false);
                window.Width = 880; window.Height = 640;
                window.ViewModel.SelectedPage = SettingsViewModel.EqualizerPage;
                window.PageScroll.ScrollToTop(); await Pump();
                await Task.Delay(TimeSpan.FromSeconds(60));
            }

            Observe(Fingerprint(window.ViewModel.Draft) == draftBefore, "scrolling preserves the edited equalizer curve and settings draft", checks, failures);
            Observe(Fingerprint(store.Saved) == savedBefore && store.Saves == savesBefore, "scrolling never saves settings", checks, failures);
            Observe(engine.Commands.Count(c => c.Text("command") == "start") == startsBefore && app.Snapshot.State == PlaybackState.Idle, "scrolling never starts receiver playback", checks, failures);

            var report = new
            {
                ok = failures.Count == 0, failures, measurements,
                note = "Mock-only scroll layout and routed wheel verification; interactive mode temporarily exposes the same isolated Settings window. No real receiver or settings store is used."
            };
            File.WriteAllText(Path.Combine(directory, "ui-scrolling-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            if (failures.Count > 0) throw new InvalidOperationException($"Settings scrolling regression: {failures.Count} failed checks; first: {failures[0]}. See ui-scrolling-report.json.");
        }
        finally { window.Close(); }
    }

    private static async Task VerifyNavigationAsync(SettingsWindow window, bool requireOverflow, string label,
        List<string> checks, List<string> failures)
    {
        var navigation = (ListBox)window.FindName("Navigation");
        var scroll = UiSmoke.Descendants(navigation).OfType<ScrollViewer>().Single();
        var bar = ScrollBarFor(scroll);
        var viewport = ViewportFor(scroll);
        scroll.ScrollToTop(); await Pump();
        // At least one case must exercise overflow even when font metrics fit at minimum size.
        var constrain = requireOverflow && scroll.ScrollableHeight == 0;
        if (constrain) { navigation.MaxHeight = 160; await Pump(); }
        try
        {
            if (requireOverflow)
                Observe(scroll.ScrollableHeight > 0, $"{label}: short navigation has a scrollable extent", checks, failures);
            if (scroll.ScrollableHeight > 0)
            {
                var first = (ListBoxItem)navigation.ItemContainerGenerator.ContainerFromIndex(0);
                Observe(bar.IsVisible && Bounds(bar, scroll).Left - Bounds(first, scroll).Right >= 7.5,
                    $"{label}: navigation entries clear the visible scrollbar", checks, failures);
                Wheel(first, -120); await Pump();
                Observe(scroll.VerticalOffset > 0, $"{label}: the wheel scrolls navigation", checks, failures);
            }
            else
                Observe(!bar.IsVisible && Math.Abs(viewport.ActualWidth - scroll.ActualWidth) <= 1,
                    $"{label}: a fitting navigation list uses its entire width", checks, failures);

            navigation.ScrollIntoView(navigation.Items[^1]); await Pump();
            var last = (ListBoxItem)navigation.ItemContainerGenerator.ContainerFromIndex(navigation.Items.Count - 1);
            var bounds = Bounds(last, viewport);
            Observe(bounds.Top >= -.5 && bounds.Bottom <= viewport.ActualHeight + .5,
                $"{label}: the last navigation entry is fully accessible", checks, failures);
        }
        finally
        {
            if (constrain) navigation.ClearValue(FrameworkElement.MaxHeightProperty);
            scroll.ScrollToTop(); await Pump();
        }
    }

    private static async Task VerifyCaptureListsAsync(SettingsWindow window, string label, List<string> checks, List<string> failures)
    {
        var viewModel = window.ViewModel;
        var originalMode = viewModel.Draft.CaptureMode;
        viewModel.SelectedPage = SettingsViewModel.AudioCapturePage;
        try
        {
            foreach (var (mode, name) in new[] { ("application", "ApplicationCombo"), ("endpoint", "EndpointCombo") })
            {
                viewModel.Draft.CaptureMode = mode;
                if (mode == "application") await viewModel.LoadApplicationsAsync();
                else await viewModel.LoadEndpointsAsync(true);
                await Pump();
                var combo = UiSmoke.Descendants(window).OfType<ComboBox>().Single(item => item.Name == name);
                combo.BringIntoView(); await Pump();
                var selected = combo.SelectedItem;
                combo.IsDropDownOpen = true;
                try
                {
                    await Pump();
                    var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
                    var scroll = UiSmoke.Descendants(popup.Child).OfType<ScrollViewer>().Single();
                    var bar = ScrollBarFor(scroll);
                    var viewport = ViewportFor(scroll);
                    var first = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(0);
                    Observe(combo.Items.Count >= 24 && scroll.ScrollableHeight > 0 && bar.IsVisible
                        && Bounds(bar, scroll).Left - Bounds(first, scroll).Right >= 7.5,
                        $"{label}: {mode} rows clear the popup scrollbar", checks, failures);
                    scroll.ScrollToTop(); await Pump();
                    Wheel(first, -120); await Pump();
                    Observe(scroll.VerticalOffset > 0, $"{label}: the wheel scrolls the {mode} dropdown", checks, failures);
                    scroll.ScrollToBottom(); await Pump();
                    var last = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(combo.Items.Count - 1);
                    var bounds = Bounds(last, viewport);
                    Observe(bounds.Top >= -.5 && bounds.Bottom <= viewport.ActualHeight + .5
                        && Equals(combo.SelectedItem, selected),
                        $"{label}: the last {mode} choice is reachable without changing selection", checks, failures);
                }
                finally { combo.IsDropDownOpen = false; await Pump(); }
            }
        }
        finally { viewModel.Draft.CaptureMode = originalMode; }
    }

    private static ScrollBar ScrollBarFor(ScrollViewer scroll)
        => (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);

    private static ScrollContentPresenter ViewportFor(ScrollViewer scroll)
        => (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);

    private static async Task VerifyPanelAsync(List<string> checks, string directory)
    {
        var failures = new List<string>();
        var measurements = new List<object>();
        var store = new UiSmoke.MemoryStore();
        var engine = new UiSmoke.MockFactory();
        var audio = new PanelAudio();
        var receivers = Enumerable.Range(1, 12).Select(i => new Receiver($"panel-scroll-{i}", $"Panel scroll receiver {i}", $"127.0.0.{i}")
            { Model = "HomePod mini" }).ToArray();
        var settings = store.Load();
        settings.Options(receivers[0].Id).LastVolume = 28;
        store.Save(settings);
        var discovery = new UiSmoke.MockDiscovery { Items = receivers };
        await using var app = new AppViewModel(store, discovery, new UiSmoke.MockAutostart(), audio, engine, Application.Current.Dispatcher, isolatedVerification: true);
        app.UseControlledScheduling();
        var requestedDpi = 96;
        var panel = new ControlPanel(app, () => new MonitorArea(new PixelRect(0, 0, 1600, 1200), requestedDpi / 96d))
            { SizeToContent = SizeToContent.Manual, Height = 600, MenuOpen = true };
        try
        {
            app.Start(); await app.Startup;
            panel.ShowPanel(); await Pump();
            var savesBefore = store.Saves;
            var commandsBefore = engine.Commands.Count;
            var mutesBefore = audio.Mutes; var restoresBefore = audio.Restores;
            var savedBefore = Fingerprint(store.Saved);
            foreach (var dark in new[] { false, true })
            {
                ThemeService.SetForVerification(dark); await Pump();
                foreach (var dpi in new[] { 96, 120 })
                {
                    requestedDpi = dpi;
                    await UseDpiAsync(panel, dpi);
                    var width = ControlPanel.CompactWidth;
                    panel.Height = Math.Min(600, panel.MaxHeight);
                    await ReceiversAsync(app, discovery, receivers);
                    var scroll = panel.DeviceScroll;
                    app.ShowError(new IOException($"Synthetic discovery warning for receiver scrolling {dark} {width:F0} {dpi}."));
                    await Pump();
                    var details = UiSmoke.Descendants(panel).OfType<Expander>().Single();
                    details.IsExpanded = true; await Pump();
                    scroll.ScrollToTop(); await Pump();
                    var label = $"receiver scroll {(dark ? "dark" : "light")} {width:F0} DIP {dpi} DPI";
                    var finalDpi = VisualTreeHelper.GetDpi(panel);
                    Observe(Math.Abs(panel.ActualWidth - width) <= 1 && finalDpi.DpiScaleX == dpi / 96d && finalDpi.DpiScaleY == dpi / 96d,
                        $"{label}: final logical width and window DPI match the requested geometry", checks, failures);
                    Observe(panel.NoticeBanner.IsVisible && details.IsVisible && details.IsExpanded,
                        $"{label}: receiver scrolling remains usable with a notice and expanded error details", checks, failures);
                    var vertical = scroll.Template.FindName("PART_VerticalScrollBar", scroll) as ScrollBar
                        ?? throw new InvalidOperationException("The receiver vertical scrollbar is unavailable.");
                    var viewport = scroll.Template.FindName("PART_ScrollContentPresenter", scroll) as ScrollContentPresenter
                        ?? throw new InvalidOperationException("The receiver scroll viewport is unavailable.");
                    var cards = ReceiverCards(scroll, app).ToArray();
                    var barBounds = Bounds(vertical, scroll);
                    var gutters = cards.Select(card => barBounds.Left - Bounds(card.Card, scroll).Right).ToArray();
                    var minimumGutter = gutters.DefaultIfEmpty(double.NaN).Min();
                    var controlsFit = cards.All(card =>
                        VisibleControls(card.Card).OfType<Button>().Any(button => ReferenceEquals(button.Command, card.Row.PlaybackCommand))
                            && ControlsClearScrollbar(card.Card, scroll, barBounds));
                    Observe(cards.Length == receivers.Length && scroll.ScrollableHeight > 1
                        && vertical.IsVisible && vertical.ActualWidth > 0 && panel.DeviceScrollGutter.IsVisible, $"{label}: all receiver cards render with a visible overflow scrollbar", checks, failures);
                    Observe(minimumGutter >= 8 - .5, $"{label}: every receiver card clears the actual scrollbar by at least 8 DIP (minimum {minimumGutter:F2})", checks, failures);
                    Observe(controlsFit, $"{label}: disconnected playback controls stay inside their cards and clear the scrollbar", checks, failures);
                    Observe(cards.All(card => UiSmoke.Descendants(card.Card).OfType<StackPanel>()
                        .Single(group => group.Name == "ReceiverVolumeControls").Visibility == Visibility.Collapsed
                        && !VisibleControls(card.Card).Any(control => control is Slider || control is Button button
                            && ReferenceEquals(button.Command, card.Row.MuteCommand))),
                        $"{label}: disconnected cards collapse their mute and volume controls", checks, failures);
                    Observe(scroll.ScrollableWidth == 0, $"{label}: receiver controls need no horizontal scrolling", checks, failures);
                    UiSmoke.Render(panel, Path.Combine(directory, $"receiver-scroll-{(dark ? "dark" : "light")}-{width:F0}-{dpi}-top.png"), dpi / 96d);

                    Wheel(cards[0].Card, -120); await Pump();
                    Observe(scroll.VerticalOffset > 0, $"{label}: ordinary wheel over a card scrolls receivers downward", checks, failures);
                    scroll.ScrollToBottom(); await Pump();
                    var last = cards[^1]; var lastBounds = Bounds(last.Card, viewport);
                    var play = UiSmoke.Descendants(last.Card).OfType<Button>().Single(button => ReferenceEquals(button.Command, last.Row.PlaybackCommand));
                    panel.Activate();
                    Observe(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) <= 1,
                        $"{label}: final scroll position reaches the last receiver", checks, failures);
                    Observe(lastBounds.Top >= -.5 && lastBounds.Bottom <= viewport.ActualHeight + .5,
                        $"{label}: the last receiver card is fully inside the viewport", checks, failures);
                    Observe(play.IsVisible && play.IsEnabled && play.Command!.CanExecute(play.CommandParameter)
                        && play.Focus() && play.IsKeyboardFocused,
                        $"{label}: the final receiver Play control remains accessible by keyboard", checks, failures);
                    UiSmoke.Render(panel, Path.Combine(directory, $"receiver-scroll-{(dark ? "dark" : "light")}-{width:F0}-{dpi}-bottom.png"), dpi / 96d);
                    measurements.Add(new
                    {
                        theme = dark ? "dark" : "light", requested_width = width, requested_dpi = dpi,
                        actual_width = panel.ActualWidth, actual_height = panel.ActualHeight,
                        actual_dpi_x = VisualTreeHelper.GetDpi(panel).DpiScaleX,
                        actual_dpi_y = VisualTreeHelper.GetDpi(panel).DpiScaleY,
                        scrollbar_bounds = Coordinates(barBounds), minimum_card_gutter_dip = minimumGutter,
                        controls_fit = controlsFit, viewport_height = viewport.ActualHeight,
                        scrollable_height = scroll.ScrollableHeight, final_offset = scroll.VerticalOffset,
                        last_card_bounds = Coordinates(lastBounds)
                    });

                    await ReceiversAsync(app, discovery, [receivers[^1]]);
                    scroll.ScrollToTop(); await Pump();
                    var fitting = ReceiverCards(scroll, app).Single();
                    var fittingBounds = Bounds(fitting.Card, viewport);
                    Observe(scroll.ScrollableHeight <= 1 && !vertical.IsVisible && panel.DeviceScrollGutter.Visibility == Visibility.Collapsed,
                        $"{label}: a fitting receiver list hides its vertical scrollbar and releases the gutter", checks, failures);
                    Observe(fittingBounds.Left >= -.5 && fittingBounds.Top >= -.5
                        && Math.Abs(fittingBounds.Right - viewport.ActualWidth) <= .5 && fittingBounds.Bottom <= viewport.ActualHeight + .5,
                        $"{label}: a fitting receiver card stays fully accessible", checks, failures);
                    Wheel(fitting.Card, -120); await Pump();
                    Observe(scroll.VerticalOffset == 0, $"{label}: wheel leaves a fitting receiver list at its accessible origin", checks, failures);
                }
            }
            Observe(store.Saves == savesBefore && Fingerprint(store.Saved) == savedBefore,
                "receiver scrolling, resizing and synthetic DPI changes never save settings", checks, failures);
            Observe(engine.Commands.Count == commandsBefore && audio.Mutes == mutesBefore && audio.Restores == restoresBefore
                && app.Snapshot.State == PlaybackState.Idle,
                "receiver scrolling never starts playback or mutates local audio", checks, failures);

            await ReceiversAsync(app, discovery, receivers);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void ConfirmVolume(SessionSnapshot snapshot)
            {
                if (snapshot.State == PlaybackState.Streaming && snapshot.Receiver?.Id == receivers[0].Id
                    && snapshot.DeviceVolume?.Available == true) ready.TrySetResult();
            }
            app.Session.Changed += ConfirmVolume;
            try
            {
                await app.Session.StartAsync(receivers[0], settings);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Pump();
                var activeCommands = engine.Commands.Count;
                var activeRestores = audio.Restores;
                foreach (var dark in new[] { false, true })
                {
                    ThemeService.SetForVerification(dark); await Pump();
                    foreach (var dpi in new[] { 96, 120 })
                    {
                        requestedDpi = dpi;
                        await UseDpiAsync(panel, dpi);
                        var scroll = panel.DeviceScroll;
                        scroll.ScrollToTop(); await Pump();
                        var card = ReceiverCards(scroll, app).Single(entry => entry.Row.Receiver.Id == receivers[0].Id);
                        var vertical = ScrollBarFor(scroll);
                        var barBounds = Bounds(vertical, scroll);
                        var controls = VisibleControls(card.Card).ToArray();
                        var slider = controls.OfType<Slider>().Single();
                        var label = $"active receiver scroll {(dark ? "dark" : "light")} {ControlPanel.CompactWidth:F0} DIP {dpi} DPI";
                        Observe(vertical.IsVisible && scroll.ScrollableHeight > 1 && scroll.ScrollableWidth == 0
                            && controls.OfType<Button>().Any(button => ReferenceEquals(button.Command, app.StopCommand) && button.IsEnabled)
                            && controls.OfType<Button>().Any(button => ReferenceEquals(button.Command, card.Row.MuteCommand) && button.IsEnabled)
                            && slider.IsEnabled && slider.ActualWidth >= 60 - .5 && ControlsClearScrollbar(card.Card, scroll, barBounds),
                            $"{label}: streaming Stop, mute and volume stay inside the card and clear the scrollbar", checks, failures);
                        var row = (Grid)slider.Parent;
                        var percentage = row.Children.OfType<TextBlock>().Single(text => Grid.GetColumn(text) == 2);
                        var track = Bounds(slider, row);
                        Observe(Grid.GetColumn(slider) == 1 && Math.Abs(row.ColumnDefinitions[0].Width.Value - 22) <= .1
                            && Math.Abs(track.Left - row.ColumnDefinitions[0].ActualWidth) <= 1
                            && Math.Abs(slider.ActualWidth - row.ColumnDefinitions[1].ActualWidth) <= 1
                            && Bounds(percentage, row).Left >= track.Right - .5,
                            $"{label}: the active volume track fills the column between the status slot and percentage", checks, failures);
                        var master = UiSmoke.Descendants(panel).OfType<Slider>()
                            .Single(candidate => candidate.IsVisible && AutomationProperties.GetName(candidate) == L.Get("Master volume"));
                        var masterBounds = Bounds(master, panel);
                        var activeBounds = Bounds(slider, panel);
                        Observe(Math.Abs(masterBounds.Left - activeBounds.Left) <= 1 && Math.Abs(masterBounds.Right - activeBounds.Right) <= 1,
                            $"{label}: master and receiver volume tracks align during overflow", checks, failures);
                        UiSmoke.Render(panel, Path.Combine(directory, $"receiver-scroll-active-{(dark ? "dark" : "light")}-{dpi}.png"), dpi / 96d);
                        var volume = slider.Value;
                        Wheel(slider, -120); await Pump();
                        Observe(scroll.VerticalOffset > 0 && slider.Value == volume,
                            $"{label}: wheel over the active slider scrolls receivers without changing volume", checks, failures);
                    }
                }
                Observe(engine.Commands.Count == activeCommands && store.Saves == savesBefore && Fingerprint(store.Saved) == savedBefore
                    && audio.Mutes == mutesBefore && audio.Restores == activeRestores && app.Snapshot.State == PlaybackState.Streaming,
                    "receiver scrolling during simulated playback sends no commands and saves no settings", checks, failures);
            }
            finally
            {
                app.Session.Changed -= ConfirmVolume;
                await app.Session.StopAsync();
            }
            File.WriteAllText(Path.Combine(directory, "ui-panel-scrolling-report.json"), JsonSerializer.Serialize(new
            {
                ok = failures.Count == 0, failures, measurements,
                note = "Actual mock ControlPanel geometry, notice/error details, scroll input and keyboard access at 400 DIP and synthetic 96/120 DPI with an injected work area. No physical display qualification."
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (failures.Count > 0) throw new InvalidOperationException($"Receiver scrolling regression: {failures.Count} failed checks; first: {failures[0]}. See ui-panel-scrolling-report.json.");
        }
        finally { panel.ShutdownPanel(); }
    }

    private static IEnumerable<FrameworkElement> VisibleControls(Border card)
        => UiSmoke.Descendants(card).OfType<FrameworkElement>().Where(element => element.IsVisible && element is Button or Slider);

    private static bool ControlsClearScrollbar(Border card, ScrollViewer scroll, Rect barBounds)
    {
        var cardBounds = Bounds(card, scroll);
        return VisibleControls(card).All(control =>
        {
            var bounds = Bounds(control, scroll);
            return control.ActualWidth > 0 && control.ActualHeight > 0
                && bounds.Left >= cardBounds.Left - .5 && bounds.Right <= cardBounds.Right + .5
                && bounds.Right <= barBounds.Left - 8 + .5;
        });
    }

    private static IEnumerable<(Border Card, ReceiverViewModel Row)> ReceiverCards(ScrollViewer scroll, AppViewModel app)
    {
        var items = UiSmoke.Descendants(scroll).OfType<ItemsControl>().Single(item => ReferenceEquals(item.ItemsSource, app.Receivers));
        foreach (var row in app.Receivers)
        {
            var container = items.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement
                ?? throw new InvalidOperationException("A receiver item container is unavailable.");
            yield return (UiSmoke.Descendants(container).OfType<Border>().First(), row);
        }
    }

    private static async Task ReceiversAsync(AppViewModel app, UiSmoke.MockDiscovery discovery, Receiver[] receivers)
    {
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
        {
            if (app.Receivers.Select(row => row.Receiver.Id).ToHashSet(StringComparer.Ordinal)
                .SetEquals(receivers.Select(receiver => receiver.Id))) published.TrySetResult();
        }
        app.Receivers.CollectionChanged += Changed;
        try
        {
            discovery.Items = receivers; discovery.Publish();
            Changed(null, new(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
            await published.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Pump();
        }
        finally { app.Receivers.CollectionChanged -= Changed; }
    }

    // Notify only the isolated fake-services window, preserving its logical size.
    // This neither changes Windows display preferences nor messages another app.
    private static async Task UseDpiAsync(Window window, int dpi)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var bounds))
            throw new InvalidOperationException("The synthetic receiver panel is unavailable.");
        var previous = VisualTreeHelper.GetDpi(window);
        bounds.Right = bounds.Left + (int)Math.Round((bounds.Right - bounds.Left) * dpi / 96d / previous.DpiScaleX);
        bounds.Bottom = bounds.Top + (int)Math.Round((bounds.Bottom - bounds.Top) * dpi / 96d / previous.DpiScaleY);
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRect>());
        try
        {
            Marshal.StructureToPtr(bounds, memory, false);
            SendMessage(handle, 0x02E0 /* WM_DPICHANGED */, new IntPtr((dpi << 16) | dpi), memory);
        }
        finally { Marshal.FreeHGlobal(memory); }
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render); await Pump();
        var observed = VisualTreeHelper.GetDpi(window);
        if (observed.DpiScaleX != dpi / 96d || observed.DpiScaleY != dpi / 96d)
            throw new InvalidOperationException($"Synthetic {dpi} DPI was not adopted: {observed.DpiScaleX}, {observed.DpiScaleY}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);

    private sealed class ScrollApplications : IApplicationCatalog
    {
        public bool Supported => true;
        public Task<IReadOnlyList<CaptureApplication>> RefreshAsync(CancellationToken cancellation = default)
            => Task.FromResult<IReadOnlyList<CaptureApplication>>(Enumerable.Range(1, 24)
                .Select(i => new CaptureApplication($"synthetic-{i}.exe", $"Synthetic capture application {i}", (uint)i, 1)).ToArray());
    }

    private sealed class PanelAudio : IAudioService
    {
        public int Mutes { get; private set; }
        public int Restores { get; private set; }
        public event Action? EndpointsChanged { add { } remove { } }
        public IReadOnlyList<AudioEndpoint> Endpoints { get; init; } = [new("panel-endpoint", "Panel endpoint")];
        public Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync() => Task.FromResult(Endpoints);
        public Task<string?> GetDefaultEndpointIdAsync() => Task.FromResult<string?>("panel-endpoint");
        public Task MuteAsync(string? endpointId) { Mutes++; return Task.CompletedTask; }
        public Task RestoreAsync() { Restores++; return Task.CompletedTask; }
    }

    private static Rect Bounds(FrameworkElement element, Visual ancestor)
        => element.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static object Coordinates(Rect rect) => new { left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom };

    private static bool IsHorizontallyVisible(FrameworkElement element, ScrollViewer scroll)
    {
        var bounds = Bounds(element, scroll);
        return bounds.Left >= 0 && bounds.Right <= scroll.ActualWidth;
    }

    private static void Wheel(UIElement source, int delta)
    {
        // Preview and bubbling input share handled state, as real WPF mouse input does.
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        source.RaiseEvent(args);
        args.RoutedEvent = Mouse.MouseWheelEvent;
        source.RaiseEvent(args);
    }

    private static string Fingerprint(AppSettings settings) => JsonSerializer.Serialize(settings);
    private static void Observe(bool value, string name, List<string> checks, List<string> failures)
    {
        if (value) checks.Add(name); else failures.Add(name);
    }
    private static async Task Pump()
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(40);
    }
}
