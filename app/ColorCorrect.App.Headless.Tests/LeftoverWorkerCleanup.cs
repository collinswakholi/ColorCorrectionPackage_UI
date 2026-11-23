using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

[assembly: AssemblyFixture(typeof(ColorCorrect.App.Headless.Tests.LeftoverWorkerCleanup))]

namespace ColorCorrect.App.Headless.Tests;

/// <summary>
/// Stops worker processes that a test started but never disposed (for example a
/// view model created without <c>DisposeAsync</c>). A leaked child inherits this
/// process's standard handles, so it can keep the test host waiting for output
/// after every test has finished.
/// </summary>
public sealed class LeftoverWorkerCleanup : IDisposable
{
    public void Dispose()
    {
        foreach (var child in ChildProcesses())
        {
            using (child)
            {
                try
                {
                    if (child.HasExited) continue;
                    Console.Error.WriteLine($"Stopping leftover test child process {child.ProcessName} ({child.Id}).");
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit(3000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already exited or not ours to stop.
                }
            }
        }
    }

    /// <summary>Direct children of this process (Windows only; empty elsewhere).</summary>
    internal static List<Process> ChildProcesses()
    {
        var children = new List<Process>();
        if (!OperatingSystem.IsWindows()) return children;
        var self = Environment.ProcessId;
        var selfStart = Process.GetCurrentProcess().StartTime;
        var snapshot = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return children;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (var ok = Process32First(snapshot, ref entry); ok; ok = Process32Next(snapshot, ref entry))
            {
                if (entry.th32ParentProcessID != self || entry.th32ProcessID == self) continue;
                try
                {
                    var process = Process.GetProcessById((int)entry.th32ProcessID);
                    // A recycled parent id belongs to an older process: only trust
                    // children that started after this process did.
                    if (process.StartTime >= selfStart) children.Add(process);
                    else process.Dispose();
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return children;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
