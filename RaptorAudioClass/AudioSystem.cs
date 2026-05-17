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
        public float BaseVolume { get; internal set; }

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
            BaseVolume = isample.Volume;
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
        private readonly Dictionary<string, Collectionaire> buses;
        private readonly Dictionary<string, Queue<AudioInstance>> pools;
        private readonly Dictionary<string, int> poolsizes;
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
            buses = new(StringComparer.Ordinal);
            pools = new(StringComparer.Ordinal);
            poolsizes = new(StringComparer.Ordinal);
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
        public void CreatePool(string name, int howmuch)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be empty", nameof(name));
            lock (audiolock)
            {
                if (disposed)
                    throw new ObjectDisposedException(nameof(AudioSystem));
                if (pools.ContainsKey(name))
                    return;
                if (!audiolist.TryGetValue(name, out var template))
                    throw new KeyNotFoundException($"Sound '{name}' not found");
                var queue = new Queue<AudioInstance>(howmuch);
                var templatecollectionaires = buses.Values.Where(item => item.instances.Contains(template)).ToList();
                for (int i = 0; i < howmuch; i++)
                {
                    var clone = CloneInstanceInternal(template);
                    if (clone == null)
                        continue;
                    foreach (var bus in templatecollectionaires)
                    {
                        bus.instances.Add(clone);
                    }
                    queue.Enqueue(clone);
                }
                pools[name] = queue;
                poolsizes[name] = howmuch;
            }
        }
        private AudioInstance CloneInstanceInternal(AudioInstance template)
        {
            try
            {
                NativeBuffer? cached = null;
                if (audioCache.TryGetValue(template.path, out var buffer))
                    cached = buffer;

                // Use template's current BaseVolume when creating clone
                var clone = new AudioInstance(
                    template.name,
                    template.path,
                    cached,
                    (int)(template.BaseVolume * 100),
                    template.looping
                );

                clone.BaseVolume = template.BaseVolume;
                return clone;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to clone '{template.name}': {ex.Message}");
                return null;
            }
        }
        private Task PlayPooledInstance(AudioInstance instance,string orginalname, Queue<AudioInstance> pool)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action finished = null;
            finished = () =>
            {
                if (instance.eventiful != null)
                    instance.eventiful.Finished -= finished;

                lock (audiolock)
                {
                    try { mixer.RemoveMixerInput(instance.isample); } catch { }

                    instance.reader.Position = 0;
                    instance.isPlaying = false;

                    pool.Enqueue(instance); // Put back in pool
                }

                tcs.TrySetResult(true);
            };
            instance.eventiful.Finished += finished;
            lock (audiolock)
            {
                try
                {
                    instance.reader.Position = 0;
                    instance.isPlaying = true;
                    mixer.AddMixerInput(instance.isample);
                }
                catch
                {
                    instance.reader.Position = 0;
                    instance.isPlaying = false;
                    pool.Enqueue(instance);
                    tcs.TrySetResult(false);
                }
            }
            return tcs.Task;
        }
        public Task PlayPooled(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be empty", nameof(name));

            lock (audiolock)
            {
                if (pools.TryGetValue(name, out var queue) && queue.Count > 0)
                {
                    var instance = queue.Dequeue();
                    return PlayPooledInstance(instance, name, queue);
                }
            }

            // If pool is empty or doesn't exist, use normal method
            return PlayAndForget(name);
        }
        public void RemovePool(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;
            lock (audiolock)
            {
                if (disposed)
                    throw new ObjectDisposedException(nameof(AudioSystem));
                if (!pools.TryGetValue(name, out var queue))
                    return;
                while (queue.Count > 0)
                {
                    var instance = queue.Dequeue();
                    if (instance == null) continue;
                    foreach (var bus in buses.Values)
                    {
                        bus.instances.Remove(instance);
                    }
                    try
                    {
                        mixer.RemoveMixerInput(instance.isample);
                    }
                    catch { }
                    instance.Dispose();
                }
                pools.Remove(name);
                poolsizes.Remove(name);
            }
        }
        public void ClearAllPools()
        {
            lock (audiolock)
            {
                if (disposed) return;

                var poolNames = pools.Keys.ToList();

                foreach (string name in poolNames)
                {
                    RemovePool(name);
                }
            }
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

            float newBaseVol = AudioScaling(volume);

            lock (audiolock)
            {
                if (disposed)
                    throw new ObjectDisposedException(nameof(AudioSystem));

                if (!audiolist.TryGetValue(name, out var template))
                    throw new KeyNotFoundException($"Audio instance '{name}' not found");

                // Update template
                template.BaseVolume = newBaseVol;
                template.isample.Volume = newBaseVol;

                // Update pooled instances
                if (pools.TryGetValue(name, out var queue))
                {
                    foreach (var clone in queue)
                    {
                        if (clone?.isample != null)
                        {
                            clone.BaseVolume = newBaseVol;
                            clone.isample.Volume = newBaseVol;
                        }
                    }
                }

                // Update any live PlayAndForget clones
                foreach (var instance in audiolist.Values)
                {
                    if (instance.name == name && instance != template)
                    {
                        if (instance.isample != null)
                        {
                            instance.BaseVolume = newBaseVol;
                            instance.isample.Volume = newBaseVol;
                        }
                    }
                }

                // Re-apply all bus volumes that affect this sound
                ReapplyBusVolumesForSound(name);
            }
        }
        private void ReapplyBusVolumesForSound(string name)
        {
            foreach (var bus in buses.Values)
            {
                var span = CollectionsMarshal.AsSpan(bus.instances);
                for (int i = 0; i < span.Length; i++)
                {
                    var inst = span[i];
                    if (inst?.name == name && inst?.isample != null)
                    {
                        inst.isample.Volume = inst.BaseVolume * bus.Volume;
                    }
                }
            }
        }
        public void CreateCollectionaire(string name)
        {
            lock (audiolock)
            {
                if (disposed) throw new ObjectDisposedException(nameof(AudioSystem));

                if (buses.ContainsKey(name))
                    return;
                var collectionaire = new Collectionaire(name);
                buses.Add(name, collectionaire);
            }
        }
        public void AddToCollectionaire(string audio, string collectionaire)
        {
            lock (audiolock)
            {
                if (disposed)
                    throw new ObjectDisposedException(nameof(AudioSystem));
                if (!buses.TryGetValue(collectionaire, out var bus))
                    throw new KeyNotFoundException($"Collectionaire '{collectionaire}' not found");
                if (!audiolist.TryGetValue(audio, out var instance))
                    throw new KeyNotFoundException($"Audio '{audio}' not found");
                if (!bus.instances.Contains(instance))
                    bus.instances.Add(instance);
            }
        }
        public void ChangeCollectionaireVolume(string collectionaire, float volume)
        {
            lock (audiolock)
            {
                if (disposed)
                    throw new ObjectDisposedException(nameof(AudioSystem));
                if (!buses.TryGetValue(collectionaire, out var bus))
                    throw new KeyNotFoundException($"Bus '{collectionaire}' not found");
                bus.Volume = volume;
                ApplyBusVolume(bus);
            }
        }
        private void ApplyBusVolume(Collectionaire collectionaire)
        {
            float vol = collectionaire.Volume;
            var span = CollectionsMarshal.AsSpan(collectionaire.instances);

            for (int i = 0; i < span.Length; i++)
            {
                var inst = span[i];
                if (inst?.isample != null)
                    inst.isample.Volume = inst.BaseVolume * vol;
            }
        }
        public void RemoveFromCollectionaire(string audio, string collectionaire)
        {
            lock (audiolock)
            {
                if (disposed)
                    throw new ObjectDisposedException(nameof(AudioSystem));
                if (!buses.TryGetValue(collectionaire, out var bus))
                    throw new KeyNotFoundException($"Collectionaire '{collectionaire}' not found");
                if (!audiolist.TryGetValue(audio, out var instance))
                    throw new KeyNotFoundException($"Audio '{audio}' not found");
                bus.instances.Remove(instance);
            }
        }
        public void ClearCollectionaire(string collectionaire)
        {
            lock (audiolock)
            {
                if (buses.TryGetValue(collectionaire, out var bus))
                    bus.instances.Clear();
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
            foreach (var bus in buses.Values)
            {
                bus.instances.Remove(oldInstance);
            }
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
            float basevolume;
            bool looping;
            NativeBuffer? cached = null;
            List<Collectionaire> busesToJoin = null;   // ← New

            lock (audiolock)
            {
                if (disposed)
                    throw new ObjectDisposedException(nameof(AudioSystem));

                if (!audiolist.TryGetValue(name, out var template))
                    throw new KeyNotFoundException($"Audio instance '{name}' not found");

                path = template.path;
                basevolume = template.BaseVolume;
                looping = template.looping;

                if (audioCache.TryGetValue(path, out var nativeBuffer))
                    cached = nativeBuffer;

                // Collect all buses that the original belongs to
                busesToJoin = buses.Values
                    .Where(bus => bus.instances.Contains(template))
                    .ToList();
            }

            AudioInstance clone;
            try
            {
                clone = new AudioInstance(name, path, cached, (int)(basevolume * 100), looping);
                clone.BaseVolume = basevolume;
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

                // Add clone to the same buses as the original
                if (busesToJoin != null && busesToJoin.Count > 0)
                {
                    foreach (var bus in busesToJoin)
                    {
                        if (!bus.instances.Contains(clone))
                            bus.instances.Add(clone);
                    }
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
        public void PrintVolumeInfo(string name)
        {
            lock (audiolock)
            {
                Console.WriteLine($"=== Volume Info for '{name}' ===");
                if (audiolist.TryGetValue(name, out var template))
                {
                    Console.WriteLine($"Template -> Base: {template.BaseVolume:F2} | Current: {template.isample?.Volume:F2}");
                }

                if (pools.TryGetValue(name, out var queue))
                {
                    Console.WriteLine($"Pool has {queue.Count} ready instances");
                }

                foreach (var bus in buses.Values)
                {
                    int count = bus.instances.Count(inst => inst?.name == name);
                    if (count > 0)
                        Console.WriteLine($"Bus '{bus.Name}' (vol={bus.Volume:F2}) affects {count} instance(s) of this sound");
                }
            }
        }
        public void Dispose()
        {
            lock (audiolock)
            {
                if (disposed)
                    return;

                disposed = true;
                foreach (var bus in buses.Values)
                {
                    bus.instances.Clear();
                }
                buses.Clear();
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
                foreach (var queue in pools.Values)
                {
                    while (queue.Count > 0)
                    {
                        queue.Dequeue()?.Dispose();
                    }
                }
                pools.Clear();
                poolsizes.Clear();
                mixer?.Dispose();
            }
        }
    }
}
