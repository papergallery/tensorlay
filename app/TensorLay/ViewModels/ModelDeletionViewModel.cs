using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TensorLay.Models;
using TensorLay.Services;

namespace TensorLay.ViewModels;

// One row in ModelDeletionWindow: a requested file after preflight, with a
// checkbox so the user can approve a subset.
public partial class DeletionRowViewModel : ObservableObject
{
    public DeletionRowViewModel(DeletionCandidate candidate)
    {
        Candidate = candidate;
        _isSelected = candidate.CanDelete;
    }

    public DeletionCandidate Candidate { get; }

    public bool CanDelete => Candidate.CanDelete;
    public string ServiceName => Candidate.ServiceName;

    public string FileName => Candidate.IsOllama
        ? Candidate.Item.Path
        : Candidate.Item.Path[(Candidate.Item.Path.LastIndexOf('/') + 1)..];

    // Folder inside the models root ("loras", "insightface/models/antelopev2"),
    // or "models" for files directly in the root. Ollama has no folders.
    public string Folder
    {
        get
        {
            if (Candidate.IsOllama) return "model";
            int slash = Candidate.Item.Path.LastIndexOf('/');
            return slash < 0 ? "models" : Candidate.Item.Path[..slash];
        }
    }

    public string Location => Candidate.FullPath ?? Candidate.Item.Path;
    public string SizeDisplay => Candidate.CanDelete ? ModelFiles.FormatBytes(Candidate.SizeBytes) : "";
    public string Problem => Candidate.Problem ?? "";
    public bool HasProblem => Candidate.Problem is not null;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private bool _resultIsError;
    [ObservableProperty] private bool _hasResult;
}

// Drives ModelDeletionWindow for both a remote cleanup request from the relay
// (agent label, reason, Reject, result reported back) and a local Delete click
// on the Models page (single item, no relay). Items are preflighted against
// the disk before anything is shown as deletable.
public partial class ModelDeletionViewModel : ViewModelBase
{
    private readonly ModelDeletionService _deletionService;
    private readonly RemoteCleanupService? _cleanupService;
    private readonly RemoteDeletionRequest? _request;
    private readonly List<RemoteDeletionItem> _items;
    private HashSet<string> _touchedServices = new();

    private ModelDeletionViewModel(
        ModelDeletionService deletionService,
        RemoteCleanupService? cleanupService,
        RemoteDeletionRequest? request,
        List<RemoteDeletionItem> items)
    {
        _deletionService = deletionService;
        _cleanupService = cleanupService;
        _request = request;
        _items = items;

        IsRemote = request is not null;
        WindowTitle = IsRemote ? "Remote Cleanup Request" : "Delete Model";
        AgentLabel = request?.AgentLabel ?? "";
        Reason = request?.Reason ?? "";
        HasReason = !string.IsNullOrWhiteSpace(Reason);
        ExpiresDisplay = request?.ExpiresAt.ToLocalTime().ToString("g") ?? "";
        DeleteButtonText = IsRemote ? "Delete selected" : "Delete";
        DismissButtonText = IsRemote ? "Reject" : "Cancel";
    }

    public static ModelDeletionViewModel ForRemoteRequest(
        ModelDeletionService deletionService, RemoteCleanupService cleanupService, RemoteDeletionRequest request)
        => new(deletionService, cleanupService, request, request.Items);

    public static ModelDeletionViewModel ForLocalModel(ModelDeletionService deletionService, ModelInfo model)
        => new(deletionService, null, null, new List<RemoteDeletionItem>
        {
            new() { ServiceId = model.ServiceId, Path = model.RelativePath, SizeBytes = model.SizeBytes },
        });

    public ObservableCollection<DeletionRowViewModel> Rows { get; } = new();

    public bool IsRemote { get; }
    public string WindowTitle { get; }
    public string AgentLabel { get; }
    public string Reason { get; }
    public bool HasReason { get; }
    public string ExpiresDisplay { get; }
    public string DeleteButtonText { get; }
    public string DismissButtonText { get; }

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private bool _isFinished;
    [ObservableProperty] private string _summary = "Checking files…";
    [ObservableProperty] private string _statusMessage = "";

    // Decision buttons are visible until the user has acted.
    public bool ShowDecisionButtons => !IsWorking && !IsFinished;

    // Raised once the window should close (Reject, Cancel, or Close after
    // deletion). Carries the ids of services whose files were touched so the
    // caller can refresh their model lists (empty when nothing was deleted).
    public event Action<IReadOnlyCollection<string>>? Finished;

    partial void OnIsWorkingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDecisionButtons));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        DismissCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsFinishedChanged(bool value) => OnPropertyChanged(nameof(ShowDecisionButtons));

    partial void OnIsLoadingChanged(bool value) => DeleteSelectedCommand.NotifyCanExecuteChanged();

    public async Task InitializeAsync()
    {
        List<DeletionCandidate> candidates;
        try
        {
            candidates = await _deletionService.PreflightAsync(_items).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            IsLoading = false;
            Summary = $"Could not check the files: {ex.Message}";
            return;
        }
        foreach (var candidate in candidates.OrderBy(c => c.CanDelete ? 0 : 1).ThenByDescending(c => c.SizeBytes))
        {
            var row = new DeletionRowViewModel(candidate);
            row.PropertyChanged += OnRowPropertyChanged;
            Rows.Add(row);
        }
        IsLoading = false;
        UpdateSummary();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeletionRowViewModel.IsSelected))
            UpdateSummary();
    }

    private void UpdateSummary()
    {
        var selected = Rows.Where(r => r.IsSelected && r.CanDelete).ToList();
        int deletable = Rows.Count(r => r.CanDelete);
        long bytes = selected.Sum(r => r.Candidate.SizeBytes);
        Summary = deletable == 0
            ? "Nothing here can be deleted."
            : $"{selected.Count} of {deletable} selected · {ModelFiles.FormatBytes(bytes)} will be freed";
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectAll(string? mode)
    {
        bool value = mode != "none";
        foreach (var row in Rows.Where(r => r.CanDelete))
            row.IsSelected = value;
    }

    private bool CanDeleteSelected() => !IsLoading && !IsWorking && !IsFinished && Rows.Any(r => r.IsSelected && r.CanDelete);

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task DeleteSelected()
    {
        if (_request is not null && _request.ExpiresAt <= DateTime.UtcNow)
        {
            StatusMessage = "This request has expired — nothing was deleted. Ask the agent to send it again.";
            IsFinished = true;
            return;
        }

        IsWorking = true;
        StatusMessage = "Deleting…";

        var results = new List<RemoteDeletionItemResult>();
        var touchedServices = new HashSet<string>();
        long freed = 0;
        int failed = 0;

        foreach (var row in Rows)
        {
            RemoteDeletionItemResult result;
            if (row.CanDelete && !row.IsSelected)
            {
                result = new RemoteDeletionItemResult
                {
                    ServiceId = row.Candidate.Item.ServiceId,
                    Path = row.Candidate.Item.Path,
                    Status = "skipped",
                    Error = "unticked by the user",
                };
            }
            else
            {
                // Rows that failed preflight still go through DeleteAsync so
                // the agent gets a per-item "missing"/"error" with the reason.
                result = await _deletionService.DeleteAsync(row.Candidate).ConfigureAwait(true);
            }
            results.Add(result);

            switch (result.Status)
            {
                case "deleted":
                    freed += result.FreedBytes;
                    touchedServices.Add(result.ServiceId);
                    row.ResultText = "Deleted";
                    break;
                case "missing":
                    row.ResultText = "Already gone";
                    break;
                case "skipped":
                    row.ResultText = "Kept";
                    break;
                default:
                    failed++;
                    row.ResultText = $"Not deleted: {result.Error}";
                    row.ResultIsError = true;
                    break;
            }
            row.HasResult = true;
        }

        StatusMessage = failed == 0
            ? $"Freed {ModelFiles.FormatBytes(freed)}."
            : $"Freed {ModelFiles.FormatBytes(freed)}. {failed} item(s) could not be deleted — see the list.";

        _touchedServices = touchedServices;
        IsWorking = false;
        IsFinished = true;

        // Report after the UI is released: the files are already gone, and a
        // slow relay (the post retries) must not hold the window hostage.
        if (_request is not null && _cleanupService is not null)
        {
            _cleanupService.MarkInventoryDirty();
            bool reported = await _cleanupService.PostResultAsync(_request.Id, new RemoteDeletionResult
            {
                State = "completed",
                Results = results,
            }).ConfigureAwait(true);
            if (!reported)
                StatusMessage += " (Could not report the result to the VPS agent.)";
        }
    }

    private bool CanDismiss() => !IsWorking;

    // Reject (remote) / Cancel (local) before deciding, or Close afterwards.
    [RelayCommand(CanExecute = nameof(CanDismiss))]
    private void Dismiss()
    {
        if (!IsFinished && _request is not null && _cleanupService is not null)
        {
            // Nothing in the request could be deleted (all gone, invalid, or
            // Ollama down): tell the agent why per item instead of "rejected".
            bool nothingDeletable = !IsLoading && Rows.Count > 0 && !Rows.Any(r => r.CanDelete);
            _ = _cleanupService.PostResultAsync(_request.Id, nothingDeletable
                ? new RemoteDeletionResult
                {
                    State = "failed",
                    ErrorMsg = "Nothing in the request could be deleted.",
                    Results = Rows.Select(r => new RemoteDeletionItemResult
                    {
                        ServiceId = r.Candidate.Item.ServiceId,
                        Path = r.Candidate.Item.Path,
                        Status = r.Candidate.IsMissing ? "missing" : "error",
                        Error = r.Candidate.IsMissing ? null : r.Candidate.Problem,
                    }).ToList(),
                }
                : new RemoteDeletionResult
                {
                    State = "rejected",
                    ErrorMsg = "Rejected by the user.",
                });
        }
        Finished?.Invoke(_touchedServices);
    }
}
