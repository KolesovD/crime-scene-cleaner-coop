using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static CscCoop.Net.SteamApi;

namespace CscCoop.Net;

// Finding each other without lobbies (lobby calls need call results, see SteamApi) and without typing
// SteamIDs: everything goes through Steam rich presence, which friends in the same game can read.
//   host:   csccoop = "host"
//   client: csccoop = "join", csccoop_host = <host's SteamID>
// The host accepts a networking session only from friends whose rich presence asks to join it.
public sealed class SteamDirectory
{
    public struct Friend { public ulong Id; public string Name; public bool Hosting; }

    readonly IntPtr friends = SteamAPI_SteamFriends_v018();
    readonly IntPtr gameInfo = Marshal.AllocHGlobal(64);   // FriendGameInfo_t, 24 bytes on x64
    public readonly ulong Me = SteamAPI_ISteamUser_GetSteamID(SteamAPI_SteamUser_v023());
    public string MyName => Utf8(SteamAPI_ISteamFriends_GetPersonaName(friends));

    public void Announce(string role, ulong host = 0)
    {
        SteamAPI_ISteamFriends_SetRichPresence(friends, "csccoop", role);
        SteamAPI_ISteamFriends_SetRichPresence(friends, "csccoop_host", host == 0 ? "" : host.ToString());
        SteamAPI_ISteamFriends_SetRichPresence(friends, "status", role == "host" ? "Co-op host" : role == "join" ? "Co-op" : "");
    }

    // Friends who are in Crime Scene Cleaner right now.
    public List<Friend> FriendsInGame()
    {
        var list = new List<Friend>();
        int n = SteamAPI_ISteamFriends_GetFriendCount(friends, FriendFlagImmediate);
        for (int i = 0; i < n; i++)
        {
            ulong id = SteamAPI_ISteamFriends_GetFriendByIndex(friends, i, FriendFlagImmediate);
            if (!SteamAPI_ISteamFriends_GetFriendGamePlayed(friends, id, gameInfo)) continue;
            ulong gameId = (ulong)Marshal.ReadInt64(gameInfo, 0);
            if ((gameId & 0xFFFFFF) != AppId) continue;
            list.Add(new Friend { Id = id, Name = Utf8(SteamAPI_ISteamFriends_GetFriendPersonaName(friends, id)) });
        }
        return list;
    }

    public string Presence(ulong friend, string key) => Utf8(SteamAPI_ISteamFriends_GetFriendRichPresence(friends, friend, key));
    public void Refresh(ulong friend) => SteamAPI_ISteamFriends_RequestFriendRichPresence(friends, friend);

    public List<Friend> Hosts()
    {
        var hosts = new List<Friend>();
        foreach (var f in Everyone()) if (f.Hosting) hosts.Add(f);
        return hosts;
    }

    // Every friend in this game, hosting ones first (the co-op screen's list).
    public List<Friend> Everyone()
    {
        var all = FriendsInGame();
        for (int i = 0; i < all.Count; i++) { var f = all[i]; Refresh(f.Id); f.Hosting = Presence(f.Id, "csccoop") == "host"; all[i] = f; }
        all.Sort((a, b) => a.Hosting != b.Hosting ? (a.Hosting ? -1 : 1) : string.CompareOrdinal(a.Name, b.Name));
        return all;
    }

    public List<Friend> Joiners()
    {
        var j = new List<Friend>();
        foreach (var f in FriendsInGame())
        {
            Refresh(f.Id);
            if (Presence(f.Id, "csccoop") == "join" && Presence(f.Id, "csccoop_host") == Me.ToString()) j.Add(f);
        }
        return j;
    }
}
