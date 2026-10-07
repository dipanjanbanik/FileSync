using System.ComponentModel;

namespace FileSync
{
    internal enum SynchronizationOperation
    {
        None,
        New,
        Overwrite,
        Delete,
        Skip
    }

    internal sealed class SynchronizationLogEntry : INotifyPropertyChanged
    {
        private int progress;
        private string status = "Running";
        private string? message;

        public DateTime StartedAt { get; } = DateTime.Now;
        public required string FileName { get; init; }
        public required string Rule { get; init; }
        public SynchronizationOperation Operation { get; init; }
        public int Progress => progress;
        public string Status => status;
        public string? Message => message;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Update(int value, string state, string? detail = null)
        {
            progress = Math.Clamp(value, 0, 100);
            status = state;
            message = detail;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Message)));
        }
    }
}
