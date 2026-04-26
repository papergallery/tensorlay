using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TensorLay.Models;
using TensorLay.Services;

namespace TensorLay.ViewModels;

public partial class ServiceViewModel : ViewModelBase
{
    private readonly ProcessManager _processManager;
    private readonly HealthCheckService _healthCheckService;
    private readonly InstallerService _installerService;
    private readonly ModelDownloader _modelDownloader;
    private readonly SettingsService _settingsService;

    public ServiceDefinition Definition { get; }

    [ObservableProperty]
    private ServiceState _state = ServiceState.NotInstalled;

    [ObservableProperty]
    private ObservableCollection<ModelInfo> _models = new();

    [ObservableProperty]
    private string _statusText = "Not installed";

    [ObservableProperty]
    private double _installProgress;

    [ObservableProperty]
    private string _newModelUrl = "";

    [ObservableProperty]
    private DownloadTask? _activeDownload;

    public ServiceViewModel(
        ServiceDefinition definition,
        ProcessManager processManager,
        HealthCheckService healthCheckService,
        InstallerService installerService,
        ModelDownloader modelDownloader,
        SettingsService settingsService)
    {
        Definition = definition;
        _processManager = processManager;
        _healthCheckService = healthCheckService;
        _installerService = installerService;
        _modelDownloader = modelDownloader;
        _settingsService = settingsService;

        _healthCheckService.HealthChanged += OnHealthChanged;
        _processManager.ProcessExited += OnProcessExited;
        _modelDownloader.DownloadProgressChanged += OnDownloadProgress;
        _modelDownloader.DownloadCompleted += OnDownloadCompleted;

        var settings = _settingsService.Load();
        bool installed = _installerService.IsInstalled(definition, settings.InstallDirectory);
        State = installed ? ServiceState.Stopped : ServiceState.NotInstalled;
        UpdateStatusText();
    }

    private void OnHealthChanged(string serviceId, bool isHealthy)
    {
        if (serviceId != Definition.Id) return;

        RunOnUI(() =>
        {
            if (State == ServiceState.Starting || State == ServiceState.Running)
            {
                State = isHealthy ? ServiceState.Running : ServiceState.Starting;
                UpdateStatusText();
                StartCommand.NotifyCanExecuteChanged();
                StopCommand.NotifyCanExecuteChanged();
            }
        });
    }

    private void OnProcessExited(string serviceId, int exitCode)
    {
        if (serviceId != Definition.Id) return;

        RunOnUI(() =>
        {
            State = exitCode == 0 ? ServiceState.Stopped : ServiceState.Error;
            UpdateStatusText();
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
        });
    }

    private void OnDownloadProgress(DownloadTask task)
    {
        if (task.ServiceId != Definition.Id) return;
        RunOnUI(() =>
        {
            ActiveDownload = task;
            InstallProgress = task.ProgressPercent;
        });
    }

    private void OnDownloadCompleted(DownloadTask task)
    {
        if (task.ServiceId != Definition.Id) return;
        RunOnUI(() =>
        {
            ActiveDownload = null;
            InstallProgress = 0;
            NewModelUrl = "";
            RefreshModels();
        });
    }

    private void UpdateStatusText()
    {
        StatusText = State switch
        {
            ServiceState.NotInstalled => "Not installed",
            ServiceState.Installing => "Installing...",
            ServiceState.Stopped => "Stopped",
            ServiceState.Starting => "Starting...",
            ServiceState.Running => $"Running on port {Definition.Port}",
            ServiceState.Stopping => "Stopping...",
            ServiceState.Error => "Error",
            _ => "Unknown"
        };
    }

    [RelayCommand]
    private async Task Install()
    {
        State = ServiceState.Installing;
        UpdateStatusText();

        try
        {
            var settings = _settingsService.Load();
            await _installerService.Install(Definition, settings.InstallDirectory);
            State = ServiceState.Stopped;
        }
        catch
        {
            State = ServiceState.Error;
        }

        UpdateStatusText();
        InstallProgress = 0;
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task Uninstall()
    {
        try
        {
            if (State == ServiceState.Running)
                await Stop();

            var settings = _settingsService.Load();
            await _installerService.Uninstall(Definition, settings.InstallDirectory);
            State = ServiceState.NotInstalled;
            Models.Clear();
        }
        catch
        {
            State = ServiceState.Error;
        }

        UpdateStatusText();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private bool CanStart() => State == ServiceState.Stopped;
    private bool CanStop() => State == ServiceState.Running || State == ServiceState.Starting;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        State = ServiceState.Starting;
        UpdateStatusText();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();

        try
        {
            var settings = _settingsService.Load();
            _processManager.StartService(Definition, settings.InstallDirectory);
            _healthCheckService.RegisterService(
                Definition.Id,
                $"http://127.0.0.1:{Definition.Port}",
                Definition.HealthEndpoint);
        }
        catch
        {
            State = ServiceState.Error;
            UpdateStatusText();
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
        }

        await Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Stop()
    {
        State = ServiceState.Stopping;
        UpdateStatusText();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();

        try
        {
            _healthCheckService.UnregisterService(Definition.Id);
            await _processManager.StopService(Definition.Id);
            State = ServiceState.Stopped;
        }
        catch
        {
            State = ServiceState.Error;
        }

        UpdateStatusText();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task DownloadModel()
    {
        if (string.IsNullOrWhiteSpace(NewModelUrl)) return;

        var settings = _settingsService.Load();
        var targetPath = Path.Combine(
            settings.InstallDirectory,
            Definition.RelativeInstallPath,
            Definition.ModelsSubfolder,
            Path.GetFileName(new Uri(NewModelUrl).AbsolutePath));

        var task = _modelDownloader.StartDownload(NewModelUrl, targetPath, Definition.Id);
        ActiveDownload = task;
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void DeleteModel(ModelInfo model)
    {
        try
        {
            if (File.Exists(model.FullPath))
                File.Delete(model.FullPath);
            Models.Remove(model);
        }
        catch { /* ignore delete errors */ }
    }

    public void RefreshModels()
    {
        var list = _modelDownloader.GetModelsForService(Definition, _settingsService.Load().InstallDirectory);
        Models.Clear();
        foreach (var m in list)
            Models.Add(m);
    }

    partial void OnStateChanged(ServiceState value)
    {
        UpdateStatusText();
    }
}
