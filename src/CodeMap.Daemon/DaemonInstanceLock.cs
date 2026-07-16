namespace CodeMap.Daemon;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

/// <summary>Owns the per-data-directory mutex and human-readable lock metadata.</summary>
public sealed class DaemonInstanceLock : IDisposable
{
    public const int AlreadyRunningExitCode = 17;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly NamedMutexLease _mutex;
    private readonly FileStream _stream;
    private readonly string _lockPath;
    private bool _disposed;

    private DaemonInstanceLock(NamedMutexLease mutex, FileStream stream, string lockPath, DaemonLockInfo info)
    {
        _mutex = mutex;
        _stream = stream;
        _lockPath = lockPath;
        Info = info;
    }

    public DaemonLockInfo Info { get; }

    public static bool TryAcquire(
        RuntimeConfiguration runtime,
        string version,
        out DaemonInstanceLock? instance,
        out DaemonLockInfo? existing)
    {
        instance = null;
        existing = null;
        var canonicalDataDirectory = Path.GetFullPath(runtime.DataDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var identity = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(OperatingSystem.IsWindows()
                ? canonicalDataDirectory.ToUpperInvariant()
                : canonicalDataDirectory)))[..24];
        var mutexName = OperatingSystem.IsWindows()
            ? $"Local\\MGsCodeMap.Daemon.{identity}"
            : $"MGsCodeMap.Daemon.{identity}";
        NamedMutexLease? mutex = null;
        try
        {
            mutex = NamedMutexLease.TryAcquire(mutexName);
            var lockPath = Path.Combine(canonicalDataDirectory, "daemon.lock");
            if (mutex is null)
            {
                existing = Read(lockPath);
                return false;
            }

            FileStream stream;
            try
            {
                stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            }
            catch (IOException)
            {
                existing = Read(lockPath);
                mutex.Dispose();
                return false;
            }

            var process = Process.GetCurrentProcess();
            var info = new DaemonLockInfo(
                process.Id,
                process.StartTime.ToUniversalTime(),
                Environment.ProcessPath ?? process.MainModule?.FileName ?? "MGsCodeMap.Daemon",
                canonicalDataDirectory,
                runtime.McpEndpoint,
                runtime.HealthEndpoint,
                version);
            stream.SetLength(0);
            JsonSerializer.Serialize(stream, info, JsonOptions);
            stream.Flush(flushToDisk: true);
            stream.Position = 0;
            instance = new DaemonInstanceLock(mutex, stream, lockPath, info);
            return true;
        }
        catch
        {
            mutex?.Dispose();
            throw;
        }
    }

    public static DaemonLockInfo? Read(string lockPath)
    {
        try
        {
            using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<DaemonLockInfo>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
        try
        {
            var current = Read(_lockPath);
            if (current?.ProcessId == Info.ProcessId) File.Delete(_lockPath);
        }
        catch (IOException) { }
        _mutex.Dispose();
    }

    private sealed class NamedMutexLease : IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly Thread _thread;
        private int _disposed;

        private NamedMutexLease(string name, ManualResetEventSlim ready, StrongBox<bool> acquired)
        {
            _thread = new Thread(() =>
            {
                using var mutex = new Mutex(false, name);
                try { acquired.Value = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired.Value = true; }
                ready.Set();
                if (!acquired.Value) return;
                _release.Wait();
                mutex.ReleaseMutex();
            }) { IsBackground = true, Name = "MGsCodeMap instance mutex" };
            _thread.Start();
        }

        public static NamedMutexLease? TryAcquire(string name)
        {
            using var ready = new ManualResetEventSlim(false);
            var acquired = new StrongBox<bool>();
            var lease = new NamedMutexLease(name, ready, acquired);
            ready.Wait();
            if (acquired.Value) return lease;
            lease._thread.Join();
            lease._release.Dispose();
            return null;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _release.Set();
            _thread.Join();
            _release.Dispose();
        }
    }
}

public sealed record DaemonLockInfo(
    int ProcessId,
    DateTimeOffset StartTimeUtc,
    string ExecutablePath,
    string DataDirectory,
    string McpEndpoint,
    string HealthEndpoint,
    string Version);
