namespace LoopIt7.ViewModels;

/// <summary>
/// One tab in the patchbay's page strip. Just identity, a name and whether it is the one on
/// screen: the boxes and cables it holds live in AppSettings and are only loaded onto the
/// canvas while this is the active page.
/// </summary>
public sealed class PatchPageViewModel : ObservableObject
{
    private string _name;
    private bool _isActive;
    private bool _isEditing;

    public PatchPageViewModel(string id, string name)
    {
        Id = id;
        _name = name;
    }

    public string Id { get; }

    /// <summary>Never allowed to go blank, the same way an Excel sheet tab cannot.</summary>
    public string Name
    {
        get => _name;
        set
        {
            string trimmed = value.Trim();
            if (trimmed.Length == 0) trimmed = _name;
            if (SetProperty(ref _name, trimmed)) Renamed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    /// <summary>True while the tab shows a text box in place of its label.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

    public event EventHandler? Renamed;
}
