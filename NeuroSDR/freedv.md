# NeuroSDR FreeDV 통합 기록

이 문서는 2026-09-08에 MinGW로 `libcodec2.dll`을 붙여 넣은 **임시(Windows GCC) 구현**을 나중에 **Visual Studio(MSVC)만으로 다시 컴파일·교체**할 때 쓴다.

후속 AI는 이 파일만 읽고:

1. 아래 “삭제할 것”을 제거하거나 교체하고
2. 같은 사용자 동작을 유지한 채 codec2를 MSVC로 다시 붙이면 된다.

**사용자 요구:** 실행 PC에 MinGW/MSYS2 설치 금지. 배포는 `NeuroSDR.exe` + 네이티브 DLL(기존 `dsdfme.dll`과 동일 형태).

---

## 왜 MinGW인가 (MSVC 포팅 시 이 장벽을 깨면 됨)

공식 라이브러리: [drowe67/codec2](https://github.com/drowe67/codec2) (LGPL 2.1). FreeDV 모뎀+보코더 API는 `src/freedv_api.h` / `freedv_api.c`.

메인테이너 문서: Windows는 **MinGW로 만든 DLL을 링크**하라고 하고, MSVC C99(VLA 등) 미지원을 이유로 **MSVC 포크를 권하지 않는다**.

이 머신에서 MSVC(VS 18 / `cl` 19.51)로 공유 라이브러리를 돌리면 예:

- `error C2057` / `C2466` — 가변 길이 배열 (`lpc.c`, `nlp.c`, `codec2.c`, …)
- gcc 전용 플래그 `-Wno-strict-overflow` → `D8021`
- `m.lib` 링크 (MinGW/Unix `libm`)

따라서 **런타임이 MinGW가 필요한 것이 아니라, 그 당시 codec2를 Windows에서 빌드하는 유일한 실용 경로가 GCC였다.**

ESP32 ezDV 등도 **같은 C 소스**를 **GCC(ESP-IDF)** 로 컴파일한다. 알고리즘을 다시 짠 것이 아니다.

MSVC 포팅이 가능해지면: VLA를 `malloc`/고정 상한으로 바꾸고, `CMakeLists.txt`의 gcc 플래그를 `if(NOT MSVC)`로 가리고, `libm`을 Windows에서 빼면 된다. 공식 업스트림이 MSVC를 받으면 패치 없이 그 태그를 쓰면 된다.

---

## 사용한 codec2 스냅샷

| 항목 | 값 |
|------|-----|
| 저장소 | `https://github.com/drowe67/codec2.git` |
| 브랜치 | `main` (shallow clone `--depth 1`) |
| 커밋 | `310777b` (`git rev-parse --short HEAD`) |
| 라이브러리 버전 문자열 | cmake `1.2.0` |
| 산출물 이름 | `libcodec2.dll` (MinGW 기본; MSVC면 `codec2.dll`일 수 있음) |
| 링크 | `-static-libgcc -static` → 의존성 **KERNEL32.dll, msvcrt.dll만** (사용자에 gcc DLL 불필요) |
| 아키텍처 | **x64(PE32+)만** 빌드됨. x86 DLL은 없음 |

로컬 클론/빌드 디렉터리(gitignore, 배포 금지):

- `NeuroSDR/NeuroSDR/native/codec2-src/` — 소스 + 아래 CMake 패치
- `NeuroSDR/NeuroSDR/native/codec2-mingw/` — MinGW 빌드 트리
- `NeuroSDR/NeuroSDR/native/codec2-build/` — 실패한 MSVC 시도 잔여 가능

배포에 넣은 파일:

- `NeuroSDR/NeuroSDR/native/win-x64/libcodec2.dll`
- `NeuroSDR/NeuroSDR/native/CODEC2-COPYING.txt` (업스트림 `COPYING`)

스크립트: `NeuroSDR/NeuroSDR/native/build-codec2.ps1`

### 실제 MinGW cmake 명령

```text
cmake -S codec2-src -B codec2-mingw -G "MinGW Makefiles" ^
  -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON -DUNITTEST=OFF ^
  -DCMAKE_C_COMPILER=C:/msys64/mingw64/bin/gcc.exe ^
  -DCMAKE_MAKE_PROGRAM=C:/msys64/mingw64/bin/mingw32-make.exe ^
  "-DCMAKE_SHARED_LINKER_FLAGS=-static-libgcc -static"
cmake --build codec2-mingw --target codec2 -j 8
copy codec2-mingw\src\libcodec2.dll → native\win-x64\libcodec2.dll
```

x86 나중에: **i686** gcc로 **별도 빌드 디렉터리**, 결과를 `native\win-x86\libcodec2.dll`. x64 DLL을 32비트 프로세스에 로드하면 안 된다.

### codec2-src에 가한 CMake 패치 (업스트림 아님)

MSVC 재시도/폐기 시 참고. MinGW 성공 빌드에 꼭 필요한 것은 `libm` 가드 정도.

1. `codec2-src/CMakeLists.txt`
   - `CMAKE_C_FLAGS`에 gcc `-Wall -Wno-strict-overflow`를 **MSVC가 아니면**만 적용
   - MSVC일 때 `/W3 /D_CRT_SECURE_NO_WARNINGS`, `CMAKE_WINDOWS_EXPORT_ALL_SYMBOLS`
   - `CMAKE_C_FLAGS_DEBUG`/`RELEASE`의 `-g -O2`/`-O3`를 MSVC에서 덮어쓰지 않게 `if(NOT MSVC)`
2. `codec2-src/src/CMakeLists.txt`
   - `generate_codebook` / `codec2`의 `target_link_libraries(... m)`를 `if(UNIX OR MINGW)`로 제한 (MSVC `m.lib` 없음)

---

## 신호 경로 (동작 계약 — 포팅 후에도 유지)

HF FreeDV(예: **7.1670 MHz LSB**)는 **NFM 판별기가 아니다.**

1. IQ → `AudioDemodulator`, `RadioMode.FREEDV`
   - 채널 필터·오디오는 **LSB 또는 USB**와 동일 (`i * 2.2f`)
   - `AudioDemodulator.SsbLower`: `true` = LSB. Auto면 **10 MHz 미만 LSB**, 이상은 USB. 설정 `LSB`/`USB`로 고정 가능
2. 48 kHz float AF → int16 → `FreeDvSession`
   - 6샘플 박스카로 **8 kHz** (codec2 모뎀 레이트)
   - `freedv_nin()`만큼 모아서 `freedv_rx()`
3. 복호 음성 8 kHz int16 → `DigitalVoicePlayback.PushDecodedMono8k` → 48 kHz 링 → `DigitalVoicePacer` (DMR과 같은 OUT)

DMR/D-STAR/C4FM은 계속 NFM → `dsdfme.dll`. FreeDV는 **그 경로를 타지 않는다.**

원격 Kiwi/WebSDR: `RadioModes.DemodMode(FREEDV, ssbLower)` → `LSB` 또는 `USB` (`NbFm` 아님).

기본 필터 폭 **2700 Hz**. 워터폴 패스밴드는 `RadioModes.FilterRange`.

### 모드 Auto

병렬 인스턴스 순서: **700D(7) → 700E(13) → 1600(0) → 700C(6)**. 싱크 잠금, ~2.5초 로스 후 재탐색. 700D/700E는 `freedv_set_eq(true)`, 전 모드 `freedv_set_squelch_en(true)`.

2020/RADE/LPCNet는 넣지 않음.

UI: 디지털 모드 **마지막** 버튼 **FDV** (C4FM 다음, NFM 앞). `_modeBox` 항목 `"FREEDV"`.

---

## 새로 만든 파일 (포팅 시 P/Invoke 층만 갈아끼우거나 유지)

| 경로 | 역할 |
|------|------|
| `Plugins/DigitalVoice/Codec2Native.cs` | `cdecl` P/Invoke, DLL 이름 `libcodec2` |
| `Plugins/DigitalVoice/FreeDvSession.cs` | 다운샘플, Auto, `freedv_rx` |
| `native/build-codec2.ps1` | MinGW 재빌드. MSVC로 바꾸면 **이 스크립트는 폐기/교체** |
| `native/CODEC2-COPYING.txt` | 라이선스. 유지 |
| `native/.gitignore` | `codec2-src/`, `codec2-mingw/`, `codec2-build/` 등 |
| **이 파일** `freedv.md` | 컨버전 가이드 |

`DigitalVoicePlayback.PushDecodedMono8k`는 기존 파일에 **추가된 메서드**.

---

## 수정한 기존 파일 (기능은 유지, MinGW 전용 가정만 제거)

### `Core/RadioMode.cs`

- enum **맨 끝**에 `FREEDV` (JSON은 `UseStringEnumConverter`라 중간 insert보다 안전)
- `IsFmDigitalVoice` = DMR/DSTAR/C4FM만
- `IsDigitalVoice` = 위 + FREEDV
- `DemodMode(mode, freedvLower)` — 예전 시그니처 `DemodMode(mode)`만 쓰던 호출부는 **SsbLower를 넘기도록** 바뀜
- `FilterRange(...)` 추가
- `DefaultBandwidth(FREEDV) = 2700`

### `Plugins/DigitalVoice/DigitalModeEngine.cs`

- `FreeDvSession` 보유. FREEDV일 때 dsdfme 시작 안 함
- `ApplyFreedvOptions(modem, lowerSideband)`
- `ProcessAf`에서 FREEDV면 `_freedv.PushPcm16_48k`

### `Plugins/DigitalVoice/DigitalVoicePlayback.cs`

- `PushDecodedMono8k`

### `HostNativeDllResolver.cs`

- `codec2.dll` ↔ `libcodec2.dll` 별칭

`Codec2Native.LibraryAvailable()`는 `win-x86` 폴더를 아직 안 본다. MSVC/x86 포팅 때 `HostNativeDllResolver` 후보와 맞출 것.

### DSP / UI / 원격

- `Dsp/AudioDemodulator.cs` — `SsbLower`, FREEDV 필터·복조·AF cutoff
- `Dsp/SubVfoReceiver.cs` — FREEDV를 SSB처럼 폭 계산
- `Controls/SpectrumWaterfallControl.cs` — `SsbLower`, `FilterFrequencyRange` → `RadioModes.FilterRange`
- `Controls/DigitalModeOptionsPanel.cs` — FreeDV MODE/SIDEBAND 콤보, 캡션, WAV 힌트(SSB AF)
- `Settings/AppSettings.cs` — `FreeDvModem`, `FreeDvSideband` (`Auto` / `700D` / `700E` / `1600` / `700C`, `Auto`/`LSB`/`USB`)
- `frmNeuroSDR.Designer.cs` — `_modeFreedvButton` 텍스트 `FDV`, 모드 버튼 폭 45px·간격 재배치, combo `FREEDV`
- `frmNeuroSDR.cs` — 버튼 맵, 클릭, `_rxShiftControls`, `DemodMode(..., SsbLower)`, `FilterRange`, `ConfigureDisplay` → `SyncFreedvSideband`
- `frmNeuroSDR.DigitalMode.cs` — `ApplyFreedvOptions` / `SyncFreedvSideband`, 툴팁, WAV 제목
- `frmNeuroSDR.Satellite.cs` — `DemodMode` 인자, 대역폭 2700
- `Hardware/WebRadioSampleSource.cs` — `FREEDV => DemodMode.Lsb` (실제 적용은 호스트가 LSB/USB로 변환 후 호출하는 것이 정석)
- `Settings/NeuroSDRPresetCatalog.cs` — FREEDV 기본 BW
- `NeuroSDR.csproj` — `native/win-x64/libcodec2.dll` (x64), `native/win-x86/libcodec2.dll` (x86, 파일 있을 때만)

### 디자이너 버튼 좌표 (y=121, 폭 45)

AM 10, SAM 56, DMR 102, DSTAR 148, C4FM 194, **FDV 240**, NFM 286, WFM 332, USB 378, LSB 424, CW 470, RAW 516.

---

## P/Invoke 계약 (`src/freedv_api.h`)

`CallingConvention.Cdecl`. 심볼은 장식 없음 (`dumpbin /EXPORTS`).

| 엔트리 | 용도 |
|--------|------|
| `freedv_open(int mode)` | `struct freedv*` |
| `freedv_close` | |
| `freedv_nin` | 다음에 넣을 모뎀 샘플 수 |
| `freedv_rx(freedv, short speech[], short demod[])` | 반환 = 음성 샘플 수 |
| `freedv_get_n_max_speech_samples` / `freedv_get_n_max_modem_samples` | 버퍼 크기 |
| `freedv_get_sync` | |
| `freedv_get_modem_stats(int* sync, float* snr_est)` | |
| `freedv_set_squelch_en(bool)` | `UnmanagedType.I1` |
| `freedv_set_eq(bool)` | 700D/E |

모드 상수: `FREEDV_MODE_1600=0`, `700C=6`, `700D=7`, `700E=13`.

입력은 **8 kHz int16 모뎀(SSB AF)**. 호스트가 48k→8k.

MSVC DLL이 `codec2.dll`로 나가면 `Codec2Native`의 `Dll` 문자열과 resolver 별칭만 맞추면 된다.

---

## MSVC 컨버전 절차 (후속 AI)

1. **동작 회귀 기준:** 7.167 MHz LSB, FDV, BW 2700, Auto 또는 700D, 싱크 후 VOICE OUT. DMR 경로 불변.
2. 업스트림 codec2를 **패치 없이** MSVC+`BUILD_SHARED_LIBS`로 빌드해 본다. 실패하면 VLA/C99만 최소 수정한 **별도 트리**(`native/codec2-msvc/` 등)를 쓰고, MinGW 산출물은 버린다.
3. 산출 DLL을 `native/win-x64/` (및 x86이면 `win-x86`)에 넣고, 의존성이 **MSVC 런타임 + KERNEL32**인지 확인. gcc DLL(`libgcc_s_seh-1.dll` 등)을 다시 끌어오면 안 된다.
4. `NeuroSDR.csproj` 복사 항목의 파일명만 새 DLL에 맞춘다.
5. **삭제해도 되는 것**
   - `native/codec2-src/`, `codec2-mingw/`, `codec2-build/`
   - `native/build-codec2.ps1` (MSVC 빌드 스크립트/csproj Native 프로젝트로 대체)
   - 정적 링크된 **MinGW** `libcodec2.dll` (새 MSVC DLL로 교체)
6. **삭제하면 안 되는 것 (제품 기능)**
   - `RadioMode.FREEDV` 및 SSB 복조 분기
   - FDV 버튼·옵션 패널·48k→8k→playback
   - `FreeDvSession` 논리 (DLL만 바꾸면 됨)
7. C#에서 `libcodec2` / `cdecl`이 새 링커와 맞는지 `dumpbin /EXPORTS`로 `freedv_open` 확인.
8. 이 `freedv.md` 상단에 “MSVC 포팅 완료, 커밋/생성기/플래그”를 追記한다.

---

## 의도적으로 하지 않은 것

- FreeDV 2020 / LPCNet / RADE
- C#으로 OFDM/LDPC 재구현
- codec2를 NeuroSDR 단일 EXE에 정적 링크 (dsdfme와 같은 DLL 동봉 정책)
- 이 머신에서 x86 `libcodec2.dll` 빌드 (i686 gcc 없음)

---

## 라이선스

codec2: **LGPL 2.1**. 동적 링크 + `licenses/CODEC2-COPYING.txt` 동봉. MSVC로 바꿔도 동일.
