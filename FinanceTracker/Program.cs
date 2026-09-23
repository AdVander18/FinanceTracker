using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Avalonia;

namespace FinanceTracker
{
    internal class Program
    {
        private const string MutexName = "FinanceTracker_SingleInstance";

        [STAThread]
        public static void Main(string[] args)
        {
            // Если файл данных Expenses.dat уже открыт каким-либо процессом —
            // закрываем его (завершаем владеющий процесс) и открываем доступ заново.
            EnsureDataFileReleased();

            StartSingleInstance(args);
        }

        private static void StartSingleInstance(string[] args)
        {
            Mutex? mutex = null;
            try
            {
                mutex = new Mutex(true, MutexName, out bool createdNew);
                if (!createdNew)
                {
                    // Другой экземпляр уже запущен — завершаем его и захватываем mutex.
                    TerminateLockingProcesses();
                    try
                    {
                        mutex.WaitOne(TimeSpan.FromSeconds(5));
                    }
                    catch (AbandonedMutexException)
                    {
                        // Предыдущий владелец был завершён, mutex переходит к нам.
                    }
                }

                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            finally
            {
                if (mutex is not null)
                {
                    try { mutex.ReleaseMutex(); } catch { /* ignore */ }
                    mutex.Dispose();
                }
            }
        }

        private static void EnsureDataFileReleased()
        {
            string filePath = GetDataFilePath();
            if (!File.Exists(filePath))
                return;

            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (!IsFileLocked(filePath))
                    return;
                TerminateLockingProcesses();
                Thread.Sleep(500);
            }
        }

        private static string GetDataFilePath()
            => Path.Combine(AppContext.BaseDirectory, "expenses.dat");

        private static bool IsFileLocked(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static void TerminateLockingProcesses()
        {
            string filePath = GetDataFilePath();
            int[]? pids = RestartManager.GetProcessesUsingFile(filePath);
            if (pids is null)
                return;

            var currentDir = Path.GetFullPath(AppContext.BaseDirectory)
                                 .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            foreach (int pid in pids)
            {
                if (pid == Environment.ProcessId)
                    continue;

                Process? proc = null;
                try { proc = Process.GetProcessById(pid); }
                catch { continue; }

                try
                {
                    string name = proc.ProcessName;
                    string? exePath = null;
                    try { exePath = proc.MainModule?.FileName; }
                    catch { /* possible access denied */ }

                    bool isOurs =
                        name.IndexOf("FinanceTracker", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (exePath is not null &&
                         Path.GetFullPath(exePath).StartsWith(currentDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

                    if (!isOurs)
                        continue;

                    try
                    {
                        proc.Kill();
                        proc.WaitForExit(3000);
                    }
                    catch { /* ignore */ }
                }
                finally
                {
                    proc.Dispose();
                }
            }

            Thread.Sleep(300);
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();

        private static class RestartManager
        {
            private const int RmErrorMoreData = 234;
            private const int CchRmMaxAppName = 255;
            private const int CchRmMaxSvcName = 63;
            private const int CchRmSessionKey = 64;

            internal static int[]? GetProcessesUsingFile(string filePath)
            {
                if (!OperatingSystem.IsWindows())
                    return null;

                uint sessionHandle = 0;
                try
                {
                    var sessionKey = new StringBuilder(CchRmSessionKey);
                    if (RmStartSession(out sessionHandle, 0, sessionKey) != 0)
                        return null;

                    string[] resources = { filePath };
                    if (RmRegisterResources(sessionHandle, 1, resources, 0, null, 0, null) != 0)
                        return null;

                    uint procInfoNeeded = 0, procInfo = 0, rebootReasons = 0;
                    int err = RmGetList(sessionHandle, out procInfoNeeded, ref procInfo, null, ref rebootReasons);
                    if (err != 0 && err != RmErrorMoreData)
                        return null;

                    var processInfos = new RmProcessInfo[procInfoNeeded];
                    procInfo = procInfoNeeded;
                    err = RmGetList(sessionHandle, out procInfoNeeded, ref procInfo, processInfos, ref rebootReasons);
                    if (err != 0 && err != RmErrorMoreData)
                        return null;

                    var result = new List<int>();
                    for (int i = 0; i < procInfo; i++)
                        result.Add(processInfos[i].Process.ProcessId);
                    return result.ToArray();
                }
                finally
                {
                    if (sessionHandle != 0)
                        RmEndSession(sessionHandle);
                }
            }

            [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern int RmStartSession(
                out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

            [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern int RmRegisterResources(
                uint pSessionHandle,
                uint nFiles,
                string[] rgsFilenames,
                uint nApplications,
                RmUniqueProcess[]? rgsApplications,
                uint nServices,
                string[]? rgsServiceNames);

            [DllImport("rstrtmgr.dll", SetLastError = true)]
            private static extern int RmGetList(
                uint dwSessionHandle,
                out uint pnProcInfoNeeded,
                ref uint pnProcInfo,
                [In, Out] RmProcessInfo[]? rgAffectedApps,
                ref uint lpdwRebootReasons);

            [DllImport("rstrtmgr.dll", SetLastError = true)]
            private static extern int RmEndSession(uint pSessionHandle);

            [StructLayout(LayoutKind.Sequential)]
            private struct RmUniqueProcess
            {
                public int ProcessId;
                public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct RmProcessInfo
            {
                public RmUniqueProcess Process;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)]
                public string AppName;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)]
                public string ServiceShortName;
                public RmAppType ApplicationType;
                public uint AppStatus;
                public uint TSSessionId;
                [MarshalAs(UnmanagedType.Bool)]
                public bool Restartable;
            }

            private enum RmAppType
            {
                RmUnknownApp = 0,
                RmMainWindow = 1,
                RmOtherWindow = 2,
                RmService = 3,
                RmExplorer = 4,
                RmConsole = 5,
                RmCritical = 1000
            }
        }
    }
}