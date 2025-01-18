using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace POE2_AntiFreeze;

internal static partial class Program
{
    private class ProcessState
    {
        private readonly Lock _lock = new Lock();
        private bool _isInLoadingScreen;
        private bool _isUnresponsive;
        private DateTime _lastResponseTime;
        
        // As we're performing actions through various different async methods, we need to
        // apply locks to prevent race conditions etc.

        public bool IsInLoadingScreen
        {
            get
            {
                lock (_lock) { return _isInLoadingScreen; }
            }
            set
            {
                lock (_lock) { _isInLoadingScreen = value; }
            }
        }

        public bool IsUnresponsive
        {
            get
            {
                lock (_lock) { return _isUnresponsive; }
            }
            set
            {
                lock (_lock) { _isUnresponsive = value; }
            }
        }

        public DateTime LastResponseTime
        {
            get
            {
                lock (_lock) { return _lastResponseTime; }
            }
            set
            {
                lock (_lock) { _lastResponseTime = value; }
            }
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
    private static bool _initialDetectionDone;
    private static int _totalCores;
    private static bool _logFileNotificationShown;
    private static int? _cachedCoreCount;
    private static readonly ProcessState State = new ProcessState();
    private static readonly Lock ConsoleLock = new Lock();
    
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
    
    private static string? DetermineLogFilePath(Process process)
    {
        try 
        {
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
            
            // Fallback log path if not found
            WriteLineWithPrefix("warn", $"Log file not found at {logPath}. Waiting for file to become available.");
            return null;
        }
        catch (Exception ex)
        {
            WriteLineWithPrefix("error", $"Error determining log file path: {ex.Message}");
            return null;
        }
    }
    
    private static async Task MonitorProcessResponsiveness(CancellationToken cancellationToken) 
    {
        while (!cancellationToken.IsCancellationRequested) 
        {
            try 
            {
                var processes = GetGameProcesses();
                switch (processes.Length)
                {
                    case 0:
                        // No process to monitor, just wait for next cycle
                        await Task.Delay(ProcessUnresponsiveCheckPollingRateMs, cancellationToken);
                        continue;
                    case 1:
                    {
                        var process = processes[0];
                        
                        // Check if process is responding
                        if (!process.Responding)
                        {
                            if (!State.IsUnresponsive)
                            {
                                State.IsUnresponsive = true;
                                State.LastResponseTime = DateTime.Now;
                                _currentRecoveryAttempt = 0;
                                
                                WriteLineWithPrefix("warn", "Process became unresponsive. Applying temporary affinity restrictions...", true);
                                QueueAffinityChange(RestrictedExcludedCores, ProcessPriorityClass.RealTime);
                            }
                            else if (DateTime.Now - State.LastResponseTime > UnresponsiveTimeout)
                            {
                                // Calculate delay for this attempt using exponential backoff
                                var currentDelay = Math.Min(
                                    InitialRecoveryAttemptDelayMs * (int)Math.Pow(2, _currentRecoveryAttempt),
                                    MaxRecoveryAttemptDelayMs
                                );

                                WriteLineWithPrefix("warn", $"Process still unresponsive. Recovery attempt {_currentRecoveryAttempt + 1}/{MaxRecoveryAttempts}", true);
                                
                                try
                                {
                                    // First try graceful shutdown
                                    process.CloseMainWindow();
                                    await Task.Delay(ProcessCloseWindowDelayMs, cancellationToken);
                                    
                                    if (!process.HasExited)
                                    {
                                        if (_currentRecoveryAttempt >= MaxRecoveryAttempts - 1)
                                        {
                                            WriteLineWithPrefix("error", "Max recovery attempts reached. Forcing process termination.", true);
                                            process.Kill();
                                        }
                                        else
                                        {
                                            WriteLineWithPrefix("info", $"Waiting {currentDelay/1000.0:F1}s before next recovery attempt...");
                                            await Task.Delay(currentDelay, cancellationToken);
                                            _currentRecoveryAttempt++;
                                            continue;
                                        }
                                    }
                                    
                                    // Reset all states
                                    _initialDetectionDone = false;
                                    State.IsUnresponsive = false;
                                    State.IsInLoadingScreen = false;
                                    _currentRecoveryAttempt = 0;
                                    
                                    WriteLineWithPrefix("info", "Process terminated. Waiting for new instance...", true);
                                }
                                catch (Exception ex)
                                {
                                    WriteLineWithPrefix("error", $"Failed to handle unresponsive process: {ex.Message}");
                                }
                            }
                        }
                        else
                        {
                            // Process is responding
                            if (State.IsUnresponsive)
                            {
                                WriteLineWithPrefix("info", $"Process recovered after {_currentRecoveryAttempt} attempts. Restoring normal configuration.", true);
                                QueueAffinityChange(DefaultExcludedCores, ProcessPriorityClass.Normal);
                                State.IsUnresponsive = false;
                                _currentRecoveryAttempt = 0;
                            }
                            
                            // Update last response time
                            State.LastResponseTime = DateTime.Now;
                        }
                        break;
                    }
                    default:
                        // Multiple processes detected, skip monitoring cycle
                        await Task.Delay(ProcessUnresponsiveCheckPollingRateMs, cancellationToken);
                        continue;
                }
            }
            catch (Exception ex)
            {
                WriteLineWithPrefix("error", $"Error monitoring process responsiveness: {ex.Message}");
            }

            await Task.Delay(ProcessUnresponsiveCheckPollingRateMs, cancellationToken);
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
    
    private static Process[] GetGameProcesses()
    {
        return ProcessNames.SelectMany(Process.GetProcessesByName).ToArray();
    }

    private static async Task MonitorProcess(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var processes = GetGameProcesses();
                
                if (processes.Length > 1)
                {
                    // Multiple processes detected
                    if (_initialDetectionDone)
                    {
                        // Reset monitoring state
                        _initialDetectionDone = false;
                        _logFilePath = string.Empty;
                        _processDirectory = string.Empty;
                        _cachedCoreCount = null;
                    }
                    
                    WriteLineWithPrefix("warn", "Multiple game processes detected. Please ensure only one instance is running.", true);
                    await Task.Delay(ProcessMonitorPollingRateMs, cancellationToken);
                    continue;
                }

                switch (processes.Length)
                {
                    case 1 when !_initialDetectionDone:
                    {
                        var process = processes[0];
                        
                        // Give any pending writes time to complete
                        await Task.Delay(ProcessDetectionInitialDelayMs, cancellationToken);
                        WriteLineWithPrefix("info", $"{process.ProcessName} ({process.Id}) detected.");
                        // Small delay between status messages
                        await Task.Delay(ProcessDetectionStatusDelayMs, cancellationToken);
                    
                        // Determine log file path dynamically
                        var detectedLogPath = DetermineLogFilePath(process);
                    
                        // Wait for log file to become available
                        while (detectedLogPath == null && !cancellationToken.IsCancellationRequested)
                        {
                            if (!_logFileNotificationShown)
                            {
                                WriteLineWithPrefix("warn", "Waiting for log file to become available. Please ensure the game is fully launched.", true);
                                _logFileNotificationShown = true;
                            }

                            await Task.Delay(LogFileRetryDelay, cancellationToken);
                            detectedLogPath = DetermineLogFilePath(process);
                        }

                        // Reset notification flag once log file is found
                        _logFileNotificationShown = false;

                        // Set log file path
                        _logFilePath = detectedLogPath ?? string.Empty;
                    
                        // Save current affinity
                        long currentAffinity = process.ProcessorAffinity;
                    
                        // Get real core count by testing what Windows allows
                        _totalCores = GetTotalAvailableCores(process);
                        WriteLineWithPrefix("info", $"{_totalCores} CPUs available to process.");
                        // Small delay between status messages
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
                            WriteLineWithPrefix("action", $"Applying default affinity (Excluded CPUs: {defaultExcludedCoresOutput})");
                            QueueAffinityChange(DefaultExcludedCores, ProcessPriorityClass.Normal);
                        }
                    
                        _initialDetectionDone = true;
                        break;
                    }
                    case 0 when _initialDetectionDone:
                        WriteLineWithPrefix("info", "Process closed. Waiting for new instance...", true);
                        _initialDetectionDone = false;
                        _logFilePath = string.Empty;
                        _processDirectory = string.Empty;
                        _cachedCoreCount = null;
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
                
                // We can't use a FileSystemWatcher to monitor the log file for changes as it doesn't seem to detect
                // when changes are made. We need to poll it at given intervals.

                await using var fs = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs, Encoding.UTF8, true, 1024);

                // Initialize last position if not set
                if (_lastPosition == 0)
                {
                    _lastPosition = fs.Length;
                }

                fs.Seek(_lastPosition, SeekOrigin.Begin);
                
                // Run the most recent line against our pre-defined regexps
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
        // If in a loading screen, prioritize log-based actions
        if (State.IsUnresponsive)
        {
            // If we get log matches while unresponsive, consider the process as potentially recovering
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

    private static async Task SetProcessorAffinity(int[] excludedCores, ProcessPriorityClass priority, CancellationToken cancellationToken)
    {
        try
        {
            var processes = GetGameProcesses();
            
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
                // Report current configuration instead of showing a non-change
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
            
            // Use async delay
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
    
    private static long CalculateAffinityMask(int[] excludedCores)
    {
        // Enable all CPUs by default
        var mask = (1L << _totalCores) - 1;
        // Be sure to remove duplicates as this will hinder out bitmask
        excludedCores = excludedCores.Distinct().OrderBy(x => x).ToArray();
        // Double check that the manually specified exclusions can actually be applied
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
}