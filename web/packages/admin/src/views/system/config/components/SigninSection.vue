<script setup lang="ts">
// 登录方式:登录页上出现哪些登录入口——账号密码(始终可用)、短信验证码、各第三方登录。
// 每个第三方一行:状态(未安装 / 未配置 / 已配置)、「设置」按钮、开关。连接信息与密钥在设置面板里填,
// 加密入库,不经过页面草稿;开关是草稿项,随底部保存条保存。没配好的方式开关灰掉,也永远不写库。
// 「按账号自动关联」打开前要确认两边账号是同一批人。预览就是登录框本身。
import { computed, ref } from 'vue'
import { NButton, NSwitch } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import BrandIcon from '#/components/oauth/BrandIcon.vue'
import { useConfirm } from '#/composables/useConfirm'
import { useAppStore } from '#/stores/app'
import type { ConfigProviderRow } from '#/utils/oauthBrand'
import { SMS_LOGIN_KEY } from '../groups'
import { enabledKey, hasLinkSwitch, linkKey, useConfigDraft, useDraftFields } from '../draft'
import SectionLayout from './SectionLayout.vue'
import CfgGroup from './CfgGroup.vue'
import CfgRow from './CfgRow.vue'
import ScaleToFit from './ScaleToFit.vue'
import LoginMock from './LoginMock.vue'
import ProviderSheet from './ProviderSheet.vue'

const { t } = useI18n()
const app = useAppStore()
const { ask } = useConfirm()
const draft = useConfigDraft()
const { values } = draft
const { bool } = useDraftFields()
const smsLogin = bool(SMS_LOGIN_KEY)
const rows = computed(() => draft.providerRows.value)
const isOn = (code: string) => values[enabledKey(code)] === 'true'
const linkOn = (code: string) => values[linkKey(code)] === 'true'

/** 官方厂商对应的服务器扩展包,「未安装」时告诉管理员装哪个。 */
const PACKAGE_OF: Record<string, string> = {
  wecom: 'SmartAdmin.Auth.WeCom',
  dingtalk: 'SmartAdmin.Auth.DingTalk',
  github: 'SmartAdmin.Auth.GitHub',
  wechat: 'SmartAdmin.Auth.WeChat',
}

const sheetShow = ref(false)
const sheetRow = ref<ConfigProviderRow | null>(null)
const sheetNewType = ref('')
const oidcInstalled = computed(() => draft.catalog.value.types.some(x => x.type === 'oidc'))

function openSheet(row: ConfigProviderRow) {
  sheetRow.value = row
  sheetNewType.value = ''
  sheetShow.value = true
}

function openAddOidc() {
  sheetRow.value = null
  sheetNewType.value = 'oidc'
  sheetShow.value = true
}

const statusText = (p: ConfigProviderRow) => t(`config.externalAuth.state.${p.state}`)

function toggle(p: ConfigProviderRow, v: boolean) {
  if (p.registered) values[enabledKey(p.code)] = String(v)
}

async function toggleLink(p: ConfigProviderRow, v: boolean) {
  if (!p.registered) return
  if (
    v &&
    !(await ask({
      title: t('config.externalAuth.linkByAccountConfirmTitle'),
      content: t('config.externalAuth.linkByAccountConfirm', { name: p.displayName }),
    }))
  )
    return
  values[linkKey(p.code)] = String(v)
}
</script>

<template>
  <SectionLayout
    :title="t('config.tab.signin')"
    :desc="t('config.externalAuth.hint')"
    :preview-title="t('config.externalAuth.preview')"
  >
    <CfgGroup :title="t('config.externalAuth.groupLocal')">
      <CfgRow
        :label="t('config.externalAuth.password')"
        :hint="t('config.externalAuth.passwordHint')"
      >
        <n-switch :value="true" disabled :aria-label="t('config.externalAuth.password')" />
      </CfgRow>
      <CfgRow
        :label="t('config.security.smsLogin.enabled')"
        :hint="t('config.externalAuth.smsHint')"
        :keys="[SMS_LOGIN_KEY]"
      >
        <n-switch v-model:value="smsLogin" :aria-label="t('config.security.smsLogin.enabled')" />
      </CfgRow>
    </CfgGroup>

    <CfgGroup :title="t('config.externalAuth.groupThird')">
      <template #extra>
        <n-button
          v-if="oidcInstalled"
          text
          size="tiny"
          type="primary"
          data-testid="add-oidc"
          @click="openAddOidc"
        >
          {{ t('config.externalAuth.addOidc') }}
        </n-button>
      </template>
      <template v-for="p in rows" :key="p.code">
        <CfgRow :keys="[enabledKey(p.code)]" :data-provider="p.code" :data-state="p.state">
          <template #label>
            <span class="avatar"><BrandIcon :code="p.code" :icon="p.icon" :size="18" /></span>
            <span :class="{ na: p.state === 'notInstalled' }">{{ p.displayName }}</span>
          </template>
          <template v-if="p.state === 'notInstalled' || p.source === 'code'" #hint>
            {{
              p.source === 'code'
                ? t('config.externalAuth.codeRegistered')
                : t('config.externalAuth.needPackage', { pkg: PACKAGE_OF[p.code] ?? p.code })
            }}
          </template>
          <span :class="['status', p.state]">{{ statusText(p) }}</span>
          <n-button
            v-if="p.state !== 'notInstalled' && p.source !== 'code'"
            size="small"
            :data-testid="`provider-set-${p.code}`"
            @click="openSheet(p)"
          >
            {{ t('config.externalAuth.settings') }}
          </n-button>
          <n-switch
            :value="isOn(p.code)"
            :disabled="!p.registered"
            :aria-label="p.displayName"
            @update:value="v => toggle(p, v)"
          />
        </CfgRow>
        <CfgRow
          v-if="hasLinkSwitch(p.code)"
          :label="t('config.externalAuth.linkByAccount')"
          :hint="t('config.externalAuth.linkByAccountHint')"
          :keys="[linkKey(p.code)]"
          :disabled="!p.registered || !isOn(p.code)"
          :data-link="p.code"
          sub
        >
          <n-switch
            :value="linkOn(p.code)"
            :aria-label="`${p.displayName} ${t('config.externalAuth.linkByAccount')}`"
            @update:value="v => toggleLink(p, v)"
          />
        </CfgRow>
      </template>
    </CfgGroup>

    <ProviderSheet v-model:show="sheetShow" :row="sheetRow" :new-type="sheetNewType" />

    <template #preview>
      <ScaleToFit :width="440" :height="540">
        <LoginMock :locale="app.locale === 'en-US' ? 'en-US' : 'zh-CN'" form-only />
      </ScaleToFit>
    </template>
  </SectionLayout>
</template>

<style scoped>
.avatar {
  display: grid;
  flex: none;
  place-items: center;
  width: 24px;
  height: 24px;
  margin-right: 6px;
  overflow: hidden;
  border-radius: 6px;
  background: var(--color-fill);
}
.na {
  color: var(--color-text-tertiary);
}
.status {
  font-size: var(--font-size-sm);
  color: var(--color-text-tertiary);
}
.status.configured {
  color: var(--color-success);
}
</style>
