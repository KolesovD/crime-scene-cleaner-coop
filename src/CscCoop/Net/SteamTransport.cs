using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CoopCore;
using static CscCoop.Net.SteamApi;

namespace CscCoop.Net;

// ISteamNetworkingMessages by SteamID: Valve's relays, no IPs, no port forwarding.
// Session acceptance without callbacks: sending to a user implicitly accepts their session; the host
// calls AcceptSessionWithUser for friends whose rich presence asks to join it (SteamDirectory).
public sealed class SteamTransport : ITransport
{
    const int Channel = 0;
    // x64 SteamNetworkingMessage_t: data pointer at 0, size at 8, sender identity at 16
    // (playbook/networking-steam-spacewar.md; 32-bit builds differ).
    const int MsgSizeOffset = 8, MsgIdentityOffset = 16;
    const int IdentityBytes = 160;    // the struct is 136 bytes; treat it as opaque

    readonly IntPtr nm;
    readonly IntPtr identity = Marshal.AllocHGlobal(IdentityBytes);
    readonly IntPtr[] msgs = new IntPtr[64];
    IntPtr sendBuf = Marshal.AllocHGlobal(2048);
    int sendBufSize = 2048;

    public ulong LocalId { get; }
    public int SendErrors { get; private set; }

    public SteamTransport()
    {
        nm = SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
        if (nm == IntPtr.Zero) throw new InvalidOperationException("SteamNetworkingMessages unavailable (is Steam running?)");
        LocalId = SteamAPI_ISteamUser_GetSteamID(SteamAPI_SteamUser_v023());
        // The join snapshot of a big job is reliable and sent at once; the default 512 KB send buffer is tight.
        var utils = SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
        SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(utils, ConfigSendBufferSize, 32 << 20);
        SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(utils);   // start finding relays now, not on the first message
    }

    IntPtr Identity(ulong steamId)
    {
        for (int i = 0; i < IdentityBytes; i++) Marshal.WriteByte(identity, i, 0);
        SteamAPI_SteamNetworkingIdentity_SetSteamID64(identity, steamId);
        return identity;
    }

    // True only while a session request from that user is pending; the host retries until it hears them.
    public bool Accept(ulong steamId) => SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(nm, Identity(steamId));

    // A session in state 4/5 stays dead until closed (friend's test: both sides sat in "problem detected
    // locally" until they restarted host and join). Closing it lets the next send open a fresh one.
    public void Reset(ulong steamId) => SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(nm, Identity(steamId));
    public static bool Dead(int state) => state == 4 || state == 5;

    public int State(ulong steamId) => SteamAPI_ISteamNetworkingMessages_GetSessionConnectionInfo(nm, Identity(steamId), IntPtr.Zero, IntPtr.Zero);
    public static string StateName(int s) => s switch
    {
        0 => "none", 1 => "connecting", 2 => "finding route", 3 => "connected",
        4 => "closed by peer", 5 => "problem detected locally", _ => s.ToString(),
    };

    public void Send(ulong to, byte[] data, int length, bool reliable)
    {
        if (length > sendBufSize) { Marshal.FreeHGlobal(sendBuf); sendBuf = Marshal.AllocHGlobal(length); sendBufSize = length; }
        Marshal.Copy(data, 0, sendBuf, length);
        int flags = (reliable ? SendReliable : SendUnreliable) | SendNoNagle;
        int result = SteamAPI_ISteamNetworkingMessages_SendMessageToUser(nm, Identity(to), sendBuf, (uint)length, flags, Channel);
        if (result != 1) SendErrors++;   // EResult 1 = OK
    }

    public void Poll(List<Packet> into)
    {
        while (true)
        {
            int n = SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(nm, Channel, msgs, msgs.Length);
            if (n <= 0) return;
            for (int i = 0; i < n; i++)
            {
                var m = msgs[i];
                IntPtr data = Marshal.ReadIntPtr(m, 0);
                int size = Marshal.ReadInt32(m, MsgSizeOffset);
                ulong from = SteamAPI_SteamNetworkingIdentity_GetSteamID64(m + MsgIdentityOffset);
                var bytes = new byte[size];
                Marshal.Copy(data, bytes, 0, size);
                SteamAPI_SteamNetworkingMessage_t_Release(m);
                into.Add(new Packet { From = from, Data = bytes });
            }
            if (n < msgs.Length) return;
        }
    }

    public void Dispose()
    {
        Marshal.FreeHGlobal(identity);
        Marshal.FreeHGlobal(sendBuf);
    }
}
