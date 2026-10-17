# 히메짱 옷장 정리기 (KRWardrobeCleaner)

FF14 **한국 서버**용 Dalamud 플러그인. 환상의 옷장·장비함·가방 사이의 장비 정리를 자동화합니다.

- Dalamud API 15 / .NET 10 / x64
- 명령어: `/kwc`

## 설치

Dalamud 설정 → 실험적 기능 → **사용자 정의 플러그인 저장소**에 아래 주소를 추가하세요.
히메짱 WHM(Rotation Solver Reborn 포크)과 **같은 주소**이므로, 이미 등록했다면 추가로 할 일이 없습니다.

```
https://raw.githubusercontent.com/pancakeLab/Himechan_addon/himechan/pluginmaster.json
```

플러그인 설치 창에서 "히메짱 옷장 정리기"를 검색해 설치합니다.
테스트 빌드를 받으려면 Dalamud 설정에서 "플러그인 테스트 버전 받기"를 켜고, 설치 창에서 이 플러그인을 우클릭해 테스트 버전 수신을 켜세요.

## 기능 (탭 순서)

| 탭 | 하는 일 |
|---|---|
| 1. 옷장 → 가방 | 추억의 보관함에 넣을 수 있는 아이템이 환상의 옷장에 있으면 가방으로 꺼냄 |
| 1-2. 옷장 중복 | 환상의 옷장에 두 벌 이상 든 아이템(세트 안에 있는 조각 포함)을 찾아 중복분을 가방으로 꺼냄 |
| 2. 가방 → 옷장 | 장비함·가방의 장비를 환상의 옷장에 넣음(기존 세트 보충 → 세트 우선 → 단벌). 내구도 100% 미만 자동 제외 |
| 3. 장비함 ↔ 가방 | 장비함 → 가방(전체/카테고리, 4번 가방만, 장비세트 제외) / 가방 → 장비함(부위별 자동) |
| 4. 자동 파기 | 체크한 장비 또는 4번 가방 전체를 파기(확인 창 자동 '예') |

2·3번 탭은 **직업 / 착용 레벨 / 환상의 옷장 보유** 필터를 공유합니다. 켜진 필터를 위에서 아래로 그리고(AND)·또는(OR)로 묶고, 조건에 맞는 장비가 자동으로 체크됩니다.

디버그 로그를 켜면 `/xllog`에 `[KWC]` 접두어로 후보 판정·요청·검증 결과가 남습니다.

## 빌드

```
dotnet build KRWardrobeCleaner/KRWardrobeCleaner.csproj -c Release
```

Dalamud 개발 파일(`%APPDATA%\XIVLauncher\addon\Hooks\dev`)이 필요합니다. 결과물은 `KRWardrobeCleaner/bin/Release/`.

## 배포

- GitHub Actions `Build and Release`: 푸시/PR은 빌드만, **Run workflow**에서 `release`를 켜면 `vX.Y.Z.W` 릴리스를 만듭니다(`prerelease` = 테스트 채널용).
- Dalamud 저장소 파일(`pluginmaster.json`)은 [pancakeLab/Himechan_addon](https://github.com/pancakeLab/Himechan_addon)에 있으며, 그 저장소의 `.github/himechan/wardrobe-release.ps1`로 갱신합니다(`-Testing`이면 테스트 채널만).
