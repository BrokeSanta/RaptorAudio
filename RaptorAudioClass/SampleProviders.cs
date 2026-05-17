using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RaptorAudio
{
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
}
