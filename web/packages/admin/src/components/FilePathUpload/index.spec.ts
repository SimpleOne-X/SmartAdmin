import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, defineComponent, h, nextTick, type App } from 'vue'

// n-upload 本身不在这里测:换成桩,只把组件交给它的 customRequest 存下来,由用例直接调用。
const stub = vi.hoisted(() => ({
  customRequest: undefined as undefined | ((opts: unknown) => Promise<void>),
  error: vi.fn(),
}))
vi.mock('naive-ui', async importOriginal => ({
  ...(await importOriginal<typeof import('naive-ui')>()),
  NUpload: defineComponent({
    inheritAttrs: false,
    props: { customRequest: { type: Function, default: undefined } },
    setup(props, { slots }) {
      stub.customRequest = props.customRequest as typeof stub.customRequest
      return () => h('div', { class: 'upload-stub' }, slots.default?.())
    },
  }),
  useMessage: () => ({ error: stub.error }),
}))
vi.mock('#/utils/filePathUpload')

import { ApiError, type FilePathUploadOutput } from '#/api'
import { uploadToFilePath } from '#/utils/filePathUpload'
import FilePathUpload from './index.vue'

const uploadMock = vi.mocked(uploadToFilePath)

let app: App<Element> | undefined

function mount(props: {
  code: string
  subPath?: string
  onUploaded?: (out: FilePathUploadOutput) => void
  onLoadingChange?: (loading: boolean) => void
}) {
  const host = document.createElement('div')
  document.body.appendChild(host)
  app = createApp({ render: () => h(FilePathUpload, props) })
  app.mount(host)
}

/** 按 n-upload 的调用约定触发一次 customRequest。 */
async function request(file: File) {
  const hooks = { onFinish: vi.fn(), onError: vi.fn(), onProgress: vi.fn() }
  await stub.customRequest!({ file: { file }, ...hooks })
  await nextTick()
  return hooks
}

beforeEach(() => {
  uploadMock.mockReset()
  stub.error.mockReset()
  stub.customRequest = undefined
})

afterEach(() => {
  app?.unmount()
  app = undefined
  document.body.innerHTML = ''
})

describe('FilePathUpload', () => {
  it('emits_uploaded_payload', async () => {
    const out = { path: '2026/10/a.pdf', name: 'a.pdf', size: 3, viewUrl: '/v?sig=x' }
    uploadMock.mockResolvedValue(out)
    const onUploaded = vi.fn()
    const onLoadingChange = vi.fn()
    mount({ code: 'Order.Attachment', subPath: 'o1', onUploaded, onLoadingChange })
    const file = new File(['abc'], 'a.pdf')

    const hooks = await request(file)

    expect(uploadMock).toHaveBeenCalledWith(
      'Order.Attachment',
      file,
      expect.objectContaining({ subPath: 'o1', onProgress: expect.any(Function) }),
    )
    expect(onUploaded).toHaveBeenCalledWith(out)
    expect(hooks.onFinish).toHaveBeenCalledTimes(1)
    expect(hooks.onError).not.toHaveBeenCalled()
    expect(onLoadingChange.mock.calls).toEqual([[true], [false]])
  })

  it('reports_error_and_calls_onError', async () => {
    uploadMock.mockRejectedValue(new ApiError(44110, 'error.filePath.uploadDenied'))
    const onUploaded = vi.fn()
    const onLoadingChange = vi.fn()
    mount({ code: 'Order.Attachment', onUploaded, onLoadingChange })

    const hooks = await request(new File(['abc'], 'a.pdf'))

    expect(stub.error).toHaveBeenCalledTimes(1)
    expect(hooks.onError).toHaveBeenCalledTimes(1)
    expect(hooks.onFinish).not.toHaveBeenCalled()
    expect(onUploaded).not.toHaveBeenCalled()
    // 失败也要收到 loading=false,否则触发器一直转圈
    expect(onLoadingChange.mock.calls).toEqual([[true], [false]])
  })
})
