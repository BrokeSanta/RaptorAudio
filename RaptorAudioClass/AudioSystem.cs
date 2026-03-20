using NAudio.CoreAudioApi;
using NAudio.Utils;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace RaptorAudio
{
    public unsafe class NativeMp3Reader : WaveStream, ISampleProvider
    {
        private IntPtr handle;
        private readonly WaveFormat waveFormat;
        private readonly long totalSamples;
        private readonly int channels;
        private readonly int sampleRate;
        private long currentSample;
        private bool disposed;
        private NativeBuffer? ownedBuffer; // track buffer we need to free
        public NativeMp3Reader(NativeBuffer buffer) : this(buffer, false) { }
        private NativeMp3Reader(NativeBuffer buffer, bool ownsBuffer)
        {
            if (ownsBuffer)
                ownedBuffer = buffer;

            handle = NativeMp3.mp3_open_memory(buffer.Pointer, buffer.Length);
            if (handle == IntPtr.Zero)
            {
                if (ownsBuffer)
                {
                    buffer.Dispose();
                    ownedBuffer = null;
                }
                throw new InvalidDataException("Failed to open MP3 data");
            }

            channels = NativeMp3.mp3_get_channels(handle);
            sampleRate = NativeMp3.mp3_get_sample_rate(handle);
            totalSamples = NativeMp3.mp3_get_total_samples(handle);
            waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }
        public NativeMp3Reader(string path) : this(LoadFile(path), ownsBuffer: true) { }
        public override WaveFormat WaveFormat => waveFormat;
        public override long Length => totalSamples * sizeof(float);
        public override long Position
        {
            get => currentSample * sizeof(float);
            set
            {
                long sample = value / sizeof(float);
                NativeMp3.mp3_seek_sample(handle, sample);
                currentSample = sample;
            }
        }
        public override TimeSpan TotalTime => TimeSpan.FromSeconds((double)totalSamples / channels / sampleRate);
        public override TimeSpan CurrentTime
        {
            get => TimeSpan.FromSeconds((double)currentSample / channels / sampleRate);
            set => Position = (long)(value.TotalSeconds * sampleRate * channels) * sizeof(float);
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (disposed || handle == IntPtr.Zero) return 0;

            int floatsRequested = count / sizeof(float);

            fixed (byte* bptr = &buffer[offset])
            {
                float* fptr = (float*)bptr;
                int read = NativeMp3.mp3_read_float(handle, fptr, floatsRequested);
                currentSample += read;
                return read * sizeof(float);
            }
        }
        public int Read(float[] buffer, int offset, int count)
        {
            if (disposed || handle == IntPtr.Zero) return 0;

            fixed (float* fptr = &buffer[offset])
            {
                int read = NativeMp3.mp3_read_float(handle, fptr, count);
                currentSample += read;
                return read;
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (!disposed)
            {
                disposed = true;
                if (handle != IntPtr.Zero)
                {
                    NativeMp3.mp3_close(handle);
                    handle = IntPtr.Zero;
                }
                // Free the native buffer if we own it
                if (ownedBuffer.HasValue)
                {
                    var buf = ownedBuffer.Value;
                    buf.Dispose();
                    ownedBuffer = null;
                }
            }
            base.Dispose(disposing);
        }
        private static NativeBuffer LoadFile(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            var buf = new NativeBuffer(data.Length);
            fixed (byte* src = data)
                Buffer.MemoryCopy(src, buf.Pointer, buf.Length, data.Length);
            return buf;
        }
    }
    public unsafe class NativeVorbisReader : WaveStream, ISampleProvider
    {
        private IntPtr handle;
        private readonly WaveFormat waveFormat;
        private readonly long totalSamples; // in per-channel samples
        private readonly int channels;
        private readonly int sampleRate;
        private long currentSample;
        private bool disposed;
        private NativeBuffer? ownedBuffer; // track buffer we need to free

        /// <summary>
        /// Opens an OGG/Vorbis from a NativeBuffer (caller manages the buffer's lifetime).
        /// </summary>
        public NativeVorbisReader(NativeBuffer buffer) : this(buffer, false) { }

        private NativeVorbisReader(NativeBuffer buffer, bool ownsBuffer)
        {
            if (ownsBuffer)
                ownedBuffer = buffer;

            handle = NativeVorbis.vorbis_open_memory(buffer.Pointer, buffer.Length);
            if (handle == IntPtr.Zero)
            {
                if (ownsBuffer)
                {
                    buffer.Dispose();
                    ownedBuffer = null;
                }
                throw new InvalidDataException("Failed to open Vorbis data");
            }

            channels = NativeVorbis.vorbis_get_channels(handle);
            sampleRate = NativeVorbis.vorbis_get_sample_rate(handle);
            totalSamples = NativeVorbis.vorbis_get_total_samples(handle);
            waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        /// <summary>
        /// Opens an OGG/Vorbis from a file path (loads entirely into memory, owns the buffer).
        /// </summary>
        public NativeVorbisReader(string path) : this(LoadFile(path), ownsBuffer: true) { }

        public override WaveFormat WaveFormat => waveFormat;

        public override long Length => (long)totalSamples * channels * sizeof(float);

        public override long Position
        {
            get => currentSample * channels * sizeof(float);
            set
            {
                long sample = value / (channels * sizeof(float));
                NativeVorbis.vorbis_seek_sample(handle, sample);
                currentSample = sample;
            }
        }

        public override TimeSpan TotalTime =>
            TimeSpan.FromSeconds((double)totalSamples / sampleRate);

        public override TimeSpan CurrentTime
        {
            get => TimeSpan.FromSeconds((double)currentSample / sampleRate);
            set => Position = (long)(value.TotalSeconds * sampleRate) * channels * sizeof(float);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (disposed || handle == IntPtr.Zero) return 0;

            int floatsRequested = count / sizeof(float);
            int samplesRequested = floatsRequested / channels;

            fixed (byte* bptr = &buffer[offset])
            {
                float* fptr = (float*)bptr;
                // vorbis_read_float returns samples (not floats)
                int samplesRead = NativeVorbis.vorbis_read_float(handle, fptr, samplesRequested);
                currentSample += samplesRead;
                return samplesRead * channels * sizeof(float);
            }
        }
        public int Read(float[] buffer, int offset, int count)
        {
            if (disposed || handle == IntPtr.Zero) return 0;

            int samplesRequested = count / channels;

            fixed (float* fptr = &buffer[offset])
            {
                int samplesRead = NativeVorbis.vorbis_read_float(handle, fptr, samplesRequested);
                currentSample += samplesRead;
                return samplesRead * channels;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposed)
            {
                disposed = true;
                if (handle != IntPtr.Zero)
                {
                    NativeVorbis.vorbis_close(handle);
                    handle = IntPtr.Zero;
                }
                // Free the native buffer if we own it
                if (ownedBuffer.HasValue)
                {
                    var buf = ownedBuffer.Value;
                    buf.Dispose();
                    ownedBuffer = null;
                }
            }
            base.Dispose(disposing);
        }

        private static NativeBuffer LoadFile(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            var buf = new NativeBuffer(data.Length);
            fixed (byte* src = data)
                Buffer.MemoryCopy(src, buf.Pointer, buf.Length, data.Length);
            return buf;
        }
    }

    public unsafe class NativeMixer : ISampleProvider, IDisposable
    {
        private readonly List<ISampleProvider> sources;
        private const int MaxInputs = 1024;

        private float[] sourceBuffer;
        private GCHandle pinnedHandle;
        private float* sourcePtr;
        private int sourceBufferLen;
        private bool disposed;
        public WaveFormat WaveFormat { get; private set; }
        public bool ReadFully { get; set; }
        public IEnumerable<ISampleProvider> MixerInputs => sources;
        public NativeMixer(WaveFormat waveFormat)
        {
            if (waveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
                throw new ArgumentException("Mixer wave format must be IEEE float");

            sources = new List<ISampleProvider>();
            WaveFormat = waveFormat;
        }
        private void EnsureBuffer(int count)
        {
            if (sourceBufferLen >= count) return;

            // Free old pinned buffer if it exists
            if (pinnedHandle.IsAllocated)
                pinnedHandle.Free();

            // GC.AllocateArray with pinned:true gives us an array the GC will never relocate.
            // This means sourcePtr stays valid forever without needing fixed blocks.
            sourceBuffer = GC.AllocateArray<float>(count, pinned: true);
            pinnedHandle = GCHandle.Alloc(sourceBuffer, GCHandleType.Pinned);
            sourcePtr = (float*)pinnedHandle.AddrOfPinnedObject();
            sourceBufferLen = count;
        }


        public NativeMixer(IEnumerable<ISampleProvider> sources)
        {
            this.sources = new List<ISampleProvider>();
            foreach (var source in sources)
            {
                AddMixerInput(source);
            }
            if (this.sources.Count == 0)
            {
                throw new ArgumentException("Must provide at least one input in this constructor");
            }
        }


        [System.Runtime.CompilerServices.SkipLocalsInit]
        public int Read(float[] buffer, int offset, int count)
        {
            if (disposed) return 0;
            int outputSamples = 0;
            EnsureBuffer(count);
            fixed (float* dst = &buffer[offset])
            {
                NativeMemory.Clear(dst, (nuint)(count * sizeof(float)));
                lock (sources)
                {
                    for (int i = sources.Count - 1; i >= 0; i--)
                    {
                        NativeMemory.Clear(sourcePtr, (nuint)(count * sizeof(float)));
                        int read = sources[i].Read(sourceBuffer, 0, count);

                        if (read == 0)
                        {
                            sources.RemoveAt(i);
                            continue;
                        }

                        // SIMD-accelerated sum
                        SumBuffers(dst, sourcePtr, read);

                        outputSamples = Math.Max(outputSamples, read);
                    }
                }
            }
            if (ReadFully && outputSamples < count)
                outputSamples = count;

            return outputSamples;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SumBuffers(float* dst, float* src, int count)
        {
            int n = 0;
            int vectorSize = Vector<float>.Count; // 4 (SSE), 8 (AVX2), or 4 (NEON)

            // SIMD bulk: process vectorSize floats per iteration
            int limit = count - (count % vectorSize);
            for (; n < limit; n += vectorSize)
            {
                // Load vectorSize floats from each buffer into SIMD registers,
                // add them, and write back — all in ~1 clock cycle.
                var output = *(Vector<float>*)(dst + n);
                var input = *(Vector<float>*)(src + n);
                *(Vector<float>*)(dst + n) = output + input;
            }

            // Scalar tail: handle remaining samples that don't fill a full vector
            for (; n < count; n++)
            {
                dst[n] += src[n];
            }
        }
        public void AddMixerInput(ISampleProvider mixerInput)
        {
            lock (sources)
            {
                if (sources.Count >= MaxInputs)
                    throw new InvalidOperationException("Too many mixer inputs");
                sources.Add(mixerInput);
            }

            if (WaveFormat == null)
            {
                WaveFormat = mixerInput.WaveFormat;
            }
            else if (WaveFormat.SampleRate != mixerInput.WaveFormat.SampleRate ||
                     WaveFormat.Channels != mixerInput.WaveFormat.Channels)
            {
                throw new ArgumentException("All mixer inputs must have the same WaveFormat");
            }
        }

        public void RemoveMixerInput(ISampleProvider mixerInput)
        {
            lock (sources)
            {
                sources.Remove(mixerInput);
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            lock (sources)
            {
                sources.Clear();
            }

            if (pinnedHandle.IsAllocated)
                pinnedHandle.Free();

            sourceBuffer = null;
            sourcePtr = null;
            sourceBufferLen = 0;
        }
    }
    public class CorruptionSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private float lastSampleL;
        private float lastSampleR;
        private int sampleCounter;
        private float filterL;
        private float filterR;
        private float tremoloPhase;

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Distortion { get; set; } = 0;
        public int Bitcrush { get; set; } = 0;
        public int SampleCrush { get; set; } = 0;
        public int Filter { get; set; } = 0;
        public int Tremolo { get; set; } = 0;

        public CorruptionSampleProvider(ISampleProvider source)
        {
            this.source = source;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);

            int distortion = Distortion;
            int bitcrush = Bitcrush;
            int sampleCrush = SampleCrush;
            int filter = Filter;
            int tremolo = Tremolo;

            if (distortion <= 0 && bitcrush <= 0 && sampleCrush <= 0
                && filter <= 0 && tremolo <= 0)
                return read;

            float dMix = distortion / 100f;
            float bMix = bitcrush / 100f;
            float sMix = sampleCrush / 100f;
            float fMix = filter / 100f;
            float tMix = tremolo / 100f;

            float drive = 10f;
            float crushLevels = 6f;
            int crushRate = 6;
            float cutoff = 0.05f;
            float tremoloDepth = 0.8f;
            float tremoloRate = 14f;
            float phaseInc = tremoloRate / WaveFormat.SampleRate * 2f;

            for (int i = 0; i < read; i += 2)
            {
                float left = buffer[offset + i];
                float right = buffer[offset + i + 1];

                float corruptedL = left;
                float corruptedR = right;

                if (distortion > 0)
                {
                    float distL = MathF.Tanh(corruptedL * (1f + drive));
                    float distR = MathF.Tanh(corruptedR * (1f + drive));
                    corruptedL = corruptedL * (1f - dMix) + distL * dMix;
                    corruptedR = corruptedR * (1f - dMix) + distR * dMix;
                }

                if (bitcrush > 0)
                {
                    float crushedL = MathF.Round(corruptedL * crushLevels) / crushLevels;
                    float crushedR = MathF.Round(corruptedR * crushLevels) / crushLevels;
                    corruptedL = corruptedL * (1f - bMix) + crushedL * bMix;
                    corruptedR = corruptedR * (1f - bMix) + crushedR * bMix;
                }

                if (sampleCrush > 0)
                {
                    float crushL, crushR;
                    if (sampleCounter % crushRate != 0)
                    {
                        crushL = lastSampleL;
                        crushR = lastSampleR;
                    }
                    else
                    {
                        crushL = corruptedL;
                        crushR = corruptedR;
                        lastSampleL = corruptedL;
                        lastSampleR = corruptedR;
                    }
                    corruptedL = corruptedL * (1f - sMix) + crushL * sMix;
                    corruptedR = corruptedR * (1f - sMix) + crushR * sMix;
                    sampleCounter++;
                }

                if (filter > 0)
                {
                    filterL += cutoff * (corruptedL - filterL);
                    filterR += cutoff * (corruptedR - filterR);
                    corruptedL = corruptedL * (1f - fMix) + filterL * fMix;
                    corruptedR = corruptedR * (1f - fMix) + filterR * fMix;
                }

                if (tremolo > 0)
                {
                    float trem = 1f - (tremoloDepth * (0.5f + 0.5f * MathF.Sin(tremoloPhase * MathF.PI)));
                    float tremL = corruptedL * trem;
                    float tremR = corruptedR * trem;
                    corruptedL = corruptedL * (1f - tMix) + tremL * tMix;
                    corruptedR = corruptedR * (1f - tMix) + tremR * tMix;
                    tremoloPhase += phaseInc;
                    if (tremoloPhase > 2f) tremoloPhase -= 2f;
                }

                buffer[offset + i] = corruptedL;
                buffer[offset + i + 1] = corruptedR;
            }

            return read;
        }
    }
    public class AutoDisposeSampleProvider : ISampleProvider, IDisposable
    {
        private readonly ISampleProvider sampleSource;
        private readonly WaveStream waveStream;
        private readonly bool looping;
        private bool disposed;
        private bool finished;
        public event Action Finished;

        public WaveFormat WaveFormat => sampleSource.WaveFormat;

        public AutoDisposeSampleProvider(ISampleProvider source, WaveStream stream, bool looping)
        {
            this.sampleSource = source;
            this.waveStream = stream;
            this.looping = looping;
        }
        [System.Runtime.CompilerServices.SkipLocalsInit]
        public int Read(float[] buffer, int offset, int count)
        {
            if (disposed || finished) return 0;

            int total = 0;
            while (total < count)
            {
                int read = sampleSource.Read(buffer, offset + total, count - total);
                if (read == 0)
                {
                    if (looping)
                    {
                        waveStream.Position = 0;
                        continue;
                    }
                    else
                    {
                        finished = true;
                        // Fire the event off the audio thread to avoid deadlocks.
                        var handler = Finished;
                        if (handler != null)
                        {
                            Task.Run(() => handler.Invoke());
                        }
                        return total;
                    }
                }
                total += read;
            }
            return total;
        }

        public void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
                Finished = null;
                waveStream?.Dispose();
            }
        }
    }
    public unsafe struct NativeBuffer : IDisposable
    {
        public byte* Pointer;
        public int Length;
        public NativeBuffer(int length)
        {
            Pointer = (byte*)NativeMemory.Alloc((nuint)length);
            Length = length;
        }
        public readonly byte[] ToManagedArray()
        {
            if (Pointer == null)
                throw new ObjectDisposedException(nameof(NativeBuffer));
            byte[] managed = new byte[Length];
            fixed (byte* dst = managed)
            {
                Buffer.MemoryCopy(Pointer, dst, Length, Length);
            }
            return managed;
        }
        public void Dispose()
        {
            if (Pointer != null)
            {
                NativeMemory.Free(Pointer);
                Pointer = null;
                Length = 0;
            }
        }
    }
    internal static class AudioPlatform
    {
        public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        public static IWavePlayer CreateOutput()
        {
            if (IsWindows)
            {
                return new WasapiOut(AudioClientShareMode.Shared, 40);
            }
            else
            {
                return new NAudio.Sdl2.WaveOutSdl();
            }
        }
        public static WaveStream CreateReader(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".mp3" => new NativeMp3Reader(path),
                ".ogg" => new NativeVorbisReader(path),
                ".wav" => new WaveFileReader(path),
                _ => throw new NotSupportedException($"Audio format not supported: {ext}")
            };
        }
        public static unsafe WaveStream CreateReader(NativeBuffer buffer, string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".mp3" => new NativeMp3Reader(buffer),
                ".ogg" => new NativeVorbisReader(buffer),
                ".wav" => new WaveFileReader(new UnmanagedMemoryStream(buffer.Pointer, buffer.Length)),
                _ => throw new NotSupportedException($"Not supported: {ext}")
            };
        }
    }
    internal class AudioInstance : IDisposable
    {
        public string name { get; }
        public WaveStream reader { get; private set; }
        public VolumeSampleProvider isample { get; private set; }
        public bool looping { get; }
        public long savedposition;
        public string path;
        public string actualname { get; }
        public AutoDisposeSampleProvider eventiful { get; private set; }
        private Stream owningStream;
        private bool disposed = false;
        public bool isPlaying { get; set; } = false;

        public AudioInstance(string name, string path, NativeBuffer? cached, int volume, bool looping)
        {
            this.name = name ?? throw new ArgumentNullException(nameof(name));
            this.path = path ?? throw new ArgumentNullException(nameof(path));
            this.actualname = System.IO.Path.GetFileNameWithoutExtension(path);
            this.looping = looping;
            if (cached.HasValue)
            {
                reader = AudioPlatform.CreateReader(cached.Value, path);
            }
            else
            {
                reader = AudioPlatform.CreateReader(path);
            }

            var sampleProvider = reader as ISampleProvider ?? reader.ToSampleProvider();
            var sampleprovider = new AutoDisposeSampleProvider(sampleProvider, reader, this.looping);
            eventiful = sampleprovider;
            var resampled = new WdlResamplingSampleProvider(sampleprovider, 48000);
            var stereo = resampled.WaveFormat.Channels == 1 ? resampled.ToStereo() : resampled;
            isample = new VolumeSampleProvider(stereo) { Volume = Math.Clamp(volume / 100f, 0f, 1f) };
        }

        // Convenience overload for non-cached usage
        public AudioInstance(string name, string path, int volume, bool looping)
            : this(name, path, (NativeBuffer?)null, volume, looping) { }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;

            // AutoDisposeSampleProvider.Dispose() already disposes the reader,
            // so we must NOT dispose reader a second time.
            eventiful?.Dispose();

            // Dispose the MemoryStream if we created one
            owningStream?.Dispose();

            eventiful = null;
            isample = null;
            reader = null;
            owningStream = null;
        }
    }

    public class AudioSystem : IDisposable
    {
        private Dictionary<string, AudioInstance> audiolist;
        private Dictionary<string, Action> activehandlers;
        private Dictionary<string, NativeBuffer> audioCache;
        private IWavePlayer player;
        private NativeMixer mixer;
        private CorruptionSampleProvider CorruptionControl;
        private bool disposed;
        private readonly object audiolock = new object();

        public AudioSystem()
        {
            audiolist = new Dictionary<string, AudioInstance>(StringComparer.Ordinal);
            activehandlers = new();
            audioCache = new Dictionary<string, NativeBuffer>(StringComparer.OrdinalIgnoreCase);
            disposed = false;
            player = AudioPlatform.CreateOutput();

            mixer = new NativeMixer(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))
            {
                ReadFully = true
            };
            CorruptionControl = new CorruptionSampleProvider(mixer);
            player.Init(CorruptionControl);
            player.Play();
        }

        private float AudioScaling(int volume)
        {
            return Math.Clamp(volume / 100f, 0f, 1f);
        }

        public void Cache(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be null or empty", nameof(path));
            if (!File.Exists(path))
                throw new FileNotFoundException($"Audio file not found: {path}");

            lock (audiolock)
            {
                if (!audioCache.ContainsKey(path))
                {
                    byte[] temp = File.ReadAllBytes(path);
                    var buffer = new NativeBuffer(temp.Length);
                    try
                    {
                        unsafe
                        {
                            fixed (byte* src = temp)
                            {
                                Buffer.MemoryCopy(src, buffer.Pointer, buffer.Length, temp.Length);
                            }
                        }
                        audioCache[path] = buffer;
                    }
                    catch
                    {
                        buffer.Dispose();
                        throw;
                    }
                }
            }
        }

        public void Uncache(string path)
        {
            lock (audiolock)
            {
                if (audioCache.TryGetValue(path, out var buffer))
                {
                    buffer.Dispose(); // FREE the native memory!
                    audioCache.Remove(path);
                }
            }
        }

        public void Instantiate(string name, string path, int volume, bool looping)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be null or empty", nameof(name));
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be null or empty", nameof(path));
            if (!File.Exists(path))
                throw new FileNotFoundException($"Audio file not found: {path}");
            NativeBuffer? cached = null;
            lock (audiolock)
            {
                if (disposed)
                    throw new ObjectDisposedException(nameof(AudioSystem));
                if (audioCache.TryGetValue(path, out var nativeBuffer))
                {
                    cached = nativeBuffer;
                }
            }
            AudioInstance instance;
            try
            {
                instance = new AudioInstance(name, path, cached, volume, looping);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error instantiating audio '{name}': {ex.Message}");
                throw;
            }
            lock (audiolock)
            {
                if (disposed)
                {
                    instance.Dispose();
                    throw new ObjectDisposedException(nameof(AudioSystem));
                }
                if (audiolist.ContainsKey(name))
                    DeleteInternal(name);
                audiolist.Add(name, instance);
            }
        }

        public Task Play(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be null or empty", nameof(name));

            lock (audiolock)
            {
                if (disposed)
                    throw new ObjectDisposedException(nameof(AudioSystem));

                if (!audiolist.TryGetValue(name, out var instance))
                    throw new KeyNotFoundException($"Audio instance '{name}' not found");

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                // Detach any previous finished handler for this name
                if (activehandlers.TryGetValue(name, out var oldHandler))
                {
                    if (instance.eventiful != null)
                        instance.eventiful.Finished -= oldHandler;
                    activehandlers.Remove(name);
                }

                Action action = null;
                action = () =>
                {
                    lock (audiolock)
                    {
                        if (instance.eventiful != null)
                            instance.eventiful.Finished -= action;

                        activehandlers.Remove(name);
                        instance.isPlaying = false;

                        try { mixer.RemoveMixerInput(instance.isample); } catch { }
                    }
                    tcs.TrySetResult(true);
                };

                activehandlers[name] = action;
                instance.eventiful.Finished += action;

                // Remove then re-add to mixer to ensure a clean start
                try { mixer.RemoveMixerInput(instance.isample); } catch { }

                instance.reader.Position = 0;
                instance.isPlaying = true;

                try
                {
                    mixer.AddMixerInput(instance.isample);
                }
                catch (Exception ex)
                {
                    instance.eventiful.Finished -= action;
                    activehandlers.Remove(name);
                    instance.isPlaying = false;
                    Console.WriteLine($"Error adding mixer input: {ex.Message}");
                    throw;
                }

                return tcs.Task;
            }
        }
        public void SetDistortion(int percent) => CorruptionControl.Distortion = Math.Clamp(percent, 0, 100);
        public void SetBitcrush(int percent) => CorruptionControl.Bitcrush = Math.Clamp(percent, 0, 100);
        public void SetSampleCrush(int percent) => CorruptionControl.SampleCrush = Math.Clamp(percent, 0, 100);
        public void SetFilter(int percent) => CorruptionControl.Filter = Math.Clamp(percent, 0, 100);
        public void SetTremolo(int percent) => CorruptionControl.Tremolo = Math.Clamp(percent, 0, 100);

        public void ClearEffects()
        {
            CorruptionControl.Distortion = 0;
            CorruptionControl.Bitcrush = 0;
            CorruptionControl.SampleCrush = 0;
            CorruptionControl.Filter = 0;
            CorruptionControl.Tremolo = 0;
        }

        public void ChangeVolume(string name, int volume)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be null or empty", nameof(name));

            lock (audiolock)
            {
                if (disposed)
                    throw new ObjectDisposedException(nameof(AudioSystem));

                if (!audiolist.TryGetValue(name, out var instance))
                    throw new KeyNotFoundException($"Audio instance '{name}' not found");

                instance.isample.Volume = AudioScaling(volume);
            }
        }
        public TimeSpan GetPosition(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be null or empty", nameof(name));
            lock (audiolock)
            {
                if (disposed)
                    throw new ObjectDisposedException(nameof(AudioSystem));

                if (!audiolist.TryGetValue(name, out var instance))
                    throw new KeyNotFoundException($"Audio instance '{name}' not found");
                return instance.reader.CurrentTime;
            }
        }
        public TimeSpan GetDuration(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be null or empty", nameof(name));
            lock (audiolock)
            {
                if (disposed)
                    throw new ObjectDisposedException(nameof(AudioSystem));

                if (!audiolist.TryGetValue(name, out var instance))
                    throw new KeyNotFoundException($"Audio instance '{name}' not found");
                return instance.reader.TotalTime;
            }
        }
        public bool GetIfPlaying(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be null or empty", nameof(name));
            lock (audiolock)
            {
                if (disposed)
                    throw new ObjectDisposedException(nameof(AudioSystem));

                if (!audiolist.TryGetValue(name, out var instance))
                    throw new KeyNotFoundException($"Audio instance '{name}' not found");
                return instance.isPlaying;
            }
        }

        public void Seek(string name, TimeSpan time)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be null or empty", nameof(name));
            lock (audiolock)
            {
                if (disposed)
                    throw new ObjectDisposedException(nameof(AudioSystem));

                if (!audiolist.TryGetValue(name, out var instance))
                    throw new KeyNotFoundException($"Audio instance '{name}' not found");
                try
                {
                    instance.reader.CurrentTime = time;
                    instance.savedposition = instance.reader.Position;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error seeking audio '{name}': {ex.Message}");
                    throw;
                }
            }
        }

        public void Stop(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be null or empty", nameof(name));

            lock (audiolock)
            {
                if (disposed)
                    return;

                if (!audiolist.TryGetValue(name, out var instance))
                {
                    Console.WriteLine($"Warning: Audio instance '{name}' not found for Stop");
                    return;
                }

                try
                {
                    instance.savedposition = instance.reader.Position;
                    mixer.RemoveMixerInput(instance.isample);
                    instance.isPlaying = false;

                    if (activehandlers.TryGetValue(name, out var handler))
                    {
                        instance.eventiful.Finished -= handler;
                        activehandlers.Remove(name);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error stopping audio '{name}': {ex.Message}");
                }
            }
        }

        public void Resume(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be null or empty", nameof(name));

            lock (audiolock)
            {
                if (disposed)
                    throw new ObjectDisposedException(nameof(AudioSystem));

                if (!audiolist.TryGetValue(name, out var instance))
                    throw new KeyNotFoundException($"Audio instance '{name}' not found");

                if (instance.isPlaying)
                    throw new InvalidOperationException($"Audio instance '{name}' is already playing");

                try
                {
                    instance.reader.Position = instance.savedposition;
                    instance.isPlaying = true;
                    mixer.AddMixerInput(instance.isample);
                }
                catch (Exception ex)
                {
                    instance.isPlaying = false;
                    Console.WriteLine($"Error resuming audio '{name}': {ex.Message}");
                    throw;
                }
            }
        }

        public void Delete(string name)
        {
            lock (audiolock)
            {
                DeleteInternal(name);
            }
        }

        private void DeleteInternal(string name)
        {
            if (!audiolist.TryGetValue(name, out var oldInstance))
                return;

            try
            {
                if (activehandlers.TryGetValue(name, out var handler))
                {
                    oldInstance.eventiful.Finished -= handler;
                    activehandlers.Remove(name);
                }

                try
                {
                    mixer.RemoveMixerInput(oldInstance.isample);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Error removing mixer input for '{name}': {ex.Message}");
                }

                oldInstance.isPlaying = false;
                oldInstance.Dispose();
                audiolist.Remove(name);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error deleting audio instance '{name}': {ex.Message}");
            }
        }

        public Task PlayAndForget(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be null or empty", nameof(name));
            string path;
            int volume;
            bool looping;
            NativeBuffer? cached = null;
            lock (audiolock)
            {
                if (disposed)
                    throw new ObjectDisposedException(nameof(AudioSystem));
                if (!audiolist.TryGetValue(name, out var template))
                    throw new KeyNotFoundException($"Audio instance '{name}' not found");
                path = template.path;
                volume = (int)(template.isample.Volume * 100);
                looping = template.looping;

                if (audioCache.TryGetValue(path, out var nativeBuffer))
                {
                    cached = nativeBuffer;
                }
            }
            AudioInstance clone;
            try
            {
                clone = new AudioInstance(name, path, cached, volume, looping);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating audio clone for '{name}': {ex.Message}");
                throw;
            }
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action finishedhandler = null;
            finishedhandler = () =>
            {
                if (clone.eventiful != null)
                    clone.eventiful.Finished -= finishedhandler;

                lock (audiolock)
                {
                    if (!disposed)
                    {
                        try
                        {
                            mixer.RemoveMixerInput(clone.isample);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error removing clone mixer input: {ex.Message}");
                        }
                    }
                }

                clone?.Dispose();
                tcs.TrySetResult(true);
            };

            clone.eventiful.Finished += finishedhandler;

            lock (audiolock)
            {
                if (disposed)
                {
                    clone.Dispose();
                    throw new ObjectDisposedException(nameof(AudioSystem));
                }

                try
                {
                    mixer.AddMixerInput(clone.isample);
                }
                catch (Exception ex)
                {
                    clone.eventiful.Finished -= finishedhandler;
                    clone.Dispose();
                    Console.WriteLine($"Error adding clone mixer input: {ex.Message}");
                    throw;
                }
            }

            return tcs.Task;
        }

        public void Dispose()
        {
            lock (audiolock)
            {
                if (disposed)
                    return;

                disposed = true;

                try
                {
                    player?.Stop();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error stopping output: {ex.Message}");
                }

                foreach (var kvp in activehandlers)
                {
                    if (audiolist.TryGetValue(kvp.Key, out var instance))
                    {
                        try
                        {
                            instance.eventiful.Finished -= kvp.Value;
                        }
                        catch { }
                    }
                }
                activehandlers.Clear();

                foreach (var instance in audiolist.Values.ToArray())
                {
                    try
                    {
                        mixer.RemoveMixerInput(instance.isample);
                    }
                    catch { }

                    instance.Dispose();
                }

                audiolist.Clear();
                foreach (var buffer in audioCache.Values)
                {
                    buffer.Dispose();
                }
                audioCache.Clear();
                try
                {
                    player?.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error disposing output: {ex.Message}");
                }
                mixer?.Dispose();
            }
        }
    }
}
