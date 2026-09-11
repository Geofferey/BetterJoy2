using System.Diagnostics;
using System.ServiceProcess;

namespace BetterJoyForCemu {
    // Hosts the exact same core pipeline (Program.Start/Stop) as GUI mode, just wired to a
    // HeadlessJoyconHost instead of a MainForm - see EntryPoint.cs for the "-service" switch
    // that runs this instead of the normal WinForms path via ServiceBase.Run(new
    // BetterJoyService()). A session helper handles input hooks and ordinary desktop fallback
    // after login; before login, the service writes controller mouse output directly through
    // FakerInput's virtual HID device (see HeadlessJoyconHost/DesktopInputBackend).
    public class BetterJoyService : ServiceBase {
        private HeadlessJoyconHost host;
        private bool resumeHandled;

        // Serialises the two routines against each other. A suspend's stop can still be in flight
        // when Windows delivers the resume - that overlap had the resume building a second pipeline
        // while the first was mid-teardown, and the teardown then finished by stopping the scan on
        // the manager the resume had just created, leaving a pad in the list with nothing scanning.
        private readonly object pipelineLock = new object();

        public BetterJoyService() {
            // Must match Installer/BetterJoy.iss's sc.exe create name exactly, and
            // MainForm.cs's ServiceController("BetterJoy2") lookup - the SCM associates a
            // running process with its registered service entry by this name.
            ServiceName = "BetterJoy2";
            CanHandleSessionChangeEvent = true;
            CanHandlePowerEvent = true;
        }

        protected override void OnStart(string[] args) {
            StartPipeline();
        }

        protected override void OnStop() {
            StopPipeline();
        }

        // The service's actual start routine, factored out so a suspend/resume can run the very
        // same one the SCM does. Everything above this in startup (EntryPoint.Main's culture, DLL
        // search path, AppPaths and config redirect) is process-level and stays in effect, so this
        // is the whole of what a stop/start cycle rebuilds.
        private void StartPipeline() {
            lock (pipelineLock) {
                host = new HeadlessJoyconHost();
                Program.SetHost(host);
                Program.Start();

                LaunchInputHelper();
                host.StartControlServer();
                host.StartConfigWatcher();
            }
        }

        // Program.Stop() already guards the specific failures we know about (e.g. disconnecting
        // a ViGEm target that was never actually plugged in), but an unhandled exception here
        // would otherwise propagate back to the SCM as a failed/hung stop (see
        // ServiceBase.DeferredStop) instead of the service just stopping - belt and suspenders,
        // matching MainForm.ExitApplication()'s same try/catch around GUI shutdown. The host
        // teardown is a full Shutdown() rather than just StopInputRouting(): stopping used to be
        // immediately followed by the process dying, which released the control pipe and config
        // watcher for free, and a suspend/resume no longer has that luxury.
        private void StopPipeline(bool suspending = false) {
            lock (pipelineLock) {
                try {
                    Program.Stop(suspending);
                } catch { } finally {
                    if (host != null) {
                        host.Shutdown();
                        host = null;
                    }
                }
            }
        }

        // Sleep/wake. A suspend runs the service's stop routine and a resume runs its start
        // routine - the same two the SCM calls, so a wake reaches exactly the state restarting the
        // service produces, which is the behaviour that was already correct. Windows can deliver
        // more than one resume status for a single wake (ResumeAutomatic then ResumeSuspend when
        // the machine wakes to a user), so resumeHandled collapses them - starting twice would
        // leave the first pipeline orphaned. Always returns true: only QuerySuspend (which Windows
        // no longer sends to services) can be vetoed, and refusing a suspend is never what we want.
        protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus) {
            switch (powerStatus) {
                case PowerBroadcastStatus.Suspend:
                    resumeHandled = false;
                    DebugLog.Write("Power: suspend - running the service stop routine");
                    StopPipeline(suspending: true);
                    break;
                case PowerBroadcastStatus.ResumeSuspend:
                case PowerBroadcastStatus.ResumeAutomatic:
                case PowerBroadcastStatus.ResumeCritical:
                    if (!resumeHandled) {
                        resumeHandled = true;
                        DebugLog.Write("Power: resume - running the service start routine");
                        StartPipeline();
                    }
                    break;
            }

            return true;
        }

        // Fires on logon/unlock/console-connect (a session becoming the active one) - relaunch
        // the helper there. We don't bother explicitly killing the old helper on logoff/
        // disconnect: relaunching here always tears down the previous pipe first (see
        // HeadlessJoyconHost.StartNewHelperSession), and the orphaned helper notices its pipe
        // drop and exits itself (see InputHelper.Run), so there's nothing extra to do on the
        // logoff-side events.
        protected override void OnSessionChange(SessionChangeDescription changeDescription) {
            base.OnSessionChange(changeDescription);

            if (changeDescription.Reason == SessionChangeReason.SessionLogon ||
                changeDescription.Reason == SessionChangeReason.ConsoleConnect ||
                changeDescription.Reason == SessionChangeReason.SessionUnlock) {
                LaunchInputHelper();
            }
        }

        private void LaunchInputHelper() {
            if (host == null)
                return;

            string pipeName = host.StartNewHelperSession();
            string commandLine = "\"" + Process.GetCurrentProcess().MainModule.FileName + "\" -inputhelper " + pipeName;

            if (!SessionLauncher.TryLaunchInActiveSession(commandLine, out int processId)) {
                host.AppendTextBox("Could not launch the keyboard/mouse remap helper right now - no interactive session may be available yet. It will retry on the next session change.");
            }
        }
    }
}
