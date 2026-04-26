namespace TensorLay.Models;

public class ModelInfo
{
    public string ServiceId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long SizeBytes { get; set; }

    public string DisplaySize
    {
        get
        {
            const long gb = 1024L * 1024 * 1024;
            const long mb = 1024L * 1024;
            if (SizeBytes >= gb)
                return $"{SizeBytes / (double)gb:F2} GB";
            return $"{SizeBytes / (double)mb:F0} MB";
        }
    }
}
