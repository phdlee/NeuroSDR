# NeuroSDR

Version 0.1

NeuroSDR은 특정 기존 SDR 애플리케이션에 종속되지 않는 C# 기반 수신기 프로젝트입니다. 현재 단계는 실시간 DSP와 UI 골격을 검증하는 최초 실행 버전입니다.

## 현재 구현

- 장치 소스 인터페이스와 개발용 합성 IQ 소스
- 순수 C# radix-2 FFT 및 Blackman-Harris 윈도
- 실시간 스펙트럼과 워터폴
- 클릭/드래그/휠 주파수 튜닝
- AM, SAM, NFM, WFM, USB, LSB, CW, RAW 모드와 모드별 기본 필터 폭
- 설치된 SDRplay API 3.x 및 연결 장치 탐지
- SDRplay RSP1 2/5/8/10 MS/s 선택형 IQ 스트리밍, 최대 8 MHz tuner filter, 주파수 재튜닝과 RF 게인
- AM/NFM/WFM/USB/LSB/CW 기본 복조와 Windows 48 kHz 오디오 출력
- 실행 중 SDRplay/합성 IQ 소스 전환
- MHz 직접 입력(Enter로 적용), 기본 89.1 MHz WFM
- ADC/DSP/drop/audio drop 실시간 파이프라인 상태 표시
- RF 중심과 청취 VFO 분리, 스펙트럼 내 독립 VFO 이동
- 샘플당 삼각함수가 없는 재귀형 NCO 주파수 변환
- 250→50 kS/s FIR 감산과 모드별 복소 채널 필터
- USB/LSB 반대 측파대 억압, SAM 반송파 PLL, CW 700Hz BFO
- FFT/워터폴 공통 주파수 viewport와 5 kHz까지 범위 확대
- 로그형 줌 슬라이더, 일반 마우스 휠 줌, 배경 드래그 RF 중심 이동
- 외부 NuGet 패키지 없는 .NET 10 WinForms 구성

연결된 SDRplay 장치가 있으면 시작 시 기본 소스로 선택하고, 없으면 합성 IQ 소스로 폴백합니다. 제조사 DLL은 설치된 공식 API 경로에서 동적으로 불러옵니다.

AM/FM/SSB/CW/SAM은 각각 독립된 실제 복조 경로를 사용합니다. 협대역 모드는 50 kS/s FIR 채널 필터를 거치며 USB/LSB는 복소 대역통과 필터로 원하는 측파대만 선택합니다.

## 실행 및 검증

```powershell
dotnet run --project .\NeuroSDR\NeuroSDR.csproj
dotnet run --project .\NeuroSDR\NeuroSDR.csproj -- --verify
dotnet run --project .\NeuroSDR\NeuroSDR.csproj -- --verify-sdrplay
dotnet run --project .\NeuroSDR\NeuroSDR.csproj -- --verify-audio
dotnet run --project .\NeuroSDR\NeuroSDR.csproj -- --soak-sdrplay=60
dotnet run --project .\NeuroSDR\NeuroSDR.csproj -- --ui-soak=30
dotnet run --project .\NeuroSDR\NeuroSDR.csproj -- --survey-rf
```

`--verify`는 합성 소스, FFT, 복조와 렌더링을 검사합니다. `--verify-sdrplay`는 RSP1을 실제로 열어 IQ를 FFT와 WFM 복조 경로에 통과시키고 재튜닝한 다음 장치를 해제합니다. `--verify-audio`는 무음 버퍼로 Windows 오디오 출력 수명주기를 검사합니다. 성공 시 종료 코드 0을 반환합니다.

`--soak-sdrplay=N`은 N초 동안 실제 장치→FFT→복조→waveOut 처리량과 정지 시간을 측정합니다. `--ui-soak=N`은 실제 WinForms 메시지 루프에서 수신 시작, 화면 갱신, 오디오, 정지와 창 닫기를 자동 검증합니다. `--survey-rf`는 FM과 주요 단파 구간을 순회합니다. 결과 JSON은 실행 파일 옆 `diagnostics` 폴더에 저장됩니다.

## 2026-08-11 안정화 결과

화면이 0.5~2초 후 멈추고 프로그램이 닫히지 않던 원인은 `waveOut` 완료 콜백 안에서 `waveOutUnprepareHeader`를 호출한 교착이었다. 콜백은 이벤트 통지만 하고 별도 스레드에서 버퍼를 정리하도록 수정했다. 현재 RSP1/HackRF 경로는 2/5 MS/s에서 32,768, 8 MS/s 이상에서 65,536 IQ 샘플 단위로 병합하고 32-block bounded queue로 순간 burst를 흡수한다. 입력 정규화는 unsafe 포인터 블록 루프를 사용하며 완료된 고정 크기 배열을 재사용한다.

RSP1 60초 실장치 시험에서 120,235,248 ADC 샘플 중 120,127,488 샘플을 DSP가 처리했고, 오디오 버퍼 3,003개가 모두 완료됐다. 최대 DSP 전달 지연은 63ms, 종료 시간은 1.15초였다. 실제 UI 30초 시험과 장치 시작/정지 5회 반복도 통과했다.

RF 조사에서는 현재 장치와 안테나 상태로 FM 89.1 MHz 및 96.0 MHz 부근과 여러 단파 구간에서 강한 신호가 확인됐다. 기본 주파수는 청취 검증이 쉬운 89.1 MHz WFM으로 설정했다.

## 설계 원칙

하드웨어 어댑터는 `ISampleSource` 뒤에 격리합니다. 이후 장치 어댑터를 추가해도 DSP와 UI는 바뀌지 않습니다. SDRplay는 사용자가 설치한 API 3.x를 로드하고, RTL-SDR과 HackRF는 아키텍처별 native 호스트 라이브러리를 배포물에 포함합니다.

## 다음 단계

1. RTL-SDR 및 HackRF 실제 장치 장기 수신 시험
2. 메모리 bank·즐겨찾기와 scan lockout
3. RF 중심의 연속 팬과 자동 재튜닝
4. 배포 UI 기술 확정과 하드웨어별 통합 시험

## 웹 원격 제어 (PWA)

`NeuroSDR.Web`는 .NET 10 ASP.NET Core 호스트입니다. **SETUP → General → ENABLE WEB REMOTE** 를 켠 뒤에만 Kestrel이 뜨고, 끄면 즉시 종료됩니다.

- 기본 URL: `http://127.0.0.1:8765/`
- 모바일: 반응형 UI + **PWA** (`manifest` / service worker). Safari·Chrome에서 “홈 화면에 추가 / 설치” 가능
- SignalR: 상태·스펙트럼·워터폴·**웹 전용 AF 오디오**·**AF Plugin decode feed**
- 웹 볼륨은 브라우저에서만 적용 (데스크탑 OUT1/OUT2 볼륨과 독립)
- RX START/STOP·주파수·모드·BW·게인·데스크탑 볼륨/SQL·소스 전환

`settings.json` (`%LocalAppData%\NeuroSDR\`):

| 키 | 기본 | 설명 |
|---|---|---|
| `WebRemoteEnabled` | `false` | SETUP에서 켠 경우에만 웹 원격 기동 |
| `WebRemotePort` | `8765` | 포트 |
| `WebRemoteBindAllInterfaces` | `false` | `true`면 LAN 폰에서 접속 (`http://<PC-IP>:8765/`) |
| `WebRemoteAccessToken` | `""` | 비어 있지 않으면 `?token=` / `?access_token=` 또는 `X-NeuroSDR-Token` 필요 |
| `RtlTcpEndpoint` | `127.0.0.1:1234` | rtl_tcp 소스 host:port |
| `VoiceGuidanceEnabled` | `false` | 오프라인 eSpeak NG 음성 안내 |

상태줄에 `Web remote · listening · …`이 보이면 준비된 것입니다.

## rtl_tcp 테스트 서버

하드웨어 없이 NeuroSDR의 `rtl_tcp` 입력을 검증하려면:

```powershell
dotnet run --project NeuroSDR.RtlTcpTest -c Debug
# optional: -- -p 1234 -f 145000000 -s 2048000
```

1. 위 서버를 실행 (기본 `127.0.0.1:1234`, CU8 IQ + RTL0 헤더)
2. NeuroSDR에서 소스 **rtl_tcp (127.0.0.1:1234)** 선택 (엔드포인트는 SETUP에서 변경 가능)
3. RX START → 스펙트럼에 ~5 kHz 톤이 보이면 연결 성공

## NFM CTCSS

NFM에서 EIA/NATO **CTCSS(PL)** 톤이 있으면 ANALOG 패널과 AF 스펙트럼/워터폴에 `CTCSS xx.x`로 표시됩니다.

## 음성 안내 (TTS)

SETUP → General → **VOICE GUIDANCE**: 주파수·모드 변경을 **오프라인**으로 안내합니다.

- 엔진: **eSpeak NG** (Windows/Linux 공통, 인터넷 불필요)
- PATH의 `espeak-ng`/`espeak` 또는 `native/espeak-ng(.exe)`
- Windows: eSpeak NG 설치 · Linux: `sudo apt install espeak-ng`
- 순수 C# 고품질 오프라인 TTS는 사실상 없고, eSpeak가 이식성에 가장 적합합니다 (나중에 Piper도 같은 인터페이스로 추가 가능)

## 하드웨어 소스 (요약)


| 소스 | 방식 | 비고 |
|---|---|---|
| SDRplay RSP | 설치 API | `Program Files\SDRplay\API` |
| RTL-SDR | bundled `librtlsdr` | win-x64 / win-x86 |
| HackRF One | bundled `hackrf` | win-x64 / win-x86 |
| Airspy R2/Mini | bundled `airspy` | win-x64 / win-x86 |
| Airspy HF+ | bundled `airspyhf` | win-x86 기본; x64는 DLL 추가 또는 Soapy |
| SoapySDR | 선택적 | PothosSDR 설치 시 Lime/Pluto/BladeRF/USRP 등 |
| rtl_tcp | 네트워크 | 기본 `127.0.0.1:1234` |
| WebSDR / Kiwi / OpenWebRX | 원격 AF | IQ 없음 |
| Synthetic / IQ WAV | 테스트·파일 | |

```powershell
NeuroSDR.exe --probe-airspy
NeuroSDR.exe --probe-airspyhf
NeuroSDR.exe --probe-soapy
NeuroSDR.exe --verify-airspy
```

Soapy 설치 안내: `native\SOAPY-NOTES.txt`. Airspy 바이너리: `native\AIRSPY-BINARIES.txt`.

## RTL-SDR / HackRF 입력


RTL-SDR과 HackRF 어댑터를 `ISampleSource`로 구현했다. 연결된 장치와 native 라이브러리가 모두 발견될 때만 SOURCE 목록에 나타나며, 없을 때는 SDRplay와 합성 소스의 동작에 영향을 주지 않는다. 공식 HackRF 2026.01.3 및 libusb 1.0.30 소스로 빌드한 x64/x86 DLL을 배포하며 두 아키텍처 모두 HackRF One 실장치 IQ/FFT/복조 검증을 통과했다. 라이브러리는 다음 순서로 동적으로 찾는다.

USB API 1.03의 구형 펌웨어가 설치된 현재 시험 장치는 일반 구간에서 명령한 quadrature sample rate의 약 절반만 전달한다. 이 API 버전에서는 실측 보정한 장치 rate를 명령하고 DSP에는 사용자가 선택한 실제 rate를 유지한다. 15 MS/s는 구형 divider의 비선형성 때문에 별도 실측 명령값 24.77 MHz를 사용하며 전체 UI에서 RF 약 14.96 MS/s, PCM 약 47.86 kHz를 확인했다. 현재 장치는 20 MS/s 선택에 필요한 40 MS/s 명령도 받아 실제 약 20 MS/s IQ를 전달했으며, 확장한 RF burst queue와 고속 audio jitter buffer를 포함한 전체 UI 시험에서 IQ/audio drop과 underrun 0을 확인했다. 최신 API 장치는 보정 없이 사용자 rate를 사용한다.

1. 실행 파일의 `native` 하위 폴더
2. 실행 파일과 같은 폴더
3. Windows DLL 검색 경로

RTL-SDR은 `rtlsdr.dll` 또는 `librtlsdr.dll`, HackRF는 `hackrf.dll` 또는 `libhackrf.dll`과 각 라이브러리가 요구하는 USB 종속 DLL이 필요하다. 저장소의 `samples` 폴더는 ABI와 설계 비교 자료일 뿐 실행 시 자동으로 참조하거나 복사하지 않는다.

RTL-SDR은 `native\win-x64`와 `native\win-x86`에 Osmocom 공식 Windows 바이너리를 분리해 포함한다. `Platform=x64` 빌드는 x64 세트만, `Platform=x86` 빌드는 x86 세트와 해당 종속 DLL만 출력의 `native` 폴더로 복사한다. 장치가 없어도 `NeuroSDR.exe --probe-rtlsdr`로 현재 프로세스 아키텍처에 맞는 DLL과 필수 export의 로드 여부를 검사할 수 있다. 과거에 수동 복사한 `native\rtlsdr.dll`은 플랫폼별 폴더보다 나중에만 검사하므로 잘못된 아키텍처 또는 UsbDk 전용 DLL이 정상 세트를 가리지 않는다.

```powershell
dotnet build .\NeuroSDR\NeuroSDR.csproj -p:Platform=x64
dotnet build .\NeuroSDR\NeuroSDR.csproj -p:Platform=x86

.\NeuroSDR\bin\x64\Debug\net10.0-windows\NeuroSDR.exe --probe-rtlsdr
.\NeuroSDR\bin\x86\Debug\net10.0-windows\NeuroSDR.exe --probe-rtlsdr
```

실장치 IQ 파이프라인까지 검사할 때는 `--verify-rtlsdr`를 사용한다. x64 시험 종료 직후 x86 시험을 연속 실행하면 Windows가 USB 핸들을 반환하기 전에 두 번째 open이 실행될 수 있으므로 각 실행을 별도 프로세스로 수행하고 잠시 간격을 둔다.

두 장치 모두 RF gain 슬라이더, 실행 중 재튜닝, 취소 가능한 정지와 bounded IQ 전달을 지원한다. RTL-SDR의 unsigned 8-bit interleaved IQ와 HackRF의 signed 8-bit interleaved IQ는 공통 16,384-sample dispatcher에서 `Complex32`로 정규화된다. 자동 검증은 이 두 변환과 SDRplay 16-bit 변환, HackRF transfer 구조체 ABI를 함께 검사한다.

## 표시 플러그인과 SETUP

상단 `SETUP`의 `Plugin` 탭에서 RF spectrum과 waterfall renderer를 독립적으로 선택한다. `RF / Performance` 탭에서는 현재 지원 장치의 capture rate와 RF 표시 FPS(5/10/15/20/30), FFT 해상도(Low/Balanced/High)를 선택한다. RSP1은 2/5/8/10 MS/s, HackRF One은 2/5/8/10/15/20 MS/s를 지원하며 각 SOURCE는 자신의 지원 목록까지만 표시한다. HackRF/PortaPack UI 명칭에 맞춰 전용 RF AMP를 `+14 dB`로 표시하며 안전한 시작값은 OFF다. 현재 시험 장치는 ON 상태가 PortaPack에도 정상 표시되지만 실제 gain 상승은 측정되지 않았으므로 전원 또는 하드웨어 경로를 별도로 확인한다. 표시 옵션은 오디오와 decoder sample 품질을 낮추지 않는다.

RF→복조 PCM→waveOut 연속성은 `--diagnose-audio=rsp|hackrf --sample-rate=N --seconds=N [--with-display]`로 측정한다. JSON은 실행 폴더 `diagnostics/audio-continuity-*.json`에 저장되며 RF/PCM 실측률, callback gap, 복조 평균/P95/P99/최악 시간, dispatcher 최대 큐, waveOut 제출 간격·pending 범위·underrun을 포함한다. 메인 상태 표시줄에도 `underrun` 누계를 표시한다.

좁은 AM/SSB/CW/NFM 복조 전에는 capture rate에 비례하는 2단 anti-alias decimator를 적용한다. 과거의 1단 평균기는 넓은 RSP IF 대역에 들어온 강한 방송 신호가 250 kHz demodulation domain으로 접히는 것을 충분히 막지 못해 2 MS/s보다 8 MS/s에서 혼선이 증가할 수 있었다. 새 필터는 2/5/8/10 MS/s에서 같은 상대 응답을 유지한다. 다만 원래 RSP1에서 넓은 IF 필터를 선택하면 더 많은 강신호가 ADC에 직접 들어가므로, 하드웨어 intermodulation과 dynamic-range 손실까지 소프트웨어가 제거할 수는 없다.

HackRF RF AMP 하드웨어 A/B 비교는 `--diagnose-hackrf-amp [--frequency=Hz]`로 실행한다. 동일 수신 상태에서 OFF/ON을 두 차례 교차 측정하여 `diagnostics/hackrf-amp-comparison.json`에 DC 제거 IQ 전력과 ON-OFF 차이를 기록한다.

실제 WinForms 경로는 `SETUP → RF / Performance → RECORD FULL UI AUDIO PIPELINE DIAGNOSTICS`로 기록한다. 실행 파일 옆 `diagnostics/ui-pipeline-*.csv`에 초당 한 행씩 IQ plugin, RF display 제출, demodulator, AF plugin, AF display, 두 audio DSP/waveOut 단계의 평균·최대 시간과 RF/PCM 실효율, callback gap, dispatcher queue/drop, waveOut underrun, GC/ThreadPool 상태를 저장한다. 쓰기 권한이 없는 설치 폴더에서는 LocalAppData의 `NeuroSDR\Diagnostics`로 대체한다. `--pipeline-log`로도 강제 활성화할 수 있고 `--sample-rate=N`을 함께 주면 설정 파일을 바꾸지 않고 실제 UI 시험 rate를 지정한다.

이 전체 UI 계측으로 단순 진단에 없던 KiwiTIMECODE IQ analyzer의 잠금 역전 병목을 찾았다. 비동기 decoder worker가 긴 처리 lock을 보유한 동안 RF thread가 단순 `ProducesIqOutput` 조회에서 평균 약 19 ms 대기하여 5 MS/s에서 530만 sample drop과 waveOut underrun을 일으켰다. 출력 여부를 lock-free 상태로 분리한 뒤 동일 UI 시험에서 5/8/10 MS/s 모두 source drop과 audio underrun이 0이 되었다.

## 상태 저장과 듀얼 오디오

창 위치·크기·최대화 여부, 마지막 source 장치, VFO/RF/view 주파수, mode/BW/gain, DSP, AF filter, 표시 plugin과 두 출력 장치 설정을 `settings.json` 하나에 저장한다. SETUP의 General 탭에서 전체 설정을 초기화할 수 있다. 이전 `plugins.ini`가 있으면 최초 실행 시 값을 가져온다.

SETUP Audio에서 Windows waveOut 장치를 최대 2개 선택한다. 두 출력은 같은 복조 audio에서 분기되지만 RX Control의 OUT 1/2 volume, SQL 사용 여부, threshold와 gate가 독립적이다. RF/AF level은 작은 막대 meter로 표시하고 SQL open 상태는 출력별 LED로 표시한다. 저장된 장치가 사라지면 Windows 기본 출력으로 폴백한다.

RX Control의 VFO는 시스템 font 대신 직접 그리는 7-segment 숫자를 사용한다. CW-U/CW-L 버튼은 제거하고 SETUP General에서 기본 CW-L/Icom 또는 CW-U/Yaesu 방향을 선택한다. 왼쪽 설정 영역의 scrollbar에는 Windows dark explorer theme을 적용한다.

7-segment VFO는 숫자 자리를 클릭해 직접 조정한다. 자리 위쪽 1/3 클릭은 증가, 가운데 1/3은 선택만, 아래쪽 1/3은 감소하며 자리올림/자리내림을 포함한다. 선택 자리에는 선명하지만 얇은 밑줄, VFO 포커스에는 호박색 border를 표시한다. VFO가 미리 선택되지 않았어도 포인터 아래 숫자에서 wheel을 돌리면 그 자리가 자동 선택되고 해당 step으로 증감한다. 포인터를 좌우로 옮기면 다음 wheel 이벤트부터 새 자리를 사용한다. 다른 control을 선택하거나 1분이 지나면 VFO 선택이 해제된다.

RF/AF meter는 빠른 attack과 조금 느린 release 지수 평활화를 적용해 작은 계기처럼 자연스럽게 움직인다. 메인 spectrum FFT 속도는 그대로 유지한다. 메인 표시 오른쪽의 `SP`와 `WF` vertical level bar는 spectrum trace와 waterfall palette 감도를 각각 -40~+40 dB 범위로 조정하며 이 값도 `settings.json`에 저장된다. `SET`과 `RX START/STOP`은 RX Control 내부에서 같은 크기의 굵은 어두운 테두리, hover와 눌림 색상을 갖는 주요 스위치로 표시한다.

외장 표시 DLL은 실행 폴더의 `plugins\display`에서 자동 발견한다. 공개 계약, 기본 생성자 규칙과 샘플 코드는 루트의 `NeuroSDR플러그인.md`에 정리했다. RF 축, VFO/filter, drag/zoom은 core가 유지하고 플러그인은 trace 또는 waterfall history만 담당하므로 표시 플러그인 교체가 수신·튜닝 상태를 바꾸지 않는다.

## Source hardware plugin

UI는 이제 SDRplay, RTL-SDR 또는 HackRF 클래스를 직접 생성하지 않는다. 내장 및 외부 `ISampleSourceProvider`를 `HardwareSourceCatalog`가 발견하고 provider가 반환한 `ISampleSource`만 사용한다. 외부 plugin assembly는 배포 폴더의 `plugins\hardware`에 넣으며, public 기본 생성자가 있는 `ISampleSourceProvider` 구현을 자동으로 로드한다.

Provider와 reflection은 프로그램 시작 시 장치 발견에만 사용한다. 실시간 수신 중에는 기존과 동일하게 `ISampleSource.SamplesAvailable`에서 DSP로 직접 전달하므로 plugin 구조로 인한 IQ hot-path 성능 저하는 없다. 향후 WebSDR/KiwiSDR 수신 모듈도 네트워크 입력을 `ISampleSource`로 내보내는 provider로 연결할 수 있다.

## IQ WAV 녹음과 재생

수신 중 `IQ 녹음 시작`으로 현재 RF capture 전체를 기록하고 `IQ WAV 열기`로 녹음 파일을 가상 sample source처럼 재생할 수 있다. 파일은 16-bit stereo PCM WAV이며 왼쪽 채널은 I, 오른쪽 채널은 Q이다. `enrf` metadata chunk에 RF 중심 주파수를 저장하므로 재생할 때 주파수 눈금과 VFO 위치를 복원한다.

디스크 기록은 16블록 bounded queue와 별도 writer task에서 수행한다. 저장 장치가 수신 속도를 따라가지 못하면 장치 callback을 기다리게 하지 않고 녹음 drop 수를 누적한다. 현재 RIFF 호환성을 위해 파일당 데이터는 4 GB로 제한하며, 녹음 중에는 한 파일에 서로 다른 RF 중심이 섞이지 않도록 hardware RF 재튜닝을 막는다.

## VFO 조작

상단 입력은 청취 VFO 주파수다. 현재 source의 RF capture 범위 안의 주파수를 입력하거나 스펙트럼을 클릭하면 하드웨어 중심은 고정되고 DSP NCO만 이동한다. 범위 밖의 주파수를 직접 입력하면 RF 중심도 자동으로 이동한다. `RF 중심 = VFO` 버튼은 현재 청취 주파수를 화면 중앙으로 재배치한다. SETUP에서 선택한 source별 실제 sample rate가 이 범위를 결정한다.

실장치에서 RF 중심을 고정한 채 VFO를 +300 kHz 이동하는 20초 시험을 통과했다. 40,412,736 ADC 샘플 중 40,370,176 샘플을 처리했고 오디오 1,009개 버퍼가 전부 완료됐으며, 같은 조건의 실제 UI 시험도 정상 종료됐다.

## 스펙트럼 줌과 팬

왼쪽 `SPECTRUM VIEW` 슬라이더는 전체 RF capture부터 최소 5 kHz 범위까지 로그형으로 확대한다. 스펙트럼이나 워터폴 위에서 일반 마우스 휠을 돌리면 마우스가 가리키는 주파수를 고정한 채 확대/축소한다. `VFO를 화면 중앙으로`와 `전체 RF 범위` 버튼으로 즉시 복귀할 수 있으며 스펙트럼과 워터폴은 항상 동일한 viewport를 사용한다. 화면 오른쪽 위에는 현재 `CENTER`와 `SPAN`이 표시된다.

- 배경 클릭: 해당 주파수로 VFO 이동
- 배경을 누른 채 드래그: 하드웨어 RF 중심 주파수 이동
- 노란 필터 영역 드래그: VFO 이동
- 오른쪽 또는 가운데 버튼 드래그: 확대된 화면 범위 팬
- 마우스 휠: 마우스 위치 중심 확대/축소
- Ctrl+휠: 100Hz VFO 튜닝, Shift+휠: 1kHz VFO 튜닝
- 더블클릭: VFO를 선택하고 현재 viewport 중앙으로 이동

이 조작은 SDR#의 필터/배경 드래그 분리와 SDR++의 별도 줌 슬라이더·VFO 중심 확대 방식을 참고해 새로 구현했다. 확대 렌더링, 배경 팬/클릭 판정, 실제 RSP1 UI 25초 동작과 종료 회귀시험을 통과했다.

확대 화면은 전체 대역 FFT 이미지를 단순 확대하지 않는다. span에 따라 FFT를 2,048점부터 최대 131,072점까지 자동 증가시킨다. 큰 FFT는 수신 callback 및 오디오와 분리된 bounded worker에서 실행되므로 분석 중에도 수신과 복조를 막지 않는다.

RSP1은 SETUP에서 2/5/8/10 MS/s를 선택한다. 대응 tuner filter는 각각 1.536/5/8/8 MHz이며, 10 MS/s 화면의 양 끝 약 1 MHz씩은 filter roll-off 영역이다. 2 MS/s가 연속 음성 수신과 decoder용 기본값이고 5/8/10 MS/s는 넓은 대역 관찰이 필요할 때 사용한다. 이전 버전에서 강제된 10 MS/s 설정은 한 번 2 MS/s 안전 기본값으로 마이그레이션된다.

필터 표시는 모드의 실제 통과대역 방향을 따른다. USB는 선택 주파수 오른쪽, LSB는 왼쪽에만 영역을 표시한다. AM, SAM, NFM, WFM, RAW와 CW는 선택 주파수를 중심으로 표시한다. CW-U/Yaesu(+700 Hz)와 CW-L/Icom(-700 Hz)을 선택할 수 있고 실제 BFO 위치를 점선으로 함께 표시한다. 배경 또는 필터를 드래그하는 동안에는 워터폴을 매번 지우지 않고 마우스 이벤트에서 대기 중인 최신 FFT frame을 즉시 그린다.

RF 중심 배경 드래그 중에는 hardware retune을 반복하지 않는다. 기존 spectrum과 waterfall을 마우스 이동량만큼 수평 shift하면서 새 FFT 행도 같은 offset으로 이어 붙이고, 마우스를 놓을 때 SDRplay RF 중심을 한 번만 변경한다. 실제 UI 자동시험은 500ms에 걸친 10단계 배경 드래그 중 ADC sample이 계속 증가하는지 확인한다.

## 메모리 채널과 스캔

왼쪽 `MEMORY / SCAN` 버튼은 SDRuno처럼 메인 spectrum을 가리지 않는 독립 메모리 패널을 연다. `STORE`는 현재 VFO 주파수, mode와 filter bandwidth를 저장한다. 표의 Description, Frequency, Mode, BW를 직접 편집할 수 있고 더블클릭 또는 `RECALL`로 호출한다. 채널은 `%LocalAppData%\NeuroSDR\channels.json`에 즉시 저장된다.

`SCAN`은 저장된 채널을 dwell 시간에 따라 순환하며 서로 다른 RF capture에 있는 채널도 자동 재튜닝한다. `HOLD`를 켜면 선택한 threshold보다 현재 filter passband의 FFT peak가 강한 동안 그 채널에 머문다. 실제 RSP1 UI 시험은 1.1 MHz 떨어진 WFM/USB 임시 채널을 500ms dwell로 왕복하면서 RF 재튜닝, mode/filter 복원과 ADC sample 지속을 검사한다. 시험용 채널은 사용자 JSON에 저장하지 않는다.

`RANGE`는 START/END MHz와 STEP kHz를 지정해 메모리 저장 없이 연속 범위를 순환한다. 메모리 스캔과 범위 스캔은 하나의 수신 재튜닝 경로를 공유하되 동시에 실행되지 않으며, 범위 스캔도 dwell, threshold와 signal hold를 지원한다.

## Audio DSP

왼쪽 `AUDIO DSP`에서 FFT passband 신호 레벨 기반 squelch, AF AGC, 적응형 noise reduction과 가변 audio notch를 제어한다. Squelch는 attack/release gate로 열고 닫아 click을 줄인다. AGC는 빠른 감쇠와 느린 증폭 복구를 사용하며, notch는 50~20,000 Hz에서 조절한다. 기능은 모두 48 kHz audio 단계에서 동작하고 RF FFT 및 장치 callback과 분리된다.

## AF 스펙트럼과 워터폴

`samples\sdruno.png`의 RX control/AUX spectrum 구성을 참고해 메인 RF 화면 위에 별도 AF 영역을 배치했다. 큰 RX Control에는 검은 계기창과 청색 디지털 VFO, source 전환, 8개 mode, CW-U/CW-L, 14개 band preset, filter BW preset·미세 증감, 그래픽 volume/squelch를 배치했다. 오른쪽에는 복조 audio의 8,192점 FFT와 독립 waterfall을 표시한다. 48 kHz audio에서 해상도는 약 5.86 Hz/bin이므로 FT8 같은 좁은 tone 간격을 확인할 수 있다.

밴드 버튼은 MW/SW, 160~6m amateur band, FM 방송과 AIR band의 대표 주파수로 즉시 이동하고 적합한 기본 mode/BW를 함께 선택한다. 왼쪽 세로 영역은 위에서 아래로 쌓이는 레이아웃을 유지하며 IQ, spectrum view, RF gain, 녹음, memory와 DSP 및 향후 plugin별 설정에 사용한다.

AF filter는 RF filter bandwidth와 독립적이다. `AF FILTER` 체크박스는 기본 활성화되고, 기본 통과 범위는 0~20 kHz이다. 그래프/워터폴의 왼쪽과 오른쪽 경계선을 각각 드래그해 서로 교차하지 않는 BPF를 구성한다. 왼쪽 경계가 0 Hz이면 오른쪽 경계만으로 LPF처럼 동작한다. 실제 48 kHz audio 경로에는 2차 Butterworth HPF와 LPF가 적용된다.

경계 드래그는 수신기나 AF FFT worker를 재시작하지 않으며, AF FFT는 volume, squelch와 audio 출력 여부보다 앞에서 분기되므로 음소거 중에도 표시된다. 자동 UI 시험은 두 경계를 500ms 동안 연속 이동하면서 ADC sample과 AF frame이 모두 계속 증가하는지 검사한다.

## 협대역 DSP

입력 sample rate에 맞춰 첫 감산율을 동적으로 선택해 약 250 kS/s로 낮춘 뒤, 127탭 FIR로 5:1 감산해 협대역 처리를 약 50 kS/s에서 수행한다. 따라서 RSP1 10 MS/s와 RTL-SDR 2.048 MS/s가 같은 복조 경로를 안전하게 공유한다. AM/SAM/NFM/CW에는 저역통과 필터를, USB/LSB에는 양·음 주파수를 구분하는 255탭 복소 대역통과 필터를 적용한다. SAM은 최대 ±3kHz 반송파 오차를 추적하는 PLL을 사용하고 CW는 700Hz BFO를 합성한다.

합성 USB/LSB 동시 입력에서 원하는 측파대가 반대 측파대보다 최소 18dB 이상 강한 조건을 통과했다. 450Hz 반송파 오차가 있는 SAM의 1kHz 변조 복원과 CW 700Hz 출력도 검증했다. 실제 RSP1은 AM→USB→WFM/+300kHz VFO 순서의 30초 시험에서 60,252,192 샘플 중 60,211,200 샘플을 처리했고 오디오 1,505개 버퍼를 드롭 없이 완료했다.

## Native AOT 배포 시험

2026-08-11에 .NET SDK 10.0.302의 WinForms trim 차단을 `_SuppressWinFormsTrimError=true`로 명시적으로 해제하고 `win-x64` Native AOT 게시에 성공했다. 생성된 단일 `NeuroSDR.exe`는 약 21.35MB이며 `--verify`, `--verify-sdrplay`와 실제 RSP1 12초 `--ui-soak`가 모두 종료 코드 0으로 통과했다.

재현 가능한 설정은 `Properties\PublishProfiles\NativeAotWinX64.pubxml`에 있으며 다음 명령으로 게시한다.

```powershell
dotnet publish .\NeuroSDR\NeuroSDR.csproj -p:PublishProfile=NativeAotWinX64 -o .\artifacts\native-aot\win-x64\
```

현재 SDK는 WinForms 자체와 reflection 기반 JSON에 AOT 분석 경고를 출력한다. 따라서 이 결과는 NeuroSDR의 현재 사용 경로가 실제로 동작함을 검증한 것이며, 모든 WinForms API의 공식 AOT 호환성을 뜻하지는 않는다. 외부 hardware plugin의 런타임 assembly 탐색도 AOT 배포에서는 별도 정적 등록 방식을 마련해야 한다.
