namespace TensorLay.Models;

public class DownloadTask
{
    public string Url { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string ServiceId { get; set; } = "";
    public double ProgressPercent { get; set; }
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public DownloadState State { get; set; } = DownloadState.Pending;
    public string ErrorMessage { get; set; } = "";
    public CancellationTokenSource CancellationTokenSource { get; set; } = new();
}
