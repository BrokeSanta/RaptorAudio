using NAudio.Wave;
using RaptorAudio;
using System;
using System.Collections.Generic;
using System.Linq;
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
}
