using System.Security.Cryptography;
using System.Text;
using BepInEx;
using GTFO.PersistentCheckpoint.Core;

namespace GTFO.PersistentCheckpoint.Persistence;

internal static class CheckpointStore
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("GTFOCP01");
    private const int FormatVersion = 1;
    private const int HashLength = 32;
    private const int MaxStringBytes = 2048;
    private const int MaxPasses = 32;
    private const int MaxPacketsPerPass = 1_000_000;
    private const int MaxPacketBytes = 16 * 1024 * 1024;
    private const long MaxPayloadBytes = 512L * 1024 * 1024;

    private static readonly object QueueLock = new();
    private static Task _pending = Task.CompletedTask;
    private static string _directory = string.Empty;

    internal static void Initialize()
    {
        _directory = Path.Combine(Paths.ConfigPath, "GTFO_PersistentCheckpoint", "checkpoints");
        Directory.CreateDirectory(_directory);
    }

    internal static SnapshotLoadResult TryLoad(string expeditionKey)
    {
        try
        {
            var path = GetPath(expeditionKey);
            if (!File.Exists(path))
                return new SnapshotLoadResult(null, null);

            var bytes = File.ReadAllBytes(path);
            if (bytes.LongLength < Magic.Length + sizeof(int) + HashLength || bytes.LongLength > MaxPayloadBytes)
                return new SnapshotLoadResult(null, "checkpoint file has an invalid size");

            var payloadLength = bytes.Length - HashLength;
            var expectedHash = SHA256.HashData(bytes.AsSpan(0, payloadLength));
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, bytes.AsSpan(payloadLength, HashLength)))
                return new SnapshotLoadResult(null, "checkpoint integrity check failed");

            using var stream = new MemoryStream(bytes, 0, payloadLength, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
                return new SnapshotLoadResult(null, "checkpoint magic is invalid");
            if (reader.ReadInt32() != FormatVersion)
                return new SnapshotLoadResult(null, "checkpoint format is unsupported");

            var snapshot = ReadSnapshot(reader);
            if (stream.Position != stream.Length)
                return new SnapshotLoadResult(null, "checkpoint contains trailing payload data");
            if (!string.Equals(snapshot.ExpeditionKey, expeditionKey, StringComparison.Ordinal))
                return new SnapshotLoadResult(null, "checkpoint expedition identity does not match its file");

            return new SnapshotLoadResult(snapshot, null);
        }
        catch (Exception exception)
        {
            Log.Warning($"Failed to load checkpoint for {expeditionKey}: {exception.Message}");
            return new SnapshotLoadResult(null, exception.Message);
        }
    }

    internal static void EnqueueSave(CheckpointSnapshot snapshot)
    {
        Enqueue(() => WriteAtomic(snapshot));
    }

    internal static void EnqueueDelete(string expeditionKey)
    {
        Enqueue(() =>
        {
            var path = GetPath(expeditionKey);
            if (File.Exists(path))
                File.Delete(path);
        });
    }

    internal static void Flush(TimeSpan timeout)
    {
        Task pending;
        lock (QueueLock)
            pending = _pending;

        try
        {
            if (!pending.Wait(timeout))
                Log.Warning("Timed out while flushing checkpoint writes.");
        }
        catch (Exception exception)
        {
            Log.Error(exception);
        }
    }

    private static void Enqueue(Action operation)
    {
        lock (QueueLock)
        {
            _pending = _pending.ContinueWith(_ =>
            {
                try
                {
                    operation();
                }
                catch (Exception exception)
                {
                    Log.Error(exception);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private static void WriteAtomic(CheckpointSnapshot snapshot)
    {
        Directory.CreateDirectory(_directory);
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            WriteSnapshot(writer, snapshot);
        }

        if (payload.Length > MaxPayloadBytes)
            throw new InvalidDataException($"Checkpoint payload is too large ({payload.Length} bytes).");

        var payloadBytes = payload.ToArray();
        var hash = SHA256.HashData(payloadBytes);
        var path = GetPath(snapshot.ExpeditionKey);
        var temporaryPath = path + ".tmp";

        using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            output.Write(payloadBytes);
            output.Write(hash);
            output.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, path, overwrite: true);
        Log.Info($"Saved checkpoint for {snapshot.ExpeditionKey} ({payloadBytes.Length + hash.Length:N0} bytes).");
    }

    private static void WriteSnapshot(BinaryWriter writer, CheckpointSnapshot snapshot)
    {
        writer.Write(snapshot.BuildRevision);
        WriteString(writer, snapshot.ExpeditionKey);
        WriteString(writer, snapshot.RundownKey);
        writer.Write(snapshot.Tier);
        writer.Write(snapshot.ExpeditionIndex);
        writer.Write(snapshot.HostSeed);
        writer.Write(snapshot.SessionSeed);
        writer.Write(snapshot.LevelChecksum);
        writer.Write(snapshot.ProgressionTime);
        writer.Write(snapshot.RecallCount);
        writer.Write(snapshot.SavedAtUtcTicks);
        writer.Write(snapshot.RequiredSlotCapacity);
        writer.Write(snapshot.LobbyExpansionPresent);
        WriteString(writer, snapshot.LobbyExpansionVersion);

        writer.Write(snapshot.Players.Count);
        foreach (var player in snapshot.Players)
        {
            writer.Write(player.Lookup);
            writer.Write(player.CharacterIndex);
            writer.Write(player.IsBot);
        }

        writer.Write(snapshot.Passes.Count);
        foreach (var pass in snapshot.Passes)
        {
            writer.Write(pass.Count);
            foreach (var packet in pass)
            {
                if (packet.Length > MaxPacketBytes)
                    throw new InvalidDataException($"Capture packet is too large ({packet.Length} bytes).");
                writer.Write(packet.Length);
                writer.Write(packet);
            }
        }
    }

    private static CheckpointSnapshot ReadSnapshot(BinaryReader reader)
    {
        var buildRevision = reader.ReadInt32();
        var expeditionKey = ReadString(reader);
        var rundownKey = ReadString(reader);
        var tier = reader.ReadInt32();
        var expeditionIndex = reader.ReadInt32();
        var hostSeed = reader.ReadInt32();
        var sessionSeed = reader.ReadInt32();
        var levelChecksum = reader.ReadUInt64();
        var progressionTime = reader.ReadSingle();
        var recallCount = reader.ReadUInt64();
        var savedAtUtcTicks = reader.ReadInt64();
        var requiredSlotCapacity = reader.ReadInt32();
        var lobbyExpansionPresent = reader.ReadBoolean();
        var lobbyExpansionVersion = ReadString(reader);

        var playerCount = ReadBoundedCount(reader, 256, "player");
        var players = new List<CapturedPlayer>(playerCount);
        for (var i = 0; i < playerCount; i++)
            players.Add(new CapturedPlayer(reader.ReadUInt64(), reader.ReadInt32(), reader.ReadBoolean()));

        var passCount = ReadBoundedCount(reader, MaxPasses, "capture pass");
        var passes = new List<List<byte[]>>(passCount);
        long packetBytes = 0;
        for (var passIndex = 0; passIndex < passCount; passIndex++)
        {
            var packetCount = ReadBoundedCount(reader, MaxPacketsPerPass, "packet");
            var pass = new List<byte[]>(packetCount);
            for (var packetIndex = 0; packetIndex < packetCount; packetIndex++)
            {
                var length = ReadBoundedCount(reader, MaxPacketBytes, "packet byte");
                packetBytes += length;
                if (packetBytes > MaxPayloadBytes)
                    throw new InvalidDataException("Checkpoint packet data exceeds the allowed size.");
                var packet = reader.ReadBytes(length);
                if (packet.Length != length)
                    throw new EndOfStreamException("Checkpoint packet was truncated.");
                pass.Add(packet);
            }
            passes.Add(pass);
        }

        return new CheckpointSnapshot
        {
            BuildRevision = buildRevision,
            ExpeditionKey = expeditionKey,
            RundownKey = rundownKey,
            Tier = tier,
            ExpeditionIndex = expeditionIndex,
            HostSeed = hostSeed,
            SessionSeed = sessionSeed,
            LevelChecksum = levelChecksum,
            ProgressionTime = progressionTime,
            RecallCount = recallCount,
            SavedAtUtcTicks = savedAtUtcTicks,
            RequiredSlotCapacity = Math.Max(4, requiredSlotCapacity),
            LobbyExpansionPresent = lobbyExpansionPresent,
            LobbyExpansionVersion = lobbyExpansionVersion,
            Players = players,
            Passes = passes
        };
    }

    private static int ReadBoundedCount(BinaryReader reader, int maximum, string name)
    {
        var value = reader.ReadInt32();
        if (value < 0 || value > maximum)
            throw new InvalidDataException($"Invalid {name} count: {value}.");
        return value;
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if (bytes.Length > MaxStringBytes)
            throw new InvalidDataException("Checkpoint string is too long.");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = ReadBoundedCount(reader, MaxStringBytes, "string byte");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException("Checkpoint string was truncated.");
        return Encoding.UTF8.GetString(bytes);
    }

    private static string GetPath(string expeditionKey)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(expeditionKey));
        return Path.Combine(_directory, Convert.ToHexString(digest) + ".pcp");
    }
}
