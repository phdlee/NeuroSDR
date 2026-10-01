# NeuroSDR 安装说明（发布版 zip）

面向**下载发布压缩包并直接运行**的用户。无需编译。

## 1. 获取

1. 在 GitHub **[Releases](https://github.com/phdlee/NeuroSDR/releases)** 下载 **NeuroSDR-v0.1-win-x64.zip**（或最新 zip）。
2. 解压到可写目录（例如 `C:\NeuroSDR`），请完整解压。

带截图的详细安装/使用说明请见 **[https://www.hamskey.com](https://www.hamskey.com)**（KD8CEC）。

## 2. zip 内已包含

- `NeuroSDR.exe` 及该构建所需的支持文件
- RTL-SDR / HackRF / Airspy 等捆绑的 native 库（如 `native\`）
- 内置 AF / IQ 插件

使用 RTL-SDR、HackRF、Airspy 时，**通常不必再单独下载额外 DLL**。

## 3. 按电台额外安装

### RTL-SDR / HackRF / Airspy（USB）

1. 插入加密狗。
2. 若 Windows 未识别为可用 SDR，用 **Zadig** 安装一次 WinUSB。
   - 请使用**最新版 Zadig**：**[https://zadig.akeo.ie/](https://zadig.akeo.ie/)**（或 [libwdi releases](https://github.com/pbatard/libwdi/releases) 最新资源）。撰写时为 **Zadig 2.9**。
   - **不要使用旧版 Zadig。** 在较新的 Windows 上，旧版常常无法正确绑定 WinUSB，导致 RTL-SDR 不会出现在 SOURCE 中。
   - 在 Zadig 中勾选 **Options → List All Devices**，选择 SDR 的 bulk 接口（多数 RTL-SDR：`Bulk-In, Interface (Interface 0)` / Realtek），选 **WinUSB**，再 **Install/Replace Driver**。
3. 在 NeuroSDR 中选择对应 **SOURCE**。

### SDRplay

请从 SDRplay 官网安装官方 **SDRplay API 3.x**。发布 zip **不包含**该 API。

### SoapySDR（Lime、Pluto 等）

可选。安装 **[PothosSDR](https://github.com/pothosware/PothosSDR/wiki)** 等后，SOURCE 中可能出现相应设备。

### WebSDR / Kiwi / OpenWebRX

无需本地电台或驱动，只需网络与站点 URL。

## 4. 首次运行

1. 启动 `NeuroSDR.exe`
2. 选择 **SOURCE** → **RX START**
3. 点击频谱，或在 VFO 输入频率后按 Enter
4. 在 **SET** 中配置音频、性能、插件与网页遥控

设置目录：`%LocalAppData%\NeuroSDR\`

## 5. 可选功能

- **VOICE GUIDANCE**：离线 eSpeak NG（非必需）
- **WEB REMOTE**：仅在 SETUP 中开启后可用（默认关闭）

## 6. 故障排除

| 现象 | 检查 |
|---|---|
| SOURCE 中无加密狗 | **最新 Zadig**（勿用旧版）/ 线缆 / 端口；SDRplay 需安装 API |
| RX START 失败 | 其他 SDR 软件是否占用设备；状态栏提示 |
| 无声音 | SETUP 音频设备、OUT1、Windows 音量 |
| 仅远程异常 | URL、换站点、防火墙 |

## 7. 日常使用不需要

Visual Studio、.NET SDK、克隆本 Git 仓库、自行编译 native DLL。

**下载发布包 → 解压 →（如需）厂商 / USB 驱动 → NeuroSDR.exe**

---

[English](INSTALL.md) · [한국어](INSTALL_kor.md) · [日本語](INSTALL_jpn.md) · [README_chn.md](README_chn.md)

---

NeuroSDR · **KD8CEC**

带截图的详细安装/使用手册将发布在博客。完整图文说明与更新请访问 **[https://www.hamskey.com](https://www.hamskey.com)**。
