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
        private int _progress;
        private string _status = "Running";
        private string? _message;

        public DateTime StartedAt { get; } = DateTime.Now;
        public required string FileName { get; init; }
        public required string Rule { get; init; }
        public SynchronizationOperation Operation { get; init; }
        public int Progress => _progress;
        public string Status => _status;
        public string? Message => _message;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Update(int value, string state, string? detail = null)
        {
            _progress = Math.Clamp(value, 0, 100);
            _status = state;
            _message = detail;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Message)));
        }
    }
}
