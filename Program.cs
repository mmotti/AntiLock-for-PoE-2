using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace POE2_AntiFreeze;

internal static partial class Program
{
    public enum ProcessStatus
    {
        Unknown,        // Initial state before we know anything
        Running,        // Process is running normally
        Unresponsive,  // Process has stopped responding to Windows messages
        Recovering,    // We're trying to recover the process
        Terminated     // Process has exited
    }

    private class ProcessState
    {
        private readonly Lock _lock = new Lock();
        private ProcessStatus _status = ProcessStatus.Unknown;  // Start in Unknown state
        private DateTime _lastResponseTime = DateTime.Now;
        private bool _isInLoadingScreen;
    
        public ProcessStatus Status
        {
            get { lock (_lock) { return _status; } }
            set 
            { 
                lock (_lock)
                {
                    if (_status == value) return;
                    // Only log state changes after we leave Unknown state
                    if (_status != ProcessStatus.Unknown)
                    {
                        WriteLineWithPrefix("info", $"Process state changed: {_status} -> {value}");
                    }
                    _status = value;
                } 
            }
        }

        public DateTime LastResponseTime
        {
            get { lock (_lock) { return _lastResponseTime; } }
            set { lock (_lock) { _lastResponseTime = value; } }
        }

        public bool IsInLoadingScreen
        {
            get { lock (_lock) { return _isInLoadingScreen; } }
            set { lock (_lock) { _isInLoadingScreen = value; } }
        }
    }
    
    // Watch configuration
    private static readonly string[] ProcessNames = ["PathOfExileSteam", "PathOfExile"];
    private const string LogFilename = "Client.txt";
    private const string LogSubdirectory = "logs";
    
    // Polling rates
    private const int ProcessMonitorPollingRateMs = 1000;
    private const int AffinityChangesPollingRateMs = 100;
    private const int LogMonitorPollingRateMs = 250;
    private const int ProcessUnresponsiveCheckPollingRateMs = 1000;
    
    // Delays
    private const int ProcessDetectionInitialDelayMs = 100;
    private const int ProcessDetectionStatusDelayMs = 50;
    private const int ProcessCloseWindowDelayMs = 3000;
    private const int ProcessAffinityApplyDelayMs = 100;
    
    // Recovery attempts for gracefully closing the window
    private const int InitialRecoveryAttemptDelayMs = 1000;
    private const int MaxRecoveryAttemptDelayMs = 5000;
    private const int MaxRecoveryAttempts = 3;
    private static int _currentRecoveryAttempt;
    
    // Timeouts
    private static readonly TimeSpan UnresponsiveTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LogFileRetryDelay = TimeSpan.FromSeconds(5);
    
    // Specific increments
    private const int IncrementCoresByCount = 4;
    
    // Misc initialisation
    private static string _logFilePath = string.Empty;
    private static string _processDirectory = string.Empty;
    private static long _lastPosition;
    private static int _totalCores;
    private static int? _cachedCoreCount;
    
    // Flags
    private static bool _initialDetectionDone;
    private static bool _logFileNotificationShown;
    
    // Locks
    private static readonly ProcessState State = new();
    private static readonly Lock ConsoleLock = new();
    
    // Queue for handling affinity changes
    private static readonly ConcurrentQueue<(int[] excludedCores, ProcessPriorityClass priority)> PendingAffinityChanges = new();
    private static volatile bool _processingChanges;
    
    // Regexps to determine game state
    private static readonly Regex GameLaunchRegex = GameLaunchGenRegex();
    private static readonly Regex StartLoadingScreenRegex = StartLoadingScreenGenRegex();
    private static readonly Regex EndOfLoadingScreenRegex = EndOfLoadingScreenGenRegex();
    
    // Restriction parameters
    private static readonly int[] DefaultExcludedCores = [];
    private static readonly int[] RestrictedExcludedCores = [0, 1, 2, 3];


    private static async Task Main(string[] args)
    {
        Console.WriteLine("""
                              _          _   _ _____                      
                             / \   _ __ | |_(_)  ___| __ ___  ___ _______
                            / _ \ | '_ \| __| | |_ | '__/ _ \/ _ \_  / _ \
                           / ___ \| | | | |_| |  _|| | |  __/  __// /  __/
                          /_/   \_\_| |_|\__|_|_|  |_|  \___|\___/___\___|
                                                                          
                          With a little (a LOT of) assistance from Claude 3.5 Sonnet.
                          Inspired by PoEUncrasher by Kapps (https://github.com/Kapps/PoEUncrasher)
                           
                          """);
        
        WriteLineWithPrefix("warn", "Keep me running in the background!", includeTimestamp:false);
        WriteLineWithPrefix("warn", "Make sure to launch me before PoE 2.", includeTimestamp:false);
        
        Console.WriteLine();
        
        WriteLineWithPrefix("action", "Generating fortune's favour...", true);

        GetPoEFortune().ToList().ForEach(line => WriteLineWithPrefix("info", line));

        Console.WriteLine();
        WriteLineWithPrefix("info", $"Waiting for game process...", true);

        var cancellationTokenSource = new CancellationTokenSource();
        
        // Start process monitoring task
        var processMonitorTask = Task.Run(() => MonitorProcess(cancellationTokenSource.Token), cancellationTokenSource.Token);
        
        // Start log monitoring task
        var logMonitorTask = Task.Run(() => ReadLogContinuously(cancellationTokenSource.Token), cancellationTokenSource.Token);
        
        // Start affinity processing task
        var affinityProcessingTask = Task.Run(() => ProcessAffinityChanges(cancellationTokenSource.Token), cancellationTokenSource.Token);

        // Start process responsiveness monitoring task
        var responsivenessMonitorTask = Task.Run(() => MonitorProcessResponsiveness(cancellationTokenSource.Token), cancellationTokenSource.Token);

        // Keep the application running indefinitely
        try 
        {
            await Task.WhenAll(processMonitorTask, logMonitorTask, affinityProcessingTask, responsivenessMonitorTask);
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            WriteLineWithPrefix("error", $"Unhandled exception: {ex.Message}");
        }
    }

    private static string[] GetPoEFortune()
    {
        var exaltedCount = Random.Shared.Next(10, 25);
        var divineCount = Random.Shared.Next(1, 3);
        return
        [
            "The ancients have spoken. Your next farming session holds great promise!",
            $"The gods have foreseen {exaltedCount} Exalted {(exaltedCount == 1 ? "Orb" : "Orbs")} and {divineCount} Divine {(divineCount == 1 ? "Orb" : "Orbs")}."
        ];
    }

    [GeneratedRegex(@"\[ENGINE\]\s+Init$", RegexOptions.Compiled)]
    private static partial Regex GameLaunchGenRegex();
    [GeneratedRegex(@"\[SHADER\]\s+Delay:\s+OFF$", RegexOptions.Compiled)]
    private static partial Regex StartLoadingScreenGenRegex();
    [GeneratedRegex(@"\[SHADER\]\s+Delay:\s+ON$", RegexOptions.Compiled)]
    private static partial Regex EndOfLoadingScreenGenRegex();

    private static async Task MonitorProcess(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var processes = await GetGameProcesses();
                
                switch (processes.Length)
                {
                    case > 1:
                    {
                        if (_initialDetectionDone)
                        {
                            ResetMonitorState();
                        }
                    
                        WriteLineWithPrefix("warn", "Multiple game processes detected. Please ensure only one instance is running.", true);
                        await Task.Delay(ProcessMonitorPollingRateMs, cancellationToken);
                        continue;
                    }
                    case 1 when !_initialDetectionDone:
                    {
                        var process = processes[0];
                                
                        await Task.Delay(ProcessDetectionInitialDelayMs, cancellationToken);
                        // First output the detection message
                        WriteLineWithPrefix("info", $"{process.ProcessName} ({process.Id}) detected.");
                        await Task.Delay(ProcessDetectionStatusDelayMs, cancellationToken);
                        // Start trying to get the log file and other info
                        try 
                        {
                            // Wait for log file with proper async handling
                            _logFilePath = await WaitForLogFile(process, cancellationToken);
                        
                            if (string.IsNullOrEmpty(_logFilePath))
                            {
                                // Process likely exited during log file detection
                                WriteLineWithPrefix("info", "Process closed. Waiting for new instance...", true);
                                ResetMonitorState();
                                continue;
                            }

                            // Reset notification flag once log file is found
                            _logFileNotificationShown = false;

                            // Save current affinity
                            long currentAffinity = process.ProcessorAffinity;
                    
                            // Get real core count by testing what Windows allows
                            _totalCores = GetTotalAvailableCores(process);
                            WriteLineWithPrefix("info", $"{_totalCores} CPUs available to process.");
                            await Task.Delay(ProcessDetectionStatusDelayMs, cancellationToken);
                    
                            // Check if current affinity matches our default
                            var defaultMask = CalculateAffinityMask(DefaultExcludedCores);
                            var defaultExcludedCoresOutput = DefaultExcludedCores.Length > 0
                                ? string.Join(", ", DefaultExcludedCores)
                                : "None";

                            if (currentAffinity == defaultMask)
                            {
                                WriteLineWithPrefix("info", $"Default CPU configuration validated (Excluded CPUs: {defaultExcludedCoresOutput})");
                            }
                            else
                            {
                                WriteLineWithPrefix("action", $"Applying default affinity (Excluded CPUs: {defaultExcludedCoresOutput})", true);
                                QueueAffinityChange(DefaultExcludedCores, ProcessPriorityClass.Normal);
                            }

                            // Now set the status to Running - we've verified everything is okay
                            State.Status = ProcessStatus.Running;
                            _initialDetectionDone = true;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            // Normal cancellation, just exit
                            return;
                        }
                        catch (Exception ex)
                        {
                            WriteLineWithPrefix("error", $"Error during process initialization: {ex.Message}");
                            ResetMonitorState();
                        }

                        break;
                    }
                    case 0 when _initialDetectionDone:
                        WriteLineWithPrefix("info", "Process closed. Waiting for new instance...", true);
                        ResetMonitorState();
                        break;
                }
            }
            catch (Exception ex)
            {
                WriteLineWithPrefix("error", $"Error monitoring process: {ex.Message}");
            }

            await Task.Delay(ProcessMonitorPollingRateMs, cancellationToken);
        }
    }

    private static async Task<string> WaitForLogFile(Process process, CancellationToken cancellationToken)
    {
        using var processLifetimeToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        
        // Create a task that completes when the process exits
        var processExitTask = Task.Run(async () =>
        {
            while (!process.HasExited && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, cancellationToken);
            }
        }, cancellationToken);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var logPath = DetermineLogFilePath(process);
                
                if (!string.IsNullOrEmpty(logPath))
                {
                    return logPath;
                }

                if (process.HasExited)
                {
                    return string.Empty;
                }

                // Wait for either the delay to complete or the process to exit
                var delayTask = Task.Delay(LogFileRetryDelay, cancellationToken);
                var completedTask = await Task.WhenAny(delayTask, processExitTask);
                
                if (completedTask == processExitTask)
                {
                    return string.Empty;
                }
            }

            return string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return string.Empty;
        }
    }

    private static string? DetermineLogFilePath(Process process)
    {
        try
        {
            // Check if process has exited before trying to access any properties
            if (process.HasExited)
            {
                return null;
            }

            // Get the directory of the process executable
            var processDir = process.MainModule?.FileName;
            if (string.IsNullOrEmpty(processDir))
            {
                WriteLineWithPrefix("error", "Could not determine process executable path.");
                return null;
            }

            _processDirectory = Path.GetDirectoryName(processDir) ?? string.Empty;
        
            // Construct log file path
            var logPath = Path.Combine(_processDirectory, LogSubdirectory, LogFilename);
        
            // Validate log file exists
            if (File.Exists(logPath))
            {
                WriteLineWithPrefix("info", $"Log file detected: {LogFilename}");
                return logPath;
            }

            if (_logFileNotificationShown) return null;
        
            WriteLineWithPrefix("warn", $"Log file not found at {logPath}. Waiting for file to become available.");
            _logFileNotificationShown = true;

            return null;
        }
        catch (Exception ex)
        {
            // Don't log if the error is just that the process exited
            if (!process.HasExited)
            {
                WriteLineWithPrefix("error", $"Error determining log file path: {ex.Message}");
            }
            return null;
        }
    }

    private static async Task MonitorProcessResponsiveness(CancellationToken cancellationToken) 
    {
        while (!cancellationToken.IsCancellationRequested) 
        {
            try 
            {
                // Skip monitoring until initial detection is done
                if (!_initialDetectionDone)
                {
                    await Task.Delay(ProcessUnresponsiveCheckPollingRateMs, cancellationToken);
                    continue;
                }

                var processes = await GetGameProcesses();
                if (processes.Length != 1)
                {
                    if (State.Status != ProcessStatus.Terminated)
                    {
                        State.Status = ProcessStatus.Terminated;
                    }
                    await Task.Delay(ProcessUnresponsiveCheckPollingRateMs, cancellationToken);
                    continue;
                }

                var process = processes[0];
                
                if (process.HasExited)
                {
                    State.Status = ProcessStatus.Terminated;
                    await Task.Delay(ProcessUnresponsiveCheckPollingRateMs, cancellationToken);
                    continue;
                }

                if (State.IsInLoadingScreen)
                {
                    // Still check if process is responding, but use a longer timeout
                    if (!process.Responding)
                    {
                        // Only start recovery if we haven't seen any log activity for a while
                        var timeSinceLastResponse = DateTime.Now - State.LastResponseTime;
                        if (timeSinceLastResponse > UnresponsiveTimeout * 2)  // Double timeout during loading
                        {
                            WriteLineWithPrefix("warn", "Process appears to have crashed during loading screen.", true);
                            State.Status = ProcessStatus.Recovering;
                            await AttemptProcessRecovery(process, cancellationToken);
                        }
                    }
                    await Task.Delay(ProcessUnresponsiveCheckPollingRateMs, cancellationToken);
                    continue;
                }

                // Check process responsiveness
                if (process.Responding)
                {
                    // Only restore normal config if we were previously unresponsive
                    if (State.Status is ProcessStatus.Unresponsive or ProcessStatus.Recovering)
                    {
                        WriteLineWithPrefix("info", $"Process recovered after {_currentRecoveryAttempt} attempts. Restoring normal configuration.", true);
                        State.Status = ProcessStatus.Running;
                        QueueAffinityChange(DefaultExcludedCores, ProcessPriorityClass.Normal);
                        _currentRecoveryAttempt = 0;
                    }
                    State.LastResponseTime = DateTime.Now;
                }
                else 
                {
                    // Process is not responding
                    switch (State.Status)
                    {
                        case ProcessStatus.Running:
                            // Just became unresponsive
                            State.Status = ProcessStatus.Unresponsive;
                            State.LastResponseTime = DateTime.Now;
                            _currentRecoveryAttempt = 0;
                            WriteLineWithPrefix("warn", "Process became unresponsive. Applying temporary affinity restrictions...", true);
                            QueueAffinityChange(RestrictedExcludedCores, ProcessPriorityClass.RealTime);
                            break;

                        case ProcessStatus.Unresponsive:
                            // Check if we've waited long enough to start recovery
                            if (DateTime.Now - State.LastResponseTime > UnresponsiveTimeout)
                            {
                                State.Status = ProcessStatus.Recovering;
                                await AttemptProcessRecovery(process, cancellationToken);
                            }
                            break;

                        case ProcessStatus.Recovering:
                            await AttemptProcessRecovery(process, cancellationToken);
                            break;

                        case ProcessStatus.Unknown:
                            // This shouldn't happen as we only monitor after initialization
                            WriteLineWithPrefix("error", "Process in Unknown state during responsiveness check - this shouldn't happen");
                            break;

                        case ProcessStatus.Terminated:
                            // This shouldn't happen as we check for termination earlier
                            WriteLineWithPrefix("error", "Process marked as Terminated during responsiveness check - this shouldn't happen");
                            break;

                        default:
                            // Handle any future enum values that might be added
                            WriteLineWithPrefix("error", $"Unhandled process state {State.Status} during responsiveness check");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                WriteLineWithPrefix("error", $"Error monitoring process responsiveness: {ex.Message}");
            }

            await Task.Delay(ProcessUnresponsiveCheckPollingRateMs, cancellationToken);
        }
    }

    private static async Task AttemptProcessRecovery(Process process, CancellationToken cancellationToken)
    {
        // Calculate delay for this attempt using exponential backoff
        var currentDelay = Math.Min(
            InitialRecoveryAttemptDelayMs * (int)Math.Pow(2, _currentRecoveryAttempt),
            MaxRecoveryAttemptDelayMs
        );

        WriteLineWithPrefix("warn", $"Attempting process recovery ({_currentRecoveryAttempt + 1}/{MaxRecoveryAttempts})", true);

        try
        {
            // Create a task that completes when the process exits
            var processExitTask = Task.Run(async () => {
                try {
                    process.CloseMainWindow();
                    while (!process.HasExited && !cancellationToken.IsCancellationRequested)
                    {
                        await Task.Delay(100, cancellationToken);
                    }
                }
                catch (Exception) {
                    // Process may have already exited
                }
            }, cancellationToken);

            // Wait for either the graceful shutdown or the timeout
            var timeoutTask = Task.Delay(ProcessCloseWindowDelayMs, cancellationToken);
            var completedTask = await Task.WhenAny(processExitTask, timeoutTask);
            
            if (completedTask == timeoutTask && !process.HasExited)
            {
                if (_currentRecoveryAttempt >= MaxRecoveryAttempts - 1)
                {
                    WriteLineWithPrefix("error", "Max recovery attempts reached. Forcing process termination.", true);
                    process.Kill();
                    State.Status = ProcessStatus.Terminated;
                }
                else
                {
                    WriteLineWithPrefix("info", $"Waiting {currentDelay/1000.0:F1}s before next recovery attempt...");
                    await Task.Delay(currentDelay, cancellationToken);
                    _currentRecoveryAttempt++;
                }
            }
        }
        catch (Exception ex)
        {
            WriteLineWithPrefix("error", $"Failed during recovery attempt: {ex.Message}");
        }
    }

    private static async Task<Process[]> GetGameProcesses()
    {
        // Get initial processes
        var processes = ProcessNames.SelectMany(Process.GetProcessesByName).ToArray();
    
        if (processes.Length == 0)
            return processes;

        // If we found any processes, wait a tiny bit and verify they still exist
        // This helps avoid detecting processes that are in the process of exiting
        await Task.Delay(50);
    
        return processes.Where(p => 
        {
            try 
            {
                // HasExited will throw if process is already gone
                return !p.HasExited;
            }
            catch 
            {
                return false;
            }
        }).ToArray();
    }

    private static async Task ReadLogContinuously(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Skip if no log path is set
                if (string.IsNullOrEmpty(_logFilePath) || !File.Exists(_logFilePath))
                {
                    await Task.Delay(LogMonitorPollingRateMs, cancellationToken);
                    continue;
                }
                
                // We can't use a FileSystemWatcher as it doesn't reliably detect changes
                // Instead we poll at regular intervals
                await using var fs = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs, Encoding.UTF8, true, 1024);

                // Initialize last position if not set
                if (_lastPosition == 0)
                {
                    _lastPosition = fs.Length;
                }

                fs.Seek(_lastPosition, SeekOrigin.Begin);
                
                // Process each new line
                while (await sr.ReadLineAsync(cancellationToken) is { } newLine)
                {
                    MatchLogLine(newLine);
                    _lastPosition = fs.Position;
                }
            }
            catch (IOException ex)
            {
                WriteLineWithPrefix("error", $"Log file error: {ex.Message}");
            }

            await Task.Delay(LogMonitorPollingRateMs, cancellationToken);
        }
    }

    private static void MatchLogLine(string line)
    {
        // If unresponsive, treat new log entries as signs of potential recovery
        if (State.Status == ProcessStatus.Unresponsive || State.Status == ProcessStatus.Recovering)
        {
            State.LastResponseTime = DateTime.Now;
        }

        if ((GameLaunchRegex.IsMatch(line) || StartLoadingScreenRegex.IsMatch(line)) && !State.IsInLoadingScreen)
        {
            State.IsInLoadingScreen = true;
            WriteLineWithPrefix("action", "Applying restricted CPU affinity...", true);
            QueueAffinityChange(RestrictedExcludedCores, ProcessPriorityClass.RealTime);
        }
        else if (EndOfLoadingScreenRegex.IsMatch(line) && State.IsInLoadingScreen)
        {
            State.IsInLoadingScreen = false;
            WriteLineWithPrefix("action", "Reverting to default CPU affinity...", true);
            QueueAffinityChange(DefaultExcludedCores, ProcessPriorityClass.Normal);
        }
    }

    private static async Task ProcessAffinityChanges(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (PendingAffinityChanges.TryDequeue(out var change) && !_processingChanges)
                {
                    _processingChanges = true;
                    await SetProcessorAffinity(change.excludedCores, change.priority, cancellationToken);
                    _processingChanges = false;
                }
            }
            catch (Exception ex)
            {
                WriteLineWithPrefix("error", $"Error processing affinity changes: {ex.Message}");
            }
            await Task.Delay(AffinityChangesPollingRateMs, cancellationToken);
        }
    }

    private static void QueueAffinityChange(int[] excludedCores, ProcessPriorityClass priority)
    {
        // Clear any pending changes
        while (PendingAffinityChanges.TryDequeue(out _)) { }
        
        // Queue the new change
        PendingAffinityChanges.Enqueue((excludedCores, priority));
    }

    private static async Task SetProcessorAffinity(int[] excludedCores, ProcessPriorityClass priority, CancellationToken cancellationToken)
    {
        try
        {
            var processes = await GetGameProcesses();
            
            switch (processes.Length)
            {
                case 0:
                    WriteLineWithPrefix("error", "Process not found.");
                    return;
                case > 1:
                    WriteLineWithPrefix("error", "Multiple game processes detected. Skipping affinity change.");
                    return;
            }

            var process = processes[0];
            var currentAffinity = process.ProcessorAffinity;
            var newAffinityMask = CalculateAffinityMask(excludedCores);
            
            // If affinity is already what we want, just verify priority
            if (currentAffinity == newAffinityMask)
            {
                WriteLineWithPrefix("info", $"CPU configuration already at desired state ({CountActiveCores(currentAffinity)}/{_totalCores} active)");
                
                // Only update priority if it's different
                if (process.PriorityClass != priority)
                {
                    try
                    {
                        process.PriorityClass = priority;
                        WriteLineWithPrefix("info", $"Process priority set to {priority}");
                    }
                    catch (Exception ex)
                    {
                        WriteLineWithPrefix("error", $"Failed to set process priority: {ex.Message}");
                    }
                }
                return;
            }
            
            // Set affinity
            process.ProcessorAffinity = checked((IntPtr)newAffinityMask);
            
            // Set priority
            try
            {
                process.PriorityClass = priority;
                WriteLineWithPrefix("info", $"Process priority set to {priority}");
            }
            catch (Exception ex)
            {
                WriteLineWithPrefix("error", $"Failed to set process priority: {ex.Message}");
            }
            
            await Task.Delay(ProcessAffinityApplyDelayMs, cancellationToken);
            
            long verifiedAffinity = process.ProcessorAffinity;
            PrintAffinityChange(currentAffinity, verifiedAffinity);
            
            if (verifiedAffinity != newAffinityMask)
            {
                WriteLineWithPrefix("error", "Affinity change failed");
            }
        }
        catch (Exception ex)
        {
            WriteLineWithPrefix("error", $"Affinity error: {ex.Message}");
        }
    }

    private static void PrintAffinityChange(long before, long after)
    {
        var beforeActive = CountActiveCores(before);
        var afterActive = CountActiveCores(after);
        WriteLineWithPrefix("info", $"CPUs: {beforeActive}/{_totalCores} -> {afterActive}/{_totalCores} | Mask: 0x{before:X4} -> 0x{after:X4} ({Convert.ToString(before, 2).PadLeft(_totalCores, '0')} -> {Convert.ToString(after, 2).PadLeft(_totalCores, '0')})");
    }

    private static int GetTotalAvailableCores(Process gameProcess)
    {
        // Return cached value if available
        if (_cachedCoreCount.HasValue)
        {
            return _cachedCoreCount.Value;
        }

        try
        {
            // Save current affinity
            var currentAffinity = (long)gameProcess.ProcessorAffinity;

            try
            {
                // Try to find the max total CPUs that the game process will accept using increments
                long testMask = 0;
                long lastWorkingMask = 0;
                var lastWorkingI = 0;
                
                for (var i = IncrementCoresByCount; i <= 64; i += IncrementCoresByCount)
                {
                    testMask = (1L << i) - 1;
                    try
                    {
                        gameProcess.ProcessorAffinity = checked((IntPtr)testMask);
                        lastWorkingMask = testMask;
                        lastWorkingI = i;
                    }
                    catch
                    {
                        // If this failed, start from last working position and increment by 1
                        testMask = lastWorkingMask;
                        for (var j = lastWorkingI; j < i; j++)
                        {
                            try
                            {
                                var newMask = testMask | (1L << j);
                                gameProcess.ProcessorAffinity = checked((IntPtr)newMask);
                                testMask = newMask;
                            }
                            catch
                            {
                                // Found our limit
                                break;
                            }
                        }
                        // Exit main loop as we've found our limit
                        break;
                    }
                }

                // Cache and return the count
                _cachedCoreCount = CountActiveCores(testMask);
                return _cachedCoreCount.Value;
            }
            finally
            {
                // Always restore original affinity
                gameProcess.ProcessorAffinity = checked((IntPtr)currentAffinity);
            }
        }
        catch (Exception ex)
        {
            WriteLineWithPrefix("error", $"Failed to determine total CPU count: {ex.Message}");
            // Don't cache fallback value in case of error
            return CountActiveCores(gameProcess.ProcessorAffinity);
        }
    }

    private static long CalculateAffinityMask(int[] excludedCores)
    {
        // Enable all CPUs by default
        var mask = (1L << _totalCores) - 1;
        // Remove duplicates and sort
        excludedCores = excludedCores.Distinct().OrderBy(x => x).ToArray();
        // Validate exclusions are within range
        if (excludedCores.Any(core => core >= _totalCores || core < 0))
        {
            throw new ArgumentException($"Excluded processor indices must be within range 0 to {_totalCores - 1}.");
        }

        return excludedCores.Aggregate(mask, (current, core) => current & ~(1L << core));
    }

    private static int CountActiveCores(long affinityMask) 
    {
        var count = 0;
        for (var i = 0; i < 64; i++)
        {
            if ((affinityMask & (1L << i)) != 0)
            {
                count++;
            }
        } 
        return count; 
    }

    private static void WriteLineWithPrefix(string type, string message, bool bold = false, bool includeTimestamp = true)
    {
        lock (ConsoleLock)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        
            // Define normal and bold colors for each type
            (ConsoleColor normal, ConsoleColor bold) prefixColors = type.ToLower() switch
            {
                "error" => (ConsoleColor.DarkRed, ConsoleColor.Red),
                "warn" => (ConsoleColor.DarkYellow, ConsoleColor.Yellow),
                "info" => (ConsoleColor.DarkGreen, ConsoleColor.Green),
                "action" => (ConsoleColor.DarkCyan, ConsoleColor.Cyan),
                _ => (Console.ForegroundColor, Console.ForegroundColor)
            };

            var prefix = type.ToLower() switch
            {
                "error" => "[!]",
                "warn" => "[!]",
                "info" => "[i]",
                "action" => "[>]",
                _ => "[-]"
            };

            var originalColor = Console.ForegroundColor;
        
            // Write timestamp
            Console.Write(includeTimestamp ? $"[{timestamp}] " : string.Empty);
        
            // Write prefix with normal intensity color
            Console.ForegroundColor = prefixColors.normal;
            Console.Write($"{prefix} ");
        
            // Write message with either normal or bold (bright) color
            Console.ForegroundColor = bold ? prefixColors.bold : prefixColors.normal;
            Console.Write(message);
        
            // Reset color
            Console.ForegroundColor = originalColor;
            Console.WriteLine();
        }
    }

    private static void ResetMonitorState()
    {
        _initialDetectionDone = false;
        _logFilePath = string.Empty;
        _processDirectory = string.Empty;
        _cachedCoreCount = null;
        _logFileNotificationShown = false;
        State.Status = ProcessStatus.Unknown;  // Reset to Unknown instead of Terminated
    }
}