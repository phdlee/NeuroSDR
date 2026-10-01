# NeuroSDR インストール手順（リリース zip）

コンパイルは不要です。**リリース用アーカイブを展開して実行**する方向けの手順です。

## 1. 入手

1. GitHub **[Releases](https://github.com/phdlee/NeuroSDR/releases)** から **NeuroSDR-v0.1-win-x64.zip**（または最新 zip）をダウンロードします。
2. 書き込み可能なフォルダに**すべて**展開します（例: `C:\NeuroSDR`）。

スクリーンショット付きの詳しい手順は **[https://www.hamskey.com](https://www.hamskey.com)**（KD8CEC）をご覧ください。

## 2. zip に含まれるもの

- `NeuroSDR.exe` とビルドに必要な付属ファイル
- RTL-SDR / HackRF / Airspy 用の同梱 native ライブラリ（`native\` など）
- 標準の AF / IQ プラグイン

RTL-SDR・HackRF・Airspy のために**追加の DLL を別途取る必要は通常ありません。**

## 3. ラジオ別の追加手順

### RTL-SDR / HackRF / Airspy（USB）

1. ドングルを接続します。
2. Windows が SDR として認識しない場合は **Zadig** で WinUSB を一度インストールします。
   - **最新の Zadig** を使ってください: **[https://zadig.akeo.ie/](https://zadig.akeo.ie/)**（または [libwdi releases](https://github.com/pbatard/libwdi/releases) の最新アセット）。本稿時点では **Zadig 2.9**。
   - **古い Zadig は使わないでください。** 現行 Windows では古い版が WinUSB を正しく付けられず、RTL-SDR が SOURCE に出ないことがよくあります。
   - Zadig で **Options → List All Devices** を有効にし、SDR の bulk インターフェース（多くの RTL-SDR: `Bulk-In, Interface (Interface 0)` / Realtek）を選び **WinUSB** → **Install/Replace Driver**。
3. NeuroSDR で該当 **SOURCE** を選びます。

### SDRplay

公式 **SDRplay API 3.x** を SDRplay サイトからインストールしてください。リリース zip だけでは API は入りません。

### SoapySDR（Lime, Pluto など）

任意です。**[PothosSDR](https://github.com/pothosware/PothosSDR/wiki)** などを入れると SOURCE に出ることがあります。

### WebSDR / Kiwi / OpenWebRX

ローカルラジオ・ドライバ不要。インターネットとサイト URL だけで利用できます。

## 4. 初回起動

1. `NeuroSDR.exe` を起動
2. **SOURCE** → **RX START**
3. スペクトラムをクリック、または VFO に周波数を入れて Enter
4. **SET** で音声・性能・プラグイン・Web リモートを設定

設定の保存先: `%LocalAppData%\NeuroSDR\`

## 5. 任意機能

- **VOICE GUIDANCE**: オフライン eSpeak NG（必須ではない）
- **WEB REMOTE**: SETUP で有効化したときのみ（既定オフ）

## 6. トラブル

| 症状 | 確認 |
|---|---|
| SOURCE にドングルがない | **最新 Zadig**（旧版不可）/ ケーブル / ポート、SDRplay は API |
| RX START 失敗 | 他の SDR アプリがデバイスを占有していないか、ステータス行 |
| 音が出ない | SETUP の音声デバイス、OUT1、Windows 音量 |
| リモートだけ異常 | URL・別サイト・ファイアウォール |

## 7. 日常利用に不要なもの

Visual Studio、.NET SDK、この Git のクローン、native DLL の自前ビルド。

**リリース取得 → 展開 →（必要なら）ベンダ／USB ドライバ → NeuroSDR.exe**

---

[English](INSTALL.md) · [한국어](INSTALL_kor.md) · [中文](INSTALL_chn.md) · [README_jpn.md](README_jpn.md)

---

NeuroSDR · **KD8CEC**

スクリーンショット付きの詳しいインストール／使い方マニュアルはブログに掲載予定です。図解ガイドと更新情報は **[https://www.hamskey.com](https://www.hamskey.com)** をご覧ください。
