# NeuroSDR 설치 안내 (릴리스 zip)

이 문서는 **릴리스 압축 파일을 받아 실행**하는 사용자를 위한 것입니다. 컴파일은 필요 없습니다.

## 1. 받기

1. GitHub **Releases**에서 최신 Windows zip(보통 x64)을 받습니다.
2. 쓰기 가능한 폴더에 **전부** 압축을 풉니다 (예: `C:\NeuroSDR`).

## 2. zip에 이미 들어 있는 것

- `NeuroSDR.exe`와 해당 빌드에 필요한 지원 파일
- RTL-SDR / HackRF / Airspy 등용 번들 native 라이브러리(`native\` 등)
- 기본 AF / IQ 플러그인

RTL-SDR·HackRF·Airspy를 위해 **별도 DLL을 더 받을 필요는 보통 없습니다.**

## 3. 라디오별 추가 설치

### RTL-SDR / HackRF / Airspy (USB)

1. 동글을 꽂습니다.
2. Windows가 SDR로 인식하지 않으면 **[Zadig](https://zadig.akeo.ie/)** 로 WinUSB를 한 번 설치합니다.
3. NeuroSDR에서 해당 **SOURCE**를 고릅니다.

### SDRplay

공식 **SDRplay API 3.x**를 SDRplay 사이트에서 설치해야 합니다. 릴리스 zip만으로는 API가 포함되지 않습니다.

### SoapySDR (Lime, Pluto 등)

선택 사항입니다. **[PothosSDR](https://github.com/pothosware/PothosSDR/wiki)** 등을 설치하면 SOURCE에 나타날 수 있습니다.

### WebSDR / Kiwi / OpenWebRX

로컬 라디오·드라이버 불필요. 인터넷과 사이트 URL만 있으면 됩니다.

## 4. 첫 실행

1. `NeuroSDR.exe` 실행
2. **SOURCE** 선택 → **RX START**
3. 스펙트럼 클릭 또는 VFO에 주파수 입력 후 Enter
4. **SET**에서 오디오·성능·플러그인·웹 원격 설정

설정 위치: `%LocalAppData%\NeuroSDR\`

## 5. 선택: 음성 안내 / 웹 원격

- **VOICE GUIDANCE**: 오프라인 eSpeak NG 설치 시 (필수 아님)
- **WEB REMOTE**: SETUP에서 켠 뒤에만 동작 (기본 꺼짐)

## 6. 문제 대응

| 증상 | 확인 |
|---|---|
| SOURCE에 동글 없음 | Zadig / 케이블 / 포트; SDRplay는 API 설치 |
| RX START 실패 | 다른 SDR 앱이 장치를 잡고 있는지, 상태줄 메시지 |
| 소리 없음 | SETUP 오디오 장치, OUT1, Windows 볼륨 |
| 원격만 이상 | URL·다른 사이트·방화벽 |

## 7. 일상 사용에 필요 없는 것

Visual Studio, .NET SDK, 이 Git 저장소 클론, native DLL 직접 빌드.

**릴리스 받기 → 압축 풀기 → (필요 시) 제조사/USB 드라이버 → NeuroSDR.exe**

---

[English install](INSTALL.md) · [日本語](INSTALL_jpn.md) · [中文](INSTALL_chn.md) · [README_kor.md](README_kor.md)
