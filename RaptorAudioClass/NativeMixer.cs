using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace RaptorAudio
{
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
}
