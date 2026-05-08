using System.Windows;
using System.Windows.Controls;
using TensorLay.Models;
using TensorLay.ViewModels;

namespace TensorLay.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel = DataContext as MainViewModel;

        if (ClearLogsButton != null)
            ClearLogsButton.Click += (_, _) => _viewModel?.LogLines.Clear();

        if (CopyLogsButton != null)
        {
            CopyLogsButton.Click += (_, _) =>
            {
                if (_viewModel != null)
                    Clipboard.SetText(string.Join("\n", _viewModel.LogLines));
            };
        }

        RefreshHomeServices();
        RefreshAllModelsPanel();

        if (_viewModel != null)
        {
            _viewModel.Services.CollectionChanged += (_, _) => { RefreshHomeServices(); RefreshAllModelsPanel(); };
            foreach (var svc in _viewModel.Services)
            {
                svc.PropertyChanged += (_, _) => { RefreshHomeServices(); _viewModel.NotifyCountersChanged(); };
                svc.Models.CollectionChanged += (_, _) => RefreshAllModelsPanel();
            }
        }
    }

    private void RefreshHomeServices()
    {
        if (_viewModel == null || HomeServicesPanel == null) return;

        var hasInstalled = _viewModel.Services.Any(s => s.State != Models.ServiceState.NotInstalled);
        if (hasInstalled)
        {
            // Show only installed services
            HomeServicesPanel.ItemsSource = _viewModel.Services
                .Where(s => s.State != Models.ServiceState.NotInstalled).ToList();
        }
        else
        {
            // Show all available services
            HomeServicesPanel.ItemsSource = _viewModel.Services;
        }
    }

    private void RefreshAllModelsPanel()
    {
        if (_viewModel == null || AllModelsPanel == null) return;

        var flat = _viewModel.Services
            .SelectMany(s => s.Models.Select(m => new FlatModelItem
            {
                ServiceName = s.Definition.DisplayName,
                FileName    = m.FileName,
                DisplaySize = m.DisplaySize,
                FullPath    = m.FullPath
            }))
            .ToList();

        AllModelsPanel.ItemsSource = flat;

        // Also update Home models panel
        if (HomeModelsPanel != null)
            HomeModelsPanel.ItemsSource = flat;

        _viewModel.NotifyCountersChanged();
    }

    // ── Window chrome buttons ──
    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    // The ⋯ overflow button next to each service row drops its ContextMenu
    // on a plain left-click. Without this WPF would only open the menu on
    // right-click, which isn't discoverable.
    private void OverflowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu is not null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        Application.Current.Shutdown();
    }
}

/// <summary>Flat model record for the Models page list.</summary>
internal sealed class FlatModelItem
{
    public string ServiceName { get; init; } = "";
    public string FileName    { get; init; } = "";
    public string DisplaySize { get; init; } = "";
    public string FullPath    { get; init; } = "";
}
