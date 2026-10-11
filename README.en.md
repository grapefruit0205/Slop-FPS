# Slop FPS

[한국어](README.md) · [English](README.en.md) · [简体中文](README.zh-CN.md)

A **public source edition** of a browser multiplayer FPS built with Unity. Based on [Armour/Multiplayer-FPS](https://github.com/Armour/Multiplayer-FPS), extended with match rules, FishNet networking, loadouts, multilingual UI, and AWS deployment tooling.

**[Play in your browser](https://d34odl5jhuo0lc.cloudfront.net/)** · [Issues / feedback](https://github.com/grapefruit0205/Slop-FPS/issues)

Interested in development and technology? Visit the **[LINUX.DO](https://linux.do/)** community.

> This is a source edition. Production characters, animations, maps, and player prefabs are omitted because their source assets cannot be freely redistributed or their permissions were not verified. **A fresh clone is not a complete, immediately playable or buildable Unity project.** See the asset setup guide below. The browser demo is an already compiled game.

## Features

- Matches start with **2 players**, with **up to 8 per room**. Players can join an ongoing match while slots remain; late joiners in Elimination enter the next round.
- Team Deathmatch (TDM), Free-for-All (FFA), and Elimination.
- AK-47, MP5, M700, M870, and M1911 pistol. Iron sights, red dot, 4×, and 8× optics where supported by the weapon.
- Select a loadout in the lobby, Esc settings, or death screen. Changes during a match apply on the **next respawn or next round**.
- Korean, English, and Simplified Chinese UI, with a saved language selection. Web URLs also accept `?lang=ko`, `?lang=en`, and `?lang=zh`.
- Dedicated servers validate match state, scores, damage, and ammunition. This is an experimental FPS; improvements and feedback are welcome.

## Controls

| Input | Action |
|---|---|
| WASD / mouse | Move / look |
| Left / right mouse / R | Fire / aim / reload |
| 1, 2 / mouse wheel | Switch equipped primary and pistol |
| Shift / Space | Sprint / jump |
| C / Z / Q, E | Crouch / prone / lean |
| G / Esc | Cycle grenade selection / settings, loadout, language |

## Layout

| Path | Purpose |
|---|---|
| `Assets/Scripts/Game/` | Match rules, scores, rounds, spectating |
| `Assets/Scripts/Net/` | FishNet, GameLift, authentication, server validation |
| `Assets/Scripts/Weapons/`, `FpsGun.cs` | Loadouts, optics, firing, ammunition |
| `Assets/Scripts/UI/`, `Assets/Resources/localization.json` | Runtime UI and three translations |
| `Assets/Scripts/Input/` | Original minimal movement/input controller for this edition |
| `Assets/Editor/` | Asset tools, networking setup, builds, UI checks |
| `Assets/FishNet/` | FishNet Free and Bayou, with their licenses |
| `Assets/Art/Military/Weapons/`, `Assets/UI/` | CC0 weapon assets and OFL fonts |
| `Server/backend/`, `Server/infra/`, `Server/deploy/` | Lobby API, CloudFormation, deployment scripts |

## Development

Use Unity **2022.3.55f1**, Node.js **22+**, and the Linux server/WebGL build modules.

The lobby backend can be tested independently of Unity assets:

```bash
git clone https://github.com/grapefruit0205/Slop-FPS.git
cd Slop-FPS/Server/backend
npm ci
npm test
```

For Unity source review and UI checks, open the project and run `Tools → FPS → Import TMP Essentials`. To run the game, create and wire your own maps, avatar prefabs, and animations following [Asset setup](docs/ASSET_SETUP.md). The public controller was written separately to avoid redistributing Unity Standard Assets; it does not reproduce all movement presentation from the hosted edition.

```bash
# Set UNITY_EDITOR to your Unity executable; run from the repository root.
"$UNITY_EDITOR" -batchmode -nographics -projectPath . -executeMethod ProjectSetup.ImportTmpEssentials
# This UI check exits itself; do not add -quit.
"$UNITY_EDITOR" -batchmode -nographics -projectPath . -executeMethod GameplayChecks.Run
```

Hosting: S3/CloudFront for WebGL, API Gateway/Lambda for the lobby, and GameLift dedicated servers with FishNet/Bayou WebSocket TLS. See [Self-hosting](Server/README.md). Credentials, certificates, production configuration, and old Git history are excluded. Mainland China performance is not guaranteed; asset loading, lobby requests, and game connections require separate local checks.

## License and credits

The original game code is Chong Guo's [Multiplayer-FPS](https://github.com/Armour/Multiplayer-FPS); its copyright and [MIT license](LICENSE) are retained. Changes to the public game code are also MIT. **Dependencies and assets retain their own licenses.** FishNet has its own license, Bayou is MIT, GameLift SDK is Apache-2.0, bundled weapon art is CC0, and fonts are SIL OFL. See [Notices and exclusions](THIRD_PARTY_NOTICES.md).

When reporting a bug, include the mode, weapon/optic, browser, and reproduction steps. Asset contributions must permit public source redistribution.
