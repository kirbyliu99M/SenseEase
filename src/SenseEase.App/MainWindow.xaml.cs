using System;
using System.Windows;
using System.Windows.Interop;
using SenseEase.App.ViewModels;
using SenseEase.App.Views;
using SenseEase.Core.Models;

namespace SenseEase.App;

/// <summary>Dashboard window. Hosts the view model and manages the overlay window.</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private OverlayWindow? _overlay;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        _viewModel.OverlayVisibilityChanged += OnOverlayVisibilityChanged;
        _viewModel.StateChanged             += OnStateChanged;

        if (DataContext is SenseEase.App.ViewModels.MainViewModel vm)
        {
            vm.ChatbotVm.Messages.CollectionChanged += (_, _) =>
                Dispatcher.BeginInvoke(new Action(() => ChatScroll.ScrollToEnd()));
            ChatScroll.Loaded        += (_, _) => ChatScroll.ScrollToEnd();
            ChatScroll.IsVisibleChanged += (_, _) =>
            {
                if (ChatScroll.IsVisible)
                    Dispatcher.BeginInvoke(new Action(() => ChatScroll.ScrollToEnd()));
            };
        }

        // ── Eye-Protection chart bootstrap ─────────────────────────────────────
        Loaded += (_, _) =>
        {
            EnsureChartInitialized();
            if (DataContext is SenseEase.App.ViewModels.MainViewModel vm2)
            {
                // Coalescing: mark dirty on each ring-buffer append; a 15 Hz timer
                // redraws once per interval only when data has actually changed.
                vm2.OfiHistory.CollectionChanged      += (_, _) => _chartDirty = true;
                vm2.PressureHistory.CollectionChanged += (_, _) => _chartDirty = true;
                _chartTimer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(67) };
                _chartTimer.Tick += (_, _) =>
                {
                    if (_chartDirty) { _chartDirty = false; RedrawChart(); }
                };
                _chartTimer.Start();
            }
        };

        SourceInitialized += (_, _) =>
        {
            if (DataContext is SenseEase.App.ViewModels.MainViewModel vm)
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                vm.WireLuxComfort(hwnd);
            }
        };
        Closed += (_, _) =>
        {
            if (DataContext is SenseEase.App.ViewModels.MainViewModel vm)
            {
                vm.DisposeServices();
                vm.LuxController?.Shutdown();
            }
        };
        Closed += (_, _) => _overlay?.Close();
        // Stop the 15 Hz chart timer so the DispatcherTimer + VM ref don't pin the
        // window after Close (kept the Tick closure alive otherwise).
        Closed += (_, _) => _chartTimer?.Stop();
    }

    // B2/B4: Recreate overlay if null or already unloaded; sync OverlayVisible on external close.
    private void OnOverlayVisibilityChanged(object? sender, bool visible)
    {
        if (visible)
        {
            if (_overlay == null || !_overlay.IsLoaded)
            {
                _overlay = new OverlayWindow();
                _overlay.Closed += OnOverlayClosed;   // B4: keep VM in sync
            }

            _overlay.Show();
            _overlay.ApplyState(_viewModel.CurrentState);
        }
        else
        {
            _overlay?.Hide();
        }
    }

    // B4: User closes overlay window externally → sync ViewModel flag.
    private void OnOverlayClosed(object? sender, System.EventArgs e)
    {
        _overlay = null;                       // B2: force recreation next time
        _viewModel.OverlayVisible = false;    // B4: keep checkbox/button in sync
    }

    private void OnStateChanged(object? sender, SenseState state)
    {
        if (_overlay is { IsVisible: true })
            _overlay.ApplyState(state);
    }

    // ── Eye-Protection live chart rendering ───────────────────────────────────
    private const double ChartYMax = 200.0;
    private const double ChartTriggerLine = 85.0;
    private System.Windows.Shapes.Polyline? _ofiPolyline;
    private System.Windows.Shapes.Polyline? _pressurePolyline;
    private System.Windows.Shapes.Line? _triggerLine;
    private bool _chartDirty;
    private System.Windows.Threading.DispatcherTimer? _chartTimer;

    private void EnsureChartInitialized()
    {
        if (PressureChartCanvas == null || _ofiPolyline != null) return;
        _ofiPolyline = new System.Windows.Shapes.Polyline
        {
            Stroke = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x7B, 0x8D, 0xB0)),
            StrokeThickness = 1.5,
        };
        _pressurePolyline = new System.Windows.Shapes.Polyline
        {
            Stroke = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x02, 0x4A, 0xD8)),
            StrokeThickness = 2.0,
        };
        _triggerLine = new System.Windows.Shapes.Line
        {
            Stroke = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xE5, 0x47, 0x47)),
            StrokeThickness = 1.0,
            StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 3 },
        };
        PressureChartCanvas.Children.Add(_triggerLine);
        PressureChartCanvas.Children.Add(_ofiPolyline);
        PressureChartCanvas.Children.Add(_pressurePolyline);
        PressureChartCanvas.SizeChanged += (_, _) => RedrawChart();
    }

    private void RedrawChart()
    {
        if (PressureChartCanvas == null || _ofiPolyline == null || _pressurePolyline == null || _triggerLine == null) return;
        if (DataContext is not SenseEase.App.ViewModels.MainViewModel vm) return;
        double w = PressureChartCanvas.ActualWidth;
        double h = PressureChartCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double triggerY = h - (ChartTriggerLine / ChartYMax * h);
        _triggerLine.X1 = 0; _triggerLine.X2 = w;
        _triggerLine.Y1 = triggerY; _triggerLine.Y2 = triggerY;
        // In-place mutation: reuse existing PointCollections to avoid per-tick allocation.
        FillChartPoints(vm.OfiHistory,      _ofiPolyline.Points,      w, h);
        FillChartPoints(vm.PressureHistory, _pressurePolyline.Points, w, h);
    }

    private static void FillChartPoints(
        System.Collections.Generic.IReadOnlyList<double> samples,
        System.Windows.Media.PointCollection target,
        double w, double h)
    {
        int n = samples.Count;
        // Grow or shrink the collection to exactly n slots.
        while (target.Count < n) target.Add(new System.Windows.Point(0, 0));
        while (target.Count > n) target.RemoveAt(target.Count - 1);
        if (n < 2) return;
        double dx = w / Math.Max(1, n - 1);
        for (int i = 0; i < n; i++)
        {
            double v = Math.Clamp(samples[i], 0, ChartYMax);
            double y = h - (v / ChartYMax * h);
            // Point is a struct — indexer assignment is a value copy, no heap allocation.
            target[i] = new System.Windows.Point(i * dx, y);
        }
    }
}
