# NeuroSDR

**バージョン 0.2** — Windows ソフトウェア無線（SDR）受信機 · **KD8CEC**

ほとんどの方は**コンパイル不要**です。リリース用 zip をダウンロードして展開し、`NeuroSDR.exe` を実行してください。

| 文書 | 言語 |
|---|---|
| [英語 README（メイン）](README.md) · [Install](INSTALL.md) | English |
| [한국어](README_kor.md) · [설치](INSTALL_kor.md) | Korean |
| [インストール手順](INSTALL_jpn.md) | 日本語 |
| [中文](README_chn.md) · [安装](INSTALL_chn.md) | Chinese |

## できること

- スペクトラム／ウォーターフォール、クリック・ドラッグ・ホイールでチューニング
- AM, SAM, NFM, WFM, USB, LSB, CW, RAW、およびデジタル音声（DMR, D-STAR, C4FM, FreeDV など）
- ローカル: SDRplay, RTL-SDR, HackRF, Airspy（任意で SoapySDR）
- リモート: WebSDR, KiwiSDR, OpenWebRX
- AF プラグイン（FT8/FT4, CW, SSTV, FAX など）、メモリ／スキャン、IQ・AF 録音、衛星シーン
- デュアル音声出力、Web リモート（任意）、シーン／設定の保存

## クイックスタート

1. GitHub の **Releases** から最新 zip を入手
2. **[INSTALL_jpn.md](INSTALL_jpn.md)** のドライバ案内に従う
3. `NeuroSDR.exe` → **SOURCE** → **RX START**

日常利用に Visual Studio や .NET SDK、このソースツリーは不要です。

## ハードウェア一覧

| ラジオ | 追加インストール |
|---|---|
| RTL-SDR, HackRF, Airspy | 通常不要（USB は**最新 Zadig** が一度必要な場合あり。旧版は失敗しやすい） |
| SDRplay RSP | 必要 — 公式 **SDRplay API 3.x** |
| Lime / Pluto / BladeRF / USRP | 任意 — **PothosSDR** (SoapySDR) |
| WebSDR / Kiwi / OpenWebRX | ハード不要、インターネットのみ |

詳細: [INSTALL_jpn.md](INSTALL_jpn.md)

## 開発・メンテナンス

**NeuroSDR の設計・コーディング・ドキュメント・GitHub 管理は AI（Grok 4.7）が行っています。** 人による確認と実機テストがリリースを支えますが、日々の開発とリポジトリ作業は AI 主導です。

## ソースコード

このリポジトリは貢献者・ソースビルド向けです。一般利用はリリース zip を使ってください。

```text
NeuroSDR/          メイン Windows アプリ
KiwiSDRPlugin/     任意 AF プラグインのサンプル
```

---

NeuroSDR · **KD8CEC**

スクリーンショット付きの詳しいインストール／使い方マニュアルはブログに掲載予定です。図解ガイドと更新情報は **[https://www.hamskey.com](https://www.hamskey.com)** をご覧ください。
