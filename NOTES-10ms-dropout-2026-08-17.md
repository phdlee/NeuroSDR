# RSP1 10 MS/s 끊김 수정 메모 (2026-08-17)

회사 PC로 폴더 복사 후 Debug/일반실행에서 RSP1 10 MS/s USB 수신이 주기적으로 끊기던 문제와, 그에 대한 수정·계측 결과.

## 증상

- Debug(F5): 약 1초에 한 번 끊김
- 디버거 없이 시작(Ctrl+F5, 여전히 Debug 빌드): 약 1~1.5초에 한 번
- AF Plugin을 켜면 간격이 길어지고, 모두 끄면 더 짧은 간격으로 끊김 → 병목은 플러그인 자체가 아니라 **실시간 DSP 예산 초과** 패턴

## 원인 (계측으로 확정)

10 MS/s에서 RF 블록 크기 131,072 → 블록 주기 **약 13.1 ms**.

| 조건 | 복조 평균 | RF drop | audio underrun |
|---|---|---|---|
| 수정 전 Debug 전체 UI | 15~23 ms | 초당 수백만 | ~1회/초 |
| 수정 후 Debug 전체 UI | ~10 ms | 0 | 0 |
| Release 전체 UI | ~2 ms | 0 | 0 |

Debug USB 복조가 블록 주기를 넘기면 `SampleBlockDispatcher` 큐(16블록)가 가득 차고 IQ drop → PCM 공백 → waveOut underrun이 초 단위로 반복된다.

단독 `--diagnose-audio` (복조+표시만)에서는 Debug도 평균 ~11 ms로 borderline였고, 이중 오디오·AF 스펙트럼·WinForms UI가 붙으면 15 ms+로 넘어갔다.

이전 KiwiTIMECODE lock 역전 이슈와는 별개. 이번 로그에서 `iq_avg_ms`는 무시할 수준이었다.

## 수정 내용

### 1. RF/AF 스펙트럼 FFT 대배열 재사용 (`SpectrumProcessor`, `AudioSpectrumPipeline`)

- 매 프레임 `new Complex[N]`, `new float[N]` (N이 최대 131,072) 하던 것을 슬롯 재사용
- RF 표시 품질·FFT 크기 선택 로직은 그대로
- Gen2/LOH 압박을 줄이기 위한 할당 제거 (기능 저하 없음)

### 2. 복조 PCM 버퍼 재사용 (`AudioDemodulator`)

- 블록마다 `List<float>` + `ToArray()` 제거 → scratch/publish 배열 재사용
- VFO NCO를 float 경로로 정리 (필터 탭 수는 동일)

### 3. 수신 중 GC latency (`frmNeuroSDR`)

- Start 시 `GCLatencyMode.SustainedLowLatency`, Stop 시 복원
- Server GC 등에서 미지원이면 try/catch로 무시

### 4. 채널 FIR / 디시메이션 FIR 핫패스 (`FirFilters.cs` + `AudioDemodulator`)

**이번 끊김 해결의 핵심.**

- `ComplexFirFilter`, `ComplexFirDecimator` 내부 delay/tap을 `Complex32` 구조체 배열 대신 **float I/Q 분리 배열**로 변경
- 탭 길이·계수 설계(`FirDesigner.LowPass` / `ComplexBandPass`)·수학식은 동일
- Debug에서 USB 복조 ~11 ms → ~7.8 ms (단독), UI 포함 ~10 ms로 13.1 ms 예산 안으로 진입
- Debug `--ui-soak=25 --sample-rate=10000000` (FTX/CW 포함) EXIT 0, RF drop/underrun 0 확인

### 건드리지 않은 것

- fldigi FSK/RTTY/CW/FAX (`Plugins/Fldigi/*`) — Kahn/raised-cosine/FFT 스캔 경로 **미변경**
- Kiwi FSK/FAX/CW 플러그인 DLL 경로 **미변경**
- 샘플레이트 강제 하향, FFT 품질 하향, 안티앨리어싱 단수 축소, 큐를 키워 drop을 숨기는 식의 완화 **하지 않음**

## 성능 관련 메모 / 영향 가능 지점

### 여유 마진 (Debug 10 MS/s USB)

- 블록 예산 13.1 ms 대비 Debug UI 복조 ~10 ms → **여유 약 2~3 ms**
- 아래가 동시에 켜지면 다시 빡빡해질 수 있음:
  - **Sub VFO가 RF 캡처 안에 있고** `OutputChannel` 1/2 또는 AF route로 **추가 복조**가 돌 때
  - Audio1 + Audio2 동시 출력 (현재 설정 둘 다 Enabled)
  - 좁은 viewport → FFT 131k + 매 프레임 1 MB IQ 복사 (`SpectrumPipeline.Submit`)
  - AF analyzer 다수 + 매 블록 PCM `Clone`
  - Debug + 디버거 연결(F5)은 Ctrl+F5보다 더 무거움

### Release

- 복조 ~2 ms로 여유 큼. 10 MS/s 전대역/협대역 관측은 **Release 구성 권장**

### FIR float 누적

- 예전 FIR 합산은 `double` accumulator, 지금은 `float`
- 255탭 오디오 FIR에서 실청취상 의미 있는 품질 저하를 의도한 변경은 아님
- 수치 회귀가 걱정되면 USB/LSB sideband 검증(`--verify`)과 실청취로 확인 (2026-08-17 `--verify` 통과)

### 스펙트럼 결과 버퍼 슬롯 (3개)

- `SpectrumProcessor`가 결과 `float[]`를 3슬롯 순환
- UI가 `_pendingSpectrum`을 한 프레임 이상 붙잡고 **같은 슬롯이 다시 쓰이기 전**에 소비하는 전제
- 현재는 UI timer가 `Interlocked.Exchange`로 가져가므로 문제 없음. 나중에 spectrum 배열을 장시간 보관하는 코드를 넣으면 **덮어쓰기**에 주의

### 복조 `PublishAudio` 재사용 배열

- `AudioDemodulator.Process`가 반환하는 `float[]`는 인스턴스 내부 버퍼
- 호출측에서 보관하려면 **반드시 복사** (`frmNeuroSDR`의 AF/audio 경로는 `CopyAudio` / plugin host의 Clone 사용)
- 새 코드가 반환 배열을 복사 없이 큐에 넣으면 다음 블록에서 내용이 바뀜

### Sub VFO + 이중 오디오

- 설정 예: SUB가 CH2를 소유하고 주파수가 캡처 밖이면 `OutputOwner`가 null → MAIN이 CH1·CH2에 동일 PCM을 씀 (설계상)
- SUB가 캡처 안에 들어오면 MAIN + SUB 각각 풀 USB 복조 → Debug 10 MS/s에서 CPU 거의 2배

### 진단 명령 (재현용)

```text
# 복조+표시만 (UI 없음)
NeuroSDR.exe --diagnose-audio=rsp --sample-rate=10000000 --seconds=20 --with-display --mode=USB --frequency=14074000 --gain=70

# 전체 WinForms 경로 + 초당 CSV
NeuroSDR.exe --ui-soak=25 --sample-rate=10000000 --pipeline-log
```

CSV: 실행 폴더 `diagnostics/ui-pipeline-*.csv`  
주목 열: `demod_avg_ms`, `callback_avg_ms`, `source_dropped`, `source_queue`, `audio1_underruns`, `gc2`

### 이전(8/15) 실패 로그와의 관계

- `diagnostics/audio-continuity-rsp-10000000.json` / `ui-pipeline-20260815-*` 는 **수정 전** 상태(대량 drop, demod ~15 ms, underrun 누적)로 남겨 둔 참고용
- 수정 후 재계측은 `artifacts/diag-debug` 또는 새 `diagnostics` 타임스탬프를 볼 것

## 관련 파일

- `NeuroSDR/Dsp/FirFilters.cs` — FIR 핫패스
- `NeuroSDR/Dsp/AudioDemodulator.cs` — PCM 재사용, float NCO, FIR 호출
- `NeuroSDR/Dsp/SpectrumProcessor.cs` — FFT work/result 재사용
- `NeuroSDR/Dsp/AudioSpectrumPipeline.cs` — AF FFT 버퍼 재사용
- `NeuroSDR/frmNeuroSDR.cs` — SustainedLowLatency, `CopyAudio`
- `NeuroSDR/Hardware/SdrplaySampleSource.cs` — 10 MS/s 블록 131,072 / 큐 16 (이번 수정에서 용량 미변경)
