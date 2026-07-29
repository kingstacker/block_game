using System.Text.Json;
using BlockGame.Core.Models;

namespace BlockGame.Core.Services;

public sealed class HeartbeatStore
{
    private readonly DataPaths _paths;
    private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Create();

    public HeartbeatStore(DataPaths paths)
    {
        _paths = paths;
    }

    public void Write(GuardHeartbeat heartbeat)
    {
        _paths.EnsureDirectory();
        string temporaryFile = _paths.HeartbeatFile + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(heartbeat, _jsonOptions));
        File.Move(temporaryFile, _paths.HeartbeatFile, overwrite: true);
    }

    public GuardHeartbeat? Read()
    {
        if (!File.Exists(_paths.HeartbeatFile))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GuardHeartbeat>(
                File.ReadAllText(_paths.HeartbeatFile),
                _jsonOptions);
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
