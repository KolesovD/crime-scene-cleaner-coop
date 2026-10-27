using System;
using System.Runtime.InteropServices;

namespace CscCoop.Net;

// Steamworks flat C API from the game's own steam_api64.dll, already loaded and initialised by the
// game under app 1040200. Every export used here was checked to exist in that dll (ISteamFriends is
// v018 in it, not v017). No callbacks and no call results: the game dispatches Steam manually and
// frees results it doesn't know (PR #176), so everything here is a plain synchronous read.
static class SteamApi
{
    const string Dll = "steam_api64";
    public const uint AppId = 1040200;

    [DllImport(Dll)] public static extern IntPtr SteamAPI_SteamUser_v023();
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);

    [DllImport(Dll)] public static extern IntPtr SteamAPI_SteamFriends_v018();
    [DllImport(Dll)] public static extern int SteamAPI_ISteamFriends_GetFriendCount(IntPtr self, int flags);
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamFriends_GetFriendByIndex(IntPtr self, int index, int flags);
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SteamAPI_ISteamFriends_GetFriendGamePlayed(IntPtr self, ulong friend, IntPtr info);
    [DllImport(Dll)] public static extern IntPtr SteamAPI_ISteamFriends_GetFriendPersonaName(IntPtr self, ulong friend);
    [DllImport(Dll)] public static extern IntPtr SteamAPI_ISteamFriends_GetPersonaName(IntPtr self);
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SteamAPI_ISteamFriends_SetRichPresence(IntPtr self, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Dll)] public static extern IntPtr SteamAPI_ISteamFriends_GetFriendRichPresence(IntPtr self, ulong friend, [MarshalAs(UnmanagedType.LPUTF8Str)] string key);
    [DllImport(Dll)] public static extern void SteamAPI_ISteamFriends_RequestFriendRichPresence(IntPtr self, ulong friend);

    [DllImport(Dll)] public static extern IntPtr SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(IntPtr self, int config, int value);
    public const int ConfigSendBufferSize = 9;

    [DllImport(Dll)] public static extern IntPtr SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
    [DllImport(Dll)] public static extern int SteamAPI_ISteamNetworkingMessages_SendMessageToUser(IntPtr self, IntPtr identity, IntPtr data, uint size, int flags, int channel);
    [DllImport(Dll)] public static extern int SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(IntPtr self, int channel, [Out] IntPtr[] messages, int max);
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(IntPtr self, IntPtr identity);
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(IntPtr self, IntPtr identity);
    [DllImport(Dll)] public static extern void SteamAPI_SteamNetworkingIdentity_SetSteamID64(IntPtr identity, ulong id);
    [DllImport(Dll)] public static extern ulong SteamAPI_SteamNetworkingIdentity_GetSteamID64(IntPtr identity);
    [DllImport(Dll)] public static extern void SteamAPI_SteamNetworkingMessage_t_Release(IntPtr message);
    // Returns ESteamNetworkingConnectionState: 0 none, 1 connecting, 2 finding route, 3 connected,
    // 4 closed by peer, 5 problem detected locally. Both info pointers may be null.
    [DllImport(Dll)] public static extern int SteamAPI_ISteamNetworkingMessages_GetSessionConnectionInfo(IntPtr self, IntPtr identity, IntPtr info, IntPtr status);
    [DllImport(Dll)] public static extern void SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(IntPtr self);

    public const int FriendFlagImmediate = 4;
    public const int SendUnreliable = 0, SendNoNagle = 1, SendReliable = 8;

    public static string Utf8(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p);
}
