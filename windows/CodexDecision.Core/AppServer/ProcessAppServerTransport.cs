using System.Diagnostics;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CodexDecision.Core.AppServer;

public sealed class ProcessAppServerTransport : IAppServerTransport
{
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentQueue<string> diagnostics = new();
    private Process? process;
    private Task? stderrPump;
    private WindowsJobObject? job;

    public string? LastDiagnostic { get; private set; }

    public int? ProcessId => process?.Id;

    public IReadOnlyList<string> Diagnostics => diagnostics.ToArray();

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (process is not null)
        {
            throw new InvalidOperationException("The Codex app-server process is already started.");
        }

        var startInfo = CreateStartInfo();
        process = Process.Start(startInfo)
                  ?? throw new InvalidOperationException("Unable to start Codex app-server.");
        if (OperatingSystem.IsWindows())
        {
            try
            {
                job = WindowsJobObject.Attach(process);
            }
            catch
            {
                process.Kill(entireProcessTree: true);
                process.Dispose();
                process = null;
                throw;
            }
        }
        stderrPump = PumpStandardErrorAsync(process, lifetime.Token);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ReadLinesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var active = process ?? throw new InvalidOperationException("The transport is not started.");
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await active.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        var active = process ?? throw new InvalidOperationException("The transport is not started.");
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            await active.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
            await active.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (process is not null)
        {
            try
            {
                process.StandardInput.Close();
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync();
            }
            catch (InvalidOperationException)
            {
                // The process exited between the checks.
            }
            finally
            {
                process.Dispose();
                process = null;
            }
        }

        if (stderrPump is not null)
        {
            try
            {
                await stderrPump;
            }
            catch (OperationCanceledException)
            {
            }
        }

        job?.Dispose();
        job = null;

        writeGate.Dispose();
        lifetime.Dispose();
    }

    private static ProcessStartInfo CreateStartInfo()
    {
        var configuredBinary = Environment.GetEnvironmentVariable("CODEX_BIN");
        ProcessStartInfo startInfo;
        if (!string.IsNullOrWhiteSpace(configuredBinary) &&
            !configuredBinary.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) &&
            !configuredBinary.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            startInfo = new ProcessStartInfo(configuredBinary);
            startInfo.ArgumentList.Add("app-server");
            startInfo.ArgumentList.Add("--stdio");
        }
        else
        {
            var command = string.IsNullOrWhiteSpace(configuredBinary)
                ? "codex.cmd"
                : $"\"{configuredBinary}\"";
            startInfo = new ProcessStartInfo(
                Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe");
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add($"{command} app-server --stdio");
        }

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
        startInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
        return startInfo;
    }

    private async Task PumpStandardErrorAsync(Process active, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await active.StandardError.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return;
            }

            // Diagnostics stay in memory and are never written to the workspace store.
            LastDiagnostic = line.Length > 500 ? line[..500] : line;
            diagnostics.Enqueue(LastDiagnostic);
            while (diagnostics.Count > 100)
            {
                diagnostics.TryDequeue(out _);
            }
        }
    }

    private sealed class WindowsJobObject : IDisposable
    {
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private IntPtr handle;

        private WindowsJobObject(IntPtr handle)
        {
            this.handle = handle;
        }

        public static WindowsJobObject Attach(Process process)
        {
            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create the Codex process job object.");
            }

            var job = new WindowsJobObject(handle);
            try
            {
                var information = new JobObjectExtendedLimitInformation
                {
                    BasicLimitInformation = new JobObjectBasicLimitInformation
                    {
                        LimitFlags = JobObjectLimitKillOnJobClose,
                    },
                };
                if (!SetInformationJobObject(
                        handle,
                        9,
                        ref information,
                        (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()) ||
                    !AssignProcessToJobObject(handle, process.Handle))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to guard the Codex process lifetime.");
                }

                return job;
            }
            catch
            {
                job.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }

            CloseHandle(handle);
            handle = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            IntPtr job,
            int informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
