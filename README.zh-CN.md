# Slop FPS

[한국어](README.md) · [English](README.en.md) · [简体中文](README.zh-CN.md)

这是使用 Unity 开发的浏览器多人 FPS 的**公开源码版本**。项目基于 [Armour/Multiplayer-FPS](https://github.com/Armour/Multiplayer-FPS)，扩展了比赛规则、FishNet 联网、武器配置、多语言界面及 AWS 部署工具。

**[在浏览器中游玩](https://d34odl5jhuo0lc.cloudfront.net/)** · [问题反馈](https://github.com/grapefruit0205/Slop-FPS/issues)

> 本仓库用于公开源码。线上游戏的角色、动画、地图和玩家预制体未包含在内，因为相关原始资源不允许自由再分发，或其再分发权限尚未确认。**克隆后不能直接运行或构建完整的 Unity 游戏。** 请参阅下方资源配置说明。浏览器演示是已编译的游戏。

## 游戏特色

- **2 人即可开始，每个房间最多 8 人**。比赛开始后仍可在有空位时加入；歼灭模式中途加入的玩家从下一回合开始参战。
- 团队死亡竞赛（TDM）、自由混战（FFA）和歼灭模式（Elimination）。
- AK-47、MP5、M700、M870，以及 M1911 手枪。根据武器支持情况选择机械瞄具、红点、4 倍或 8 倍瞄准镜。
- 在大厅、Esc 设置或死亡界面选择装备。比赛中更改装备将在**下次重生或下一回合**生效。
- 韩语、英语、简体中文界面；语言选择会保存。网页参数支持 `?lang=ko`、`?lang=en`、`?lang=zh`。
- 专用服务器验证比赛状态、分数、伤害和弹药。这是一款实验性 FPS，欢迎反馈与改进。

## 操作

| 输入 | 操作 |
|---|---|
| WASD / 鼠标 | 移动 / 视角 |
| 鼠标左键 / 右键 / R | 射击 / 瞄准 / 换弹 |
| 1、2 / 鼠标滚轮 | 切换当前主武器和手枪 |
| Shift / Space | 冲刺 / 跳跃 |
| C / Z / Q、E | 蹲下 / 趴下 / 侧倾 |
| G / Esc | 循环选择投掷物 / 设置、装备、语言 |

## 仓库结构

| 路径 | 内容 |
|---|---|
| `Assets/Scripts/Game/` | 比赛规则、分数、回合和观战 |
| `Assets/Scripts/Net/` | FishNet、GameLift、认证和服务器验证 |
| `Assets/Scripts/Weapons/`、`FpsGun.cs` | 装备、瞄准镜、射击和弹药 |
| `Assets/Scripts/UI/`、`Assets/Resources/localization.json` | 运行时界面与三种语言翻译 |
| `Assets/Scripts/Input/` | 为公开版独立编写的最小移动及输入控制器 |
| `Assets/Editor/` | 资源工具、网络配置、构建和界面检查 |
| `Assets/FishNet/` | FishNet Free 和 Bayou，保留各自许可证 |
| `Assets/Art/Military/Weapons/`、`Assets/UI/` | CC0 武器资源与 OFL 字体 |
| `Server/backend/`、`Server/infra/`、`Server/deploy/` | 大厅 API、CloudFormation 与部署脚本 |

## 开发

需要 Unity **2022.3.55f1**、Node.js **22 或更新版本**，以及 Linux 专用服务器和 WebGL 构建模块。

大厅后端可以独立测试，无需 Unity 资源：

```bash
git clone https://github.com/grapefruit0205/Slop-FPS.git
cd Slop-FPS/Server/backend
npm ci
npm test
```

查看 Unity 源码和测试界面时，请打开项目并运行 `Tools → FPS → Import TMP Essentials`。运行完整游戏前，需要按照[资源配置说明](docs/ASSET_SETUP.md)制作并连接自己的地图、角色预制体和动画。公开版控制器是独立编写的，避免再分发 Unity Standard Assets；它没有完全复现线上版本的移动表现。

```bash
# 将 UNITY_EDITOR 设置为 Unity 可执行文件路径，并从仓库根目录运行。
"$UNITY_EDITOR" -batchmode -nographics -projectPath . -executeMethod ProjectSetup.ImportTmpEssentials
# 界面检查会自行退出，不要添加 -quit。
"$UNITY_EDITOR" -batchmode -nographics -projectPath . -executeMethod GameplayChecks.Run
```

部署结构：WebGL 使用 S3/CloudFront，大厅使用 API Gateway/Lambda，专用游戏服务器使用 GameLift，通过 FishNet/Bayou 的 WebSocket TLS 通信。详见[自行部署](Server/README.md)。本仓库不包含密钥、证书、线上配置及旧 Git 历史。中国大陆的访问速度与稳定性不作保证；资源下载、大厅请求和游戏连接需要分别在当地验证。

## 许可证与致谢

原始游戏代码来自 Chong Guo 的 [Multiplayer-FPS](https://github.com/Armour/Multiplayer-FPS)，保留原版权声明与 [MIT 许可证](LICENSE)。公开游戏代码的修改也按 MIT 发布。**依赖库和资源仍遵循各自的许可证。** FishNet 使用其独立许可证，Bayou 为 MIT，GameLift SDK 为 Apache-2.0，附带武器资源为 CC0，字体为 SIL OFL。请参阅[第三方声明及排除范围](THIRD_PARTY_NOTICES.md)。

反馈问题时，请提供游戏模式、武器及瞄准镜、浏览器和复现步骤。提交资源时，请确认允许公开再分发原始资源。
