using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace BetterJoyForCemu {
    // Tells a locally running OpenRGB instance to rescan for devices, so it picks BetterJoy's
    // exposed HID controller back up without the user manually clicking Rescan in OpenRGB's own
    // UI. Needed because OpenRGB's device list only reflects HID visibility as of its own last
    // scan, and BetterJoy's HidHide state can change afterward (a fresh connection, Passthrough
    // being toggled) - see Program.cs's two call sites (on Attach, and on a real HidHide
    // hidden-to-visible transition), both gated on Lighting Mode: OpenRGB.
    //
    // Ported from the user's own working openrgb-rescan-safe.ps1, which talks to OpenRGB's SDK
    // server directly over its documented TCP protocol (127.0.0.1:6742) - no OpenRGB plugin or
    // extra dependency needed, just packet 50 (SET_CLIENT_NAME) followed by packet 140
    // (REQUEST_RESCAN).
    internal static class OpenRgbRescan {
        private const string Host = "127.0.0.1";
        private const int Port = 6742;
        private const uint PacketIdSetClientName = 50;
        private const uint PacketIdRequestRescan = 140;
        private const string ClientName = "BetterJoy2";

        private static int inFlight;

        // Fire-and-forget, single-flight like the original script's named mutex - a rescan
        // already in progress makes a second request redundant rather than additive, so this
        // drops it instead of queuing (matches the script's WaitOne(0) exiting immediately when
        // the mutex is already held).
        public static void RequestRescan() {
            if (Interlocked.CompareExchange(ref inFlight, 1, 0) != 0)
                return;

            new Thread(RescanWorker) { IsBackground = true, Name = "OpenRgbRescan" }.Start();
        }

        private static void RescanWorker() {
            try {
                // Settle delay before connecting - mirrors the original script, giving Windows
                // and OpenRGB time to finish HID re-enumeration after whatever triggered this
                // before asking OpenRGB to look for the device.
                Thread.Sleep(5000);

                using (var client = new TcpClient()) {
                    client.SendTimeout = 5000;
                    client.ReceiveTimeout = 5000;
                    client.Connect(Host, Port);

                    using (NetworkStream stream = client.GetStream()) {
                        byte[] nameBytes = Encoding.ASCII.GetBytes(ClientName + "\0");
                        SendPacket(stream, PacketIdSetClientName, nameBytes);
                        Thread.Sleep(500);
                        SendPacket(stream, PacketIdRequestRescan, Array.Empty<byte>());
                        // Give the rescan a moment to actually run before the socket closes.
                        Thread.Sleep(2000);
                    }
                }
            } catch (Exception ex) {
                // OpenRGB not running/reachable is an expected common case (not everyone with
                // Lighting Mode: OpenRGB has it open right now) - log only, nothing to surface to
                // the user beyond the log.
                DebugLog.Write("OpenRgbRescan: rescan failed - " + ex.Message);
            } finally {
                Volatile.Write(ref inFlight, 0);
            }
        }

        // OpenRGB SDK wire format: 4-byte "ORGB" magic, 4-byte device index (0 - both packets
        // used here are server-wide, not addressed to a specific device), 4-byte packet ID,
        // 4-byte payload length, then the payload itself.
        private static void SendPacket(NetworkStream stream, uint packetId, byte[] payload) {
            byte[] header = new byte[16];
            header[0] = (byte)'O';
            header[1] = (byte)'R';
            header[2] = (byte)'G';
            header[3] = (byte)'B';
            Array.Copy(BitConverter.GetBytes(packetId), 0, header, 8, 4);
            Array.Copy(BitConverter.GetBytes((uint)payload.Length), 0, header, 12, 4);

            stream.Write(header, 0, header.Length);
            if (payload.Length > 0)
                stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }
    }
}
