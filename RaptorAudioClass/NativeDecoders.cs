using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace RaptorAudio
{
    internal static unsafe class NativeMp3
    {
        private const string Lib = "minimp3";
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mp3_open_memory(byte* data, int length);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mp3_get_channels(IntPtr handle);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mp3_get_sample_rate(IntPtr handle);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long mp3_get_total_samples(IntPtr handle);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mp3_read_float(IntPtr handle, float* output, int maxSamples);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mp3_seek_sample(IntPtr handle, long sampleIndex);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mp3_close(IntPtr handle);
    }
    internal static unsafe class NativeVorbis
    {
        private const string Lib = "stb_vorbis";

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr vorbis_open_memory(byte* data, int length);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vorbis_get_channels(IntPtr handle);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vorbis_get_sample_rate(IntPtr handle);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long vorbis_get_total_samples(IntPtr handle);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vorbis_read_float(IntPtr handle, float* output, int maxSamples);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vorbis_seek_sample(IntPtr handle, long sampleIndex);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vorbis_close(IntPtr handle);
    }
}
