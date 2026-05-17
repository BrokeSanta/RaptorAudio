using NAudio.CoreAudioApi;
using NAudio.Wave;
using RaptorAudio;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace RaptorAudio
{
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
}
