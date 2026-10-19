# 项目维护约定

- 除非用户明确要求保持或覆盖版本，每次用户请求的项目更改都递增补丁版本号；用户指定版本号时以指定值为准。
- 同一次请求内的实现、修复和验证只升级一次，不按文件或提交次数重复升级。
- 同步更新 `src/AssemblyInfo.cs`、README 和相关发布说明。Release 的版本信息以构建后的 EXE 为准。
- 发布时保留旧版本目录，生成 `dist/<版本号>/RA2FlakASMR.exe`，并更新默认的 `dist/RA2FlakASMR.exe`；按现有流程推送 main 并验证自动 Release 成功。
