// SPDX-License-Identifier: AGPL-3.0-only
// Added 2026-11-12 for the Windows island.
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SapphireGameHud;

public partial class IslandPanel : UserControl, IDisposable
{
    private enum View { Music, Shelf, Clipboard, Timer }

    private sealed record ShelfItem(string Path)
    {
        public string Label => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path));
    }

    private sealed record ClipboardEntry(string Text)
    {
        public string Label => Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private readonly ObservableCollection<ShelfItem> shelf = [];
    private readonly ObservableCollection<ClipboardEntry> clipboard = [];
    private readonly DispatcherTimer seekDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly Action<bool> listenToClipboard;
    private readonly WindowsMedia media;
    private View view;
    private MediaReading reading = MediaReading.Empty;
    private long receivedAt;
    private (TrackDirection Direction, long Expires)? trackIntent;
    private long seekIntentUntil;
    private readonly ScaleTransform artworkScale = new(1, 1);
    private bool updatingSeek;
    private bool seeking;
    private Point? fileDragOrigin;
    private bool fileDragActive;
    private bool pickerOpen;
    private double remaining = 25 * 60;
    private double selectedDuration = 25 * 60;
    private long timerEndsAt;
    private bool timerCompleted;
    private long copyFeedbackUntil;
    private int mediaCommandVersion;

    internal IslandPanel(Action<bool> listenToClipboard, Action<MediaReading, PlaybackChange> compactMedia)
    {
        this.listenToClipboard = listenToClipboard;
        InitializeComponent();
        AlbumArt.RenderTransformOrigin = new Point(.5, .5);
        AlbumArt.RenderTransform = artworkScale;
        Files.ItemsSource = shelf;
        Clips.ItemsSource = clipboard;
        Files.SelectionChanged += (_, _) => RemoveFile.IsEnabled = Files.SelectedItem is ShelfItem;
        Clips.SelectionChanged += (_, _) => CopyClip.IsEnabled = Clips.SelectedItem is ClipboardEntry;
        Loaded += (_, _) => UpdateSelection(false);
        PanelBody.SizeChanged += (_, _) => ViewChanged?.Invoke();
        seekDebounce.Tick += async (_, _) =>
        {
            if (Mouse.LeftButton == MouseButtonState.Pressed && Seek.IsMouseCaptureWithin) return;
            await CommitSeek();
        };
        media = new WindowsMedia(value => _ = Dispatcher.InvokeAsync(() =>
        {
            compactMedia(value, UpdateMedia(value));
        }));
    }

    internal bool HoldsOpen => fileDragActive || pickerOpen || Seek.IsMouseCaptureWithin;

    internal event Action? ViewChanged;

    internal double DesiredPanelHeight
    {
        get
        {
            // Measure the control as a whole. Measuring only its child leaves the
            // parent's desired size stale after navigation and can clip actions.
            Measure(new Size(Width, double.PositiveInfinity));
            // WPF already caches valid measurements. A separate cached height can
            // outlive deferred template realization, font changes, or list updates.
            return Math.Ceiling(DesiredSize.Height);
        }
    }

    internal void ResizeTo(double width)
    {
        if (Width == width) return;
        Width = width;
        OnViewChanged();
    }

    internal void FocusControls()
    {
        var firstControl = view switch
        {
            View.Shelf => AddFile,
            View.Clipboard => (Control)ClipboardEnabled,
            View.Timer => TimerButton,
            _ when Toggle.IsEnabled => Toggle,
            _ when Navigation.Visibility == Visibility.Visible => MusicTab,
            _ => this
        };
        firstControl.Focus();
    }

    internal void SelectTimer() => ShowView(View.Timer);
    internal void SelectMusic() => ShowView(View.Music);

    internal void SetGameMusicMode(bool enabled)
    {
        if (enabled) SelectMusic();
        Navigation.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        ViewHost.MinHeight = enabled ? 0 : 164;
        OnViewChanged();
    }

    private void NavigationSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSelection(false);

    private void UpdateSelection(bool animate)
    {
        if (SelectionIndicator is null || Navigation.ActualWidth <= 0) return;
        var slot = Navigation.ActualWidth / 4;
        SelectionIndicator.Width = Math.Max(0, slot - 4);
        PlaybackMotion.EaseTo(SelectionOffset, TranslateTransform.XProperty, (int)view * slot, 220,
            animate && IsVisible && SystemParameters.ClientAreaAnimation);
    }

    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (IsInitialized && sender is RadioButton { Tag: string name } && Enum.TryParse<View>(name, out var destination))
            ShowView(destination);
    }

    private void ShowView(View destination, bool moveFocus = false)
    {
        if (view == destination) return;
        var direction = Math.Sign(destination - view);
        view = destination;
        MusicView.Visibility = ViewVisibility(View.Music);
        ShelfView.Visibility = ViewVisibility(View.Shelf);
        ClipboardView.Visibility = ViewVisibility(View.Clipboard);
        TimerView.Visibility = ViewVisibility(View.Timer);
        MusicTab.IsChecked = view == View.Music;
        ShelfTab.IsChecked = view == View.Shelf;
        ClipboardTab.IsChecked = view == View.Clipboard;
        TimerTab.IsChecked = view == View.Timer;
        UpdateSelection(true);
        var incoming = view switch
        {
            View.Shelf => ShelfView, View.Clipboard => ClipboardView, View.Timer => TimerView, _ => MusicView
        };
        PlaybackMotion.Arrive(incoming, direction * 5, 170, .65);
        OnViewChanged();
        if (moveFocus) FocusControls();
    }

    private void OnViewChanged()
    {
        InvalidateMeasure();
        ViewChanged?.Invoke();
    }

    private Visibility ViewVisibility(View target) => view == target ? Visibility.Visible : Visibility.Collapsed;

    internal PlaybackChange UpdateMedia(MediaReading value)
    {
        var now = Environment.TickCount64;
        var direction = trackIntent is { } intent && now <= intent.Expires ? intent.Direction : TrackDirection.None;
        var change = PlaybackMotion.Observe(reading, value, (now - receivedAt) / 1000d, direction,
            seeking || now <= seekIntentUntil || seekDebounce.IsEnabled || Seek.IsMouseCaptureWithin);
        if (PlaybackMotion.ReplacesTrack(change))
        {
            trackIntent = null;
            mediaCommandVersion++;
        }
        var oldHeightState = (reading.HasTrack, reading.Duration > 0, ArtistName.Visibility, MusicStatus.Text);
        if (PlaybackMotion.ReplacesTrack(change) || change is PlaybackChange.Restarted or PlaybackChange.Resumed)
        {
            seekDebounce.Stop();
            MusicStatus.Text = "";
        }
        reading = value;
        receivedAt = now;
        TrackName.Text = value.HasTrack ? value.Title : "Nothing playing";
        ArtistName.Text = value.HasTrack ? value.Artist : "Start playback in a media app.";
        ArtistName.Visibility = string.IsNullOrEmpty(ArtistName.Text) ? Visibility.Collapsed : Visibility.Visible;
        TrackName.ToolTip = value.HasTrack ? value.Title : null;
        ArtistName.ToolTip = string.IsNullOrEmpty(value.Artist) ? null : value.Artist;
        AlbumArt.Visibility = value.Artwork is null ? Visibility.Collapsed : Visibility.Visible;
        if ((AlbumArt.Fill as ImageBrush)?.ImageSource != value.Artwork)
            AlbumArt.Fill = value.Artwork is { } art ? new ImageBrush(art) { Stretch = Stretch.UniformToFill } : null;
        Previous.IsEnabled = value.CanPrevious;
        Toggle.IsEnabled = value.CanToggle;
        Next.IsEnabled = value.CanNext;
        Transport.Visibility = value.HasTrack ? Visibility.Visible : Visibility.Collapsed;
        Seek.IsEnabled = value.CanSeek;
        Timeline.Visibility = value.HasTrack && value.Duration > 0 ? Visibility.Visible : Visibility.Collapsed;
        Seek.Opacity = value.CanSeek ? 1 : 0.55;
        var label = value.Playing ? "Pause" : "Play";
        Toggle.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(Toggle, label);
        if (change == PlaybackChange.Ended && value.HasTrack) MusicStatus.Text = "Playback ended";
        if (change != PlaybackChange.None) AnimatePlayback(change);
        UpdateProgress();
        if (view == View.Music && oldHeightState != (value.HasTrack, value.Duration > 0, ArtistName.Visibility, MusicStatus.Text))
            OnViewChanged();
        return change;
    }

    private void AnimatePlayback(PlaybackChange change)
    {
        var animate = MusicView.IsVisible && SystemParameters.ClientAreaAnimation;
        SetGlyphOpacity(PlayGlyph, reading.Playing ? 0 : 1, animate);
        SetGlyphOpacity(PauseGlyph, reading.Playing ? 1 : 0, animate);
        var scale = reading.HasTrack && !reading.Playing ? .92 : 1;
        PlaybackMotion.EaseTo(artworkScale, ScaleTransform.ScaleXProperty, scale, 180, animate);
        PlaybackMotion.EaseTo(artworkScale, ScaleTransform.ScaleYProperty, scale, 180, animate);
        if (PlaybackMotion.ReplacesTrack(change))
        {
            var offset = PlaybackMotion.Offset(change, 9);
            var duration = change == PlaybackChange.Advanced ? 240 : 190;
            PlaybackMotion.Arrive(ArtTile, offset, duration);
            PlaybackMotion.Arrive(TrackDetails, offset, duration);
        }
        else if (change == PlaybackChange.Restarted)
            PlaybackMotion.Arrive(Timeline, -5, 160, .72);
    }

    private static void SetGlyphOpacity(UIElement glyph, double target, bool animate)
    {
        var current = glyph.Opacity;
        glyph.Opacity = target;
        glyph.BeginAnimation(OpacityProperty, animate && Math.Abs(current - target) > .01
            ? PlaybackMotion.To(current, target, 125) : null);
    }

    private void UpdateProgress()
    {
        if (seekDebounce.IsEnabled || seeking || Seek.IsMouseCaptureWithin) return;
        var elapsed = reading.Playing ? (Environment.TickCount64 - receivedAt) / 1000d * reading.PlaybackRate : 0;
        var position = Math.Clamp(reading.Position + elapsed, 0, reading.Duration);
        updatingSeek = true;
        Seek.Maximum = Math.Max(1, reading.Duration);
        Seek.Value = position;
        updatingSeek = false;
        Elapsed.Text = reading.Duration > 0 ? FormatTime(position) : "";
        Duration.Text = reading.Duration > 0 ? FormatTime(reading.Duration) : "";
    }

    private async void PreviousTrack(object sender, RoutedEventArgs e) => await SendMediaCommand("previous");
    private async void ToggleTrack(object sender, RoutedEventArgs e) => await SendMediaCommand("toggle");
    private async void NextTrack(object sender, RoutedEventArgs e) => await SendMediaCommand("next");

    private async Task SendMediaCommand(string action, double seconds = 0)
    {
        var command = ++mediaCommandVersion;
        MusicStatus.Text = "";
        if (view == View.Music) OnViewChanged();
        var direction = action switch
        {
            "previous" => TrackDirection.Backward,
            "next" => TrackDirection.Forward,
            _ => TrackDirection.None
        };
        var intent = (direction, Environment.TickCount64 + 2_000);
        if (direction != TrackDirection.None) trackIntent = intent;
        if (action == "seek") seekIntentUntil = intent.Item2;
        if (!await media.Control(action, seconds) && command == mediaCommandVersion)
        {
            if (trackIntent == intent) trackIntent = null;
            MusicStatus.Text = action == "seek" ? "Couldn’t change playback position." : "This player didn’t respond.";
            if (view == View.Music) OnViewChanged();
        }
    }

    private async void FinishSeek(object sender, MouseButtonEventArgs e) => await CommitSeek();

    private async Task CommitSeek()
    {
        if (!seekDebounce.IsEnabled || seeking) return;
        seekDebounce.Stop();
        if (!Seek.IsEnabled) return;
        seeking = true;
        try { await SendMediaCommand("seek", Seek.Value); }
        finally { seeking = false; }
    }

    private void SeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized || updatingSeek || !Seek.IsEnabled) return;
        Elapsed.Text = FormatTime(e.NewValue);
        seekDebounce.Stop();
        seekDebounce.Start();
    }

    internal void Tick()
    {
        UpdateTimer();
        if (IsVisible) UpdateProgress();
        if (copyFeedbackUntil != 0 && Environment.TickCount64 >= copyFeedbackUntil)
        {
            copyFeedbackUntil = 0;
            CopyClip.Content = "Copy";
        }
    }

    private void AddFiles(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Multiselect = true, CheckFileExists = true, Title = "Add files to the shelf" };
        pickerOpen = true;
        try
        {
            if (picker.ShowDialog(Window.GetWindow(this)) == true) AddPaths(picker.FileNames);
        }
        finally { pickerOpen = false; }
    }

    internal void AddPaths(string[] paths)
    {
        SetStatus(ShelfStatus, null);
        var skipped = false;
        foreach (var path in paths)
        {
            if (shelf.Any(item => StringComparer.OrdinalIgnoreCase.Equals(item.Path, path))) continue;
            if (shelf.Count >= 32) { skipped = true; break; }
            if (File.Exists(path) || Directory.Exists(path)) shelf.Add(new ShelfItem(path));
            else skipped = true;
        }
        RefreshShelf();
        if (skipped) SetStatus(ShelfStatus, shelf.Count >= 32 ? "Shelf full. Remove a file to add another." : "Some files are no longer available.");
        ShowView(View.Shelf);
    }

    private void RefreshShelf()
    {
        ShelfEmpty.Visibility = shelf.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfCount.Text = shelf.Count == 1 ? "1 file" : $"{shelf.Count} files";
    }

    private void SetStatus(TextBlock target, string? message)
    {
        target.Text = message ?? "";
        target.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        OnViewChanged();
    }

    private void AddDroppedFiles(object sender, DragEventArgs e)
    {
        DropSurface.BorderBrush = Brushes.Transparent;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths);
        e.Handled = true;
    }

    private void ShowDropEffect(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? e.AllowedEffects & (DragDropEffects.Copy | DragDropEffects.Link) : DragDropEffects.None;
        DropSurface.BorderBrush = e.Effects != DragDropEffects.None ? (Brush)FindResource("Accent") : Brushes.Transparent;
        e.Handled = true;
    }

    private void LeaveDropTarget(object sender, DragEventArgs e) => DropSurface.BorderBrush = Brushes.Transparent;

    private void RemoveSelectedFile(object sender, RoutedEventArgs e)
    {
        if (Files.SelectedItem is ShelfItem item) shelf.Remove(item);
        SetStatus(ShelfStatus, null);
        RefreshShelf();
    }

    private void OpenSelectedFile(object sender, MouseButtonEventArgs e)
        => OpenSelectedFile();

    private void OpenSelectedFileWithKeyboard(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) RemoveSelectedFile(sender, e);
        else if (e.Key == Key.Enter) OpenSelectedFile();
        else return;
        e.Handled = true;
    }

    private void OpenSelectedFile()
    {
        if (Files.SelectedItem is not ShelfItem item) return;
        if (!(File.Exists(item.Path) || Directory.Exists(item.Path)))
        {
            SetStatus(ShelfStatus, "File missing. Remove it and add it again.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
            SetStatus(ShelfStatus, null);
        }
        catch (System.ComponentModel.Win32Exception) { SetStatus(ShelfStatus, "Couldn’t open this file. Try File Explorer."); }
    }

    private void StartFileDrag(object sender, MouseButtonEventArgs e) => fileDragOrigin = e.GetPosition(Files);

    private void DragFile(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || fileDragOrigin is not { } origin || Files.SelectedItem is not ShelfItem item)
            return;
        var offset = e.GetPosition(Files) - origin;
        if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        fileDragOrigin = null;
        if (!(File.Exists(item.Path) || Directory.Exists(item.Path))) return;
        fileDragActive = true;
        try
        {
            DragDrop.DoDragDrop(Files, new DataObject(DataFormats.FileDrop, new[] { item.Path }), DragDropEffects.Copy);
        }
        finally { fileDragActive = false; }
    }

    private void ClipboardPreferenceChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        var enabled = ClipboardEnabled.IsChecked == true;
        listenToClipboard(enabled);
        if (!enabled) clipboard.Clear();
        SetStatus(ClipboardStatus, null);
        UpdateClipboardEmpty();
    }

    internal void RecordClipboard()
    {
        if (ClipboardEnabled.IsChecked != true) return;
        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || text.Length > 32_768) return;
            var duplicate = clipboard.FirstOrDefault(item => StringComparer.Ordinal.Equals(item.Text, text));
            if (duplicate is not null) clipboard.Remove(duplicate);
            clipboard.Insert(0, new ClipboardEntry(text));
            if (clipboard.Count > 20) clipboard.RemoveAt(20);
            UpdateClipboardEmpty();
        }
        catch (ExternalException) { }
    }

    private void CopySelected(object sender, RoutedEventArgs e)
    {
        if (Clips.SelectedItem is not ClipboardEntry item) return;
        try
        {
            Clipboard.SetText(item.Text);
            CopyClip.Content = "Copied";
            copyFeedbackUntil = Environment.TickCount64 + 1800;
            SetStatus(ClipboardStatus, null);
        }
        catch (ExternalException) { SetStatus(ClipboardStatus, "Clipboard busy. Try copying again."); }
    }

    private void CopyClipWithKeyboard(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && !(e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)) return;
        CopySelected(sender, e);
        e.Handled = true;
    }

    private void ClearClipboard(object sender, RoutedEventArgs e)
    {
        clipboard.Clear();
        SetStatus(ClipboardStatus, null);
        UpdateClipboardEmpty();
    }

    private void UpdateClipboardEmpty()
    {
        ClipboardEmpty.Text = ClipboardEnabled.IsChecked == true ? "Copy text to add it here." : "Turn on history to keep copied text here.";
        ClipboardEmpty.Visibility = clipboard.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearClips.IsEnabled = clipboard.Count > 0;
    }

    private void TimerDurationChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || timerEndsAt != 0) return;
        if (sender is RadioButton { Tag: string value } && int.TryParse(value, out var minutes))
            selectedDuration = minutes * 60;
        remaining = selectedDuration;
        timerCompleted = false;
        UpdateTimer();
    }

    private void ToggleTimer(object sender, RoutedEventArgs e)
    {
        if (timerEndsAt != 0)
        {
            remaining = Math.Max(0, (timerEndsAt - Environment.TickCount64) / 1000d);
            timerEndsAt = 0;
        }
        else
        {
            if (remaining <= 0) remaining = selectedDuration;
            timerEndsAt = Environment.TickCount64 + (long)(remaining * 1000);
            timerCompleted = false;
        }
        UpdateTimer();
    }

    private void ResetTimer(object sender, RoutedEventArgs e)
    {
        timerEndsAt = 0;
        remaining = selectedDuration;
        timerCompleted = false;
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        if (timerEndsAt != 0)
        {
            remaining = Math.Max(0, (timerEndsAt - Environment.TickCount64) / 1000d);
            if (remaining <= 0)
            {
                timerEndsAt = 0;
                timerCompleted = true;
                System.Media.SystemSounds.Exclamation.Play();
            }
        }
        TimerReadout.Text = timerCompleted ? "Time’s up" : FormatTime(Math.Ceiling(remaining), true);
        var paused = timerEndsAt == 0 && !timerCompleted && remaining < selectedDuration;
        TimerButton.Content = timerEndsAt != 0 ? "Pause timer" : paused ? "Resume timer" : timerCompleted ? "Start again" : "Start timer";
        TimerState.Text = timerCompleted ? "Finished" : timerEndsAt != 0 ? "Counting down" : paused ? "Paused" : "Ready";
        TimerProgress.Value = Math.Clamp(remaining / selectedDuration, 0, 1);
        TimerReset.IsEnabled = timerEndsAt != 0 || remaining < selectedDuration || timerCompleted;
        TimerMinutes.IsEnabled = timerEndsAt == 0;
    }

    internal string? CompactTimerText => timerCompleted ? "Time’s up"
        : timerEndsAt != 0 ? FormatTime(Math.Ceiling(remaining), true) : null;
    internal bool TimerCompleted => timerCompleted;

    private static string FormatTime(double seconds, bool padMinutes = false)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss")
            : time.ToString(padMinutes ? @"mm\:ss" : @"m\:ss");
    }

    public void Dispose()
    {
        seekDebounce.Stop();
        media.Dispose();
    }
}
