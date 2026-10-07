# FileSync

FileSync is a Windows desktop application for comparing folders and synchronizing files. Built with **WPF and .NET 10**, it provides side-by-side folder comparisons, live processing progress, and an activity log of completed, failed, skipped, and cancelled entries.

> **Important:** Replicate source can overwrite destination files and delete destination-only files and folders. Review the selected paths and keep backups before starting synchronization.

## Features

- Recursively scan source and destination folders and compare entries by relative path.
- Display entry type, file size, modification time, and comparison status in aligned tables.
- Highlight missing entries, different files, and file/folder conflicts.
- Sort comparison tables together and optionally synchronize their scrolling.
- Replicate source files into the destination, with Start and Stop controls.
- Show execution start date/time, full path, applied rule, live percentage progress, and status for each processed entry.
- Update Pending, Ongoing, Failed, Success, Overwritten, New, Deleted, and Skipped counters during processing, including both files and folders.
- Resize the comparison and activity log heights using the divider between them.
- Display the application assembly version in the bottom-right corner.

## Synchronization behavior

**Replicate source** is the currently implemented synchronization mode.

| Comparison result | Action |
| --- | --- |
| Entry exists only in the source | Copy the file or create the folder in the destination. |
| File differs between the two folders | Overwrite the destination file with the source file. |
| Entry exists only in the destination | Delete the destination entry. |
| Entries match | Skip the entry. |
| One side is a file and the other is a folder | Report a conflict without replacing either entry. |
| Entry cannot be compared safely | Report a failure without processing that entry. |

Files are compared using their **size and exact UTC modification timestamp**, not content hashes. Matching metadata does not guarantee identical file contents.

Synchronization takes a fresh comparison before processing. Copy operations use a temporary file before replacing the destination and preserve the source file's modification timestamp. Stopping requests cancellation; already completed changes are not rolled back.

Activity rows and counters reset when the next sync starts. Failed and cancelled rows retain their last reported progress. Success counts completed operations, while Skipped is tracked separately. Destination-change counters increase only after successful operations.

### Current limitations

- The other synchronization modes shown in the dropdown are placeholders and cannot be started.
- Source and destination folders must exist and must not be the same folder or nested within each other.
- Symbolic links and junctions are not supported for synchronization.
- Activity history is in memory and is not saved across application restarts.

## Getting started

1. Launch `FileSync.exe`.
2. Select the **Source** and **Destination** folders using Browse or enter their paths.
3. Click **Scan** to review the comparison results.
4. Select **Replicate source**.
5. Click **Start** and confirm the overwrite/delete warning.
6. Monitor the live counters and activity log. Use **Stop** to request cancellation.

## Build and run

### Requirements

- Windows.
- The **.NET 10 SDK** to build the project.
- Optionally, a Visual Studio version supporting .NET 10 with the **.NET desktop development** workload.
- The **.NET 10 Desktop Runtime** to run the default framework-dependent build, unless a matching SDK/runtime is already installed.

From the repository root:

```powershell
dotnet restore FileSync.slnx
dotnet build FileSync.slnx --configuration Release --no-restore
.\LocalOutput\Build\FileSync.exe
```

Each build copies its output to `LocalOutput\Build`. Distribute the entire folder, not just the executable.

To clean the Release build and the copied output:

```powershell
dotnet clean FileSync.slnx --configuration Release
```

Clean also removes `LocalOutput\Build`, including read-only output files and folders, while leaving other `LocalOutput` folders untouched.

### Including the runtime

The self-contained settings in `FileSync.csproj` are currently commented out. To build a Windows x86 version that includes the .NET and WPF runtimes without changing those defaults:

```powershell
dotnet build FileSync.slnx --configuration Release --runtime win-x86 --self-contained true
```

Distribute the entire resulting `LocalOutput\Build` folder. This build does not require a separate .NET runtime installation on the destination machine.

## Continuous integration

The [Windows build workflow](.github/workflows/windows-build.yml) runs on pushes, pull requests, and manual dispatches. It restores and builds the solution in Release mode on a Windows runner using .NET 10, then ZIPs `LocalOutput`.

Download the **FileSync-Windows-Release** artifact from the workflow run's artifacts section. It contains `FileSync-LocalOutput.zip`. With the current project defaults, the CI build is framework-dependent.

## Project structure

| File | Responsibility |
| --- | --- |
| `MainWindow.xaml` / `MainWindow.xaml.cs` | WPF interface, synchronization lifecycle, log, and counters. |
| `FolderComparisonController.cs` | Folder scanning, comparison, and aligned sorting. |
| `FolderComparisonRow.cs` | Comparison entry data. |
| `FileSynchronizationService.cs` | Replication, file-copy progress, deletion, and cancellation. |
| `SynchronizationRules.cs` | Synchronization modes and rule descriptions. |
| `SynchronizationLogEntry.cs` | Observable activity entries and operation types. |
| `FileSync.csproj` | Build configuration and copied-output cleanup. |

## License

Licensed under the [MIT License](LICENSE). Copyright (c) 2026 Dipanjan Banik.
