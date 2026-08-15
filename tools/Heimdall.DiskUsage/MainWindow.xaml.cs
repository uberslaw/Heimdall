using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Heimdall.DiskUsage.Models;
using Heimdall.DiskUsage.Services;

namespace Heimdall.DiskUsage;

public partial class MainWindow : Window
{
    FolderNode? _scanRoot;
    CancellationTokenSource? _scanCts;
    FolderNode? _selected;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshDrives();
    }

    void RefreshDrives()
    {
        DriveCombo.Items.Clear();
        foreach (var d in DriveScanner.GetLocalDrives())
        {
            var label = $"{d.Name}  ({FolderNode.FormatSize(d.TotalSize)} total, {FolderNode.FormatSize(d.AvailableFreeSpace)} free)";
            DriveCombo.Items.Add(new DriveItem(d.RootDirectory.FullName, label));
        }

        if (DriveCombo.Items.Count > 0)
            DriveCombo.SelectedIndex = 0;
    }

    async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (DriveCombo.SelectedItem is not DriveItem drive)
        {
            MessageBox.Show(this, "Pick a local drive first.", "Disk Usage",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await CancelScanAsync();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        var excludeSystem = ExcludeSystemCheck.IsChecked == true;

        ScanButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        FolderTree.ItemsSource = null;
        _scanRoot = null;
        _selected = null;
        StatusText.Text = "Starting scan…";
        ProgressBar.IsIndeterminate = true;

        var progress = new Progress<DriveScanner.Progress>(p =>
        {
            StatusText.Text =
                $"{p.Message}  ·  {FolderNode.FormatSize(p.BytesSeen)} in {p.FilesSeen:N0} files / {p.FoldersSeen:N0} folders";
        });

        try
        {
            var root = await Task.Run(
                () => DriveScanner.Scan(drive.RootPath, excludeSystem, progress, ct),
                ct);

            _scanRoot = root;
            ApplyFilterAndBind();
            root.IsExpanded = true;
            StatusText.Text =
                $"Scan complete — {FolderNode.FormatSize(root.SizeBytes)} under {root.Name} ({root.Children.Count} top folders)";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Scan failed.";
            MessageBox.Show(this, ex.Message, "Scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = 0;
            ScanButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    async void CancelButton_Click(object sender, RoutedEventArgs e) => await CancelScanAsync();

    async Task CancelScanAsync()
    {
        if (_scanCts is null) return;
        _scanCts.Cancel();
        try { await Task.Delay(50); } catch { /* ignore */ }
        _scanCts.Dispose();
        _scanCts = null;
    }

    void ApplyFilter_Click(object sender, RoutedEventArgs e) => ApplyFilterAndBind();

    void ApplyFilterAndBind()
    {
        if (_scanRoot is null)
        {
            FolderTree.ItemsSource = null;
            return;
        }

        // Blank min/max = no filter (null). Only apply when the box has a parseable number.
        var min = ParseSizeBox(MinSizeBox.Text, MinSizeUnit.SelectedItem as ComboBoxItem);
        var max = ParseSizeBox(MaxSizeBox.Text, MaxSizeUnit.SelectedItem as ComboBoxItem);

        // If both blank, skip filter work and show every child (fast path).
        if (min is null && max is null)
        {
            ShowAllChildren(_scanRoot);
            FolderTree.ItemsSource = _scanRoot.VisibleChildren;
            return;
        }

        TreeFilter.Apply(_scanRoot, min, max);
        FolderTree.ItemsSource = _scanRoot.VisibleChildren;
    }

    static void ShowAllChildren(FolderNode node)
    {
        node.VisibleChildren.Clear();
        foreach (var child in node.Children)
        {
            ShowAllChildren(child);
            node.VisibleChildren.Add(child);
        }
        node.IsVisible = true;
    }

    /// <summary>Blank or whitespace → null (no bound). Invalid text → null (treat as no bound).</summary>
    static long? ParseSizeBox(string? text, ComboBoxItem? unitItem)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.CurrentCulture, out var amount)
            && !double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out amount))
            return null;
        if (amount < 0) return null;

        var unit = (unitItem?.Content?.ToString() ?? "MB").Trim().ToUpperInvariant();
        var bytes = unit switch
        {
            "B" => amount,
            "KB" => amount * 1024d,
            "GB" => amount * 1024d * 1024d * 1024d,
            "TB" => amount * 1024d * 1024d * 1024d * 1024d,
            _ => amount * 1024d * 1024d // MB
        };
        return (long)Math.Round(bytes);
    }

    void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selected = e.NewValue as FolderNode;
        SelectionText.Text = _selected is null
            ? ""
            : $"{_selected.FullPath}  [{FolderNode.FormatSize(_selected.SizeBytes)}]";
    }

    void FolderTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_selected is null) return;
        OpenInExplorer(_selected.FullPath);
    }

    void FolderTree_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _selected is not null)
        {
            e.Handled = true;
            DeleteSelected();
        }
        else if (e.Key == Key.Enter && _selected is not null)
        {
            e.Handled = true;
            OpenInExplorer(_selected.FullPath);
        }
    }

    void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not null)
            OpenInExplorer(_selected.FullPath);
    }

    void DeleteButton_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    void OpenInExplorer(string path)
    {
        try
        {
            var fsPath = DriveScanner.ToFileSystemPath(path);
            if (!Directory.Exists(fsPath))
            {
                MessageBox.Show(this, $"Folder no longer exists:\n{fsPath}", "Open folder",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{fsPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void DeleteSelected()
    {
        if (_selected is null) return;

        var path = _selected.FullPath;
        var name = _selected.Name;

        if (RecycleBinService.IsDriveRoot(path) || _selected == _scanRoot)
        {
            MessageBox.Show(this, "Cannot delete a drive root.", "Delete",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(
            this,
            $"Send this folder to the Recycle Bin?\n\n{path}\n\n[{FolderNode.FormatSize(_selected.SizeBytes)}]",
            "Delete folder",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            RecycleBinService.SendDirectoryToRecycleBin(path);

            // Remove from in-memory tree and re-bind
            var parent = _selected.Parent;
            if (parent is not null)
            {
                SubtractSizeUp(parent, _selected.SizeBytes, _selected.FileCount);
                parent.Children.Remove(_selected);
            }
            else if (_scanRoot is not null && _selected != _scanRoot)
            {
                _scanRoot.Children.Remove(_selected);
            }

            _selected = null;
            ApplyFilterAndBind();
            StatusText.Text = $"Sent to Recycle Bin: {name}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    static void SubtractSizeUp(FolderNode from, long bytes, int files)
    {
        for (var n = from; n is not null; n = n.Parent)
        {
            n.SizeBytes = Math.Max(0, n.SizeBytes - bytes);
            // FileCount on ancestors is not strictly tracked for descendants; leave as-is.
        }
    }

    sealed record DriveItem(string RootPath, string Label)
    {
        public override string ToString() => Label;
    }
}
