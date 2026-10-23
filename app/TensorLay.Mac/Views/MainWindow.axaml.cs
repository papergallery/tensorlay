using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TensorLay.Models;
using TensorLay.ViewModels;

namespace TensorLay.Mac.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        TitleBar.PointerPressed += OnTitleBarPressed;
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
    }

    // Extended client area: our title strip replaces the native one, so it
    // has to move the window itself. Double-click zooms like a native bar.
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            BeginMoveDrag(e);
    }

    // Same filtering as the WPF MainWindow: Home lists installed services
    // once any exist (all of them before that), and the Models views show a
    // flat list across services.
    private void Attach(MainViewModel? vm)
    {
        _viewModel = vm;
        if (vm is null) return;

        RefreshHomeServices();
        RefreshAllModels();

        vm.Services.CollectionChanged += (_, _) => { RefreshHomeServices(); RefreshAllModels(); };
        foreach (var svc in vm.Services)
        {
            svc.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ServiceViewModel.State))
                {
                    RefreshHomeServices();
                    vm.NotifyCountersChanged();
                }
            };
            svc.Models.CollectionChanged += (_, _) => RefreshAllModels();
        }

        vm.LogLines.CollectionChanged += OnLogLinesChanged;
    }

    private void RefreshHomeServices()
    {
        if (_viewModel is null) return;
        var installed = _viewModel.Services.Where(s => s.State != ServiceState.NotInstalled).ToList();
        HomeServicesPanel.ItemsSource = installed.Count > 0 ? installed : _viewModel.Services.ToList();
    }

    private void RefreshAllModels()
    {
        if (_viewModel is null) return;
        var flat = _viewModel.Services
            .SelectMany(s => s.Models.Select(m => new FlatModelItem
            {
                ServiceName = s.Definition.DisplayName,
                FileName = m.FileName,
                DisplaySize = m.DisplaySize,
                Model = m,
            }))
            .ToList();
        AllModelsPanel.ItemsSource = flat;
        HomeModelsPanel.ItemsSource = flat;
        _viewModel.NotifyCountersChanged();
    }

    // Keep the log view pinned to the newest line while the user is at the
    // bottom; leave it alone if they scrolled up to read something.
    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        bool atBottom = LogScroll.Offset.Y >= LogScroll.Extent.Height - LogScroll.Viewport.Height - 40;
        if (atBottom)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(),
                Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnDeleteModelClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: FlatModelItem { Model: { } model } })
            _viewModel?.RequestModelDeletion(model);
    }

    private void OnDeleteServiceModelClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ModelInfo model })
            _viewModel?.RequestModelDeletion(model);
    }

    // ⋯ opens its ContextMenu on a plain left click.
    private void OverflowButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
            menu.Open(button);
    }

    private void OnClearLogsClick(object? sender, RoutedEventArgs e) => _viewModel?.LogLines.Clear();

    private async void OnCopyLogsClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || Clipboard is null) return;
        await Clipboard.SetTextAsync(string.Join("\n", _viewModel.LogLines));
    }
}

/// <summary>Flat model record for the Models lists.</summary>
internal sealed class FlatModelItem
{
    public string ServiceName { get; init; } = "";
    public string FileName { get; init; } = "";
    public string DisplaySize { get; init; } = "";
    public ModelInfo? Model { get; init; }
}
