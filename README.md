# Slop FPS

[한국어](README.md) · [English](README.en.md) · [简体中文](README.zh-CN.md)

Unity로 만든 브라우저 멀티플레이어 FPS의 **공개 소스 저장소**입니다. [Armour/Multiplayer-FPS](https://github.com/Armour/Multiplayer-FPS)를 기반으로 게임 규칙, FishNet 네트워킹, 무장 선택, 다국어 UI와 AWS 배포 구성을 확장했습니다.

**[브라우저에서 플레이](https://d34odl5jhuo0lc.cloudfront.net/)** · [이슈 / 피드백](https://github.com/grapefruit0205/Slop-FPS/issues)

> 이 저장소는 소스 공개용 에디션입니다. 배포 중인 게임의 캐릭터, 애니메이션, 맵, 플레이어 프리팹은 재배포 권한 문제로 포함하지 않았습니다. **클론한 뒤 바로 실행하거나 빌드할 수 있는 완성 Unity 프로젝트는 아닙니다.** 아래의 에셋 구성 안내를 확인해 주세요. 브라우저 데모는 이미 빌드된 게임입니다.

## 게임 특징

- **2명부터 경기 시작, 방당 최대 8명**. 시작 후에도 정원 내에서 추가 입장할 수 있습니다. 섬멸전의 늦은 참가자는 다음 라운드부터 출전합니다.
- 팀 데스매치(TDM), 개인전(FFA), 섬멸전(Elimination).
- AK-47, MP5, M700, M870과 M1911 권총. 무기별로 허용되는 기계식 조준기 / 레드도트 / 4배 / 8배 조준경.
- 로비, Esc 설정, 사망 화면에서 다음 무장을 선택합니다. 경기 중 변경은 **다음 부활 또는 다음 라운드**에 적용됩니다.
- 한국어, 영어, 중국어 간체 UI. 로비와 설정 메뉴에서 변경하며 선택을 저장합니다. URL의 `?lang=ko`, `?lang=en`, `?lang=zh`도 지원합니다.
- 전용 서버가 경기 상태, 점수, 피해와 탄약을 검증합니다. 이 프로젝트는 실험적인 FPS이며 개선 제안을 환영합니다.

## 조작

| 입력 | 동작 |
|---|---|
| WASD / 마우스 | 이동 / 시점 |
| 좌클릭 / 우클릭 / R | 발사 / 조준 / 재장전 |
| 1, 2 / 마우스 휠 | 현재 주무기와 권총 전환 |
| Shift / Space | 달리기 / 점프 |
| C / Z / Q, E | 앉기 / 엎드리기 / 기울이기 |
| G / Esc | 수류탄 선택 순환 / 설정·무장·언어 |

## 저장소 구조

| 경로 | 내용 |
|---|---|
| `Assets/Scripts/Game/` | 경기 규칙, 점수, 라운드와 관전 |
| `Assets/Scripts/Net/` | FishNet 연결, GameLift, 입장 인증과 서버 검증 |
| `Assets/Scripts/Weapons/`, `FpsGun.cs` | 무장, 조준경, 사격과 탄약 |
| `Assets/Scripts/UI/`, `Assets/Resources/localization.json` | 런타임 UI와 3개 언어 번역 |
| `Assets/Scripts/Input/` | 공개용으로 새로 작성한 최소 이동·입력 컨트롤러 |
| `Assets/Editor/` | 에셋 구성, 네트워크 설정, 빌드와 UI 검증 도구 |
| `Assets/FishNet/` | FishNet Free 및 Bayou; 각각의 라이선스 유지 |
| `Assets/Art/Military/Weapons/`, `Assets/UI/` | CC0 총기 에셋과 OFL 폰트 |
| `Server/backend/`, `Server/infra/`, `Server/deploy/` | 로비 API, CloudFormation과 배포 스크립트 |

## 개발 및 실행

Unity **2022.3.55f1**, Node.js **22 이상**, Linux 서버/WebGL 빌드 모듈을 사용합니다.

로비 백엔드는 Unity 에셋 없이 테스트할 수 있습니다.

```bash
git clone https://github.com/grapefruit0205/Slop-FPS.git
cd Slop-FPS/Server/backend
npm ci
npm test
```

Unity 소스 검토 및 UI 테스트: 프로젝트를 연 후 `Tools → FPS → Import TMP Essentials`를 실행합니다. 게임 실행에는 [에셋 구성 안내](docs/ASSET_SETUP.md)에 따라 직접 만든 맵, 아바타 프리팹과 애니메이션을 연결해야 합니다. 공개용 컨트롤러는 기존 Unity Standard Assets를 재배포하지 않도록 별도로 작성했으며, 배포판의 이동 연출을 완전히 재현하지 않습니다.

```bash
# Unity 실행 파일의 경로를 환경에 맞게 지정하세요.
"$UNITY_EDITOR" -batchmode -nographics -projectPath . -executeMethod ProjectSetup.ImportTmpEssentials
# UI 검사: 스스로 종료하므로 -quit을 붙이지 않습니다.
"$UNITY_EDITOR" -batchmode -nographics -projectPath . -executeMethod GameplayChecks.Run
```

AWS 구성은 WebGL → S3/CloudFront, 로비 → API Gateway/Lambda, 게임 서버 → GameLift(FishNet/Bayou WebSocket TLS)입니다. [자체 배포 안내](Server/README.md)를 참고하세요. 계정 키, 인증서, 운영 설정과 기존 저장소의 Git 이력은 포함하지 않았습니다. 글로벌 CloudFront 데모의 중국 본토 접속 품질은 보장하지 않으며, 현지에서 다운로드·로비·게임 연결을 각각 확인해야 합니다.

## 라이선스와 출처

게임 코드의 원본은 Chong Guo의 [Multiplayer-FPS](https://github.com/Armour/Multiplayer-FPS)이며 원저작권과 [MIT LICENSE](LICENSE)를 유지합니다. 공개용 코드 수정도 MIT로 제공합니다. **의존성과 에셋에는 각자의 라이선스가 적용됩니다.** FishNet은 자체 라이선스, Bayou는 MIT, GameLift SDK는 Apache-2.0, 포함된 총기 에셋은 CC0, 폰트는 SIL OFL입니다. [전체 출처 및 제외 범위](THIRD_PARTY_NOTICES.md)를 확인하세요.

버그 제보에는 게임 모드, 무기/조준경, 브라우저와 재현 순서를 적어 주세요. 에셋 기여는 공개 재배포가 허용되는 자료에 한합니다.
