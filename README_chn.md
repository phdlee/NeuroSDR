# NeuroSDR

**版本 0.1** — Windows 软件无线电（SDR）接收机 · **KD8CEC**

大多数人**不需要编译**。下载发布版 zip，解压后运行 `NeuroSDR.exe` 即可。

| 文档 | 语言 |
|---|---|
| [English README（主页）](README.md) · [Install](INSTALL.md) | English |
| [한국어](README_kor.md) · [설치](INSTALL_kor.md) | Korean |
| [日本語](README_jpn.md) · [インストール](INSTALL_jpn.md) | Japanese |
| [安装说明](INSTALL_chn.md) | 中文 |

## 功能概要

- 频谱 / 瀑布图，点击、拖曳、滚轮调谐
- AM、SAM、NFM、WFM、USB、LSB、CW、RAW，以及数字话音（DMR、D-STAR、C4FM、FreeDV 等）
- 本地：SDRplay、RTL-SDR、HackRF、Airspy（可选 SoapySDR）
- 远程：WebSDR、KiwiSDR、OpenWebRX
- AF 插件（FT8/FT4、CW、SSTV、传真等）、记忆 / 扫描、IQ 与 AF 录音、卫星场景
- 双音频输出、可选网页遥控、场景 / 设置保存

## 快速开始

1. 在 GitHub **Releases** 下载最新 zip
2. 按 **[INSTALL_chn.md](INSTALL_chn.md)** 完成驱动相关步骤
3. 运行 `NeuroSDR.exe` → 选择 **SOURCE** → **RX START**

日常使用不需要 Visual Studio、.NET SDK 或本源码树。

## 硬件一览

| 电台 | 额外安装 |
|---|---|
| RTL-SDR、HackRF、Airspy | 通常不需要（USB 可能需用**最新 Zadig** 一次；旧版易失败） |
| SDRplay RSP | 需要 — 官方 **SDRplay API 3.x** |
| Lime / Pluto / BladeRF / USRP | 可选 — **PothosSDR**（SoapySDR） |
| WebSDR / Kiwi / OpenWebRX | 无需硬件，仅需网络 |

详见：[INSTALL_chn.md](INSTALL_chn.md)

## 开发与维护

**NeuroSDR 的设计、编程、文档与 GitHub 维护由 AI（Grok 4.7）完成。** 人工审阅与真机测试协助发布，日常工程与仓库工作由 AI 主导。

## 源代码

本仓库面向贡献者与从源码构建。普通用户请使用发布版 zip。

```text
NeuroSDR/          主 Windows 程序
KiwiSDRPlugin/     可选 AF 插件示例
```

---

NeuroSDR · **KD8CEC**

带截图的详细安装/使用手册将发布在博客。完整图文说明与更新请访问 **[https://www.hamskey.com](https://www.hamskey.com)**。
