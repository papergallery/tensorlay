using System.Text.Json.Serialization;

namespace TensorLay.Models;

// Wire shapes for relay v1.6.0 remote model cleanup: deletion requests the
// agent queues (GET /api/deletions/pending), the result the desktop reports
// back (POST /api/deletions/{id}/result) and the inventory snapshot it pushes
// (PUT /api/inventory). snake_case on the wire via [JsonPropertyName], same
// convention as RemoteTask.

public class RemoteDeletionItem
{
    [JsonPropertyName("service_id")]
    public string ServiceId { get; set; } = "";

    // Forward-slash path relative to the service's models root
    // (ModelFiles.GetModelsRoot); for Ollama the model tag.
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    // Agent's claim, informational only — the approval window shows the size
    // measured on disk.
    [JsonPropertyName("size_bytes")]
    public long? SizeBytes { get; set; }
}

public class RemoteDeletionRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "pending";

    [JsonPropertyName("items")]
    public List<RemoteDeletionItem> Items { get; set; } = new();

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("agent_label")]
    public string AgentLabel { get; set; } = "";

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("expires_at")]
    public DateTime ExpiresAt { get; set; }
}

public class RemoteDeletionListResponse
{
    [JsonPropertyName("requests")]
    public List<RemoteDeletionRequest> Requests { get; set; } = new();
}

public class RemoteDeletionItemResult
{
    [JsonPropertyName("service_id")]
    public string ServiceId { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    // deleted | missing | skipped | error — see DELETE_ITEM_STATUSES in relay.py.
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("freed_bytes")]
    public long FreedBytes { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

public class RemoteDeletionResult
{
    // completed | failed | rejected
    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("error_msg")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMsg { get; set; }

    [JsonPropertyName("results")]
    public List<RemoteDeletionItemResult> Results { get; set; } = new();
}

public class ModelInventory
{
    [JsonPropertyName("generated_at")]
    public DateTime GeneratedAt { get; set; }

    [JsonPropertyName("app_version")]
    public string AppVersion { get; set; } = "";

    [JsonPropertyName("install_directory")]
    public string InstallDirectory { get; set; } = "";

    [JsonPropertyName("disk")]
    public InventoryDisk? Disk { get; set; }

    [JsonPropertyName("services")]
    public List<InventoryService> Services { get; set; } = new();
}

public class InventoryDisk
{
    [JsonPropertyName("root")]
    public string Root { get; set; } = "";

    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("free_bytes")]
    public long FreeBytes { get; set; }
}

public class InventoryService
{
    [JsonPropertyName("service_id")]
    public string ServiceId { get; set; } = "";

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "";

    // False when the models folder doesn't exist or Ollama isn't reachable;
    // `note` then says why and `files` is empty.
    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("models_root")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelsRoot { get; set; }

    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }

    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("files")]
    public List<InventoryFile> Files { get; set; } = new();
}

public class InventoryFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("modified_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ModifiedAt { get; set; }
}
