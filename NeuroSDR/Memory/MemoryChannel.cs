using NeuroSDR.Core;

namespace NeuroSDR.Memory;

public sealed record MemoryChannel(Guid Id, string Name, long Frequency, RadioMode Mode, int Bandwidth)
{
    public static MemoryChannel Create(string name, long frequency, RadioMode mode, int bandwidth) =>
        new(Guid.NewGuid(), name, frequency, mode, bandwidth);
}
