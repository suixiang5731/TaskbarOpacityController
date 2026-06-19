# Taskbar Opacity Controller

Windows 10 任务栏透明控制器。

当前版本优先保证低占用和稳定性：没有渐隐动画，不枚举全系统窗口，不修改任务栏子窗口透明度，也不强制重绘 Explorer。程序只在目标状态变化时切换任务栏主窗口透明度。

## 行为

| 场景 | 任务栏 |
| --- | --- |
| 桌面 / Win+D / 显示桌面 | 透明 |
| 有正常应用窗口可见且未最小化 | 显示 |
| 鼠标进入屏幕底部 40px 区域 | 显示 |
| 开始菜单 | 显示 |
| 搜索 | 显示 |
| 任务视图 | 显示 |
| Alt+Tab | 显示 |

## 使用方法

在项目目录运行：

```powershell
dotnet run
```

也可以直接运行发布后的 `TaskbarOpacityController.exe`。

启动后，右下角系统托盘会出现：

```text
任务栏透明控制器
```

## 托盘菜单

右键系统托盘图标：

| 菜单项 | 作用 |
| --- | --- |
| 立即显示任务栏 | 立刻恢复任务栏显示 |
| 开机自启动 | 为当前 Windows 用户开启或关闭开机自启 |
| 退出 | 恢复任务栏并退出程序 |

## 开机自启动

`开机自启动` 会写入当前用户注册表：

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
```

不需要管理员权限。

## 注意事项

- 如果任务栏没有隐藏，先确认鼠标没有停在屏幕底部 40px 区域。
- QQ 等应用贴边收起后，程序会把这种“几乎不可见的窗口”按桌面处理。
- 程序只改变任务栏透明度，不会修改 Windows 的“自动隐藏任务栏”设置。

## 发布

Release 已配置为 win-x64 自包含单文件程序：

```powershell
dotnet publish -c Release
```

生成的 exe 位于：

```text
bin\Release\net8.0-windows\win-x64\publish\
```
