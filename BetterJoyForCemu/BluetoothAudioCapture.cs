using System;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BetterJoyForCemu {
    // Runs inside the per-session input-helper process (InputHelper.cs) - WASAPI loopback capture
    // needs an interactive desktop, which Session 0 (BetterJoyService) doesn't have. See
    // IJoyconHost.StartBluetoothAudioCapture for why this lives here instead of in the service.
    //
    // Pipeline: EventSyncedLoopbackCapture (below, built on NAudio's WasapiCapture - already a
    // project dependency) on the selected/default render endpoint -> downmix to stereo float
    // (matches nefarius/DS4AudioStreamer's Downmixer.cs, MIT) -> SampleRateResampler
    // (SampleRateResampler.cs, libsamplerate) to 32kHz float -> manual float-to-16-bit-PCM
    // conversion -> SbcEncoder (already added for the phase-1 test tone) encodes CodeSize-byte PCM
    // blocks one at a time. An earlier attempt used NAudio's own MediaFoundationResampler here to
    // avoid a second native dependency; on real hardware that measurably dropped a fixed ~19% of
    // every callback's audio (confirmed via this file's own debug logging), so this now matches
    // the reference project's actual resampler choice instead.
    // NAudio's own WasapiLoopbackCapture only exposes a parameterless/default-device constructor
    // pairing with the default (non-event-synced, ~100ms-buffered) WasapiCapture base behavior.
    // Ported from nefarius/DS4AudioStreamer's BufferedLoopbackCapture (MIT): event-synced with the
    // minimum buffer the audio engine allows, delivering audio in small, steady chunks instead of
    // occasional ~100ms bursts - the bursty version measurably produced periodic audio skips on
    // real hardware, since DualShock4Controller's stream worker expects roughly steady arrival to
    // pace its own real-time output against.
    internal sealed class EventSyncedLoopbackCapture : WasapiCapture {
        public EventSyncedLoopbackCapture(MMDevice captureDevice) : base(captureDevice, true, 0) { }

        protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
            AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
    }

    internal sealed class BluetoothAudioCapture : IDisposable {
        private const int TargetSampleRate = 32000;
        private const int TargetChannels = 2;

        private readonly Action<byte[]> onFrame;
        private EventSyncedLoopbackCapture capture;
        private SampleRateResampler resampler;
        private SbcEncoder encoder;
        private float[] sourceCarry = new float[0]; // unconsumed stereo float frames, interleaved
        private byte[] pcmCarry = new byte[0]; // resampled 16-bit PCM bytes not yet a full SBC block
        private readonly object lifecycleLock = new object();
        private Stopwatch debugStopwatch;
        private double debugLastCallbackMs;

        public BluetoothAudioCapture(Action<byte[]> onFrame) {
            this.onFrame = onFrame;
        }

        // Never lets an exception escape: this runs on the input helper's single shared pipe
        // read-loop thread, which also drives keyboard/mouse remap - an unhandled throw here
        // (a missing/exclusive-mode audio device, a resampler init failure, ...) would otherwise
        // kill that entire loop and take the whole helper connection down with it, not just audio.
        //
        // Construction happens entirely outside lifecycleLock, only assigning the finished objects
        // to fields under a brief lock, and Stop (below) never calls a capture/resampler/encoder
        // method while holding that same lock either - WasapiCapture.StopRecording can block
        // waiting for its own callback thread to finish, and that callback thread
        // (OnDataAvailable) also takes lifecycleLock, so calling StopRecording from inside the
        // lock would risk a real deadlock between the two threads.
        public void Start(string endpointId) {
            Stop();
            try {
                StartInner(endpointId);
            } catch {
                Stop(); // tear down whatever partially came up before the throw
            }
        }

        private void StartInner(string endpointId) {
            MMDevice device = null;
            using (var enumerator = new MMDeviceEnumerator()) {
                if (!String.IsNullOrEmpty(endpointId)) {
                    try { device = enumerator.GetDevice(endpointId); } catch { device = null; }
                }
                if (device == null)
                    device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }

            var newCapture = new EventSyncedLoopbackCapture(device);
            double ratio = (double)TargetSampleRate / newCapture.WaveFormat.SampleRate;
            var newResampler = new SampleRateResampler(ResampleQuality.SincBest, TargetChannels, ratio);
            var newEncoder = new SbcEncoder(TargetSampleRate, SbcSubBandCount.Sb8, 48,
                SbcChannelMode.JointStereo, SbcAllocationMode.Snr, SbcBlockCount.Blk16);

            lock (lifecycleLock) {
                capture = newCapture;
                resampler = newResampler;
                encoder = newEncoder;
                sourceCarry = new float[0];
                pcmCarry = new byte[0];
                debugStopwatch = Stopwatch.StartNew();
                debugLastCallbackMs = 0;
            }

            newCapture.DataAvailable += OnDataAvailable;
            newCapture.StartRecording();
            AudioDebugLog.Write("Capture", "Start device=" + device.FriendlyName +
                " sourceRate=" + newCapture.WaveFormat.SampleRate +
                " sourceChannels=" + newCapture.WaveFormat.Channels +
                " codeSize=" + newEncoder.CodeSize + " frameSize=" + newEncoder.FrameSize);
        }

        public void Stop() {
            EventSyncedLoopbackCapture captureToStop;
            SampleRateResampler resamplerToDispose;
            SbcEncoder encoderToDispose;
            lock (lifecycleLock) {
                captureToStop = capture;
                resamplerToDispose = resampler;
                encoderToDispose = encoder;
                capture = null;
                resampler = null;
                encoder = null;
            }

            if (captureToStop != null) {
                captureToStop.DataAvailable -= OnDataAvailable;
                try { captureToStop.StopRecording(); } catch { }
                captureToStop.Dispose();
                AudioDebugLog.Write("Capture", "Stop");
            }
            resamplerToDispose?.Dispose();
            encoderToDispose?.Dispose();
        }

        // Runs on a WASAPI-internal callback thread - an unhandled exception here would take
        // capture down (or worse) with no path back for this session until the helper relaunches.
        // Best-effort: drop this one buffer's worth of audio rather than the whole stream.
        private void OnDataAvailable(object sender, WaveInEventArgs e) {
            try {
                OnDataAvailableLocked(e);
            } catch {
            }
        }

        private void OnDataAvailableLocked(WaveInEventArgs e) {
            lock (lifecycleLock) {
                if (capture == null)
                    return; // stopped concurrently with a capture callback already in flight

                int sourceChannels = capture.WaveFormat.Channels;
                int floatCount = e.BytesRecorded / 4;
                var samples = new float[floatCount];
                Buffer.BlockCopy(e.Buffer, 0, samples, 0, floatCount * 4);

                if (sourceChannels < 2)
                    return; // mono/invalid loopback format - nothing sensible to downmix

                int frames = floatCount / sourceChannels;
                // Always routed through Downmix, even when sourceChannels is already 2 - it isn't
                // just a channel-count reducer, it also applies the reference's 0.5x headroom
                // scale unconditionally. An earlier "already stereo, skip it" fast path silently
                // dropped that scaling for the common case (a plain stereo default device),
                // sending full-scale audio into the encoder - real hardware showed that as
                // clipping (muffled/distorted audio), not a channel-mixing problem.
                var stereo = new float[frames * 2];
                Downmix(samples, stereo, frames, sourceChannels);

                // Prepend whatever libsamplerate didn't consume last callback - it doesn't
                // guarantee draining everything given to it in one Process() call.
                float[] input;
                int inputFrames;
                int carryFrames = sourceCarry.Length / 2;
                if (carryFrames == 0) {
                    input = stereo;
                    inputFrames = frames;
                } else {
                    input = new float[sourceCarry.Length + stereo.Length];
                    Buffer.BlockCopy(sourceCarry, 0, input, 0, sourceCarry.Length * 4);
                    Buffer.BlockCopy(stereo, 0, input, sourceCarry.Length * 4, stereo.Length * 4);
                    inputFrames = carryFrames + frames;
                }

                int maxOutFrames = (int)Math.Ceiling(inputFrames * resampler.Ratio) + 16;
                var resampled = new float[maxOutFrames * 2];
                (int used, int generated) = resampler.Process(input, inputFrames, resampled, maxOutFrames, false);

                int unusedFrames = inputFrames - used;
                if (unusedFrames > 0) {
                    sourceCarry = new float[unusedFrames * 2];
                    Buffer.BlockCopy(input, used * 2 * 4, sourceCarry, 0, sourceCarry.Length * 4);
                } else {
                    sourceCarry = new float[0];
                }

                var pcm = new byte[generated * TargetChannels * 2];
                for (int i = 0; i < generated * TargetChannels; i++) {
                    float sample = Math.Max(-1f, Math.Min(1f, resampled[i]));
                    short pcmSample = (short)Math.Round(sample * short.MaxValue);
                    pcm[i * 2] = (byte)pcmSample;
                    pcm[i * 2 + 1] = (byte)(pcmSample >> 8);
                }

                int framesEncoded = pcm.Length > 0 ? ConsumeResampledPcm(pcm, pcm.Length) : 0;

                double nowMs = debugStopwatch.Elapsed.TotalMilliseconds;
                double intervalMs = nowMs - debugLastCallbackMs;
                debugLastCallbackMs = nowMs;
                AudioDebugLog.Write("Capture", "callbackIntervalMs=" + intervalMs.ToString("F1") +
                    " bytesRecorded=" + e.BytesRecorded + " inputFrames=" + inputFrames +
                    " used=" + used + " generated=" + generated + " framesEncoded=" + framesEncoded);
            }
        }

        private int ConsumeResampledPcm(byte[] data, int length) {
            byte[] pcm;
            if (pcmCarry.Length == 0) {
                pcm = data;
            } else {
                pcm = new byte[pcmCarry.Length + length];
                Buffer.BlockCopy(pcmCarry, 0, pcm, 0, pcmCarry.Length);
                Buffer.BlockCopy(data, 0, pcm, pcmCarry.Length, length);
                length = pcm.Length;
            }

            int codeSize = encoder.CodeSize;
            int offset = 0;
            int framesEncoded = 0;
            byte[] pcmBlock = new byte[codeSize];
            byte[] sbcBlock = new byte[encoder.FrameSize];
            while (length - offset >= codeSize) {
                Buffer.BlockCopy(pcm, offset, pcmBlock, 0, codeSize);
                offset += codeSize;

                int encoded = encoder.Encode(pcmBlock, sbcBlock);
                if (encoded <= 0)
                    continue;

                byte[] frame = new byte[encoded];
                Buffer.BlockCopy(sbcBlock, 0, frame, 0, encoded);
                onFrame(frame);
                framesEncoded++;
            }

            int remaining = length - offset;
            pcmCarry = new byte[remaining];
            if (remaining > 0)
                Buffer.BlockCopy(pcm, offset, pcmCarry, 0, remaining);

            return framesEncoded;
        }

        // Ported from nefarius/DS4AudioStreamer's Downmixer.DownmixToStereo (MIT). Always called,
        // even for an already-2-channel source (see the caller's comment) - the unconditional
        // 0.5x scale on every output sample is the reference's own headroom choice, kept faithful
        // rather than "corrected" without a reason to.
        private static void Downmix(float[] input, float[] output, int frames, int sourceChannels) {
            int inIdx = 0, outIdx = 0;
            for (int i = 0; i < frames; i++) {
                float left = input[inIdx];
                float right = input[inIdx + 1];

                if (sourceChannels > 2) {
                    left += input[inIdx + 2] * 0.7f;
                    right += input[inIdx + 2] * 0.7f;
                }
                if (sourceChannels > 3) {
                    left += input[inIdx + 3] * 0.5f;
                    right += input[inIdx + 3] * 0.5f;
                }
                if (sourceChannels > 4)
                    left += input[inIdx + 4] * 0.7f;
                if (sourceChannels > 5)
                    right += input[inIdx + 5] * 0.7f;
                if (sourceChannels > 6)
                    left += input[inIdx + 6] * 0.7f;
                if (sourceChannels > 7)
                    right += input[inIdx + 7] * 0.7f;

                output[outIdx] = left * 0.5f;
                output[outIdx + 1] = right * 0.5f;

                inIdx += sourceChannels;
                outIdx += 2;
            }
        }

        public void Dispose() => Stop();
    }
}
