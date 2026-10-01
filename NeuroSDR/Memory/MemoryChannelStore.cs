using System.Text.Json;
using System.Text.Json.Serialization;
using NeuroSDR.Core;

namespace NeuroSDR.Memory;

internal sealed class MemoryChannelStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly object _sync = new();
    private readonly string _path;
    private List<MemoryChannel> _channels = [];

    public MemoryChannelStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeuroSDR", "channels.json");
        Load();
    }

    public string? LastError { get; private set; }

    public IReadOnlyList<MemoryChannel> Snapshot()
    {
        lock (_sync) return _channels.ToArray();
    }

    public MemoryChannel Add(string name, long frequency, Core.RadioMode mode, int bandwidth)
    {
        var channel = MemoryChannel.Create(name, frequency, mode, bandwidth);
        lock (_sync)
        {
            _channels.Add(channel);
            SaveCore();
        }
        return channel;
    }

    public void Update(MemoryChannel channel)
    {
        lock (_sync)
        {
            var index = _channels.FindIndex(item => item.Id == channel.Id);
            if (index < 0) return;
            _channels[index] = channel;
            SaveCore();
        }
    }

    public void Remove(Guid id)
    {
        lock (_sync)
        {
            _channels.RemoveAll(channel => channel.Id == id);
            SaveCore();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var channels = JsonSerializer.Deserialize<List<MemoryChannel>>(File.ReadAllText(_path), JsonOptions);
            _channels = channels?.Where(channel => channel.Frequency is >= RadioLimits.MinimumFrequency and <= RadioLimits.MaximumFrequency && channel.Bandwidth > 0).ToList() ?? [];
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            _channels = [];
        }
    }

    private void SaveCore()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_channels, JsonOptions));
            File.Move(temporaryPath, _path, true);
            LastError = null;
        }
        catch (Exception exception) { LastError = exception.Message; }
    }
}
