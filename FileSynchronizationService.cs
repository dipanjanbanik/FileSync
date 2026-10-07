using System.IO;
using System.Security;

namespace FileSync
{
    internal static class FileSynchronizationService
    {
        public static void ValidateFolders(string source, string destination)
        {
            source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
            destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
            if (!Directory.Exists(source) || !Directory.Exists(destination))
            {
                throw new IOException("Both folders must exist.");
            }

            if (IsWithin(source, destination) || IsWithin(destination, source))
            {
                throw new IOException("Source and destination must be separate, non-overlapping folders.");
            }

            EnsureNoLinks(source);
            EnsureNoLinks(destination);
        }

        private static bool IsWithin(string path, string root) =>
            string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);

        private static void EnsureNoLinks(string path)
        {
            for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current))
                    && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"Symbolic links and junctions are not supported: {current}");
                }
            }
        }

        public static int GetPendingCount(IReadOnlyList<FolderComparisonRow> sourceRows,
            IReadOnlyList<FolderComparisonRow> destinationRows) => GetWork(sourceRows, destinationRows).Count();

        private static IEnumerable<(FolderComparisonRow Row, bool Delete)> GetWork(
            IReadOnlyList<FolderComparisonRow> sourceRows, IReadOnlyList<FolderComparisonRow> destinationRows) =>
            sourceRows.Where(row => row.FullPath is not null && row.RelativePath != ".")
                .OrderBy(row => row.IsFolder ? 0 : 1)
                .ThenBy(row => row.RelativePath.Count(character => character == Path.DirectorySeparatorChar))
                .Select(row => (Row: row, Delete: false))
                .Concat(destinationRows.Where(row => row.Status == "Only here" && row.RelativePath != ".")
                    .OrderByDescending(row => row.RelativePath.Count(character => character == Path.DirectorySeparatorChar))
                    .ThenBy(row => row.IsFolder ? 1 : 0)
                    .Select(row => (Row: row, Delete: true)));

        public static async Task RunAsync(string source, string destination,
            IReadOnlyList<FolderComparisonRow> sourceRows, IReadOnlyList<FolderComparisonRow> destinationRows,
            Action<SynchronizationLogEntry> append, Action<SynchronizationLogEntry, int, string, string?> update,
            CancellationToken token)
        {
            ValidateFolders(source, destination);
            var destinations = destinationRows.ToDictionary(row => row.RelativePath, StringComparer.OrdinalIgnoreCase);
            foreach (var item in GetWork(sourceRows, destinationRows))
            {
                token.ThrowIfCancellationRequested();
                var row = item.Row;
                var target = Path.GetFullPath(Path.Combine(destination, row.RelativePath));
                if (!IsWithin(target, Path.GetFullPath(destination)))
                {
                    throw new IOException("An entry is outside the destination folder.");
                }

                var rule = item.Delete ? "Only here on Destination — Delete on Destination"
                    : row.Status == "Only here" ? "Missing on Destination — Copy from Source"
                    : row.Status == "Different" ? "Different on Destination — Overwrite on Destination"
                    : row.Status == "Matched" ? "Matched — Skip"
                    : row.Status == "Type differs" ? "Type differs — Skip and report conflict"
                    : "Not compared — Skip and report error";
                var entry = new SynchronizationLogEntry
                {
                    FileName = item.Delete ? target : row.FullPath!,
                    Rule = rule,
                    Operation = item.Delete ? SynchronizationOperation.Delete : row.Status switch
                    {
                        "Only here" => SynchronizationOperation.New,
                        "Different" => SynchronizationOperation.Overwrite,
                        "Matched" => SynchronizationOperation.Skip,
                        _ => SynchronizationOperation.None
                    }
                };
                append(entry);
                var progress = 0;
                try
                {
                    if (!row.CanCompare || row.Status == "Not compared")
                    {
                        throw new IOException(row.Message ?? "The entry could not be compared safely.");
                    }

                    if (row.Status == "Type differs")
                    {
                        throw new IOException("One side is a file and the other is a folder; no changes were made.");
                    }

                    if (row.Status == "Matched")
                    {
                        update(entry, 100, "Skipped", null);
                        continue;
                    }

                    EnsureNoLinks(target);
                    token.ThrowIfCancellationRequested();
                    if (item.Delete)
                    {
                        if (row.IsFolder)
                        {
                            Directory.Delete(target, false);
                        }
                        else
                        {
                            File.Delete(target);
                        }
                    }
                    else
                    {
                        EnsureNoLinks(row.FullPath!);
                        if (row.IsFolder)
                        {
                            Directory.CreateDirectory(target);
                        }
                        else
                        {
                            await CopyAsync(row.FullPath!, target,
                                destinations.TryGetValue(row.RelativePath, out var counterpart) && counterpart.FullPath is not null,
                                value =>
                                {
                                    if (value != progress)
                                    {
                                        progress = value;
                                        update(entry, progress, "Running", null);
                                    }
                                }, token).ConfigureAwait(false);
                        }
                    }

                    update(entry, 100, "Completed", null);
                }
                catch (OperationCanceledException)
                {
                    update(entry, progress, "Cancelled", "Synchronization was stopped.");
                    throw;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                {
                    update(entry, progress, "Failed", ex.Message);
                }
            }
        }

        private static async Task CopyAsync(string source, string target, bool overwrite,
            Action<int> report, CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporaryPath = Path.Combine(Path.GetDirectoryName(target)!, $".filesync-{Guid.NewGuid():N}.tmp");
            try
            {
                var modified = File.GetLastWriteTimeUtc(source);
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                    131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    131072, FileOptions.Asynchronous))
                {
                    var length = input.Length;
                    var buffer = new byte[131072];
                    long copied = 0;
                    int count;
                    while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                        copied += count;
                        report(length == 0 ? 0 : (int)Math.Min(99, copied * 100.0 / length));
                    }

                    await output.FlushAsync(token).ConfigureAwait(false);
                }

                File.SetLastWriteTimeUtc(temporaryPath, modified);
                token.ThrowIfCancellationRequested();
                EnsureNoLinks(target);
                File.Move(temporaryPath, target, overwrite);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }
}
