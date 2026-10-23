namespace TensorLay.Models;

public class GpuInfo
{
    public string GpuName { get; set; } = "";
    public int VramUsedMb { get; set; }
    public int VramTotalMb { get; set; }
    public int VramFreeMb => VramTotalMb - VramUsedMb;
    public float TemperatureCelsius { get; set; }
    public float GpuUtilPercent { get; set; }
    public float PowerDrawWatts { get; set; }
    public float PowerLimitWatts { get; set; }
    public int RamUsedMb { get; set; }
    public int RamTotalMb { get; set; }
}
