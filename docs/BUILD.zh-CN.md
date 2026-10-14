# 源码预览版构建

[English](BUILD.md) · [安装快速入门](QUICKSTART.zh-CN.md)

v1.0.0-rc.1 暂不附可运行二进制包。客户端安装入口需要哈希／MVID 清单，不能把源码中的客户端直接当作安装包运行。前置条件：Windows x64、Python 3.11+、.NET 8 SDK、合法的兼容 AutoCAD 2026 安装。实测宿主 R25.1.74.0.0；2026.1.2 的 .NET 10 更新尚未认证。先核对 COMPATIBILITY.md。

在仓库根目录执行：

```powershell
dotnet build third_party/bimwright-dwg-mcp/fork/src/server/Bimwright.Dwg.Server.csproj -c Release
dotnet build third_party/bimwright-dwg-mcp/fork/src/plugin-acad26/Bimwright.Dwg.Plugin.Acad26.csproj -c Release -p:AutoCad2026Dir="C:\Program Files\Autodesk\AutoCAD 2026"
python -m unittest discover -s tests
dotnet run --project third_party/bimwright-dwg-mcp/fork/tests/FoundationLedgerContract -c Release
dotnet run --project third_party/bimwright-dwg-mcp/fork/tests/PluginJobContract -c Release
```

原生项目引用已安装的 Autodesk API／SDK 程序集，禁止将其再分发。这里只导出选定的离线合同，未附私有历史实机夹具；编译和离线测试通过不等于 CAD 实机验收。

发布二进制仍需审查依赖、清单、哈希／MVID，并在新隔离环境做针对性的安装、加载和读回核验。不能把原本地版 ZIP 改名当公开包。本次源码可构建原生桥接，但底座的一键本地安装流程仍待经审核的公开二进制资产。
