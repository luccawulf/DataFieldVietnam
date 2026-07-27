using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataField42.Interfaces;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace DataField42.ViewModels;

/// <summary>One patch as the page shows it: what it is, where it stands, and what pressing the button does.</summary>
public class GamePatchViewModel(GamePatch patch, GamePatchState state)
{
    public GamePatch Patch { get; } = patch;
    public GamePatchState State { get; } = state;

    public string Name => Patch.Name;
    public string Description => Patch.Description;

    public string StatusText => State switch
    {
        GamePatchState.Applied => "Installed",
        GamePatchState.NotApplied => "Not installed",
        GamePatchState.Outdated => "Installed, but an older version",
        GamePatchState.Partial => "Half-written - needs repairing",
        GamePatchState.UnsupportedExecutable => "Unavailable for this copy of the game",
        _ => "Unknown",
    };

    public string ActionText => State switch
    {
        GamePatchState.Applied => "Remove",
        GamePatchState.NotApplied => "Apply",
        GamePatchState.Outdated => "Update",
        GamePatchState.Partial => "Repair",
        _ => "Unavailable",
    };

    /// <summary>False only when the executable is not the build these offsets were written for.</summary>
    public bool CanToggle => State != GamePatchState.UnsupportedExecutable;
}

/// <summary>
/// Lets the player choose which modifications DataField Vietnam makes to BfVietnam.exe.
/// </summary>
/// <remarks>
/// Each entry is a whole feature rather than a single byte edit: the auto-download hook alone is five
/// edits that only work together, so applying part of one would break the game. Anything the client
/// cannot recognise as the expected executable is shown but not actionable.
/// </remarks>
public partial class PatchesViewModel : ObservableObject, IPageViewModel
{
    public string Title => "Game Patches";

    private readonly Bf1942Client _bf1942Client;
    private readonly ILogger<PatchesViewModel> _logger;

    public ObservableCollection<GamePatchViewModel> Patches { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = string.Empty;

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public PatchesViewModel(Bf1942Client bf1942Client, ILogger<PatchesViewModel> logger)
    {
        _bf1942Client = bf1942Client;
        _logger = logger;
        Refresh();
    }

    [RelayCommand]
    private void Toggle(GamePatchViewModel item)
    {
        if (!item.CanToggle)
            return;

        try
        {
            if (item.State == GamePatchState.Applied)
            {
                _logger.LogInformation($"Removing patch '{item.Patch.Id}'.");
                _bf1942Client.RevertPatch(item.Patch);
                Message = $"Removed: {item.Name}";
            }
            else
            {
                _logger.LogInformation($"Applying patch '{item.Patch.Id}' (was {item.State}).");
                _bf1942Client.ApplyPatch(item.Patch);
                Message = $"Applied: {item.Name}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to change patch '{item.Patch.Id}'.");
            Message = ex.Message;
        }

        Refresh();
    }

    private void Refresh()
    {
        Patches.Clear();
        try
        {
            foreach (var status in _bf1942Client.GetPatchStatuses())
                Patches.Add(new GamePatchViewModel(status.Patch, status.State));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read patch status from the game executable.");
            Message = $"Can't read the game executable: {ex.Message}";
        }
    }
}
