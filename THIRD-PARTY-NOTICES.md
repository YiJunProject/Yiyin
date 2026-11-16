# 第三方组件

易音原创代码使用仓库根目录的个人非商业使用许可。下列组件保留自己的许可，禁止商用的条款不改变这些组件本身的独立授权。

| 组件 | 固定来源 | 许可与说明 |
| --- | --- | --- |
| .NET / WPF / ASP.NET Core | .NET 10，自包含 Windows x64 发布 | MIT，随运行时提供的 LICENSE 与 ThirdPartyNotices 保留 |
| RtAudio | `06f97b633c06ea747d6478f0e87b6e1178104511` | MIT-style；本项目只编译 WASAPI 后端，附本地 WASAPI 补丁 |
| Steinberg VST3 SDK | `3cdf9ca5d1f5b1b21e0a86832aa4abe55607bd96`，public.sdk `586dc5e6c8012c3e4b01c79389375cbe96bdb1da` | MIT；保留路径补丁及首次消息代理适配文件中的版权声明 |
| Xaymar VST2 independent interface | `339d4f31590bf77c0d0d248e09a380ac6285e069` | BSD-3-Clause；独立 ABI 定义，不是旧 Steinberg VST2 SDK |
| nlohmann/json | `native-audio/deps/json.hpp`，SHA256 见原生依赖锁 | MIT |
| Steinberg ASIO SDK interface 2.3 | 由独立 ASIO 适配器使用的接口头文件 | Steinberg 专有协议，非 MIT；不在本仓库重新分发 SDK 头文件 |

全文声明位于 `native-audio/licenses/`。ASIO、VST 名称及第三方产品商标属于各自权利人。安装包不包含任何商业效果器或声卡驱动。

产品权利人已于 2026-11-16 确认现有 ASIO 协议覆盖易音及其公开安装包分发。该确认不向源码接收者转授权 ASIO SDK。自行构建或分发启用 ASIO 的变体时，构建者必须持有适用的 ASIO 许可；默认源码构建关闭 ASIO，可使用 WASAPI。私人协议及凭据不进入仓库。

源码依赖通过官方上游固定版本获取，不把构建者电脑上的插件、驱动、工程或配置上传到仓库。`native-audio/src/vst3_plugprovider.cpp` 改自上述 VST3 SDK 的 MIT 文件，其原许可仍适用。
