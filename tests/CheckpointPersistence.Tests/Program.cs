using System.Security.Cryptography;
using System.Text;
using GTFO.PersistentCheckpoint.Persistence;

// Run the actual identity and disk-store code without Unity/IL2CPP. An optional
// checkpoint fixture is only read, then copied into test-owned scratch space.
var scratch = Path.Combine(AppContext.BaseDirectory, "checkpoint-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    BepInEx.Paths.ConfigPath = scratch;
    CheckpointStore.Initialize();
    var directory = Path.Combine(scratch, "GTFO_PersistentCheckpoint", "checkpoints");
    var sample = new CheckpointSnapshot
    {
        BuildRevision = 123, ExpeditionKey = "Local_40_TierA_0", RundownKey = "Local_40",
        RundownName = "text:Test Rundown", ExpeditionName = "Test Expedition",
        Tier = 1, ExpeditionIndex = 0, HostSeed = 111, SessionSeed = 222,
        LevelChecksum = 333, ProgressionTime = 2777.6f, RecallCount = 4,
        SavedAtUtcTicks = 638000000000000000, RequiredSlotCapacity = 8,
        LobbyExpansionPresent = true, LobbyExpansionVersion = "test",
        Players = new()
        {
            new CapturedPlayer(1001, 0, false, true, 1f, 0f, 417f, 219.2f, 0f, 120f, 4f),
            new CapturedPlayer(1002, 7, false, true, 0.4f, 0.2f, 50f, 30f, 80f, 20f, 2f),
            new CapturedPlayer(1, 1, true)
        },
        Passes = new()
        {
            new() { new byte[] { 1, 2, 3, 4 } },
            new(),
            new() { new byte[] { 5, 6 }, new byte[] { 7, 8, 9 } }
        }
    };
    CheckpointStore.EnqueueSave(sample);
    CheckpointStore.Flush(TimeSpan.FromSeconds(10));
    var source = args.Length > 0 ? File.ReadAllBytes(args[0])
        : File.ReadAllBytes(Path.Combine(directory, sample.Identity.GetFilename()));
    var identity = ReadIdentity(source);
    var other = identity with { RundownName = identity.RundownName + " (different)" };
    // Fixed reference computed independently from the length-prefixed UTF-8
    // identity. Changing this filename would make existing saves undiscoverable.
    var referenceIdentity = new CheckpointIdentity("Local_40_TierA_0", "text:Test Rundown", "Test Expedition");
    Check(referenceIdentity.GetFilename() == "6FE66C35DE4DD7D462A91E59E9EE5BE9A5F547420EA060AFB7AF0067A0A3CADC.pcp",
        "identity filename matches the fixed reference hash");
    Check(identity.GetFilename() != (identity with { ExpeditionKey = identity.ExpeditionKey + "_different" }).GetFilename(),
        "expedition identifier participates in identity");
    Check(identity.GetFilename() != other.GetFilename(), "same numeric ID with different rundown names is isolated");
    Check(identity.GetFilename() != (identity with { ExpeditionName = identity.ExpeditionName + " (different)" }).GetFilename(), "expedition name participates in identity");
    Check(new CheckpointIdentity(identity.ExpeditionKey, "a|b", "c").GetFilename()
        != new CheckpointIdentity(identity.ExpeditionKey, "a", "b|c").GetFilename(), "component boundaries are unambiguous");
    Throws<InvalidDataException>(() => new CheckpointIdentity("", "Rundown", "Expedition").GetFilename(), "missing expedition identifier is rejected");
    Throws<InvalidDataException>(() => new CheckpointIdentity("ExpeditionKey", "", "Expedition").GetFilename(), "missing rundown name is rejected");
    Throws<InvalidDataException>(() => new CheckpointIdentity("ExpeditionKey", "Rundown", "").GetFilename(), "missing expedition name is rejected");

    var namedPath = Path.Combine(directory, identity.GetFilename());
    File.WriteAllBytes(namedPath, source);
    var result = CheckpointStore.TryLoad(identity).Snapshot ?? throw new Exception("Named fixture could not be loaded");
    Check(result.Identity == identity, "named checkpoint identity loads correctly");
    CheckpointStore.EnqueueSave(result);
    CheckpointStore.Flush(TimeSpan.FromSeconds(10));
    Check(File.ReadAllBytes(namedPath).SequenceEqual(source),
        "complete format-3 file round-trips byte-for-byte, including all metadata, player values and packets");

    var otherPath = Path.Combine(directory, other.GetFilename());
    File.Copy(namedPath, otherPath);
    Check(CheckpointStore.TryLoad(other).Error == "checkpoint mission names do not match the selected mission",
        "renamed file with mismatching mission names is rejected");
    CheckpointStore.EnqueueSave(result with { RundownName = other.RundownName });
    CheckpointStore.Flush(TimeSpan.FromSeconds(10));
    Check(CheckpointStore.TryLoad(other).Success, "different rundowns can coexist under the same numeric identifier");

    var unrelatedPath = Path.Combine(directory, "unrelated.pcp");
    File.WriteAllBytes(unrelatedPath, source);
    var backupDirectory = Path.Combine(directory, "backups");
    Directory.CreateDirectory(backupDirectory);
    var backupPath = Path.Combine(backupDirectory, identity.GetFilename());
    File.WriteAllBytes(backupPath, source);
    CheckpointStore.EnqueueDelete(identity);
    CheckpointStore.Flush(TimeSpan.FromSeconds(10));
    Check(!File.Exists(namedPath) && CheckpointStore.TryLoad(other).Success
        && File.ReadAllBytes(backupPath).SequenceEqual(source) && File.ReadAllBytes(unrelatedPath).SequenceEqual(source),
        "deletion is scoped to the named mission and leaves other rundowns and backups untouched");
    Check(!CheckpointStore.TryLoad(identity).Success && CheckpointStore.TryLoad(identity).Error == null,
        "missing mission save does not load unrelated files or backup subdirectories");

    foreach (var version in new[] { 1, 2, 4 })
    {
        var unsupported = (byte[])source.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(unsupported.AsSpan(8), version);
        SHA256.HashData(unsupported.AsSpan(0, unsupported.Length - 32)).CopyTo(unsupported, unsupported.Length - 32);
        File.WriteAllBytes(namedPath, unsupported);
        Check(CheckpointStore.TryLoad(identity).Error == "checkpoint format is unsupported", $"format {version} rejected even at the named path");
    }
    var corrupted = File.ReadAllBytes(otherPath);
    corrupted[16] ^= 1;
    File.WriteAllBytes(otherPath, corrupted);
    Check(CheckpointStore.TryLoad(other).Error == "checkpoint integrity check failed", "corruption is rejected");
    File.WriteAllBytes(otherPath, new byte[] { 1, 2, 3 });
    Check(CheckpointStore.TryLoad(other).Error == "checkpoint file has an invalid size", "truncated files are rejected");
    Console.WriteLine("All checkpoint identity/store regression checks passed.");
}
finally
{
    // This directory was created solely by this invocation under the test output.
    Directory.Delete(scratch, recursive: true);
}

static CheckpointIdentity ReadIdentity(byte[] source)
{
    using var stream = new MemoryStream(source);
    using var reader = new BinaryReader(stream);
    if (!reader.ReadBytes(8).SequenceEqual(Encoding.ASCII.GetBytes("GTFOCP01")) || reader.ReadInt32() != 3)
        throw new Exception("Test fixture must be a named format-3 checkpoint");
    reader.ReadInt32(); // build revision
    string Text() => Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
    var key = Text(); Text(); // rundown key
    return new CheckpointIdentity(key, Text(), Text());
}
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAILED: " + message);
    Console.WriteLine("PASS: " + message);
}
static void Throws<T>(Action action, string message) where T : Exception
{
    try { action(); } catch (T) { Check(true, message); return; }
    throw new Exception("FAILED: " + message);
}

namespace BepInEx
{
    internal static class Paths { internal static string ConfigPath = ""; }
}
namespace GTFO.PersistentCheckpoint.Core
{
    internal static class Log
    {
        internal static void Info(string message) { }
        internal static void Warning(string message) => Console.WriteLine("WARNING: " + message);
        internal static void Error(Exception exception) => throw new Exception("Unexpected store error", exception);
    }
}
