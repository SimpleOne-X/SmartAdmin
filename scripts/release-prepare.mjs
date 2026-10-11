#!/usr/bin/env node
// 发版准备:一条命令把「定版 CHANGELOG + 前端三个 package.json 与模板依赖升版本 + 文档站中英两处徽章
// + 同步 lockfile」做完,并按 release.yml 的 verify 同样的口径自检。
//
//   node scripts/release-prepare.mjs X.Y.Z [YYYY-MM-DD]   写入(发布日缺省取今天)
//   node scripts/release-prepare.mjs X.Y.Z --dry-run       只检查并打印将做的事,不写任何文件
//   node scripts/release-prepare.mjs --selftest            用内存里的样本验证替换逻辑
//
// 为什么用脚本:这几处版本号、CHANGELOG 标题和 lockfile 必须一起改,漏一处就要等 verify 在推 tag 之后才拦下来,
// 手改又容易把「旧版本号残留」「徽章只改了一处」带进去。每处替换都断言「恰好命中预期次数」,对不上就整体中止,
// 一个文件都不写。
//
// 脚本只定版,不替你写条目:CHANGELOG 条目要按真实 diff 写,先写进 `## Unreleased` 再跑本脚本。
// 脚本不提交:看一眼 diff、按需本地验绿,再用 `chore(release): 准备 X.Y.Z — changelog 定版 + 前端与文档站版本号` 提交。
import assert from 'node:assert/strict'
import { execFileSync, execSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')

const FILES = {
  changelog: 'CHANGELOG.md',
  root: 'web/package.json',
  admin: 'web/packages/admin/package.json',
  template: 'web/template/package.json',
  site: 'site/.vitepress/config.ts',
}
const VERSION_RE = /^\d+\.\d+\.\d+$/
const DATE_RE = /^\d{4}-\d{2}-\d{2}$/
/** 文档站导航徽章:中 / 英各一处。 */
const BADGE_COUNT = 2

const escapeRe = s => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
const parse = v => v.split('.').map(Number)
/** a > b ? */
function isNewer(a, b) {
  const [x, y] = [parse(a), parse(b)]
  for (let i = 0; i < 3; i++) if (x[i] !== y[i]) return x[i] > y[i]
  return false
}

/** 在 `text` 里把 `re`(必须带 g)的每处命中的第 1 捕获组之后、第 2 捕获组之前换成 `to`;命中数必须恰好是 `expected`。 */
function replaceExactly(text, re, to, expected, label) {
  let n = 0
  const out = text.replace(re, (_, pre, post) => {
    n++
    return `${pre}${to}${post}`
  })
  if (n !== expected) throw new Error(`${label}:预期命中 ${expected} 处,实际 ${n} 处,没有改任何文件`)
  return out
}

/** 前端 package.json:`"version"` 恰好 1 处;模板的依赖 `"smart-admin-web"` 另算 1 处。 */
export function bumpPackageJson(text, oldV, newV, { dependency = false } = {}, label = 'package.json') {
  let out = replaceExactly(
    text,
    new RegExp(`^(\\s*"version":\\s*")${escapeRe(oldV)}(")`, 'gm'),
    newV,
    1,
    `${label} 的 version`,
  )
  if (dependency) {
    out = replaceExactly(
      out,
      new RegExp(`^(\\s*"smart-admin-web":\\s*")${escapeRe(oldV)}(")`, 'gm'),
      newV,
      1,
      `${label} 对 smart-admin-web 的依赖`,
    )
  }
  return out
}

/** 文档站导航徽章 `{ text: 'X.Y.Z', link: … }`:中 / 英各一处,必须恰好 2 处。 */
export function bumpBadges(text, oldV, newV) {
  return replaceExactly(
    text,
    new RegExp(`(\\btext:\\s*')${escapeRe(oldV)}(')`, 'g'),
    newV,
    BADGE_COUNT,
    '文档站版本徽章',
  )
}

/** 把 `## Unreleased` 下的条目归到新版本标题下,顶部留一个空的 `## Unreleased` 给下一版攒。 */
export function bumpChangelog(text, version, date) {
  const unreleased = /^## Unreleased[ \t]*\n/m.exec(text)
  if (!unreleased) throw new Error('CHANGELOG 里没有 `## Unreleased` 标题')
  const bodyStart = unreleased.index + unreleased[0].length
  const next = /^## /m.exec(text.slice(bodyStart))
  const body = text.slice(bodyStart, next ? bodyStart + next.index : text.length)
  if (body.trim() === '') throw new Error('`## Unreleased` 下没有条目:先把待发布的条目写进去再定版')
  if (new RegExp(`^## ${escapeRe(version)}( |$)`, 'm').test(text)) {
    throw new Error(`CHANGELOG 里已经有 ${version} 的标题`)
  }
  return `${text.slice(0, bodyStart)}\n## ${version} - ${date}\n${text.slice(bodyStart)}`
}

function selftest() {
  assert.ok(isNewer('10.23.1', '10.23.0'))
  assert.ok(isNewer('10.24.0', '10.23.9'))
  assert.ok(isNewer('11.0.0', '10.99.99'))
  assert.ok(!isNewer('10.23.0', '10.23.0'))
  assert.ok(!isNewer('10.9.0', '10.10.0'), '按数值比较,不是按字符串')

  const cl = '# 更新日志\n\n## Unreleased\n\n### Added\n\n- 新东西\n\n## 1.0.0 - 2026-01-01\n\n- 旧的\n'
  assert.equal(
    bumpChangelog(cl, '1.1.0', '2026-02-02'),
    '# 更新日志\n\n## Unreleased\n\n## 1.1.0 - 2026-02-02\n\n### Added\n\n- 新东西\n\n## 1.0.0 - 2026-01-01\n\n- 旧的\n',
  )
  assert.throws(() => bumpChangelog('## Unreleased\n\n## 1.0.0 - x\n', '1.1.0', 'd'), /没有条目/)
  assert.throws(() => bumpChangelog('## Unreleased\n\n- a\n\n## 1.1.0 - x\n', '1.1.0', 'd'), /已经有 1\.1\.0/)
  assert.throws(() => bumpChangelog('# x\n', '1.1.0', 'd'), /没有 `## Unreleased`/)
  assert.ok(bumpChangelog('## Unreleased\n\n- a\n', '1.1.0', 'd').includes('## 1.1.0 - d'), '最后一段也能定版')

  const pkg = '{\n  "name": "a",\n  "version": "1.0.0",\n  "dependencies": {\n    "smart-admin-web": "1.0.0",\n    "x": "1.0.0"\n  }\n}\n'
  const bumped = bumpPackageJson(pkg, '1.0.0', '1.1.0', { dependency: true })
  assert.ok(bumped.includes('"version": "1.1.0"') && bumped.includes('"smart-admin-web": "1.1.0"'))
  assert.ok(bumped.includes('"x": "1.0.0"'), '别的依赖的版本不能被误改')
  assert.ok(bumpPackageJson(pkg, '1.0.0', '1.1.0').includes('"smart-admin-web": "1.0.0"'), '没带 dependency 时不改依赖')

  // 失败路径:锚点对不上必须抛错,不能悄悄放过
  const noVersion = '{\n  "name": "a"\n}\n'
  assert.throws(() => bumpPackageJson(noVersion, '1.0.0', '1.1.0'), /预期命中 1 处,实际 0 处/)
  const dupVersion = '{\n  "version": "1.0.0",\n  "x": {\n    "version": "1.0.0"\n  }\n}\n'
  assert.throws(() => bumpPackageJson(dupVersion, '1.0.0', '1.1.0'), /预期命中 1 处,实际 2 处/)
  const badges = "nav: [{ text: '1.0.0', link: 'a' }]\nzh: [{ text: '1.0.0', link: 'b' }]\n"
  assert.equal(bumpBadges(badges, '1.0.0', '1.1.0').match(/text: '1\.1\.0'/g).length, 2)
  assert.throws(() => bumpBadges("{ text: '1.0.0', link: 'a' }\n", '1.0.0', '1.1.0'), /预期命中 2 处,实际 1 处/)
}

function today() {
  const d = new Date()
  const p = n => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`
}

const read = rel => fs.readFileSync(path.join(repo, rel), 'utf8')
const write = (rel, text) => fs.writeFileSync(path.join(repo, rel), text)

function git(...args) {
  try {
    return execFileSync('git', args, { cwd: repo, encoding: 'utf8' })
  } catch {
    return null
  }
}

/** lockfile 的 diff 只许是版本号那几行(顶层 + 三个工作区包 + 模板对内核的依赖,各一对)。 */
function checkLockfileDiff() {
  const diff = git('diff', '-U0', '--', 'web/package-lock.json')
  if (diff === null) return '(不在 git 仓库里,跳过 lockfile diff 核对)'
  const changed = diff.split('\n').filter(l => /^[+-]/.test(l) && !/^(\+\+\+|---)/.test(l))
  const stray = changed.filter(l => !/^[+-]\s+"(version|smart-admin-web)": "\d+\.\d+\.\d+",?$/.test(l))
  if (stray.length) throw new Error(`lockfile 里有版本号以外的变化,先查清楚:\n${stray.join('\n')}`)
  return `lockfile 只改了版本号相关的 ${changed.length} 行`
}

function main() {
  const args = process.argv.slice(2)
  if (args.includes('--selftest')) {
    selftest()
    console.log('自检通过。')
    return
  }
  const dryRun = args.includes('--dry-run')
  const [version, dateArg] = args.filter(a => !a.startsWith('--'))
  if (!version || !VERSION_RE.test(version)) {
    throw new Error('用法:node scripts/release-prepare.mjs X.Y.Z [YYYY-MM-DD] [--dry-run]')
  }
  const date = dateArg ?? today()
  if (!DATE_RE.test(date)) throw new Error(`发布日格式应为 YYYY-MM-DD,拿到 ${date}`)

  // 当前版本:以发到 npm 的那个包为准,另外两个 package.json 与模板依赖必须同号,否则说明上次就没改齐
  const current = JSON.parse(read(FILES.admin)).version
  const mismatched = [FILES.root, FILES.template].filter(f => JSON.parse(read(f)).version !== current)
  const depV = JSON.parse(read(FILES.template)).dependencies?.['smart-admin-web']
  if (mismatched.length || depV !== current) {
    throw new Error(
      `前端版本号本来就不一致(admin ${current};不同号:${mismatched.join(', ') || '无'};模板依赖 ${depV}),先对齐再发版`,
    )
  }
  if (!isNewer(version, current)) throw new Error(`新版本 ${version} 必须大于当前版本 ${current}`)

  // 全部在内存里算好,任何一处锚点对不上就抛错,此时还没写任何文件
  const next = {
    [FILES.changelog]: bumpChangelog(read(FILES.changelog), version, date),
    [FILES.root]: bumpPackageJson(read(FILES.root), current, version, {}, FILES.root),
    [FILES.admin]: bumpPackageJson(read(FILES.admin), current, version, {}, FILES.admin),
    [FILES.template]: bumpPackageJson(read(FILES.template), current, version, { dependency: true }, FILES.template),
    [FILES.site]: bumpBadges(read(FILES.site), current, version),
  }

  console.log(`${current} -> ${version}(发布日 ${date})`)
  for (const f of Object.keys(next)) console.log(`  ${dryRun ? '将改' : '改'} ${f}`)
  console.log(`  ${dryRun ? '将同步' : '同步'} web/package-lock.json(npm install --package-lock-only)`)
  if (dryRun) {
    console.log('--dry-run:没有写任何文件。')
    return
  }

  for (const [f, text] of Object.entries(next)) write(f, text)
  // 固定命令串、不拼任何外部输入;经 shell 起是因为 Windows 上 npm 是 .cmd
  execSync('npm install --package-lock-only --no-audit --no-fund', { cwd: path.join(repo, 'web'), stdio: 'inherit' })

  // 自检:按 release.yml verify 的口径读回
  const problems = []
  for (const f of [FILES.root, FILES.admin, FILES.template]) {
    if (JSON.parse(read(f)).version !== version) problems.push(`${f} 的 version 不是 ${version}`)
  }
  if (JSON.parse(read(FILES.template)).dependencies['smart-admin-web'] !== version) problems.push('模板对 smart-admin-web 的依赖不是新版本')
  const badges = (read(FILES.site).match(new RegExp(`text: '${escapeRe(version)}'`, 'g')) ?? []).length
  if (badges !== BADGE_COUNT) problems.push(`文档站徽章是 ${badges} 处,应为 ${BADGE_COUNT}`)
  if (!new RegExp(`^## ${escapeRe(version)} - ${date}$`, 'm').test(read(FILES.changelog))) problems.push('CHANGELOG 没有新版本标题')
  const stale = [FILES.root, FILES.admin, FILES.template, FILES.site].filter(f => read(f).includes(`'${current}'`) || read(f).includes(`"${current}"`))
  if (stale.length) problems.push(`这些文件里还残留旧版本 ${current}:${stale.join(', ')}`)
  if (problems.length) throw new Error(`自检未通过:\n  ${problems.join('\n  ')}`)

  console.log(checkLockfileDiff())
  console.log('自检通过:三个前端 version、模板依赖、两处徽章、CHANGELOG 标题一致。')
  console.log(`下一步:看一眼 git diff;提交 chore(release): 准备 ${version} — changelog 定版 + 前端与文档站版本号`)
}

// 只在直接运行时执行,被 import(测试 / 复用)时不动手
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    main()
  } catch (e) {
    console.error(`错误:${e.message}`)
    process.exit(1)
  }
}
