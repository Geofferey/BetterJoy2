using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BetterJoyForCemu.Collections;

namespace BetterJoyForCemu {
    // Loopback-only OpenRGB SDK protocol SERVER - the other half of OpenRgbRescan.cs's client
    // role. Exposes exactly one fixed "BetterJoy2" device, always present for as long as this
    // server is running, so a server OpenRGB is configured to connect to (its own Settings > SDK
    // Client tab) has it from OpenRGB's own startup instead of it appearing/disappearing
    // mid-session - confirmed against OpenRGB's own ResourceManager.cpp that a saved SDK Client
    // entry auto-reconnects on every OpenRGB launch, before any downstream consumer (Artemis 2,
    // etc.) ever queries OpenRGB's device list. That's what actually fixes third-party OpenRGB
    // clients that mishandle a device appearing out of nowhere mid-session.
    //
    // Deliberately ONE fixed device, not one per connected controller: an early version exposed
    // controllers individually, which meant the device count itself changed on every
    // connect/disconnect - the exact same "device appears mid-session" problem this class exists
    // to avoid, just moved one layer down. A color write applies to whichever controller(s) are
    // currently connected with Lighting Mode: OpenRGB (zero, one, or more); the color itself is
    // cached and reapplied to a controller as soon as it becomes eligible, so connecting later
    // doesn't lose whatever OpenRGB last set.
    //
    // Deliberately global, not per-profile: gated behind the "OpenRGB SDK server" Global mode
    // dropdown (Reassign.cs, see Modes below), independent of - and coexisting with -
    // OpenRgbRescan.cs's existing client-side rescan-nudge, which stays exactly as-is for anyone
    // who'd rather just expose the raw HID device to OpenRGB directly instead.
    //
    // Hard requirement: listens on 127.0.0.1 ONLY, never any other interface - every socket
    // operation here goes through that one guarantee (see Start() below).
    //
    // Wire format confirmed directly against OpenRGB's own source (CalcProgrammer1/OpenRGB:
    // NetworkProtocol.h, NetworkServer.cpp, RGBController.cpp, RGBControllerInterface.h), not
    // guessed - see each serialization method's own comment for the function it mirrors.
    // Targets protocol version 1: the original compact descriptor plus the standardized vendor
    // field. Fields introduced in later versions (brightness, segments, flags, display names,
    // device-specific configuration) remain absent. The negotiated version is always answered
    // explicitly; leaving the request unanswered keeps current OpenRGB clients in an incomplete
    // handshake rather than reliably falling back.
    internal static class OpenRgbServer {
        private const int Port = 6743; // Deliberately not 6742 (OpenRGB's own default) - this is
                                        // a second, independent server the user points OpenRGB at.
        private const uint DeclaredProtocolVersion = 1;

        // Global Options' "OpenRGB SDK server" dropdown (Reassign.cs) - single source of truth
        // for both the stored OpenRgbServerMode value and the dropdown's own Items, same pattern
        // as ControllerMappings.LightingModes. EnabledWithCache additionally persists the last
        // color to OpenRgbServerCachedColor (see SyncEnabledState) - real hardware remembers its
        // last color on its own even across a restart; this virtual device otherwise can't, since
        // cachedColor below is normally just in-process memory.
        public const string ModeDisabled = "Disabled";
        public const string ModeEnabled = "Enabled";
        public const string ModeEnabledWithCache = "EnabledWithCache";
        public static readonly (string Value, string Label)[] Modes = {
            (ModeDisabled, "Disabled"), (ModeEnabled, "Enabled"),
            (ModeEnabledWithCache, "Enabled with cache"),
        };

        // Confirmed against RGBControllerInterface.h.
        private const int DeviceTypeGamepad = 10;
        private const uint ZoneTypeSingle = 0;
        private const uint ModeFlagHasPerLedColor = 1u << 5;
        private const uint ModeColorsPerLed = 1;
        private const uint ModeFlagHasSpeed = 1u << 0;
        private const uint ModeFlagHasModeSpecificColor = 1u << 6;
        private const uint ModeColorsModeSpecific = 2;

        private const uint PacketIdRequestControllerCount = 0;
        private const uint PacketIdRequestControllerData = 1;
        private const uint PacketIdRequestProtocolVersion = 40;
        private const uint PacketIdRgbControllerUpdateLeds = 1050;
        private const uint PacketIdRgbControllerUpdateZoneLeds = 1051;
        private const uint PacketIdRgbControllerUpdateSingleLed = 1052;
        private const uint PacketIdRgbControllerUpdateMode = 1101;
        private const uint PacketIdRgbControllerSaveMode = 1102;

        // The two modes BetterJoy2 advertises - Direct (index 0, unchanged) and Rainbow Puke
        // (index 1, a discrete step-and-hold hue cycle: rainbowColorCount evenly-spaced
        // full-saturation hues, holding at each for 1/rainbowColorCount of a full cycle before
        // jumping to the next - not a smooth sweep, so "colors" visibly changes how chunky the
        // cycle looks, not just cosmetic default swatches).
        private const int ModeIndexDirect = 0;
        private const int ModeIndexRainbow = 1;
        private const uint RainbowSpeedMin = 1;
        private const uint RainbowSpeedMax = 100;
        private const uint RainbowColorsMin = 2;
        private const uint RainbowColorsMax = 8;

        private sealed class ConnectedClient {
            public TcpClient Client;
            public NetworkStream Stream;
            public readonly object WriteLock = new object();
        }

        private static readonly object lifecycleLock = new object();
        private static TcpListener listener;
        private static readonly List<ConnectedClient> clients = new List<ConnectedClient>();
        private static readonly object clientsLock = new object();

        // The fixed device's app-level color, packed 0x00BBGGRR. -1 means OpenRGB has not sent a
        // color yet, so merely enabling the server never paints a controller black. Once set, it
        // is reapplied whenever a controller becomes eligible.
        private static int cachedColor = -1;

        // Mirrors OpenRgbServerMode == ModeEnabledWithCache - whether SetCachedColor should also
        // persist to OpenRgbServerCachedColor. A plain field, not read under lifecycleLock:
        // SetCachedColor runs on a client socket thread and only needs the latest value, not
        // exclusion with Start/Stop.
        private static volatile bool cachingEnabled;

        // Which advertised mode is active, and Rainbow Puke's own parameters - session-only,
        // never persisted (unlike cachedColor above): an animation mid-cycle has no single
        // "current color" worth remembering across a restart the way a static Direct color does.
        // All three read/written via Volatile.Read/Write, matching cachedColor's own convention.
        private static int activeMode = ModeIndexDirect;
        private static uint rainbowSpeed = 50;
        private static int rainbowColorCount = 6;

        private static Thread animationThread;
        private static volatile bool animationRunning;
        private static readonly Stopwatch animationClock = Stopwatch.StartNew();

        // Called at startup and after every shared-config reload (HeadlessJoyconHost's config
        // watcher) - starts or stops the listener, and turns caching on/off, to match the
        // current "OpenRGB SDK server" Global option.
        public static void SyncEnabledState() {
            string mode = ApplicationSettings.StringValue("OpenRgbServerMode", ModeDisabled);
            bool shouldRun = mode != ModeDisabled;
            bool shouldCache = mode == ModeEnabledWithCache;

            lock (lifecycleLock) {
                if (shouldCache && !cachingEnabled)
                    LoadOrPersistCachedColor();
                cachingEnabled = shouldCache;

                if (shouldRun && listener == null)
                    Start();
                else if (!shouldRun && listener != null)
                    Stop();
            }
        }

        // Called once, right as caching turns on. If this session already has a color (OpenRGB
        // set one before the mode was switched to "Enabled with cache"), persist it immediately
        // rather than waiting for the next color write. Otherwise there's nothing from this
        // session yet, so restore whatever a previous session last persisted.
        private static void LoadOrPersistCachedColor() {
            int color = Volatile.Read(ref cachedColor);
            if (color >= 0) {
                PersistCachedColor(color);
                return;
            }

            int persisted;
            if (Int32.TryParse(ApplicationSettings.StringValue("OpenRgbServerCachedColor", ""),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out persisted) &&
                    persisted >= 0) {
                Volatile.Write(ref cachedColor, persisted);
                ApplyCachedColorToEligibleControllers();
            }
        }

        private static void PersistCachedColor(int color) {
            try {
                ApplicationSettings.SetValue("OpenRgbServerCachedColor",
                    color.ToString(CultureInfo.InvariantCulture));
            } catch (ConfigurationErrorsException ex) {
                DebugLog.Write("OpenRgbServer: failed to persist cached color - " + ex.Message);
            }
        }

        private static void Start() {
            try {
                // IPAddress.Loopback, never IPAddress.Any - the one hard requirement this class
                // exists to satisfy. Deliberately not configurable.
                listener = new TcpListener(IPAddress.Loopback, Port);
                listener.Start();
                new Thread(AcceptLoop) { IsBackground = true, Name = "OpenRgbServerAccept" }.Start();

                animationRunning = true;
                animationThread = new Thread(AnimationLoop) {
                    IsBackground = true,
                    Name = "OpenRgbServerAnimation",
                };
                animationThread.Start();

                DebugLog.Write("OpenRgbServer: listening on 127.0.0.1:" + Port);
            } catch (Exception ex) {
                listener = null;
                DebugLog.Write("OpenRgbServer: failed to start - " + ex.Message);
            }
        }

        private static void Stop() {
            try {
                listener?.Stop();
            } catch { }
            listener = null;
            animationRunning = false;

            lock (clientsLock) {
                foreach (ConnectedClient c in clients) {
                    try { c.Client.Close(); } catch { }
                }
                clients.Clear();
            }
            DebugLog.Write("OpenRgbServer: stopped");
        }

        // Called once per JoyconManager.ApplyControllerProfileOptions reconciliation pass
        // (Program.cs, ~every 2s, and after every config reload) - reapplies the cached color to
        // every controller currently eligible (connected, Lighting Mode: OpenRGB) rather than
        // only the ones that happened to be eligible the moment OpenRGB last sent a color. Cheap:
        // Controller.SetLightColor already dedupes an unchanged color internally.
        public static void ApplyCachedColorToEligibleControllers() {
            if (Volatile.Read(ref activeMode) != ModeIndexDirect)
                return; // Rainbow Puke's own animation loop owns output while it's active.

            int color = Volatile.Read(ref cachedColor);
            if (color < 0)
                return;

            byte cachedRed = (byte)(color & 0xFF);
            byte cachedGreen = (byte)((color >> 8) & 0xFF);
            byte cachedBlue = (byte)((color >> 16) & 0xFF);
            List<Controller> eligible = GetEligibleControllers();
            DebugLog.Write("OpenRgbServer: applying " + cachedRed + "," + cachedGreen + "," +
                cachedBlue + " to " + eligible.Count + " eligible controller(s)");
            foreach (Controller c in eligible)
                c.SetOpenRgbLightColor(cachedRed, cachedGreen, cachedBlue);
        }

        private static void AcceptLoop() {
            TcpListener currentListener = listener;
            try {
                while (true) {
                    TcpClient tcpClient = currentListener.AcceptTcpClient();
                    var connected = new ConnectedClient {
                        Client = tcpClient,
                        Stream = tcpClient.GetStream(),
                    };
                    lock (clientsLock) clients.Add(connected);
                    new Thread(() => ClientLoop(connected)) {
                        IsBackground = true,
                        Name = "OpenRgbServerClient",
                    }.Start();
                }
            } catch {
                // listener.Stop() breaks AcceptTcpClient() out with an exception - expected
                // shutdown path, nothing to log.
            }
        }

        // Drives Rainbow Puke while it's the active mode; a cheap idle sleep loop otherwise.
        // Re-derives the eligible controller list only twice a second rather than every step -
        // GetEligibleControllers logs one line per controller, and at the fastest speed/highest
        // color count this loop can step every 25ms, so calling it per-step would flood the log
        // and redo the profile/lighting-mode lookup far more often than useful.
        private static void AnimationLoop() {
            int lastStepIndex = -1;
            List<Controller> eligible = new List<Controller>();
            long nextEligibilityRefresh = 0;

            while (animationRunning) {
                if (Volatile.Read(ref activeMode) != ModeIndexRainbow) {
                    lastStepIndex = -1;
                    Thread.Sleep(200);
                    continue;
                }

                long now = animationClock.ElapsedMilliseconds;
                if (now >= nextEligibilityRefresh) {
                    eligible = GetEligibleControllers();
                    nextEligibilityRefresh = now + 500;
                }

                int colorCount = Volatile.Read(ref rainbowColorCount);
                long stepDurationMs =
                    Math.Max(20, CycleDurationMs(Volatile.Read(ref rainbowSpeed)) / colorCount);
                int stepIndex = (int)((now / stepDurationMs) % colorCount);
                if (stepIndex != lastStepIndex) {
                    lastStepIndex = stepIndex;
                    (byte r, byte g, byte b) = HsvToRgb(360f * stepIndex / colorCount);
                    foreach (Controller c in eligible)
                        c.SetOpenRgbLightColor(r, g, b);
                }

                Thread.Sleep(20);
            }
        }

        // speed 1 (slowest) -> 10s per full cycle, speed 100 (fastest) -> 200ms per full cycle.
        // No real device convention to match here - Rainbow Puke is BetterJoy2's own mode, not
        // mirroring anything from OpenRGB's own source.
        private static long CycleDurationMs(uint speed) {
            uint clamped = Math.Min(RainbowSpeedMax, Math.Max(RainbowSpeedMin, speed));
            double t = (clamped - RainbowSpeedMin) / (double)(RainbowSpeedMax - RainbowSpeedMin);
            return (long)(10000 - t * 9800);
        }

        // Standard full-saturation, full-value HSV-to-RGB conversion, hue in [0, 360).
        private static (byte R, byte G, byte B) HsvToRgb(float hue) {
            const float c = 255f;
            float x = c * (1 - Math.Abs(hue / 60f % 2 - 1));
            if (hue < 60) return ((byte)c, (byte)x, 0);
            if (hue < 120) return ((byte)x, (byte)c, 0);
            if (hue < 180) return (0, (byte)c, (byte)x);
            if (hue < 240) return (0, (byte)x, (byte)c);
            if (hue < 300) return ((byte)x, 0, (byte)c);
            return ((byte)c, 0, (byte)x);
        }

        private static void ClientLoop(ConnectedClient connected) {
            try {
                byte[] header = new byte[16];
                while (true) {
                    if (!ReadExact(connected.Stream, header, 16))
                        break;
                    if (header[0] != (byte)'O' || header[1] != (byte)'R' ||
                            header[2] != (byte)'G' || header[3] != (byte)'B')
                        break;

                    uint devId = BitConverter.ToUInt32(header, 4);
                    uint packetId = BitConverter.ToUInt32(header, 8);
                    uint payloadLength = BitConverter.ToUInt32(header, 12);

                    byte[] payload = payloadLength > 0 ? new byte[payloadLength] : Array.Empty<byte>();
                    if (payloadLength > 0 && !ReadExact(connected.Stream, payload, (int)payloadLength))
                        break;

                    HandlePacket(connected, devId, packetId, payload);
                }
            } catch (Exception ex) {
                DebugLog.Write("OpenRgbServer: client connection ended - " + ex.Message);
            } finally {
                lock (clientsLock) clients.Remove(connected);
                try { connected.Client.Close(); } catch { }
            }
        }

        private static void HandlePacket(ConnectedClient connected, uint devId, uint packetId,
                                         byte[] payload) {
            DebugLog.Write("OpenRgbServer: packet " + packetId + " devId=" + devId +
                " payloadLen=" + payload.Length);
            switch (packetId) {
                // Confirmed against NetworkServer.cpp: on receiving this packet the real server
                // both reads the client's declared version AND replies with its own - a client
                // that gets no reply here does not reliably fall back in practice
                // (real-hardware testing: OpenRGB reconnected in a tight loop instead). This
                // reply is the fix - a plain 4-byte DeclaredProtocolVersion, no payload parsing
                // needed since this class's own behavior never varies by what the client sent.
                case PacketIdRequestProtocolVersion:
                    lock (connected.WriteLock) {
                        SendPacket(connected.Stream, 0, PacketIdRequestProtocolVersion,
                            BitConverter.GetBytes(DeclaredProtocolVersion));
                    }
                    break;
                case PacketIdRequestControllerCount: {
                    byte[] reply = BitConverter.GetBytes((uint)1);
                    lock (connected.WriteLock)
                        SendPacket(connected.Stream, 0, PacketIdRequestControllerCount, reply);
                    break;
                }
                case PacketIdRequestControllerData: {
                    if (devId != 0)
                        break; // only one device, ID 0 - anything else is stale/invalid
                    byte[] descriptor = BuildDeviceDescriptor();
                    byte[] reply = new byte[4 + descriptor.Length];
                    // Self-referential leading size field (own 4 bytes + descriptor) - confirmed
                    // against NetworkServer.cpp's SendReply_ControllerData, not just the
                    // descriptor-only size GetDeviceDescriptionSize itself returns.
                    Array.Copy(BitConverter.GetBytes((uint)reply.Length), 0, reply, 0, 4);
                    Array.Copy(descriptor, 0, reply, 4, descriptor.Length);
                    lock (connected.WriteLock)
                        SendPacket(connected.Stream, devId, PacketIdRequestControllerData, reply);
                    break;
                }
                case PacketIdRgbControllerUpdateLeds:
                    if (devId == 0)
                        ApplyUpdateLeds(payload);
                    break;
                case PacketIdRgbControllerUpdateZoneLeds:
                    if (devId == 0)
                        ApplyUpdateZoneLeds(payload);
                    break;
                case PacketIdRgbControllerUpdateSingleLed:
                    if (devId == 0)
                        ApplyUpdateSingleLed(payload);
                    break;
                // SAVEMODE and UPDATEMODE carry an identical payload (NetworkServer.cpp routes
                // both through the same handler, one flag apart) - this class has no real
                // persistent "saved mode slot" concept, so both just switch/apply the same way.
                case PacketIdRgbControllerUpdateMode:
                case PacketIdRgbControllerSaveMode:
                    if (devId == 0)
                        ApplyUpdateMode(payload);
                    break;
                default:
                    // SET_CLIENT_NAME and everything else this class doesn't implement: no
                    // response needed.
                    break;
            }
        }

        // Confirmed against NetworkServer.cpp's ProcessRequest_RGBController_UpdateLEDs: payload
        // is a self-referential 4-byte size (must equal the packet's own payload length) followed
        // by num_colors(2) and that many 4-byte RGBColor values. Applies the first color to every
        // currently eligible controller - this class only ever advertises one LED on the one
        // fixed device (see BuildDeviceDescriptor), so there is only ever one meaningful color.
        private static void ApplyUpdateLeds(byte[] payload) {
            if (payload.Length < 6 + 4)
                return;

            ushort numColors = BitConverter.ToUInt16(payload, 4);
            if (numColors == 0)
                return;

            uint rgbColor = BitConverter.ToUInt32(payload, 6);
            SetCachedColor(rgbColor);
        }

        // Confirmed against NetworkServer.cpp's ProcessRequest_RGBController_UpdateZoneLEDs:
        // payload is a self-referential 4-byte size, then zone_idx(4), num_colors(2), then that
        // many 4-byte RGBColor values. This is what OpenRGB sends when the user sets a color from
        // the per-zone color picker (as opposed to the whole-device one) - this class only ever
        // advertises zone 0, so zone_idx is ignored rather than validated.
        private static void ApplyUpdateZoneLeds(byte[] payload) {
            if (payload.Length < 4 + 4 + 2 + 4)
                return;

            ushort numColors = BitConverter.ToUInt16(payload, 8);
            if (numColors == 0)
                return;

            uint rgbColor = BitConverter.ToUInt32(payload, 10);
            SetCachedColor(rgbColor);
        }

        // Confirmed against NetworkServer.cpp's ProcessRequest_RGBController_UpdateSingleLED:
        // unlike the other two UPDATE* packets, this one has NO leading self-referential size
        // field - payload is exactly led_idx(4) then RGBColor(4). This is what OpenRGB's "Toggle
        // LED view" sends when the user picks a color for one specific LED - this class only ever
        // advertises LED 0, so led_idx is ignored rather than validated.
        private static void ApplyUpdateSingleLed(byte[] payload) {
            if (payload.Length < 4 + 4)
                return;

            uint rgbColor = BitConverter.ToUInt32(payload, 4);
            SetCachedColor(rgbColor);
        }

        // Confirmed against NetworkServer.cpp's ProcessRequest_RGBController_UpdateSaveMode and
        // RGBController.cpp's SetModeDescription (protocol version 1): self-referential 4-byte
        // size, mode_idx(4), then the full mode description this class's own GetModeDescriptionData
        // equivalent (WriteDirectMode/WriteRainbowMode) writes - name, value, flags, speed_min,
        // speed_max, colors_min, colors_max, speed, direction, color_mode, num_colors, colors[].
        // Only mode_idx/speed/num_colors matter here - name/flags/min/max/direction/color_mode
        // are this class's own fixed shape per mode, not something OpenRGB can redefine, and the
        // actual color VALUES are skipped too: Rainbow Puke derives its own palette purely from
        // colorCount (see HsvToRgb in AnimationLoop), not from whatever OpenRGB's mode editor
        // happened to show as swatches.
        private static void ApplyUpdateMode(byte[] payload) {
            try {
                using (var stream = new MemoryStream(payload))
                using (var r = new BinaryReader(stream)) {
                    r.ReadUInt32();                  // self-referential size - unused
                    int modeIndex = r.ReadInt32();
                    if (modeIndex != ModeIndexDirect && modeIndex != ModeIndexRainbow)
                        return;

                    ushort nameLen = r.ReadUInt16();
                    r.ReadBytes(nameLen);             // name - unused, this class names its own modes
                    r.ReadInt32();                    // mode value - unused, present for < v6
                    r.ReadUInt32();                   // flags - unused, fixed per mode
                    r.ReadUInt32();                   // speed_min - unused, fixed per mode
                    r.ReadUInt32();                   // speed_max - unused, fixed per mode
                    r.ReadUInt32();                   // colors_min - unused, fixed per mode
                    r.ReadUInt32();                   // colors_max - unused, fixed per mode
                    uint speed = r.ReadUInt32();
                    r.ReadUInt32();                   // direction - unused
                    r.ReadUInt32();                   // color_mode - unused, fixed per mode
                    ushort numColors = r.ReadUInt16();

                    Volatile.Write(ref activeMode, modeIndex);
                    if (modeIndex == ModeIndexRainbow) {
                        Volatile.Write(ref rainbowSpeed,
                            Math.Min(RainbowSpeedMax, Math.Max(RainbowSpeedMin, speed)));
                        Volatile.Write(ref rainbowColorCount, (int)Math.Min(RainbowColorsMax,
                            Math.Max(RainbowColorsMin, (uint)numColors)));
                    }
                    DebugLog.Write("OpenRgbServer: mode set to " +
                        (modeIndex == ModeIndexRainbow
                            ? "Rainbow Puke speed=" + rainbowSpeed + " colors=" + rainbowColorCount
                            : "Direct"));
                }
            } catch (EndOfStreamException) {
                // Malformed/truncated payload - ignore rather than crash the client thread.
            }
        }

        private static void SetCachedColor(uint rgbColor) {
            int color = (int)(rgbColor & 0x00FFFFFF);
            Volatile.Write(ref cachedColor, color);
            if (cachingEnabled)
                PersistCachedColor(color);
            byte cachedRed = (byte)(color & 0xFF);
            byte cachedGreen = (byte)((color >> 8) & 0xFF);
            byte cachedBlue = (byte)((color >> 16) & 0xFF);
            DebugLog.Write("OpenRgbServer: cached color set to " +
                cachedRed + "," + cachedGreen + "," + cachedBlue);

            ApplyCachedColorToEligibleControllers();
        }

        // Every controller currently connected with Lighting Mode: OpenRGB - zero, one, or more.
        // Order doesn't matter here (unlike an earlier version of this class): there is only ever
        // one advertised device, so nothing depends on a stable per-controller index anymore.
        private static List<Controller> GetEligibleControllers() {
            var result = new List<Controller>();
            ConcurrentList<Controller> all = Program.mgr?.j;
            if (all == null)
                return result;

            foreach (Controller c in all) {
                string profileId = ControllerMappings.ProfileIdFor(c);
                string lightingMode = ControllerMappings.LightingMode(profileId);
                bool eligible = c.state > Controller.state_.DROPPED &&
                    lightingMode == ControllerMappings.LightingModeOpenRgb;
                DebugLog.Write("OpenRgbServer: controller state=" + c.state +
                    " profile=" + profileId + " lightingMode=" + lightingMode +
                    " eligible=" + eligible);
                if (eligible)
                    result.Add(c);
            }
            return result;
        }

        // Mirrors RGBController::GetDeviceDescriptionData for protocol version 1 exactly - see
        // the class comment for why v0 specifically, and why this is one fixed app-level device
        // rather than one per controller. One zone (ZONE_TYPE_SINGLE, no matrix map), one LED,
        // one "Direct" mode with per-LED color support - the simplest shape that lets OpenRGB
        // (and anything downstream of it) set a color BetterJoy2 then routes to whichever
        // controller(s) are currently eligible.
        private static byte[] BuildDeviceDescriptor() {
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream)) {
                w.Write(DeviceTypeGamepad);
                WriteString(w, "BetterJoy2");
                WriteString(w, "BetterJoy2"); // vendor - introduced in protocol version 1
                WriteString(w, "Applies to whichever connected controller has Lighting Mode: " +
                    "OpenRGB");
                WriteString(w, "1");
                WriteString(w, "betterjoy2-openrgb-server");
                WriteString(w, "BetterJoy2");

                w.Write((ushort)2); // num_modes
                w.Write(Volatile.Read(ref activeMode));
                WriteDirectMode(w);
                WriteRainbowMode(w);

                w.Write((ushort)1); // num_zones
                WriteSingleZone(w);

                w.Write((ushort)1); // num_leds
                WriteString(w, "Lightbar");
                w.Write(0u); // led.value - unused, present because protocol_version < 6

                w.Write((ushort)1); // num_colors (top-level)
                int color = Volatile.Read(ref cachedColor);
                w.Write(color < 0 ? 0u : (uint)color);

                w.Flush();
                return stream.ToArray();
            }
        }

        private static void WriteDirectMode(BinaryWriter w) {
            WriteString(w, "Direct");
            w.Write(0);                          // mode_value - unused, present for < v6
            w.Write(ModeFlagHasPerLedColor);      // mode_flags
            w.Write(0u);                          // speed_min
            w.Write(0u);                          // speed_max
            w.Write(0u);                          // colors_min
            w.Write(0u);                          // colors_max
            w.Write(0u);                          // speed
            w.Write(0u);                          // direction (MODE_DIRECTION_LEFT)
            w.Write(ModeColorsPerLed);            // color_mode
            w.Write((ushort)0);                   // mode_num_colors - colors live on the
                                                   // controller itself for MODE_COLORS_PER_LED
        }

        // Colors written here are just OpenRGB mode-editor preview swatches (evenly-spaced hues
        // matching what the cycle actually shows) - AnimationLoop derives its own palette from
        // rainbowColorCount alone at animation time, never reads these back.
        private static void WriteRainbowMode(BinaryWriter w) {
            WriteString(w, "Rainbow Puke");
            w.Write(0);                                        // mode_value - unused, present for < v6
            w.Write(ModeFlagHasSpeed | ModeFlagHasModeSpecificColor); // mode_flags
            w.Write(RainbowSpeedMin);
            w.Write(RainbowSpeedMax);
            w.Write(RainbowColorsMin);
            w.Write(RainbowColorsMax);
            w.Write(Volatile.Read(ref rainbowSpeed));
            w.Write(0u);                                       // direction (MODE_DIRECTION_LEFT)
            w.Write(ModeColorsModeSpecific);                    // color_mode
            int colorCount = Volatile.Read(ref rainbowColorCount);
            w.Write((ushort)colorCount);                        // mode_num_colors
            for (int i = 0; i < colorCount; i++) {
                (byte r, byte g, byte b) = HsvToRgb(360f * i / colorCount);
                w.Write(ToRgbColor(r, g, b));
            }
        }

        private static void WriteSingleZone(BinaryWriter w) {
            WriteString(w, "Lightbar");
            w.Write(ZoneTypeSingle);
            w.Write(1u); // leds_min
            w.Write(1u); // leds_max
            w.Write(1u); // leds_count
            w.Write((ushort)0); // matrix_map_size - always present, 0 for a non-matrix zone
                                 // (confirmed against GetMatrixMapDescriptionData: writes nothing
                                 // when matrix_map.Empty())
        }

        // 2-byte length prefix INCLUDING the null terminator, then the string bytes, then the
        // null terminator itself - confirmed against every *_len/strcpy pair in
        // RGBController.cpp's Get*DescriptionData methods. Only strings embedded in a structured
        // payload like this one use this convention - a flat single-string packet (SET_CLIENT_NAME)
        // does not, see OpenRgbRescan.cs's own SendPacket.
        private static void WriteString(BinaryWriter w, string value) {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? String.Empty);
            w.Write((ushort)(bytes.Length + 1));
            w.Write(bytes);
            w.Write((byte)0);
        }

        // Confirmed against RGBControllerInterface.h: typedef unsigned int RGBColor; #define
        // ToRGBColor(r, g, b) ((RGBColor)((b << 16) | (g << 8) | (r))) - matches the packing
        // SetCachedColor/ApplyCachedColorToEligibleControllers already unpack cachedColor with.
        private static uint ToRgbColor(byte r, byte g, byte b) {
            return (uint)((b << 16) | (g << 8) | r);
        }

        private static bool ReadExact(NetworkStream stream, byte[] buffer, int count) {
            int offset = 0;
            while (offset < count) {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0)
                    return false;
                offset += read;
            }
            return true;
        }

        // Same OpenRGB SDK wire format OpenRgbRescan.cs's client side already sends: 4-byte
        // "ORGB" magic, 4-byte device ID, 4-byte packet ID, 4-byte payload length, then payload.
        private static void SendPacket(NetworkStream stream, uint devId, uint packetId,
                                       byte[] payload) {
            byte[] header = new byte[16];
            header[0] = (byte)'O';
            header[1] = (byte)'R';
            header[2] = (byte)'G';
            header[3] = (byte)'B';
            Array.Copy(BitConverter.GetBytes(devId), 0, header, 4, 4);
            Array.Copy(BitConverter.GetBytes(packetId), 0, header, 8, 4);
            Array.Copy(BitConverter.GetBytes((uint)payload.Length), 0, header, 12, 4);

            stream.Write(header, 0, header.Length);
            if (payload.Length > 0)
                stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }
    }
}
