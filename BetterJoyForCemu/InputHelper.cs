using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BetterJoyForCemu {
    // Runs as a separate BetterJoyForCemu.exe -inputhelper <pipeName> process, launched by
    // BetterJoyService (via SessionLauncher) into whichever session is currently active. Its only
    // job is the desktop-bound half of keyboard/mouse remap that Session 0 can't do itself:
    // capture global key/mouse events and forward them to the service, and execute Simulate
    // commands the service sends back. No config/decision logic lives here at all - see
    // HeadlessJoyconHost (the other end of the pipe) and Program.OnKeyDown/OnKeyUp/
    // OnMouseButtonDown/OnMouseButtonUp for that.
    internal static class InputHelper {
        public static void Run(string pipeName) {
            var desktopInput = new DesktopInputBackend();
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try {
                pipe.Connect(5000);
            } catch {
                desktopInput.Dispose();
                return; // service isn't listening (torn down/superseded before we connected)
            }

            var reader = new BinaryReader(pipe);
            var writer = new BinaryWriter(pipe);
            var writeLock = new object();

            // One concurrent Bluetooth audio capture at a time, matching
            // DualShock4Controller's own single-stream scope. padId is remembered here (not
            // carried by BluetoothAudioCapture itself) purely to tag outgoing AudioFrame
            // messages with the right pad.
            int audioCapturePadId = -1;
            var audioCapture = new BluetoothAudioCapture(frame => {
                if (audioCapturePadId >= 0)
                    SendAudioFrame(pipe, writer, writeLock, audioCapturePadId, frame);
            });

            // Unlike Bluetooth audio, USB loopback never sends data over the pipe at all (capture
            // and render both happen locally here), and has none of hidapi's single-handle
            // thread-safety concerns forcing one-at-a-time scope - one independent instance per
            // pad is fine.
            var usbAudioLoopbacks = new Dictionary<int, UsbAudioLoopback>();

            var keyboard = WindowsInput.Capture.Global.KeyboardAsync();
            keyboard.KeyEvent += (sender, e) => {
                if (e.Data.KeyDown != null) SendSafe(pipe, writer, writeLock, InputMessageType.KeyDown, (int)e.Data.KeyDown.Key);
                if (e.Data.KeyUp != null) SendSafe(pipe, writer, writeLock, InputMessageType.KeyUp, (int)e.Data.KeyUp.Key);
            };

            var mouse = WindowsInput.Capture.Global.MouseAsync();
            mouse.MouseEvent += (sender, e) => {
                if (e.Data.ButtonDown != null) SendSafe(pipe, writer, writeLock, InputMessageType.MouseButtonDown, (int)e.Data.ButtonDown.Button);
                if (e.Data.ButtonUp != null) SendSafe(pipe, writer, writeLock, InputMessageType.MouseButtonUp, (int)e.Data.ButtonUp.Button);
            };

            // Global hooks need a message pump on this thread to deliver events at all - there's
            // no visible window (nothing is ever Show()n), just the pump itself. Exits as soon as
            // the pipe drops, which happens whenever the service tears this session's connection
            // down (session change, service stop, etc.) - see HeadlessJoyconHost.StartNewHelperSession.
            var context = new ApplicationContext();

            // Must be created on this thread, before Application.Run starts pumping - it's what
            // lets the read-loop task below (a different thread) safely call back onto this one.
            // context.ExitThread() called directly from that background thread doesn't reliably
            // stop the message loop: the PostQuitMessage it triggers targets whichever thread
            // calls it, not necessarily the one actually running Application.Run, so the process
            // (and its global keyboard/mouse hooks) could stay alive indefinitely after the pipe
            // dropped - accumulating an orphaned helper on every session change.
            var syncContext = new WindowsFormsSynchronizationContext();

            Task.Run(() => {
                try {
                    while (pipe.IsConnected) {
                        InputMessage msg = InputMessage.ReadFrom(reader);
                        switch (msg.Type) {
                            case InputMessageType.StartAudioCapture:
                                // Do not retag a final callback from the previous controller/
                                // codec as belonging to the new stream while Start synchronously
                                // stops and replaces the capture pipeline.
                                audioCapturePadId = -1;
                                audioCapture.Start(reader.ReadString(), (BluetoothAudioCodec)msg.B);
                                audioCapturePadId = msg.A;
                                break;
                            case InputMessageType.StopAudioCapture:
                                audioCapture.Stop();
                                audioCapturePadId = -1;
                                break;
                            case InputMessageType.StartUsbAudioLoopback: {
                                string sourceEndpointId = reader.ReadString();
                                string targetEndpointId = reader.ReadString();
                                string targetNameHint = reader.ReadString();
                                if (!usbAudioLoopbacks.TryGetValue(msg.A, out UsbAudioLoopback loopback)) {
                                    loopback = new UsbAudioLoopback();
                                    usbAudioLoopbacks[msg.A] = loopback;
                                }
                                loopback.Start(sourceEndpointId, targetEndpointId, targetNameHint, msg.B);
                                break;
                            }
                            case InputMessageType.StopUsbAudioLoopback:
                                if (usbAudioLoopbacks.TryGetValue(msg.A, out UsbAudioLoopback toStop)) {
                                    toStop.Dispose();
                                    usbAudioLoopbacks.Remove(msg.A);
                                }
                                break;
                            default:
                                DesktopInputBackend.Execute(msg, desktopInput);
                                break;
                        }
                    }
                } catch {
                    // pipe closed/service gone - fall through and stop the message pump below
                } finally {
                    syncContext.Post(_ => context.ExitThread(), null);
                }
            });

            Application.Run(context);

            keyboard.Dispose();
            mouse.Dispose();
            audioCapture.Dispose();
            foreach (UsbAudioLoopback loopback in usbAudioLoopbacks.Values)
                loopback.Dispose();
            desktopInput.Dispose();
            try { pipe.Dispose(); } catch { }
        }

        private static void SendSafe(NamedPipeClientStream pipe, BinaryWriter writer, object writeLock, InputMessageType type, int code) {
            lock (writeLock) {
                if (!pipe.IsConnected)
                    return;

                try {
                    new InputMessage { Type = type, A = code }.WriteTo(writer);
                    writer.Flush();
                } catch {
                    // best-effort - a mid-write disconnect just drops this one event
                }
            }
        }

        // BluetoothAudioCapture invokes its onFrame callback from a WASAPI capture callback
        // thread, concurrently with the keyboard/mouse hook threads already using SendSafe above -
        // same writeLock, so writes to the shared pipe/writer never interleave.
        private static void SendAudioFrame(NamedPipeClientStream pipe, BinaryWriter writer, object writeLock, int padId, byte[] frame) {
            lock (writeLock) {
                if (!pipe.IsConnected)
                    return;

                try {
                    new InputMessage { Type = InputMessageType.AudioFrame, A = padId, B = frame.Length }.WriteTo(writer);
                    writer.Write(frame);
                    writer.Flush();
                } catch {
                    // best-effort - a mid-write disconnect just drops this one frame
                }
            }
        }
    }
}
