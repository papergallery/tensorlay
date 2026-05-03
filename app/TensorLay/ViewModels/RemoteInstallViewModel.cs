using System.IO;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TensorLay.Models;
using TensorLay.Services;

namespace TensorLay.ViewModels;

// One-shot ViewModel for RemoteInstallApprovalWindow. Drives the Approve /
// Reject / AlwaysReject decision and, on Approve, the download itself.
//
// Lifecycle: created with a single RemoteTask, lives until DecisionMade
// fires (the window subscribes to that event to close itself). The download
// continues running on ModelDownloader after DecisionMade if the user
// clicked Approve and the download is still in flight — the ViewModel keeps
// itself alive via the active subscription to DownloadProgressChanged
// until the corresponding DownloadCompleted event arrives.
public partial class RemoteInstallViewModel : ViewModelBase
{
    private readonly RemoteTaskService _remoteTaskService;
    private readonly SettingsService _settingsService;
    private readonly RemoteTask _task;
    private DownloadTask? _downloadTask;

    public RemoteInstallViewModel(RemoteTaskService remoteTaskService, SettingsService settingsService, RemoteTask task)
    {
        _remoteTaskService = remoteTaskService;
        _settingsService = settingsService;
        _task = task;

        AgentLabel = task.AgentLabel;
        DisplayName = task.DisplayName;
        Reason = task.Reason ?? "";
        ServiceLabel = ServiceDisplayName(task.ServiceId);
        SourceUrl = task.Url;

        TargetFolder = ResolveTargetFolder(task);
        SizeMbDisplay = FormatSize(task);
        ExpiresDisplay = task.ExpiresAt.ToLocalTime().ToString("g");
    }

    [ObservableProperty] private string _agentLabel = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _serviceLabel = "";
    [ObservableProperty] private string _sourceUrl = "";
    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private string _targetFolder = "";
    [ObservableProperty] private string _sizeMbDisplay = "";
    [ObservableProperty] private string _expiresDisplay = "";
    [ObservableProperty] private bool _hasReason;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _isFinished;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _statusMessage = "";

    partial void OnReasonChanged(string value)
    {
        HasReason = !string.IsNullOrWhiteSpace(value);
    }

    public event Action? DecisionMade;

    [RelayCommand]
    private async Task Approve()
    {
        IsDownloading = true;
        StatusMessage = "Starting download…";

        // Tell the relay we accepted before we start the download — that
        // way an agent polling status sees `approved` immediately, not 30s
        // later when we're already streaming bytes.
        await _remoteTaskService.PostStatusAsync(_task.Id, "approved", null, null).ConfigureAwait(true);

        var settings = _settingsService.Load();
        string? targetPath = _remoteTaskService.ResolveTargetPath(_task, settings.InstallDirectory);
        if (targetPath is null)
        {
            StatusMessage = "Cannot resolve target folder for this service.";
            IsFinished = true;
            await _remoteTaskService.PostStatusAsync(_task.Id, "failed", null, "Target path resolution failed").ConfigureAwait(true);
            DecisionMade?.Invoke();
            return;
        }

        // Subscribe BEFORE StartDownload so we don't miss the first
        // progress event for tiny files.
        _remoteTaskService.Downloader.DownloadProgressChanged += OnDownloadProgress;
        _remoteTaskService.Downloader.DownloadCompleted += OnDownloadCompleted;

        _downloadTask = _remoteTaskService.Downloader.StartDownload(_task.Url, targetPath, _task.ServiceId);
        await _remoteTaskService.PostStatusAsync(_task.Id, "downloading", 0, null).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task Reject()
    {
        await _remoteTaskService.PostStatusAsync(_task.Id, "rejected", null, null).ConfigureAwait(true);
        DecisionMade?.Invoke();
    }

    [RelayCommand]
    private async Task AlwaysReject()
    {
        var settings = _settingsService.Load();
        if (!settings.RejectedAgentLabels.Contains(_task.AgentLabel))
        {
            settings.RejectedAgentLabels.Add(_task.AgentLabel);
            _settingsService.Save(settings);
        }
        await Reject().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCancelDownload))]
    private void CancelDownload()
    {
        if (_downloadTask is null) return;
        _remoteTaskService.Downloader.CancelDownload(_downloadTask);
    }

    private bool CanCancelDownload() => IsDownloading && !IsFinished;

    private void OnDownloadProgress(DownloadTask t)
    {
        if (_downloadTask is null || t.Url != _downloadTask.Url) return;
        RunOnUI(() =>
        {
            DownloadProgress = t.ProgressPercent;
            StatusMessage = t.TotalBytes > 0
                ? $"{t.BytesDownloaded / 1_048_576} / {t.TotalBytes / 1_048_576} MB"
                : $"{t.BytesDownloaded / 1_048_576} MB";
        });
        // Throttled relay update — only on whole-percent changes to keep
        // the relay log readable. Fire-and-forget is fine; if the post
        // fails the next progress event retries naturally.
        if ((int)t.ProgressPercent % 5 == 0)
            _ = _remoteTaskService.PostStatusAsync(_task.Id, "downloading", t.ProgressPercent, null);
    }

    private async void OnDownloadCompleted(DownloadTask t)
    {
        if (_downloadTask is null || t.Url != _downloadTask.Url) return;
        _remoteTaskService.Downloader.DownloadProgressChanged -= OnDownloadProgress;
        _remoteTaskService.Downloader.DownloadCompleted -= OnDownloadCompleted;

        string finalState;
        string? errorMsg = null;
        switch (t.State)
        {
            case DownloadState.Completed:
                if (!string.IsNullOrEmpty(_task.Sha256))
                {
                    if (!await VerifySha256(t.TargetPath, _task.Sha256!).ConfigureAwait(true))
                    {
                        try { File.Delete(t.TargetPath); } catch { /* best-effort */ }
                        finalState = "failed";
                        errorMsg = "SHA-256 checksum mismatch";
                        break;
                    }
                }
                finalState = "completed";
                break;
            case DownloadState.Cancelled:
                finalState = "cancelled";
                break;
            default:
                finalState = "failed";
                errorMsg = string.IsNullOrEmpty(t.ErrorMessage) ? "Download failed" : t.ErrorMessage;
                break;
        }

        await _remoteTaskService.PostStatusAsync(_task.Id, finalState, finalState == "completed" ? 100.0 : null, errorMsg).ConfigureAwait(true);

        RunOnUI(() =>
        {
            IsFinished = true;
            StatusMessage = finalState == "completed"
                ? "Download complete."
                : finalState == "cancelled"
                    ? "Cancelled."
                    : $"Failed: {errorMsg ?? "unknown error"}";
            DecisionMade?.Invoke();
        });
    }

    private static async Task<bool> VerifySha256(string filePath, string expected)
    {
        try
        {
            using var sha = SHA256.Create();
            await using var stream = File.OpenRead(filePath);
            byte[] hash = await sha.ComputeHashAsync(stream).ConfigureAwait(false);
            string actual = Convert.ToHexString(hash).ToLowerInvariant();
            return actual == expected.Trim().ToLowerInvariant();
        }
        catch
        {
            return false;
        }
    }

    private string ResolveTargetFolder(RemoteTask task)
    {
        var settings = _settingsService.Load();
        var def = ServiceRegistry.GetAll().FirstOrDefault(s => s.Id == task.ServiceId);
        if (def is null) return "(unknown service)";
        return Path.Combine(settings.InstallDirectory, def.RelativeInstallPath, def.ModelsSubfolder);
    }

    private static string ServiceDisplayName(string id)
    {
        var def = ServiceRegistry.GetAll().FirstOrDefault(s => s.Id == id);
        return def?.DisplayName ?? id;
    }

    private static string FormatSize(RemoteTask task)
    {
        // Prefer the HEAD-preflight value when present — that's what the
        // user is actually going to receive on disk. Fall back to the
        // agent's claim with an "(estimated)" suffix so the user knows
        // it's a guess.
        if (task.ActualSizeMb is double actual)
            return FormatMb(actual);
        if (task.SizeMb is double estimated)
            return $"{FormatMb(estimated)} (estimated)";
        return "unknown size";
    }

    private static string FormatMb(double mb)
    {
        if (mb >= 1024) return $"{mb / 1024:F1} GB";
        return $"{mb:F0} MB";
    }
}
