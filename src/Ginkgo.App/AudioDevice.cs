using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Ginkgo.App;

/// <summary>
/// The one sound output for all videos: a Windows (WASAPI) stream on the default speakers, fed by <see cref="AudioMixer"/>. It
/// follows the default device (plugging in headphones moves the sound there) and restarts itself if the device goes away.
/// </summary>
public static class AudioDevice
{
    private const int LatencyMs = 40;
    private static WasapiOut? s_out;
    private static MMDeviceEnumerator? s_enum;
    private static Notify? s_notify;
    private static readonly object s_lock = new();
    public static string Status { get; private set; } = "not started";

    public static void Start()
    {
        lock (s_lock)
        {
            if (s_out is not null) return;
            try
            {
                s_enum ??= new MMDeviceEnumerator();
                if (s_notify is null) { s_notify = new Notify(); s_enum.RegisterEndpointNotificationCallback(s_notify); }
                var dev = s_enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var o = new WasapiOut(dev, AudioClientShareMode.Shared, true, LatencyMs);
                o.Init(new Feed());
                o.PlaybackStopped += (_, e) => { if (!s_stopping) { Status = "restarting" + (e.Exception is null ? "" : ": " + e.Exception.Message); Restart(); } };
                o.Play();
                s_out = o;
                Status = "playing on " + dev.FriendlyName;
            }
            catch (Exception ex) { Status = "no sound output: " + ex.Message; s_out = null; }
        }
    }

    private static volatile bool s_stopping;
    public static void Stop()
    {
        lock (s_lock)
        {
            s_stopping = true;
            try { s_out?.Stop(); s_out?.Dispose(); } catch { }
            s_out = null;
            s_stopping = false;
        }
    }

    private static void Restart() => ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(300); Stop(); Start(); });

    /// <summary>Asks the mixer for each stretch of sound, as floats, 48 kHz stereo.</summary>
    private sealed class Feed : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioMixer.Rate, 2);
        private float[] _f = Array.Empty<float>();

        public int Read(byte[] buffer, int offset, int count)
        {
            int frames = count / 8;
            if (_f.Length < frames * 2) _f = new float[frames * 2];
            // what's asked for now is heard after what's already queued in the output (about its latency)
            try { AudioMixer.Mix(_f, 0, frames, LatencyMs * 1000L); }
            catch { Array.Clear(_f, 0, frames * 2); }                // never let the output stop over one bad stretch
            Buffer.BlockCopy(_f, 0, buffer, offset, frames * 8);
            return frames * 8;
        }
    }

    /// <summary>The default speakers changed (headphones in or out): move to the new ones.</summary>
    private sealed class Notify : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { if (flow == DataFlow.Render && role == Role.Multimedia) Restart(); }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
