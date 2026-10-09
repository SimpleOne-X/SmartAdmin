# 组件使用约定

> 约定:后台不设组件演示菜单,组件用法统一沉淀在本文件。完整 API 见各包 README。
>
> **怎么导入**:应用里一律从包入口具名导入,`import { FormContainer, useConfirm, translateError } from 'smart-admin-web'`,SmartTable 从 `smart-naive-table` 导入;本文件提到的组件、composable、工具函数都在 `web/packages/admin/src/index.ts` 导出。内核包自己的页面用包内别名导入同一个文件,如 `import FormContainer from '#/components/FormContainer/index.vue'`。
> 文中的源码路径(`src/...`、`composables/...`)都相对内核包 `web/packages/admin/`。

## SmartTable(smart-naive-table)

列驱动表格:`columns` 数组同时驱动搜索表单、字典渲染与列设置;`fetcher` 是唯一后端契约。
完整文档:https://github.com/SimpleOne-X/smart-naive-table/blob/main/README.md

SmartAdmin 内接入约定:

- **fetcher**:直接传 `xxxApi.page`,api 层负责把后端 `PagedList{current,size,total,items}` 归一成 `{items,total}`、把 `{page,pageSize}` 映射成 `{Current,Size}`(见 `src/api/index.ts` 的 `userApi.page`)。
- **labels**:**全局注入,页面不手传**。`createSmartAdmin` 里 `app.provide(SMART_TABLE_DEFAULTS, createSmartTableDefaults({ labels: computed(...) }))` 一次接上 i18n(键在 locale 的 `common.*`/`app.*`/`table.*`),切语言即时生效。要覆盖单页文案才传 `:labels`。全局密度与每页条数选项由应用经 `createSmartAdmin({ table: { density, pageSizes } })` 调整。
  初始每页条数全局就是 100(`defaultPageSize`),页面不用写 `:default-page-size`;每页条数档位**不写**,由 smart-naive-table 3.0 按 `fill-height` 选(没开 `[100,500,1000]`,开了 `[100,1000,10000]`),上限是后端 `SmartAdmin:Api:MaxPageSize`(默认 10000,超限后端回 48001,不会静默截断)。应用把后端上限调小的话,要同步传 `table.pageSizes` 把前端档位压到不超过它。
  别在应用里再 provide 一次 `SMART_TABLE_DEFAULTS`,inject 取最近的一份、不合并,会把内核注入的 labels 整份挡掉。
- **列标题必须函数形式** `title: () => t('...')`,切语言即时生效;写成 `title: t('...')` 只在建列那一刻求值,切语言不更新。
- **错误处理留在视图层**:`@error="(e) => message.error(translateError(e))"`,包内不弹 UI。
- **storage-key 命名**:`{模块}-{页面}`,如 `sys-user`;列设置按此键持久化到 localStorage(`protable:` 前缀)。
- **树形页(org / menu)**:远程取数器模式 —— `:fetcher="treeFetcher"`(取数器里过滤页面已加载好的整棵树,行带 `children`)+ `row-key="id"` + `:pagination="false"`,树列设 `align:'left'`;搜索走条件构造器(`:search="{ container: 'table' }"`),不放关键字输入框;新增 / 展开全部等按钮放 `#toolbar-right`(菜单页的应用选择器是限定数据范围的控件,留在 `#toolbar`)。
  - **列的取舍**:树列 `minWidth:220 + fixed:'left'`、操作列 `fixed:'right'`(`scrollX` 由包内按 `sum(width ?? minWidth ?? 120)` 自动算并绑给 `n-data-table`,无需手传 `scroll-x`);文本列一律 `ellipsis:{tooltip:true}`,否则长路径换行会把行撑高、行高参差。**操作最多留 2 个**(编辑 + `n-dropdown` 更多▾),4 个平铺在 260~300px 里必换行且横向滚动时够不着,org/menu 两页都按这条做。下拉项里的删除用 `useConfirm().confirm`(dialog),`n-popconfirm` 是内联触发器,塞不进 dropdown。
  - **别加恒空列**:菜单树剥掉按钮后只剩目录/页面,而权限码只挂按钮 → 「权限码」列 100% 是「—」。同理树的过滤跑在剥离后的树上,写 `n.permission` 永不命中,要按权限码搜得查节点的按钮子节点(见 `menu/index.vue` 的 `buttonInfoById`)。
  - **树表不能用 3.0 的静态过滤,改用远程取数器 + `filterTree`**:3.0 静态 `:data` 模式的前端过滤是平铺的 `rows.filter(...)`,**不递归 `children`**,在树上只会命中顶层行。所以树表传 `:fetcher`,取数器收到条件构造器经 `flatFilterSerializer` 摊平的扁平键(`name` / `code` 等),再用 `utils/tree.ts` 的 `filterTree` 过滤内存里的树(命中节点保留整棵子树,未命中但有后代命中的节点作为祖先链保留)。搜索列写 `search: { actions: SEARCH_ACTIONS.fuzzy }`;过滤后的树要驱动「展开全部」与受控展开,用 `@loaded` 拿到取数器返回的行;树加载或增删改之后要 `tableRef.value?.refresh()` 让表格重新取数。范例:`src/views/system/org/index.vue`、`menu/index.vue`。
  - **展开要受控**:`:expanded-row-keys` + `@update:expanded-row-keys`(不是 SmartTable 的 prop,靠 `inheritAttrs:false + v-bind="attrs"` 透传给 `n-data-table`,和 `:loading` 同理)。**受控后必须删掉 `default-expand-all`** —— naive 里受控值优先,两者并存会让初始 `[]` 把"默认全展开"直接覆盖成全折叠;"全展开"改由 `expandableIds(tree)` 播种。data 变了受控 keys 不会自动跟着变,搜索后要重算,否则命中结果藏在折叠的祖先里。
  - **行内写值的坑**:`filterTree` 剪枝时,"仅因后代命中而保留"的祖先是浅拷贝。搜索态下往行对象上写值(`r.enabled = v`)写的是副本,不回源树 → 开关会弹回去。行内变更后**重拉**(`load()`)而不是本地写回;`StatusSwitch` 是悲观更新(请求成功才 emit),重拉即最终态。
- **主从选中(dict)**:`:active-row-key` + `@row-click` 做行高亮/选中(勿写 `:deep(> td)`);3.0 里点行内的按钮 / 勾选框 / 链接 / 输入框不会触发 `@row-click`,行内控件的 render 里不必写 `stopPropagation`(多写无害;裸 `n-data-table` 没有这层保护,所以内核页面不裸用它)。
- **窄栏搜索(dict)**:字典页等窄栏同样写 `:search="{ container: 'table' }"`,容器宽小于 600 时 3.0 自动把条件构造器收成「输入框 + 筛选」,不用另写 `layout: 'inline'`。
- **排序(user)**:列写 `sorter: true` → 点表头把 `{ sortField, sortOrder }` 并进 fetcher;**api 层要把它们映射成后端 `SortField/SortOrder` query**(见 `userApi.page`/`positionApi.page`)。后端按实体列白名单安全排序(非法字段忽略回退默认,`PagedListExtensions.OrderBySafe`),字段名 = 实体属性名(大小写不敏感)。
- **行排序(position)**:岗位顺序用**可编辑 `Sort` 字段**——编辑弹窗一个 `n-input-number`,列表默认 `OrderBy(Sort)`,用户表单的岗位下拉也继承此序。SmartTable 本身有 `row-draggable`(`drag-handle` + `@row-drag-sort`,sortablejs 懒加载)这个能力,但本项目**未接线**:没有 `positionApi.reorder`,后端也无 `POST /sys/position/reorder` 端点。要真拖拽排序,需先补该端点(按序赋 Sort)再启用手柄——对一个极少改动的小列表,数字框已够,故未做。
- **条件搜索**:列表页统一 `:search="{ container: 'table' }"`(条件构造器:字段 + 比较符 + 值,并入表格卡片),不要用独立搜索卡片、`layout: 'inline'`、列上的 `search: true` 或自己写的搜索框;`listSearch.spec.ts` 守卫这一条。
  - 搜索列写 `search: { actions: SEARCH_ACTIONS.fuzzy | exact | dayRange }`,**只开放后端真正支持的比较符**:文本 `contains`(后端 `Contains`)、枚举与 id `equal`(后端 `==`)、日期 `gte` / `lte` / `equal`(后端 `>=` / `<=`)。开放后端做不到的比较符,界面就在说谎。
  - 条件构造器产出的是 `filters`,不是扁平参数;内核全局注入了 `flatFilterSerializer` 把它摊平回各接口现有的扁平键,所以 `xxxApi.page` 不用改。条件构造器下 **`search.key` 被忽略**,过滤键就是列 key;列 key 与后端参数名不同、或有日期区间字段的页面,自己 `createFlatFilterSerializer({ rename, ranges })` 后传 `:filter-serializer`(见 `views/system/job-log`、`log/op`)。
  - 带 `search.render` 或 `type: 'switch'` 的列放不进构造器。要搜的话改成隐藏的选项列(`hideInTable: true` + `options` + `search`),见 `log/op` 的 `operatorId`。
  - 路由 query 预置筛选用 `presetEqualFilter(inst, key, value)`(内部走 `setFilter`),别写 `inst.params.x = ...`;导出「带当前筛选」用 `flatSearchOf(inst, serializer)`。
  - 后端表达不了的组合(同一字段两个「包含」等)序列化器不抛错,按第一个可识别条件生效并提示一次;通用 `filters` 协议是后端的另一项工作。
- **表格统一标准(`listSearch.spec.ts` 守卫)**:所有用了 SmartTable 的表格是同一个样子 —— 卡片顶部单行三段式,左半是**条件构造器**,右半是**业务按钮 + 内置图标**。
  - **不放标题**:页面、表格、表单上方不放菜单名或页面大标题,页签和面包屑已经标明所在页;工具栏没有标题,包括主从页右侧的表(当前选中的是哪一项,由左边列表的高亮表达)。所以**不要传 `:title`、不要写 `#title`**,`listSearch.spec.ts` 反过来守卫这一条。`useTableTitle()` 保留,但表格卡片不使用它。
  - 工具栏:`:toolbar="TABLE_TOOLBAR"`(刷新 / **放大还原(`maximize`)** / 列设置三个内置图标,所有表格统一显示;密度按钮不放,密度只在「系统设置」里调;3.0 的 `toolbar` 没有全局配置,所以逐表传)。窄栏里的从表(字典类型列表)放大没有意义,可自己 `{ ...TABLE_TOOLBAR, maximize: false }`,但要登记进 `listSearch.spec.ts` 的 `MAXIMIZE_OFF_OK`。有导入 / 导出就 `{ ...TABLE_TOOLBAR, more: [...] }` 收进「更多 ▾」菜单(哪怕只有一项),选中走 `@more-select`;`more` 为空数组时按钮不出现。
  - **定高表格必须传 `:min-row-height="TABLE_MIN_ROW_HEIGHT"`**:库给虚拟滚动的行高下限是 40(紧凑)/ 48,本项目紧凑行实际约 33px,估高偏大会让滚到底少渲染最后几行(48 条只看到 45 条)。`listSearch.spec.ts` 守卫;行确实更高的页面可自己传更大的值(树表实测也只有约 35px,同样用这个常量)。
  - **分页栏左侧显示「共 N 条」**:每张分页表格写 `<template #pagination-prefix="{ itemCount }"><TableTotal :count="itemCount" /></template>`(`:pagination="false"` 的树表不用),`listSearch.spec.ts` 守卫这一条。
  - **列宽与溢出(`views/tableColumns.spec.ts` 守卫)**:SmartTable 是 `table-layout: fixed`,没写 `width` 的列只分到约 120~150px,内容更长就会越界盖到隔壁列。所以每个数据列要么写 `width`(内容长度可预期:时间列 `170`、状态标签 `90`、IP / 等宽编码按最长值留足),要么写 `ellipsis: { tooltip: true }`(名称、标题、账号等自由文本,超出省略并悬浮提示);**不要写 `minWidth`**,固定布局只认 `width`,`minWidth` 写了也不生效。树表要给标题列写 `tree: true`,否则展开箭头和缩进会落到 64px 的序号列上,层级一深序号就被挤没(范例 `views/system/org`、`menu`)。
  - 业务按钮写 `#toolbar-right`,**不要写 `#toolbar`**:`#toolbar` 是工具栏左半,按钮会挤到搜索框左边。放进去的只能是「限定数据范围的控件」(菜单页的应用选择器、用户页窄档的机构按钮),不是业务按钮,例外登记在 `listSearch.spec.ts` 的 `LEFT_SLOT_OK`。批量操作写 `#batch`(勾选后工具栏换成批量栏,按钮出现即代表有勾选;`useBatchDelete` 的 `run` 接 `@click`,勾选态绑 `checked-row-keys`)。
  - 后端没有过滤能力的分页表:`createClientFilterFetcher(api, fields)` + `:filter-serializer="passthroughFilterSerializer"`(无条件走服务端分页,有条件取全量(上限 10000 条)前端求值,超过时提示只覆盖前 10000 条)。
  - 树表:静态过滤不递归 `children`(3.0 / 3.1 的行为:搜子行名整张表为空),用远程取数器 + `filterTree`,见上面「树形页」与 `views/system/org/index.vue`。
  - **不要裸用 `n-data-table`**:小表用 SmartTable 静态数据模式(`:data` + `row-key`,前端分页 / 求值搜索 / 窄档卡片 `card-on-narrow` 都是内置的),范例 `views/personal/sessions.vue`、`views/system/job-monitor`、`views/system/dict`(字典项表)。行拖拽排序用内置 `row-draggable` + `drag-handle` + `@row-drag-sort`。
  - 嵌在抽屉 / 弹窗里的小表(各次尝试、导入预览)不套整页标准:`:toolbar="false"`、不传 `search`、不写任何列的 `search`,并登记进 `listSearch.spec.ts` 的 `EMBEDDED`(范例 `views/system/job-log/components/AttemptTable.vue`)。
  - 嵌入弹窗的表格(`UserPicker`)是唯一例外。
  - 写在模板注释里的 `<SmartTable` 也会被 `listSearch.spec.ts` 的正则当成一张表而误判,注释里别写带尖括号的标签名。
- **别用 scoped 样式去调 SmartTable 内部**:包内 `inheritAttrs:false`,`class` 落在内层 `n-data-table` 上,而 scope id 落在 SmartTable 自己的根元素上,`.x :deep(.y)` 要求两者在同一元素,**永不命中**(比如拿它写 `min-height:0` 治横向滚动、写 `padding` 压空态高度,都不生效)。调内部样式走 `:theme-overrides`(经 attrs 透传给 `n-data-table`,只影响这一张表,如 `:theme-overrides="{ emptyPadding: '16px 0' }"`);实在要写 CSS 就用不带类名前缀的 `:deep(.y)`——编译成 `[data-v-xxx] .y`,起点是 SmartTable 根元素,能命中。
- **搜索折叠**:`collapsible` 只对独立搜索卡片(`container: 'card'`,本项目不用)有效;条件构造器靠「更多条件」展开,不需要它。
- **逃生口 slot**:`#toolbar-right`(业务按钮,工具栏右半)、`#batch`(勾选后的批量栏)、`#header-{key}`、`#pagination-prefix`(`#empty` 不用:无数据统一走表格内置的空状态,`listSearch.spec.ts` 守卫);全局默认(align/pageSizes/emptyText/tag 等)都在 `createSmartAdmin` 注入的那份 `SMART_TABLE_DEFAULTS` 里,应用能调的见上面 labels 一条。
- **已能用(透传)**:列宽拖拽(列 `resizable`)、合计行(`:summary`)、合并单元格(列 `rowSpan/colSpan`)——经 attrs/列透传,无需新 API。铺满父容器 + 虚拟滚动是 3.0 自带的 `fill-height`(整页列表一律用它,不写 `flex-height` + `virtual-scroll`)。
- **弹窗 / 下拉表格选择**:用 `smart-naive-table` 自带的 `SmartSelectTable`(触发器像下拉框,点开是「搜索框 + 带分页的表格」,单选 / 多选、本地 `data` 或远程 `fetcher`),内核不自研选择表组件。
  远程 `fetcher` 收 `{ page, pageSize, keyword }`,所以后端入参要有关键字(编号或名称的或匹配);`v-model:value` 是主键,行对象走 `@pick`。完整用法见包 README 的「下拉表格选择 SmartSelectTable」一节。
- **版本**:依赖 `^3.1.0`(peer,实际解析 3.1.0);列排序依赖后端 `SortField/SortOrder`(行拖拽是纯前端能力,本项目未接线,见上),改后端后 `npm run gen:api` 重生成 schema。

范例页:`src/views/system/user/index.vue`(标准列表 + 排序)、`position`(可编辑 Sort 排序)、`org`/`menu`(树)、`dict`(主从 + 窄栏条件搜索)。
条件搜索范例:`system/log/op`(日期区间 + 隐藏选项列 + 导出)、`system/user`(路由预置 + 导出)、`system/job-log`(列改名 + 路由预置)。

## 自研通用组件索引

**每组件详细 API 见其目录 README**;新通用组件一律「目录 + `index.vue` + `README.md`」;composable/store 的说明写在源码头注释,本文件只留索引与一句话定位。

| 组件 | 定位 | README |
|---|---|---|
| FormContainer | 弹窗/抽屉二合一表单容器;形态跟随全局偏好 `app.formStyle`(**默认弹窗**,外观设置「表单形态」可切抽屉;抽屉形态宽 / 中档右侧、窄档底部抽屉高 92%,取消钮 secondary;`variant` 按实例覆盖);onConfirm 协议接管 loading/关闭 | `src/components/FormContainer/README.md` |
| StatusSwitch | 表格行内启停开关;悲观更新,失败自动回滚 | `src/components/StatusSwitch/README.md` |
| TableTotal | 分页栏左侧的「共 N 条」,写在 SmartTable 的 `#pagination-prefix` 插槽里;每张分页表格都要有 | `src/components/TableTotal/README.md` |
| DictSelect | 字典下拉,`$attrs` 全透传 n-select | `src/components/DictSelect/README.md` |
| DictTag | 表格列字典翻译 + 语义色标签 | `src/components/DictTag/README.md` |
| OrgTreeSelect | 机构树下拉;拉 `org/list` 平铺 → `utils/tree.buildTree` 拼树,`$attrs` 透传 n-tree-select;`excludeSubtreeOf` 剪自身子树防成环 | `src/components/OrgTreeSelect/README.md` |
| FileUpload | 封 n-upload `custom-request`;内部 `fileApi.upload` 自动带 Bearer,成功 `emit('uploaded', out)`,上传起止各 `emit('loadingChange', bool)`(try/finally 兜底,给触发器加"上传中"spinner);`$attrs` 透传(accept/multiple/show-file-list)。`chunked` 走分片/断点续传/秒传(`utils/chunkUpload`,进度回 n-upload) | `src/components/FileUpload/README.md` |
| PasswordStrength | 密码强度条 + 规则清单(紧贴输入框、弱/中/强取 err/warn/ok 语义色);自包含,`:value` 传密码明文,内部拉当前生效密码策略动态构建规则(改密页 / 建用户页共用) | `src/components/PasswordStrength/README.md` |
| ApiSelect | 通用远程分页下拉基座;`fetch(keyword)` 回归一选项,管加载/远程搜索防抖/竞态/loading,`$attrs` 透传 n-select | `src/components/ApiSelect/README.md` |
| UserSelect | 人员选择器;基于 ApiSelect,`userApi.page` 搜索 + 可选 `orgId` 部门过滤,label 为「姓名(账号)」 | `src/components/UserSelect/README.md` |
| MarkdownEditor / MarkdownView | 通知公告 Markdown 编辑/只读渲染(封 md-editor-v3);存 Markdown 纯文本,跟随明暗主题;正文里以单个 `/` 开头的站内链接走 vue-router(不整页刷新,Ctrl/Cmd + 点击仍新开标签),http(s) 外链新标签打开;通知页已落地 | `src/components/MarkdownEditor/README.md` |
| Chart(+ LineChart/BarChart/PieChart) | ECharts 封装(封 vue-echarts);自动跟随明暗主题/accent、按需注册图种、自带 autoresize;预设传 data、BaseChart 传 option;工作台已落地 | `src/components/Chart/README.md` |
| CodeBlock | 代码/JSON 只读展示;NCode + `hljs/lib/core` 按需注册(现仅 json),复制按钮 + 自动换行,配色随 Naive 主题;操作日志详情已落地 | `src/components/CodeBlock/README.md` |
| DetailPage | 详情页外壳:返回 + 标题 + actions/body 插槽;`@back` 交父级(路由态关标签回列表 / 就地态清状态),补偿非菜单详情路由的空面包屑;配 `useTabTitle` 设动态标签标题。用法/骨架见 `skills/create-page-variant.md` 变体四 | `src/components/DetailPage/README.md` |
| ImportWizard | 导入四步向导(`n-steps`):上传 → 列映射 → 预览改错(**裸 `n-data-table`**,可编辑+错误红底 tooltip)→ 结果;api 注入,用户管理已落地。**「已存在」(46010)按重复策略呈现**,不是硬错误 —— 判定在 `src/utils/importDup.ts` | `src/components/ImportWizard/README.md` |
| ExportColumnsModal | 导出选列弹窗;默认按 `defaultSelected` 勾选,确认后父级带 **SmartTable 当前筛选** 请求 blob 下载;用户管理 / 操作日志已落地 | `src/components/ExportColumnsModal/README.md` |
| CronEditor | 6 段 cron 可视化编辑(秒/分/时/日/月/周 + 表达式直填,日 L/L-n/nW/LW、周 nL/n#m 专项);日/周互斥自动落 `?`,防抖 400ms 调 preview-cron 预览未来时刻;定时任务表单已落地 | `src/components/CronEditor/README.md` |
| DictRadio / DictCheckbox | 字典单选(按钮组)/ 字典多选(复选框组);`typeCode` 取数经 `stores/dict` 缓存,其余 `$attrs` 透传 n-radio-group / n-checkbox-group;与 DictSelect 同源同范式 | `src/components/DictRadio/README.md`、`src/components/DictCheckbox/README.md` |
| RoleSelect | 角色选择器;基于 ApiSelect,`roleApi.page` 名称搜索,只列启用角色,value=角色 id,多选经 `$attrs` | `src/components/RoleSelect/README.md` |
| JsonEditor | JSON 值编辑:textarea + 实时校验 + 一键格式化,零依赖;只给约定为 JSON 的配置字段用 | `src/components/JsonEditor/README.md` |
| UserPicker | 授权用户选择器;机构树过滤 + 用户表格多选,确认后回传用户 Id 数组;宽 / 中档是 1100 宽弹窗三栏,窄档(或内容区 < 900)收成「可选用户 / 已选」两页,窄档走底部抽屉 | `src/components/UserPicker/README.md` |
| ErrorBoundary | 内容区渲染错误兜底;`onErrorCaptured` + 重试/回首页,chunk 失效自动重载一次。已包住 `layouts/default.vue` 的 `router-view`,页面作者不必再手动套 | `src/components/ErrorBoundary/README.md` |
| TableZoomButton | 表格「放大/还原」按钮;形态与 SmartTable 工具栏按钮一致,状态由调用方的 `useTableZoom()` 持有,必须与表格一起进 `Teleport`。**已弃用,改用 `toolbar.maximize`**(内核页面的 `TABLE_TOOLBAR` 已统一开放大),下一个 .NET 大版本删除 | `src/components/TableZoomButton/README.md` |

字典三件套的数据基座是 `src/stores/dict.ts`(按 typeCode 缓存 + 并发去重;字典管理操作后调 `invalidate()`),页面拿原始选项用 `useDictOptions(typeCode)`。范例页:`src/views/system/menu/index.vue`、`module/index.vue`(FormContainer + useConfirm + StatusSwitch 完整落地)。

## 页面标题与菜单标题(composable / 单源)

- `composables/usePageTitle.ts`:App.vue 调一次,`document.title` 随路由变(多标签页的动态标题优先),`<html lang>` 跟随当前语言。
- `locales/menuTitle.ts` 的 `translateMenuTitle(title, path)` 是**菜单标题翻译的单源**:侧栏 / 面包屑 / 全局搜索 / 多标签 / 浏览器标题必须走它(规则=标题含 `.` 视为 i18n key),否则同一个菜单能在四个地方显示成四个样子。
- `composables/useTableTitle.ts` 的 `useTableTitle()` 返回当前路由菜单标题的 `ComputedRef<string>`(内部就是 `translateMenuTitle`)。表格工具栏不放标题(见上面 SmartTable 的「表格统一标准」),内核页面不调用它,函数保留给下游应用(页面自己需要标题文案时用)。

## 通用格式化(utils/format.ts)

`fmtDateTime(值, { seconds?, empty? })` / `fmtBytes(字节)` / `operatorText(行, empty?)` —— 时间只做截断不走 `new Date()`
(后端下发的是服务器本地时区的 ISO 串,交给 Date 会被当 UTC 平移几小时)。写页面时别再各写一遍:
各写各的字节格式化容易长出两套口径,同一个数在文件页与监控页显示不一样。

## useConfirm(二次确认 + 结果 toast,composable)

`src/composables/useConfirm.ts`,**仅限 setup 中调用**(依赖 Dialog/Message Provider)。`const { ask, confirm, run } = useConfirm()`:

- `run(action, successMsg?) => Promise<boolean>`:执行 → 成/败 toast。配模板层 `n-popconfirm` 用(popconfirm 当触发器,后半段交给 run):
  ```ts
  onPositiveClick: () => run(() => xxApi.remove(r.id), t('xx.deleted')).then((ok) => { if (ok) load() })
  ```
- `confirm({ content, title?, type?, action, successMsg? }) => Promise<boolean>`:先弹 dialog、**action 在 dialog 挂起期间执行**(确认钮 loading,执行中锁死取消/Esc/遮罩,防连点重复执行);给不适合内联的重操作(批量删除等)。取消/关闭/Esc/失败均 false;`successMsg: false` 关掉成功 toast。
- `ask({ content, title?, type? }) => Promise<boolean>`:仅确认不执行,给需要自管后续流程的组件用(StatusSwitch 即基于它)。Esc/遮罩/取消均 false。

## useTabTitle(详情页动态标签标题,composable)

`src/composables/useTabTitle.ts`,**仅限 setup 中调用**。`const setTabTitle = useTabTitle()`,详情页数据加载后 `setTabTitle(记录名)` 把当前标签标题改成记录名(如「张三」);内部走 `tabsStore.setTitle` → 置 `titleFixed`,`addTab` 复访时不会用静态 `meta.title` 覆盖,标题随 tab 持久化、F5 无闪复原。**就地态(列表页内切换详情)别调用**——那时 `route.path` 是列表标签,会改错标签。

## 屏幕形态三件套(平板 / 小屏,composable)

平板与小屏适配收成三个内核能力,页面直接用:

- `composables/useCompactScreen.ts`:`useCompactScreen({ maxWidth?, maxHeight? })` → `ComputedRef<boolean>`。判据是**视口尺寸不是 UA** —— 平板横竖屏是同一台设备,认 UA 必然错一半。默认宽 ≤ 900 或高 ≤ 640 算紧凑(iPad 竖屏 768/834、1280×600 矮屏笔记本都命中)。纯函数版 `isCompactSize()` 供非响应式场景。
- `composables/useVisualViewportHeight.ts`:把 `visualViewport.height` 持续写进 `--vvh`(px),供需要避开软键盘的页面写 `height: var(--vvh, 100dvh)`。`dvh` 跟的是布局视口,软键盘弹起时它**不变**,底部按钮会被键盘压住 —— 只有 `visualViewport` 会缩。布局壳已全局调过一次,页面直接用变量即可。
- `composables/useFullscreenToggle.ts`:`{ isSupported, isFullscreen, enter, exit, toggle }`。不用 VueUse 的 `useFullscreen`,因为它在 iPadOS 上把 `isSupported` 判成 false(iPadOS Safari 伪装桌面版,只有 `webkitRequestFullscreen`,没有标准名)。`isSupported === false`(iPhone)时**把按钮藏掉**,别留一个点了没反应的。顶栏的全屏按钮用的就是它。

FormContainer 的 `:fullscreen="true" | 'auto'` 建在 `useCompactScreen` 上,默认 `false`(没传这个 prop 的既有页面不该因为换台设备就换形态)。

## useTableZoom(表格放大,composable;已弃用)

- `composables/useTableZoom.ts`(**已弃用**:SmartTable 3.0 自带放大,写 `:toolbar="{ ...TABLE_TOOLBAR, maximize: true }"`(`TABLE_TOOLBAR` 已开放大);没有内核页面在用,仅为已依赖它的下游应用保留,下一个 .NET 大版本删除):把某块区域临时铺满视口看宽表。刻意不用 Fullscreen API(Naive 浮层 teleport 到 body,原生全屏看不见)。`const { zoomed, toggle } = useTableZoom()`,容器 `:class="{ 'table-zoom': zoomed }"`,Esc 退出,body 滚动锁带引用计数。

## 页面形状与高度链(styles/layout.css,约定式)

满屏列表页整页吃满一屏、滚动只发生在表体里(SmartTable 加 3.0 自带的 `fill-height`,它同时给出虚拟滚动与基准高度;裸 `n-data-table` 才需要 `flex-height` + `virtual-scroll` 成对写)。高度链只写在 `styles/layout.css` 一处,页面按形状套类名,不各自抄 `:deep` 链:

1. 模板顶层直接是 `<SmartTable>`:什么都不用加,自动满屏;
2. 顶层是自己的外壳 div:加 `.fill-page`;主表不是直接子元素时,给「包住主表的那一层」加 `.fill-main`;
3. 「左分组栏 + 右列表」:外壳加 `.side-page`(侧栏宽度按页覆盖 `--side-filter-width`),侧栏面板外观用 `.side-filter` / `.side-row` / `.side-tree`;
4. 页签页:`<n-tabs>` 加 `.fill-tabs`;
5. 上下 / 左右分栏均分:容器加 `.fill-split`(横向加 `--row`),每栏加 `.fill-main`。

链中间夹了 `<n-spin>` 给它加 `.fill-pass`;矮屏宁可让页面滚也别把表压扁时外壳再叠 `.fill-page--soft`。弹窗表单分节用 `.form-section` / `.form-section__title`(修饰 `--fieldset` / `--panel`),别在页面 scoped 里再画一遍标题。表格全局外观(斑马纹 / 列分隔 / 固定列实底 / 选中行 `.row-selected`)在 `styles/table.css`,页面也不必再写。已落地:用户 / 字典(形状 3),菜单 / 机构 / 通知 / 回收站 / 任务监控(形状 2、4)。

## naive-ui 组件必须逐个显式 import

内核包与模板应用都**没有自动导入插件,也没有全局 `app.use(naive)`** —— 模板里用到哪个 `<n-xxx>`,就得在同一个 SFC 的 `<script setup>` 里 `import { NXxx } from 'naive-ui'`(范例:`views/system/position/index.vue` 一次列全)。

**漏了不会编译报错**:未注册组件在 Vue 里是运行时警告,`vue-tsc --noEmit` / `vite build` / `vitest` 一律不查模板里的组件解析。表现是浏览器控制台一行 `Failed to resolve component: n-xxx`,页面上那块**直接空白**——表格不渲染、表单 ref 拿不到实例(于是 `formRef.value?.validate()` 静默跳过),看着就像「点了没反应」。**页面写完必须在浏览器里点一遍**。容易漏的是只在模板里出现、不在 `h()` 里用的那些:`NCard` / `NTabs` / `NTabPane` / `NEmpty` / `NGrid` / `NFormItemGi` / `NDataTable`。

## 外链 / iframe 菜单(约定式,零后端字段新增)

菜单节点(`Type=Menu`)复用现有 `path`/`component` 字段承载,判据是 `isHttpUrl`(`src/utils/url.ts`):
- **外链**:`path` 填 `http(s)://…`、`component` 留空 → 不建路由(`useAuthMenu.buildRoutesForModule` 跳过),点击时 `window.open` 新窗口(`useLayoutMenu.onSelect/onSelectL1` + `MenuSearch.go` 各有 `isHttpUrl` 分支)。
- **内嵌 iframe**:`path` 填内部路径(如 `/embed/docs`)、`component` 填 `http(s)://…` → 注册通用视图 `views/embed/iframe.vue`,URL 进 `meta.iframeSrc`,keep-alive 顺带保住 iframe 状态。
- 菜单管理表单在页面类型下给出 `menu.linkHint` 说明;seed 里同理(`path`/`component` 填 URL 即可)。后端实体/枚举/种子结构不需要为此加字段。

- 布局壳之外的**静态**路由(整屏看板 `/fullscreen/*`、独立打印页)经 `createSmartAdmin({ routes: [...] })` 传入 `RouteRecordRaw[]`,并入顶级静态路由。菜单节点 path 填该路径、component 留空,点击即 `window.open` 新标签(`utils/url.isFullscreenPath`)。

## 数字动画 / 水印(用 Naive 内建,不自研)

- 数字动画:`<span class="tabular"><n-number-animation :from="0" :to="n" show-separator /></span>`;`.tabular` 防滚动抖宽(styles/index.css),前后缀直接写旁边。
- 水印:全局水印已内建,由「系统配置 → 水印设置」(`sys.watermark.*`)控制,不在外观设置里。管理员选印哪几项(姓名 / 账号 / 机构 / 手机尾号 / 当前时间 / 自定义文字)和版式,每个人印的是自己的信息;组件是 `components/AppWatermark.vue`,构造入参的纯函数在 `lib/watermark.ts`(倾斜后的包围盒要用 `xOffset` / `yOffset` 平移回画布,否则文字会被裁掉)。局部包 `<n-watermark content="…" cross>` 即可。

## 三个登记表:菜单角标 / 顶栏工具 / 实时事件(应用扩展位)

都是**登记表而非插槽**:布局壳(`layouts/default.vue`、`AppHeader.vue`)在包里,应用改不到,登记表让扩展代码留在应用自己的文件里,在 `createSmartAdmin({ install })` 或组件 setup 里调用。三者包内都没有调用方,**别当未使用代码清掉**(同 `docs/coding-standards.md` §2.1)。

- **菜单角标** `composables/useMenuBadge.ts`:`registerMenuBadge('/todo/mine', unreadRef)` → 返回注销函数。按路由 path 登记(不是 `MenuNode` 上的字段——菜单树每次重拉都会把前端算的动态值冲掉);值可传 ref / getter,变了角标自己跟着变。渲染成 `MenuOption.extra` 的 `n-badge`,侧栏 / 顶栏 / 二级 / 移动抽屉四处共用。`0` / `''` / `null` 不渲染(未读为零不留空圈);折叠与 rail 态 n-menu 只画图标、不渲染 `extra`,角标随之隐藏。
- **顶栏工具** `composables/useHeaderTools.ts`:`registerHeaderTool({ key, icon, label, onClick, order?, keepOnMobile? })` 画一个和内置项同款的圆形按钮;要自带角标 / 弹层就换组件模式 `{ key, component }`(内置铃铛就是这形状)。登记项渲染在铃铛左侧,按 `order` 升序,默认窄屏隐藏——顶栏挤不下时右端会被裁,最右的登出入口优先。
- **实时事件** `composables/useRealtime.ts` 的 `onRealtime(event, handler)`:在内置那条 SignalR 连接上挂自己的 hub 事件,返回退订函数。建连前登记的于 `start()` 时统一绑定,之后登记的直接挂上,连接重建自动重绑。**不要另建一条连向 `/hub/realtime` 的连接**:后端按 userId 群发到该用户的全部连接,第二条会把 `notice-changed` / `force-logout` 又收一遍(未读刷两次、强退弹两次)。

## 可加但先不加(别提前造)

- FormContainer size 档位 / `onBeforeClose`;useConfirm 返回结果值版;StatusSwitch 泛型值/乐观模式;DictSelect 展示禁用项;dict 缓存 TTL/SWR;CountTo 自研包装(NNumberAnimation 不够用再说)。

## IconPicker / AppIcon(smart-naive-icon)

离线优先图标选择与渲染:包里的渲染器是 `SmartIcon`、选择器是 `SmartIconPicker`,应用里用内核的封装 `AppIcon`、`IconPicker`。初始化与包装见 `src/lib/icons.ts` 顶部注释、`src/components/IconPicker/index.vue`、`src/components/AppIcon.vue`。应用的本地 SVG 经 `createSmartAdmin({ icons: import.meta.glob('./assets/svg/*.svg', { query: '?raw', import: 'default', eager: true }) })` 注册,在选择器与 `AppIcon` 里以 `local:<文件名>` 使用。页面里用了新的 `ph:*` 名字就跑 `npm run gen:icons`(包带的 `smart-admin-icons` 命令),生成的子集经 `createSmartAdmin({ iconSets })` 启动时同步注册,不必懒加载整套 ph。
