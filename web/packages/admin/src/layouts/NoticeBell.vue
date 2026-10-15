<script setup lang="ts">
// 顶栏消息通知铃铛。富面板(n-popover):按类型分页签的列表 + 正文弹层。
// 页签按"消息从哪来"分:消息(业务系统推给个人的)与通知(管理员发的通知、公告);
// "读没读"是过滤器不是分类,不做成页签(筛选在个人通知页)。
// 未读=圆点+加粗+淡主色底,已读=整行变淡。总未读数每 30s 轮询;面板展开时取各页签未读数与当前页签列表。
// 通知带动作列表时,正文弹层底部渲染成按钮(useNoticeAction):铃铛里直接"办",不必先跳到目标页面找按钮。
import { computed, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import {
  NPopover,
  NBadge,
  NButton,
  NTabs,
  NTab,
  NTag,
  NEllipsis,
  NScrollbar,
  NSpace,
  NText,
  NEmpty,
  NModal,
  NSpin,
  useMessage,
} from 'naive-ui'
import { Icon } from '@iconify/vue'
import { useIntervalFn } from '@vueuse/core'
import { useI18n } from 'vue-i18n'
import { noticeApi } from '#/api'
import { noticeBus } from '#/composables/useRealtime'
import { useNoticeAction } from '#/composables/useNoticeAction'
import { NoticeType, type NoticeAction, type NoticeMineItem } from '#/types/api'
import MarkdownView from '#/components/MarkdownEditor/MarkdownView.vue'
import { translateError } from '#/utils/error'
import { fmtDateTime } from '#/utils/format'

const { t } = useI18n()
const router = useRouter()
const message = useMessage()
const noticeAction = useNoticeAction()

// 页签顺序即打开面板时的优先级:先停在第一个有未读的页签,都没有就停第一个。
const TABS = [
  {
    key: 'message',
    types: [NoticeType.Message],
    label: 'app.notice.tabMessage',
    empty: 'app.notice.emptyMessage',
  },
  {
    key: 'notice',
    types: [NoticeType.Notice, NoticeType.Announcement],
    label: 'app.notice.tabNotice',
    empty: 'app.notice.empty',
  },
] as const
type TabKey = (typeof TABS)[number]['key']
const typesOf = (key: TabKey) => [...TABS.find(x => x.key === key)!.types]

const unread = ref(0)
const tab = ref<TabKey>(TABS[0].key)
const tabUnread = reactive<Record<TabKey, number>>({ message: 0, notice: 0 })
const current = computed(() => TABS.find(x => x.key === tab.value)!)
const items = ref<NoticeMineItem[]>([])
const loading = ref(false)
const panelShow = ref(false)

// 页签未读数只在面板开着时取:轮询只数总数,请求量不随页签数翻倍。
async function fetchTabUnread() {
  const counts = await Promise.all(TABS.map(x => noticeApi.unreadCount([...x.types])))
  TABS.forEach((x, i) => (tabUnread[x.key] = counts[i]!))
}
async function fetchUnread() {
  try {
    unread.value = await noticeApi.unreadCount()
    if (panelShow.value) await fetchTabUnread()
  } catch {
    /* 轮询失败静默,下次自愈 */
  }
}
// 每个页签拉最近一页,未读靠条目样式强调;筛选能力在"查看全部"的个人通知页。
async function fetchList() {
  const key = tab.value
  loading.value = true
  try {
    const { items: list } = await noticeApi.mine({ page: 1, pageSize: 10, types: typesOf(key) })
    // 快速切页签时,先发的请求可能后回来,不能覆盖当前页签的列表。
    if (key === tab.value) items.value = list
  } catch {
    if (key === tab.value) items.value = []
  } finally {
    if (key === tab.value) loading.value = false
  }
}
async function onShow(show: boolean) {
  if (!show) return
  loading.value = true
  try {
    await fetchTabUnread()
  } catch {
    /* 取不到页签数就停在第一个页签 */
  }
  tab.value = TABS.find(x => tabUnread[x.key] > 0)?.key ?? TABS[0].key
  fetchList()
}
function onTab(key: TabKey) {
  tab.value = key
  fetchList()
}

// 正文弹层:内容随列表取回,直接渲染(Markdown,不能纯文本直出);点条目即标记已读。
const showNotice = ref(false)
const viewNotice = ref<NoticeMineItem | null>(null)
async function markRead(item: NoticeMineItem) {
  if (item.isRead) return
  try {
    await noticeApi.markRead(item.id)
    item.isRead = true
    await fetchUnread()
  } catch (e) {
    message.error(translateError(e))
  }
}
async function openNotice(item: NoticeMineItem) {
  viewNotice.value = item
  showNotice.value = true
  await markRead(item)
}
// 动作:成功后关掉弹层与面板(页面跳转时尤其要关,否则面板盖在新页面上)。
const running = ref<number | null>(null)
async function onAction(a: NoticeAction, index: number) {
  running.value = index
  try {
    if (await noticeAction.run(a)) {
      showNotice.value = false
      panelShow.value = false
    }
  } finally {
    running.value = null
  }
}
async function markAllRead() {
  try {
    await noticeApi.markAllRead(typesOf(tab.value))
    await Promise.all([fetchUnread(), fetchList()])
  } catch (e) {
    message.error(translateError(e))
  }
}
function viewAll() {
  panelShow.value = false
  router.push('/personal/notice')
}

// 类型 → 标签色/文案。新增类型在此补一行即可。
const tagType = (ty: NoticeType) =>
  ty === NoticeType.Announcement ? 'warning' : ty === NoticeType.Message ? 'success' : 'info'
const typeLabel = (ty: NoticeType) =>
  ty === NoticeType.Announcement
    ? t('notice.typeAnnouncement')
    : ty === NoticeType.Message
      ? t('notice.typeMessage')
      : t('notice.typeNotice')
// 页签里只有一种类型时标签不带信息,不显示。
const showTag = computed(() => current.value.types.length > 1)

// 正文摘要:去掉常见 Markdown 记号,压空白,截断。ponytail: 正则够用,要精准渲染再引 md 解析。
function snippet(md?: string | null) {
  if (!md) return ''
  const s = md
    .replace(/!\[[^\]]*\]\([^)]*\)/g, '') // 图片
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1') // 链接留文字
    .replace(/[#>*_`~-]/g, '') // 记号
    .replace(/\s+/g, ' ')
    .trim()
  return s.length > 60 ? s.slice(0, 60) + '…' : s
}

// 双腿:实时推送到达即刷新未读(SignalR 开启时,即时);30s 轮询兜底(实时关闭/连接掉线时)。
// VueUse 的 useEventBus.on 在组件卸载时自动退订。
noticeBus.on(() => {
  fetchUnread()
})
useIntervalFn(fetchUnread, 30000)
fetchUnread()
</script>

<template>
  <n-popover
    v-model:show="panelShow"
    trigger="click"
    placement="bottom-end"
    :show-arrow="false"
    style="padding: 0"
    @update:show="onShow"
  >
    <template #trigger>
      <!-- offset 的 y 把徽标整体下移:默认它以按钮右上角为中心、一半在按钮顶边之上,
           顶栏面板 overflow:hidden,两三位数(99+)的胶囊顶边会被切平 -->
      <n-badge :value="unread" :max="99" :show="unread > 0" :offset="[0, 5]">
        <n-button quaternary circle :aria-label="t('app.notice.title')">
          <Icon icon="ph:bell" :width="19" />
        </n-button>
      </n-badge>
    </template>

    <div class="notice-panel">
      <div class="notice-head">
        <span class="notice-title">{{ t('app.notice.title') }}</span>
        <n-button text size="tiny" :disabled="tabUnread[tab] === 0" @click="markAllRead">
          <template #icon><Icon icon="ph:checks" :width="15" /></template>
          {{ t('app.notice.markAllRead') }}
        </n-button>
      </div>

      <n-tabs
        :value="tab"
        type="line"
        size="small"
        :tabs-padding="16"
        class="notice-tabs"
        @update:value="onTab"
      >
        <n-tab v-for="x in TABS" :key="x.key" :name="x.key">
          {{ t(x.label) }}
          <n-badge
            :value="tabUnread[x.key]"
            :max="99"
            :show="tabUnread[x.key] > 0"
            class="tab-badge"
          />
        </n-tab>
      </n-tabs>

      <n-spin :show="loading">
        <n-scrollbar style="max-height: 360px">
          <div v-if="items.length === 0" class="notice-empty">
            <n-empty :description="t(current.empty)" size="small" />
          </div>
          <ul v-else class="notice-list">
            <li
              v-for="n in items"
              :key="n.id"
              :class="['notice-item', n.isRead ? 'read' : 'unread']"
              @click="openNotice(n)"
            >
              <span v-if="!n.isRead" class="dot" />
              <div class="notice-body">
                <div class="line1">
                  <n-tag v-if="showTag" :type="tagType(n.type)" size="tiny" :bordered="false" round>
                    {{ typeLabel(n.type) }}
                  </n-tag>
                  <n-ellipsis class="item-title" :class="{ unread: !n.isRead }">
                    {{ n.title }}
                  </n-ellipsis>
                  <Icon
                    v-if="n.actions?.length"
                    icon="ph:lightning"
                    :width="13"
                    class="item-actions-mark"
                  />
                </div>
                <n-text v-if="snippet(n.content)" depth="3" class="item-snippet">
                  {{ snippet(n.content) }}
                </n-text>
                <n-text depth="3" class="item-time">
                  {{ fmtDateTime(n.publishTime, { seconds: false }) }}
                </n-text>
              </div>
            </li>
          </ul>
        </n-scrollbar>
      </n-spin>

      <div class="notice-foot">
        <n-button text size="small" @click="viewAll">
          {{ t('app.notice.viewAll') }}
          <template #icon><Icon icon="ph:arrow-right" :width="14" /></template>
        </n-button>
      </div>
    </div>
  </n-popover>

  <n-modal
    v-model:show="showNotice"
    preset="card"
    :title="viewNotice?.title || t('notice.detailTitle')"
    style="width: 640px; max-width: 92vw"
  >
    <MarkdownView :value="viewNotice?.content" />
    <template v-if="viewNotice?.actions?.length" #footer>
      <n-space justify="end">
        <n-button
          v-for="(a, i) in viewNotice.actions"
          :key="i"
          size="small"
          :type="noticeAction.buttonType(a)"
          :loading="running === i"
          :disabled="running !== null && running !== i"
          @click="onAction(a, i)"
        >
          {{ noticeAction.label(a) }}
        </n-button>
      </n-space>
    </template>
  </n-modal>
</template>

<style scoped>
.notice-panel {
  width: 360px;
  max-width: 92vw;
}
.notice-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 12px 16px 4px;
}
.notice-title {
  font-weight: 600;
  font-size: 15px;
}
.tab-badge {
  margin-left: 6px;
}
.notice-empty {
  padding: 32px 0;
}
.notice-list {
  margin: 0;
  padding: 0;
  list-style: none;
}
.notice-item {
  position: relative;
  display: flex;
  gap: 8px;
  padding: 12px 16px;
  cursor: pointer;
  border-top: 1px solid var(--separator);
  transition: background-color var(--transition-fast);
}
.notice-item:hover {
  background-color: var(--hover);
}
/* 未读:整行淡主色底(明暗主题都成立);已读:整行变淡 */
.notice-item.unread {
  background-color: color-mix(in srgb, var(--signal) 5%, transparent);
}
.notice-item.unread:hover {
  background-color: color-mix(in srgb, var(--signal) 10%, transparent);
}
.notice-item.read {
  opacity: 0.55;
}
.dot {
  position: absolute;
  left: 6px;
  top: 18px;
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--signal);
}
.notice-body {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.line1 {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}
.item-title {
  flex: 1;
  min-width: 0;
  font-size: 14px;
}
.item-title.unread {
  font-weight: 600;
}
.item-actions-mark {
  flex-shrink: 0;
  color: var(--signal);
}
.item-snippet {
  font-size: 13px;
  line-height: 1.4;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}
.item-time {
  font-size: 12.5px;
}
.notice-foot {
  display: flex;
  justify-content: center;
  padding: 8px;
  border-top: 1px solid var(--separator);
}
</style>
