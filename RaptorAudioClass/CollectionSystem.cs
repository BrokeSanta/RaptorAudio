using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RaptorAudio
{
    internal class Collectionaire
    {
        public string Name { get; }
        public float Volume { get; internal set; } = 1f;
        internal List<AudioInstance> instances { get; } = new();

        internal Collectionaire(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Collectionaire name cannot be null or whitespace.", nameof(name));
            Name = name;
        }
    }
}
