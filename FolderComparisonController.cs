using System.ComponentModel;
using System.IO;
using System.Security;
using System.Windows.Controls;

namespace FileSync
{
    internal sealed class FolderComparisonController : IDisposable
    {
        private readonly DataGrid sourceGrid;
        private readonly DataGrid destinationGrid;
        private CancellationTokenSource? scanCancellation;
        private Comparison? latestComparison;
        private string sortMember = nameof(FolderComparisonRow.RelativePath);
        private ListSortDirection sortDirection = ListSortDirection.Ascending;
        private bool sortFromSource = true;
        private bool disposed;

        public IReadOnlyList<FolderComparisonRow> SourceRows => latestComparison?.Source ?? [];
        public IReadOnlyList<FolderComparisonRow> DestinationRows => latestComparison?.Destination ?? [];

        public FolderComparisonController(DataGrid sourceGrid, DataGrid destinationGrid)
        {
            this.sourceGrid = sourceGrid;
            this.destinationGrid = destinationGrid;
        }

        public void Clear()
        {
            CancelScan();
            latestComparison = null;
            sourceGrid.ItemsSource = null;
            destinationGrid.ItemsSource = null;
        }

        public void SwitchSides()
        {
            CancelScan();
            if (latestComparison is not { } comparison)
            {
                return;
            }

            latestComparison = new Comparison(
                comparison.Destination.Select(row => row.FullPath is null && row.Status == "Missing"
                    ? row with { Message = "This entry is missing from the source folder." } : row).ToList(),
                comparison.Source.Select(row => row.FullPath is null && row.Status == "Missing"
                    ? row with { Message = "This entry is missing from the destination folder." } : row).ToList());
            ApplyComparison();
        }

        public void Sort(string member, ListSortDirection direction, bool fromSource)
        {
            sortMember = member;
            sortDirection = direction;
            sortFromSource = fromSource;
            ApplyComparison();
        }

        private void ApplyComparison()
        {
            if (latestComparison is not { } comparison)
            {
                return;
            }

            var selectedSource = (sourceGrid.SelectedItem as FolderComparisonRow)?.RelativePath;
            var selectedDestination = (destinationGrid.SelectedItem as FolderComparisonRow)?.RelativePath;
            var primary = sortFromSource ? comparison.Source : comparison.Destination;
            var secondary = sortFromSource ? comparison.Destination : comparison.Source;
            if (primary.Count == 0)
            {
                primary = secondary;
            }

            var counterparts = secondary.ToDictionary(row => row.RelativePath, StringComparer.OrdinalIgnoreCase);
            var comparer = Comparer<FolderComparisonRow>.Create((left, right) =>
            {
                var leftValue = left.FullPath is null && sortMember != nameof(FolderComparisonRow.Status)
                    ? counterparts.GetValueOrDefault(left.RelativePath, left) : left;
                var rightValue = right.FullPath is null && sortMember != nameof(FolderComparisonRow.Status)
                    ? counterparts.GetValueOrDefault(right.RelativePath, right) : right;
                var result = sortMember switch
                {
                    nameof(FolderComparisonRow.SizeBytes) => Nullable.Compare(leftValue.SizeBytes, rightValue.SizeBytes),
                    nameof(FolderComparisonRow.LastWriteTimeUtc) => Nullable.Compare(leftValue.LastWriteTimeUtc, rightValue.LastWriteTimeUtc),
                    nameof(FolderComparisonRow.EntryType) => StringComparer.OrdinalIgnoreCase.Compare(leftValue.EntryType, rightValue.EntryType),
                    nameof(FolderComparisonRow.Status) => StringComparer.OrdinalIgnoreCase.Compare(leftValue.Status, rightValue.Status),
                    _ => StringComparer.OrdinalIgnoreCase.Compare(leftValue.RelativePath, rightValue.RelativePath)
                };
                if (result != 0)
                {
                    return sortDirection == ListSortDirection.Ascending ? result : -result;
                }

                return StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath);
            });
            var order = primary.OrderBy(row => row, comparer)
                .Select((row, index) => (row.RelativePath, Index: index))
                .ToDictionary(item => item.RelativePath, item => item.Index, StringComparer.OrdinalIgnoreCase);
            var sourceRows = comparison.Source.OrderBy(row => order[row.RelativePath]).ToList();
            var destinationRows = comparison.Destination.OrderBy(row => order[row.RelativePath]).ToList();
            sourceGrid.ItemsSource = sourceRows;
            destinationGrid.ItemsSource = destinationRows;
            sourceGrid.SelectedItem = sourceRows.FirstOrDefault(row => row.RelativePath == selectedSource);
            destinationGrid.SelectedItem = destinationRows.FirstOrDefault(row => row.RelativePath == selectedDestination);
        }

        public async Task ScanAsync(string sourcePath, string destinationPath)
        {
            if (disposed)
            {
                return;
            }

            Clear();
            var cancellation = new CancellationTokenSource();
            scanCancellation = cancellation;
            var token = cancellation.Token;
            var source = sourcePath.Trim();
            var destination = destinationPath.Trim();

            try
            {
                var comparison = await Task.Run(() => CompareFolders(source, destination, token), token).ConfigureAwait(false);
                await sourceGrid.Dispatcher.InvokeAsync(() =>
                {
                    if (disposed || token.IsCancellationRequested)
                    {
                        return;
                    }

                    latestComparison = comparison;
                    ApplyComparison();
                });
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (!sourceGrid.Dispatcher.HasShutdownStarted)
                {
                    try
                    {
                        await sourceGrid.Dispatcher.InvokeAsync(() =>
                        {
                            if (ReferenceEquals(scanCancellation, cancellation))
                            {
                                scanCancellation = null;
                            }
                        });
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                cancellation.Dispose();
            }
        }

        private static Comparison CompareFolders(string sourcePath, string destinationPath, CancellationToken token)
        {
            var source = ScanFolder(sourcePath, token);
            var destination = ScanFolder(destinationPath, token);
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
            {
                return new Comparison(
                    source.Entries.Values.OrderBy(row => row.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
                    destination.Entries.Values.OrderBy(row => row.RelativePath, StringComparer.OrdinalIgnoreCase).ToList());
            }

            var sourceRows = new List<FolderComparisonRow>();
            var destinationRows = new List<FolderComparisonRow>();
            var paths = source.Entries.Keys.Union(destination.Entries.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                source.Entries.TryGetValue(path, out var sourceRow);
                destination.Entries.TryGetValue(path, out var destinationRow);
                var unknown = sourceRow?.CanCompare == false || destinationRow?.CanCompare == false
                    || (sourceRow is null && IsUnavailable(source, path))
                    || (destinationRow is null && IsUnavailable(destination, path));
                var status = unknown ? "Not compared"
                    : sourceRow is null || destinationRow is null ? "Missing"
                    : sourceRow.IsFolder != destinationRow.IsFolder ? "Type differs"
                    : !sourceRow.IsFolder && (sourceRow.SizeBytes != destinationRow.SizeBytes
                        || sourceRow.LastWriteTimeUtc != destinationRow.LastWriteTimeUtc) ? "Different"
                    : "Matched";
                var mismatch = status is "Missing" or "Type differs" or "Different";
                sourceRows.Add(CreateComparedRow(sourceRow, path, status, mismatch, "source"));
                destinationRows.Add(CreateComparedRow(destinationRow, path, status, mismatch, "destination"));
            }

            return new Comparison(sourceRows, destinationRows);
        }

        private static FolderComparisonRow CreateComparedRow(FolderComparisonRow? row, string path, string status, bool mismatch, string side)
        {
            if (row is null)
            {
                return new FolderComparisonRow
                {
                    RelativePath = path,
                    Status = status == "Not compared" ? status : "Missing",
                    IsMismatch = mismatch,
                    Message = status == "Not compared" ? "The containing folder could not be scanned completely."
                        : $"This entry is missing from the {side} folder."
                };
            }

            return row with
            {
                Status = status == "Missing" ? "Only here" : status,
                IsMismatch = mismatch,
                Message = row.Message ?? (status == "Different" ? "File size or exact UTC modified timestamp differs."
                    : status == "Type differs" ? "One side is a file and the other is a folder."
                    : status == "Missing" ? "The corresponding entry is missing on the other side." : row.FullPath)
            };
        }

        private static bool IsUnavailable(Snapshot snapshot, string path)
        {
            return snapshot.UnavailableFolders.Any(folder => folder == "."
                || string.Equals(folder, path, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }

        private static Snapshot ScanFolder(string path, CancellationToken token)
        {
            var snapshot = new Snapshot();
            if (string.IsNullOrWhiteSpace(path))
            {
                return snapshot;
            }

            var pending = new Stack<(string FullPath, string RelativePath)>();
            try
            {
                var root = Path.GetFullPath(path);
                var attributes = File.GetAttributes(root);
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    throw new IOException("The selected path is not a folder.");
                }

                snapshot.Entries["."] = ReadEntry(root, ".", attributes);
                pending.Push((root, "."));
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                MarkUnavailable(snapshot, path, ".", ex.Message);
            }

            while (pending.TryPop(out var folder))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    foreach (var entryPath in Directory.EnumerateFileSystemEntries(folder.FullPath))
                    {
                        token.ThrowIfCancellationRequested();
                        var relativePath = folder.RelativePath == "." ? Path.GetFileName(entryPath)
                            : Path.Combine(folder.RelativePath, Path.GetFileName(entryPath));
                        try
                        {
                            var attributes = File.GetAttributes(entryPath);
                            var entry = ReadEntry(entryPath, relativePath, attributes);
                            snapshot.Entries[relativePath] = entry;
                            if (entry.IsFolder)
                            {
                                if ((attributes & FileAttributes.ReparsePoint) != 0)
                                {
                                    snapshot.UnavailableFolders.Add(relativePath);
                                    snapshot.Entries[relativePath] = entry with
                                    {
                                        CanCompare = false,
                                        Message = "Directory link listed but not traversed to prevent cycles."
                                    };
                                }
                                else
                                {
                                    pending.Push((entryPath, relativePath));
                                }
                            }
                        }
                        catch (Exception ex) when (IsFileSystemError(ex))
                        {
                            MarkUnavailable(snapshot, entryPath, relativePath, ex.Message);
                        }
                    }
                }
                catch (Exception ex) when (IsFileSystemError(ex))
                {
                    MarkUnavailable(snapshot, folder.FullPath, folder.RelativePath, ex.Message);
                }
            }

            return snapshot;
        }

        private static FolderComparisonRow ReadEntry(string fullPath, string relativePath, FileAttributes attributes)
        {
            var isFolder = (attributes & FileAttributes.Directory) != 0;
            FileSystemInfo info = isFolder ? new DirectoryInfo(fullPath) : new FileInfo(fullPath);
            info.Refresh();
            return new FolderComparisonRow
            {
                RelativePath = relativePath,
                FullPath = info.FullName,
                IsFolder = isFolder,
                SizeBytes = info is FileInfo file ? file.Length : null,
                LastWriteTimeUtc = info.LastWriteTimeUtc
            };
        }

        private static void MarkUnavailable(Snapshot snapshot, string fullPath, string relativePath, string message)
        {
            snapshot.UnavailableFolders.Add(relativePath);
            snapshot.Entries.TryGetValue(relativePath, out var previous);
            snapshot.Entries[relativePath] = (previous ?? new FolderComparisonRow
            {
                RelativePath = relativePath,
                FullPath = fullPath,
                IsFolder = relativePath == "."
            }) with { CanCompare = false, Status = "Unavailable", Message = message };
        }

        private static bool IsFileSystemError(Exception ex)
        {
            return ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException;
        }

        private void CancelScan()
        {
            scanCancellation?.Cancel();
            scanCancellation = null;
        }

        public void Dispose()
        {
            disposed = true;
            CancelScan();
        }

        private sealed class Snapshot
        {
            public Dictionary<string, FolderComparisonRow> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> UnavailableFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed record Comparison(List<FolderComparisonRow> Source, List<FolderComparisonRow> Destination);
    }
}
