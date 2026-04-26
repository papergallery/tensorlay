namespace TensorLay.Models;

public class ServiceDefinition
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public int Port { get; init; }
    public int VramMb { get; init; }
    public string Category { get; init; } = "";
    public string HealthEndpoint { get; init; } = "";
    public string GitRepoUrl { get; init; } = "";
    public string RelativeInstallPath { get; init; } = "";
    public string StartExecutable { get; init; } = "";
    public string StartArguments { get; init; } = "";
    public string ModelsSubfolder { get; init; } = "";
    public bool UseSystemInstaller { get; init; }
}
