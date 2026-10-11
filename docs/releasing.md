# 发布流程(Release Runbook)

SmartAdmin 前后端同仓、**同版本号一起发**。后端以 NuGet 包发布(版本在 CI 打包时经 `-p:Version` 注入,取 `web/packages/admin/package.json` 的版本号,也就是将要建的 tag 的号);前端内核以 npm 包 `smart-admin-web` 发布,版本写在 `web/packages/admin/package.json`,workspace 根与 `web/template` 的 `version` 和它同号(模板的号就是模板登录页页脚显示的版本),模板对 `smart-admin-web` 的依赖精确钉在 `X.Y.Z`(前端页面调的端点跟着后端版本走,浮动范围会装到比 NuGet 新的包);文档站顶栏徽章在 `site/.vitepress/config.ts`(中/英各一处)。这几处必须一起 bump,否则 npm 上的包、用户在 UI / 文档站看到的版本和实际跑的 NuGet 包对不上。

版本号规则:**主版本号 = 内核所用的 .NET 主版本**(10.x 对应 .NET 10,下一次大版本跟随下一个 .NET LTS),次版本号加功能,修订号修 bug;全部包共用一个版本号。破坏性变更尽量攒到换 .NET 大版本时一起发;周期内确需破坏的在次版本发,并在 CHANGELOG 对应版本段落顶部加粗提示。

节奏:**开发在 `dev`,发布在 `main`**——先经 PR 把 `dev` 合进 `main`(`main` 受保护,不能直接 push)。**合并后 `.github/workflows/release.yml` 自动接手**:合并进 `main` 的提交里前端版本号变了、且还没有对应的 `v*` tag,就校验 CHANGELOG / 前端版本 / 模板依赖版本 / 文档站徽章一致、被发布的提交上 `ci gate` 已绿,构建、测试、模板冒烟、打 13 个 nupkg 与前端 tgz,全绿才经 **Trusted Publishing** 推 nuget.org、发 npm,**最后一步才建 `v*` tag 与 GitHub Release**。仓库里不保存任何 NuGet API key 或 npm token。**合并发版 PR 就等于发包,发出去的版本不可撤回**:nuget.org 只能 unlist,npm 上用过的版本号也不能再用;唯一的否决点是合并之前(取消自动合并)。真正的闸门是 workflow 的 verify 阶段:它红了不会发包。

## 一、准备(可逆,在 `dev` 上做)

1. **写 CHANGELOG 条目**:把待发布的条目写进顶部的 `## Unreleased`(定版由下一条的脚本完成),按 Keep a Changelog 归类(Added / Fixed / Changed / Removed)。条目按**真实 diff** 写,别照提交标题猜——快速拉自上个 tag 以来的提交:

   ```powershell
   git log vX.Y.<上一个>..dev --oneline
   ```

2. **定版并 bump 版本号**:一条命令做完(后端不用改文件,pack 时注入):

   ```powershell
   node scripts/release-prepare.mjs X.Y.Z [YYYY-MM-DD]   # 发布日缺省取今天;先加 --dry-run 只看不写
   ```

   它按 `release.yml` 的 verify 同样的口径一次改完并自检:每处替换都断言恰好命中预期次数,对不上就整体中止、一个文件都不写;不提交,也不替你写条目。
   - CHANGELOG:把 `## Unreleased` 下的条目移到 `## X.Y.Z - YYYY-MM-DD`,顶部留空的 `## Unreleased` 给下一版攒
   - `web/packages/admin/package.json`(npm 包本身)、`web/package.json`(workspace 根)、`web/template/package.json` 的 `version`
   - `web/template/package.json` 的 `dependencies["smart-admin-web"]` 改成 `X.Y.Z`(精确版本,不带 `^`)
   - `web/package-lock.json`:在 `web/` 下跑 `npm install --package-lock-only` 同步,并核对 diff 只有版本号相关的 5 对行
   - **`site/.vitepress/config.ts`**:英文 nav 与中文(`zh`)nav **各一处**版本徽章(文档站顶栏显示的版本号;`release.yml` 的 verify 会数这两处)

   脚本以「自检通过」结束即可。agent 流程见 `skills/smart-release.md`(`/smart-release`)。

3. **本地验绿**(可选但推荐):`ci` workflow 会在 push 上跑同样的检查,本地先过一遍能少等一轮。本机内存紧,**别并发**跑 vue-tsc 和 dotnet test,一次一个:

   ```powershell
   Push-Location web; try { npm run lint; if ($LASTEXITCODE -ne 0) { throw 'Vue lint failed' }; npm run format:check; if ($LASTEXITCODE -ne 0) { throw 'Vue format check failed' }; npm test; if ($LASTEXITCODE -ne 0) { throw 'Vue tests failed' }; npm run build; if ($LASTEXITCODE -ne 0) { throw 'Vue build failed' } } finally { Pop-Location }
   dotnet test backend/SmartAdmin.slnx -c Release; if ($LASTEXITCODE -ne 0) { throw '.NET tests failed' }
   pwsh templates/smoke-test.ps1; if ($LASTEXITCODE -ne 0) { throw 'template smoke failed' }
   ```

   模板冒烟(`smoke-test.ps1`)把内核打进临时本地 feed,再 `dotnet new smart-app` 生成消费方项目并 build、启动,验的是消费方拿到包后的第一条命令。改了 `site/**` 还要跑 `cd site; npm run lint:prose; npx vitepress build`。

4. 提交:`chore(release): 准备 X.Y.Z — changelog 定版 + 前端与文档站版本号`(提交信息规范见 [`skills/write-commit.md`](../skills/write-commit.md):中文主题 + conventional-commit),推 `dev`(`ci` 在 PR 上并行跑,见第二节;改了容器相关文件时的 `docker-smoke` 另看)。

## 二、发布(合并进 `main` 即触发发包)

`main` 受保护:禁止直接 push,只能从 `dev` 经 PR 合并,且必检 `ci gate` 通过(它汇总 `ci` 里的所有 job:后端四库、模板冒烟、前端、e2e、codeql、依赖审计,被门控跳过的算通过,失败或取消才变红;对管理员也生效,不需要人工 review)。本地合并后直接 `git push origin main` 会被拒(`GH006`)。`main` 上有历史发版留下的 PR 合并提交,`dev` 没有它们,所以 `main` 通常不是 `dev` 的祖先,这是正常的,合并由 PR 完成。

```powershell
# 1. 推 dev(不必等这次推送的 ci:PR 上的 ci 对同一 SHA 并行跑同一套检查,先等再开 PR 只会把约 20 分钟变成两倍)
git fetch origin
git push origin dev

# 2. 开 PR,并设成「必检 ci gate 绿了自动合并」(仓库已开 Allow auto-merge;保持 merge commit,不 squash、不 rebase)
gh pr create --base main --head dev --title "chore(release): X.Y.Z" --body-file <说明文件>
gh pr merge <N> --auto --merge
gh pr view <N> --json state,mergeStateStatus,autoMergeRequest   # 等 state 变成 MERGED

# 3. 本地 main 快进到 origin/main;不重置本地分支
git fetch origin
if (git show-ref --verify --quiet refs/heads/main) { git switch main } else { git switch --track -c main origin/main }
git merge-base --is-ancestor main origin/main
if ($LASTEXITCODE -ne 0) { throw 'Local main has commits origin/main does not; inspect it before merging.' }
git merge --ff-only origin/main
```

同一个 head SHA 上会有两批同名检查(`dev` 推送触发的和 PR 触发的),PR 触发的那批没跑完前 `mergeStateStatus` 一直是 BLOCKED,整轮约 20 分钟,跑绿后自动合并。`ci gate` 红了不会合并:修好后推到 `dev`,PR 自动更新并重跑,推完核对 `autoMergeRequest` 仍在。`ci gate` 一直没出现、PR 一直 BLOCKED 时,说明这次改动整个被 `ci` 的 `paths-ignore` 跳过了(纯文档改动);发版 PR 必然改 `web/` 里的版本号,不会这样,先看 `ci` 有没有跑起来。PR 页面显示冲突,说明有人直接改了 `main`(它有 `dev` 没有的内容提交):先在本地把 `origin/main` 合进 `dev`,确认版本号 / CHANGELOG 没被顶回旧值,再推。

合并进 `main` 之后 `release` workflow 自动接手,**不需要也不应该手动打 tag**:

- **detect**:前端版本号相对这次推送前变了、且还没有对应的 `vX.Y.Z` tag,才算发版;版本没变的合并(文档、修复)整个跳过 verify 与 publish,普通合并永远不会发包。
- **verify**:CHANGELOG / 前端版本 / 模板依赖版本 / 文档站徽章一致;被合并进来的提交(合并提交的第二父,即 PR 的 head)上必须有 `ci gate` 成功的记录,且合并提交的树与它逐字节相同,否则直接失败、不发包。`main` 上因此不再重跑一轮 `ci`:发出去的就是 PR 上已经跑绿的那棵树。通过后构建、测试(该提交上 `backend (sqlite)` 已绿时跳过 SQLite + Redis 测试)、模板冒烟、打 13 个 nupkg 与前端 tgz、抓 openapi。
- **publish**:OIDC 换临时 key 推 nuget.org、发 npm,**最后一步才**建 `vX.Y.Z` tag 与 GitHub Release,并核对 tag 指向被发布的提交。

去 Actions 页盯 `release` workflow(或 `gh run list --workflow release.yml --limit 1` 拿到 id 后 `gh run watch <id> --exit-status`):

- **verify** 红了不会发包,也没有 tag:修好后推到 `dev` 再走一遍 PR。
- **publish** 跑完,包就在 nuget.org 与 npm 上了,只能 unlist / 弃号、再发修订版。publish 中途失败(比如 NuGet 推完、npm 没发出去)时没有 tag(tag 最后才建),修好外部配置后重跑失败的 job,或在 `main` 上手动触发 `gh workflow run release.yml --ref main -f dry_run=false`:NuGet 带 `--skip-duplicate`,npm 那步先查版本在不在,已发的都会跳过。

**手动演练**:`gh workflow run release.yml --ref <分支> -f dry_run=true`。完整跑 verify,publish 只换 NuGet 临时 key、npm 只 `--dry-run`(版本已在 npm 上则跳过),不推包、不建 tag;演练的提交上同样要有 `ci gate` 成功记录。npm 的 OIDC 在不真发布时验证不了,演练证明不了这一点。

**不要手动推 `v*` tag**:`release` 不再由 tag 触发,而 detect 看到版本已有 tag 就判成「不是发版」,这次发布会被悄悄跳过。

`release` 只跑 SQLite 腿 + Redis 契约测试,而且被发布的提交上 `backend (sqlite)` 已跑绿时连这一步也跳过(构建一次、晋级发布;带版本号的构建、模板冒烟、打包、抓 openapi 照跑);四库矩阵与 Docker 冒烟由 `ci` / `docker-smoke` 在 dev 推送与 PR 上跑,`ci gate` 是 `main` 的必检,release 还会再核对一次。

## 三、发布后

- **核对 nuget.org**:`SmartAdmin`、`SmartAdmin.Templates` 等的 `X.Y.Z` 可见(索引有几分钟延迟);空目录里 `dotnet new install SmartAdmin.Templates::X.Y.Z` 后 `dotnet new smart-app` 能还原。
- **核对 npm**:`npm view smart-admin-web@X.Y.Z version` 输出 `X.Y.Z`;npmjs.com 包页的版本旁有 provenance 标记(证明是从本仓库 `release.yml` 构建发布的)。空目录里 `npx degit SimpleOne-X/SmartAdmin/web/template web; cd web; npm install` 能装上新版。
- **GitHub Release 与 tag**:workflow 在发布成功后自动建,说明取自 CHANGELOG 对应版本段落,附 13 个 nupkg、前端 tgz 与 `openapi.json`,不用手工建。「发了什么」的真源始终是 tag + CHANGELOG.md。
- **文档站**:`docs` workflow 在 `main` 上构建并自动部署到 GitHub Pages(仓库设置里 Pages 的 Source 选 GitHub Actions)。部署步骤按运行时的仓库可见性门控,仓库若转私有会退回只构建不部署,那时需要把 `site/.vitepress/dist` 发到自己的静态托管。核对站顶导航徽章已是 `X.Y.Z`。
- **仅当 `main` 出现 `dev` 没有的内容时,才把 `main` 合回 `dev`**(比如 hotfix 直接落在 `main`、发布工具往 `main` 提交了版本号),否则 `dev` 会真的分叉。常规发版不需要:PR 合并提交不带内容,`main` 的树与已发布的 `dev` 相同,`dev` 显示「落后 `main`」只是整洁问题,不影响下一次 PR(`dev` 落后几个合并提交时开 PR 照常合并)。先看有没有内容差异,为空就不用合;有差异再合,提交信息用 `-m` 传,`git merge` 不支持 `-F -` 从标准输入读消息,会直接报错、合并也不会发生:

  ```powershell
  git fetch origin
  git diff --stat origin/dev origin/main   # 为空 = 无内容差异,不用合;有差异才继续往下
  git switch dev
  git merge origin/main --no-ff -m "chore(release): 把 main 上的内容同步回 dev"
  git push origin dev
  ```

- 发布成功后,前端 `package.json` 与文档站导航徽章已是新版本,`dev` 继续开发,下一版从新的 `## Unreleased` 攒起。

## 四、一次性准备(换账号 / 换仓库时才需要)

- **nuget.org Trusted Publishing 策略**:登录 nuget.org → 用户名 → Trusted Publishing → Create。Package owner `Andy_Zhong`,Repository Owner `SimpleOne-X`,Repository `SmartAdmin`,Workflow File `release.yml`(只填文件名,不带路径),Environment 留空,Scopes 允许发布新包,glob `SmartAdmin*`(不带点,否则匹配不到元包 `SmartAdmin` 本身)。注意:**在仓库还是私有时建的策略只临时生效 7 天**,窗口内必须完成一次成功发布才会永久激活;过期了在页面上重新激活即可。仓库已公开时建的策略没有这个窗口。
- **⚠ 删掉 GitHub 仓库重建(哪怕同名)会废掉现有策略,必须删了重建。** 策略成功发过一次包之后,nuget.org 会把 GitHub 的 **repository id 与 owner id 永久锁进策略**——这是防"resurrection attack"的设计:否则谁都能删掉一个仓库、用同名重建,再冒充它发包。重建后的仓库是**新的 id**,策略页面照旧显示 `Active`,但锁的是那个已经不存在的旧 id,发版会在 `NuGet/login` 那步被拒。**没有改绑入口,只能 Delete 再 Create。** 危险之处在于它不会提前报错:等你发现时发版 PR 已经合并、包已经发出去了,而发出去的包不可逆,只能弃号补发下一版。
  - **怎么自查**(30 秒,换仓库后务必做一次):策略卡片上 `Repository: SmartAdmin #<数字>` 里的数字,要和 `gh api repos/<owner>/<repo> --jq .id` 的输出一致;`Repository Owner` 后面的数字对应 `--jq .owner.id`。
- nuget.org 用户名默认写在 `release.yml` 里(`Andy_Zhong`);换账号时改仓库变量 `NUGET_USER`,不用改 workflow。
- **npm Trusted Publisher**:npm 只能给已经存在的包建信任关系,所以包的**第一个版本**由 npm 账号 `smartcode-x` 在本地手动发:

  ```powershell
  npm login                      # smartcode-x,账号要开 2FA
  Push-Location web; npm ci; npm test; Pop-Location
  Push-Location web/packages/admin; npm publish; Pop-Location   # prepack 自动构建;发之前 npm pack --dry-run 可先看文件清单
  ```

  发完到 npmjs.com → `smart-admin-web` → Settings → Trusted Publisher,选 GitHub Actions:Organization or user `SimpleOne-X`,Repository `SmartAdmin`,Workflow filename `release.yml`(只填文件名),Environment 留空,**Allowed actions 勾上直接 `npm publish`**(新建的配置默认只允许 `npm stage publish`,不勾的话 `release.yml` 的发布会被拒)。字段区分大小写,npm 保存时不校验,填错要到发版那一步才报错;建好的配置不能改,只能删了重建。再到 Settings → Publishing access 选 "Require two-factor authentication and disallow tokens":之后任何 token 都发不了版,能发的只有 `release.yml`(OIDC)和维护者本人过 2FA 的交互式 `npm publish`。
  - 信任关系还核对 `web/packages/admin/package.json` 的 `repository.url`(`git+https://github.com/SimpleOne-X/SmartAdmin.git`,区分大小写),仓库搬家或改名时两边一起改。
  - **⚠ 删掉 GitHub 仓库重建(哪怕同名)同样会废掉 npm 这边的配置,必须在 npmjs.com 上删除后重建。** npm 官方文档写明:第一次成功发布会校验配置并把它绑定到仓库的不可变身份,所以重建出的新仓库(新的仓库 id)与已绑定的配置对不上,发版会在 npm 那一步被拒,而此时 NuGet 往往已经推出去了。重建后、合并发版 PR 之前,先把这条配置删掉再按上面的字段新建。
  - Trusted Publishing 只支持 GitHub 托管的 runner,npm CLI 要 11.5.1 以上;`release.yml` 的 npm 那步用 Node 24 并在发布前断言 npm 版本。
- 仓库公开,GitHub Actions 不计分钟数,`ci` 的 SqlServer 全量定时腿每晚照跑。若哪天转回私有,每月只有 2000 分钟免费额度,那条定时腿会按可见性自动停。

> 发布节奏与闸门的**为什么**在 [CHANGELOG.md](../CHANGELOG.md) 顶部;本页是**怎么做**的操作清单。
