# NeuroSDR 아키텍처

```text
SDRplay / RTL-SDR / HackRF / File / Synthetic
                    │
              ISampleSource
                    │ Complex32 IQ blocks
          ┌─────────┴──────────┐
     SpectrumProcessor     AudioDemodulator
          │                  │
   Spectrum + Waterfall   NCO → dynamic decimation → FIR 5:1 → Channel FIR → Demodulator → WaveOut
```

RF 중심 주파수는 하드웨어가 샘플링하는 source별 capture 범위를 정하고, VFO 주파수는 그 범위 안에서 실제 청취할 신호를 선택한다. RSP1은 2/5/8/10 MS/s와 1.536/5/8/8 MHz tuner filter를 대응시켜 사용한다. `AudioDemodulator`의 재귀형 복소 NCO가 VFO 오프셋을 0 Hz로 이동한다. 블록마다 회전 계수의 sin/cos를 한 번만 계산하고 샘플별로 복소 곱을 사용하며, 4,096샘플마다 크기를 정규화해 장시간 수치 오차를 제한한다.

표시 viewport는 RF 중심과 샘플레이트가 정한 캡처 범위 안에서 별도의 중심과 대역폭을 가진다. `SpectrumPipeline`은 viewport span에 따라 2,048~131,072점 FFT를 선택하고 전용 bounded worker에서 처리한다. 따라서 확대 화면은 낮은 해상도의 전체 대역 FFT bin을 늘리지 않으며, 오디오 및 장치 callback도 큰 FFT 계산을 기다리지 않는다. 대역폭이 바뀔 때만 워터폴 히스토리를 비우고 중심 주파수나 VFO 드래그 중에는 기존 히스토리를 유지하면서 최신 frame을 계속 추가한다.

RF 표시 계층은 `ISpectrumRendererPlugin`과 `IWaterfallRendererPlugin`으로 분리된다. Core control은 주파수/dB 축, VFO/filter overlay와 모든 mouse interaction을 소유한다. Spectrum plugin은 FFT trace를, waterfall plugin은 bitmap/history와 palette를 소유한다. 따라서 두 표시 방식을 독립 교체해도 DSP, tuning 및 RF center preview 규칙은 유지된다. 내장형과 `plugins\display` 외장형은 같은 계약과 catalog를 사용한다.

Spectrum/Waterfall level control은 FFT data를 변경하지 않고 renderer에 독립 dB offset을 전달한다. `SpectrumRenderFrame.LevelOffsetDb`와 waterfall `Push`의 `levelOffsetDb`가 이를 나타낸다. 따라서 level 조정은 signal 측정, squelch 및 복조에 영향을 주지 않는다.

RF 중심 드래그는 preview와 commit의 두 단계다. preview 중에는 cached spectrum과 waterfall bitmap을 수평 이동시키고 hardware center는 유지하므로 native stream이 중단되지 않는다. mouse-up에서 RF update를 한 번 수행하고 spectrum generation을 교체한다. retune 이전에 이미 queue에 있던 block을 고려해 두 번째 새 spectrum frame에서 preview offset을 제거한다.

`MemoryChannelStore`는 주파수, mode, filter bandwidth와 설명을 JSON으로 원자적 교체 저장한다. `MemoryScanScheduler`와 `RangeScanScheduler`는 UI 및 hardware와 독립된 시간 기반 상태기계이며 dwell과 signal hold만 결정한다. Main form은 scheduler가 반환한 채널 또는 주파수를 기존 VFO/recenter 경로로 호출한다. Signal hold 값은 현재 mode의 실제 USB/LSB/center filter 방향에 해당하는 FFT bin peak에서 계산한다.

`AudioPostProcessor`는 복조 후 48 kHz stream에 독립 AF HPF/LPF, squelch gate, envelope AGC, adaptive low-level expander와 biquad notch를 적용한다. 모든 설정은 atomic property로 전달되며 audio state는 한 DSP thread에서만 갱신한다. `AudioSpectrumPipeline`은 post processing 전 audio를 bounded queue로 분기해 8,192점 FFT를 별도 worker에서 계산하므로 waveOut이나 수신 callback을 기다리게 하지 않는다.

오디오 출력은 최대 두 개의 `WaveOutPlayer`와 `AudioPostProcessor` pair로 구성된다. 복조 원본을 출력별로 clone한 뒤 각 processor에서 독립 SQL gate를 적용하고 선택한 waveOut device로 전달한다. 한 출력 장치의 open 실패는 다른 출력과 SDR 수신을 중단시키지 않는다.

`AppSettingsStore`는 source-generated System.Text.Json으로 창, receiver, DSP, plugin과 audio channel 상태를 `%LocalAppData%\NeuroSDR\settings.json`에 원자적으로 저장한다. UI 자동 소크는 사용자 설정을 오염시키지 않도록 저장을 건너뛴다.

`AudioSpectrumWaterfallControl`은 0~20 kHz AF 범위를 positive FFT에서 매핑한다. 두 edge drag event는 독립 AF high-pass/low-pass cutoff만 바꾸며 RF channel filter에는 영향을 주지 않는다. 마우스 이동은 bitmap/FFT 상태를 초기화하지 않으므로 드래그 중에도 AF spectrum과 waterfall 갱신이 계속된다.

`ISampleSource`는 장치 발견과 스트리밍을 UI에서 분리한다. 콜백은 블로킹하지 않고 IQ 블록만 전달해야 한다. DSP는 제조사 SDK 타입을 받지 않으며 정규화된 `Complex32`만 처리한다.

`RtlSdrSampleSource`와 `HackRfSampleSource`는 compile-time DLL 참조 없이 native export를 동적으로 바인딩한다. `SampleBlockDispatcher`는 서로 다른 native sample 형식을 정규화하고 작은 콜백을 고정 크기 블록으로 합친다. 큐는 8블록으로 제한되며 DSP가 늦으면 새 블록을 버리고 drop 계수를 올린다. RTL-SDR의 blocking `read_async`는 전용 background thread에서 실행하고 정지 시 `cancel_async` 후 join/close한다. HackRF는 `stop_rx`가 callback 종료를 보장한 뒤 dispatcher와 장치를 해제한다.

하드웨어 발견은 `ISampleSourceProvider`와 `HardwareSourceCatalog`가 담당한다. 내장 provider도 외부 plugin과 같은 계약을 사용하며 UI는 구체 하드웨어 타입을 참조하지 않는다. 외부 assembly 탐색과 객체 생성은 시작 시 한 번만 수행되고, 수신 hot path는 provider를 우회해 `ISampleSource`에서 DSP로 직접 연결된다.

`IqWaveRecorder`는 sample event에서 받은 immutable IQ 블록 참조를 bounded queue에 넣고 별도 task에서 PCM16 stereo로 변환한다. `IqWaveFileSampleSource`는 같은 파일을 실시간 속도로 재생하는 고정 RF 중심 source이다. 따라서 녹음 재생도 실제 장치와 같은 FFT, VFO, 복조 및 오디오 경로를 사용한다.

향후 확장 경계는 다음과 같다. WebSDR/KiwiSDR 모듈은 네트워크 연결·압축 해제·재연결을 provider 내부에 두고 `ISampleSource`로 정규화된 IQ를 출력한다. SSTV, Packet, WeatherFax, RTTY, FT8/FT4 등의 기존 디코더는 hardware provider에 섞지 않고 별도의 decoder plugin 계약으로 구성하며, 선택한 VFO의 복소 baseband 또는 복조 audio stream을 입력으로 받는다. 이 decoder 계약과 모듈 로더는 실제 통합 단계에서 각 기존 모듈의 입력 형식을 확인한 뒤 확정한다.

`SdrplaySampleSource`는 API 3.x의 네이티브 수명주기와 콜백을 소유한다. 16비트 정수 I/Q를 `Complex32`로 정규화한 뒤에만 공통 경로로 전달하므로 RTL-SDR과 HackRF 어댑터도 같은 DSP를 사용할 수 있다. UI 종료 또는 소스 전환 때 `Uninit → ReleaseDevice → Close` 순서로 해제한다.

`AudioContinuityTest`는 총량 기반 soak가 놓치는 주기적 끊김을 검출한다. RF callback의 시간축과 실측 sample rate, 복조 PCM 생성률, stage 처리시간, dispatcher queue 깊이, waveOut pending buffer와 underrun을 함께 기록한다. FFT를 제외한 시험과 실제 비동기 표시를 포함한 시험을 같은 기준으로 비교할 수 있다. `WaveOutPlayer`의 underrun은 재생이 시작된 뒤 pending buffer가 0이 된 순간을 계수한다.

HackRF는 시작 전에 `hackrf_usb_api_version_read`로 장치 API를 확인한다. API 1.03 이하의 구형 시험 장치에서는 실측 전달률에 맞춘 장치 rate 명령을 사용한다. 2/5/8/10/20 MS/s는 2배 명령, 15 MS/s는 비선형 divider를 보정한 24.77 MHz 명령을 사용한다. 연결된 시험 장치는 전체 UI에서 15 MS/s 논리 rate에 약 14.96 MS/s, 20 MS/s에 약 20 MS/s IQ를 전달했다. DSP와 UI는 논리 rate만 보므로 PCM은 약 48 kHz를 유지한다. 최신 API 장치는 보정 없이 2/5/8/10/15/20 MS/s를 사용한다. RF AMP는 LNA/VGA gain과 분리된 `IRfAmplifierSampleSource` 기능이며 UI에는 PortaPack 표기와 같은 `+14 dB`로 표시하고 안전한 기본값은 OFF다.

SDRplay와 HackRF가 전달하는 작은 네이티브 콜백은 2/5 MS/s에서 32,768샘플, 8 MS/s 이상에서 65,536샘플 블록으로 병합한다. 고속 입력의 managed callback 횟수를 절반으로 낮추면서도 2 MS/s의 오디오 지연은 유지한다. unsafe 포인터 루프로 native IQ를 직접 정규화하고 처리 완료 배열은 pool로 되돌린다. 32-block bounded 큐가 순간 burst를 흡수하며 실시간 처리가 계속 밀릴 경우 오래된 지연을 누적하지 않는다. RF FFT 제출은 5~30 FPS로 제한되며 FFT 품질과 무관하게 오디오·decoder 경로는 원본 품질을 유지한다.

입력 전용 IQ analyzer는 bounded background runner에서 처리하며 capture thread에서 analyzer의 장시간 decoder lock을 기다리지 않는다. 조건부 IQ 출력 플러그인의 `ProducesIqOutput` 조회도 lock-free여야 한다. 실제 UI 전용 `UiPipelineTrace`는 hot path에서 interlocked counter만 갱신하고 UI timer가 초당 한 번 CSV를 기록하므로, 독립 진단에서 빠지는 plugin/display/dual-output 비용과 큐 손실을 함께 관찰할 수 있다.

`AudioDemodulator`의 첫 rate reduction은 capture-rate-scaled cascaded moving-average anti-alias filter다. 좁은 모드는 2단, 넓은 WFM은 통과대역 보존과 처리량을 위해 1단을 사용하고 이후 기존 channel FIR이 최종 선택도를 만든다. RSP의 8 MHz 아날로그 IF가 추가로 받아들이는 강신호가 디지털 250 kHz 영역에 alias되는 현상을 줄이면서, RSP 8 MS/s 실시간 처리가 유지되도록 단수를 제한한다. RSP dispatcher는 모드 변경과 USB burst를 흡수하는 64-block bounded queue를 사용한다.

선택적으로 IQ를 교체하는 plugin은 실제 output 모드에서만 capture thread에서 동기 실행한다. 분석 전용 상태에서는 한 블록만 허용하는 background worker로 격리하고 worker가 바쁠 때는 배열 복사도 생략한다. Kiwi Timecode scope 결과는 10 FPS로 제한해 UI message queue가 spectrum, waterfall, 종료 이벤트를 밀어내지 않게 한다.

Windows `waveOutProc`는 완료 이벤트만 설정한다. `waveOutUnprepareHeader`와 메모리 해제는 별도 정리 스레드가 수행해 멀티미디어 콜백 교착을 피한다. 복조기의 작은 출력은 20ms/960샘플 블록으로 합쳐 오디오 드라이버에 제출한다.

WFM은 250 kS/s 경로에서 직접 복조한다. 협대역 모드는 127탭 FIR 감산기로 50 kS/s까지 낮춘다. 이후 AM/SAM/NFM/CW는 실수 저역통과 계수의 복소 FIR을 사용하고, USB/LSB는 저역통과 프로토타입을 양수 또는 음수 중심으로 변조한 255탭 복소 FIR을 사용해 반대 측파대를 제거한다. SAM PLL과 CW BFO도 이 50 kS/s 도메인에서 동작한다.

샘플 프로젝트 SDR#와 SDR++에서 확인한 핵심 패턴은 장치 계층, 신호 처리 계층, 표시 계층의 분리와 FFT 출력의 독립 갱신이다. 코드는 복사하지 않았으며 NeuroSDR의 구현은 새로 작성했다. 특히 SDR++는 GPL-3.0이므로 향후에도 소스 복사는 피하고 알고리즘/구조 비교 자료로만 취급한다.

Native AOT를 고려해 런타임 리플렉션, 동적 프록시 및 불필요한 관리 패키지를 피한다. 하드웨어 네이티브 API는 얇은 명시적 ABI 어댑터에서만 사용한다. .NET 10.0.302에서는 WinForms trim 차단을 명시적으로 해제한 `win-x64` Native AOT 게시와 현재 RSP1 UI 경로가 실제 검증을 통과했다. 다만 WinForms 내부 AOT 경고와 runtime plugin assembly 탐색 제약이 남아 있으므로 일반 빌드와 AOT 배포를 별도 publish profile로 유지한다.
