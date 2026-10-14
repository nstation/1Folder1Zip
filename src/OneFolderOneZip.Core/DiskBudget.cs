namespace OneFolderOneZip.Core;

public sealed class DiskBudget(Func<string, (string Key, long? FreeBytes)>? spaceProvider = null)
{
    private readonly object _gate = new();
    private readonly Func<string, (string Key, long? FreeBytes)> _spaceProvider = spaceProvider ?? WindowsFileSystem.VolumeSpace;
    private readonly List<Reservation> _active = [];

    public Reservation Reserve(string directory, long estimate)
    {
        lock (_gate)
        {
            var (key, free) = _spaceProvider(directory);
            long reserved = _active.Where(item => item.Key == key).Sum(item => Math.Max(0, item.Estimate - item.Written));
            if (free.HasValue && estimate <= free.Value && estimate > Math.Max(0, free.Value - reserved))
                throw new ReservationConflictException();
            if (free.HasValue && estimate > Math.Max(0, free.Value - reserved))
                throw new PathException(directory, new IOException("Insufficient free space for this folder and active archives.", unchecked((int)0x80070070)));
            var reservation = new Reservation(this, key, estimate);
            _active.Add(reservation);
            return reservation;
        }
    }

    public sealed class Reservation : IDisposable
    {
        private readonly DiskBudget _owner;
        internal string Key { get; }
        internal long Estimate { get; }
        internal long Written { get; private set; }
        internal Reservation(DiskBudget owner, string key, long estimate) { _owner = owner; Key = key; Estimate = estimate; }
        public void UpdateWritten(long bytes) { lock (_owner._gate) Written = bytes; }
        public void Dispose() { lock (_owner._gate) _owner._active.Remove(this); }
    }

    public static long Estimate(ScanProgress scan) => checked(scan.Bytes + scan.Bytes / 1000 + scan.EntryOverhead);
}

public sealed class ReservationConflictException() : IOException("Waiting for active archives on this volume to release their reservations.");
