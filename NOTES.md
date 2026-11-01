# Crime Scene Cleaner co-op: notes

Following the playbook in `playbook-repo/` (cranebeaker/coop-anything, AGENTS.md), cloned 2026-10-25.

## Observed (this machine, 2026-10-25)

- Install: the owner's Steam install (read only, never modified).
- Steam app 1040200, buildid 26046462.
- `CrimeCleaner.exe` SHA-256 `e9167a329f7c8b1c9eef4d0a8dee6d6b08acdee0ce265f1e78ca474f4d560173`
- `GameAssembly.dll` SHA-256 `8af483d77daca17387f4875638f180072195e0f0c8541e9f1e7e8ae999fa3847`
- The owner's install has MelonLoader (`version.dll` proxy) and 3 mods in `Mods/`
  (CleanOVision, FunctionalPistol, UltraCleaningTools). Left out of the test copy so BepInEx is the only loader.
- Prior work: draft PR cranebeaker/coop-anything#176 (branch `game/crime-scene-cleaner` on KolesovD's fork),
  a BepInEx 6 be.791 plugin "CSC Coop" with QA level `automated`. **Same buildid and same exe/GameAssembly hashes**
  as this machine, so its write-up applies. Its source is **not published**; only the write-up and notes are
  (cloned to `pr176/`). Reviewer requested changes on 2026-10-25, PR still draft.
- No entry for the game in `data/directory.yaml` on main. `gh` CLI is not logged in, issues/PRs read via the web.
- BepInEx-IL2CPP-be.791.zip reused from the Shadows of Doubt project, SHA-256
  `bb1c510909b69522733791f3a0817a487379a52da6468a492f1c9c19b3ec6f9a` (same value PR #176 reports).
- Tools present: .NET SDK 8.0.426, Python 3.12.10; cpp2il and ilspycmd in the SoD project's `local/tools`.

## Workspace

- `local/game-copy`: robocopy of the install (without MelonLoader, Mods, Plugins, UserData, UserLibs, version.dll).
  Saves live inside the install (`CrimeCleaner_Data/Saves`), so the copy has its own saves.

## Decision (owner, 2026-10-25)

Build from zero, using PR #176's write-up as the map (its source is unpublished). Same loader (BepInEx be.791),
same plan: players and items first, then stains, bodies, doors, quests, tools, leaving.

## Sync core, game-free (ran 2026-10-25)

- `src/CoopCore`: wire format, UDP and in-memory transports, `Session` (host = player 0, up to 4 players).
  Poses at 20 Hz with sequence numbers, interpolated 100 ms in the past.
  Items: owner per item decided by the host, epoch raised on every change, states accepted only from the owner
  at the current epoch, grab = claim (optimistic on the client), released items yield back to the host once still.
  Scene key (scene + item table hash) gates item sync, so players in different scenes don't touch each other's items.
- `src/CoopCore.Tests` (`dotnet run -c Release`): 1 host + 3 clients, 20% and 35% loss of unreliable packets,
  jitter. RESULT: PASS (61 checks): join, poses < 0.5 m, carry/throw converge < 1 cm, simultaneous grab
  (one winner), host can't steal a held item, replayed old owner message ignored, leaver's item returns,
  late joiner gets all 50 items, other scene not synced.
  Mutants, each must fail: `any-epoch` FAIL (2), `no-authority` FAIL (10), `no-drop` FAIL (6), `grab-always` FAIL (16).
- Bugs found by the test: (1) two clients grabbing in the same frame both got the item granted and both dropped
  it; fixed by counting a fresh grant as held. (2) The late-join snapshot arrived before Welcome and was
  discarded; fixed by accepting states once the scene key matches.

## In the game (observed 2026-10-25)

- First copy missed `CrimeCleaner_Data/Plugins` (robocopy `/XD Plugins` also matched that folder): no steam_api64 or
  AkSoundEngine, the game quit at start. Copied it; now complete.
- BepInEx be.791 loads: 136 interop assemblies, plugin `src/CscCoop` loads.
- Bot: `SaveSystem.Instance.StartGame(1)` from the menu goes LoadingScreenScene -> HubResidence-GameIntro ->
  "Dealer's Apartment" (this save). Table there: 592 GrabableItemControllers (incl. inactive), key 49D270DE, same on both copies.
- `GrabController.maxDistance` 2.5 m. Grabbing in the same frame as a teleport flings the item (hundreds of metres):
  the bot now steps up to the item and grabs a second later.
- Second copy `local/game-copy-2`: 2410 hard links + 273 real copies (BepInEx, Saves, PlayerPrefs, KeyBindings, doorstop ini).
- Live suite `tools/run-pair.ps1 -Test items` (host + client, localhost UDP), checked by `tools/item_report.py`:
  client walks up to a box, grabs it, backs away 1.5 m, throws it; host tries to take it from the client's hands;
  host carries and drops another box; tables compared.
  - clean: RESULT: PASS (8 checks): carried item 0.15 m apart, moved 1.80 m; host's carried item 0.07 m apart,
    moved 1.56 m; 592 of 592 items within 5 cm (max 0.009 m), owner/epoch agree; steal refused.
  - 20% loss + 80 ms, two runs: RESULT: PASS (8) both, max final error 0.009 m.
  - mutant `grab-always` (grab check off): RESULT: FAIL (steal allowed), as it must.
- Bugs found by the suite: (1) the first `hostcarry` check passed with an item that never moved (grabbed from 4.17 m,
  beyond maxDistance); added "the carried item moved > 0.3 m" checks. (2) UdpTransport's latency emulation sent
  delayed packets newest first, so Welcome overtook the owner table and the client ignored part of it (4 items
  diverged up to 1.7 m); now oldest first.
- Intro skip (`CSCCOOP_SKIP_INTRO=1`, `src/CscCoop/IntroSkip.cs`): job intro is the `PlayableDirectorOverride`
  "TruckExitCTSPhone" (15.8 s). `ExitCutscene()` fast-forwards it: player can move 8-10 s after the job loads
  (about 30 s without). Items suite with skip: PASS (8) twice. Jumping `playableDirector.time` to the end was faster
  (0-6 s) but the client then lost its held item a few seconds later (suite FAIL), so not used.

## Steam path (2026-10-25)

- The game's steam_api64.dll exports ISteamFriends **v018** (not v017), SteamUser v023, NetworkingMessages v002,
  NetworkingUtils v004, Get/SetRichPresence, GetFriendGamePlayed (checked in the export table).
- No lobbies (they need call results, which the game's manual dispatcher frees). Friends find each other by
  rich presence: host `csccoop=host`; joiner `csccoop=join`, `csccoop_host=<host id>`. The host accepts the
  networking session of friends asking to join it (`src/CscCoop/Net/SteamDirectory.cs`). F8 panel in Russian.
- One-account smoke test (`CSCCOOP_TEST=steam`, ran twice): own SteamID and name read; a reliable message to
  myself came back with me as sender; own presence read back as `host`, empty after leaving; host session
  started; screenshot shows the panel. NOT tested: a second account (join, session accept, delivery between
  two machines), whether two different saves give the same item table for the same job.
- Items suite after these changes (intro skip now on by default): RESULT: PASS (8).
- Package: `python -I tools/make-package.py 0.1.0` -> dist/CscCoop-0.1.0.zip (BepInEx be.791 + plugin + README-RU,
  no game files). Owner plays from `local/game-copy` via `Играть кооп.cmd` (the Steam install has MelonLoader
  and stays untouched). Copy saves restored from local/copy_saves_backup; a Slot2 the tests made moved to local/test_leftovers.

## Friend's first launch (log from the friend, 2026-10-25)

- BepInEx failed before the plugin: `Failed to generate Il2Cpp interop assemblies: InvalidDataException: End of
  Central Directory record could not be found` while extracting `BepInEx\unity-libs\2021.3.45.zip` (downloaded from
  unity.bepinex.dev on first launch, came out truncated), then `Could not load UnityEngine.CoreModule`, no plugins.
  BepInEx reuses an existing file of that name, so retrying doesn't help.
- Fix: package 0.1.1 ships the good unity-libs zip (SHA-256 3cfde161...7da9e5, 66 entries); README tells to
  unpack again with overwrite. Not yet confirmed on the friend's PC.

## First two-account attempt (owner + friend, both logs, 2026-10-26)

- Both loaded the plugin (0.1.0) and the same job: Dealer's Apartment, 592 items, key 49D270DE on both
  (so two different saves gave the same table for this job).
- Host logged "accepted session from friend", then rx 0 for minutes while the client's tx rose (Hello every
  second): nothing from the client reached the host. Both also switched host/client roles a few times.
- Cause (derived, not proven): the host called AcceptSessionWithUser once and ignored its result (it only works
  while a request is pending) and never sent the friend anything (it only sends to peers after their Hello),
  so the session was never accepted from the host side.
- Fix in 0.1.2: while an invited friend hasn't said Hello, the host calls Accept and sends a reliable Ping every
  2 s (sending to a user accepts their session); InitRelayNetworkAccess at start; logs accept result, Steam
  session state and send errors (host per invite every 10 s, client in its status line).
  Ran: core tests PASS (61), Steam smoke on one account (state call works), items suite PASS (8).
  NOT tested between two accounts yet.
- The owner installed the package into the Steam install (MelonLoader removed there); that install now runs
  the mod too.

## Second two-account test (owner + friend, v0.1.2, 2026-10-26, their logs)

- Worked: friend joined as player 1, ~15 min session, items passed back and forth, grabs of held items refused.
- Connecting took a second try: both Steam sessions sat in state 5 "problem detected locally" until both
  restarted host/join. 0.1.3: a dead session (state 4/5) is closed and retried automatically (host per invited
  friend, client towards the host); panel shows the connection state in words; AutoHost / AutoJoin settings.

## The full world (2026-10-26, all live suites run on two copies, localhost)

World events (`Session.SendEvent`, reliable, through the host; state events keep the newest per key for late
joiners; global events ignore the scene) carry everything below. `src/CscCoop/WorldEvents.cs` lists the kinds.
Tables (scene key = hash of all of them): items (without the player's own tools), stains, bodies, van items,
locked doors, keys, evidence, cassettes, secrets, valuables, breakables, breakable objects, trash
(Dealer's Apartment: 591 / 383 / 3 / 5 / 22 / 4 / 28 / 5 / 3 / 53 / 62 / 36 / 143). Items use path +
start position; everything else hierarchy path with sibling numbers (positions differed between copies).

| suite | what it does | result |
|---|---|---|
| core (`dotnet run`, game-free) | + world events, state replay, global events, full lobby | PASS 75; mutants any-epoch, no-authority, no-drop, grab-always all FAIL |
| stains | strokes replayed with identical args, intensity min rule, destroy both ways, a new stain (ProjectBlood) copied to the same spot, cleaned by the other player | PASS 13 (clean); PASS 8 x2 + lossy before new stains were added; mutant stain-raise FAIL |
| bodies | client carries a body 8 m (root 0.27 m, bones <= 0.27 m apart), host can't take it, all 55 bones within 10 cm at rest, van both ways | PASS 9 clean and 20%/80 ms; mutant grab-always FAIL |
| doors | locked door unlock+open by host, other door open+close by client (poses equal), key into both KeyHolders, evidence, valuable on both reports, secret, breakable object, cracked item, quest entry | PASS 17 |
| trash | can pickup gone on the other side, host's new bag shown at the same spot, client takes it over and carries it (copy follows), at rest equal, into the van and counted on the host, host's placed bucket shown | PASS 7 |
| items | (rerun after bodies joined the index space; the bot looped to ItemCount = items + bodies and threw every frame) | PASS 8 clean and 20%/80 ms |
| jobs | host leaves -> client leaves, both get the report, close it, back in the hub; host starts the job, client follows by itself, both share it again | PASS 7 (saves restored after) |

Bugs found by these suites and fixed: lazy stain table took the already-changed value (host's first change never
sent); `Door.CrackOpen` patch turned the game's own menu NullReferenceException into a swallowed one (153 per
start; plain doors aren't in jobs, patch removed); bodies: returned bodies resettled on the host (keep kinematic),
bones under rigidbody-less parents (send poses relative to the root); keys need KeyHolder.CollectKey; the
player's bucket was in the item table (different tool upgrades would have switched off the whole job's sync).

Design notes: the stain-raise mutant first passed because the receiver also refuses raises; it now breaks both.
Cleaning visuals: synthetic PaintTexture calls (any arguments) never changed a decal's colour in probes, so the
replay is checked by arguments and hit counts; whether a real mop stroke looks the same on both screens is
unverified until a human plays.

Not shared (known): shards from cracked items (each machine has its own), tool puppets can't be used by the
other player (rinsing in a friend's bucket), re-dirtying a cleaned stain (raises don't spread; checked later: not
a gap, see the 2026-10-28 contribution), MusicDisc (hub record player only), plain Door (menu/hub only).
Each player keeps own money, skill points and cassettes in own save.

## Owner's manual two-window test of 0.2.0 (2026-10-26, logs in local/manual1)

Worked: capsules, carrying/placing items, refusing a held item, cleaning both ways, keys, evidence, bodies into
the van, the cleaning/trash/body quest progress, cassettes, secrets, leaving together, report, following the
host into the next job (Spa: 201 items / 603 stains / 5 bodies, tables equal).
Found, and what was done (all in 0.3.0):
- RMB throw: the item "landed" on the host and jumped back on the client; thrown bodies hung in the air, one
  thrown out of a window reappeared at its start on the host (the game's out-of-bounds reset). Cause (derived):
  "at rest" was "kinematic", and the game holds a thrown item/body kinematic for a moment, so it was handed back
  mid-air. Now rest = not moving for 0.3 s (GameWorld.Track); a takeover starts from the owner's last state and
  velocity, not from the smoothed copy.
- Positions from the host arrived late: resting items were only corrected by the slow round-robin refresh (~10 s
  for 590 items). Now the final state is resent 3 times after an item stops; items 20 Hz; faster smoothing.
- A body twitched on the client: the game re-enabled its physics; remote-driven items/bodies are kept kinematic
  every frame. Bodies lying since the start get their bone pose sent once.
- The mop didn't push items on the client (they slid back): OnToolPunch/OnHandPunch claim the item first.
- Not shared: drawers/cabinets/room doors (Openable, 80 in Dealer's Apartment), the lift (MovingPlatform),
  boarded windows (CrackedObject parts, 93), furniture back in place (FurnitureHologram.UpdateQuest -> LoadItem
  on the others, 34 spots), valuables taken by hand (the real path calls StealableItem.OnSteal, never
  AddStolenItemFunds), shards (now paired: same CrackItem on every machine, CreatedCracks in the same order).
- Two-window test: an unfocused window paused (Application.runInBackground is set during a session; this also
  keeps an alt-tabbed player's session alive). A 75-79 s game freeze on the host showed up in 3 world2 runs
  (mod time 3 ms in that frame); not reproduced with the watchdog in place; not seen with a single copy.
  `Watchdog` reports the last mod step if frames stop for 5 s.
- world2 suite (`-Test world2`): drawer, board part, valuable via OnSteal (both reports), mop hit takeover, lift,
  furniture spot, shards moved/collected. PASS 11 (after test-timing fixes); earlier fails were the test
  (dump before the other side acted, a destroyed part read after breaking).

## Owner's manual test of 0.3.0 and 0.3.1 (2026-10-26, logs in local/manual2)

Worked: figures, carrying/throwing (positions now right, a short delay on the client), mop pushes items, furniture
spot counted on both, valuables counted on both, shards at the same spots. Not yet: room doors and cabinets
(clicks), trash from broken objects counted per player, a stolen valuable stayed visible for the other player,
the two copies' saves were at different missions.
- Clicks don't go through what the bot called: Openable.Interact tweens and flips isOpen itself; ShortcutDoors
  clicks go Interact -> TryOpen / Toggle (ISIL). Openable.Interact is now a state event; ShortcutDoors Toggle /
  TryOpen are replayed (Interact itself not: it checks which side of the door the clicking player stands on).
- Actions (src/CscCoop/Actions.cs): a whitelist of Type.Method calls with int/bool/float arguments replayed on
  the others, outermost call only: Interactable.Interact (plain interactables), lift levers and controllers,
  ladders, shock boxes, floor buttons, lift panels, warehouse lift/power switch/conveyor knobs, crane buttons,
  water elevator, elevator doors. UI/dialogue interactables skipped.
- Broken-object parts (CrackedObject, collectable once broken) picked up by a can: a state event per part.
- A stolen valuable is hidden on the others (the thief's game hides it with the animation).
- Saves at different missions: the host announces a job it loaded straight into (no StartMission), and a client
  in another job switches to the host's job by itself (not only from the hub).
- world2 suite PASS 15 (+ drawer click, door Toggle, valuable hidden, part collected); follow suite PASS 4 (host
  starts Spa from inside Dealer's Apartment, the client switches and both share SPA&Wellness).

## Build 0.3.1 (2026-10-26)

- dist/CscCoop-0.3.1.zip (SHA-256 00af13dc...1e2e48). Suites on this code: items 8 (held items 0.00 m apart on both
  sides), stains 13, bodies 9, doors 17, trash 7, world2 15, follow 4, jobs 7.
- Test fixes on the way: with lockstep the bots' clocks differ, so walking and dumping are now separate steps;
  the long frames (75-153 s) came right after the other copy was killed at the end of a run (not mid-test).

## Build 0.3.0 (2026-10-26)

- Everything from the manual-test list above, plus PlayerFigure (human figure of primitives: walking legs/arms,
  head pitch, arms forward when carrying) instead of the capsule, the save slot choice, and test lockstep
  (bots wait for each other's step changes; a frozen copy no longer breaks the timeline).
- Suites on 0.3.0's code: items PASS 8 (and 20%/80 ms), stains 13, bodies 9, doors 17, trash 7, world2 11,
  jobs 7. Freeze in the bot runs: the whole game process (watchdog thread too) stood 75-118 s, only with two
  copies and bots; the owner's manual two-window runs had none. Treated as the test bench, not the mod.
- dist/CscCoop-0.3.0.zip (SHA-256 35558dec...377c96).

## Build 0.2.0 (2026-10-26)

- `python -I tools/make-package.py 0.2.0` -> dist/CscCoop-0.2.0.zip (SHA-256 33364e59...f114f, 36 MB): BepInEx be.791,
  unity-libs zip, CscCoop.dll + CoopCore.dll, README-RU. Steam smoke on one account passes on 0.2.0.
- The test copy's Saves now also has a folder for the Steam account logged in during the last runs (tests use
  whichever account Steam has); left untouched, it may hold the owner's play from the copy.

## Save slot from the panel (owner's friend's request, 2026-10-26)

- F8 in the main menu lists the existing save slots (SaveSystem.IsFileAvailable 1-3, header: chapter, time,
  last save); the chosen one (setting LoadSlot) is loaded by SaveSystem.StartGame when hosting or joining from
  the menu, also by AutoHost/AutoJoin, so a player goes menu -> hub -> host's job without clicking Continue.
  Bot run (`CSCCOOP_TEST=slots`): slot 1 listed, host + load from MenuScreen -> Dealer's Apartment, player can
  move, session on. Screenshot local/shot_slots.png.
- Manual two-window test: `tools/run-manual.ps1` (host left, client right, localhost UDP, no bots).
- First manual run went wrong (owner, 2026-10-26): the launcher titled and moved Process.MainWindowHandle, which
  for the host was the BepInEx console, so the windows' HOST/CLIENT labels didn't match; then the game applied its
  saved full-screen setting after the menu, so both windows covered the screen. The owner pressed Steam "Host" in
  the host copy, which replaced the localhost session; the client was left alone. Fixes: the launcher finds the
  UnityWndClass window, the BepInEx console is off in both copies, the plugin keeps the window in place
  (CSCCOOP_WINDOW, WindowPlacer), in test mode the panel offers only "reconnect (test)" instead of Steam host/join,
  and stopping a session is logged. `tools/window-check.ps1` prints where the windows are (checked: HOST x 0..1286,
  CLIENT x 1280..2566).

## Next

- Joint test of 0.2.0 with the friend: a whole job together.
- Kowalski model instead of the capsule. Model probe (2026-10-26, Dealer's Apartment): the job's cutscenes
  (TruckExitCTSPhone, CTSTruckEscape, TruckExitCTS) have no player Animator bound; the only human Animators in
  the scene are the 3 bodies (DrugDealerAnimAvatar, Girl02Avatar). So no player model is loaded in a job; it
  would have to come from the hub/intro scenes or Addressables. Unknown which.
- Shards, using another player's bucket, re-dirtying (re-dirtying: not a gap, see the 2026-10-28 contribution).
- Open, from the owner's test on Developers' Hell (2026-11-01; mission 11, scene GameDevOffice, run-pair -Mission 11):
  - Elevator: one player's ride moves every other player too, even outside it. Not looked into; likely suspect:
    Actions replays ElevatorDoor.SummonElevator / lift calls (GameDevOffice has 2 ElevatorDoor), and the elevator
    script may move "the player" (each machine's own) rather than whoever stands in the car.
  - Heavy furniture lifted by two players at the same moment ends up not lifted on one of them (Furniture: Heavy
    events, HeavyGrabber; the grab check only refuses a piece someone else is already known to push).
  - Two trash bags merging make duplicates until they end as one big bag: probably both owners run the merge
    (GarbageBagConnector.OnCollisionEnter is blocked only on puppets, and a bag can be ours on one machine and the
    other's bag ours on the other).
  - Super Stacker arcade machine: using it opens the minigame for all players; the others then vanish, the
    connection seems lost, the debug panel says "player in another place" though all are in the same level, and the
    world stops syncing. Probably a replayed interaction (Actions: Interactable.Interact or similar) loads the
    minigame on every machine, and the minigame changes the active scene, so the scene key differs.
  - Personal flashlight: not shown to the other players (PlayerPose carries the tool in hand, not the flashlight).
  - End of a job: the report's score animations play twice, about a second apart. Not looked into; likely the end of
    the job reaches the report twice on a machine: once from its own leave and once from the other player's
    (Ev.Leave -> Jobs.OnLeave), or a replayed call that opens the report.

## Unknown

- Whether the PR author will publish the source.
- Visual result of replayed cleaning strokes in real play; power washer and lamp stations (PortableItem
  subclasses) as dynamic objects (only the bucket was tested).

## 0.3.2 (2026-10-26, built without running the game — owner needed the PC)

From the owner's three-mission two-PC run on 0.3.1 (logs in local/friend3):
- Lift rode the opposite way on the friend's side: we replayed `MovingPlatform.ChangePlatformState`, which is
  the per-physics-frame movement step. Now `Interact` is watched (only a ride that just started) and the event
  carries the destination; the receiver calls `Interact` only if its lift isn't already there or heading there.
  Bot tests (world2, lift, shard probe) switched to `Interact`.
- Bodies thrown by the client hung in the air: a body taken over stayed kinematic (remote-driven state).
  `GameWorld.ReleaseBodyPhysics` on grab gives physics back (animator restored only if it was on).
- `InvalidCastException StatsTracker -> Interactable`: IL2CPP identical code folding sends other types'
  calls through our patch; `Actions` now TryCasts and ignores non-matching instances.
- Heavy (pushed) furniture: `HeavyItemController`, moved by `HeavyGrabber.GrabItem` (parents to the grab point,
  `GrabChildObjects` takes what stands on it) / `DropItem` (unparent, `Release`: floor raycast,
  `RestoreChildObjects`). New `Ev.Heavy` state event, 10 Hz while pushed, final pose sent in the DropItem
  prefix; receivers call `Grab(null)` / `Release()` around the smoothed pose. A remotely pushed piece can't be
  grabbed locally.
- Interactables audit: subclasses overriding `Interact` aren't seen by the `Interactable.Interact` patch.
  Added TvController, CableTvController, RadioController, MusicBoxController, LampController (UV lamp skipped:
  personal sight), SoundOnInteract, Ozonator. Left out on purpose: UI panels, Barman, MusicDisc,
  StandingGarbageCollector (Trash), InspectableItem/Mail (Doors/personal), PowerWasherStation (tool).
- Untested in game: all of the above. CoopCore tests 75/75.

### 0.3.2 test run (2026-10-26, quiet mode) and the save-class audit
- Quiet bot runs: `run-pair.ps1` now starts both copies small, stacked on the second monitor, lowest quality,
  30 fps, Wwise output volume 0 (`Quiet.cs`, CSCCOOP_QUIET=1; `-Loud` for the old behaviour).
- lift PASS (both copies ride the same way within 0.2 s); bodies 11/11 incl. a real client throw
  (`TryReleaseBody(8)`: the old test never let go, IsGrabbingBody is false right after GrabBody); world2 15/15;
  doors 17/17 after splitting the client's close into its own lockstep step (it closed in the same step as its
  dump, so the host dumped after the close arrived); stains 13/13; items 8/8 (one run had one item 11 cm off).
- `Actions.Apply` leaked `depth` when the cast failed (returned after depth++): every later action would have
  stopped being sent. Fixed.
- Audit: the 83 classes with Save/Load are the world state the game itself keeps. Shared now or before: doors,
  drawers, lifts, ladders, keys, evidence, valuables, secrets, cassettes, breakables, furniture, quests, trash,
  bodies, lamps, radio/TV, power switches, conveyors, cranes, elevators. Added (no direct callers, UnityEvents;
  all one-way): BoxBoxCage.PullDownCage, CryoChamber.OnPanelClick, VacuumChamber.TryActivateChamber,
  Pool.TryDrain/TryFill, UfoButton.Press, Lockdown.EndLockdown, FixableObject.CompleteFix, ObjectDestroyer.Destroy,
  DoorsCardReader.Unlock. Personal on purpose: tools and buckets (Dyn), inventory, perks, skins, challenges,
  codex/tablet, hacking and lock-picking UIs (their result reaches the others through quests/doors),
  contamination per item, mission timer. Not added: toggles with unknown triggers (TogglableEvent, TrashSpawner,
  WarehouseTrapdoor: driven by the shared power switch).

### Census of all jobs (2026-10-26)
- `tools/run-census.ps1` (Bot "census"): one quiet copy starts every mission from the hub and writes
  `local/census.csv` (scene,type,count); `tools/census_report.py` compares it with the mod's source.
  First try hung: a StartMission sent while a reload of the same job was under way was lost and the game
  stuck on the loading screen; the bot now waits for a loading scene to appear and retries a lost start.
  Game's own NREs (DialogueEventsController in Taint.SetIntensity) show up when a job is restarted in itself.
- 17 scenes, 510 game types: 74 looked at by the mod, 260 interface/personal, the rest passive or mission
  machinery. Added: knocked-over furniture `FixableObject.OnFix` (the owner's shelf: 5 in Dealer's Apartment;
  world2 checks it, 16/16), `CleanableQuote.OnHit` (washable writing, guarded against a second quest event),
  `SpaLockerStateSaver.OnOpen`, `UniversalHackingPresenter.OnHackingEnd` (8 jobs), `LiftFloor*.OnPressButton`,
  `HoldInteractable.HoldInteract`, the Lab's `MadScientistFusesController.Change*Power`. 55 actions in all.
- Friend's bucket: Dyn tool state carries LiquidContainer water and dirt (trash suite 8/8; the game reset
  the host's water the test gave it, the dirt 0.40 arrived).
- Left: turrets, pushing fans, conveyors (physics, run on both sides), bowling balls/vending machine spawns,
  decontamination zone and portals (personal triggers), Act2 intro sequence.

### Manual test on Warehouse (2026-10-27) and the hanging bodies
- `run-manual.ps1 -Mission 10`: both copies load slot 1 by themselves, the host starts the job, the client
  follows; CSCCOOP_LOWGFX=1 (lowest settings, 60 fps, sound on). Owner: furniture, drawers, power, conveyors,
  hacking, bucket, shards all shared. Racks: FixableObjectChild parts; the game counts the rack (parent) only
  when all its parts are up, so two parts stood up didn't move the furniture counter (not the mod).
- Bodies thrown by the client still froze mid-air 1-2 s later. Cause: right after the throw the bones report
  IsSleeping (they slept while kinematic) though the body still moves (log: root 1.52 m, 0.59 m/s, 11/11
  "sleeping", still False); the owner took that for rest, handed it to the host, and both froze it kinematic.
  Now bones are kept awake for 4 s after a let-go and rest = sleeping AND not moved for 0.3 s. bodies suite:
  CSCCOOP_THROW=25 knob, height watch every 0.2 s with a raycast of what is under the body, and a check that
  the hand-over after the throw happens with "still True" (the old code fails it). 12/12.

## 0.3.3 (2026-10-27): how another player looks

- Tool probe (`CSCCOOP_TEST=toolprobe`, `tools/run-probe.ps1`, one quiet copy): a held tool is an instance of
  `ToolData.ItemPrefab` under `Player/LookHandle/ArmsNew/Arms/ObjAttach_R|L/ObjAttachRotation_*` (the garbage
  collector on the left), layer 8 (hands camera only), URP Lit materials (so URP Lit works for our meshes; the
  "only a shadow" problem of PR #176 was the built-in shader). `InventoryController.TryAddItemToInventory`
  puts `modelOffsetPosition/Rotation` on the tool root, so the root's pose is what the first-person view shows.
  Save slot 1 hotbar: `Equip(2)` mop (WeakMop, tool 20), `Equip(3)` sponge (17), axe = `ItemEquipper.AXE_TOOL_ID`
  (46). Eyes are 1.70 m above the player transform.
- `PlayerFigure`: one `SkinnedMeshRenderer`, 17 bones (hips, spine, chest, neck, head, 3 per arm, 3 per leg),
  ~3150 vertices built in code: lofted rings along the bones (18 per ring) with 1-2 bone weights, ellipsoids
  for head, hands, boots, respirator, goggles; 4 submeshes (overalls in the player colour, dark gloves/boots/belt,
  respirator, goggles). Procedural pose: walk by speed (legs, knees, feet, arm swing, hip bob), view pitch split
  over chest/neck/head, arms forward with an item, two-bone IK of the hands onto a held tool. No Blender.
- `ToolLook`: the pose carries `Tool` (ToolData id, -2 lance, -1 none), a use flag (CleaningItem/PowerWasherLance
  `isCleaning`, `AxeTool.ExecuteHit` in the last 0.4 s) and the tool root's pose relative to the player transform.
  Receivers copy the enabled MeshRenderers of the prefab (lance: their own lance instance); parts under names
  matching water/stream/vfx (the lance's "Mgielka" mist) are shown only while used. While used the tool strokes
  forward and back (0.14 m, 1.8 Hz) on top of the owner's hold. Old senders (no tool bytes) still parse.
- `Bodies.Shoulder`: a body whose holder (pose HeldItem = body entity, owner = that player) is another player is
  posed on that player's right shoulder through the body's humanoid bones (`CC_Base_Hip`...): pelvis on the
  shoulder, thighs forward, torso and head down the back, arms hanging; its bone events and root smoothing are
  skipped meanwhile. Let go, the stream takes over again (the 4 s wake / hand-over at rest is untouched).
- Tests: CoopCore 77/77 (tool fields in poses). New `figure` suite (`tools/figure_report.py`, 11 checks, writes
  `local/shot_figure_*.png` from a camera of its own: near plane 1.6 m and a ray-picked direction so walls don't
  hide the figure). bodies report: the carry checks now expect the body on the client's shoulder on the host
  (root 0.55 m from the client's own carry pose) instead of the same bones on both machines.
  A bodies run once failed "handed back at rest": the thrown body landed on a shelf and jittered ~1 cm, never
  "still", and the core's 8 s YieldMax handed it over; the rerun passed 12/12. A real weakness of the 1 mm
  stillness threshold, not of this change.
- Test-only tool poses (`ToolLook.ForceTool`, axe and lance aren't in the bot's inventory) are guesses; in play
  the pose comes from the owner's first-person view. Hose of the power washer: not shown.

## 0.4.0 (2026-10-27): co-op from the main menu, shared hub view, laptop jobs, ping

- **Save-wiping bug in 0.3.x found**: `SaveSystem.StartGame(slot)` is the game's *new game* (ISIL: logs "New save
  <file>", `ES3File.Clear`, `PlayerSave.InitSave`, Restart of perks/missions/challenges/skins, `GameManager.StartNewGame`);
  `MenuNewGameController.OnSlotClick` calls it. Loading is `SaveSystem.LoadSlot(slot)` (`MenuLoadSaveController.OnSlotClick`:
  restore/create backup, `Load`, `LoadIntoHub` or `StartMissionImmediate` if saved in a job). The 0.3.x F8 slot choice
  and AutoHost/AutoJoin with LoadSlot set called StartGame: a friend who used it got a new game in that slot (the
  game keeps `SlotN.es3.bac` and `Saves/SavesBackups*/MissionN`). The bots always started new games this way, so the
  test copies' slot 1 is just whatever the last run left (now: a save inside Warehouse with no mails or jobs).
  0.4.0 loads with LoadSlot and only calls StartGame for an empty slot ("Новая игра в этом слоте").
- Main menu (`MenuScreen_Warehouse`): `MenuButtonsController.mainMenu` = MenuGeneral/OptionsMenu, a VerticalLayoutGroup
  of `MenuTopButton`s (Image, LocalizedText on the Text child). `CoopScreen` clones LoadGame after NewGame, drops
  LocalizedText, subscribes `UICustomButton.add_OnClick`. A click goes through `OnPointerClick(PointerEventData)`
  (the no-argument overload does nothing). While the screen is up the menu's EventSystem is off.
- Screen: Кооператив -> Хостить -> slot / Присоединиться -> slot -> friend ("Играть у", or "Ждать" = AutoJoin on
  that friend) / Назад. Friends: every Steam friend in the game, hosting first (`SteamDirectory.Everyone`).
  Test mode lists the localhost host; `CSCCOOP_VIA_MENU=1` starts the localhost session from the screen.
- Hub: `GameWorld` gives Hub scenes a key from the scene name only and no tables (players only); `WorldEvents`
  drops non-global events in a hub both ways; Dyn/Furniture/Stains/Bodies don't tick there. Different acts' hubs
  (HubResidence, Hub02) have different keys: not shown, status says the host is elsewhere.
- Jobs and saves: laptop accept = `MailController.AcceptMission` -> `GameManager.AddMission(mail)` ->
  `Missions.AcceptMission` (MailData.HasBeenAccepted). Truck = `MissionsMapController.StartMission` ->
  `GameManager.StartMission`. New mails come with the next hub day (`HubController.ProgressHub` -> `SendNewMails`).
  Host sends its accepted jobs (HostJobs), clients accept the same mails their own laptop offers and report what
  they can start (MyJobs); truck start refused for clients and for the host while a player lacks the job; a client
  follows only into jobs its save has (`CSCCOOP_ANY_JOB=1` for manual tests with odd saves).
- Ping: `Msg.Rtt` once a second both ways, echoed, smoothed 1/4; corner label "Кооп: ..., пинг N мс". On the two
  quiet copies on one PC it reads ~350 ms: it includes both games' frame times (each side answers on its next poll).
- Slots are numbered from 0 in code: `SaveSystem.GetSlotFileName` 0 -> Slot1.es3, 1 -> Slot2.es3, 2 -> Slot3.es3,
  anything else -> Slot1.es3. The 0.3.x panel listed 1..3, so its "slot 1" was Slot2.es3 and "slot 3" (index 3)
  started a new game in Slot1.es3. The bots' `StartGame(1)` makes a new game in Slot2.es3. 0.4.0's screen counts
  1-3 as the game's menu shows them and passes n-1.
- Owner (2026-10-27): before a job's mail shows on the laptop the player must sleep and then answer the phone.
  A client whose story is behind gets the host's job only after doing that itself; mirroring retries every 2 s, so
  the mail is accepted as soon as it arrives. The host's refusal text says so.
- `laptop` suite: run-pair copies a played-through save (default: the owner's install Slot2.es3, read only) into
  Slot3.es3 of every account folder of both copies, both load it from the co-op screen; saves restored after.

### Objectives both ways, story objects, restart for both (0.4.0, 2026-10-27)
Owner's two-window test of 0.4.0: furniture 3/63 vs 2/63, bodies 10/13 vs 9/13, the attic hatch and the Spa
walkie-talkie had to be used by each player (the attic objective kept the host's job open), "get rid of the
victims' bodies" only on the host, a restart only on the host.
- `Objectives.cs`: Dialogue System Lua variables both ways (true wins; counters' numbers — written inside
  IncrementEvent/BodyQuest UpdateLuaVariable* — the larger wins and a machine behind keeps showing it; other
  numbers and strings latest wins; SetLoc, the player's own location, stays personal). Counter lines
  (`SetQuestEntry`, `SetQuestEntryPercentage`) larger wins. `AddQuestWithName` replayed with the same
  LocalizedString (table name or guid + key id). `GameManager.RestartMission` by either player restarts both.
  Boxed Il2Cpp values must be unboxed by type: `ToString()` gave "466594880" for a count of 1 (caught by a
  world2 check that all numbers received are real counts).
- `InteractablePanel` is not UI: a click firing its OnInteract story event (attic hatch, walkie-talkie; 1-14
  per job). It was on the Actions skip list; now `InteractablePanel.Interact` and
  `InteractableWithModalPanel.SendResponse` are replayed. No actions are replayed in the hub (laptop).
- world2 checks: story object used on both, a client's Lua flag on both, a client's 7/9 counter on the host,
  received numbers sane. 20/20.
- Regression found by the owner the same day (Dealer's Apartment, two windows): one client body counted 2/2 on
  the host and then 3/2 on the client; blood 95% (apartment) and 59% (attic) on the client with nothing cleaned,
  the client's game wiping the rest of its blood itself; the host at 100% blood with the objective open and
  blood still there. Cause: numbers merged across machines. BodyQuest keeps its count in Lua
  ("<quest>.BodiesMin") and adds 1 to what it reads, so the client's 1 plus the replayed van collect made 2; a
  percent from the other machine pushed the client's cleaning objective over the game's auto-finish line.
  Fix: numbers never cross (counts, percents, numeric Lua values); flags, text, added objectives, story
  objects and the restart still do. Gates G8-G10 (GATES.md): bodies (each body objective's count equal on both
  after one van body each), stains and world2 (no objective number crossed; the world2 check fails on the run
  before the fix: 5 crossed). Furniture briefly at a wrong spot on the client: not reproduced yet. Bodies suite now opens the attic and
  each side vans one victim: without numbers crossing it showed the real hole, host 2 / client 1: a body counts
  only after `BodyQuest.AddBodyToQuest` (the player finds it), and the client had not found the host's body.
  `BodyQuest.AddBodyToQuest` is now a shared action, and a replayed van collect adds the body first. 2 / 2.
  The throw check now raycasts from every bone (a body lying on a shelf hangs its hips over the edge).

### F9 bug reports (0.4.1, 2026-10-27)
- `Reports.cs`: F9 saves `BepInEx/coop-reports/<yyyyMMdd-HHmmss>-host|client.png/.txt` (screenshot; scene,
  connection, ping, position, held thing, active/done quests with Current/Max/BodiesMin and entry states) and
  sends Ev.Report (global) so every player saves the same id at once; then an IMGUI box takes a comment (Enter
  saves it into every player's file via Ev.ReportNote, F9 again skips; the player is Block()ed while typing).
  world2 checks it end to end (the client's report: same id, png and txt on both, comment in both).

### Players' lamps and UV lamps (contributed, 2026-11-01)
- Owner's report (Developers' Hell): switching my lamp 1 switches the friend's lamp 1; with 2 UV lamps each (4 on the
  map), lamps over the limit don't light blood.
- Lamps: each player's own tools (PortableItem) were in the scene tables (actions:LampController and others). With
  the same lamps on both machines a replay by index switched the other player's own lamp; with different lamps the
  table differed and was switched off (scene lamps unshared). Now GameWorld.Collect leaves anything under a
  PortableItem out of every table, Actions never replays on one, and Dyn's tool state carries the lamp's on/off
  (isOn, then Switch); switching another player's lamp (LampController/UVLampController.Interact) takes it over.
- UV lamps light blood through a shader matrix: UVLampController takes a slot when enabled
  (CleaningSenseController.TryGetSenseIndex into cleanerSenseIndex, -1 when none), Update lights only slots above 0,
  OnDisable returns only those: 3 slots. The other player's UV lamps are copies here and took slots in the order
  they appeared, so an own lamp could stay dark. UvSlots (every 0.5 s) gives the slots to own lamps that are on, then
  the other player's that are on, then own off, then the other's off (ReturnIndex / TryGetSenseIndex). The game can't
  light a fourth: with 4 UV lamps on, the other player's second lamp is dark on this screen.
- Also: UVLampController.Interact does nothing unless the lamp stands (DetectObjectOrientation.IsStanding), and
  Update switches a fallen one off.
- lamps suite (-Test lamps -Mission 11): each player puts down a lamp and two UV lamps (client first, so the host's
  copies of them come first); the host's lamp on, the client's off, the client switches the host's off later; all UV
  lamps on. PASS 10/10 twice. Mutant uv-first (no UvSlots): the host's own lamp is on and dark (slot -1), FAIL.
- The client's 31 "table differs" warnings while loading the next job were stale fingerprints of the old job
  compared under the new scene name (BuildTable reset them); GameWorld.Clear now clears them.

### Cleaning products in buckets and power washers (contributed, 2026-11-01)
- Owner's request: share the products in buckets (and the tools upgraded from them), as the earlier csc-coop
  prototype did. Only the bucket's water and dirt were shared; products not, and using another player's bucket
  changed it here only.
- Dyn tool state now carries the products (LiquidContainer.activeBuffs / PowerWasherStation.activeBuffs as
  "buffId:effectiveness:threshold;", rebuilt with TryFindBuffBase + BuffInstance.LoadBuffData) and the station's
  water. Using another player's bucket or station here (CleanTool, TryAddBuff, TryAddWater, AddWater, CleanWater;
  station TryAddBuff, TryAddWater) takes it over first, so its new state goes out from here. A mop rinsed in a
  friend's bucket takes its products (CleanTool copies them). PowerWasherStation overrides PickUp: the
  only-the-owner rule is patched there too.
- products suite (-Test products -Mission 11): the host pours green into its bucket; the client sees it, rinses its
  mop there (the mop gets it), empties it, fills it and pours red (a one-star bucket holds one product). PASS 6/6
  (both end with red, same water and dirt); mutant bucket-look (no take-over on use): the host keeps green, FAIL.
  toolcarry 7/7, trash 8/8.

### Doors drifting apart, footprints missing on some floors (contributed, 2026-11-01)
- Owner's report (Developers' Hell): a door open on the host is closed on the client, and the host closing it opens it
  there; a footprint made on one machine sometimes doesn't appear on the other.
- Doors (84 ShortcutDoors in GameDevOffice; doorprobe: tools/run-probe.ps1 -Test doorprobe -Mission 11): a click is
  Interact -> OnOpen -> Toggle, a push TryForceOpen/TryForceClose, a switch ExternalToggle; none calls
  OpenDoors/CloseDoors. Actions replayed Toggle as a toggle, Doors sent only OpenDoors/CloseDoors: any path not
  replayed (a switch, a push) left the door inverted, and every later click kept it so. Now Toggle is not replayed;
  Doors sends the state (isClosed, set at once by every path: field +224) after Toggle, TryForceOpen/Close,
  ExternalToggle, OpenDoors, CloseDoors, ResetDoors, once per change, and the others apply it with
  OpenDoors/CloseDoors. doorsync suite (-Test doorsync -Mission 11: the host opens a door by switch and one by
  click, the client clicks, each clicked door gets two actions): PASS 4/4; mutant door-old (the 0.4.2 rule) FAIL,
  door 0 open on the host and closed on the client. doors 17/17.
- Footprints: the floors there are under a root named "WALLS/WINDOWS/FLOORS". GameWorld.FindByIndexedPath split
  stain target paths at every '/', found no target, and ProjectBlood threw (NullReferenceException) on the other
  machine: no stain on those floors was shared (footprints, blood, anything). The path is now split where a segment
  ends in "#<sibling index>". footsteps suite (-Test footsteps -Mission 11: the client steps 12 times with bloody
  shoes through FootprintSpawner.ProcessStep): 12 of 12 missing on the host before, PASS 5/5 after (same decal,
  spot and strength). (Sending the decal prefab along was tried and made no difference: the blood type's prefab is
  the same FootprintDecal.) stains 14/14, world2 20/20.

### Anyone can carry another player's bucket or lamp (contributed, 2026-11-01)
- Owner's report: players can't move other players' buckets and lamps. Tool puppets refused every grab (Dyn.OnLocalGrab).
  Also, nothing stopped another player's E on a tool puppet: PortableItem.PickUp (DOTween scale, then
  PlayerInventory.TryAddItemToInventory) would have put the friend's tool into a second inventory.
- Now a tool puppet is taken over like a bag (DynTake): the grabber simulates it, the owner's copy follows. Only the
  player the tool belongs to (its id's player number) may PickUp it; if someone else simulates it then, the owner
  takes it back first.
- toolcarry suite (-Test toolcarry -Mission 11): the host takes its bucket out the game's way
  (InventoryController.Equip(bucket, 0), then lets go), the client grabs and carries it 0.9 m; same spot on both; the
  client's PickUp is refused; the host's PickUp works and it is gone on both. PASS 7/7; mutant tool-locked (the 0.4.2
  rule) FAIL 4/7. trash 8/8.

### Items at rest get physics near the action (contributed, 2026-11-01)
- Owner's report (Developers' Hell): physics worse than in the earlier csc-coop prototype. Cause: the host owns every
  resting item, and every other machine kept them kinematic. On the client a thrown item bounced off resting items as
  off a wall (bump suite with the old rule, mutant kinematic-rest: target moved 0.00 m), and nothing could be knocked
  over except by a mop or hand hit (those claim the item).
- First try, all resting items on local physics everywhere (as in the prototype): items started from the owner's pose
  tipped over by themselves (vases 40 deg, bottles, a ball), 30 claims in seconds, and piles settled apart; putting
  them back looped (28618 corrections in 30 s); Rigidbody.IsSleeping() said asleep while an item moved 20 cm.
- Now (GameWorld.Smooth/WakeNearby/LocalPhysics): a remote item the owner has at rest gets physics here only within
  1.2 m of our player (checked every 0.1 s) or within reach of an item we move (every frame, 0.8 m + 0.3 s of its
  flight). Knocked away fast (> 0.5 m/s or 3 rad/s, > 3 cm or 6 deg), it is claimed (at most 3 claims a minute) and
  the others follow; otherwise, once still again, it goes back to the owner's spot, kinematic. Moving is judged by
  Track's per-frame movement, not IsSleeping.
- bump suite (tools/run-pair.ps1 -Test bump -Mission 11, Developers' Hell): the client throws an item into a resting
  item the host owns; it must be knocked away on both, end at the same spot, and all 1158 items must lie at the same
  spots on both. PASS 7/7 twice (target 0.71 m, 0.17 m); mutant kinematic-rest FAIL. Also world2 20/20, trash 8/8,
  breakhit 6/6, stains 14/14, items 8/8; bodies 12/13 (the shelf case, as on 0.4.2 here).
- run-pair -Mission <id> runs any suite in that job (11 = Game Dev, scene GameDevOffice; the bot lists all missions).

### Test runs leave the mouse alone and skip the waits (contributed, 2026-11-01)
- Quiet bot runs (CSCCOOP_QUIET=1) took the owner's mouse: the game takes focus when its window opens and again when
  a job loads, and locks the cursor to its window every frame (Controller_Switch, FirstPersonAIO, MoveCamera,
  InputController set Cursor.lockState). It also locks it while starting and then stalls for seconds loading the menu
  (no frames): tools/mouse-check.ps1 saw the cursor clipped to one point for 4-8 s with Chrome in front.
- HandsOff.cs (quiet runs only): cursor freed in CoopRunner.LateUpdate; when the game takes focus with no mouse button
  down, focus goes back to the window that was in front when tools/run-game.ps1 started (CSCCOOP_FOCUS_BACK); a
  background thread releases any cursor clip while another window is in front (it runs through the stalls). Rewired
  already ignores input while unfocused (ignoreInputWhenAppNotInFocus was true), so nothing to do there.
- Bot runs also skip the new game's intro cinematic (IntroController.Cinematic.Stop(); the game's OnSkipButtonPressed
  throws with a default Rewired event) and the loading screen's key wait (LoadingScreenController.WaitingForSkip =
  false), and run-pair passes CSCCOOP_MENU_WAIT=10 (was 25). Job cutscenes were already skipped (IntroSkip).
- tools/mouse-check.ps1 every second during trash, stains and world2 (259 samples): never clipped, the game in front
  once for under a second. Results: trash 8/8, stains 14/14, world2 20/20, each about 88 s (stains took 161 s without
  the shorter menu wait and focus handling).
- Manual runs (run-manual.ps1, no CSCCOOP_QUIET) keep normal mouse look.

### CCTV recordings and shards picked up by the other player (contributed fix, 2026-10-28)
- CCTV (Smart House laptop, checked in game): the 10 recordings ("1/05/2023" .. "10/05/2023") exist as soon as
  the job loads, without the UI. The game makes them with `new CCTVRecord(name)`, a MonoBehaviour without a native
  object, so Unity's `== null` is true for every one and the `rec != null` filters in Cctv.Tick and the cctv bot
  skipped them all ("10 null entries"; the managed object and its name are fine). Deleting in the UI goes through
  HackableComputerPresenter.HandleFileDelete, which removes the record from the model's list itself (no
  RemoveRecordInFolder call). RemoveRecordInFolder(name) on the other machine works, and an empty folder turns the
  "Delete CCTV" quest to Success.
- Fix: Cctv.IsRecord (managed reference, not Unity's ==); a prefix on HandleFileDelete (instance cast checked,
  for folded bodies); the watch records every name from the job's start and sends deletions made before the other
  player was connected once live. cctv suite: the client deletes one recording, then all, through
  HandleFileDelete; checks the host's folder and the quest on both. 7/7; the shipped Cctv.cs on the same suite:
  host keeps 10 recordings, quest Active (3 of 7 fail).
- Shards: a shard simulated by one player and picked up by another player's trash can stayed on the first
  machine: Dyn.OnState returned for its own objects before the "gone" branch. Now "gone, collected" is applied to
  our own object too. New breakhit suite (tools/run-pair.ps1 -Test breakhit): the client throws an item onto a
  bottle the host owns (it breaks once, the client simulates the shards, same spots on both), then the host picks
  up one of the client's shards. 6/6; before the fix the shard stayed on the client.
- Run with these changes: world2, trash, bodies, stains, items and the core tests (results in the PR).
- Re-dirtying a cleaned stain is not a gap (probe in the contributor's mod, Dealer's Apartment, 2026-10-29). In the
  game code, Taint.SetIntensity is called by CleanableDecal.Update (intensity follows the decal texture, and strokes
  only erase it), the save loaders, the cheat panel, and FootprintSpawner.LeaveFootprint (on the new footprint it just
  made). Every way of adding blood makes a NEW decal through BloodSpreaderController.ProjectBlood: dirty tool
  (CleaningItem.CleanOrTaint), footprints, bucket spill (LiquidContainer.TryCreateBloodSpill), drips; Taint.Update
  only sets the sort order. In game: a mop cleaned a stain 1.000 -> 0.784; the mop set fully dirty and stroked the
  same spot for 15 s (29 CleanOrTaint calls): no change; blood projected on top: a new decal, the old one unchanged.
  No rise on any of the 371 stains (SetIntensity hook and a scan every second), two runs. So the min rule matches the
  game, and the stains replay of ProjectBlood already covers re-dirtying. Not seen: the dirty mop's own blood (none
  spawned in that setup).

### Friend test of 0.4.1 -> 0.4.2 (2026-10-27)
- Villa: host key 85C12853, client 155AA62C, same 535 items: some table differed and the whole job's sync was
  off (label still "2 players"). Now the scene key is the entity space only (items + bodies); every other table
  has its own fingerprint (GameWorld.Parts), exchanged as a global state per player (Ev.TableHashes,
  TableCheck); a differing one is switched off alone (GameWorld.Off: Table indexer/IndexOf, TaintIndex,
  GarbageIndex, Stains.Get) and logged "COOP table 'x' differs ...". world2 with -Mutant table-differ (client's
  cassettes fingerprint changed) passes 20/20 and logs the switch-off (gate-mutant.mjs).
- Bodies counted twice both ways: after a replayed van collect the game calls CollectAndDestroy again (logs:
  "collected by another player" then "collected") and BodyQuest.CollectBody adds 1 each time. A guard on
  BodyQuest.wasCollected made the second victim count on neither side (that flag isn't the collect flag), so
  the same GarbageItem is now collected once (prefix on CollectAndDestroy, per scene). bodies: host gets the
  game's second call for the client's body, 2/2 on both.
- CCTV laptop (Smart House, HackableComputerModel): hacked and deleted recordings shared (Ev.CctvHack/Delete).
  The Hack(bool) patch never fired (a one-store method), so laptops are polled once a second. cctv suite (host
  starts job 5 from the first job, CSCCOOP_ANY_JOB): hack verified; the recordings exist only once the folder
  opens in the UI (10 null entries for the bot), so the delete is left to the owner's test (GATES G15 abandoned).
- Friend saw empty hands on the host: the host's MopTool copied with 0 renderers on the client (it only has
  WeakMop). ToolLook now copies SkinnedMeshRenderers too and falls back to a tool of the same kind. Not
  verifiable on the test copies (no upgraded tools).
- F9 reports from that run: objective maxima differ between the two (Bottom Floor Furniture max 21 host / 26
  client, Garbage 50 / 58) — open; a host screenshot taken while its window wasn't drawing came out a frame
  later (CaptureScreenshot waits for a rendered frame).
