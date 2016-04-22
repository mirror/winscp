﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Security.Principal;
using System.Security.AccessControl;

namespace WinSCP
{
    internal class ExeSessionProcess : IDisposable
    {
        public event OutputDataReceivedEventHandler OutputDataReceived;

        public bool HasExited { get { return _process.HasExited; } }
        public int ExitCode { get { return _process.ExitCode; } }

        public ExeSessionProcess(Session session)
        {
            _session = session;
            _logger = session.Logger;
            _incompleteLine = string.Empty;

            using (_logger.CreateCallstack())
            {
                string executablePath = GetExecutablePath();

                _logger.WriteLine("EXE executable path resolved to {0}", executablePath);

                string assemblyFilePath = _logger.GetAssemblyFilePath();
                FileVersionInfo assemblyVersion = null;
                if (assemblyFilePath != null)
                {
                    assemblyVersion = FileVersionInfo.GetVersionInfo(assemblyFilePath);
                }

                CheckVersion(executablePath, assemblyVersion);

                string configSwitch;
                if (_session.DefaultConfigurationInternal)
                {
                    configSwitch = "/ini=nul ";
                }
                else
                {
                    if (!string.IsNullOrEmpty(_session.IniFilePath))
                    {
                        configSwitch = string.Format(CultureInfo.InvariantCulture, "/ini=\"{0}\" ", _session.IniFilePath);
                    }
                    else
                    {
                        configSwitch = "";
                    }
                }

                string logSwitch = null;
                if (!string.IsNullOrEmpty(_session.SessionLogPath))
                {
                    logSwitch = string.Format(CultureInfo.InvariantCulture, "/log=\"{0}\" ", LogPathEscape(_session.SessionLogPath));
                }

                string xmlLogSwitch = string.Format(CultureInfo.InvariantCulture, "/xmllog=\"{0}\" ", LogPathEscape(_session.XmlLogPath));

                string assemblyVersionStr =
                    (assemblyVersion == null) ? "unk" :
                    string.Format(CultureInfo.InvariantCulture, "{0}{1}{2} ", assemblyVersion.ProductMajorPart, assemblyVersion.ProductMinorPart, assemblyVersion.ProductBuildPart);

                string assemblyVersionSwitch =
                    string.Format(CultureInfo.InvariantCulture, "/dotnet={0} ", assemblyVersionStr);

                string arguments =
                    xmlLogSwitch + "/xmlgroups /nointeractiveinput " + assemblyVersionSwitch +
                    configSwitch + logSwitch + _session.AdditionalExecutableArguments;

                Tools.AddRawParameters(ref arguments, _session.RawConfiguration, "/rawconfig");

                _process = new Process();
                _process.StartInfo.FileName = executablePath;
                _process.StartInfo.WorkingDirectory = Path.GetDirectoryName(executablePath);
                _process.StartInfo.Arguments = arguments;
                _process.StartInfo.UseShellExecute = false;
                _process.Exited += ProcessExited;
            }
        }

        private static string LogPathEscape(string path)
        {
            return Tools.ArgumentEscape(path).Replace("!", "!!");
        }

        public void Abort()
        {
            using (_logger.CreateCallstack())
            {
                lock (_lock)
                {
                    if ((_process != null) && !_process.HasExited)
                    {
                        _process.Kill();
                    }
                }
            }
        }

        public void Start()
        {
            using (_logger.CreateCallstack())
            {
                InitializeConsole();
                InitializeChild();
            }
        }

        private void InitializeChild()
        {
            using (_logger.CreateCallstack())
            {
                _process.StartInfo.Arguments += string.Format(CultureInfo.InvariantCulture, " /console /consoleinstance={0}", _instanceName);

                // When running under IIS in "impersonated" mode, the process starts, but does not do anything.
                // Supposedly it "displayes" some invisible error message when starting and hangs.
                // Running it "as the user" helps, eventhough it already runs as the user.
                // These's probably some difference between "run as" and impersonations
                if (!string.IsNullOrEmpty(_session.ExecutableProcessUserName))
                {
                    _logger.WriteLine("Will run process as {0}", _session.ExecutableProcessUserName);

                    _process.StartInfo.UserName = _session.ExecutableProcessUserName;
                    _process.StartInfo.Password = _session.ExecutableProcessPassword;
                    // One of the hints for resolving C0000142 error (see below)
                    // was setting this property, so that an environment is correctly loaded,
                    // so DLLs can be found and loaded.
                    _process.StartInfo.LoadUserProfile = true;

                    // Without granting both window station and desktop access permissions,
                    // WinSCP process aborts with C0000142 (DLL Initialization Failed) error,
                    // when "running as user"
                    _logger.WriteLine("Granting access to window station");
                    try
                    {
                        IntPtr windowStation = UnsafeNativeMethods.GetProcessWindowStation();
                        GrantAccess(windowStation, (int)WindowStationRights.AllAccess);
                    }
                    catch (Exception e)
                    {
                        throw new SessionLocalException(_session, "Error granting access to window station", e);
                    }

                    _logger.WriteLine("Granting access to desktop");
                    try
                    {
                        IntPtr desktop = UnsafeNativeMethods.GetThreadDesktop(UnsafeNativeMethods.GetCurrentThreadId());
                        GrantAccess(desktop, (int)DesktopRights.AllAccess);
                    }
                    catch (Exception e)
                    {
                        throw new SessionLocalException(_session, "Error granting access to desktop", e);
                    }
                }

                _logger.WriteLine("Starting \"{0}\" {1}", _process.StartInfo.FileName, _process.StartInfo.Arguments);

                _process.Start();

                _logger.WriteLine("Started process {0}", _process.Id);

                _thread = new Thread(ProcessEvents);
                _thread.IsBackground = true;
                _thread.Start();
            }
        }

        // Handles returned by GetProcessWindowStation and GetThreadDesktop should not be closed
        internal class NoopSafeHandle : SafeHandle
        {
            public NoopSafeHandle(IntPtr handle) :
                base(handle, false)
            {
            }

            public override bool IsInvalid
            {
                get { return false; }
            }

            protected override bool ReleaseHandle()
            {
                return true;
            }
        }

        private void GrantAccess(IntPtr handle, int accessMask)
        {
            using (SafeHandle safeHandle = new NoopSafeHandle(handle))
            {
                GenericSecurity security =
                    new GenericSecurity(false, ResourceType.WindowObject, safeHandle, AccessControlSections.Access);

                security.AddAccessRule(
                    new GenericAccessRule(new NTAccount(_session.ExecutableProcessUserName), accessMask, AccessControlType.Allow));
                security.Persist(safeHandle, AccessControlSections.Access);
            }
        }

        private void ProcessExited(object sender, EventArgs e)
        {
            _logger.WriteLine("Process {0} exited with exit code {1}", _process.Id, _process.ExitCode);
        }

        private bool AbortedOrExited()
        {
            if (_abort)
            {
                _logger.WriteLine("Aborted");
                return true;
            }
            else if (_process.HasExited)
            {
                _logger.WriteLine("Exited");
                return true;
            }
            else
            {
                return false;
            }
        }

        private void ProcessEvents()
        {
            using (_logger.CreateCallstack())
            {
                while (!AbortedOrExited())
                {
                    _logger.WriteLineLevel(1, "Waiting for request event");
                    if (_requestEvent.WaitOne(100, false))
                    {
                        _logger.WriteLineLevel(1, "Got request event");
                        ProcessEvent();
                    }
                }
            }
        }

        private void ProcessEvent()
        {
            using (_logger.CreateCallstack())
            {
                using (ConsoleCommStruct commStruct = AcquireCommStruct())
                {
                    switch (commStruct.Event)
                    {
                        case ConsoleEvent.Print:
                            ProcessPrintEvent(commStruct.PrintEvent);
                            break;

                        case ConsoleEvent.Input:
                            ProcessInputEvent(commStruct.InputEvent);
                            break;

                        case ConsoleEvent.Choice:
                            ProcessChoiceEvent(commStruct.ChoiceEvent);
                            break;

                        case ConsoleEvent.Title:
                            ProcessTitleEvent(commStruct.TitleEvent);
                            break;

                        case ConsoleEvent.Init:
                            ProcessInitEvent(commStruct.InitEvent);
                            break;

                        case ConsoleEvent.Progress:
                            ProcessProgressEvent(commStruct.ProgressEvent);
                            break;

                        default:
                            throw new NotImplementedException();
                    }
                }

                _responseEvent.Set();
            }
        }

        private void ProcessChoiceEvent(ConsoleChoiceEventStruct e)
        {
            using (_logger.CreateCallstack())
            {
                if (e.Timeouting)
                {
                    Thread.Sleep((int)e.Timer);
                    e.Result = e.Timeouted;
                }
                else
                {
                    e.Result = e.Break;
                }
            }
        }

        private void ProcessTitleEvent(ConsoleTitleEventStruct e)
        {
            using (_logger.CreateCallstack())
            {
                _logger.WriteLine("Not-supported title event [{0}]", e.Title);
            }
        }

        private void ProcessInputEvent(ConsoleInputEventStruct e)
        {
            using (_logger.CreateCallstack())
            {
                while (!AbortedOrExited())
                {
                    lock (_input)
                    {
                        if (_input.Count > 0)
                        {
                            e.Str = _input[0];
                            e.Result = true;
                            _input.RemoveAt(0);
                            Print(false, e.Str + "\n");
                            return;
                        }
                    }

                    _inputEvent.WaitOne(100, false);
                }
            }
        }

        private void Print(bool fromBeginning, string message)
        {
            if (fromBeginning && ((message.Length == 0) || (message[0] != '\n')))
            {
                _lastFromBeginning = message;
                _logger.WriteLine("Buffered from-beginning message [{0}]", _lastFromBeginning);
            }
            else
            {
                if (!string.IsNullOrEmpty(_lastFromBeginning))
                {
                    AddToOutput(_lastFromBeginning);
                    _lastFromBeginning = null;
                }

                if (fromBeginning && (message.Length > 0) && (message[0] == '\n'))
                {
                    AddToOutput("\n");
                    _lastFromBeginning = message.Substring(1);
                    _logger.WriteLine("Buffered from-beginning message [{0}]", _lastFromBeginning);
                }
                else
                {
                    AddToOutput(message);
                }
            }
        }

        private void AddToOutput(string message)
        {
            string[] lines = (_incompleteLine + message).Split(new[] { '\n' });

            _incompleteLine = lines[lines.Length - 1];

            for (int i = 0; i < lines.Length - 1; ++i)
            {
                if (OutputDataReceived != null)
                {
                    OutputDataReceived(this, new OutputDataReceivedEventArgs(lines[i]));
                }
            }
        }

        private void ProcessPrintEvent(ConsolePrintEventStruct e)
        {
            Print(e.FromBeginning, e.Message);
        }

        private void ProcessInitEvent(ConsoleInitEventStruct e)
        {
            using (_logger.CreateCallstack())
            {
                e.InputType = 3; // pipe
                e.OutputType = 3; // pipe
                e.WantsProgress = _session.WantsProgress;
            }
        }

        private void ProcessProgressEvent(ConsoleProgressEventStruct e)
        {
            using (_logger.CreateCallstack())
            {
                _logger.WriteLine(
                    "File Name [{0}] - Directory [{1}] - Overall Progress [{2}] - File Progress [{3}] - CPS [{4}]",
                    e.FileName, e.Directory, e.OverallProgress, e.FileProgress, e.CPS);

                FileTransferProgressEventArgs args = new FileTransferProgressEventArgs();

                switch (e.Operation)
                {
                    case ConsoleProgressEventStruct.ProgressOperation.Copy:
                        args.Operation = ProgressOperation.Transfer;
                        break;

                    default:
                        throw new ArgumentOutOfRangeException("Unknown progress operation", (Exception)null);
                }

                switch (e.Side)
                {
                    case ConsoleProgressEventStruct.ProgressSide.Local:
                        args.Side = ProgressSide.Local;
                        break;

                    case ConsoleProgressEventStruct.ProgressSide.Remote:
                        args.Side = ProgressSide.Remote;
                        break;

                    default:
                        throw new ArgumentOutOfRangeException("Unknown progress side", (Exception)null);
                }

                args.FileName = e.FileName;
                args.Directory = e.Directory;
                args.OverallProgress = ((double)e.OverallProgress) / 100;
                args.FileProgress = ((double)e.FileProgress) / 100;
                args.CPS = (int) e.CPS;
                _session.ProcessProgress(args);
            }
        }

        private void InitializeConsole()
        {
            using (_logger.CreateCallstack())
            {
                int attempts = 0;
                Random random = new Random();
                int process = Process.GetCurrentProcess().Id;

                do
                {
                    if (attempts > MaxAttempts)
                    {
                        throw new SessionLocalException(_session, "Cannot find unique name for event object.");
                    }

                    int instanceNumber = random.Next(1000);

                    _instanceName = string.Format(CultureInfo.InvariantCulture, "_{0}_{1}_{2}", process, GetHashCode(), instanceNumber);
                    _logger.WriteLine("Trying event {0}", _instanceName);
                    if (!TryCreateEvent(ConsoleEventRequest + _instanceName, out _requestEvent))
                    {
                        _logger.WriteLine("Event {0} is not unique", _instanceName);
                        _requestEvent.Close();
                        _requestEvent = null;
                    }
                    else
                    {
                        _logger.WriteLine("Event {0} is unique", _instanceName);
                        _responseEvent = CreateEvent(ConsoleEventResponse + _instanceName);
                        _cancelEvent = CreateEvent(ConsoleEventCancel + _instanceName);
                        string fileMappingName = ConsoleMapping + _instanceName;
                        _fileMapping = CreateFileMapping(fileMappingName);
                        if (Marshal.GetLastWin32Error() == UnsafeNativeMethods.ERROR_ALREADY_EXISTS)
                        {
                            throw new SessionLocalException(_session, string.Format(CultureInfo.InvariantCulture, "File mapping {0} already exists", fileMappingName));
                        }
                        if (_fileMapping.IsInvalid)
                        {
                            throw new SessionLocalException(_session, string.Format(CultureInfo.InvariantCulture, "Cannot create file mapping {0}", fileMappingName));
                        }
                    }
                    ++attempts;
                }
                while (_requestEvent == null);

                using (ConsoleCommStruct commStruct = AcquireCommStruct())
                {
                    commStruct.InitHeader();
                }

                if (_session.GuardProcessWithJobInternal)
                {
                    string jobName = ConsoleJob + _instanceName;
                    _job = new Job(_logger, jobName);
                }
            }
        }

        private SafeFileHandle CreateFileMapping(string fileMappingName)
        {
            unsafe
            {
                IntPtr securityAttributesPtr = IntPtr.Zero;

                // We use the EventWaitHandleSecurity only to generate the descriptor binary form
                // that does not differ for object types, so we abuse the existing "event handle" implementation,
                // not to have to create the file mapping SecurityAttributes via P/Invoke.

                // .NET 4 supports MemoryMappedFile and MemoryMappedFileSecurity natively already

                EventWaitHandleSecurity security = CreateSecurity((EventWaitHandleRights)FileMappingRights.AllAccess);

                if (security != null)
                {
                    SecurityAttributes securityAttributes = new SecurityAttributes();
                    securityAttributes.nLength = (uint)Marshal.SizeOf(securityAttributes);

                    byte[] descriptorBinaryForm = security.GetSecurityDescriptorBinaryForm();
                    byte * buffer = stackalloc byte[descriptorBinaryForm.Length];
                    for (int i = 0; i < descriptorBinaryForm.Length; i++)
                    {
                        buffer[i] = descriptorBinaryForm[i];
                    }
                    securityAttributes.lpSecurityDescriptor = (IntPtr)buffer;

                    int length = Marshal.SizeOf(typeof(SecurityAttributes));
                    securityAttributesPtr = Marshal.AllocHGlobal(length);
                    Marshal.StructureToPtr(securityAttributes, securityAttributesPtr, false);
                }

                return
                    UnsafeNativeMethods.CreateFileMapping(
                        new SafeFileHandle(new IntPtr(-1), true), securityAttributesPtr, FileMapProtection.PageReadWrite, 0,
                        ConsoleCommStruct.Size, fileMappingName);
            }
        }

        private ConsoleCommStruct AcquireCommStruct()
        {
            return new ConsoleCommStruct(_session, _fileMapping);
        }

        private bool TryCreateEvent(string name, out EventWaitHandle ev)
        {
            bool createdNew;
            _logger.WriteLine("Creating event {0}", name);

            EventWaitHandleSecurity security = CreateSecurity(EventWaitHandleRights.FullControl);

            ev = new EventWaitHandle(false, EventResetMode.AutoReset, name, out createdNew, security);
            _logger.WriteLine(
                "Created event {0} with handle {1} with security {2}, new {3}",
                name, ev.SafeWaitHandle.DangerousGetHandle(),
                (security != null ? security.GetSecurityDescriptorSddlForm(AccessControlSections.All) : "none"), createdNew);
            return createdNew;
        }

        private EventWaitHandleSecurity CreateSecurity(EventWaitHandleRights eventRights)
        {
            EventWaitHandleSecurity security = null;

            // When "running as user", we have to grant the target user permissions to the objects (events and file mapping) explicitly
            if (!string.IsNullOrEmpty(_session.ExecutableProcessUserName))
            {
                security = new EventWaitHandleSecurity();
                IdentityReference si;
                try
                {
                    si = new NTAccount(_session.ExecutableProcessUserName);
                }
                catch (Exception e)
                {
                    throw new SessionLocalException(_session, string.Format(CultureInfo.CurrentCulture, "Error resolving account {0}", _session.ExecutableProcessUserName), e);
                }

                EventWaitHandleAccessRule rule =
                    new EventWaitHandleAccessRule(
                        si, eventRights, AccessControlType.Allow);
                security.AddAccessRule(rule);
            }

            return security;
        }

        private EventWaitHandle CreateEvent(string name)
        {
            EventWaitHandle ev;
            if (!TryCreateEvent(name, out ev))
            {
                throw new SessionLocalException(_session, string.Format(CultureInfo.InvariantCulture, "Event {0} already exists", name));
            }
            return ev;
        }

        private void TestEventClosed(string name)
        {
            if (_session.TestHandlesClosedInternal)
            {
                _logger.WriteLine("Testing that event {0} is closed", name);
                EventWaitHandle ev;
                if (TryCreateEvent(name, out ev))
                {
                    ev.Close();
                }
                else
                {
                    _logger.WriteLine("Exception: Event {0} was not closed yet", name);
                }
            }
        }

        private void AddInput(string str)
        {
            Type structType = typeof(ConsoleInputEventStruct);
            FieldInfo strField = structType.GetField("Str");
            object[] attributes = strField.GetCustomAttributes(typeof(MarshalAsAttribute), false);
            if (attributes.Length != 1)
            {
                throw new InvalidOperationException("MarshalAs attribute not found for ConsoleInputEventStruct.Str");
            }
            MarshalAsAttribute marshalAsAttribute = (MarshalAsAttribute)attributes[0];

            if (marshalAsAttribute.SizeConst <= str.Length)
            {
                throw new SessionLocalException(
                    _session,
                    string.Format(CultureInfo.CurrentCulture, "Input [{0}] is too long ({1} limit)", str, marshalAsAttribute.SizeConst));
            }

            lock (_input)
            {
                _input.Add(str);
                _inputEvent.Set();
            }
        }

        public void ExecuteCommand(string command)
        {
            using (_logger.CreateCallstack())
            {
                AddInput(command);
            }
        }

        public void Close()
        {
            using (_logger.CreateCallstack())
            {
                int timeout;

                #if DEBUG
                // in debug build, we expect the winscp.exe to run in tracing mode, being very slow
                timeout = 10000;
                #else
                timeout = 2000;
                #endif

                _logger.WriteLine("Waiting for process to exit ({0} ms)", timeout);

                if (!_process.WaitForExit(timeout))
                {
                    _logger.WriteLine("Killing process");
                    _process.Kill();
                }
            }
        }

        public void Dispose()
        {
            using (_logger.CreateCallstack())
            {
                lock (_lock)
                {
                    if (_session.TestHandlesClosedInternal)
                    {
                        _logger.WriteLine("Will test that handles are closed");
                    }
                    _abort = true;
                    if (_thread != null)
                    {
                        _thread.Join();
                        _thread = null;
                    }
                    if (_process != null)
                    {
                        _process.Dispose();
                        _process = null;
                    }
                    if (_requestEvent != null)
                    {
                        _requestEvent.Close();
                        TestEventClosed(ConsoleEventRequest + _instanceName);
                    }
                    if (_responseEvent != null)
                    {
                        _responseEvent.Close();
                        TestEventClosed(ConsoleEventResponse + _instanceName);
                    }
                    if (_cancelEvent != null)
                    {
                        _cancelEvent.Close();
                        TestEventClosed(ConsoleEventCancel + _instanceName);
                    }
                    if (_fileMapping != null)
                    {
                        _fileMapping.Dispose();
                        _fileMapping = null;
                        if (_session.TestHandlesClosedInternal)
                        {
                            _logger.WriteLine("Testing that file mapping is closed");
                            string fileMappingName = ConsoleMapping + _instanceName;
                            SafeFileHandle fileMapping = CreateFileMapping(fileMappingName);
                            if (Marshal.GetLastWin32Error() == UnsafeNativeMethods.ERROR_ALREADY_EXISTS)
                            {
                                _logger.WriteLine("Exception: File mapping {0} was not closed yet", fileMappingName);
                            }
                            if (!fileMapping.IsInvalid)
                            {
                                fileMapping.Dispose();
                            }
                        }
                    }
                    if (_inputEvent != null)
                    {
                        _inputEvent.Close();
                        _inputEvent = null;
                    }
                    if (_job != null)
                    {
                        _job.Dispose();
                        _job = null;
                    }
                }
            }
        }

        private string GetExecutablePath()
        {
            using (_logger.CreateCallstack())
            {
                string executablePath;
                if (!string.IsNullOrEmpty(_session.ExecutablePath))
                {
                    executablePath = _session.ExecutablePath;
                    if (!File.Exists(executablePath))
                    {
                        throw new SessionLocalException(_session, string.Format(CultureInfo.CurrentCulture, "{0} does not exists.", executablePath));
                    }
                }
                else
                {
                    if (!TryFindExecutableInPath(GetAssemblyPath(), out executablePath) &&
                        !TryFindExecutableInPath(GetInstallationPath(Registry.CurrentUser), out executablePath) &&
                        !TryFindExecutableInPath(GetInstallationPath(Registry.LocalMachine), out executablePath) &&
                        !TryFindExecutableInPath(GetDefaultInstallationPathOnx64(), out executablePath) &&
                        !TryFindExecutableInPath(GetDefaultInstallationPathOnx86(), out executablePath))
                    {
                        throw new SessionLocalException(_session,
                            string.Format(CultureInfo.CurrentCulture,
                                "The {0} executable was not found at location of the assembly ({1}), nor in an installation path. You may use Session.ExecutablePath property to explicitly set path to {0}.",
                                ExeExecutableFileName, GetAssemblyPath()));
                    }
                }
                return executablePath;
            }
        }

        private static string GetDefaultInstallationPathOnx64()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinSCP");
        }
        
        private static string GetDefaultInstallationPathOnx86()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinSCP");
        }        

        private static string GetInstallationPath(RegistryKey rootKey)
        {
            RegistryKey key = rootKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\winscp3_is1");
            return (key != null) ? (string)key.GetValue("Inno Setup: App Path") : null;
        }

        private bool TryFindExecutableInPath(string path, out string result)
        {
            if (string.IsNullOrEmpty(path))
            {
                result = null;
            }
            else
            {
                string executablePath = Path.Combine(path, ExeExecutableFileName);
                if (File.Exists(executablePath))
                {
                    result = executablePath;
                    _logger.WriteLine("Executable found in {0}", executablePath);
                }
                else
                {
                    result = null;
                    _logger.WriteLine("Executable not found in {0}", executablePath);
                }
            }
            return (result != null);
        }

        private string GetAssemblyPath()
        {
            string codeBasePath = _logger.GetAssemblyFilePath();
            string path = null;
            if (!string.IsNullOrEmpty(codeBasePath))
            {
                path = Path.GetDirectoryName(codeBasePath);
                Debug.Assert(path != null);
            }
            return path;
        }

        private void CheckVersion(string exePath, FileVersionInfo assemblyVersion)
        {
            using (_logger.CreateCallstack())
            {
                FileVersionInfo version = FileVersionInfo.GetVersionInfo(exePath);

                _logger.WriteLine("Version of {0} is {1}, product {2} version is {3}", exePath, version.FileVersion, version.ProductName, version.ProductVersion);

                if (_session.DisableVersionCheck)
                {
                    _logger.WriteLine("Version check disabled (not recommended)");
                }
                else if (assemblyVersion == null)
                {
                    _logger.WriteLine("Assembly version not known, cannot check version");
                }
                else if (assemblyVersion.ProductVersion != version.ProductVersion)
                {
                    throw new SessionLocalException(
                        _session, string.Format(CultureInfo.CurrentCulture,
                            "The version of {0} ({1}) does not match version of this assembly {2} ({3}).",
                            exePath, version.ProductVersion, _logger.GetAssemblyFilePath(), assemblyVersion.ProductVersion));
                }
            }
        }

        public void WriteStatus()
        {
            string executablePath = GetExecutablePath();
            _logger.WriteLine("{0} - exists [{1}]", executablePath, File.Exists(executablePath));
        }

        private const int MaxAttempts = 10;
        private const string ConsoleMapping = "WinSCPConsoleMapping";
        private const string ConsoleEventRequest = "WinSCPConsoleEventRequest";
        private const string ConsoleEventResponse = "WinSCPConsoleEventResponse";
        private const string ConsoleEventCancel = "WinSCPConsoleEventCancel";
        private const string ConsoleJob = "WinSCPConsoleJob";
        private const string ExeExecutableFileName = "winscp.exe";

        private Process _process;
        private readonly object _lock = new object();
        private readonly Logger _logger;
        private readonly Session _session;
        private EventWaitHandle _requestEvent;
        private EventWaitHandle _responseEvent;
        private EventWaitHandle _cancelEvent;
        private SafeFileHandle _fileMapping;
        private string _instanceName;
        private Thread _thread;
        private bool _abort;
        private string _lastFromBeginning;
        private string _incompleteLine;
        private readonly List<string> _input = new List<string>();
        private AutoResetEvent _inputEvent = new AutoResetEvent(false);
        private Job _job;
    }
}
