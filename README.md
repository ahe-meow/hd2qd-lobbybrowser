# HD2QD Lobby Browser

A plugin of [HD2 Mod Quick Deploy](https://github.com/pat-pat-patrick/hd2-mod-cli): the public
lobbies of Helldivers 2, listed in a window of the manager, so you pick the one you join instead
of the game's random matchmaking.

Adapted from [HD2LobbyBrowser](https://github.com/QLongYan/HD2LobbyBrowser) (MIT) — the lobby scan,
the lobby keys and the name tables are that program's, ported to a manager plugin.

## What it shows

![The lobby list, in the manager's own theme](docs/window.png)

One row per public lobby: planet, front, difficulty, players, mission, region, SOS, **ping**, host,
age. Every column but SOS sorts: click its name, click again to turn it round.

The filter row is one line of three groups, a hairline between them: what opens something
(Refresh, Planet, Difficulty, Mission, Presets), the three fronts as chips, and the four switches —
each a short word on the row (Public, Session, Not full, SOS) with the whole sentence in its tip,
so the row does not wrap into a ragged second line. A **planet** picked in the picker (search;
**Select all** / **Invert** for the rows the search shows; a front's chip takes all of its planets)
makes the scan ask Steam for exactly those planets; a
**mission** filter narrows the results already in hand, so it costs no scan. **Presets** keep a set of all of that under a name (planets, fronts,
difficulty, the switches, the missions, the sort) and put it back with one click; they live in
`presets.json` in the plugin's folder in the manager's settings. Each name has its own line with
**Load** and **Delete** beside it, so a long list costs one line a preset — a card of the manager's,
not a menu item per action.

**Refresh** shows its progress in the bottom bar on two lines: the stage with a real percentage
and a thin bar for the planet scan, and underneath what is actually happening ("Now: Mars · Steam
is returning the lobby list"). Only the planet scan has a real counter; reading the planet table,
opening Steam, parsing lobbies, the PlayFab lookup and the latency measurement show a moving bar
and *Working* instead of an invented percentage.

**Join** — bottom right, or a double-click, or Enter — opens
`steam://joinlobby/553850/<lobby>/<host>` and nothing else. When the game is closed, Steam itself
starts it with `+connect_lobby <lobby>` and the game joins as it boots; that is Valve's own
invitation path (`+connect_lobby` is the argument Helldivers 2's launch options use). Starting the
game first and joining afterwards does **not** work — the client then believes a game already runs
and hands the lobby to the running-game callback, in a process whose Steamworks has not started.

The ping column is **per room**, marked `≈`. A lobby publishes its host's location as keys 22/23
(longitude and latitude in hundredths of a degree — `-12230/3754` is San Francisco, `11415/2224`
Shenzhen), so rooms in the same region but in different cities can be told apart. The number is an
**estimate from that published coordinate and this PC's location**: Steam's own measured latency
to the region's closest relay is the anchor, and the estimate grows with the distance between the
two. Hosts whose published coordinates coincide legitimately show the same number — the lobby does
not publish a host IP, so this is not a measured round trip to the host and never pretends to be.

The short-lived scan helper calls Steamworks `ISteamNetworkingUtils` (`InitRelayNetworkAccess`,
`GetDirectPingToPOP` / `GetPingToDataCenter`, `GetPOPList`) while its Steam session is up and brings
the regional measurements back with the scan. A local TCP handshake or an accelerator accepting
connections locally cannot fake a 0/1 ms value here. This PC's location comes from one cached
lookup (`ipinfo.io`, then `ipwho.is`) in `location.json`, refreshed at most weekly; if neither
answers, the column falls back to the plain measured regional value. An accelerator that changes
your public exit IP can skew the estimate — the `≈` is the reminder that it is a geographic
estimate, not an observed connection.

Regions Steam did not measure fall back to the original's method: ICMP to `sdr_pops.json` nodes,
from the manager, cached five minutes. If a game accelerator's virtual adapter is up (奇游 or any
TUN/TAP one), that echo is sent **from the adapter's address** so the fallback follows the
accelerated route; an unanswered probe retries on the default route. The adapter is looked up when
the plugin loads — start the accelerator before opening the window. There is no process-name
disguise and no Winsock probe: nothing is written or run under the game's file name.

The **platform** column is the original's PlayFab lookup: a crossplay lobby names its host by
PlayFab entity id (lobby key 3), and that id's public profile object `testament_backing_platform`
says 0 = PlayStation, 1 = Steam, 4 = Xbox (its `testament_platform_user_name` fills the host name).
A lobby with crossplay off is Steam by definition and is never asked about. The lookups run in the
helper **after** its Steam session has closed — the session ticket it needs is taken first, and the
ticket outlives the session — for at most eight seconds a scan, with answers kept 30 minutes in
`platforms.json`. The **privacy** column is the lobby's own key 19: public, friends, private.

### The planet table, and its rate limit

`api.helldivers2.dev` allows **5 requests per 10 seconds** and says so in every answer
(`X-RateLimit-Limit`, `X-RateLimit-Remaining`, and `Retry-After` on a 429). One scan needs exactly
one request, and the plugin asks at most **once every five minutes**: the table is kept in
`planets.json` and reused across refreshes and restarts, and a refresh inside that window reads no
API at all. A 429 is obeyed to the second — `Retry-After`, or sixty seconds when it is not given —
and the status bar says so, with the local time it answers again, instead of retrying into a
longer ban:

> 12 个星球上共 465 个房间。· 星球表被限流，约 07:51:27 后恢复；本次用上次读到的星球表。

The table is read in the manager, not in the helper, so the scan itself makes no request but
Steam's.

The window speaks **HD2QD's own language**: the manager gives a tag when it opens the window, and
its settings file (`gui.json`, "lang") is read too, so a window opened after the manager's language
was changed follows it. The button at the right of the filter row switches this window between 中文
and English — or back to following HD2QD — and keeps the choice beside the presets.

The plugin ships an icon of its own (`src/app.ico`, a ringed planet against deep space in the manager's colours),
which the manager reads for the 20×20 picture on the plugin's card and for the content of a
title-bar button — a button the manager gives only to the plugins in its own official list
(`PluginFiles.HasButton` asks `OfficialOf`), so a plugin added from a file shows the icon on its
card and no title-bar button.

The window is drawn in the manager's own theme and never in WPF's: the ground and card of
`Ground900`/`Ground700`, `Ghost` and `Primary` buttons, `Chip` for the fronts, `Tick` for the
switches, banded rows with the chosen one filled yellow, the thin dark scrollbar. The theme is
taken from the running manager (`Application.Current.TryFindResource`); `Ui.xaml` holds the same
styles with their colours written out, for when it is not the manager drawing — a test window, or
an older manager.

The scan asks Steam for one planet at a time — the planets the game reports as active, from
`api.helldivers2.dev` — because Steam answers at most 50 lobbies per request. When that API cannot
be read, the scan falls back to one worldwide list.

## Install

1. Build it (below) or take `HD2QD.LobbyBrowser.dll` from a release.
2. In HD2 Mod Quick Deploy: **Plugins** page (the jigsaw piece beside the gear) → **Add plugin…**,
   or `hd2qd plugin-add <file>`.
3. Needs manager 0.15.0 or later (plugin Api 2), the Steam client signed in, and Helldivers 2
   installed — the plugin's helper program loads the game's own `steam_api64.dll` to talk to the
   Steam client (never the manager: see below).

## Why the scan runs in another process

Steam treats every process that initialises Steamworks for an App ID as part of that game: it
lists the app as running, and its **Stop** (in the library, or the close inside the game) ends
the processes it knows. HD2LobbyBrowser, which initialised Steamworks itself, was ended with the
game for that reason.

A plugin lives in the manager's process, so the same call would put HD2 Mod Quick Deploy itself
on that list — closing the game would close the manager, its deploy and this window.

So the manager never touches Steamworks. The DLL carries a small program of its own,
`HD2QD.LobbyBrowser.Helper.exe` (64-bit, .NET Framework, embedded in the DLL), writes it to the
manager's settings folder on first use and starts it for each scan. It is the helper that Steam
knows as Helldivers 2, and it lives for one scan: at worst, a Stop ends a scan, and the next
Refresh starts another.

**Steam still shows the game as running while a scan runs** — ten seconds or so. That is what
initialising Steamworks *means*: the lobby list has no read-only door (the same call is what Steam
idlers use to make a game look played). Only two things are ours to choose: how long the session
lives, and what pays for it. So the session covers the lobby requests alone — the planet table is
read before Steam is started — and it is the helper, never the manager, that a Stop ends. The
Refresh button's tip says as much, and a scan ended that way reports it in one line instead of
Steam's own banner.

## What it reads, what it writes, what it contacts

- **Reads:** `HKCU\Software\Valve\Steam` (`SteamPath`) and `libraryfolders.vdf` only to find
  `steam_api64.dll` in the game's `bin` folder — or the path in the `HD2_STEAM_API` environment
  variable. Then Steam's public lobby list and lobby data through that DLL. Not the game, not its
  memory, not its files.
- **Writes:** all in `%USERPROFILE%\.hd2-mod-cli\lobbybrowser\`: `HD2QD.LobbyBrowser.Helper.<version>.exe`,
  written from the DLL's own resource and replaced when the plugin updates; `presets.json`, the
  presets the player saved; `planets.json`, the last planet table and any rate-limit cooldown;
  `platforms.json`, the host platforms PlayFab named, with when it named them; `location.json`,
  this PC's approximate latitude/longitude for the room estimates, looked up at most weekly;
  `language.txt`, the window's language when it was set to something other than HD2QD's (no file:
  follow). No log.
- **Contacts:** the Steam client on this PC (local); `https://api.helldivers2.dev/api/v1/planets`
  for the planet table (once per five minutes, with the identifying headers that API asks for);
  PlayFab's `974a9` title (`https://974a9.playfabapi.com`) for host platforms, signed in with the
  Steam ticket, one profile request a crossplay host; `ipinfo.io` (then `ipwho.is`) for this PC's
  approximate location, once a week; Steam's relay network itself, for its own POP ping
  measurements; and the relay IPs in `sdr_pops.json`, reached by ICMP as a fallback.

## Not ported

Presets' single-slot behaviour (this keeps several, named), the lobby level column, the "unjoinable
lobby" blacklist, the global hotkey and the program's own updater: the manager runs the plugin, so
the plugin updates with the manager and the DLL.

## Build

```
build.bat            :: or: dotnet build src\HD2QD.LobbyBrowser.csproj -c Release
```

`release\HD2QD.LobbyBrowser.dll` is the plugin: one file, .NET Framework 4.8, no other file beside
it. `tests\Verify.exe <dll>` checks it against the plugin contract of
[docs/plugin-authors.md](https://github.com/ahe-meow/hd2qd-plugins/blob/main/docs/plugin-authors.md);
`--offline` skips the live scan, `--window` opens the window and writes `window.png`.

---

# HD2QD 大厅搜索器

[HD2 Mod Quick Deploy](https://github.com/pat-pat-patrick/hd2-mod-cli) 的插件：在管理器里列出
《绝地潜兵2》的公开大厅，自己挑房间进，不用随机匹配。

改编自 [HD2LobbyBrowser](https://github.com/QLongYan/HD2LobbyBrowser)（MIT）——大厅扫描、大厅键位
和名称表都来自该项目，移植为管理器插件。

## 能做什么

![管理器主题下的大厅列表](docs/window.png)

每个公开大厅一行：星球、战线、难度、人数、任务、地区、SOS、**延迟**、房主、建立时间。除 SOS 外每一列
都可排序：点列名升序，再点一次反向。

筛选行是一整行三组，组间一条细线：能展开的（刷新、星球、难度、任务、预设）、三条战线 chip、四个开关——
开关在行内用短词（公开房 / 有会话 / 未满员 / SOS），完整含义放在悬停提示里，这样一行不会被挤成参差的第二行。
**星球**选择器（可搜索，**全选 / 反选**只作用于搜索后可见的行，战线 chip 一键全选该战线）让扫描只向 Steam 要这些星球；**任务**筛选只作用于已有结果、
不重新扫描。**预设**把这些条件
（星球、战线、难度、开关、任务、排序）连同名字存起来，一键套回；存在管理器设置目录下插件自己的文件夹里的
`presets.json`。每个名字独占一行，**载入**与**删除**就在同一行右侧——预设再多也只占“每套一行”，
不随动作数量翻倍。

**刷新**时在底部状态栏显示两行进度：第一行是当前阶段、真实百分比和细进度条（星球扫描阶段），
第二行是正在做的事（例如"当前：玛尔特 · Steam 正在返回房间列表"）。只有星球扫描有真实计数；
读取星球表、连接 Steam、解析房间、查询房主平台、测量延迟这些阶段显示流动条和"进行中"，不编造百分比。

**加入**在右下角（也可双击行、回车）：只做一件事——打开 `steam://joinlobby/553850/<大厅>/<房主>`。
游戏没运行时，Steam 自己会用 `+connect_lobby <大厅>` 启动游戏，游戏启动过程中直接进房；这正是 Valve 文档里
的邀请流程（`+connect_lobby` 也是社区用来跳过《绝地潜兵2》过场动画的启动参数）。**先启动游戏再发 join 是行不通的**：
客户端会以为游戏已在运行，改走“运行中游戏回调”，而那个进程的 Steamworks 还没起来，请求就丢了。

延迟列现在是**每个房间一个值**，带 `≈` 标记。大厅把房主位置写在键 22/23 里（经度、纬度 ×100，
`-12230/3754` 是旧金山，`11415/2224` 是深圳），所以同一区域、不同城市的房间能区分开。
显示的数字是**根据这个房主坐标和本机位置推算的**：以 Steam 实测的该区域最近中继延迟为基准，
按两地距离增减。房主公布的坐标相同，数值自然相同——大厅不公布房主 IP，这不是到房主的实测往返，
`≈` 就是"地理估算"的标记，不冒充实测值。

短命的扫描 helper 在 Steam 会话仍开着时调用 Steamworks `ISteamNetworkingUtils`
（`InitRelayNetworkAccess`、`GetDirectPingToPOP` / `GetPingToDataCenter`、`GetPOPList`），把区域实测值随扫描结果带回。
本地 TCP 握手或加速器在本地先 accept 连接都造不出 0/1ms 的假值。本机位置来自每周最多一次的缓存查询
（`ipinfo.io`，失败则 `ipwho.is`），存在 `location.json`；两个服务都不可用时退回纯区域实测值。
如果加速器改变了你的公网出口 IP，估算会偏移——`≈` 就是提醒。

Steam 没测到的区域才回退到原项目的办法：对 `sdr_pops.json` 节点做 ICMP 探测，缓存五分钟，在管理器进程里进行。
如果检测到加速器（奇游或其他 TUN/TAP）的**虚拟网卡**已启用，这个兜底探测会以该网卡的地址为源地址发出，
从而走加速线路；探测无应答时回退默认路由。网卡在插件加载时识别，请先开加速器再打开大厅浏览器。
已彻底移除按进程名伪装的 Winsock 探测：插件不会再写入或以 `Helldivers2.exe` 为名运行任何文件。

**平台**列用的是原项目的 PlayFab 查询：跨平台房间用 PlayFab 实体 id 标识房主（大厅键 3），该 id 的公开资料对象
`testament_backing_platform` 给出 0=PlayStation、1=Steam、4=Xbox（`testament_platform_user_name` 顺带补上房主名）。
关闭跨平台的房间本身就是 Steam 房，不做查询。查询在 helper **关闭 Steam 会话之后**进行（需要的 Steam 票据在关闭前
取好，票据不会随会话失效），每次扫描最多八秒，答案在 `platforms.json` 里保留 30 分钟。**隐私**列是大厅自己的键 19：
公开 / 好友 / 私密。

窗口说的是 **HD2QD 自己的语言**：管理器打开窗口时会给出语言标签，插件还会读它的设置文件（`gui.json` 的
"lang"），所以在管理器里改过语言之后再打开的窗口会跟着变。筛选行最右侧的按钮把**本窗口**在 中文 / English 之间
切换，或切回“跟随 HD2QD”，选择与预设放在一起。

插件自带一个图标（`src/app.ico`：管理器配色下的“深空带环行星”），管理器用它画插件卡片上的 20×20 图标，
也用作标题栏按钮的内容——但**标题栏按钮只给官方清单里的插件**（`PluginFiles.HasButton` 要查 `OfficialOf`），
从文件添加的插件只有卡片图标，没有标题栏按钮。

窗口用的是管理器自己的主题，不是 WPF 默认控件：`Ground900`/`Ground700` 的底与卡片、`Ghost` 与
`Primary` 按钮、`Chip` 战线、`Tick` 开关、隔行底色与选中行黄底、细窄深色滚动条。主题优先取运行中的
管理器（`Application.Current.TryFindResource`）；`Ui.xaml` 是同一套样式写明颜色的副本，供非管理器
绘制时使用（测试窗口、旧版管理器）。

### 星球表与它的限流

`api.helldivers2.dev` 限制 **每 10 秒 5 次**，并在每个响应里写明（`X-RateLimit-Limit`、`X-RateLimit-Remaining`，
429 时给 `Retry-After`）。一次扫描只需要一次请求，插件最多**每五分钟**请求一次：星球表存在 `planets.json` 里，
刷新与重启都复用，这五分钟内的刷新完全不碰接口。真的吃到 429 就按 `Retry-After`（没给就 60 秒）等满，
状态栏写明本地恢复时间，而不是重试到更长的封禁：

> 12 个星球上共 465 个房间。· 星球表被限流，约 07:51:27 后恢复；本次用上次读到的星球表。

星球表在管理器里读、不在 helper 里读，所以扫描本身除 Steam 外不发任何请求。

## 安装

1. 自行构建（见下），或从 release 取 `HD2QD.LobbyBrowser.dll`。
2. 在 HD2 Mod Quick Deploy 的**插件**页（齿轮旁的拼图按钮）点“添加插件…”，或
   `hd2qd plugin-add <文件>`。
3. 需要管理器 0.15.0 以上（插件 Api 2）、Steam 客户端已登录、已安装《绝地潜兵2》——由插件的
   助手程序加载游戏自带的 `steam_api64.dll` 与 Steam 客户端通信（管理器本身不加载，见下）。

## 为什么扫描放在另一个进程里

Steam 会把**任何**为某个 App ID 初始化 Steamworks 的进程当作该游戏的一部分：库里显示“运行中”，
点“停止”（或游戏内关闭）时，它认识的那些进程会被一起结束。HD2LobbyBrowser 自己初始化了
Steamworks，所以关游戏时它也被关掉了。

插件运行在管理器的进程里，同样的调用会把 **HD2 Mod Quick Deploy 自己**写进那份名单——于是关游戏
就连带关掉管理器、正在进行的部署和这个窗口。

所以管理器不碰 Steamworks：DLL 里带着一个自己的小程序 `HD2QD.LobbyBrowser.Helper.exe`
（64 位 .NET Framework，作为资源嵌在 DLL 内），首次使用时写到管理器的设置目录，每次扫描启动一次。
被 Steam 认作《绝地潜兵2》的是这个助手，它只活一次扫描：最坏情况是点“停止”时这次扫描被结束，
再点刷新就重来。

**扫描期间 Steam 仍会把游戏显示为“运行中”**，大约十秒。这是“初始化 Steamworks”这个动作本身的含义——
大厅列表没有只读通道（Steam 挂机工具正是靠这一点让游戏看起来在运行）。能选的只有两件事：会话活多久、
由谁承担后果。所以会话只覆盖取大厅的那几秒——星球表在启动 Steam 之前就读好了——而被“停止”结束的
永远是助手，不是管理器。刷新的悬停提示写明了这一点；这样被结束的扫描只会显示一行说明，而不是
Steam 自己那串 breakpad 文本。

## 读写与联网

- **读**：`HKCU\Software\Valve\Steam`（`SteamPath`）与 `libraryfolders.vdf`，只为找到游戏
  `bin` 目录里的 `steam_api64.dll`（也可用环境变量 `HD2_STEAM_API` 指定）。随后通过该 DLL 读取
  Steam 的公开大厅列表与大厅数据。不读游戏进程、不读内存、不改游戏文件。
- **写**：都在管理器设置目录 `%USERPROFILE%\.hd2-mod-cli\lobbybrowser` 下：`HD2QD.LobbyBrowser.Helper.<版本>.exe`
  （来自 DLL 内嵌资源，升级时替换）、`presets.json`（玩家保存的预设）、`planets.json`（上次的星球表与限流冷却）、
  `platforms.json`（PlayFab 给出的房主平台及时间）、`location.json`（本机大致经纬度，用于房间延迟估算，
  每周最多查一次）、`language.txt`（本窗口的语言，跟随管理器时不写）。不写日志。
- **联网**：本机 Steam 客户端（本地）；`https://api.helldivers2.dev/api/v1/planets`（星球表，最多每五分钟一次，
  带该接口要求的标识头）；PlayFab 的 `974a9` 标题（`https://974a9.playfabapi.com`）查房主平台——用 Steam 票据
  登录，每个跨平台房主一次资料请求；`ipinfo.io`（失败则 `ipwho.is`）查本机大致位置，每周最多一次；
  Steam 中继网络本身（Steam 自测各 POP 的延迟）；以及 `sdr_pops.json` 里的中继 IP——仅在 Steam
  未提供测量时用 ICMP 兜底。

## 未移植

预设的“只存一套”行为（这里存多套并各自命名）、大厅等级列、“进不去的房”黑名单、全局热键、程序自身的更新器：
插件由管理器加载，随管理器与 DLL 更新。

## 构建

```
build.bat            :: 或：dotnet build src\HD2QD.LobbyBrowser.csproj -c Release
```

`release\HD2QD.LobbyBrowser.dll` 即插件：单文件、.NET Framework 4.8，旁边不需要任何其他文件。
`tests\Verify.exe <dll>` 按
[docs/plugin-authors.md](https://github.com/ahe-meow/hd2qd-plugins/blob/main/docs/plugin-authors.md)
检查契约；`--offline` 跳过真实扫描，`--window` 打开窗口并写出 `window.png`。
