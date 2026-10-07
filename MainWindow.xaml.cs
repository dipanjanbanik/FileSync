using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.IO.Path;

namespace FileSync
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly FolderComparisonController comparisonController;
        private ScrollViewer? sourceScrollViewer;
        private ScrollViewer? destinationScrollViewer;
        private readonly Dictionary<ScrollViewer, (double Horizontal, double Vertical)> pendingScrollOffsets = new();
        private bool scanning;
        private bool switchingPaths;
        private readonly ObservableCollection<SynchronizationLogEntry> activityLog = new();
        private CancellationTokenSource? synchronizationCancellation;
        private (int Pending, int Ongoing, int Failed, int Success, int Overwritten, int New, int Deleted, int Skipped) synchronizationCounts;

        public MainWindow()
        {
            InitializeComponent();
            ApplicationVersionTextBlock.Text = $"Version {typeof(MainWindow).Assembly.GetName().Version}";
            SynchronizationRuleComboBox.ItemsSource = SynchronizationRules.Options;
            SynchronizationRuleComboBox.SelectedIndex = 0;
            comparisonController = new FolderComparisonController(SourceFilesDataGrid, DestinationFilesDataGrid);
            ActivityLogDataGrid.ItemsSource = activityLog;
            UpdateScanButton();
        }

        private void SortComparisonTables(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true;
            var direction = e.Column.SortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending : ListSortDirection.Ascending;
            var member = e.Column.SortMemberPath;
            comparisonController.Sort(member, direction, ReferenceEquals(sender, SourceFilesDataGrid));

            foreach (var grid in new[] { SourceFilesDataGrid, DestinationFilesDataGrid })
            {
                foreach (var column in grid.Columns)
                {
                    column.SortDirection = column.SortMemberPath == member ? direction : null;
                }
            }
        }

        private void ComparisonGridLoaded(object sender, RoutedEventArgs e)
        {
            var grid = (DataGrid)sender;
            var viewer = grid.Template.FindName("DG_ScrollViewer", grid) as ScrollViewer ?? FindScrollViewer(grid);
            if (ReferenceEquals(grid, SourceFilesDataGrid))
            {
                sourceScrollViewer = viewer;
            }
            else
            {
                destinationScrollViewer = viewer;
            }

            if (LockScrollBarCheckBox.IsChecked == true)
            {
                SynchronizeScroll(sourceScrollViewer, destinationScrollViewer);
            }
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject parent)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is ScrollViewer viewer)
                {
                    return viewer;
                }

                var nested = FindScrollViewer(child);
                if (nested is not null)
                {
                    return nested;
                }
            }

            return null;
        }

        private void LockScrollBarChecked(object sender, RoutedEventArgs e)
        {
            SynchronizeScroll(sourceScrollViewer, destinationScrollViewer);
        }

        private void ComparisonGridScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.OriginalSource is not ScrollViewer viewer
                || (!ReferenceEquals(viewer, sourceScrollViewer) && !ReferenceEquals(viewer, destinationScrollViewer)))
            {
                return;
            }

            if (pendingScrollOffsets.Remove(viewer, out var pending)
                && Math.Abs(viewer.HorizontalOffset - pending.Horizontal) < 0.01
                && Math.Abs(viewer.VerticalOffset - pending.Vertical) < 0.01)
            {
                return;
            }

            if (LockScrollBarCheckBox.IsChecked != true || (e.HorizontalChange == 0 && e.VerticalChange == 0))
            {
                return;
            }

            SynchronizeScroll(viewer, ReferenceEquals(viewer, sourceScrollViewer) ? destinationScrollViewer : sourceScrollViewer);
        }

        private void SynchronizeScroll(ScrollViewer? origin, ScrollViewer? target)
        {
            if (origin is null || target is null)
            {
                return;
            }

            var horizontal = Math.Min(origin.HorizontalOffset, target.ScrollableWidth);
            var vertical = Math.Min(origin.VerticalOffset, target.ScrollableHeight);
            if (Math.Abs(target.HorizontalOffset - horizontal) < 0.01 && Math.Abs(target.VerticalOffset - vertical) < 0.01)
            {
                return;
            }

            var pending = (Horizontal: horizontal, Vertical: vertical);
            pendingScrollOffsets[target] = pending;
            target.ScrollToHorizontalOffset(horizontal);
            target.ScrollToVerticalOffset(vertical);
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (pendingScrollOffsets.TryGetValue(target, out var current) && current == pending)
                {
                    pendingScrollOffsets.Remove(target);
                }
            }));
        }

        private void FolderPathChanged(object sender, TextChangedEventArgs e)
        {
            if (switchingPaths)
            {
                return;
            }

            comparisonController?.Clear();
            UpdateScanButton();
        }

        private void UpdateScanButton()
        {
            if (ScanButton is null || StartButton is null || StopButton is null
                || SourcePathTextBox is null || DestinationPathTextBox is null
                || SynchronizationRuleComboBox is null)
            {
                return;
            }

            var busy = scanning || synchronizationCancellation is not null;
            var validPaths = Directory.Exists(SourcePathTextBox.Text.Trim())
                && Directory.Exists(DestinationPathTextBox.Text.Trim());
            ScanButton.IsEnabled = !busy && validPaths;
            StartButton.IsEnabled = !busy && validPaths;
            StopButton.IsEnabled = synchronizationCancellation is { IsCancellationRequested: false };
            SourcePathTextBox.IsEnabled = !busy;
            DestinationPathTextBox.IsEnabled = !busy;
            BrowseSourceButton.IsEnabled = !busy;
            BrowseDestinationButton.IsEnabled = !busy;
            SwitchSidesButton.IsEnabled = !busy;
            SynchronizationRuleComboBox.IsEnabled = !busy;
        }

        private void ResetSynchronizationCounters(int pending = 0)
        {
            synchronizationCounts = (pending, 0, 0, 0, 0, 0, 0, 0);
            RefreshSynchronizationCounters();
        }

        private void RefreshSynchronizationCounters()
        {
            PendingCountRun.Text = synchronizationCounts.Pending.ToString();
            OngoingCountRun.Text = synchronizationCounts.Ongoing.ToString();
            FailedCountRun.Text = synchronizationCounts.Failed.ToString();
            SuccessCountRun.Text = synchronizationCounts.Success.ToString();
            OverwrittenCountRun.Text = synchronizationCounts.Overwritten.ToString();
            NewCountRun.Text = synchronizationCounts.New.ToString();
            DeletedCountRun.Text = synchronizationCounts.Deleted.ToString();
            SkippedCountRun.Text = synchronizationCounts.Skipped.ToString();
        }

        private void AppendSynchronizationEntry(SynchronizationLogEntry entry)
        {
            activityLog.Add(entry);
            synchronizationCounts.Pending--;
            synchronizationCounts.Ongoing++;
            RefreshSynchronizationCounters();
            ActivityLogDataGrid.ScrollIntoView(entry);
        }

        private void UpdateSynchronizationEntry(SynchronizationLogEntry entry, int progress, string status, string? message)
        {
            var previousStatus = entry.Status;
            entry.Update(progress, status, message);
            if (previousStatus != "Running" || status == "Running")
            {
                return;
            }

            synchronizationCounts.Ongoing--;
            switch (status)
            {
                case "Completed":
                    synchronizationCounts.Success++;
                    switch (entry.Operation)
                    {
                        case SynchronizationOperation.New:
                            synchronizationCounts.New++;
                            break;
                        case SynchronizationOperation.Overwrite:
                            synchronizationCounts.Overwritten++;
                            break;
                        case SynchronizationOperation.Delete:
                            synchronizationCounts.Deleted++;
                            break;
                    }
                    break;
                case "Failed":
                    synchronizationCounts.Failed++;
                    break;
                case "Skipped":
                    synchronizationCounts.Skipped++;
                    break;
            }

            RefreshSynchronizationCounters();
        }

        private async void StartSynchronization(object sender, RoutedEventArgs e)
        {
            UpdateScanButton();
            if (!StartButton.IsEnabled)
            {
                return;
            }

            if (SynchronizationRuleComboBox.SelectedItem is not SynchronizationRuleOption
                { Mode: SynchronizationMode.ReplicateSource })
            {
                MessageBox.Show(this, "Only Replicate source processing is currently implemented.",
                    "Synchronization", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string source;
            string destination;
            try
            {
                source = Path.GetFullPath(SourcePathTextBox.Text.Trim());
                destination = Path.GetFullPath(DestinationPathTextBox.Text.Trim());
                FileSynchronizationService.ValidateFolders(source, destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or System.Security.SecurityException or ArgumentException or NotSupportedException)
            {
                MessageBox.Show(this, ex.Message, "Synchronization", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (MessageBox.Show(this,
                "Replicate source will copy source files, overwrite different destination files, and delete destination-only entries. Continue?",
                "Confirm synchronization", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            using var cancellation = new CancellationTokenSource();
            synchronizationCancellation = cancellation;
            activityLog.Clear();
            ResetSynchronizationCounters();
            UpdateScanButton();
            try
            {
                scanning = true;
                await comparisonController.ScanAsync(source, destination);
                scanning = false;
                cancellation.Token.ThrowIfCancellationRequested();
                var sourceRows = comparisonController.SourceRows.ToArray();
                var destinationRows = comparisonController.DestinationRows.ToArray();
                ResetSynchronizationCounters(FileSynchronizationService.GetPendingCount(sourceRows, destinationRows));
                await Task.Run(() => FileSynchronizationService.RunAsync(source, destination, sourceRows, destinationRows,
                    entry => Dispatcher.Invoke(() => AppendSynchronizationEntry(entry)),
                    (entry, progress, status, message) => Dispatcher.Invoke(() => UpdateSynchronizationEntry(entry, progress, status, message)),
                    cancellation.Token), cancellation.Token);

                if (!cancellation.IsCancellationRequested)
                {
                    scanning = true;
                    await comparisonController.ScanAsync(source, destination);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!Dispatcher.HasShutdownStarted)
                {
                    MessageBox.Show(this, ex.Message, "Synchronization", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                foreach (var entry in activityLog.Where(entry => entry.Status == "Running"))
                {
                    UpdateSynchronizationEntry(entry, entry.Progress,
                        cancellation.IsCancellationRequested ? "Cancelled" : "Failed",
                        "Synchronization ended before this entry completed.");
                }

                scanning = false;
                synchronizationCancellation = null;
                UpdateScanButton();
            }
        }

        private void StopSynchronization(object sender, RoutedEventArgs e)
        {
            synchronizationCancellation?.Cancel();
            if (scanning)
            {
                comparisonController.Clear();
            }

            UpdateScanButton();
        }

        private async void ScanFolders(object sender, RoutedEventArgs e)
        {
            UpdateScanButton();
            if (!ScanButton.IsEnabled)
            {
                return;
            }

            scanning = true;
            UpdateScanButton();
            try
            {
                await comparisonController.ScanAsync(SourcePathTextBox.Text, DestinationPathTextBox.Text);
            }
            finally
            {
                scanning = false;
                UpdateScanButton();
            }
        }

        private void BrowseSource(object sender, RoutedEventArgs e)
        {
            BrowseFolder(SourcePathTextBox, "Select source folder");
        }

        private void BrowseDestination(object sender, RoutedEventArgs e)
        {
            BrowseFolder(DestinationPathTextBox, "Select destination folder");
        }

        private void BrowseFolder(TextBox pathTextBox, string title)
        {
            var dialog = new OpenFolderDialog
            {
                Title = title,
                Multiselect = false
            };

            var currentPath = pathTextBox.Text.Trim();
            if (Directory.Exists(currentPath))
            {
                dialog.InitialDirectory = currentPath;
            }

            if (dialog.ShowDialog(this) == true)
            {
                pathTextBox.Text = dialog.FolderName;
                comparisonController.Clear();
                UpdateScanButton();
            }
        }

        private void SwitchSides(object sender, RoutedEventArgs e)
        {
            switchingPaths = true;
            try
            {
                (SourcePathTextBox.Text, DestinationPathTextBox.Text) =
                    (DestinationPathTextBox.Text, SourcePathTextBox.Text);
            }
            finally
            {
                switchingPaths = false;
            }

            comparisonController.SwitchSides();
            UpdateScanButton();
        }

        protected override void OnClosed(EventArgs e)
        {
            synchronizationCancellation?.Cancel();
            comparisonController.Dispose();
            base.OnClosed(e);
        }
    }
}