using System;
using System.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BetterJoyForCemu {
    // Alternative to ViiperMicrophoneEndpoint that needs nothing bundled beyond a driver Valve
    // already ships and Microsoft has already attestation-signed - renders decoded DualSense
    // microphone PCM into a render endpoint, which that driver internally loops through to its
    // own paired capture endpoint (the same virtual-cable pattern VB-CABLE/Voicemeeter use). The
    // bundled INF/CAT are Valve's own, byte-for-byte unmodified - the CAT's signature covers the
    // INF's own hash, so editing its hardware ID or strings (tried once) breaks that hash and
    // Windows refuses to load it (ERROR_FILE_HASH_NOT_IN_CATALOG), right back to the "needs
    // test-signing mode" problem this driver was chosen specifically to avoid. So this always
    // targets Steam's own hardware ID/default name (harmless no-op if Steam already created the
    // same device via its own first-run trigger - see BetterJoy.iss's install step) and instead
    // distinguishes itself with a friendly-name override applied here at runtime, re-applied on
    // every Open() so it self-heals if Steam recreates the device later (confirmed on real
    // hardware that reconnecting a controller through Steam does exactly that, wiping any prior
    // rename). This targets each endpoint's own SWD\MMDEVAPI PnP node under
    // HKLM\SYSTEM\CurrentControlSet\Enum, not the audio subsystem's PKEY_Device_FriendlyName -
    // that property is documented read-only for any client app (E_ACCESSDENIED via
    // IPropertyStore::SetValue, confirmed even elevated), and the underlying MMDevices\Audio
    // property-store registry keys are ACL-locked against admin writes too. The SWD\MMDEVAPI PnP
    // node's plain FriendlyName is a different, normally-writable location that Windows composes
    // the endpoint's displayed name from - confirmed on real hardware.
    internal sealed class SteamMicrophoneEndpoint : IMicrophoneEndpoint {
        public const string EndpointNameHint = "Steam Streaming Microphone";
        private const string DisplayName = "DualSense Microphone (Steam Streaming Microphone)";

        private const int SourceSampleRate = 48000;
        private const int SourceChannels = 2;
        private const int SourceBytesPerFrame = SourceChannels * sizeof(short);

        private readonly object writeLock = new object();
        private readonly WasapiOut output;
        private readonly BufferedWaveProvider outputBuffer;
        private readonly SampleRateResampler resampler; // null when the target is already 48 kHz
        private readonly double resampleRatio;
        private int disposed;

        private static bool TryFindDevice(DataFlow flow, out MMDevice device) {
            device = null;
            using (var enumerator = new MMDeviceEnumerator()) {
                foreach (MMDevice candidate in enumerator.EnumerateAudioEndPoints(
                        flow, DeviceState.Active)) {
                    if (candidate.FriendlyName.IndexOf(EndpointNameHint,
                            StringComparison.OrdinalIgnoreCase) >= 0) {
                        device = candidate;
                        return true;
                    }
                    candidate.Dispose();
                }
            }
            return false;
        }

        // Cosmetic only - must never take down the actual microphone feature if it fails (e.g.
        // running unelevated, the registry key not existing yet). Idempotent: safe to call on
        // every Open(), which is what makes it self-heal after Steam recreates the device.
        private static void TryRenameEndpoint(MMDevice device) {
            try {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                        @"SYSTEM\CurrentControlSet\Enum\SWD\MMDEVAPI\" + device.ID, writable: true))
                    key?.SetValue("FriendlyName", DisplayName, RegistryValueKind.String);
            } catch {
                // Best-effort - the device still works fine under whatever name Windows already
                // has for it.
            }
        }

        public static SteamMicrophoneEndpoint Open() {
            if (!TryFindDevice(DataFlow.Render, out MMDevice device))
                throw new InvalidOperationException(
                    "\"" + EndpointNameHint + "\" was not found or not installed.");

            TryRenameEndpoint(device);
            if (TryFindDevice(DataFlow.Capture, out MMDevice captureDevice)) {
                using (captureDevice)
                    TryRenameEndpoint(captureDevice);
            }

            try {
                return new SteamMicrophoneEndpoint(device);
            } catch {
                device.Dispose();
                throw;
            }
        }

        private SteamMicrophoneEndpoint(MMDevice device) {
            using (device) {
                WaveFormat targetFormat = device.AudioClient.MixFormat;
                resampleRatio = (double)targetFormat.SampleRate / SourceSampleRate;
                if (targetFormat.SampleRate != SourceSampleRate)
                    resampler = new SampleRateResampler(ResampleQuality.SincBest, SourceChannels,
                        resampleRatio);

                outputBuffer = new BufferedWaveProvider(
                    WaveFormat.CreateIeeeFloatWaveFormat(targetFormat.SampleRate, SourceChannels)) {
                    // A few call's worth of slack (~10ms arrives at a time) - large enough to
                    // absorb ordinary scheduling jitter, small enough that a stale backlog after
                    // a stall doesn't turn into a noticeable delay.
                    BufferDuration = TimeSpan.FromMilliseconds(300),
                    DiscardOnBufferOverflow = true,
                };

                output = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
                output.Init(outputBuffer);
                output.Play();
            }
        }

        public void WriteMicrophonePcm(byte[] stereoPcm) {
            if (stereoPcm == null || stereoPcm.Length % SourceBytesPerFrame != 0)
                throw new ArgumentException("Malformed stereo S16LE PCM.", nameof(stereoPcm));
            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(SteamMicrophoneEndpoint));

            int frames = stereoPcm.Length / SourceBytesPerFrame;
            var floatSamples = new float[frames * SourceChannels];
            for (int i = 0; i < floatSamples.Length; i++) {
                short sample = (short)(stereoPcm[i * 2] | (stereoPcm[i * 2 + 1] << 8));
                floatSamples[i] = sample / (float)Int16.MaxValue;
            }

            lock (writeLock) {
                if (Volatile.Read(ref disposed) != 0)
                    throw new ObjectDisposedException(nameof(SteamMicrophoneEndpoint));

                if (resampler == null) {
                    WriteFloats(floatSamples, floatSamples.Length / SourceChannels);
                    return;
                }

                int maxOutFrames = (int)Math.Ceiling(frames * resampleRatio) + 4;
                var resampled = new float[maxOutFrames * SourceChannels];
                (int used, int generated) = resampler.Process(
                    floatSamples, frames, resampled, maxOutFrames, false);
                // Fixed-size 10ms pushes with no continuous carry-over buffer here (unlike
                // BluetoothAudioCapture/UsbAudioLoopback's live capture streams) - libsamplerate
                // not fully draining one call is rare enough for small, uniform chunks like this
                // one that dropping the last few unconsumed frames is an acceptable simplification
                // rather than adding a carry buffer for what would be a handful of samples.
                _ = used;
                WriteFloats(resampled, generated);
            }
        }

        private void WriteFloats(float[] samples, int frames) {
            var bytes = new byte[frames * SourceChannels * 4];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            outputBuffer.AddSamples(bytes, 0, bytes.Length);
        }

        // No API to ask the driver whether anything has actually opened the mic - unlike VIIPER,
        // this is a plain WDM driver with no companion server to query. Always active once
        // selected: BluetoothMicrophoneWorker keeps decoding/rendering for as long as the feature
        // is enabled, rather than only while some app happens to have the endpoint open.
        public bool IsMicrophoneInterfaceActive() => Volatile.Read(ref disposed) == 0;

        public void Dispose() {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            lock (writeLock) {
                try { output.Stop(); } catch { }
                output.Dispose();
            }
        }
    }
}
