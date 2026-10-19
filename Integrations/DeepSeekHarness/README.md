# DeepSeek Harness 联动插件

这个可选插件把 DeepSeek Harness 的工作事件写到本机 JSON 快照，供 DeepSeek 大肥鱼桌宠读取。它只服务于大肥鱼形象，不包含 CrossPet 的角色图片或其他桌宠素材。

## 安装

先关闭 DeepSeek Harness，然后在便携包根目录运行 PowerShell：

```powershell
.\Integrations\DeepSeekHarness\install.ps1 -ProfileDirectory "$HOME\.dsh\profiles\web"
```

如果使用其他 Harness profile，将 `-ProfileDirectory` 换成该 profile 的实际目录。脚本会复制插件到 `%LOCALAPPDATA%\DeepSeekFishPet\harness-plugin`，在所选 profile 的 `package.json` 和 `cordis.patch.yml` 中加入自己的配置，并建立 `node_modules` 链接。修改过的配置文件会留下带时间戳的备份；不会启动 Harness。

安装后重新打开 Harness。桌宠右键菜单中的“跟随 DeepSeek 工作状态”默认开启，“显示 DeepSeek 余额”默认关闭。未安装插件时，桌宠的普通互动不受影响。

余额查询默认关闭。需要余额时，在该 profile 的 `cordis.patch.yml` 中找到 `deepseek-fish-pet` 条目，加上配置：

```yaml
- insert:
    - id: deepseek-fish-pet
      name: '@local/dsh-plugin-deepseek-fish-pet'
      config:
        balanceEnabled: true
```

保存后重新打开 Harness，并在桌宠右键菜单启用“显示 DeepSeek 余额”。

## 卸载

关闭 Harness 后运行：

```powershell
.\Integrations\DeepSeekHarness\install.ps1 -ProfileDirectory "$HOME\.dsh\profiles\web" -Uninstall
```

卸载会移除该 profile 中由本插件加入的依赖、Cordis 条目和链接。`%LOCALAPPDATA%\DeepSeekFishPet\harness-plugin` 的共享副本会保留，以免影响其他 profile。

## 写入内容

- `%LOCALAPPDATA%\DeepSeekFishPet\state\deepseek-state.json`：姿态、事件名、工具名和时间戳。
- `%LOCALAPPDATA%\DeepSeekFishPet\state\deepseek-quota.json`：启用余额查询后，汇总后的余额文字及低余额标记。

插件不会把消息正文、工具参数、工具结果或 API Key 写入快照。桌宠只读这些文件；状态过期后不再显示。余额获取依赖 Harness 当前的账户或 API Key 配置，无法获取时显示“余额暂不可用”。

事件与余额汇总代码改编自 [CrossPet](https://github.com/lokicorvus/crosspet)，按 MIT 许可使用，见 [LICENSE-CrossPet.txt](LICENSE-CrossPet.txt)。
