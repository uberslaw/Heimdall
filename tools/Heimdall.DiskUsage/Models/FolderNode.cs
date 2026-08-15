using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Heimdall.DiskUsage.Models;

/// <summary>One directory in the scanned tree. SizeBytes = files in this folder + all descendants.</summary>
public sealed class FolderNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isVisible = true;

    public required string FullPath { get; init; }
    public required string Name { get; init; }
    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (_sizeBytes == value) return;
            _sizeBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SizeLabel));
            OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public long OwnFilesBytes { get; set; }
    public int FileCount { get; set; }
    public FolderNode? Parent { get; init; }
    public List<FolderNode> Children { get; } = [];

    /// <summary>Children after size sort + active filters (bound to TreeView).</summary>
    public ObservableCollection<FolderNode> VisibleChildren { get; } = [];

    public string DisplayLabel => $"{Name}  [{FormatSize(SizeBytes)}]";

    public string SizeLabel => $"[{FormatSize(SizeBytes)}]";

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public bool IsVisible
    {
        get => _isVisible;
        set { if (_isVisible != value) { _isVisible = value; OnPropertyChanged(); } }
    }

    long _sizeBytes;

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0.#} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:0.#} MB";
        double gb = mb / 1024.0;
        if (gb < 1024) return $"{gb:0.##} GB";
        return $"{gb / 1024.0:0.##} TB";
    }
}
