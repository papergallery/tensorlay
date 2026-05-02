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

    // Python interpreter used at install time for `<exe> <args> -m pip install -r ...`.
    // Defaults to "python" with no extra args. Override when a service pins
    // an older Python (e.g. sd-forge needs 3.10 because torch 2.3.1 has no
    // wheels for 3.13+, so we use the Windows Python Launcher: "py" + "-3.10").
    public string PythonExecutable { get; init; } = "python";
    public string PythonInterpreterArgs { get; init; } = "";
}
