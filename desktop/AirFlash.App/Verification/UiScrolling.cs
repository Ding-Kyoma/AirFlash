using System.Text.Json;
using System.Windows;
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
        var failures = new List<string>();
        var measurements = new List<object>();
        var store = new UiSmoke.MemoryStore();
        var engine = new UiSmoke.MockFactory();
        var discovery = new UiSmoke.MockDiscovery
        {
            Items = Enumerable.Range(1, 6).Select(i => new Receiver($"scroll-{i}", $"Scroll receiver {i}", $"127.0.0.{i}") { Model = "HomePod mini" }).ToArray()
        };
        await using var app = new AppViewModel(store, discovery, new UiSmoke.MockAutostart(), new UiSmoke.MockAudio(), engine, Application.Current.Dispatcher);
        app.Start(); await app.Startup;
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
            var startsBefore = engine.CreatedCount;

            foreach (var dark in new[] { false, true })
            {
                ThemeService.SetForVerification(dark); await Pump();
                foreach (var (width, height, size) in new[] { (880d, 640d, "default"), (640d, 440d, "minimum") })
                {
                    window.Width = width; window.Height = height; await Pump();
                    for (var pageIndex = 0; pageIndex < window.ViewModel.Pages.Length; pageIndex++)
                    {
                        window.ViewModel.SelectedPage = pageIndex;
                        window.PageScroll.ScrollToTop(); await Pump();
                        var scroll = window.PageScroll;
                        var page = window.PageContainer.Children.OfType<FrameworkElement>().Single(p => p.Visibility == Visibility.Visible);
                        var label = $"scroll {(dark ? "dark" : "light")} {size} page {pageIndex}";
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
            Observe(engine.CreatedCount == startsBefore && app.Snapshot.State == PlaybackState.Idle, "scrolling never starts receiver playback", checks, failures);

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
