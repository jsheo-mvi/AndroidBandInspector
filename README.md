# AndroidBandInspector

USB로 연결된 Android 스마트폰에서 ADB로 접근 가능한 radio band 증거를 수집하는 읽기 전용 C# 콘솔 도구입니다.

Repository: <https://github.com/jsheo-mvi/AndroidBandInspector>

License: GNU General Public License v3.0 only. See [LICENSE](LICENSE).

Copyright (C) 2026 jsheo-mvi

## 목적

- 연결된 ADB 디바이스 식별
- 모델, 제품명, baseband, RIL 관련 속성 수집
- `dumpsys telephony.registry`, `dumpsys phone`, `dumpsys telephony`, `cmd phone` 출력에서 LTE/5G NR/WCDMA/GSM band 및 channel evidence 파싱
- 제공된 `이동통신용 주파수 주요 이용 현황 (2025.02)` PDF 기준으로 한국 SKT/KT/LGU+ 주요 band 매핑
- 현재 serving cell 기준의 main band/anchor band 추정
- USIM이 없는 상태에서 칩셋/OS network policy 기반의 지원 후보 band 추정
- VoLTE/IMS stack 존재 여부, IMS service 바인딩, carrier VoLTE config 조회 가능 여부 확인
- JSON 및 Markdown 보고서 생성

## 중요한 한계

일반 stock Android + ADB 권한만으로는 modem이 지원하는 전체 band matrix를 안정적으로 조회할 수 없습니다.
USIM이 없으면 serving cell과 carrier config가 적용되지 않으므로 실제 통신사별 허용 band도 확정할 수 없습니다.

이 도구는 다음을 분리해서 보고합니다.

- `Observed`: Android framework 출력에 band가 직접 노출된 경우
- `DerivedFromChannel`: EARFCN/NRARFCN 등 channel 번호를 band plan 일부와 매칭한 경우
- `ObservedChannelOnly`: channel은 관측됐지만 내장 band table로 band를 확정하지 못한 경우

## 한국 이동통신 band 매핑

보고서의 `Korea Frequency Allocation Matches` 섹션은 PDF 기준의 주요 할당을 사용합니다.

- 5G NR: `n78` 3.5GHz 대역
  - LGU+: 3.42-3.50GHz
  - KT: 3.50-3.60GHz
  - SKT: 3.60-3.70GHz
- LTE: `B5` 800MHz, `B8` 900MHz, `B3` 1.8GHz, `B1` 2.1GHz, `B7` 2.6GHz
- 3G WCDMA: `B1` 2.1GHz

`Primary Band Assessment`는 `mRegistered=true/YES`로 표시된 serving cell을 우선 사용합니다.
5G NSA 환경에서는 LTE가 등록된 anchor/PCell로 나오고 NR `n78`은 secondary carrier로 관측될 수 있습니다.

## USIM 없는 상태의 해석

`No-SIM Capability Assessment` 섹션은 다음 근거를 사용합니다.

- `ro.board.platform`, `ro.hardware`, `gsm.version.baseband` 기반 칩셋/모뎀 family 추정
- `cmd phone get-allowed-network-types-for-users` 기반 OS 허용 RAT
- `ro.telephony.default_network`, `settings get global preferred_network_mode*` 기반 preferred network mode 해석
- 한국 주파수 할당표와 OS 허용 RAT를 교차해 candidate band 산정

예를 들어 OS가 LTE와 WCDMA를 허용하고 NR을 허용하지 않으면 국내 후보는 `WCDMA B1`, `LTE B1/B3/B5/B7/B8`로 표시하고 `n78`은 제외합니다.
이는 실제 RF front-end, regional SKU, modem NV band mask를 읽은 결과가 아니므로 `Candidate`로 표시합니다.

## VoLTE 해석

`VoLTE / IMS Assessment` 섹션은 다음 근거를 사용합니다.

- `cmd phone ims get-ims-service -d/-c`로 Android IMS service 설정 확인
- `dumpsys activity services org.codeaurora.ims`로 IMS service가 `com.android.phone`에 바인딩되어 있는지 확인
- `dumpsys ims`로 IMS service dump 가능 여부 확인
- `cmd phone cc get-value carrier_volte_available_bool` 등 carrier config 조회 시도
- IMS/VoLTE 관련 system property 수집

USIM이 없으면 통신사 carrier config, VoLTE provisioning, IMS registration은 보통 확정할 수 없습니다.
따라서 `IMS stack present; VoLTE carrier activation unverified`는 단말 OS에 IMS/VoLTE 구성요소가 있다는 의미이며, 실제 통신사 VoLTE 개통 가능 보장은 아닙니다.

기기 전체 지원 band를 인증 수준으로 확인하려면 제조사 engineering interface, Qualcomm DIAG/QMI, MediaTek Meta Mode, root 권한, 또는 제조사 공식 spec sheet가 추가로 필요할 수 있습니다.

## 빌드

```powershell
dotnet build .\AndroidBandInspector.csproj
```

## 실행

배포 exe:

```powershell
.\AndroidBandInspector.exe --out .\reports
```

소스에서 실행:

```powershell
dotnet run -- --out .\reports
```

특정 디바이스만 조사:

```powershell
dotnet run -- --serial <adb-serial> --out .\reports
```

원문 `dumpsys` 출력을 보고서 JSON에 더 길게 포함:

```powershell
dotnet run -- --raw --out .\reports
```

ADB 경로를 직접 지정:

```powershell
dotnet run -- --adb "C:\Android\platform-tools\adb.exe" --out .\reports
```

## 사전 조건

- Android Platform Tools의 `adb`
- 스마트폰의 Developer options 및 USB debugging 활성화
- PC에서 RSA debugging authorization 승인

확인:

```powershell
adb devices -l
```

## 운영 주의

이 도구는 `adb shell` 기반 읽기 명령만 실행하며 radio setting, SIM, APN, network mode를 변경하지 않습니다.
보고서에는 ADB serial, 모델명, baseband 정보가 포함될 수 있으므로 외부 공유 전 민감도 검토가 필요합니다.

## License

This project is licensed under the GNU General Public License v3.0 only.

If you distribute modified binaries, provide the corresponding source code under the same license terms.
