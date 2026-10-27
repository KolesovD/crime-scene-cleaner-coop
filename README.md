# Crime Scene Cleaner Co-op

A co-op mod for [Crime Scene Cleaner](https://store.steampowered.com/app/1040200/) (Steam, build 26046462): play the
story jobs together with a Steam friend. BepInEx 6 (IL2CPP) plugin, peer-to-peer over Steam, no dedicated server.

**Status: playable for two players.** Two players finished chapters 1–6 of the story together (0.4.1, with 0.4.2's
fixes checked on the Villa job and by the automated suites). 3–4 players, Nightmare mode and the later chapters are
untested. [Download the latest release](../../releases/latest). Русская версия — ниже.

## Install

1. Both players: the same Steam build of the game and the **same mod version**.
2. Download `CscCoop-<version>.zip` from Releases and extract it into the game folder
   (`...\steamapps\common\Crime Scene Cleaner`), so `BepInEx\` and `winhttp.dll` sit next to `CrimeCleaner.exe`.
   It contains BepInEx 6 be.791 and the plugin; the first start takes a few minutes while BepInEx generates its files.
3. Back up `CrimeCleaner_Data\Saves` before the first co-op session.
4. Main menu → **КООПЕРАТИВ / Co-op** → host: Host → pick a save slot; friend: Join → slot → the host from the
   Steam friends list. The friend is taken into the host's job automatically. F8 in game: status, ping, leave.
   F9: bug report (see below).

## What is shared

| | Status |
|---|---|
| Connecting from the main menu (host / join, save slot, Steam friends), saves load normally | works |
| Following the host into jobs, leaving together, each player's own report, money and skills | works |
| Laptop jobs and mails in the apartment reach both | works |
| Players see each other: a figure with head/limbs, the tool in hand, a carried body over the shoulder | works |
| Items: carry, throw, push with tools; another player's item can't be taken from their hands | works |
| Bodies: carry, throw (falls on both), into the van; counted once per body | works (double count fixed in 0.4.2) |
| Blood: cleaned stains, new stains, stains cleaned away; objective percent matches | works |
| Trash: trash can pickups, bags (another's bag can be taken), bags into the van; buckets show water and dirt | works |
| Doors (locked, keys, key cards, one-way), drawers, cabinets, lifts and call buttons, power, conveyors, cranes, hacking terminals | works |
| Furniture onto holograms, knocked-over furniture stood up (multi-part racks count when complete, as in the game) | works |
| Evidence, valuables (on both reports), secrets, cassettes, broken objects and shards | works |
| Story objects (e.g. the attic hatch in the first job), objectives added during a job, objective counters | works |
| Jobs where the two saves differ a little (Villa): only the differing table stops being shared | works (0.4.2) |
| CCTV laptop (Smart House and later): the hack is shared | works |
| Job restart from the pause menu restarts it for both | 0.4.2, not tested by players |

## Known issues

- **CCTV recordings:** deleting the recordings has to be done by each player on their own laptop; the objective
  doesn't count for the other player.
- **Objective maximums can differ** between players (e.g. furniture 1/21 vs 1/26): the game itself raises the
  maximum when furniture that already stood in place is picked up and put back.
- **Upgraded tools** (e.g. the microfibre mop) may show as the basic tool of the same kind on the other player's
  screen if their game doesn't have that model (0.4.2 fallback, not yet seen by players).
- The walkie-talkie in the Spa job is shared in code but wasn't tested by players.
- Skipping the intro cutscene can leave the pizza box hanging next to the player in the apartment.
- Not tested: 3–4 players, Nightmare mode, chapters after the sixth, the final mission.

## Bug reports (F9)

F9 saves a screenshot and the game state (job, objectives with counts, held item, position, ping) on **every**
player's machine under one id, then asks "what's wrong?". Reports land in `BepInEx\coop-reports`; send them with
`BepInEx\LogOutput.log`.

## Building and testing

- .NET 6 SDK. The project references the interop assemblies BepInEx generates: point `GameDir` at a game copy that
  has run BepInEx once (`dotnet build src/CscCoop -c Release -p:GameDir=<copy>`); by default it expects
  `local/game-copy`. Never build against your real install; make a copy.
- `src/CoopCore`: the game-free sync core (ownership, poses, world events) with its own tests
  (`dotnet run --project src/CoopCore.Tests`).
- `src/CscCoop`: the plugin. `NOTES.md` is the full engineering log: what each game system does, why each fix was
  needed, and the evidence.
- `tools/run-pair.ps1 -Test <items|stains|bodies|doors|trash|world2|jobs|laptop|cctv|figure>` runs two copies on one
  PC with bots over localhost and checks the result (`tools/<test>_report.py`); `tools/run-census.ps1` lists every
  component type in every job.

Made with Claude Code (Anthropic) together with the players who tested it. Not affiliated with the game's developers.

---

# Crime Scene Cleaner Co-op (RU)

Кооп-мод для Crime Scene Cleaner (Steam, сборка 26046462): сюжетные заказы вдвоём со Steam-другом.

**Статус: играбельно вдвоём.** Пройдены главы 1–6 вдвоём (0.4.1; исправления 0.4.2 проверены на Вилле и ботами).
3–4 игрока, Nightmare и поздние главы не проверены.

**Установка:** у обоих одна сборка игры и одна версия мода; архив из Releases распаковать в папку игры (рядом с
`CrimeCleaner.exe`); перед первой игрой скопировать `CrimeCleaner_Data\Saves`. Главное меню → «КООПЕРАТИВ»: хост —
«Хостить» → слот; друг — «Присоединиться» → слот → хост. F8 — статус и пинг, F9 — отчёт о баге (скриншот и
состояние у обоих, папка `BepInEx\coop-reports`).

**Общее:** заказы, предметы, тела (без двойного счёта), кровь, мусор, двери, ящики, лифты, питание, конвейеры,
терминалы, мебель, улики, ценности, секреты, кассеты, сюжетные объекты (чердак), счётчики заданий, взлом ноутбука
камер. Друг виден с инструментом в руках и телом на плече.

**Известные проблемы:** удалять записи камер каждый должен у себя; максимум мебели может отличаться (так считает
сама игра); улучшенный инструмент у друга может выглядеть базовым; рация в Spa и перезапуск заказа вживую не
проверены; после пропуска заставки коробка пиццы может висеть рядом с персонажем в квартире.
