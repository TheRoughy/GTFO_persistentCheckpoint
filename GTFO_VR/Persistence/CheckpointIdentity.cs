using System.Security.Cryptography;
using System.Text;

namespace GTFO.PersistentCheckpoint.Persistence;

internal readonly record struct CheckpointIdentity(string ExpeditionKey, string RundownName, string ExpeditionName)
{
    internal bool IsValid => !string.IsNullOrWhiteSpace(ExpeditionKey)
        && !string.IsNullOrWhiteSpace(RundownName) && !string.IsNullOrWhiteSpace(ExpeditionName);

    internal string GetFilename()
    {
        if (!IsValid)
            throw new InvalidDataException("Checkpoint mission names are unavailable.");

        // Length-prefix each component so delimiters in modded names cannot
        // make two different identities encode as the same string.
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("GTFO checkpoint identity v1");
            writer.Write(ExpeditionKey);
            writer.Write(RundownName);
            writer.Write(ExpeditionName);
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())) + ".pcp";
    }
}
