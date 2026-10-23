using CommunityToolkit.Mvvm.ComponentModel;

namespace TensorLay.Models;

// ObservableObject so Models-page UI can bind directly to ProgressPercent
// and State and see live updates without the ViewModel having to refresh
// the whole collection on every progress event.
public partial class DownloadTask : ObservableObject
{
    public string Url { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string ServiceId { get; set; } = "";

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private long _bytesDownloaded;

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    private DownloadState _state = DownloadState.Pending;

    [ObservableProperty]
    private string _errorMessage = "";

    public CancellationTokenSource CancellationTokenSource { get; set; } = new();

    public string FileName => System.IO.Path.GetFileName(TargetPath);
}
