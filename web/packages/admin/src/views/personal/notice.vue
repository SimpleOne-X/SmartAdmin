<script setup lang="ts">
// 我的通知(个人页,非管理端):列出发给自己的通知并读正文、标记已读。
// 走 [ActiveSession] 的 notice/mine —— 任何登录用户可调,故本页注册为静态路由,不进菜单/权限系统。
// 正文随列表一起返回(NoticeMineItem.content),点"查看"不另发请求。
// 通知带动作列表时,正文弹层底部渲染成按钮(useNoticeAction),与顶栏铃铛同一套行为。
import { h, ref } from 'vue'
import { NButton, NModal, NSpace, NTag, useMessage } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { SmartTable, type SmartTableColumn, type SmartTableInst } from 'smart-naive-table'
import AppIcon from '#/components/AppIcon.vue'
import MarkdownView from '#/components/MarkdownEditor/MarkdownView.vue'
import { useConfirm } from '#/composables/useConfirm'
import { useNoticeAction } from '#/composables/useNoticeAction'
import { noticeApi } from '#/api'
import { NoticeType, type NoticeAction, type NoticeMineItem } from '#/types/api'
import { translateError } from '#/utils/error'
import { fmtDateTime } from '#/utils/format'
import { TABLE_MIN_ROW_HEIGHT, TABLE_TOOLBAR } from '#/utils/tableToolbar'
import TableTotal from '#/components/TableTotal/index.vue'
import {
  createClientFilterFetcher,
  passthroughFilterSerializer,
  deriveHeaderFilters,
} from '#/utils/tableFilter'

const { t } = useI18n()
const message = useMessage()
const { run } = useConfirm()
const noticeAction = useNoticeAction()
const tableRef = ref<SmartTableInst<NoticeMineItem>>()
// 后端没有过滤能力,搜索由前端求值;fields 要与下面声明了 search 的列 key 一致
const fetcher = createClientFilterFetcher(noticeApi.mine, ['title', 'type', 'isRead'])

const columns: SmartTableColumn<NoticeMineItem>[] = [
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center' },
  {
    key: 'isRead',
    title: () => t('notice.status'),
    width: 80,
    search: {},
    // 布尔值走下拉;render 仍负责标签样式(render 优先于 options)
    options: [
      { label: () => t('notice.read'), value: true },
      { label: () => t('notice.unread'), value: false },
    ],
    render: r =>
      h(NTag, { size: 'small', type: r.isRead ? 'default' : 'success', bordered: false }, () =>
        r.isRead ? t('notice.read') : t('notice.unread'),
      ),
  },
  { key: 'title', ellipsis: { tooltip: true }, title: () => t('notice.noticeTitle'), search: {} },
  {
    key: 'type',
    title: () => t('notice.type'),
    width: 90,
    search: {},
    options: [
      { label: () => t('notice.typeNotice'), value: NoticeType.Notice },
      { label: () => t('notice.typeAnnouncement'), value: NoticeType.Announcement },
      { label: () => t('notice.typeMessage'), value: NoticeType.Message },
    ],
    // 三种类型各有各的文案与颜色(与系统通知管理页一致)。必须三类分开渲染:
    // 若「消息」被显示成「通知」,选筛选下拉里的「消息」就会筛出界面上写着「通知」的行。
    render: r =>
      h(
        NTag,
        {
          size: 'small',
          type:
            r.type === NoticeType.Announcement
              ? 'warning'
              : r.type === NoticeType.Message
                ? 'success'
                : 'info',
          bordered: false,
        },
        () =>
          r.type === NoticeType.Announcement
            ? t('notice.typeAnnouncement')
            : r.type === NoticeType.Message
              ? t('notice.typeMessage')
              : t('notice.typeNotice'),
      ),
  },
  {
    key: 'publishTime',
    title: () => t('notice.publishTime'),
    width: 170,
    render: r => fmtDateTime(r.publishTime),
  },
  {
    key: 'op',
    title: () => t('common.operation'),
    width: 80,
    fixed: 'right',
    hideInSetting: true,
    render: r =>
      h(
        NButton,
        { size: 'small', quaternary: true, type: 'primary', onClick: () => openView(r) },
        () => t('notice.view'),
      ),
  },
]
deriveHeaderFilters(columns)

// ── 查看正文(行里已带 content,不发请求)+ 顺手标记已读 ──
const showView = ref(false)
const viewRow = ref<NoticeMineItem | null>(null)
async function openView(r: NoticeMineItem) {
  viewRow.value = r
  showView.value = true
  if (r.isRead) return
  try {
    await noticeApi.markRead(r.id)
    await tableRef.value?.refresh() // 顶栏未读角标由其自身的 30s 轮询自愈
  } catch (e) {
    message.error(translateError(e))
  }
}

const running = ref<number | null>(null)
async function onAction(a: NoticeAction, index: number) {
  running.value = index
  try {
    if (await noticeAction.run(a)) showView.value = false
  } finally {
    running.value = null
  }
}

async function markAll() {
  if (await run(() => noticeApi.markAllRead(), t('notice.allRead'))) await tableRef.value?.refresh()
}
</script>

<template>
  <!-- .fill-page:高度链在 styles/layout.css,卡片吃满内容区,滚动只发生在表体里。 -->
  <div class="fill-page">
    <SmartTable
      ref="tableRef"
      :columns="columns"
      :fetcher="fetcher"
      :toolbar="TABLE_TOOLBAR"
      :search="{ container: 'table' }"
      :filter-serializer="passthroughFilterSerializer"
      fill-height
      :min-row-height="TABLE_MIN_ROW_HEIGHT"
      card-on-narrow
      storage-key="personal-notice"
      @error="e => message.error(translateError(e))"
    >
      <template #pagination-prefix="{ itemCount }">
        <TableTotal :count="itemCount" />
      </template>
      <template #toolbar-right>
        <n-button @click="markAll">
          <template #icon><AppIcon icon="ph:checks" :size="16" /></template>
          {{ t('app.notice.markAllRead') }}
        </n-button>
      </template>
    </SmartTable>

    <n-modal
      v-model:show="showView"
      preset="card"
      :bordered="false"
      :segmented="{ content: true, footer: 'soft' }"
      :title="viewRow?.title || t('notice.detailTitle')"
      style="width: 720px; max-width: 92vw"
    >
      <MarkdownView :value="viewRow?.content" />
      <template v-if="viewRow?.actions?.length" #footer>
        <n-space justify="end">
          <n-button
            v-for="(a, i) in viewRow.actions"
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
  </div>
</template>
