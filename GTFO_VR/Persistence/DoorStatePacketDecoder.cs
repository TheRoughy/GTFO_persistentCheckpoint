using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using LevelGeneration;

namespace GTFO.PersistentCheckpoint.Persistence;

internal static class DoorStatePacketDecoder
{
    internal static unsafe pDoorState Decode(byte[] packet, int payloadSize, Il2CppSystem.Type marshallingType)
    {
        if (payloadSize <= 0 || packet.Length < 3 || packet.Length - 3 != payloadSize)
            throw new InvalidDataException("Saved door packet does not match the current door marshaller's size.");

        // Wire bools occupy four bytes; the interop value type has one-byte bools.
        // Follow the native MarshalToData path instead of reading wire bytes as
        // the in-memory struct. Avoid its generated ref-T wrapper, which tries
        // to construct pDoorState through a nonexistent IntPtr constructor.
        var buffer = Marshal.AllocHGlobal(payloadSize);
        try
        {
            Marshal.Copy(packet, 3, buffer, payloadSize);
            var boxed = Il2CppSystem.Runtime.InteropServices.Marshal.PtrToStructure(buffer, marshallingType);
            if (boxed == null)
                throw new InvalidDataException("The game's door marshaller returned no state.");

            var unboxed = IL2CPP.il2cpp_object_unbox(boxed.Pointer);
            if (unboxed == IntPtr.Zero)
                throw new InvalidDataException("The game's door state could not be unboxed.");

            var state = Unsafe.Read<pDoorState>((void*)unboxed);
            GC.KeepAlive(boxed);
            return state;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
