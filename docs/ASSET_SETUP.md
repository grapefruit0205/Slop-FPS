# Preparing a playable asset project / 에셋 구성 / 资源配置

**한국어:** 공개 저장소에는 운영판의 씬, 아바타 프리팹, 캐릭터와 애니메이션이 없습니다. 자동 복구 도구나 한 번의 메뉴 클릭으로 완성되는 프로젝트가 아닙니다. 아래는 직접 에셋을 구성할 개발자를 위한 연결 규약입니다. Mixamo 등을 본인 계정에서 얻어 사용하는 경우에도 원본 파일을 공개 저장소에 올리지 마세요.

**简体中文：** 本仓库未包含线上版本的场景、玩家预制体、角色和动画，也没有一键恢复完整游戏的工具。下方是为开发者提供的资源连接规范。即使从自己的账号下载 Mixamo 等资源，也不要把受限制的原始文件提交到公开仓库。

## What is ready

Game/network/UI/editor code, translations, FishNet/Bayou, the GameLift SDK, CC0 weapon resources and OFL fonts are included. Backend tests and the asset-independent `GameplayChecks` UI test can run without production maps/characters. The public movement controller is an original minimal implementation, not a copy of Unity Standard Assets.

Open in Unity 2022.3.55f1, allow package resolution, and run **Tools → FPS → Import TMP Essentials**. Keep locally generated `Assets/TextMesh Pro/` out of commits. Font assets retain their original GUIDs; use **Setup UI Fonts** if regenerating them. There is deliberately no enabled game scene in Build Settings.

## Avatar contract

Read `PlayerNetworkMover`, `PlayerHealth`, `PlayerStance`, `FpsGun`, `TpsGun`, and `GunModels` when constructing your own prefabs; they expose serialized references that must be assigned.

The existing `NetSetup` tool expects three avatar paths:

- `Assets/Prefabs/Resources/PolicemanController.prefab`
- `Assets/Prefabs/Resources/RobotXController.prefab`
- `Assets/Prefabs/Resources/RobotYController.prefab`

These names are compatibility paths, not included character assets. You can use your own licensed or procedural characters, or adapt the tool's paths.

1. Create a root with `CharacterController`, a kinematic `Rigidbody`, `SlopFPS.Input.FirstPersonController`, `PlayerHealth`, and `PlayerNetworkMover`. Use the project's `Player` tag and player layers from `ProjectSettings/TagManager.asset`.
2. Add a child first-person camera, character model/Animator, third-person gun holder, and name tag. Assign the mover's `animator`, `cameraObject`, `gunObject`, `playerObject`, and `nameTag` fields. Assign health audio/Animator references.
3. Give the Animator float parameters `Horizontal`, `Vertical`, bools `Running`, `Firing`, triggers `IsJumping`, `IsHurt`, `IsDead`, and int `Stance`. Supply your own animation states. Humanoid hand IK is optional; its script is included but no rig is supplied.
4. On first-person and third-person guns configure `GunModels` (model and muzzle/grip/handguard/rail/sight transforms) using the supplied CC0 weapons. Assign `FpsGun` camera, animator, line, particle and third-person gun references; configure `TpsGun` effects/audio. See [weapon resource notes](../Assets/Art/Military/Weapons/WEAPONS.md).
5. Configure name tag `Text` and optional impact effect prefabs. Missing visual-effect references must be handled or replaced before playtesting.

`NetSetup.Apply` adds/configures FishNet components, loadout, stance and movement validation, writes networking prefabs, and registers spawnable objects. It **does not create a complete avatar from nothing**.

## Scene and maps

Create `Assets/Scenes/Start.unity` with a `GameNetwork`, a scene camera, player spawn transforms, lights, and your own collidable arena. Assign `GameNetwork`'s spawn points, scene camera and three avatar model prefabs. Add the scene to Build Settings.

Run **Tools → FPS → Network Setup (FishNet)** only after the avatar prefabs and scene exist. It configures the `Network` root with FishNet, Bayou, authentication and `GameServer`, plus the `NetPlayer` and `MatchManager` prefabs.

`MapLoader` captures the scene as the `warehouse` map. Its warehouse bounds/spawn validation assume the production layout; adapt `MapBounds.ConfigureWarehouse` to your own arena. For a second map, create a `MapDefinition` prefab and a Resources `MapCatalog` asset; inspect `MapDefinition` and `MapLoader`. Production Outpost geometry is excluded. Old import tools such as `MixamoImport`, `SoldierAvatars`, `WeaponRig` and `MapBuilder_Outpost` expect missing inputs and are **not** first-run setup commands.

Check team spawn assignment, grounding, collision layers, movement validation, gun origins, damage and respawns with two local clients before enabling a public server. Use protocol-matching server and client builds.

## Local server after asset integration

Build through the included Linux player and Linux dedicated-server menu commands, then run:

```bash
./Builds/Server/FPSServer.x86_64 -batchmode -nographics -server -nogamelift -port 7770 -mode tdm -map warehouse
./Builds/Linux/FPS.x86_64 -connect 127.0.0.1:7770 -name PlayerOne
./Builds/Linux/FPS.x86_64 -connect 127.0.0.1:7770 -name PlayerTwo
```

Start threshold is 2, maximum capacity is 8, and protocol is 7. These commands require the scene and avatar integration above; no successful full-game build of this stripped edition is claimed.

Excluded art directories are ignored deliberately. If you create your own redistributable replacements, review their licenses and narrow the ignore rules before committing them.
