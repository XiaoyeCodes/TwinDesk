# 前端依赖准备

当前仅M0-03依赖清单及锁文件，尚无正式控制台。M1的NX门槛通过后，React/TypeScript页面在此实施。现有根目录Demo保持原状。

在仓库根目录运行 `./scripts/verify-web-dependencies.ps1`，用项目本地Node/npm在新建的artifacts目录执行锁定安装、TypeScript类型检查、React运行检查和Vite生产构建。测试页面只存在于临时证据目录，不启动服务、不控制任何应用，也不能当作浏览器远控验收。

依赖取自npm官方registry，直接版本固定，间接依赖由package-lock.json的版本和integrity固定。`npm ci --ignore-scripts`在本次工具链上验证原生可选包可直接使用；若新平台缺包应报错，不允许静默跳过构建。正式发布仍需完整第三方许可清单、漏洞复核和发布环境验证。
