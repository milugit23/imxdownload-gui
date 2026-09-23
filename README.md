# imxdownload-gui — i.MX6ULL 镜像烧写工具（Windows 图形界面版）

把正点原子《阿尔法 Linux 开发板》配套的 Ubuntu 命令行烧写工具 **imxdownload**（imxdownload.c / imxdownload.h，Edit by zuozhongkai，2019/6/10，V1.1）移植到 Windows，做成图形界面。用于给 **i.MX6ULL** 裸机例程生成带 IVT/DCD 头的 `.imx` 镜像，并写到 SD 卡的正确偏移处，使开发板能从 SD 卡启动。

核心烧写逻辑与原版完全等价（镜像逐字节一致），界面、磁盘枚举、写盘方式全部改为 Windows 原生实现（Win32 `\\.\PhysicalDriveN` + `DeviceIoControl`）。

---

## 功能

| 功能 | 说明 |
|------|------|
| 生成镜像 | 把 `.bin` 固件加上 IVT/DCD 头，产出与官方 imxdownload 逐字节一致的 `.imx` |
| 直接烧写 | 把镜像写到目标 SD 卡的**偏移 1024 字节**处（等价于 `dd bs=512 seek=2`），并回读校验 |
| DDR 表选择 | 512MB / 256MB DDR3 两张 DCD 表，对应不同内存容量的板子 |
| 检测 `.imx` | 分析已有的 `.imx` 镜像用的是 512MB 还是 256MB 表 |
| 拖放支持 | `.bin` 直接拖进窗口即可选中（管理员模式下也能拖，详见下文） |

## 环境要求

- Windows 10/11（x64）
- 运行需要 .NET 10 Desktop Runtime（`net10.0-windows`）；如用下方 csc 方式自行编译，则无任何运行时依赖

## 从源码构建

**方式一：dotnet（推荐）**

```bash
dotnet build -c Release
# 产物：bin\Release\net10.0-windows\imxdownload-gui.exe
```

**方式二：Windows 自带 csc（免安装任何东西）**

源码是单文件、纯 .NET Framework 兼容 API，可以直接用系统自带的编译器：

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /optimize+ /codepage:65001 /out:imxdownload-gui.exe imxdownload-gui.cs
```

## 使用方法

1. **选固件**：点「浏览...」或把 `.bin` 拖进窗口。注意是 `arm-linux-gnueabihf-gcc` 编译出来的 `.bin`，不是 `.elf` / `.imx`。
2. **选 DDR 容量**：512MB 或 256MB DDR3。**选错板子起不来**，串口会毫无输出。不确定就两种都试；也可以拿能启动的 `load.imx` 用「检测」功能反查它用的哪张表。
3. **选目标磁盘**：点「刷新」枚举，下拉框里选你的 SD 卡（按容量认准）。
4. **开始烧写**：确认弹窗后写入 + 回读校验。完成后拔卡插到开发板，拨码开关切到 SD 卡启动，上电。

**命令行参数：**

| 参数 | 作用 |
|------|------|
| （无） | 目标磁盘列表锁定为「只显示可移动磁盘」，界面上没有开关可取消 |
| `--allow-fixed` | 放开过滤，固定磁盘也列出来（选中后额外弹一次二次确认） |

`--allow-fixed` 是兜底口子：部分 USB 读卡器 / USB-SATA 硬盘盒会把自己上报成固定盘，默认过滤会挡住它们。除非确认属于这种情况，否则不要带此参数——**写错盘会毁掉硬盘数据**。

## 镜像格式

与官方 imxdownload 生成的镜像布局完全一致：

```
偏移 0    ~ 1023    IVT + DCD 表（1KB，小端）
偏移 1024 ~ 3071    补 0
偏移 3072 ~         你的代码（.bin 内容）
```

整块镜像写入目标磁盘的**偏移 1024 字节**（第 2 个扇区）处：

```
镜像：  [ IVT/DCD 1KB | 2KB 补零 | 代码 ]
             ↓ 写到 SD 卡偏移 1024
SD 卡：  [ 引导保留区 1KB | IVT/DCD | ... | 代码在 4096 处 ]
```

> ⚠️ 用 balenaEtcher / Win32DiskImager 烧本工具生成的 `.imx` **不能启动**——它们从第 0 扇区整盘写，位置不对。要烧卡请直接用本程序的「开始烧写」，或 Linux 下 `dd if=xxx.imx of=/dev/sdX bs=512 seek=2`。

## 安全机制

- **可移动磁盘过滤**：默认只列可移动磁盘，固定盘根本不进下拉框（防手滑）。
- **双重确认**：烧写前弹窗确认；固定盘（`--allow-fixed` 模式下）再弹一次。
- **写前卸载卷**：写入前对目标盘上的所有卷执行 `FSCTL_LOCK_VOLUME` + `FSCTL_DISMOUNT_VOLUME`，防止 Windows 缓存回写把数据盖掉。
- **写后回读校验**：逐字节比对，不一致会报告差异数。
- **自动提权**：程序启动时自己用 `runas` 请求管理员权限（写 `\\.\PhysicalDriveN` 必须），无需右键管理员运行；UAC 弹「否」则降级继续，烧写功能不可用但生成/检测可用。

## 常见问题

| 现象 | 原因与处理 |
|------|-----------|
| 下拉框里找不到卡 | ① 读卡器/卡没插好，插好后点「刷新」；② 读卡器把自己上报成固定盘 → 带 `--allow-fixed` 启动 |
| 打不开磁盘（错误码 5） | 权限不足，以管理员身份运行 |
| 卷锁定失败 | 有程序占用该盘：资源管理器窗口、杀毒软件、播放器预览等，关掉再试 |
| 写入后校验失败、串口无输出 | 重试；若串口毫无输出大概率是 DDR 容量选错了，换另一张表再烧 |
| 《01_leds》例程的 load.imx 检测出是 256MB 表 | 属实，正点原子该例程用的就是 256MB 表 |
| 检测 `.imx` 显示「无法判定」 | 该文件不是本工具/官方 imxdownload 生成的（多半是 NXP 官方 u-boot 的 .imx，DCD 结构不同），看文件名里的 ddr256/ddr512 字样即可 |

## 与原版 imxdownload 的差异

- 界面：命令行 → WinForms 图形界面
- 写盘：`dd bs=512 seek=2` → Win32 物理磁盘句柄直写（同样写到偏移 1024）
- 新增：镜像检测（反查 DDR 表）、写后回读校验、可移动磁盘过滤、卷卸载
- 产物：与原版生成的 `.imx` 逐字节一致（两版工具生成的文件可互换使用）

## 致谢

- 原版 imxdownload：正点原子（ALIENTEK）《阿尔法 Linux 开发板》配套例程，zuozhongkai，2019/6/10，V1.1
- 本项目仅作学习交流用，烧写有风险，写错盘概不负责。
