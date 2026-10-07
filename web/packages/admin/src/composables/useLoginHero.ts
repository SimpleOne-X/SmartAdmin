import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { resolveLoginHero } from '#/lib/loginHero'
import { useSite } from './useSite'

/** 登录页 Hero 当前 locale 的生效文案:配置优先,空值逐字段回退内置 i18n。 */
export function useLoginHero() {
  const { t, locale } = useI18n()
  const { site } = useSite()

  return computed(() =>
    resolveLoginHero(
      site.loginHero[locale.value],
      {
        headline: `${t('login.headlinePre')}${t('login.headlineAccent')}${t('login.headlinePost')}`,
        highlight: t('login.headlineAccent'),
        features: [t('login.featRbac'), t('login.featScope'), t('login.featPortal')],
      },
      site.showFeatures,
    ),
  )
}
