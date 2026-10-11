# Self-hosting Slop FPS

[한국어 README](../README.md) · [English](../README.en.md) · [简体中文](../README.zh-CN.md)

한국어: 이 문서는 **본인 AWS 계정**의 신규 환경 배포 안내입니다. 게임 씬/아바타 구성과 빌드가 먼저 필요합니다. 운영판의 AWS 설정은 포함하지 않았습니다.

简体中文：此文档用于在**自己的 AWS 账号**中部署新环境。请先完成游戏场景和玩家预制体配置并构建。仓库不含线上部署配置。

```text
WebGL browser --HTTPS--> CloudFront --> private S3 bucket
              --HTTPS--> API Gateway --> Lambda lobby --> GameLift sessions
              --WSS----> GameLift Linux dedicated server (FishNet / Bayou)
```

## Prerequisites

- Complete [Unity asset integration](../docs/ASSET_SETUP.md) and test a local server plus two clients first.
- Unity 2022.3.55f1 with WebGL and Linux dedicated-server modules; Node.js 22+, npm, AWS CLI v2, Bash, jq, zip.
- Your own AWS credentials/profile, suitable permissions and GameLift quotas. AWS resources incur charges; capacity and budget limits are in `deploy/config.env.example`.
- A matching client/server protocol (currently 7). Start threshold 2 and maximum room capacity 8 are separate settings.

## Backend checks

```bash
cd Server/backend
npm ci
npm test
```

## Prepare configuration

```bash
cp Server/deploy/config.env.example Server/deploy/config.env
```

Edit `AWS_PROFILE`, `AWS_REGION`, `EXPECTED_ACCOUNT_ID`, project/stack names, fleet capacity, allowed web origins and budget settings for your own account. Budgets send alerts; they do not stop spending. `config.env` is ignored. The IAM example uses **123456789012**, a placeholder; replace it and review its resource scopes before applying. It is a deployment policy example, not a universal least-privilege role.

Do not commit credentials, GameLift session tokens, TLS private keys, deployment output, or player data. Backend CORS origins and build-time API URLs must refer to your own environment.

## Build and deploy

After the Unity scene/assets are complete, build the Linux dedicated server:

```bash
"$UNITY_EDITOR" -batchmode -nographics -quit -projectPath . -executeMethod GameBuilds.BuildServer
bash Server/deploy/deploy-server.sh --help
bash Server/deploy/deploy-server.sh Builds/Server 0.3.1
bash Server/deploy/deploy-backend.sh
```

`deploy-server.sh` uploads `Builds/Server`, prepares the GameLift fleet and alias, and waits for readiness. Inspect its help and account checks first. Read the backend stack outputs to obtain **your own** API Gateway URL, then build and deploy WebGL:

```bash
"$UNITY_EDITOR" -batchmode -nographics -quit -projectPath . -executeMethod GameBuilds.BuildWebGL -apiBase https://YOUR_API_ID.execute-api.YOUR_REGION.amazonaws.com
bash Server/deploy/deploy-web.sh Builds/WebGL
bash Server/deploy/budget.sh
```

Consult individual script help and `config.env.example` for optional arguments. On the first deployment, run `deploy-web.sh --stack-only` before `deploy-backend.sh` so the generated CloudFront origin is included in backend CORS, or redeploy the backend after creating the web stack. No deployment commands run automatically on GitHub pushes. Web clients served over HTTPS need a **WSS** game server with a valid certificate; GameLift integration obtains fleet TLS files at runtime. For local TLS testing, `-tlsdir` expects private certificate/key files outside Git.

## Validate and manage

Verify browser loading, room listing, one-player warmup, a match starting at two players, later joins up to eight, respawn loadout changes, and all three UI languages. In Elimination, late joiners wait for the next round.

`set-capacity.sh` adjusts fleet capacity subject to configured caps. `teardown.sh` removes resources; review its help and target account before using it. This public source edition does not include existing deployment identifiers or a production migration/rollback plan.

Global CloudFront hosting does not guarantee mainland China connectivity. Check download, HTTPS lobby, WSS session connection and latency separately from the target location.
