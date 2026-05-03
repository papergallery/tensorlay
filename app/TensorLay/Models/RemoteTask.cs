using System.Text.Json.Serialization;

namespace TensorLay.Models;

// Wire-shape for an install request returned by GET /api/tasks/pending or
// emitted by the relay's task table. Fields use snake_case on the wire
// (matching FastAPI/pydantic), translated via [JsonPropertyName] attributes
// so we can keep PascalCase property names in C# without a global naming
// policy that would also affect AppSettings serialization elsewhere.
public class RemoteTask
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("service_id")]
    public string ServiceId { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "";

    // Agent-supplied estimate. RemoteTaskService runs a HEAD against `Url`
    // before showing the modal, populating ActualSizeMb from Content-Length
    // when the server cooperates — that takes priority in the UI when
    // present, with a "(estimated)" caption for the agent value otherwise.
    [JsonPropertyName("size_mb")]
    public double? SizeMb { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("agent_label")]
    public string AgentLabel { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "pending";

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("expires_at")]
    public DateTime ExpiresAt { get; set; }

    // Populated by RemoteTaskService after a successful HEAD preflight; not
    // serialized back to the relay. When non-null this is the truth-source
    // for the size shown in the approval modal.
    [JsonIgnore]
    public double? ActualSizeMb { get; set; }
}

// Returned by GET /api/tasks/pending. Single field, but explicit shape
// avoids relying on a top-level array which the relay doesn't emit.
public class RemoteTaskListResponse
{
    [JsonPropertyName("tasks")]
    public List<RemoteTask> Tasks { get; set; } = new();
}

public class RemoteTaskStatusUpdate
{
    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("progress_pct")]
    public double? ProgressPct { get; set; }

    [JsonPropertyName("error_msg")]
    public string? ErrorMsg { get; set; }
}
