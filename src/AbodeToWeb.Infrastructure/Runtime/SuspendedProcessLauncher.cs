using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AbodeToWeb.Infrastructure.Runtime;

public sealed record LaunchedProcess(
    SupervisedProcess Process,
    StreamReader StandardOutput,
    StreamReader StandardError,
    StreamWriter StandardInput,
    WindowsJobObject Job) : IDisposable
{
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Job.Dispose();
        StandardInput.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
        Process.Dispose();
    }
}

public static class SuspendedProcessLauncher
{
    public static LaunchedProcess Start(ProcessStartInfo startInfo, string jobName, string? isolationKey = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!OperatingSystem.IsWindows())
        {
            if (isolationKey is not null) throw new PlatformNotSupportedException("Restricted AbodeToWeb application processes require Windows.");
            return StartPortable(startInfo, jobName);
        }
        return StartWindows(startInfo, jobName, isolationKey);
    }

    [SupportedOSPlatform("windows")]
    private static LaunchedProcess StartWindows(ProcessStartInfo startInfo, string jobName, string? isolationKey)
    {
        var executable = ResolveExecutable(startInfo.FileName, startInfo.WorkingDirectory);
        var arguments = startInfo.ArgumentList.ToArray();
        if (Path.GetExtension(executable) is var extension
            && (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            var scriptCommand = QuoteForCmd(executable, arguments);
            executable = Environment.GetEnvironmentVariable("COMSPEC") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
            arguments = ["/d", "/s", "/c", scriptCommand];
        }

        SafeFileHandle? outputRead = null, outputWrite = null, errorRead = null, errorWrite = null, inputRead = null, inputWrite = null;
        IntPtr environmentBlock = IntPtr.Zero;
        var environmentBlockBytes = 0;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr inheritedHandleList = IntPtr.Zero;
        IntPtr passwordPointer = IntPtr.Zero;
        var passwordAllocationBytes = 0;
        WindowsJobObject? job = null;
        SafeProcessHandle? retainedProcessHandle = null;
        SupervisedProcess? supervisedProcess = null;
        StreamReader? outputStream = null, errorStream = null;
        StreamWriter? inputStream = null;
        string? runtimeDesktop = null;
        RuntimeCredential? runtimeCredential = null;
        try
        {
            if (isolationKey is not null)
            {
                if (WindowsIdentity.GetCurrent().IsSystem)
                    throw new RuntimeCredentialException(
                        "AbodeToWeb cannot launch applications from a LocalSystem service. Configure the AbodeToWeb service with its restricted service identity.");
                runtimeCredential = RuntimeCredentialProvider.Load();
                RuntimeProcessIsolation.Prepare(startInfo, isolationKey, runtimeCredential);
                if (!Environment.UserInteractive)
                    runtimeDesktop = RuntimeDesktopAccess.EnsureAccess(runtimeCredential.AccountSid);
            }

            CreatePipe(out outputRead, out outputWrite, parentReads: true);
            CreatePipe(out errorRead, out errorWrite, parentReads: true);
            CreatePipe(out inputWrite, out inputRead, parentReads: false);

            var startup = new NativeMethods.StartupInfo
            {
                Cb = Marshal.SizeOf<NativeMethods.StartupInfo>(),
                Flags = NativeMethods.StartfUseStdHandles,
                Desktop = runtimeDesktop,
                StandardOutput = outputWrite.DangerousGetHandle(),
                StandardError = errorWrite.DangerousGetHandle(),
                StandardInput = inputRead.DangerousGetHandle()
            };
            environmentBlock = CreateEnvironmentBlock(startInfo.Environment, out environmentBlockBytes);
            var commandLine = (BuildCommandLine(executable, arguments) + '\0').ToCharArray();
            NativeMethods.ProcessInformation processInformation;
            bool created;
            var creationFlags = NativeMethods.CreateNoWindow | NativeMethods.CreateSuspended | NativeMethods.CreateUnicodeEnvironment;
            if (runtimeCredential is null)
            {
                var startupEx = new NativeMethods.StartupInfoEx { StartupInfo = startup };
                startupEx.StartupInfo.Cb = Marshal.SizeOf<NativeMethods.StartupInfoEx>();
                ConfigureInheritedHandles(ref startupEx,
                    [outputWrite.DangerousGetHandle(), errorWrite.DangerousGetHandle(), inputRead.DangerousGetHandle()],
                    out attributeList, out inheritedHandleList);
                created = NativeMethods.CreateProcess(
                    executable, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                    creationFlags | NativeMethods.ExtendedStartupInfoPresent,
                    environmentBlock, startInfo.WorkingDirectory, ref startupEx, out processInformation);
            }
            else
            {
                var password = runtimeCredential.GetPassword();
                passwordAllocationBytes = checked((password.Length + 1) * sizeof(char));
                passwordPointer = AllocatePassword(password);
                created = NativeMethods.CreateProcessWithLogon(
                    runtimeCredential.Username, runtimeCredential.Domain, passwordPointer, 0,
                    executable, commandLine, creationFlags, environmentBlock,
                    startInfo.WorkingDirectory, ref startup, out processInformation);
            }
            if (!created)
            {
                var errorCode = Marshal.GetLastWin32Error();
                if (runtimeCredential is not null) throw RuntimeLogonError(errorCode);
                throw new Win32Exception(errorCode, $"Could not start '{Path.GetFileName(executable)}'.");
            }

            retainedProcessHandle = new SafeProcessHandle(processInformation.Process, true);
            using var threadHandle = new SafeWaitHandle(processInformation.Thread, true);
            job = new WindowsJobObject(jobName);
            try { job.Assign(retainedProcessHandle); }
            catch
            {
                NativeMethods.TerminateProcess(processInformation.Process, 1);
                throw;
            }
            if (NativeMethods.ResumeThread(processInformation.Thread) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resume the application process.");

            outputWrite.Dispose(); outputWrite = null;
            errorWrite.Dispose(); errorWrite = null;
            inputRead.Dispose(); inputRead = null;

            outputStream = new StreamReader(new FileStream(outputRead, FileAccess.Read, 4096, isAsync: false), startInfo.StandardOutputEncoding ?? Encoding.UTF8);
            outputRead = null;
            errorStream = new StreamReader(new FileStream(errorRead, FileAccess.Read, 4096, isAsync: false), startInfo.StandardErrorEncoding ?? Encoding.UTF8);
            errorRead = null;
            inputStream = new StreamWriter(new FileStream(inputWrite, FileAccess.Write, 4096, isAsync: false), startInfo.StandardInputEncoding ?? new UTF8Encoding(false)) { AutoFlush = true };
            inputWrite = null;
            supervisedProcess = new SupervisedProcess(retainedProcessHandle, (int)processInformation.ProcessId, executable);
            retainedProcessHandle = null;
            var launched = new LaunchedProcess(supervisedProcess, outputStream, errorStream, inputStream, job);
            supervisedProcess = null;
            outputStream = null;
            errorStream = null;
            inputStream = null;
            job = null;
            return launched;
        }
        finally
        {
            ZeroAndFree(passwordPointer, passwordAllocationBytes);
            runtimeCredential?.Dispose();
            ZeroAndFree(environmentBlock, environmentBlockBytes);
            if (attributeList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (inheritedHandleList != IntPtr.Zero) Marshal.FreeHGlobal(inheritedHandleList);
            outputRead?.Dispose(); outputWrite?.Dispose(); errorRead?.Dispose(); errorWrite?.Dispose(); inputRead?.Dispose(); inputWrite?.Dispose();
            inputStream?.Dispose(); outputStream?.Dispose(); errorStream?.Dispose();
            supervisedProcess?.Dispose();
            retainedProcessHandle?.Dispose();
            job?.Dispose();
        }
    }

    private static LaunchedProcess StartPortable(ProcessStartInfo startInfo, string jobName)
    {
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The process could not be started.");
        process.EnableRaisingEvents = true;
        return new LaunchedProcess(new SupervisedProcess(process, startInfo.FileName), process.StandardOutput,
            process.StandardError, process.StandardInput, new WindowsJobObject(jobName));
    }

    private static void CreatePipe(out SafeFileHandle parentEnd, out SafeFileHandle childEnd, bool parentReads)
    {
        var attributes = new NativeMethods.SecurityAttributes
        {
            Length = Marshal.SizeOf<NativeMethods.SecurityAttributes>(),
            InheritHandle = true
        };
        if (!NativeMethods.CreatePipe(out var read, out var write, ref attributes, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create an application log pipe.");
        var readHandle = new SafeFileHandle(read, true);
        var writeHandle = new SafeFileHandle(write, true);
        var nonInherited = parentReads ? readHandle : writeHandle;
        if (!NativeMethods.SetHandleInformation(nonInherited, NativeMethods.HandleFlagInherit, 0))
        {
            readHandle.Dispose(); writeHandle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not secure an application log pipe.");
        }
        if (parentReads) { parentEnd = readHandle; childEnd = writeHandle; }
        else { parentEnd = writeHandle; childEnd = readHandle; }
    }

    internal static string BuildCommandLine(string executable, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { QuoteWindowsArgument(executable) }.Concat(arguments.Select(QuoteWindowsArgument)));

    internal static string QuoteWindowsArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(character => char.IsWhiteSpace(character) || character == '"')) return argument;
        var builder = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { backslashes++; continue; }
            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            builder.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }

    private static string QuoteForCmd(string executable, string[] arguments)
    {
        static string Escape(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        return Escape(executable) + (arguments.Length == 0 ? string.Empty : " " + string.Join(' ', arguments.Select(Escape)));
    }

    private static string ResolveExecutable(string executable, string workingDirectory)
    {
        if (Path.IsPathFullyQualified(executable)) return File.Exists(executable) ? executable : throw new FileNotFoundException("Configured executable was not found.", executable);
        if (executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            var path = Path.GetFullPath(executable, workingDirectory);
            return File.Exists(path) ? path : throw new FileNotFoundException("Configured executable was not found.", path);
        }
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';', StringSplitOptions.RemoveEmptyEntries);
        var candidates = Path.HasExtension(executable) ? [executable] : extensions.Select(extension => executable + extension).Prepend(executable);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(directory.Trim('"'), candidate);
                if (File.Exists(path)) return Path.GetFullPath(path);
            }
        throw new FileNotFoundException($"Configured executable '{executable}' was not found on PATH.");
    }

    private static IntPtr CreateEnvironmentBlock(IDictionary<string, string?> environment, out int byteCount)
    {
        if (environment.Count > 512)
            throw new ArgumentException("The application environment contains too many variables.", nameof(environment));

        var builder = new StringBuilder();
        foreach (var (key, value) in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(key) || key.Contains('=', StringComparison.Ordinal)
                || key.Contains('\0', StringComparison.Ordinal) || value is null || value.Contains('\0', StringComparison.Ordinal))
                throw new ArgumentException("The application environment contains an invalid variable.", nameof(environment));
            builder.Append(key).Append('=').Append(value).Append('\0');
            if (builder.Length > 32_000)
                throw new ArgumentException("The application environment is too large.", nameof(environment));
        }
        builder.Append('\0');
        var content = builder.ToString();
        byteCount = checked(content.Length * sizeof(char));
        return Marshal.StringToHGlobalUni(content);
    }

    private static void ConfigureInheritedHandles(
        ref NativeMethods.StartupInfoEx startup,
        IntPtr[] handles,
        out IntPtr attributeList,
        out IntPtr handleList)
    {
        nuint required = 0;
        _ = NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref required);
        if (required == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not size the secure process handle list.");

        attributeList = Marshal.AllocHGlobal(checked((nint)required));
        handleList = IntPtr.Zero;
        var initialized = false;
        try
        {
            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref required))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the secure process handle list.");
            initialized = true;

            handleList = Marshal.AllocHGlobal(checked(IntPtr.Size * handles.Length));
            Marshal.Copy(handles, 0, handleList, handles.Length);
            if (!NativeMethods.UpdateProcThreadAttribute(attributeList, 0,
                    (IntPtr)NativeMethods.ProcThreadAttributeHandleList, handleList,
                    checked((nuint)(IntPtr.Size * handles.Length)), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not restrict inherited process handles.");
            startup.AttributeList = attributeList;
        }
        catch
        {
            if (attributeList != IntPtr.Zero)
            {
                if (initialized) NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
                attributeList = IntPtr.Zero;
            }
            if (handleList != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(handleList);
                handleList = IntPtr.Zero;
            }
            throw;
        }
    }

    private static IntPtr AllocatePassword(char[] password)
    {
        var pointer = Marshal.AllocHGlobal(checked((password.Length + 1) * sizeof(char)));
        Marshal.Copy(password, 0, pointer, password.Length);
        Marshal.WriteInt16(pointer, password.Length * sizeof(char), 0);
        return pointer;
    }

    private static void ZeroAndFree(IntPtr pointer, int bytes)
    {
        if (pointer == IntPtr.Zero) return;
        for (var offset = 0; offset < bytes; offset++) Marshal.WriteByte(pointer, offset, 0);
        Marshal.FreeHGlobal(pointer);
    }

    private static RuntimeCredentialException RuntimeLogonError(int error) => error switch
    {
        1326 => new("Windows rejected the dedicated runtime account credentials. Repair the AbodeToWeb runtime identity and restart AbodeToWeb."),
        1330 => new("The dedicated runtime account password has expired. Repair the AbodeToWeb runtime identity and restart AbodeToWeb."),
        1385 => new("The dedicated runtime account is not allowed to log on. Grant its required local logon right or run AbodeToWeb repair."),
        1907 => new("The dedicated runtime account password must be changed. Repair the AbodeToWeb runtime identity and restart AbodeToWeb."),
        _ => new RuntimeCredentialException($"Windows could not launch the application with the dedicated runtime identity (error {error}).")
    };

    private static class NativeMethods
    {
        internal const uint CreateSuspended = 0x00000004;
        internal const uint CreateNoWindow = 0x08000000;
        internal const uint CreateUnicodeEnvironment = 0x00000400;
        internal const uint ExtendedStartupInfoPresent = 0x00080000;
        internal const uint StartfUseStdHandles = 0x00000100;
        internal const uint HandleFlagInherit = 0x00000001;
        internal const nuint ProcThreadAttributeHandleList = 0x00020002;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SecurityAttributes
        {
            internal int Length;
            internal IntPtr SecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct StartupInfo
        {
            internal int Cb;
            internal string? Reserved;
            internal string? Desktop;
            internal string? Title;
            internal uint X;
            internal uint Y;
            internal uint XSize;
            internal uint YSize;
            internal uint XCountChars;
            internal uint YCountChars;
            internal uint FillAttribute;
            internal uint Flags;
            internal ushort ShowWindow;
            internal ushort Reserved2;
            internal IntPtr ReservedPointer;
            internal IntPtr StandardInput;
            internal IntPtr StandardOutput;
            internal IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfoEx
        {
            internal StartupInfo StartupInfo;
            internal IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation
        {
            internal IntPtr Process;
            internal IntPtr Thread;
            internal uint ProcessId;
            internal uint ThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, ref SecurityAttributes attributes, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcess(
            string? applicationName,
            [In, Out] char[] commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("advapi32.dll", EntryPoint = "CreateProcessWithLogonW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessWithLogon(
            string username,
            string domain,
            IntPtr password,
            uint logonFlags,
            string? applicationName,
            [In, Out] char[] commandLine,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(
            IntPtr attributeList,
            int attributeCount,
            uint flags,
            ref nuint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(
            IntPtr attributeList,
            uint flags,
            IntPtr attribute,
            IntPtr value,
            nuint size,
            IntPtr previousValue,
            IntPtr returnSize);

        [DllImport("kernel32.dll")]
        internal static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateProcess(IntPtr process, uint exitCode);
    }
}
