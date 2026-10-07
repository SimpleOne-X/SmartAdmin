<script setup lang="ts">
// 站点徽标,侧栏/顶栏/登录页/应用选择页共用,品牌只在这一处生效。取值顺序:
//   1) 后台「站点 logo」配置(sys.site.logo)有图片地址 → <img>;
//   2) createSmartAdmin({ brand: { logo } }) 传的组件或图片地址(sys.site.logo 为空时使用);
//   3) 内置 SmartAdmin 标(拉丝钛金属 X,自带蓝紫渐变圆角底,明暗主题同一张)。
//      源文件 assets/smart-logo.svg 与模板 public/smart-logo.svg 是同一份,换图两处同步;
//      用 <img> 而不是内联 SVG:图里有滤镜和 clipPath,内联多份时 id 会互相串。库构建时内联进产物。
import { computed } from 'vue'
import { useSite } from '#/composables/useSite'
import { runtime } from '#/lib/runtime'
import builtinLogo from '#/assets/smart-logo.svg?url'

const props = withDefaults(
  defineProps<{
    size?: number
    /** 覆盖 sys.site.logo(配置中心预览未保存的草稿用);空串 = 按"没配 Logo"回退 */
    src?: string
  }>(),
  { size: 28, src: undefined },
)
const { site } = useSite()

const brandLogo = runtime.brand.logo
const configured = computed(() => props.src ?? site.logo)
const brandComponent = computed(() =>
  !configured.value && brandLogo && typeof brandLogo !== 'string' ? brandLogo : null,
)
const imgSrc = computed(
  () => configured.value || (typeof brandLogo === 'string' && brandLogo) || builtinLogo,
)
</script>

<template>
  <component :is="brandComponent" v-if="brandComponent" :size="props.size" />
  <!-- 配置的 logo 可能是横版:定高、宽度随比例,容器窄时被 max-width 收住 -->
  <img
    v-else
    :src="imgSrc"
    :style="{ height: `${props.size}px` }"
    :alt="site.title || 'SmartAdmin'"
    class="site-logo"
  />
</template>

<style scoped>
.site-logo {
  display: block;
  width: auto;
  max-width: 100%;
  object-fit: contain;
}
</style>
