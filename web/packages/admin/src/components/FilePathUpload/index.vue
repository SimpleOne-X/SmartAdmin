<script setup lang="ts">
// FilePathUpload:封 n-upload 的 custom-request,按文件路径编码上传(uploadToFilePath),成功 emit('uploaded', out)。
// 超过 20 MB 的文件在 uploadToFilePath 里自动分片,进度回给 n-upload 进度条;组件对此无感。
// props 最小:accept/multiple/show-file-list 等一切经 $attrs 透传;默认 slot 作触发器。
import { onBeforeUnmount } from 'vue'
import { NUpload, useMessage, type UploadCustomRequestOptions } from 'naive-ui'
import { uploadToFilePath } from '#/utils/filePathUpload'
import { translateError } from '#/utils/error'
import type { FilePathUploadOutput } from '#/api'

defineOptions({ inheritAttrs: false })
const props = defineProps<{
  /** 文件路径编码(「文件路径」配置页里登记的那个)。 */
  code: string
  /** 仅「业务指定路径」命名规则使用,其余规则服务端忽略。 */
  subPath?: string
}>()
// loadingChange:单个文件上传真正开始/结束时各发一次(try/finally 兜住),给触发器加"上传中"反馈用。
const emit = defineEmits<{
  uploaded: [out: FilePathUploadOutput]
  loadingChange: [loading: boolean]
}>()
const message = useMessage()

// 在途的上传:组件卸载时一起中止,页面关了就不再往服务端传
const inFlight = new Set<AbortController>()
onBeforeUnmount(() => inFlight.forEach(c => c.abort()))

// n-upload 每个文件回调一次;file.file 是原始 File。成/败调 onFinish/onError 让 n-upload 更新该项状态。
async function customRequest({ file, onFinish, onError, onProgress }: UploadCustomRequestOptions) {
  if (!file.file) {
    onError()
    return
  }
  const controller = new AbortController()
  inFlight.add(controller)
  emit('loadingChange', true)
  try {
    const out = await uploadToFilePath(props.code, file.file, {
      subPath: props.subPath,
      onProgress: percent => onProgress({ percent }),
      signal: controller.signal,
    })
    emit('uploaded', out)
    onFinish()
  } catch (e) {
    // 卸载时中止的上传不再提示:页面已经不在了
    if (!controller.signal.aborted) message.error(translateError(e))
    onError()
  } finally {
    inFlight.delete(controller)
    emit('loadingChange', false)
  }
}
</script>

<template>
  <n-upload v-bind="$attrs" :custom-request="customRequest">
    <slot />
  </n-upload>
</template>
