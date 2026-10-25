using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Il2CppInterop.Runtime.Runtime;

/// <summary>
/// Writes a native crash report next to the runtime assembly.
///
/// A game that installs il2cpp detours can die with no managed exception, no WER event and no
/// crash dump, which leaves nothing to debug from. A vectored exception handler sees the fault
/// before the CRT does, so at minimum the exception code and faulting address get recorded; the
/// address is then mapped back to module + RVA so it can be disassembled statically.
///
/// Only fatal codes are recorded - C++ exceptions and breakpoints are used for ordinary control
/// flow inside il2cpp and would drown the log.
/// </summary>
internal static unsafe class CrashGuard
{
    private const uint ExceptionAccessViolation = 0xC0000005;
    private const uint ExceptionStackOverflow = 0xC00000FD;
    private const uint ExceptionFailFast = 0xC0000409;
    private const uint ExceptionHeapCorruption = 0xC0000374;
    private const uint ExceptionIllegalInstruction = 0xC000001D;
    private const uint ExceptionInPageError = 0xC0000006;
    private const uint ExceptionGuardPage = 0x80000001;

    private const int MaxReports = 24;

    private static int s_reports;
    private static readonly List<(string Name, long Start, long End)> s_modules = new();
    private static readonly object s_lock = new();
    private static string s_logPath;

    private delegate int VectoredHandler(IntPtr exceptionPointers);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr AddVectoredExceptionHandler(uint first, VectoredHandler handler);

    private static VectoredHandler s_handler; // keep a root so the delegate is not collected

    public static void Install()
    {
        if (s_handler != null) return;

        try
        {
            var dir = Path.GetDirectoryName(typeof(CrashGuard).Assembly.Location);
            if (string.IsNullOrEmpty(dir)) return;
            s_logPath = Path.Combine(dir, "il2cppinterop.crash.txt");

            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                try
                {
                    s_modules.Add((module.ModuleName, module.BaseAddress.ToInt64(),
                        module.BaseAddress.ToInt64() + module.ModuleMemorySize));
                }
                catch
                {
                    // Some system modules throw on access; skipping one is harmless.
                }
            }

            s_handler = OnException;
            AddVectoredExceptionHandler(1, s_handler);

            s_exceptionPath = Path.Combine(dir, "il2cppinterop.exceptions.txt");
            InstallManagedHandlers();

            Append($"--- crash guard installed, {s_modules.Count} modules mapped ---");
        }
        catch
        {
            // Diagnostics must never take the process down on their own.
        }
    }

    private static int OnException(IntPtr exceptionPointers)
    {
        try
        {
            if (s_reports++ >= MaxReports) return 0;

            var record = *(ExceptionRecord**)exceptionPointers;
            var code = (uint)record->ExceptionCode;
            if (!IsFatal(code)) return 0;

            var address = (long)record->ExceptionAddress;
            var where = Resolve(address);

            var detail = new StringBuilder();
            detail.Append("code=0x").Append(code.ToString("X8"));
            detail.Append(" addr=0x").Append(address.ToString("X"));
            detail.Append(" at=").Append(where);

            if (code == ExceptionAccessViolation && record->NumberParameters >= 2)
            {
                var kind = record->ExceptionInformation[0] == 0 ? "read" : record->ExceptionInformation[0] == 1 ? "write" : "execute";
                detail.Append(" op=").Append(kind);
                detail.Append(" target=0x").Append(((long)record->ExceptionInformation[1]).ToString("X"));
            }

            Append(detail.ToString());
        }
        catch
        {
            // Swallow: an exception inside the handler would recurse.
        }

        return 0; // EXCEPTION_CONTINUE_SEARCH
    }

    private static string s_exceptionPath;
    private static int s_exceptionTotal;
    private static readonly Dictionary<string, int> s_exceptionCounts = new();
    private const int MaxExceptions = 400;
    private const int MaxExceptionsPerType = 15;

    /// <summary>
    /// A CLR fatal error and an unhandled managed exception both end the process with output on
    /// stderr only, which MelonLoader's file log never sees. Subscribing to the managed events
    /// turns that invisible death into a written-down one.
    ///
    /// FirstChanceException also captures exceptions that are caught further up - that is what
    /// makes an il2cpp <c>ExecutionEngineException</c> during AddComponent visible even though
    /// Unity swallows it and merely logs "The script 'X' could not be instantiated!".
    /// </summary>
    private static void InstallManagedHandlers()
    {
        try
        {
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        }
        catch
        {
            // Diagnostics must never take the process down on their own.
        }
    }

    private static void OnFirstChanceException(object sender, FirstChanceExceptionEventArgs e)
    {
        try
        {
            var exception = e.Exception;
            if (exception == null) return;

            var type = exception.GetType().FullName ?? "<unknown>";
            int seen;
            lock (s_lock)
            {
                if (s_exceptionTotal >= MaxExceptions) return;
                s_exceptionCounts.TryGetValue(type, out seen);
                if (seen >= MaxExceptionsPerType) return;
                s_exceptionCounts[type] = seen + 1;
                s_exceptionTotal++;
            }

            WriteException($"fce {type}: {exception.Message}{Environment.NewLine}{ShortStack(exception)}");
        }
        catch
        {
            // Swallow: an exception inside the handler would recurse.
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var text = e.ExceptionObject?.ToString() ?? "<null>";
            WriteException($"UNHANDLED terminating={e.IsTerminating}{Environment.NewLine}{text}");
        }
        catch
        {
        }
    }

    private static string ShortStack(Exception exception)
    {
        var stack = exception.StackTrace;
        if (string.IsNullOrEmpty(stack)) return "    <no stack>";

        var lines = stack.Split('\n');
        var builder = new StringBuilder();
        for (var i = 0; i < lines.Length && i < 8; i++)
            builder.Append("    ").Append(lines[i].TrimEnd()).Append(Environment.NewLine);

        return builder.ToString();
    }

    private static void WriteException(string text)
    {
        if (string.IsNullOrEmpty(s_exceptionPath)) return;

        lock (s_lock)
        {
            File.AppendAllText(s_exceptionPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {text}{Environment.NewLine}");
        }
    }

    private static bool IsFatal(uint code)
    {
        return code is ExceptionAccessViolation or ExceptionStackOverflow or ExceptionFailFast
            or ExceptionHeapCorruption or ExceptionIllegalInstruction or ExceptionInPageError
            or ExceptionGuardPage;
    }

    private static string Resolve(long address)
    {
        foreach (var module in s_modules)
        {
            if (address < module.Start || address >= module.End) continue;
            return $"{module.Name}+0x{(address - module.Start):X}";
        }

        return "<unmapped>";
    }

    private static void Append(string line)
    {
        if (string.IsNullOrEmpty(s_logPath)) return;

        lock (s_lock)
        {
            File.AppendAllText(s_logPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExceptionRecord
    {
        public int ExceptionCode;
        public int ExceptionFlags;
        public IntPtr ExceptionRecordPtr;
        public IntPtr ExceptionAddress;
        public int NumberParameters;
        public fixed long ExceptionInformation[15];
    }
}
