using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace WinBatLens.Services
{
    /// <summary>
    /// Keeps exactly one WinBat Lens alive per user session.
    ///
    /// A duplicate launch used to call Shutdown() and vanish, which looks
    /// identical to the app failing to start. Instead it now hands the running
    /// instance the foreground so its window appears — which is what
    /// double-clicking the shortcut was asking for — and, when the two builds
    /// are different versions, offers to replace the running one.
    /// </summary>
    public static class SingleInstanceService
    {
        // These three names are a cross-version contract: a new build has to
        // recognise, signal and replace an OLD build that is already running,
        // so none of them may ever carry the version number. MutexName in
        // particular must stay byte-for-byte what earlier releases used.
        private const string MutexName = "WinBatLens_SingleInstance_Mutex";
        private const string ActivateEventName = "WinBatLens_Activate_Event";
        private const string ExitEventName = "WinBatLens_Exit_Event";

        // Generous enough for a cold machine to finish releasing sensor
        // handles, short enough that a wedged instance does not hang the
        // launch forever.
        private const int HandoffTimeoutMs = 8000;

        private const int SW_RESTORE = 9;

        private static Mutex? _mutex;
        private static EventWaitHandle? _activateEvent;
        private static EventWaitHandle? _exitEvent;
        private static RegisteredWaitHandle? _activateWait;
        private static RegisteredWaitHandle? _exitWait;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        /// <summary>
        /// True if this process should carry on starting; false if another
        /// instance owns the session and the caller should shut down. Any
        /// user interaction (version prompt, "already running" notice) has
        /// already happened by the time this returns.
        /// </summary>
        public static bool TryClaimOwnership()
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            if (createdNew) return true;

            var running = FindRunningInstance();
            string? runningVersion = ReadVersion(running);

            // A version we could not read is treated as a match. Guessing
            // wrong the other way would offer to kill a process for no reason.
            if (running != null && runningVersion != null && runningVersion != AppInfo.Version)
            {
                if (AskToReplace(runningVersion))
                {
                    if (TryReplace(running)) return true;

                    System.Windows.MessageBox.Show(
                        string.Format(LocalizationService.Get("InstanceReplaceFailedText"), runningVersion),
                        LocalizationService.Get("InstanceReplaceFailedTitle"),
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }
            }

            ActivateRunningInstance(running);
            return false;
        }

        /// <summary>
        /// Starts listening for duplicate launches. Both callbacks fire on a
        /// thread-pool thread, so the caller is responsible for marshalling
        /// them onto the UI dispatcher.
        /// </summary>
        public static void StartListening(Action onActivate, Action onExitRequested)
        {
            try
            {
                _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
                _activateWait = ThreadPool.RegisterWaitForSingleObject(
                    _activateEvent,
                    (state, timedOut) => { if (!timedOut) onActivate(); },
                    null, Timeout.Infinite, executeOnlyOnce: false);

                _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
                _exitWait = ThreadPool.RegisterWaitForSingleObject(
                    _exitEvent,
                    (state, timedOut) => { if (!timedOut) onExitRequested(); },
                    null, Timeout.Infinite, executeOnlyOnce: false);
            }
            catch (Exception ex)
            {
                // Losing the listener costs a nicety, not the app: duplicate
                // launches fall back to the message box path.
                Debug.WriteLine($"SingleInstanceService.StartListening error: {ex.Message}");
            }
        }

        public static void Release()
        {
            try { _activateWait?.Unregister(null); } catch { }
            try { _exitWait?.Unregister(null); } catch { }
            try { _activateEvent?.Dispose(); } catch { }
            try { _exitEvent?.Dispose(); } catch { }

            // Throws when this process never owned the mutex — the normal case
            // for a duplicate launch on its way out.
            try { _mutex?.ReleaseMutex(); } catch { }
            try { _mutex?.Dispose(); } catch { }
        }

        private static Process? FindRunningInstance()
        {
            try
            {
                var self = Process.GetCurrentProcess();
                foreach (var process in Process.GetProcessesByName(self.ProcessName))
                {
                    // The mutex has no "Global\" prefix, so whoever owns it is
                    // in this logon session; a copy under another login is not
                    // ours to touch.
                    if (process.Id != self.Id && process.SessionId == self.SessionId)
                    {
                        return process;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FindRunningInstance error: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Reads the running instance's version off its EXE rather than asking
        /// the process for it. That is the whole point: it works against builds
        /// that predate this class and have no way to answer a question.
        /// </summary>
        private static string? ReadVersion(Process? process)
        {
            if (process == null) return null;

            try
            {
                var info = process.MainModule?.FileVersionInfo;
                if (info == null) return null;

                return AppInfo.Normalize(info.ProductVersion) ?? AppInfo.Normalize(info.FileVersion);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ReadVersion error: {ex.Message}");
                return null;
            }
        }

        private static bool AskToReplace(string runningVersion)
        {
            var answer = System.Windows.MessageBox.Show(
                string.Format(LocalizationService.Get("InstanceVersionText"), runningVersion, AppInfo.DisplayVersion),
                LocalizationService.Get("InstanceVersionTitle"),
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question,
                System.Windows.MessageBoxResult.Yes);

            return answer == System.Windows.MessageBoxResult.Yes;
        }

        private static bool TryReplace(Process running)
        {
            try
            {
                // Ask before killing. A build carrying this class tears down its
                // own tray icon on the way out; Kill() strands the icon in the
                // notification area until the user happens to hover over it.
                bool exited = TrySignal(ExitEventName) && running.WaitForExit(HandoffTimeoutMs);

                if (!exited)
                {
                    // Older builds have no exit listener, and CloseMainWindow is
                    // useless here — the window is hidden and its Closing handler
                    // cancels the close to go back to the tray.
                    running.Kill();
                    if (!running.WaitForExit(HandoffTimeoutMs)) return false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TryReplace error: {ex.Message}");
                return false;
            }

            // The dead owner left the mutex abandoned. Claiming it is what
            // actually makes this process the single instance.
            if (_mutex == null) return false;
            try
            {
                if (!_mutex.WaitOne(HandoffTimeoutMs)) return false;
            }
            catch (AbandonedMutexException)
            {
                // The owner died holding it, which is exactly what we arranged.
                // The wait still succeeded and the mutex is now ours.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TryReplace mutex error: {ex.Message}");
                return false;
            }

            return true;
        }

        private static void ActivateRunningInstance(Process? running)
        {
            // Windows only lets the current foreground process donate foreground
            // rights. Without this the other instance can flash in the taskbar
            // but not come to the front.
            if (running != null)
            {
                try { AllowSetForegroundWindow(running.Id); } catch { }
            }

            // Preferred path: works even when the instance is hidden in the tray.
            if (TrySignal(ActivateEventName)) return;

            // No listener, so the running build predates this class. If its
            // window happens to be on screen we can still raise it directly;
            // MainWindowHandle is zero once it has hidden itself to the tray.
            try
            {
                var handle = running?.MainWindowHandle ?? IntPtr.Zero;
                if (handle != IntPtr.Zero)
                {
                    ShowWindow(handle, SW_RESTORE);
                    SetForegroundWindow(handle);
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ActivateRunningInstance error: {ex.Message}");
            }

            // Nothing left to raise. Say so rather than disappearing.
            System.Windows.MessageBox.Show(
                LocalizationService.Get("InstanceRunningText"),
                LocalizationService.Get("InstanceRunningTitle"),
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }

        private static bool TrySignal(string eventName)
        {
            try
            {
                using var handle = EventWaitHandle.OpenExisting(eventName);
                handle.Set();
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // Expected against a build that has no listener.
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TrySignal({eventName}) error: {ex.Message}");
                return false;
            }
        }
    }
}
