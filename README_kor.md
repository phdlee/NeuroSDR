# NeuroSDR

**버전 0.2** — Windows 소프트웨어 정의 라디오(SDR) 수신기 · **KD8CEC**

대부분은 **컴파일이 필요 없습니다.** 릴리스 zip을 받아 풀고 `NeuroSDR.exe`만 실행하면 됩니다.

| 문서 | 언어 |
|---|---|
| [영어 README (메인)](README.md) · [Install](INSTALL.md) | English |
| [설치 안내](INSTALL_kor.md) | 한국어 |
| [日本語](README_jpn.md) · [インストール](INSTALL_jpn.md) | Japanese |
| [中文](README_chn.md) · [安装](INSTALL_chn.md) | Chinese |

## 기능 요약

- 스펙트럼 / 워터폴, 클릭·드래그·휠 튜닝
- AM, SAM, NFM, WFM, USB, LSB, CW, RAW 및 디지털 음성(DMR, D-STAR, C4FM, FreeDV 등)
- 로컬: SDRplay, RTL-SDR, HackRF, Airspy (선택 SoapySDR)
- 원격: WebSDR, KiwiSDR, OpenWebRX
- AF 플러그인(FT8/FT4, CW, SSTV, 팩스 등), 메모리/스캔, IQ·AF 녹음, 위성 장면
- 듀얼 오디오, 웹 원격(선택), 장면/설정 저장

## 빠른 시작

1. GitHub **Releases**에서 최신 zip 다운로드
2. **[INSTALL_kor.md](INSTALL_kor.md)** 의 드라이버 안내를 따름
3. `NeuroSDR.exe` 실행 → **SOURCE** 선택 → **RX START**

일상 사용에는 Visual Studio, .NET SDK, 이 소스 트리가 필요 없습니다.

## 하드웨어 한눈에

| 라디오 | 추가 설치 |
|---|---|
| RTL-SDR, HackRF, Airspy | 보통 불필요 (USB는 **최신 Zadig** 한 번; 구버전은 실패하기 쉬움) |
| SDRplay RSP | 필요 — 공식 **SDRplay API 3.x** |
| Lime / Pluto / BladeRF / USRP | 선택 — **PothosSDR** (SoapySDR) |
| WebSDR / Kiwi / OpenWebRX | 하드웨어 없음, 인터넷만 |

자세한 설치: [INSTALL_kor.md](INSTALL_kor.md)

## 개발·유지보수

**NeuroSDR의 설계, 코딩, 문서, GitHub 관리는 AI(Grok 4.7)가 담당합니다.** 사람 검토와 실기 테스트가 릴리스를 돕지만, 일상적인 개발과 저장소 작업은 AI가 수행합니다.

## 소스 코드

이 저장소는 기여자·소스 빌드용입니다. 일반 사용자는 릴리스 zip을 쓰세요.

```text
NeuroSDR/          메인 Windows 앱
KiwiSDRPlugin/     선택 AF 플러그인 샘플
```

---

NeuroSDR · **KD8CEC**

스크린샷이 포함된 자세한 설치/사용 메뉴얼은 블로그에 올릴 예정입니다. 전체 안내와 업데이트는 **[https://www.hamskey.com](https://www.hamskey.com)** 을 방문해 주세요.
