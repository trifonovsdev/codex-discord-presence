using System.Runtime.InteropServices;
using System.Text;

namespace CodexPresence;

/// <summary>Reviewed subset of the Discord Social SDK C ABI used by Rich Presence.</summary>
internal static class DiscordSocialNative
{
    private const string LibraryName = "discord_partner_sdk";

    [StructLayout(LayoutKind.Sequential)]
    internal struct ObjectHandle
    {
        public nint Opaque;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DiscordString
    {
        public nint Pointer;
        public nuint Size;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void UpdateRichPresenceCallback(nint result, nint userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void FreeCallback(nint userData);

    internal sealed class PinnedUtf8 : IDisposable
    {
        private readonly byte[] bytes;
        private GCHandle pin;

        public PinnedUtf8(string value)
        {
            bytes = Encoding.UTF8.GetBytes(value);
            pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            Value = new DiscordString
            {
                Pointer = pin.AddrOfPinnedObject(),
                Size = (nuint)bytes.Length,
            };
        }

        public DiscordString Value { get; }

        public void Dispose()
        {
            if (pin.IsAllocated) pin.Free();
        }
    }

    internal static string ReadAndFree(DiscordString value)
    {
        if (value.Pointer == 0) return string.Empty;
        try
        {
            if (value.Size == 0) return string.Empty;
            var size = checked((int)value.Size);
            var bytes = new byte[size];
            Marshal.Copy(value.Pointer, bytes, 0, size);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            Discord_Free(value.Pointer);
        }
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_RunCallbacks();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_Free(nint pointer);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_Client_Init(ref ObjectHandle client);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_Client_Drop(ref ObjectHandle client);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_Client_SetApplicationId(ref ObjectHandle client, ulong applicationId);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_Client_ClearRichPresence(ref ObjectHandle client);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_Client_UpdateRichPresence(
        ref ObjectHandle client,
        ref ObjectHandle activity,
        UpdateRichPresenceCallback callback,
        FreeCallback callbackUserDataFree,
        nint callbackUserData);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int Discord_Client_GetVersionMajor();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int Discord_Client_GetVersionMinor();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int Discord_Client_GetVersionPatch();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_Activity_Init(ref ObjectHandle activity);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_Activity_Drop(ref ObjectHandle activity);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_Activity_SetName(ref ObjectHandle activity, DiscordString value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_Activity_SetType(ref ObjectHandle activity, int value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_Activity_SetState(ref ObjectHandle activity, ref DiscordString value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_Activity_SetDetails(ref ObjectHandle activity, ref DiscordString value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_Activity_SetAssets(ref ObjectHandle activity, ref ObjectHandle assets);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_Activity_SetTimestamps(ref ObjectHandle activity, ref ObjectHandle timestamps);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_ActivityAssets_Init(ref ObjectHandle assets);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_ActivityAssets_Drop(ref ObjectHandle assets);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_ActivityAssets_SetLargeImage(ref ObjectHandle assets, ref DiscordString value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_ActivityAssets_SetLargeText(ref ObjectHandle assets, ref DiscordString value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_ActivityTimestamps_Init(ref ObjectHandle timestamps);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_ActivityTimestamps_Drop(ref ObjectHandle timestamps);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_ActivityTimestamps_SetStart(ref ObjectHandle timestamps, ulong value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_ClientResult_Drop(ref ObjectHandle result);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int Discord_ClientResult_Type(ref ObjectHandle result);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int Discord_ClientResult_ErrorCode(ref ObjectHandle result);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern float Discord_ClientResult_RetryAfter(ref ObjectHandle result);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_ClientResult_Error(ref ObjectHandle result, out DiscordString value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Discord_ClientResult_ToString(ref ObjectHandle result, out DiscordString value);
}
