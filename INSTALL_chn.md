# NeuroSDR 安装说明（发布版 zip）

面向**下载发布压缩包并直接运行**的用户。无需编译。

## 1. 获取

1. 在 GitHub **Releases** 下载最新 Windows zip（一般为 x64）。
2. 解压到可写目录（例如 `C:\NeuroSDR`），请完整解压。

## 2. zip 内已包含

- `NeuroSDR.exe` 及该构建所需的支持文件
- RTL-SDR / HackRF / Airspy 等捆绑的 native 库（如 `native\`）
- 内置 AF / IQ 插件

使用 RTL-SDR、HackRF、Airspy 时，**通常不必再单独下载额外 DLL**。

## 3. 按电台额外安装

### RTL-SDR / HackRF / Airspy（USB）

1. 插入加密狗。
2. 若 Windows 未识别为可用 SDR，用 **[Zadig](https://zadig.akeo.ie/)** 安装一次 WinUSB。
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
| SOURCE 中无加密狗 | Zadig / 线缆 / 端口；SDRplay 需安装 API |
| RX START 失败 | 其他 SDR 软件是否占用设备；状态栏提示 |
| 无声音 | SETUP 音频设备、OUT1、Windows 音量 |
| 仅远程异常 | URL、换站点、防火墙 |

## 7. 日常使用不需要

Visual Studio、.NET SDK、克隆本 Git 仓库、自行编译 native DLL。

**下载发布包 → 解压 →（如需）厂商 / USB 驱动 → NeuroSDR.exe**

---

[English](INSTALL.md) · [한국어](INSTALL_kor.md) · [日本語](INSTALL_jpn.md) · [README_chn.md](README_chn.md)
